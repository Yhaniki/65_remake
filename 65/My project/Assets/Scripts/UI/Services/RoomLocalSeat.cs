namespace Sdo.UI.Services
{
    /// <summary>
    /// 「這一份房間快照裡，**本機這個人**坐在哪一格、按過準備了沒」。
    ///
    /// 🔴 為什麼不能直接拿 <c>SeatInfo.Player.Id</c> 跟 <c>GameSession.LocalPlayerId</c> 比：
    /// 線上的座位是 <see cref="NetRoomMapping.ToSeatInfo"/> 從 server 快照對映來的，
    /// <c>Player.Id</c> 填的是 **server 配發的 userId**(<c>seat.UserId.ToString()</c>)，
    /// 而 <c>LocalPlayerId</c> 是本機存檔的 profile id(PROFILE/00000001)。兩個是不同的號碼系統，
    /// 在線上**永遠比不中** → 「我已經準備了嗎」永遠讀成 false：右下角那顆球不會翻成「取消」，
    /// 而且再按一次送出去的還是 <c>setReady(true)</c>(server 原樣照收 → 取消不掉)。
    /// 認人一律走 userId；離線沒有 userId(0) 才退回 profile id 比對。
    ///
    /// 房主的 <see cref="IsReady"/> 恆 false：官方房主沒有「準備」這個狀態(server 的
    /// <c>NetRoom.SetReady</c> 對房主直接回 <c>BadState</c>)，而 <see cref="NetRoomMapping"/> 為了讓
    /// 「全員準備了嗎」少一個特例，把房主座位的 <c>IsReady</c> 填成 true —— 直接讀它會讓房主那格
    /// 冒出「取消」鈕。
    ///
    /// 純函式、零 UnityEngine → 可以直接單元測試(見 RoomLocalSeatTests)。這很重要：對映錯了的症狀是
    /// 「按了準備但按鈕沒變、也取消不掉」，那要開兩個 client 才重現得出來。
    /// </summary>
    public static class RoomLocalSeat
    {
        /// <summary>本機玩家坐在第幾格?找不到(旁觀/還沒進座位/不在房裡)回 -1。</summary>
        public static int IndexOf(RoomInfo room, int netUserId, string localPlayerId)
        {
            if (room == null || room.Seats == null) return -1;
            // 連線:用 server 配的 userId 比對(名字會重複,id 不會)。
            if (netUserId != 0) return room.SeatIndexOfUser(netUserId);
            if (string.IsNullOrEmpty(localPlayerId)) return -1;
            for (int i = 0; i < room.Seats.Count; i++)
            {
                var s = room.Seats[i];
                if (s != null && !s.IsEmpty && s.Player.Id == localPlayerId) return i;
            }
            return -1;
        }

        /// <summary>本機玩家的座位;不在座位上回 null。</summary>
        public static SeatInfo Of(RoomInfo room, int netUserId, string localPlayerId)
        {
            int i = IndexOf(room, netUserId, localPlayerId);
            return i >= 0 && i < room.Seats.Count ? room.Seats[i] : null;
        }

        /// <summary>
        /// 本機玩家按過「準備」了嗎(房主恆 false —— 見類別註解)。
        /// 右下角那顆球畫「準備」還是「取消」、按下去送 <c>setReady(true/false)</c>，都只看這一個值。
        /// </summary>
        public static bool IsReady(RoomInfo room, int netUserId, string localPlayerId)
        {
            var seat = Of(room, netUserId, localPlayerId);
            if (seat == null) return false;
            // 房主判定跟 HostUserId 比(轉移房主時只換那個值、不搬座位);離線沒有 userId → 看座位旗標。
            bool isHost = seat.UserId != 0 ? room.IsHostUser(seat.UserId) : seat.IsHost;
            return !isHost && seat.IsReady;
        }
    }
}
