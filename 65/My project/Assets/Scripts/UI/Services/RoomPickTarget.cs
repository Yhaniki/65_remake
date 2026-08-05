namespace Sdo.UI.Services
{
    /// <summary>右鍵房間裡的一個 3D 角色,挑到的是誰。</summary>
    public enum RoomPickKind
    {
        /// <summary>誰也不是(那個 userId 已經不在房裡了 —— 剛離開,或快照還沒追上)。</summary>
        None,
        /// <summary>坐在座位上的人 → 座位選單(含房主的踢人/轉房主/開關位置)。</summary>
        Seat,
        /// <summary>站在旁觀席的人 → 只有社交選單(旁觀者沒有座位,管理項一項都不適用)。</summary>
        Spectator,
    }

    /// <summary>挑選結果。<see cref="Index"/> 的意思由 <see cref="Kind"/> 決定。</summary>
    public struct RoomPickResult
    {
        public RoomPickKind Kind;

        /// <summary><c>Seat</c> → 第幾格座位;<c>Spectator</c> → 旁觀名單的第幾個(-1 = 本機自己但名單上還沒有他)。</summary>
        public int Index;

        /// <summary>挑到的是我自己 → 「玩家信息」開的是本機那份,而且沒有私聊/加好友/踢自己。</summary>
        public bool IsSelf;
    }

    /// <summary>
    /// 「右鍵房間 3D 場景挑到的那個人,要開哪一種選單」的純規則。
    ///
    /// 🔴 **旁觀者也是人。** 這一層原本不存在:<c>RoomScreen</c> 直接把挑到的 userId 換算成座位,
    /// 查不到就什麼都不做 —— 於是站在旁觀席的十個人(以及**站上旁觀席的自己**)右鍵完全沒有反應,
    /// 連「玩家信息」都看不了。旁觀者沒有座位是真的,但需要座位的只有房主那組管理項;
    /// 社交那組(玩家信息 / 私聊 / 加為好友)只需要「一個人」。
    ///
    /// 🔴 本機自己在 3D 挑選裡的 userId 是 <b>0</b>(見 <c>RoomScene3D.TryPickAvatar</c>),
    /// 不是 <paramref name="localUserId"/> —— 離線根本沒有 userId,所以本機一律走「0」這條路,
    /// 先看座位、再看旁觀名單。
    ///
    /// 純函式、零 UnityEngine → 可以直接單元測試(見 RoomPickTargetTests)。這很重要:
    /// 「右鍵沒反應」這種症狀在畫面上看不出是挑選失敗還是選單規則判空,而且要開兩台 client 才重現得出來。
    /// </summary>
    public static class RoomPickTarget
    {
        /// <summary>
        /// 挑到的 3D 角色 → 選單目標。
        /// </summary>
        /// <param name="room">目前的房間快照(null → <c>None</c>)。</param>
        /// <param name="pickedUserId">挑中的人的 server userId;<b>0 = 本機自己</b>。</param>
        /// <param name="localUserId">本機的 server userId(離線 0)。</param>
        /// <param name="localSeat">本機坐在第幾格(不在座位上 = -1,見 <see cref="RoomLocalSeat.IndexOf"/>)。</param>
        /// <param name="localSpectating">本機現在是旁觀者嗎(server 快照為準)。離線恆 false。</param>
        public static RoomPickResult Resolve(RoomInfo room, int pickedUserId, int localUserId, int localSeat,
                                             bool localSpectating)
        {
            var none = new RoomPickResult { Kind = RoomPickKind.None, Index = -1 };
            if (room == null) return none;

            if (pickedUserId == 0)
            {
                // 本機自己。座位優先 —— 離線只有這條路(沒有 userId,也永遠沒有旁觀名單)。
                if (localSeat >= 0)
                    return new RoomPickResult { Kind = RoomPickKind.Seat, Index = localSeat, IsSelf = true };

                int mine = SpectatorIndexOf(room, localUserId);
                // 🔴 名單上找不到自己也照樣算旁觀者(用 localSpectating 兜底):快照是**每幀重來**的,
                //    剛按下「旁觀」到 server 回快照之間人已經站到旁觀席上了,那一段時間右鍵自己不該啞掉。
                if (mine >= 0 || localSpectating)
                    return new RoomPickResult { Kind = RoomPickKind.Spectator, Index = mine, IsSelf = true };
                return none;
            }

            int seat = room.SeatIndexOfUser(pickedUserId);
            bool isSelf = localUserId != 0 && pickedUserId == localUserId;
            if (seat >= 0)
                return new RoomPickResult { Kind = RoomPickKind.Seat, Index = seat, IsSelf = isSelf };

            int si = SpectatorIndexOf(room, pickedUserId);
            if (si >= 0)
                return new RoomPickResult { Kind = RoomPickKind.Spectator, Index = si, IsSelf = isSelf };

            return none;
        }

        /// <summary>這個人是旁觀名單的第幾個?不在名單上回 -1(userId 0 一律 -1 —— 那是「不知道」不是一個人)。</summary>
        public static int SpectatorIndexOf(RoomInfo room, int userId)
        {
            if (room == null || room.Spectators == null || userId == 0) return -1;
            for (int i = 0; i < room.Spectators.Count; i++)
                if (room.Spectators[i] != null && room.Spectators[i].UserId == userId) return i;
            return -1;
        }
    }
}
