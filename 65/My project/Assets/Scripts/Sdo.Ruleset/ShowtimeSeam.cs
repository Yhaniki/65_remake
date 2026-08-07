using System.Collections.Generic;

namespace Sdo.Ruleset
{
    /// <summary>
    /// ShowTime 自動視窗 → 手動的**接縫**。
    ///
    /// 視窗中每顆音符都由自動打擊強制 PERFECT,手動判定整條路徑不跑 —— 玩家在視窗裡按的鍵,
    /// 按下/放開的邊緣就在自動幀被吞掉、永遠不會有第二次。視窗一結束,那些按鍵沒有任何人負責:
    /// 玩家明明「一直跟著按」,音符卻流過判定線變 MISS,長條也在邊界斷掉。
    ///
    /// 這個類別只做記錄與決策(不碰 Unity、不碰音符物件內部):視窗中每一軌按了幾次、各自瞄準哪顆、
    /// 什麼時候放開;視窗結束時交給呼叫端逐筆補判。搭配 <see cref="ShowtimeSeamRules"/> 的兩條規則,
    /// 讓接縫兩側的按鍵各自落在該落的音符上。
    ///
    /// <typeparamref name="TNote"/> 是呼叫端的音符 handle(remake 傳 RuntimeNote),這裡只當識別用。
    /// </summary>
    public sealed class ShowtimeSeam<TNote> where TNote : class
    {
        /// <summary>
        /// 每軌最多保留幾次視窗內按鍵。單一插槽(舊版)會讓「視窗末尾連按兩下」的第一下被後一下蓋掉,
        /// 那顆音符就沒人補判 → MISS。四次足夠涵蓋接縫前的一小串連打,又不會把整個視窗的按鍵都堆著。
        /// </summary>
        public const int MaxPressesPerLane = 4;

        public readonly struct Press
        {
            /// <summary>玩家**真實**按下的譜面時間(ms,已做輪詢中點修正)—— 補判要用它,不是接縫那一幀的時間。</summary>
            public readonly double AtMs;
            /// <summary>這次按下當下瞄準的那一顆音符(null = 附近沒有可打的音符)。補判只認這一顆,絕不重新搜尋鄰居。</summary>
            public readonly TNote Aimed;
            public Press(double atMs, TNote aimed) { AtMs = atMs; Aimed = aimed; }
        }

        private readonly List<Press>[] _presses;
        private readonly double[] _lastPressMs;
        private readonly double[] _releaseMs;

        public ShowtimeSeam(int lanes)
        {
            _presses = new List<Press>[lanes];
            _lastPressMs = new double[lanes];
            _releaseMs = new double[lanes];
            for (int i = 0; i < lanes; i++) _presses[i] = new List<Press>(MaxPressesPerLane);
            Clear();
        }

        public int Lanes => _presses.Length;

        /// <summary>視窗結束後,接縫的**寬限期**長度(ms)。見 <see cref="ShowtimeSeamRules"/>。</summary>
        public double GraceMs { get; set; }

        /// <summary>視窗結束的譜面時間(ms);-1 = 目前沒有待處理的接縫。</summary>
        public double EndedAtMs { get; private set; } = -1.0;

        /// <summary>視窗剛結束(接縫補判就在這一幀進行)。</summary>
        public bool JustEnded { get; private set; }

        /// <summary>清空所有 latch —— 視窗開始、接縫處理完、以及重新開歌時都要呼叫。</summary>
        public void Clear()
        {
            for (int i = 0; i < _presses.Length; i++)
            {
                _presses[i].Clear();
                _lastPressMs[i] = -1.0;
                _releaseMs[i] = -1.0;
            }
            EndedAtMs = -1.0;
            JustEnded = false;
        }

        /// <summary>視窗內的一次真實按下。<paramref name="aimed"/> = 當下瞄準的音符(可為 null)。</summary>
        public void OnPress(int lane, double atMs, TNote aimed)
        {
            if (lane < 0 || lane >= _presses.Length) return;
            var list = _presses[lane];
            if (list.Count == MaxPressesPerLane) list.RemoveAt(0);   // 滿了就丟最舊的(最舊的最可能已被自動打掉)
            list.Add(new Press(atMs, aimed));
            _lastPressMs[lane] = atMs;
        }

