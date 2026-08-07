using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 連線時的 ShowTime 釋放同步 —— 「**別人**按 SPACE 的時候,我這台要看得見什麼」。
    ///
    /// 沒有這一層的話,遠端玩家在我的畫面上放 ShowTime 只會維持原本的歌曲編舞、腳下什麼都不會亮
    /// (使用者回報:「遠端的人施放 space 跳 breaking 動作,看不到它的發光和特殊的舞蹈動作」)。
    /// 原因是遠端舞者的狀態**全部**從 5 Hz 的分數流推導(<see cref="TickRemoteGates"/>),而分數流裡
    /// 只有判定計數 —— 「他正在自動全 PERFECT 的視窗中」與「他打得很好」長得一模一樣,推不出來;
    /// 就算推得出來,還缺兩個本機不可能算出的值(檔位 + breaking 變體,見 <see cref="Sdo.Net.ShowtimeReleaseRules"/>)。
    /// 所以走與 50 連擊里程碑同一條路:一則 C→S→C 的一次性事件(<c>NetProto.ShowtimeRelease</c>)。
    ///
    /// 這台**只重現屬於那位舞者身上的東西**:body_star 光環 + 那一支 breaking。
    /// note 板的金色換皮、兩側 EDGE4 閃電柱、中央 BOOM、氣條與加分數字都不做 —— 那些是**我自己的**
    /// 打擊介面,別人放 ShowTime 不該讓我的板子閃起來。
    /// </summary>
    public sealed partial class ScreenGameplay
    {
        /// <summary>
        /// 本機按 SPACE 放出視窗時觸發(level = 釋放檔位 0/1/2、variant = 該檔位的 breaking 變體、windowMs = 視窗長度)。
        /// FrontendApp 接到 <c>NetClient.SendShowtimeRelease</c>。離線時沒人訂閱 → 完全不花錢。
        /// </summary>
        public Action<int, int, double> LocalShowtimeRelease;

        /// <summary>一位遠端玩家正在進行中的 ShowTime 視窗。key = userId。</summary>
        private sealed class RemoteShowtimeWindow
        {
            public int DancerIndex;
            /// <summary>視窗結束的譜面時間(本機時鐘;收到那一刻 + windowMs)。</summary>
            public double EndMs;
            public GameObject AuraGo, AuraAnchor;
            /// <summary>換上 breaking 之前他原本的編舞與時鐘 —— 視窗結束要原封不動還回去。</summary>
            public DpsLoader SavedDps;
            public Func<float> SavedDanceTime;
            public bool DpsSwapped;
        }

        private readonly Dictionary<int, RemoteShowtimeWindow> _remoteShowtime =
            new Dictionary<int, RemoteShowtimeWindow>();

        /// <summary>迭代中要移除的 key(不能在 foreach 裡改字典)。</summary>
        private readonly List<int> _remoteShowtimeDone = new List<int>();

        /// <summary>
        /// 一位遠端玩家放出了 ShowTime 視窗:換上他那一支 breaking + 點亮他的光環。
        /// </summary>
        public void PlayRemoteShowtime(int userId, int level, int variant, double windowMs)
        {
            if (userId == 0 || _ended) return;
            // 收到怪值不能讓場上那隻舞者壞掉(卡在街舞不回來 / 光環永遠不熄)→ 一律夾進合法範圍再用。
            level = Sdo.Net.ShowtimeReleaseRules.ClampLevel(level);
            variant = Sdo.Net.ShowtimeReleaseRules.ClampVariant(level, variant);   // E 只有 6 支,夾的上界看檔位
            windowMs = Sdo.Net.ShowtimeReleaseRules.ClampWindowMs(windowMs);

            int idx;
            if (!TryGetDancerIndex(userId, out idx)) return;
            if (idx < 0 || idx >= _extraDancers.Count) return;
            var av = _extraDancers[idx];
            if (av == null) return;   // 本機那一格是 null 佔位(server 不會把事件回送給發送者,走到這裡代表資料對不上)

            // 人已經走了就不演 —— 那則事件是他離開前送出、路上還在飛的(與 PlayRemoteComboMilestone 同一條規則;
            // **死掉的人照演**:ShowTime 模式沒有血條,他還在打)。
            var opp = NetOpponents != null ? NetOpponents() : null;
            if (LeftRemote(opp, userId)) return;

            // 已經有一個開著(視窗重疊 / 事件重送)→ 先乾淨收掉再開新的,不然 SavedDps 會被 breaking 覆蓋,
            // 還原時把他永遠留在街舞上。
            EndRemoteShowtime(userId);

            var w = new RemoteShowtimeWindow { DancerIndex = idx, EndMs = _nowMs + windowMs };

            var bd = LoadBreakDps(level, variant);
            if (bd != null)
            {
                w.SavedDps = av.Dps; w.SavedDanceTime = av.DanceTimeSec; w.DpsSwapped = true;
                double startMs = _nowMs;
                av.Dps = bd;
                // 與本機的 StartBreakSegment 同一個形狀:**不夾上限**的經過秒數 —— 一旦超過這支 breaking 的
                // 全長,SdoAvatar 自己會落回 RestMot 待機(官方的「break 提早跳完 → idle 尾巴」)。
                av.DanceTimeSec = () => (float)((_nowMs - startMs) / 1000.0);
            }

            SpawnRemoteAura(w, idx);
            _remoteShowtime[userId] = w;
            Debug.Log("[showtime] 遠端 " + userId + " 釋放 lv" + level + " 變體 " + variant
                      + " → " + windowMs.ToString("0") + "ms" + (bd == null ? "(缺 breaking 資產,只有光環)" : ""));
        }

        /// <summary>
        /// 每幀:光環跟著那位舞者走、到期(或人走了)就還原。
        /// 🔴 一定要排在 <see cref="TickRemoteGates"/> **之後** —— 視窗中他一定在跳(自動全 PERFECT),
        /// 那個結論要蓋過從分數流推導出來的 gate,否則剛好跨到結算點時街舞會被 gate 關掉變成站著。
        /// </summary>
        private void TickRemoteShowtime(double nowMs)
        {
            if (_remoteShowtime.Count == 0) return;
            var opp = NetOpponents != null ? NetOpponents() : null;

            _remoteShowtimeDone.Clear();
            foreach (var kv in _remoteShowtime)
            {
                var w = kv.Value;
                if (nowMs >= w.EndMs || LeftRemote(opp, kv.Key)) { _remoteShowtimeDone.Add(kv.Key); continue; }

                // 官方 FUN_00930e50:光環每幀跟著舞者 root 的 X/Z,Y 釘在固定高度(本機走的是同一組值)。
                if (w.AuraAnchor != null)
                {
                    var src = DancerFloorAnchor(w.DancerIndex);
                    w.AuraAnchor.transform.position = new Vector3(src.x, showtimeAuraY, src.z);
                }
                // 視窗中他一定在跳(見上面的註解)。人走了 / 死了的處理仍然交給 DanceGate.RemoteEnabled ——
                // 這裡只在「他還在場上」時把 gate 頂住。
                if (_dancerDancing != null && w.DancerIndex < _dancerDancing.Length
                    && !_dancerDancing[w.DancerIndex])
                {
                    _dancerDancing[w.DancerIndex] = true;
                    RecordDancerGate(w.DancerIndex, nowMs);   // 結算的背景回放也要看到這一段他在跳(只記變化)
                }
            }

            for (int i = 0; i < _remoteShowtimeDone.Count; i++) EndRemoteShowtime(_remoteShowtimeDone[i]);
            _remoteShowtimeDone.Clear();
        }

        /// <summary>
        /// 收掉一位遠端玩家的視窗:還原他原本的編舞與時鐘、銷毀光環。
        /// 沒有這一位的視窗時什麼都不做(重入安全 —— <see cref="PlayRemoteShowtime"/> 開場就會呼叫一次)。
        /// </summary>
        private void EndRemoteShowtime(int userId)
        {
            RemoteShowtimeWindow w;
            if (!_remoteShowtime.TryGetValue(userId, out w)) return;
            _remoteShowtime.Remove(userId);

            var av = w.DancerIndex >= 0 && w.DancerIndex < _extraDancers.Count ? _extraDancers[w.DancerIndex] : null;
            // 🔴 只有真的換過才還原。沒換過就寫 null 進去 = 把他的編舞抹掉,那位舞者整首歌剩下的部分都站著。
            if (w.DpsSwapped && av != null) { av.Dps = w.SavedDps; av.DanceTimeSec = w.SavedDanceTime; }
            if (w.AuraGo != null) Destroy(w.AuraGo);
            if (w.AuraAnchor != null) Destroy(w.AuraAnchor);
        }

        /// <summary>
        /// 全部收掉。曲末(<see cref="EnterResult"/>)一定要呼叫:結算的背景回放會把每位舞者的
        /// <c>DanceTimeSec</c> 重指到迴圈時鐘,這時還開著的視窗會在回放開始後把它蓋回 breaking 的時鐘
        /// (本機那一份在同一處也是這樣處理的,見 EnterResult 的長註解)。
        /// </summary>
        private void ClearRemoteShowtimeFx()
        {
            if (_remoteShowtime.Count == 0) return;
            _remoteShowtimeDone.Clear();
            foreach (var kv in _remoteShowtime) _remoteShowtimeDone.Add(kv.Key);
            for (int i = 0; i < _remoteShowtimeDone.Count; i++) EndRemoteShowtime(_remoteShowtimeDone[i]);
            _remoteShowtimeDone.Clear();
        }

        /// <summary>這位舞者腳下的位置。星環的 transform 每幀跟著骨盆走,所以它就是最準的錨點
        /// (本機那條路徑用的是同一個東西 —— <c>_floorRing.Follow</c>)。</summary>
        private Vector3 DancerFloorAnchor(int dancerIndex)
        {
            if (_dancerRingTr != null && dancerIndex >= 0 && dancerIndex < _dancerRingTr.Length
                && _dancerRingTr[dancerIndex] != null)
                return _dancerRingTr[dancerIndex].position;
            var root = dancerIndex >= 0 && dancerIndex < _extraRoots.Count ? _extraRoots[dancerIndex] : null;
            return root != null ? root.position : Vector3.zero;
        }

        private void SpawnRemoteAura(RemoteShowtimeWindow w, int dancerIndex)
        {
            EftFile file;
            if (!TryLoadNamedEft(showtimeAuraEft, out file)) return;

            var pos = DancerFloorAnchor(dancerIndex);
            w.AuraAnchor = new GameObject("RemoteShowtimeAuraAnchor");
            w.AuraAnchor.transform.position = new Vector3(pos.x, showtimeAuraY, pos.z);
            w.AuraGo = new GameObject("RemoteShowtimeAura");
            w.AuraGo.transform.position = w.AuraAnchor.transform.position;
            int layer = use3dCamera ? SceneLayer : 0;
            var eff = w.AuraGo.AddComponent<EftEffect>();
            eff.Persistent = true;   // 整個視窗循環,結束時銷毀
            eff.Init(file, showtimeAuraScale, w.AuraAnchor.transform, ResolveEftTex, _addMat, layer,
                     comboBurstBright, comboGlow, comboGlowSpread, ResolveEftMesh);
            if (use3dCamera) SetLayerRecursive(w.AuraGo, SceneLayer);
        }

        private bool TryGetDancerIndex(int userId, out int index)
        {
            index = -1;
            if (netDancers == null) return false;
            for (int i = 0; i < netDancers.Length; i++)
                if (netDancers[i].UserId == userId) { index = i; return true; }
            return false;
        }
    }
}
