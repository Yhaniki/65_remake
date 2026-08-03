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
            if (avail == Availability.Missing) return RoomSeatBadge.NoMap;
            if (isHost) return RoomSeatBadge.Host;
            return isReady ? RoomSeatBadge.Ready : RoomSeatBadge.None;
        }

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
