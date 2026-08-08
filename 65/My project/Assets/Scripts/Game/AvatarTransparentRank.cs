using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// **跨角色**的半透明繪製順序:站在後面的人,他的半透明衣物要先畫。
    ///
    /// 問題(使用者回報,遊戲舞台截圖):「後面有一個人穿了金姬兰,但是衣服卻畫在前面的人的翅膀再更前面」。
    /// <see cref="SdoAvatarBuilder.TransparentGarmentQueue"/> 給每個角色的半透明身體衣物一組遞增的
    /// renderQueue(3000+載入順序),那是為了釘住**同一個角色內部**的疊色順序(不釘的話 Unity 逐幀的距離排序
    /// 會讓兩件重疊的紗互相翻面 = 當年的「褲子會一直閃爍」)。但那個計數器每個角色**各自從 0 開始**,而翅膀
    /// (CHIBANG,不是 body garment)根本不進那條 → 停在 shader 的 Queue=Transparent(3000)。於是:
    ///   前面的人的翅膀     queue 3000、ZWrite OFF(alpha-blend 不寫深度)
    ///   後面的人的金姬兰紗 queue 3000..3017、ZWrite ON
    /// 紗的 queue 大 → 後畫;翅膀又沒寫深度,擋不住它 → 遠處的裙子直接蓋到近處的翅膀上。renderQueue 是硬性的
    /// 先後,它壓過了「誰離相機近」。
    ///
    /// 做法:把 queue 拆成兩段 —— **高位是角色**(依離相機的距離,遠的先畫)、**低位是角色內部的載入順序**
    /// (原本那個計數器,原封不動)。<c>queue = 3000 + rank*<see cref="Band"/> + order</c>。
    ///
    /// 🔴 為什麼不用別的做法:
    ///   • 讓翅膀也 ZWrite ON → 它自己重疊的羽毛會互相裁掉,外觀就變了(紗是另一回事,它有 _Density 補償)。
    ///   • 把 <see cref="AvatarTranslucentDepth"/> 的只寫深度分身提前到衣物之前 → 同一個角色自己的多層
    ///     半透明會被自己的深度裁掉,同樣改外觀(見該類別的說明)。
    ///   • 全部改回同一個 queue 讓 Unity 距離排序 → 跨角色對了,同角色內部的閃爍又回來了。
    ///
    /// 上限:<see cref="MaxRank"/>+1 個角色各佔 <see cref="Band"/>,最高 queue 仍 &lt; 3500 —— 也就是
    /// <see cref="AvatarTranslucentDepth.DepthQueue"/>,不然深度分身會跑到衣物**前面**寫深度、把它自己的層裁掉。
    /// 超出的角色共用最後一段(退回原本的行為,不會更糟)。
    /// </summary>
    [DefaultExecutionOrder(151)]   // 在 AvatarTranslucentDepth(150) 之後
    public sealed class AvatarTransparentRank : MonoBehaviour
    {
        public const int BaseQueue = 3000;
        /// <summary>每個角色佔的 queue 區段寬度 = 它自己最多能釘住幾件半透明材質的先後。</summary>
        public const int Band = 50;
        /// <summary>最大 rank(0 起算)。(MaxRank+1)*Band 必須 ≤ DepthQueue-BaseQueue = 500。</summary>
        public const int MaxRank = 9;

        private readonly List<Material> _mats = new List<Material>();
        private readonly List<int> _order = new List<int>();
        private int _rank = -1;

        private static readonly List<AvatarTransparentRank> Live = new List<AvatarTransparentRank>();
        private static int _lastFrame = -1;

        /// <summary>畫這些角色的相機。舞台/房間各自注入自己那台(舞台是 SceneCam);沒注入就自己找一台
        /// 看得到該角色 layer 的。找不到相機 → 全部維持 rank 0,也就是原本的行為。</summary>
        public static System.Func<Camera> CameraProvider;

        /// <summary>這個角色目前的 rank(測試用;-1 = 還沒排過)。</summary>
        public int RankForTest => _rank;

        /// <summary>Pure: 角色 <paramref name="rank"/> 的第 <paramref name="order"/> 件半透明材質的 renderQueue。
        /// order 夾在一個 Band 之內 —— 溢出的件共用最後一格(它們之間退回距離排序,與加這個類別之前一樣)。</summary>
        public static int QueueFor(int rank, int order)
        {
            if (rank < 0) rank = 0; else if (rank > MaxRank) rank = MaxRank;
            if (order < 0) order = 0; else if (order > Band - 1) order = Band - 1;
            return BaseQueue + rank * Band + order;
        }

        /// <summary>Pure: 依「離相機的距離平方」給 rank —— **遠的 rank 小**(先畫)。同距離的維持原順序(穩定),
        /// 免得兩個站在同一排的人每幀互換 rank 而閃爍。</summary>
        public static int[] RanksByDistance(IList<float> sqrDistances)
        {
            int n = sqrDistances?.Count ?? 0;
            var idx = new int[n];
            for (int i = 0; i < n; i++) idx[i] = i;
            // 插入排序(n 很小,而且是穩定排序 → 同距離不會每幀翻面)
            for (int i = 1; i < n; i++)
            {
                int k = idx[i]; float d = sqrDistances[k]; int j = i - 1;
                while (j >= 0 && sqrDistances[idx[j]] < d) { idx[j + 1] = idx[j]; j--; }
                idx[j + 1] = k;
            }
            var ranks = new int[n];
            for (int r = 0; r < n; r++) ranks[idx[r]] = r;
            return ranks;
        }

        /// <summary>
        /// 收集 <paramref name="avatarRoot"/> 底下所有「半透明衣物」材質,記下它們**目前**的 queue 當作角色內
        /// 順序,之後由 rank 統一平移。沒有半透明件(絕大多數角色)就什麼都不做、也不留元件。回傳收到幾筆。
        /// 可以重複呼叫(換穿會重建角色)。
        /// </summary>
        public static int Attach(GameObject avatarRoot)
        {
            if (avatarRoot == null) return 0;
            var self = avatarRoot.GetComponent<AvatarTransparentRank>();
            if (self != null) { self._mats.Clear(); self._order.Clear(); self._rank = -1; }

            var mats = new List<Material>();
            var orders = new List<int>();
            foreach (var mr in avatarRoot.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (mr == null) continue;
                var sm = mr.sharedMaterials;
                if (sm == null) continue;
                foreach (var m in sm)
                {
                    if (m == null || m.mainTexture == null) continue;
                    // 只收「透明帶」裡的衣物材質。深度分身(3500)與不透明件都不在射程內。
                    if (m.renderQueue < BaseQueue || m.renderQueue >= AvatarTranslucentDepth.DepthQueue) continue;
                    mats.Add(m);
                    orders.Add(m.renderQueue - BaseQueue);
                }
            }
            if (mats.Count == 0)
            {
                if (self != null) Destroy(self);
                return 0;
            }
            if (self == null) self = avatarRoot.AddComponent<AvatarTransparentRank>();
            self._mats.AddRange(mats);
            self._order.AddRange(orders);
            // 🔴 註冊在這裡,不在 OnEnable:編輯器的 EditMode(非播放)加元件**不會**呼叫 OnEnable,
            //    整個名冊會是空的 → 測試裡怎麼排都沒動靜(實測踩過)。
            if (!Live.Contains(self)) Live.Add(self);
            return mats.Count;
        }

        private void OnDisable() => Live.Remove(this);
        private void OnDestroy() => Live.Remove(this);

        private void LateUpdate()
        {
            if (Time.frameCount == _lastFrame) return;   // 每幀只由最先跑到的那個實例重排一次全體
            _lastFrame = Time.frameCount;
            ReorderAll();
        }

        private static readonly List<float> _dist = new List<float>();

        private static void ReorderAll()
        {
            for (int i = Live.Count - 1; i >= 0; i--) if (Live[i] == null) Live.RemoveAt(i);
            if (Live.Count == 0) return;
            if (Live.Count == 1) { Live[0].ApplyRank(0); return; }   // 單一角色 = 原本的行為

            var cam = ResolveCamera();
            if (cam == null) { foreach (var a in Live) a.ApplyRank(0); return; }
            var camPos = cam.transform.position;
            _dist.Clear();
            foreach (var a in Live) _dist.Add((a.transform.position - camPos).sqrMagnitude);
            var ranks = RanksByDistance(_dist);
            for (int i = 0; i < Live.Count; i++) Live[i].ApplyRank(ranks[i]);
        }

        private static Camera ResolveCamera()
        {
            var provided = CameraProvider != null ? CameraProvider() : null;
            if (provided != null) return provided;
            // 沒注入 → 找一台「看得到第一個角色所在 layer」的 enabled 相機(舞台/房間各只有一台在畫角色)。
            int mask = 1 << (Live.Count > 0 && Live[0] != null ? Live[0].gameObject.layer : 0);
            var cams = Camera.allCameras;
            foreach (var c in cams)
                if (c != null && c.enabled && (c.cullingMask & mask) != 0 && c.targetTexture == null) return c;
            foreach (var c in cams)
                if (c != null && c.enabled && (c.cullingMask & mask) != 0) return c;
            return null;
        }

        private void ApplyRank(int rank)
        {
            if (rank == _rank) return;
            _rank = rank;
            for (int i = 0; i < _mats.Count; i++)
            {
                var m = _mats[i];
                if (m == null) continue;
                m.renderQueue = QueueFor(rank, _order[i]);
            }
        }

        /// <summary>測試用:立刻重排一次(不必等 LateUpdate)。</summary>
        public static void ReorderNowForTest() { _lastFrame = -1; ReorderAll(); }
    }
}
