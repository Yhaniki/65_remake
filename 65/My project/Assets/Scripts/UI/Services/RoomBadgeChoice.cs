using Sdo.Net;

namespace Sdo.UI.Services
{
    /// <summary>頭貼下緣那一條徽章一次只畫一張,這是「畫哪一張」。</summary>
    public enum RoomSeatBadge
    {
        /// <summary>空位、或有人坐但沒有任何狀態(不是房主、沒按準備、歌也在手上)。</summary>
        None = 0,
        Host,
        Ready,
        NoMap,
        Playing,
    }

    /// <summary>
    /// 房間六格頭貼下緣那一條徽章的「該畫哪一張」。
    ///
    /// 官方在那個位置(y=102)只有兩張:HOST(master.an / b06..b09)與 READY(Room66.an / a06..a09),
    /// 天生互斥 —— 房主沒有「準備」這個狀態(<see cref="NetSeat.Ready"/> 對房主恆 false)。
    /// remake 把 NO MAP(c06..c09)與 PLAYING(d06..d09)也放進**同一條**,四張共用那個位置,
    /// 並且**排在 HOST / READY 之上**:
    ///
    ///   PLAYING &gt; NO MAP &gt; HOST &gt; READY
    ///
    /// 為什麼狀態壓過身分:對留在房間的人來說,「他在場裡打歌」「他沒有這首歌」是現在唯一有用的資訊
    /// (房主在場中時那格畫 PLAYING,不是 HOST);等他回來 / 補完歌,那一格自然退回 HOST 或 READY。
    /// PLAYING 壓過 NO MAP 是同一個道理 —— 已經在打歌的人「缺不缺歌」不再是有用的資訊。
    ///
    /// **唯一的例外是自己那一格**(<c>isLocal</c>):看得到房間就代表自己已經回來了,不該看到自己 PLAYING。
    /// 理由寫在 <see cref="For"/> 的參數說明。
    ///
    /// 這裡只做「狀態 → 哪一張」這一步,與 Unity 無關 → 可單元測試(見 RoomBadgeChoiceTests)。
    /// 顏色是另一步(隊伍 → 幀索引,見 <see cref="RoomBadgeFrames"/>)。
    /// </summary>
    public static class RoomBadgeChoice
    {
        /// <param name="taken">這一格有人坐嗎(空位/關閉的位子什麼都不畫)。</param>
        /// <param name="isHost">🔴 由呼叫端算:線上看 server 的 HostUserId,離線退回 SeatInfo.IsHost。</param>
        /// <param name="isReady">按了「準備」。房主恆 false,所以順序上先判斷 host 也不會吃掉它。</param>
        /// <param name="isLocal">
        /// 這格是**本機自己**嗎。是的話永遠不畫 PLAYING —— 「我看得到房間」本身就代表我已經不在場上了。
        ///
        /// 🔴 為什麼需要這個例外:中離(Esc)或打完先回房時,client 送的是 <c>playFinished</c>,server 把座位
        /// 標成 <see cref="PlayState.Finished"/>,而那要等**全場**都打完才會被 ClearResults 打回 Idle。
        /// 中間那段(別人還在跳完整首歌,可能一兩分鐘)自己人明明就站在房間裡走動聊天,那一格卻掛著
        /// PLAYING。使用者的需求原話是「自己照理來說不會看到 playing」。
        ///
        /// 只擋自己這一格:別人畫面上的你仍然是 PLAYING(server 狀態沒動,那是他們需要的資訊 ——
        /// 你的成績還在這一場裡)。
        /// </param>
        public static RoomSeatBadge For(bool taken, bool isHost, bool isReady, PlayState play, Availability avail, bool isLocal)
        {
            if (!taken) return RoomSeatBadge.None;
            if (!isLocal && IsInMatch(play)) return RoomSeatBadge.Playing;
            if (StillWithoutSong(avail)) return RoomSeatBadge.NoMap;
            if (isHost) return RoomSeatBadge.Host;
            return isReady ? RoomSeatBadge.Ready : RoomSeatBadge.None;
        }

        /// <summary>
        /// 這個人「還沒有這首歌」嗎 —— NO MAP 要一路掛到歌真的進歌庫為止。
        ///
        /// 🔴 <b>不能只認 <see cref="Availability.Missing"/>。</b>缺歌的人按過準備之後
        /// (在上一首歌上按的),R17 刻意保留那份 Ready 意願 —— avail 變動不會取消它。
        /// 於是下載一開始、avail 從 missing 翻成 downloading 的那一刻,NO MAP 這張就消失,
        /// 徽章掉回底下的 READY:歌一個位元組都還沒進歌庫,房主卻看到那格寫著「準備」。
        /// (實機:房裡一個人 no map + 已準備,房主按開始 → 觸發上傳 → 對方開始下載 → 變 READY。)
        /// 房主照樣開不了場(server 的 RequestStart 與 UI 的 CanStart 都要求 avail==have),
        /// 只是畫面上再也沒有東西說明為什麼 —— 這正是最糟的一種:狀態在騙人。
        ///
        /// <see cref="Availability.Importing"/> 同理:檔案下載完了,但歌庫重掃還沒跑完,
        /// 那一刻他仍然打不了這首歌。
        ///
        /// <see cref="Availability.Unknown"/> **不算** —— 那是「還沒回報」(換歌會把全房打回
        /// unknown,R9),只有短短一瞬。把它當缺歌的話,每次房主換歌全房的徽章都會閃一下 NO MAP。
        ///
        /// 下載進度本身是另一件事,畫在頭貼下緣那條跑條(<c>RoomScreen.RenderSlotBar</c>)——
        /// 兩者同時出現正是預期畫面:NO MAP 徽章 + 綠色跑條在跑。
        /// </summary>
        private static bool StillWithoutSong(Availability avail)
            => avail == Availability.Missing
            || avail == Availability.Downloading
            || avail == Availability.Importing;

        /// <summary>
        /// 這個遊玩狀態算「在這一場裡」嗎 —— 頭貼要不要畫 PLAYING。
        ///
        /// 🔴 這**不是** <see cref="NetState.IsInMatch"/>,差別是多一個 <see cref="PlayState.Results"/>:
        /// 協定那邊問的是「要不要納入這一場的同步」(結算已經不用了),這裡問的是「他人還在不在房間裡」——
        /// 停在結算畫面的人還沒回來,房間這格就該繼續畫 PLAYING。不要把兩者合併。
        /// </summary>
        public static bool IsInMatch(PlayState s)
            => s == PlayState.WaitingForLoad || s == PlayState.Loaded || s == PlayState.ReadyForGameplay
            || s == PlayState.Playing || s == PlayState.Finished || s == PlayState.Results;
    }
}
