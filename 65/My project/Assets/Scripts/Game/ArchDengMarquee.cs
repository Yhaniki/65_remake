using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// SCN0006 遊樂場 拱門上的 72 顆小燈泡跑馬燈。
    ///
    /// 忠實復刻 Scene_UpdateSceneObjects_004baef0 的 case 6(0x4bafa3..0x4bb156 —— 我方另外用 capstone
    /// 逐指令核過，因為 Ghidra 把「設哪一顆燈」的 index 參數整個吃掉了，只讀反編譯會以為那些迴圈與
    /// index 無關、整個狀態機就解錯)。
    ///
    /// 兩組共用同一個 300 ms 計時器、但各自的計數器與模數:
    ///   A 組 = 燈 0..55(拱門封閉迴路 56 顆)，計數器 % 59
    ///   B 組 = 燈 56..71(拱門上方的環 16 顆)，計數器 % 19
    /// 每組的語意相同(N = 該組燈數):
    ///   c = 0        → 全暗
    ///   c = 1..N-1   → 燈 0..c-1 亮，其餘暗      ← 走過的燈「保持亮著」
    ///   c = N        → 全亮
    ///   c = N+1      → 全暗   ┐ 填滿之後的雙閃
    ///   c = N+2      → 全亮   ┘
    /// 也就是**累積填滿式**跑馬燈(亮點的頭沿 index 前進、尾巴不熄)，不是單顆追逐光。
    /// 59 與 19 都是質數，所以兩組合成的大週期是 1121 tick ≈ 336 秒。
    ///
    /// 貼圖:1.dds = 暗、2_.dds = 亮(實測解碼:premultiplied 平均亮度 23.2 vs 86.7，約 3.7 倍)。
    /// DENG.MSH 烘的預設材質就是 2_.dds，所以第一個 tick 之前 72 顆都是亮的 —— 我們也照樣先種亮的，
    /// 免得開場閃一下佔位材質。
    ///
    /// 一定要用「一個共用驅動器 + 每顆燈自己的 material」:泛用的換幀路徑是每支道具一個循環器，
    /// 表達不出「這一 tick 第 12 顆亮、第 13 顆暗」，看起來會變成隨機亂閃。與 SaloonDengMarquee /
    /// RoomDengMarquee 同一種東西，各自獨立以免動到已驗過的那兩條路。
    /// </summary>
    public sealed class ArchDengMarquee : MonoBehaviour
    {
        public const int GroupACount = 56;   // 燈 0..55
        public const int GroupBCount = 16;   // 燈 56..71
        public const int Bulbs = GroupACount + GroupBCount;
        public const float IntervalMs = 300f;

        private Texture _dim, _lit;
        private readonly List<Material>[] _bulbs = new List<Material>[Bulbs];
        private float _startTime;
        private int _lastTick = int.MinValue;

        private void Awake() { _startTime = Time.time; }

        /// <summary>兩張共用貼圖:dim = 1.dds(暗)、lit = 2_.dds(亮)。</summary>
        public void SetFrames(Texture dim, Texture lit) { _dim = dim; _lit = lit; }

        public bool HasFrames => _dim != null && _lit != null;

        /// <summary>登記一顆燈的材質(bulb 0 = placement 表的第 0 筆)。表序就是跑馬燈追的順序，
        /// 不能重排。</summary>
        public void Register(int bulb, Material[] mats)
        {
            if (bulb < 0 || bulb >= Bulbs || mats == null) return;
            var list = _bulbs[bulb] ?? (_bulbs[bulb] = new List<Material>());
            foreach (var m in mats)
                if (m != null)
                {
                    if (m.HasProperty("_Color")) m.color = Color.white;
                    if (_lit != null) m.mainTexture = _lit;   // MSH 預設就是亮的那張
                    list.Add(m);
                }
        }

        private void Update()
        {
            if (!HasFrames) return;
            int tick = (int)((Time.time - _startTime) / (IntervalMs / 1000f));
            if (tick == _lastTick) return;
            _lastTick = tick;
            ApplyTick(tick);
        }

        /// <summary>套用第 <paramref name="tick"/> 拍的亮暗分佈。tick → 狀態是純函式(計數器由 tick 取模
        /// 得到，不靠累加)，所以不會漂移，測試也能直接跳到任何一拍。</summary>
        public void ApplyTick(int tick)
        {
            int modA = GroupACount + 3, modB = GroupBCount + 3;
            int a = ((tick % modA) + modA) % modA;
            int b = ((tick % modB) + modB) % modB;
            for (int i = 0; i < GroupACount; i++) SetBulb(i, IsLit(a, i, GroupACount));
            for (int i = 0; i < GroupBCount; i++) SetBulb(GroupACount + i, IsLit(b, i, GroupBCount));
        }

        /// <summary>某一組的計數器 <paramref name="c"/> 下，組內第 <paramref name="i"/> 顆燈亮不亮。</summary>
        public static bool IsLit(int c, int i, int n)
        {
            if (c <= n) return i < c;      // 0 → 全暗;k → 前 k 顆亮;n → 全亮
            if (c == n + 1) return false;  // 填滿後的雙閃:先全暗
            return true;                   // c == n + 2:再全亮
        }

        private void SetBulb(int bulb, bool lit)
        {
            var list = _bulbs[bulb];
            if (list == null) return;
            var tex = lit ? _lit : _dim;
            for (int k = 0; k < list.Count; k++) if (list[k] != null) list[k].mainTexture = tex;
        }
    }
}
