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
    /// 這裡只做「狀態 → 哪一張」這一步,與 Unity 無關 → 可單元測試(見 RoomBadgeChoiceTests)。
    /// 顏色是另一步(隊伍 → 幀索引,見 <see cref="RoomBadgeFrames"/>)。
    /// </summary>
    public static class RoomBadgeChoice
    {
        /// <param name="taken">這一格有人坐嗎(空位/關閉的位子什麼都不畫)。</param>
        /// <param name="isHost">🔴 由呼叫端算:線上看 server 的 HostUserId,離線退回 SeatInfo.IsHost。</param>
        /// <param name="isReady">按了「準備」。房主恆 false,所以順序上先判斷 host 也不會吃掉它。</param>
        public static RoomSeatBadge For(bool taken, bool isHost, bool isReady, PlayState play, Availability avail)
        {
            if (!taken) return RoomSeatBadge.None;
            if (IsInMatch(play)) return RoomSeatBadge.Playing;
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
