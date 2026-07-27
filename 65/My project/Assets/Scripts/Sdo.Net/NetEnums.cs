using System;

namespace Sdo.Net
{
    /// <summary>
    /// 一個玩家在房間裡的狀態。**形狀與語意直接照抄 osu! 的 MultiplayerUserState** ——
    /// 那是一套已經在真實世界跑過大量玩家、把邊界情況都磨過的狀態機，沒有理由重新發明。
    ///
    /// 標「server 保留」的三個狀態 client **不准**自己設(送上來 server 會回 error{badState}):
    /// 它們代表的是「server 已經把這個人納入某個階段」，只有 server 有權宣告。
    ///
    /// enum 的數值順序刻意與 osu 一致，也刻意讓「參與本場的區間」是連續的
    /// (<see cref="WaitingForLoad"/> .. <see cref="Finished"/>)—— 但判斷一律走
    /// <see cref="NetState"/> 裡的具名 helper，不要在別處寫 <c>&gt;= WaitingForLoad</c> 這種比較。
    /// 那種寫法在有人往中間插一個新狀態時會靜默壞掉。
    /// </summary>
    public enum PlayState
    {
        /// <summary>閒著。也包含「在房間裡但這局不打」。</summary>
        Idle = 0,

        /// <summary>自己按了準備。下一場開始時會被納入。</summary>
        Ready = 1,

        /// <summary>**server 保留。** 開場瞬間所有 Ready 的人被轉成這個狀態，代表「正在載入」。</summary>
        WaitingForLoad = 2,

        /// <summary>程式載完了(場景、音檔、譜面都就緒)。</summary>
        Loaded = 3,

        /// <summary>
        /// 人也準備好了。osu 分成 Loaded / ReadyForGameplay 兩段是為了區分「機器慢」與「人在摸」:
        /// 機器慢的還停在 WaitingForLoad(會被載入逾時排除)，人在摸的已經 Loaded(會被強制推進)。
        /// **注意:房間推進到 Playing 的條件是「沒人還在 WaitingForLoad」,不是「全員 ReadyForGameplay」**
        /// —— 所以這個狀態不會阻塞開場，它只是更精細的資訊。
        /// </summary>
        ReadyForGameplay = 4,

        /// <summary>**server 保留。** 沒有人還在 WaitingForLoad 時，所有可開場的人一起被轉成這個狀態。</summary>
        Playing = 5,

        /// <summary>打完了，等結算。</summary>
        Finished = 6,

        /// <summary>**server 保留。** 正在看結算畫面。</summary>
        Results = 7,

        /// <summary>旁觀中。不佔座位。</summary>
        Spectating = 8,
    }

    /// <summary>房間整體的狀態。照抄 osu! 的 MultiplayerRoomState。</summary>
    public enum RoomStatus
    {
        /// <summary>開放中,可以加入。</summary>
        Open = 0,
        /// <summary>已經按了開始，但還有人在載入。</summary>
        WaitingForLoad = 1,
        /// <summary>正在進行。</summary>
        Playing = 2,
        /// <summary>已解散。</summary>
        Closed = 3,
    }

    /// <summary>
    /// 這個玩家手上有沒有房主選的那首歌。對映 osu! 的 DownloadState
    /// (它的 <c>Unknown</c> 也保留了 —— 那是「還在確認」，UI 要顯示成不同於「沒有」的樣子)。
    /// </summary>
    public enum Availability
    {
        /// <summary>還沒確認(剛換歌的瞬間)。UI:「確認中」。</summary>
        Unknown = 0,
        /// <summary>沒有這首歌。UI:頭貼上的「缺歌」徽章。</summary>
        Missing = 1,
        /// <summary>正在下載。UI:「下載」徽章 + 百分比。</summary>
        Downloading = 2,
        /// <summary>下載完了，正在匯入(重新掃描歌庫)。</summary>
        Importing = 3,
        /// <summary>有這首歌，可以打。</summary>
        Have = 4,
    }

    /// <summary>座位的狀態。</summary>
    public enum SeatState
    {
        /// <summary>空著,可以進來。</summary>
        Open = 0,
        /// <summary>被房主關閉。新人不能坐進來。</summary>
        Closed = 1,
        /// <summary>有人坐著。</summary>
        Taken = 2,
    }

    /// <summary>
    /// 隊伍 tag。對映官方玩家 record +0x48 ∈ {1,2,3} 與遊戲內的組隊格。
    /// <see cref="Free"/> = 不組隊(官方的「自由」)。
    /// </summary>
    public enum TeamTag
    {
        A = 0,
        B = 1,
        C = 2,
        /// <summary>自由 —— 不參與組隊。組隊模式下所有參與者都必須不是這個值。</summary>
        Free = 3,
    }

    /// <summary>
    /// enum ↔ wire 字串的轉換,以及狀態機的具名判定 helper。
    ///
    /// **為什麼 wire 上用字串而不是數字**:協定是 JSON，用字串讓封包直接看得懂
    /// (`nc localhost 27015` 就能讀懂發生什麼事)，debug 成本差很多。
    /// 代價是幾個 byte，對 5 Hz 的訊息量完全無感。
    ///
    /// 轉換一律用 switch 而不是 <c>Enum.Parse</c>/反射:反射在 IL2CPP 下有被裁掉的風險，
    /// 而且 <c>Enum.Parse</c> 對未知字串會拋例外 —— 我們要的是「未知就當成預設值/拒絕」。
    /// </summary>
    public static class NetState
    {
        // ---- PlayState ----

