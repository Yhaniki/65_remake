using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 房間上方六格裡「其他玩家」那幾格的即時 3D 頭貼。
    ///
    /// 🔴 **它不建任何 avatar** —— 它把一台相機對準房間裡**已經在跑**的那幾隻遠端角色。
    ///
    /// 為什麼不照本機那顆(<see cref="RoomHeadPortrait"/>)複製五份:那個元件會**另外建一整隻**
    /// 完整 avatar(8 個 MSH + 8 張 DDS 手工解碼 ≈ 3.5 MB 貼圖 + 每幀 CPU skin 2000+ 頂點)。
    /// 複製五份的代價實測是:同時 CPU skin 的 avatar 從 7 隻變 12 隻、每幀 +1.5~3.5 ms、
    /// 記憶體 +18 MB(全是重複的貼圖),而且**每個人進房都要吃兩次 20~60 ms 的 hitch**。
    /// 那五隻角色房間裡本來就有一隻在跑,再建一隻只為了拍它的臉是純浪費。
    ///
    /// 這個作法的代價:6 張 RT(~1 MB)+ 每幀一次極小的 draw。CPU skinning **零增加**。
    ///
    /// 兩個必須做對的技術點:
    ///   ① **隔離**:遠端角色與房間家具不能同層,否則頭貼會拍到沙發。所以遠端角色住
    ///      <see cref="RoomScene3D.RemoteAvatarLayer"/>,而這台相機只看那一層。
    ///   ② **只拍一個人**:同層有五隻,靠關掉其他人的 renderer 來隔離(不能靠「剛好在畫面外」——
    ///      相機後方 30 單位處的半寬就已經到 28 單位,而角色間距只有 24)。
    ///      還原一定寫在 <c>finally</c> 裡:一次例外就整個房間的人消失。
    /// </summary>
    public sealed class RoomRemoteHeadSet : MonoBehaviour
    {
        /// <summary>頭貼 RT 尺寸。與本機那顆一致(96×76 格子的 2× 超取樣)。</summary>
        public int rtWidth = 192, rtHeight = 152;

        public float fov = 45f;

        /// <summary>相機繞到角色**正面**的角度。0 = 正對他的臉。</summary>
        public float portraitYaw = 0f;

        // ---- 取景參數:必須與本機那顆頭貼(RoomHeadPortrait)**用同一組值** -----------------------------------
        // 🔴 踩過:這裡原本把 ComputeFraming 的參數寫死成 RoomHeadPortrait 的**欄位預設值**(aimUp 0.11),
        //    而本機那顆是由 RoomScreen.ApplyHeadFraming 覆寫成 0.25 的 —— 於是同一個角色的遠端頭貼比
        //    他自己畫面上的頭貼**高了 0.14×框高 ≈ 14% 的框高**(使用者:「女角遠端頭貼比本機高 15%」)。
        //    症狀只在「同時看得到兩顆頭貼」時才發現得了,而且看起來像取景演算法有問題,不像參數沒同步。
        //    現在由 RoomScreen 同時餵這兩顆(同一組常數),要調就只調那一處。
        public float aimUp = 0.11f;         // 與 RoomHeadPortrait.headAimUp 同義
        public float frameDist = 1.9f;      // 與 RoomHeadPortrait.headFrameDist 同義
        public float zoom = 1f;             // 與 RoomHeadPortrait.zoom 同義
        public bool fitHairTop = false;     // 與 RoomHeadPortrait.fitHairTop 同義

        private Camera _cam;
        private RoomScene3D _scene;

        private sealed class Slot
        {
            public int UserId;
            public RenderTexture Rt;
            public Renderer[] Rends;      // 這個人身上所有的 renderer(拍別人時要關掉)
            public Transform Root;        // 這組 renderer 是從哪隻角色抓的 —— 換了才需要重抓(見 Refresh)
            public bool Framed;           // 取景已凍結?
            public bool Rendered;         // 這張 RT 至少拍過一次 → 之後永遠有畫面可以顯示(見 Texture)
            public float DistModel;       // 模型空間的相機距離 —— **只有這個凍結**(頭的大小不能忽大忽小)
        }

        private readonly Dictionary<int, Slot> _slots = new Dictionary<int, Slot>();
        private readonly List<int> _order = new List<int>();      // 輪轉用的穩定順序
        private readonly List<int> _gone = new List<int>();
        private int _cursor;

        public void Build(RoomScene3D scene)
        {
            _scene = scene;
            var camGo = new GameObject("RoomRemoteHeadCam");
            camGo.transform.SetParent(transform, false);
            _cam = camGo.AddComponent<Camera>();
            _cam.orthographic = false;
            _cam.fieldOfView = fov;
            _cam.nearClipPlane = 0.5f;
            _cam.farClipPlane = 500f;
            _cam.cullingMask = 1 << RoomScene3D.RemoteAvatarLayer;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0f, 0f, 0f, 0f);   // 透明 → 格子的底框透出來
            // 🔴 手動 Render:常開相機會每幀拍一次「目前 renderer 開關狀態」下的畫面,
            // 而我們一次只想拍一個人。這與商城的商品卡是同一個已驗證的形狀(ShopScreen.BuildCardCam)。
            _cam.enabled = false;
        }

        /// <summary>某個人的頭貼 RT(還沒拍過 → null,呼叫端就不畫那格)。
        ///
        /// 🔴 條件是「拍過沒」(<c>Rendered</c>)而**不是**「取景凍結了沒」(<c>Framed</c>)。
        /// Framed 會在角色重建時被清掉,而重新取景要等輪轉回到這一格 —— 中間那幾幀若回 null,
        /// 呼叫端(RoomScreen.RenderSlots)就把整格關掉 → 頭貼**閃一下**。RT 裡明明還留著上一張
        /// 完全能看的畫面,顯示舊的一兩幀遠比消失好看。</summary>
        public Texture Texture(int userId)
        {
            Slot s;
            return _slots.TryGetValue(userId, out s) && s.Rendered ? s.Rt : null;
        }

        /// <summary>
        /// 名單同步。只在房間快照變動時呼叫(生 RT 很便宜,但重抓 renderer 陣列不該每幀做)。
        ///
        /// 🔴 「快照變動」比想像中頻繁得多:**任何人在下載/上傳歌曲時,每 500ms 就會有一次**
        /// (<see cref="Sdo.Net.NetLimits.AvailProgressThrottleMs"/> 的進度回報 → server Touch() → rev++)。
        /// 舊版在這裡無條件對每個 slot 呼叫 Refresh(),而 Refresh 會把取景清掉 → 傳檔期間頭貼
        /// **每半秒閃一次**(使用者回報)。座位表沒動時角色物件根本是同一隻(RoomScene3D.SyncRemotePlayers
        /// 對 LookKey 沒變的人什麼都不做),重抓一次純屬白費。</summary>
        public void SetRoster(List<int> userIds)
        {
            _gone.Clear();
            foreach (var kv in _slots)
                if (userIds == null || !userIds.Contains(kv.Key)) _gone.Add(kv.Key);
            foreach (int id in _gone) Drop(id);

            if (userIds == null) return;
            for (int i = 0; i < userIds.Count; i++)
            {
                int id = userIds[i];
                if (id == 0 || _slots.ContainsKey(id)) continue;
                _slots[id] = new Slot
                {
                    UserId = id,
                    Rt = new RenderTexture(rtWidth, rtHeight, 16, RenderTextureFormat.ARGB32) { name = "RoomRemoteHeadRT" },
                };
                _order.Add(id);
            }
            // 角色可能是後來才重建的(換裝)→ renderer 陣列要重抓,取景也要重量。
            foreach (var kv in _slots) Refresh(kv.Value);
        }

        /// <summary>某個人的角色被重建了(換裝)→ 丟掉快取的 renderer 與取景。
        /// (Rendered 不清:舊畫面繼續頂著,直到新的頭拍好,見 <see cref="Texture"/>。)</summary>
        public void Invalidate(int userId)
        {
            Slot s;
            if (_slots.TryGetValue(userId, out s)) { s.Rends = null; s.Root = null; s.Framed = false; }
        }

        /// <summary>
        /// 每幀拍**一格**(輪轉)。
        ///
        /// 為什麼不是每幀全部拍:一次 <c>Camera.Render()</c> 的 setup/culling 大約 0.1~0.3 ms,
        /// 五格就是最多 1.5 ms —— 而頭貼是一顆很小的頭,12 fps 完全看不出來。
        /// GPU 那一半本來就便宜(五格加起來只佔房間主 RT 取樣數的 4%),瓶頸在 CPU 的 per-render 開銷。
        /// </summary>
        public void Tick()
        {
            if (_cam == null || _scene == null || _order.Count == 0) return;

            // 找下一個「角色還活著」的格子。最多繞一圈,避免全部都死掉時空轉。
            for (int tries = 0; tries < _order.Count; tries++)
            {
                _cursor = (_cursor + 1) % _order.Count;
                int id = _order[_cursor];
                Slot s;
                if (!_slots.TryGetValue(id, out s)) continue;
                if (RenderOne(s)) return;
            }
        }

        private bool RenderOne(Slot s)
        {
            Transform root;
            SdoAvatar av;
            if (!_scene.TryGetRemoteAvatar(s.UserId, out av, out root) || av == null || root == null) return false;

            // 角色換了一隻(換裝重建)→ 就地重抓,不必等下一次 SetRoster。root 比對是零成本的,
            // 而漏掉的話舊陣列裡全是已銷毀的 renderer → 這一格會凍在舊畫面上。
            if (s.Rends == null || s.Root != root) Refresh(s);
            if (s.Rends == null || s.Rends.Length == 0) return false;
            if (!s.Framed && !TryFreeze(s, av)) return false;

            // 只補「root 骨的平移」(走路時整個人前進/浮沉),頭自己的擺動不補 —— 擺動要在框內演出,
            // 相機跟著擺的話頭反而看起來是靜止的(這條與本機那顆的處理一致)。
            // 🔴 **大小凍結、位置每幀重算**。
            //
            // 只凍結 dist(相機距離)是為了「換髮型/做動作時頭不會忽大忽小」——那是本機那顆
            // 已經確立的需求。但**取景中心不能凍結**:遠端角色是活的,穿飛行翅膀的人 idle 是
            // flystay 浮空,臉相對骨架的位置一直在變;把某一瞬間的偏移凍下來,之後就固定偏那麼多
            // (實測症狀:客戶端看到的房主頭貼只露到頭髮,而同一隻角色在他自己機器上是正的)。
            // 每幀從活的臉框算中心 → 頭永遠在框裡,而大小仍然穩定。
            // 成本:一幀只拍一格,對那一格的 9 個 renderer 取一次 bounds 聯集,可忽略。
            Vector3 aimLocal;
            if (!AimFromFace(s, out aimLocal)) return false;

            // 相機的位置與朝向**在角色自己的座標系裡**算,再一起變換到世界 ——
            // 幾何與本機那顆(相機固定在 -Z、轉的是 avatar)逐項相同,不必自己推「臉朝哪個世界方向」。
            // (第一版自己推方向 → 拍到後腦:模型的臉是 +Z,而房間裡「面向鏡頭」是 yaw 180,兩個負號疊起來就反了。)
            Vector3 camLocal = aimLocal + Quaternion.Euler(0f, portraitYaw, 0f) * new Vector3(0f, 0f, -s.DistModel);
            _cam.transform.position = root.TransformPoint(camLocal);
            _cam.transform.rotation = Quaternion.LookRotation(
                root.TransformPoint(aimLocal) - root.TransformPoint(camLocal), Vector3.up);
            _cam.fieldOfView = fov;
            // 深度精度集中在頭上(而且是第二層「別人不入鏡」的保險)。
            _cam.nearClipPlane = Mathf.Max(0.05f, s.DistModel - 20f);
            _cam.farClipPlane = s.DistModel + 30f;

            HideOthers(s.UserId);
            try
            {
                _cam.targetTexture = s.Rt;
                _cam.Render();
            }
            finally
            {
                ShowAll();   // 🔴 一定要 finally:一次例外就整個房間的人消失
            }
            s.Rendered = true;
            return true;
        }

        /// <summary>從活的臉框算這一幀的取景中心(與凍結 dist 時用的是同一個純函式,只是輸入是活的)。</summary>
        private bool AimFromFace(Slot s, out Vector3 aim)
        {
            aim = default;
            Bounds face;
            if (!MeshUnion(s.Rends, "FACE", out face) || face.size.y < 1f) return false;
            float ignoredDist;
            // 髮頂的處理也要跟本機一致:本機是 `hairFound && fitHairTop`,所以這裡照抄同一條式子
            // (fitHairTop 預設 false → 兩邊都不理頭髮;哪天要開就兩邊一起開)。
            Bounds hair;
            bool hairFound = MeshUnion(s.Rends, "HAIR", out hair);
            RoomHeadPortrait.ComputeFraming(face, hairFound && fitHairTop, hairFound ? hair.max.y : 0f,
                                            frameDist, zoom, aimUp, fov, out aim, out ignoredDist);
            return true;
        }

        /// <summary>
        /// 凍結取景:用當前姿勢量一次臉框,算出目標點/距離就定案。
        /// 追「活的 bounds」會讓頭隨著動作忽大忽小(本機那顆已經踩過這個坑)。
        /// 幾何完全沿用 <see cref="RoomHeadPortrait.ComputeFraming"/> —— 那段被 7 個單元測試釘著。
        /// </summary>
        private bool TryFreeze(Slot s, SdoAvatar av)
        {
            Bounds face;
            if (!MeshUnion(s.Rends, "FACE", out face)) return false;
            // 臉框太小 = 還沒蒙皮好(CPU skin 要跑過一次 mesh.vertices 才有正確 bounds)。
            // 門檻用「一個臉高至少 1 個單位」而不是 1e-4:剛建好那一兩幀量到的框可能極小但不是零,
            // 用那個框凍結取景的話相機會貼到臉上,而取景是**凍結**的 → 錯一次就一直錯。
            if (face.size.y < 1f) return false;

            Bounds hair;
            bool hasHair = MeshUnion(s.Rends, "HAIR", out hair);
            Vector3 aim;
            RoomHeadPortrait.ComputeFraming(face, false, hasHair ? hair.max.y : 0f,
                                            1.9f, 1f, 0.11f, fov, out aim, out s.DistModel);
            s.Framed = true;   // 只有 dist 定案;中心每幀重算(見 AimFromFace)
            return true;
        }

        /// <summary>
        /// 重抓這個人的 renderer 陣列 —— **只有在角色真的換了一隻的時候**。
        ///
        /// 判斷鍵是 root transform:RoomScene3D 對「外觀沒變」的人不重建(直接沿用同一個 GameObject),
        /// 換裝才會 RebuildRemote 出一隻新的。同一隻 → 舊陣列仍然有效,連 Framed 都不該碰:
        /// 清掉它就要等輪轉回來重新取景,那段空窗就是傳檔時每半秒一次的閃爍(見 <see cref="SetRoster"/>)。
        /// </summary>
        private void Refresh(Slot s)
        {
            Transform root;
            SdoAvatar av;
            if (!_scene.TryGetRemoteAvatar(s.UserId, out av, out root) || av == null)
            {
                // 角色不在(旁觀者、或正在重建)→ 快取失效。Rendered 保留 → 那格繼續顯示最後拍到的臉。
                s.Rends = null; s.Root = null; s.Framed = false; return;
            }
            if (s.Rends != null && s.Root == root) return;   // 同一隻角色 → 什麼都不用做
            s.Rends = av.GetComponentsInChildren<Renderer>(true);
            s.Root = root;
            s.Framed = false;
        }

        // 拍某個人時,把其他人的 renderer 關掉。只動 Renderer.enabled ——
        // **不要用 GameObject.SetActive**(商城踩過:滾動時衣服會整批消失)。
        private void HideOthers(int keepUserId)
        {
            foreach (var kv in _slots)
            {
                if (kv.Key == keepUserId) continue;
                var rs = kv.Value.Rends;
                if (rs == null) continue;
                for (int i = 0; i < rs.Length; i++) if (rs[i] != null) rs[i].enabled = false;
            }
        }

        private void ShowAll()
        {
            foreach (var kv in _slots)
            {
                var rs = kv.Value.Rends;
                if (rs == null) continue;
                for (int i = 0; i < rs.Length; i++) if (rs[i] != null) rs[i].enabled = true;
            }
        }

        private static bool MeshUnion(Renderer[] rends, string nameContains, out Bounds b)
        {
            b = default;
            bool any = false;
            if (rends == null) return false;
            for (int i = 0; i < rends.Length; i++)
            {
                var r = rends[i];
                if (r == null) continue;
                if (r.gameObject.name.ToUpperInvariant().IndexOf(nameContains, System.StringComparison.Ordinal) < 0) continue;
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                var mb = mf.sharedMesh.bounds;
                if (mb.size.sqrMagnitude < 1e-6f) continue;
                if (!any) { b = mb; any = true; } else b.Encapsulate(mb);
            }
            return any;
        }

        private void Drop(int userId)
        {
            Slot s;
            if (!_slots.TryGetValue(userId, out s)) return;
            if (s.Rt != null) { if (RenderTexture.active == s.Rt) RenderTexture.active = null; s.Rt.Release(); Destroy(s.Rt); }
            _slots.Remove(userId);
            _order.Remove(userId);
            if (_cursor >= _order.Count) _cursor = 0;
        }

        private void OnDestroy()
        {
            // RT 是 native 資源,不釋放就是洩漏(進出房間幾次就吃掉幾十 MB)。
            foreach (var kv in _slots)
                if (kv.Value.Rt != null) { kv.Value.Rt.Release(); Destroy(kv.Value.Rt); }
            _slots.Clear();
            _order.Clear();
        }
    }
}
