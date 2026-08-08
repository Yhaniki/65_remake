using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 讓角色身上的**半透明**衣物/翅膀出現在深度緩衝裡。
    ///
    /// 問題:alpha-blend 的材質 ZWrite Off(那正是它們透光的原因),所以它們對深度緩衝來說不存在。
    /// 站在前面的人穿一對半透明翅膀(例:Ribbon Star M 037939,DXT3、可見像素有一半是中間調),
    /// 後面那個人的頭上名字牌就從翅膀裡透出來 —— 名字牌自己已經吃深度測試了,只是沒有東西可以測。
    ///
    /// 做法:替每一個半透明 renderer 生一個**只寫深度的分身**(<c>Sdo/AvatarDepthOnly</c>,ColorMask 0),
    /// 排在 Queue 3500 —— 比所有衣物的色彩批(3000..3400)都晚。
    ///
    /// 🔴 為什麼這樣一定不會改變外觀:分身在**所有衣物都畫完之後**才寫深度,而且畫不出顏色。
    ///    衣物之間的疊色、紗料層層累積的密度、自己的背面全都在它之前就定案了。
    ///    (把原本材質的 ZWrite 打開就會改變外觀 —— 後畫的那層會被前面那層裁掉;而在原 shader 加
    ///    第二個 pass 更糟,試過兩次都讓每一件透明衣服整件消失,見 UnlitAvatarSheer.shader 檔頭。)
    ///
    /// 🔴 分身**掛在角色階層外面**(自己的 holder,每幀抄世界 transform):專案到處對角色做
    ///    <c>GetComponentsInChildren&lt;Renderer&gt;()</c>(量身高/拍頭貼開關別人/商城衣物檢查),
    ///    掛進去會讓它們多看到一倍 renderer —— 而且頭貼那條會把分身也搬進頭貼相機的 layer。
    ///    這個元件本身掛在角色 root 上(它不是 Renderer,不會被那些列舉看到),角色被拆時連帶收掉 holder。
    ///
    /// 掛載點只有兩個,都是「所有角色的必經之路」:<c>SdoAvatarBuilder.LoadParts</c>(遊戲內舞者)與
    /// <c>SdoRoomAvatar.Build</c>(房間/選男女的人)。頭貼/商城卡(Portrait 系)刻意不掛 —— 那是
    /// 不透明合成到透明 RT,多一份深度只會有害無益。
    /// </summary>
    [DefaultExecutionOrder(150)]   // 在 CPU 蒙皮把 mesh 改寫完之後(分身共用同一顆 Mesh,不必自己蒙皮)
    public sealed class AvatarTranslucentDepth : MonoBehaviour
    {
        public const string ShaderName = "Sdo/AvatarDepthOnly";

        /// <summary>分身材質的 alpha 門檻。見 shader 裡 <c>_Cutoff</c> 的說明。</summary>
        public const float DefaultCutoff = 0.5f;

        /// <summary>分身的 renderQueue。必須 &gt; 衣物色彩批的上限(3400),否則就會影響外觀。</summary>
        public const int DepthQueue = 3500;

        private readonly List<Renderer> _src = new List<Renderer>();
        private readonly List<MeshRenderer> _twin = new List<MeshRenderer>();
        private GameObject _holder;

        // 全場的 (主人, holder) 對照。holder 住在角色階層外面,所以它的命是**別人**的責任:
        // 🔴 主人被 DestroyImmediate 拆掉時,從 OnDestroy 裡再 DestroyImmediate 是無效的(Unity 正在拆東西,
        //    巢狀的立即銷毀會被吃掉)—— holder 於是活下來,變成一片看不見卻會寫深度的東西,
        //    在後面的畫面裡默默切掉別人的名字。實測:EditMode 整套跑的時候它污染了另一條無關的渲染測試。
        //    所以除了 OnDestroy 之外,再加一道「主人不在了就收掉」的清掃(見 SweepOrphans)。
        private static readonly List<AvatarTranslucentDepth> Owners = new List<AvatarTranslucentDepth>();
        private static readonly List<GameObject> Holders = new List<GameObject>();

        /// <summary>分身數(測試用)。</summary>
        public int TwinCount => _twin.Count;

        /// <summary>分身住的那個 holder(測試用:它必須**不是**角色的後代)。</summary>
        public GameObject HolderForTest => _holder;

        /// <summary>
        /// 一個材質算不算「半透明、卻沒寫進深度」—— 也就是需要補分身的那種。
        ///
        /// 純判斷,吃 <see cref="Material.renderQueue"/> 與 <c>_ZWriteMode</c>:
        /// 實心去背布料的 sheer 材質會被 <c>ApplySheerMaterialState</c> 打開 ZWrite,它本來就寫深度 → 不用補。
        /// </summary>
        public static bool NeedsDepthTwin(Material m)
        {
            if (m == null || m.mainTexture == null) return false;
            if (m.renderQueue < 3000 || m.renderQueue >= DepthQueue) return false;   // 不透明/已經是分身
            return !m.HasProperty("_ZWriteMode") || m.GetFloat("_ZWriteMode") < 0.5f;
        }

        /// <summary>
        /// 替 <paramref name="avatarRoot"/> 底下每個半透明 renderer 補上只寫深度的分身。
        /// 沒有半透明件(絕大多數角色)就什麼都不做、也不留元件。回傳分身數。
        ///
        /// 可以重複呼叫(換穿會重建角色,但測試/未來的路徑可能重掛)—— 舊的分身先收掉再重建。
        /// </summary>
        /// <summary>
        /// 收掉「主人已經不在」的 holder。<see cref="Attach"/> 每次都會先跑一遍,測試也直接叫它。
        /// 這是 <see cref="OnDestroy"/> 的後盾 —— 見 <see cref="Owners"/> 上面那段:被 DestroyImmediate
        /// 拆掉的角色,OnDestroy 裡的立即銷毀不會生效,holder 會留下來繼續寫深度。
        /// </summary>
        public static void SweepOrphans()
        {
            for (int i = Holders.Count - 1; i >= 0; i--)
            {
                if (Holders[i] == null) { Holders.RemoveAt(i); Owners.RemoveAt(i); continue; }
                if (Owners[i] != null) continue;   // 主人還在
                DestroyNow(Holders[i]);
                Holders.RemoveAt(i); Owners.RemoveAt(i);
            }
        }

        public static int Attach(GameObject avatarRoot)
        {
            SweepOrphans();
            if (avatarRoot == null) return 0;
            var depthShader = Shader.Find(ShaderName);
            if (depthShader == null)
            {
                // 打包版把 shader strip 掉 → 退回舊行為(半透明件不擋名字),而不是整個角色壞掉。
                Debug.LogWarning("[avatar-depth] missing " + ShaderName + " → 半透明衣物不會擋住頭上名字");
                return 0;
            }

            var self = avatarRoot.GetComponent<AvatarTranslucentDepth>();
            if (self != null) self.Clear();

            var rends = avatarRoot.GetComponentsInChildren<MeshRenderer>(true);
            var wanted = new List<MeshRenderer>();
            foreach (var r in rends)
            {
                if (r == null) continue;
                var mats = r.sharedMaterials;
                if (mats == null) continue;
                for (int i = 0; i < mats.Length; i++)
                    if (NeedsDepthTwin(mats[i])) { wanted.Add(r); break; }
            }
            if (wanted.Count == 0)
            {
                if (self != null) Destroy(self);
                return 0;
            }

            if (self == null) self = avatarRoot.AddComponent<AvatarTranslucentDepth>();
            self.Build(wanted, depthShader);
            return self._twin.Count;
        }

        private void Build(List<MeshRenderer> sources, Shader depthShader)
        {
            _holder = new GameObject(gameObject.name + "_translucentDepth");   // 🔴 場景根物件,不是角色的後代
            Owners.Add(this);
            Holders.Add(_holder);
            for (int i = 0; i < sources.Count; i++)
            {
                var src = sources[i];
                var mf = src.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                var go = new GameObject(src.gameObject.name + "_depth") { layer = src.gameObject.layer };
                go.transform.SetParent(_holder.transform, false);
                // 共用同一顆 Mesh:CPU 蒙皮就地改寫它,所以分身零成本地跟著動(bounds 也一起跟)。
                go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                var twin = go.AddComponent<MeshRenderer>();

                var srcMats = src.sharedMaterials;
                var mats = new Material[srcMats.Length];
                for (int s = 0; s < srcMats.Length; s++)
                {
                    // 不需要分身的 range(不透明/已寫深度)給 null 材質 → Unity 直接跳過那一段幾何,
                    // 不會替它寫深度。全部塞同一支 shader 的話,不透明那段會被重複寫(無害但浪費)。
                    if (!NeedsDepthTwin(srcMats[s])) { mats[s] = null; continue; }
                    mats[s] = new Material(depthShader)
                    {
                        name = "depth:" + (srcMats[s].name ?? ""),
                        mainTexture = srcMats[s].mainTexture,
                        renderQueue = DepthQueue,
                    };
                    mats[s].SetFloat("_Cutoff", DefaultCutoff);
                }
                twin.sharedMaterials = mats;
                twin.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                twin.receiveShadows = false;

                _src.Add(src);
                _twin.Add(twin);
            }
            SyncAll();
        }

        private void LateUpdate() => SyncAll();

        /// <summary>
        /// 分身跟上來源:世界 transform、layer、開關、以及換幀貼圖(翅膀的 _TexAnimEx 每幀換 mainTexture,
        /// 分身不跟的話會用第 0 幀的 alpha 去裁 —— 換幀時名字牌邊緣會抖)。
        /// </summary>
        private void SyncAll()
        {
            for (int i = _twin.Count - 1; i >= 0; i--)
            {
                var twin = _twin[i];
                var src = _src[i];
                if (twin == null) { _twin.RemoveAt(i); _src.RemoveAt(i); continue; }
                if (src == null) { Destroy(twin.gameObject); _twin.RemoveAt(i); _src.RemoveAt(i); continue; }

                var st = src.transform;
                twin.transform.SetPositionAndRotation(st.position, st.rotation);
                twin.transform.localScale = st.lossyScale;
                if (twin.gameObject.layer != src.gameObject.layer) twin.gameObject.layer = src.gameObject.layer;

                bool on = src.enabled && src.gameObject.activeInHierarchy;
                if (twin.enabled != on) twin.enabled = on;

                var sm = src.sharedMaterials;
                var tm = twin.sharedMaterials;
                int n = sm.Length < tm.Length ? sm.Length : tm.Length;
                for (int s = 0; s < n; s++)
                    if (tm[s] != null && sm[s] != null && tm[s].mainTexture != sm[s].mainTexture)
                        tm[s].mainTexture = sm[s].mainTexture;
            }
        }

        private void Clear()
        {
            _src.Clear();
            _twin.Clear();
            if (_holder == null) return;
            int at = Holders.IndexOf(_holder);
            if (at >= 0) { Holders.RemoveAt(at); Owners.RemoveAt(at); }
            DestroyNow(_holder);
            _holder = null;
        }

        private static void DestroyNow(GameObject go)
        {
            if (go == null) return;
            if (Application.isPlaying) Destroy(go); else DestroyImmediate(go);
        }

        // 角色被拆(離房/換穿重建/換畫面)→ 分身跟著走。holder 在角色階層外面,沒有這一段就會變孤兒,
        // 而且它是**看不見的**:留下來只會在畫面上默默切掉別人的名字。
        private void OnDestroy() => Clear();
    }
}