        public static string ToWire(PlayState s)
        {
            switch (s)
            {
                case PlayState.Idle: return "idle";
                case PlayState.Ready: return "ready";
                case PlayState.WaitingForLoad: return "waitingForLoad";
                case PlayState.Loaded: return "loaded";
                case PlayState.ReadyForGameplay: return "readyForGameplay";
                case PlayState.Playing: return "playing";
                case PlayState.Finished: return "finished";
                case PlayState.Results: return "results";
                case PlayState.Spectating: return "spectating";
                default: return "idle";
            }
        }

        public static bool TryParsePlayState(string w, out PlayState s)
        {
            switch (w)
            {
                case "idle": s = PlayState.Idle; return true;
                case "ready": s = PlayState.Ready; return true;
                case "waitingForLoad": s = PlayState.WaitingForLoad; return true;
                case "loaded": s = PlayState.Loaded; return true;
                case "readyForGameplay": s = PlayState.ReadyForGameplay; return true;
                case "playing": s = PlayState.Playing; return true;
                case "finished": s = PlayState.Finished; return true;
                case "results": s = PlayState.Results; return true;
                case "spectating": s = PlayState.Spectating; return true;
                default: s = PlayState.Idle; return false;
            }
        }

        /// <summary>
        /// client 可以自己宣告的狀態。其餘三個(<see cref="PlayState.WaitingForLoad"/>、
        /// <see cref="PlayState.Playing"/>、<see cref="PlayState.Results"/>)是 server 保留 ——
        /// client 送上來一律 error{badState}。
        ///
        /// 🔴 這是安全邊界:沒有這道檢查，一個改過的 client 可以自稱 Playing 來繞過載入同步。
        /// </summary>
        public static bool IsClientSettable(PlayState s)
        {
            switch (s)
            {
                case PlayState.Idle:
                case PlayState.Ready:
                case PlayState.Loaded:
                case PlayState.ReadyForGameplay:
                case PlayState.Finished:
                case PlayState.Spectating:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 這個狀態可以開始遊戲了嗎?= <see cref="PlayState.Loaded"/> 或
        /// <see cref="PlayState.ReadyForGameplay"/>。照抄 osu 的 MultiplayerRoomUser.CanStartGameplay()。
        /// </summary>
        public static bool CanStartGameplay(PlayState s)
            => s == PlayState.Loaded || s == PlayState.ReadyForGameplay;

        /// <summary>
        /// 這個人算是「參與本場」嗎?(從被納入載入開始，到打完為止)
        /// 對應 osu 的 <c>state &gt;= WaitingForLoad &amp;&amp; state &lt;= FinishedPlay</c>，
        /// 但寫成明確列舉 —— 往 enum 中間插新狀態時不會靜默改變語意。
        /// </summary>
        public static bool IsInMatch(PlayState s)
        {
            switch (s)
            {
                case PlayState.WaitingForLoad:
                case PlayState.Loaded:
                case PlayState.ReadyForGameplay:
                case PlayState.Playing:
                case PlayState.Finished:
                    return true;
                default:
                    return false;
            }
        }

        // ---- RoomStatus ----

        public static string ToWire(RoomStatus s)
        {
            switch (s)
            {
                case RoomStatus.Open: return "open";
                case RoomStatus.WaitingForLoad: return "waitingForLoad";
                case RoomStatus.Playing: return "playing";
                case RoomStatus.Closed: return "closed";
                default: return "open";
            }
        }

        public static bool TryParseRoomStatus(string w, out RoomStatus s)
        {
            switch (w)
            {
                case "open": s = RoomStatus.Open; return true;
                case "waitingForLoad": s = RoomStatus.WaitingForLoad; return true;
                case "playing": s = RoomStatus.Playing; return true;
                case "closed": s = RoomStatus.Closed; return true;
                default: s = RoomStatus.Open; return false;
            }
        }

        // ---- Availability ----

        public static string ToWire(Availability a)
        {
            switch (a)
            {
                case Availability.Unknown: return "unknown";
                case Availability.Missing: return "missing";
                case Availability.Downloading: return "downloading";
                case Availability.Importing: return "importing";
                case Availability.Have: return "have";
                default: return "unknown";
            }
        }

        public static bool TryParseAvailability(string w, out Availability a)
        {
            switch (w)
            {
                case "unknown": a = Availability.Unknown; return true;
                case "missing": a = Availability.Missing; return true;
                case "downloading": a = Availability.Downloading; return true;
                case "importing": a = Availability.Importing; return true;
                case "have": a = Availability.Have; return true;
                default: a = Availability.Unknown; return false;
            }
        }

        // ---- SeatState ----

        public static string ToWire(SeatState s)
        {
            switch (s)
            {
                case SeatState.Open: return "open";
                case SeatState.Closed: return "closed";
                case SeatState.Taken: return "taken";
                default: return "open";
            }
        }

        public static bool TryParseSeatState(string w, out SeatState s)
        {
            switch (w)
            {
                case "open": s = SeatState.Open; return true;
                case "closed": s = SeatState.Closed; return true;
                case "taken": s = SeatState.Taken; return true;
                default: s = SeatState.Open; return false;
            }
        }

        // ---- TeamTag ----

        /// <summary>隊伍 tag 在 wire 上是數字(0..3),與遊戲內 GameSession.Team 同一套編碼。</summary>
        public static bool IsValidTeam(int t) => t >= 0 && t <= 3;

        public static TeamTag ClampTeam(int t) => IsValidTeam(t) ? (TeamTag)t : TeamTag.Free;
    }
}
