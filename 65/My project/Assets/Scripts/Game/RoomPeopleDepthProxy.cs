using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 一隻角色的「隱形深度分身」——房間頭上聊天泡「只被人擋、不被場景擋」的後半段。
    ///
    /// 前半段是 <see cref="CreateDepthReset"/> 那片:它在場景畫完之後把深度整片推回無限遠,
    /// 於是家具/牆壁再也擋不住泡。這個元件接著替角色身上每個 renderer 生一個看不見的分身
    /// (同一份 mesh、<c>Sdo/DepthOnlyMask</c> 材質、住在 <see cref="RoomScene3D.PeopleDepthLayer"/>),
    /// 把「人」的剪影逐像素寫回深度 —— 泡因此只可能輸給人。
    ///
    /// 三個刻意的設計:
    ///
    /// ① **分身不掛在角色底下**,而是掛在場景給的 <c>proxyRoot</c> 上,每幀抄本尊的世界 transform。
    ///    理由:專案裡到處都有 <c>GetComponentsInChildren&lt;Renderer&gt;()</c> 在角色身上做事
    ///    (量厚度、拍頭貼時關掉別人的 renderer、衣物材質檢查……)。分身若成為角色的後代,
    ///    那些程式碼會突然多看到一倍的 renderer —— 症狀散在很多地方,而且都不會報錯。
    ///
    /// ② **一份材質對一份分身材質**(抄 <c>_MainTex</c> 與 <c>_Cutoff</c>):頭髮/鏤空布料是
    ///    alpha cutout,分身若不裁,泡會被「一張看不見的頭髮方片」咬掉一塊 —— 看起來像美術破圖。
    ///
    /// ③ **定期重掃**:翅膀之類的部件是後來才掛上去的(見 AvatarWingRig),只在建立當下掃一次
    ///    會讓那些部件永遠擋不住泡。重掃很便宜(一次 GetComponentsInChildren),半秒一次。
    /// </summary>
    public sealed class RoomPeopleDepthProxy : MonoBehaviour
    {
        public const string ShaderName = "Sdo/DepthOnlyMask";
        public const string ResetShaderName = "Sdo/DepthReset";

        // 三步的先後全靠 sortingOrder(它比 renderQueue 優先,見 [[unity-sortingorder-outranks-renderqueue]]):
        //   場景/家具/衣物 0 → 深度重置 98 → 人的分身 99 → 泡 100+(RoomScreen.BubbleSortingOrderBase)。
        // 🔴 動任何一個數字之前先確認上下兩層還排得對:排錯的症狀不是「沒效果」,而是
        //    重置片把**整個房間**的深度洗掉之後才畫場景 → 前後關係全亂。
        public const int ResetSortingOrder = 98;
        public const int ProxySortingOrder = 99;

        private static Shader _shader, _resetShader;
        private static bool _shaderLookedUp, _resetLookedUp;

        /// <summary>只寫深度的那支 shader。打包版若被 strip 掉會是 null(見 BuildScript.RequiredShaders)。</summary>
        public static Shader DepthShader
        {
            get
            {
                if (!_shaderLookedUp) { _shader = Shader.Find(ShaderName); _shaderLookedUp = true; }
                return _shader;
            }
        }

        /// <summary>「把深度推回無限遠」那支 shader。</summary>
        public static Shader ResetShader
        {
            get
            {
                if (!_resetLookedUp) { _resetShader = Shader.Find(ResetShaderName); _resetLookedUp = true; }
                return _resetShader;
            }
        }

        /// <summary>兩支 shader 都在才有這個功能(打包版被 strip 掉就退回舊行為:泡也被場景擋)。</summary>
        public static bool Available => DepthShader != null && ResetShader != null;

        /// <summary>
        /// 「把深度推回無限遠」的那片全螢幕面片(見 <c>Sdo/DepthReset</c>)。掛在相機底下,
        /// mesh 是裁剪空間的大三角形 + 超大 bounds,所以永遠蓋滿畫面也永遠不被視錐剔除。
        /// 回 null = shader 不在(呼叫端要退回舊行為,不然泡會蓋在所有東西前面)。
        /// </summary>
        public static GameObject CreateDepthReset(Transform parent, int layer)
        {
            var sh = ResetShader;
            if (sh == null) return null;

            var go = new GameObject("RoomBubbleDepthReset") { layer = layer };
            go.transform.SetParent(parent, false);
            var mesh = new Mesh { name = "fullscreenTri" };
            mesh.vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(3f, -1f, 0f), new Vector3(-1f, 3f, 0f) };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);   // 永遠在視錐內
            go.AddComponent<MeshFilter>().sharedMesh = mesh;

            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = new Material(sh) { name = "DepthReset" };
            mr.sortingOrder = ResetSortingOrder;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            return go;
        }

        private sealed class Twin
        {
            public Renderer Src;
            public Renderer Dst;
            public MeshFilter SrcFilter, DstFilter;
        }

        private readonly List<Twin> _twins = new List<Twin>();
        private readonly Dictionary<Material, Material> _mats = new Dictionary<Material, Material>();
        private readonly List<Renderer> _scratch = new List<Renderer>();
        private Transform _proxyRoot;
        private int _layer;
        private float _nextRescan;

        private const float RescanInterval = 0.5f;   // 秒;晚掛上去的部件(翅膀/道具)靠它補進來

        /// <summary>
        /// 替 <paramref name="avatarRoot"/> 這隻角色建/更新深度分身。分身放在 <paramref name="proxyRoot"/> 底下
        /// (角色被銷毀時本元件跟著走,分身在 <see cref="OnDestroy"/> 收掉)。shader 不在就回 null。
        /// </summary>
        public static RoomPeopleDepthProxy Attach(GameObject avatarRoot, Transform proxyRoot, int layer)
        {
            if (avatarRoot == null || proxyRoot == null || !Available) return null;
            var p = avatarRoot.GetComponent<RoomPeopleDepthProxy>();
            if (p == null) p = avatarRoot.AddComponent<RoomPeopleDepthProxy>();
            p._proxyRoot = proxyRoot;
            p._layer = layer;
            p.Rescan();
            return p;
        }

        /// <summary>目前分身的數量(測試用)。</summary>
        public int ProxyCount => _twins.Count;

        /// <summary>掃一次本尊身上的 renderer,替還沒有分身的建分身。</summary>
        public void Rescan()
        {
            if (_proxyRoot == null || DepthShader == null) return;
            _nextRescan = Time.unscaledTime + RescanInterval;

            _scratch.Clear();
            GetComponentsInChildren(true, _scratch);
            for (int i = 0; i < _scratch.Count; i++)
            {
                var src = _scratch[i];
                if (src == null || HasTwin(src)) continue;
                var twin = BuildTwin(src);
                if (twin != null) _twins.Add(twin);
            }
        }

        private bool HasTwin(Renderer src)
        {
            for (int i = 0; i < _twins.Count; i++)
                if (ReferenceEquals(_twins[i].Src, src)) return true;
            return false;
        }

        private Twin BuildTwin(Renderer src)
        {
            var smr = src as SkinnedMeshRenderer;
            var mf = smr == null ? src.GetComponent<MeshFilter>() : null;
            if (smr == null && (mf == null || mf.sharedMesh == null)) return null;

            var go = new GameObject(src.name + "__depth") { layer = _layer };
            go.transform.SetParent(_proxyRoot, false);
            if (!isActiveAndEnabled) go.SetActive(false);   // 本尊現在是關的 → 分身也別擋人(見 OnDisable)

            Renderer dst;
            MeshFilter dstFilter = null;
            if (smr != null)
            {
                // SMR 是靠 bones/rootBone 定位的,分享同一組骨頭就會與本尊完全重疊(不必抄 transform)。
                var d = go.AddComponent<SkinnedMeshRenderer>();
                d.sharedMesh = smr.sharedMesh;
                d.rootBone = smr.rootBone;
                d.bones = smr.bones;
                d.localBounds = smr.localBounds;
                d.updateWhenOffscreen = smr.updateWhenOffscreen;
                d.quality = smr.quality;
                dst = d;
            }
            else
            {
                // CPU skinning 的部件每幀就地改寫**同一個 Mesh 實例**(SdoAvatar 寫 mesh.vertices),
                // 所以分享 sharedMesh 的分身自動跟著變形 —— 一次骨骼運算都不用多算。
                dstFilter = go.AddComponent<MeshFilter>();
                dstFilter.sharedMesh = mf.sharedMesh;
                dst = go.AddComponent<MeshRenderer>();
            }

            dst.sharedMaterials = DepthMaterialsFor(src);
            dst.sortingOrder = ProxySortingOrder;   // 深度重置(98)之後、泡(100+)之前
            dst.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            dst.receiveShadows = false;
            dst.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            dst.enabled = src.enabled;

            var twin = new Twin { Src = src, Dst = dst, SrcFilter = mf, DstFilter = dstFilter };
            FollowTransform(twin);
            return twin;
        }

        private Material[] DepthMaterialsFor(Renderer src)
        {
            var srcMats = src.sharedMaterials;
            var outMats = new Material[Mathf.Max(1, srcMats != null ? srcMats.Length : 1)];
            for (int i = 0; i < outMats.Length; i++)
                outMats[i] = DepthMaterialFor(srcMats != null && i < srcMats.Length ? srcMats[i] : null);
            return outMats;
        }

        private Material DepthMaterialFor(Material src)
        {
            Material m;
            if (src != null && _mats.TryGetValue(src, out m) && m != null) return m;

            m = new Material(DepthShader) { name = "DepthOnly_" + (src != null ? src.name : "null") };
            if (src != null)
            {
                if (src.HasProperty("_MainTex"))
                {
                    m.SetTexture("_MainTex", src.GetTexture("_MainTex"));
                    m.SetTextureScale("_MainTex", src.GetTextureScale("_MainTex"));
                    m.SetTextureOffset("_MainTex", src.GetTextureOffset("_MainTex"));
                }
                // _Cutoff 沒有就維持 0(= 不裁)。實心布料的貼圖 alpha 可能整片是 0,裁下去會把人挖空。
                if (src.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", src.GetFloat("_Cutoff"));
                _mats[src] = m;
            }
            return m;
        }

        private static void FollowTransform(Twin t)
        {
            if (t.Dst == null || t.Src == null) return;
            var s = t.Src.transform;
            var d = t.Dst.transform;
            d.SetPositionAndRotation(s.position, s.rotation);
            d.localScale = s.lossyScale;
        }

        private void LateUpdate()
        {
            for (int i = _twins.Count - 1; i >= 0; i--)
            {
                var t = _twins[i];
                if (t.Src == null || t.Dst == null)
                {
                    if (t.Dst != null) Destroy(t.Dst.gameObject);
                    _twins.RemoveAt(i);
                    continue;
                }
                // 本尊被關掉(部件缺貼圖被隱藏、拍頭貼時暫時關掉別人)→ 分身也要關,
                // 否則會有一個「看不見的人」擋住泡。
                if (t.Dst.enabled != t.Src.enabled) t.Dst.enabled = t.Src.enabled;
                if (t.DstFilter != null && t.SrcFilter != null && t.DstFilter.sharedMesh != t.SrcFilter.sharedMesh)
                    t.DstFilter.sharedMesh = t.SrcFilter.sharedMesh;
                FollowTransform(t);
            }

            if (Time.unscaledTime >= _nextRescan) Rescan();
        }

        // 🔴 本尊被整個關掉時(換穿重建會先 SetActive(false) 舊的那隻)分身**不會**自己消失 ——
        //    它們住在角色階層外面。LateUpdate 這時也不跑了,所以同步 enabled 那條救不了:
        //    畫面上會有一個看不見的人繼續擋著泡。用 OnEnable/OnDisable 綁死。
        private void OnEnable() => SetTwinsActive(true);
        private void OnDisable() => SetTwinsActive(false);

        private void SetTwinsActive(bool on)
        {
            for (int i = 0; i < _twins.Count; i++)
                if (_twins[i].Dst != null) _twins[i].Dst.gameObject.SetActive(on);
        }

        private void OnDestroy()
        {
            for (int i = 0; i < _twins.Count; i++)
                if (_twins[i].Dst != null) Destroy(_twins[i].Dst.gameObject);
            _twins.Clear();
            foreach (var kv in _mats) if (kv.Value != null) Destroy(kv.Value);
            _mats.Clear();
        }
    }
}