        /// <summary>視窗內的一次真實放開。長條尾判要用這個**真實**時刻,不是接縫那一幀。</summary>
        public void OnRelease(int lane, double atMs)
        {
            if (lane < 0 || lane >= _presses.Length) return;
            _releaseMs[lane] = atMs;
        }

        /// <summary>視窗結束 → 武裝接縫。</summary>
        public void MarkWindowEnded(double atMs)
        {
            EndedAtMs = atMs;
            JustEnded = true;
        }

        /// <summary>接縫補判已經做完(一幀一次)。寬限期仍然有效,只是不再重播按鍵。</summary>
        public void ConsumeSeamFrame()
        {
            JustEnded = false;
            for (int i = 0; i < _presses.Length; i++)
            {
                _presses[i].Clear();
                _lastPressMs[i] = -1.0;
                _releaseMs[i] = -1.0;
            }
        }

        /// <summary>現在還在接縫寬限期內(視窗結束後 <see cref="GraceMs"/> 毫秒)。</summary>
        public bool InGrace(double nowMs) => EndedAtMs >= 0.0 && nowMs - EndedAtMs <= GraceMs;

        public IReadOnlyList<Press> PressesFor(int lane)
            => lane >= 0 && lane < _presses.Length ? (IReadOnlyList<Press>)_presses[lane] : System.Array.Empty<Press>();

        /// <summary>視窗內這一軌的**最後一個動作是放開**(按下 → 放開,而不是還按著)。</summary>
        public bool ReleasedAfterLastPress(int lane)
            => lane >= 0 && lane < _presses.Length
               && _releaseMs[lane] >= 0.0
               && _releaseMs[lane] >= _lastPressMs[lane];

        /// <summary>視窗內這一軌最後一次放開的時間(-1 = 沒放開過)。</summary>
        public double ReleaseMsFor(int lane)
            => lane >= 0 && lane < _presses.Length ? _releaseMs[lane] : -1.0;
    }

    /// <summary>
    /// 接縫兩側「這次按鍵該落在哪顆音符」的兩條規則。都是純函式,與 <see cref="ShowtimeSeam{TNote}"/> 分開,
    /// 因為呼叫端(手動判定路徑)在接縫寬限期內也要用。
    /// </summary>
    public static class ShowtimeSeamRules
    {
        /// <summary>
        /// **接縫寬限期內的提早按鍵要忽略,不能當場判成 MISS。**
        ///
        /// 判定窗是對稱的:離音符 180~240ms(精2)按下也算「打到了」,只是評價是 Miss。一般遊玩這樣沒問題
        /// —— 那是你自己按爛了。但 ShowTime 自動視窗會把你的節奏整個帶偏:視窗中你的按鍵全被吞掉,自動一
        /// 結束,你沿用自動的節奏按下去,抓到的是 200ms 後才到的那顆,當場被判死,你再也沒機會在正確時間補按。
        /// 這就是「我一直跟著按,可是就是會 miss」。
        ///
        /// 寬限期內改成 osu!mania 的作法:**太早的按鍵不判定**,音符留在譜上等你在正確時間再按一次。
        /// 只放寬「提早」那一側 —— 太晚的按鍵照舊判 Miss(音符已經過線,放過去就是漏打)。
        /// </summary>
        public static bool IgnoreEarlyPress(Judgment? judged, double noteStartMs, double pressMs)
            => judged == Judgment.Miss && pressMs < noteStartMs;

        /// <summary>
        /// **該軌還按著一條沒結束的長條時,這次按下不可能是要打「長條結束之後才到」的音符。**
        ///
        /// 自動視窗會幫你把長條頭按下去(<c>_holding[lane]</c> 被自動佔用)。視窗一結束你按鍵想接手,
        /// 手動路徑卻會跳過那條長條(它已判定過)去抓下一顆:下一顆被提早判掉,而且如果它也是長條,
        /// <c>BeginHold</c> 會**覆蓋** <c>_holding[lane]</c> —— 原本那條長條變成沒人結算的孤兒,
        /// 尾判永遠不出現,看起來就是「自動長條按到一半斷掉」。
        ///
        /// 判準只看時間:候選音符在進行中長條的結束時間之後 → 這次按下是「重新按住這條長條」,不是打新音符。
        /// </summary>
        public static bool PressBelongsToRunningHold(double holdEndMs, double candidateStartMs)
            => candidateStartMs > holdEndMs;
    }
}
