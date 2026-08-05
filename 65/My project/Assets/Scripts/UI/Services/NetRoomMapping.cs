using System.Collections.Generic;
using Sdo.Net;

// ⚠️ RoomStatus 這個名字**兩邊都有,而且語意不同** —— 協定那邊是四種
// (open / waitingForLoad / playing / closed),UI 那邊只有兩種(Waiting / InGame)。
// 不開 alias 的話,在 Sdo.UI.Services 這個 namespace 裡寫 RoomStatus 會解析到 UI 那個,
// 於是 `snap.Status == RoomStatus.Open` 變成編譯錯誤(而且錯誤訊息不會告訴你原因)。
using NetRoomStatus = Sdo.Net.RoomStatus;
using UiRoomStatus = Sdo.UI.Services.RoomStatus;

namespace Sdo.UI.Services
{
    /// <summary>
    /// 把 server 的 <see cref="NetRoomSnapshot"/> 對映成 UI 既有的 <see cref="RoomInfo"/> / <see cref="SeatInfo"/>。
    ///
    /// **為什麼要有這一層而不是讓 UI 直接讀 snapshot**:整個前端已經圍繞 <c>RoomInfo</c> 寫好了
    /// (`RoomScreen.Render` 讀 `Ctx.Rooms.CurrentRoom`、聊天讀 `SeatInfo.Player.DisplayName`…)。
    /// 換成 adapter 的話那些畫面**一行都不用改**就能顯示線上資料;讓每個畫面自己分辨
    /// 「我現在是離線還是線上」則是把同一個判斷散到幾十處,漏一處就是一個模式下才會出現的 bug。
    ///
    /// 純函式、零 UnityEngine → 可以直接單元測試。這很重要:對映錯了的症狀是
    /// 「頭貼顯示錯的人」或「房主徽章畫在錯的格子」,那種東西用眼睛看很難確定,
    /// 而且要開兩個 client 才能重現。
    /// </summary>
    public static class NetRoomMapping
    {
        /// <summary>
        /// snapshot → RoomInfo。
        ///
        /// 不需要傳「本機是誰」—— 房主判定一律走 <see cref="RoomInfo.HostUserId"/> 與
        /// 座位的 <see cref="SeatInfo.UserId"/> 比對,所以這個對映對每個 client 都產生同一份結果。
        /// </summary>
        public static RoomInfo ToRoomInfo(NetRoomSnapshot snap)
        {
            var info = new RoomInfo();
            if (snap == null) return info;

            info.Id = snap.Code;
            info.Seq = snap.Seq;
            info.Name = snap.Name ?? "";
            info.Rev = snap.Rev;
            info.HostUserId = snap.HostUserId;
            info.Capacity = snap.Capacity;

            // GameMode:協定用 int(0=自由 1=普通 2=ShowTime),UI 的 enum 只有 Free/Normal。
            // ShowTime 在 UI enum 裡沒有對應值 —— 當成 Normal(它是「普通模式 + 氣條」的變體)。
            info.Mode = snap.Settings != null && snap.Settings.GameMode == 0 ? GameMode.Free : GameMode.Normal;

            // 房間狀態:協定有四種,UI 的 enum 只有 Waiting/InGame。
            info.Status = snap.Status == NetRoomStatus.Open ? UiRoomStatus.Waiting : UiRoomStatus.InGame;

            info.SongTitle = snap.Song != null ? snap.Song.Title : null;

            for (int i = 0; i < snap.Seats.Length; i++)
                info.Seats.Add(ToSeatInfo(snap.Seats[i], snap.HostUserId));

            // 房主名字:給 RoomLabels.DisplayName 用(空房名時顯示「房主名 + 的舞蹈室」)。
            var hostSeat = snap.SeatOf(snap.HostUserId);
            info.HostName = hostSeat != null ? hostSeat.Name : "";

            if (snap.Spectators != null)
                for (int i = 0; i < snap.Spectators.Length; i++)
                {
                    var sp = snap.Spectators[i];
                    info.Spectators.Add(new SpectatorInfo
                    {
                        UserId = sp.UserId,
                        DisplayName = sp.Name ?? "",
                        Level = sp.Level,
                        Avail = sp.Avail,
                        // 🔴 家族與性別要一起帶過來:右鍵旁觀席上的人也開得了玩家資訊視窗
                        //    (見 RoomPickTarget),而那個視窗要家族名 + 性別(畫他的 3D 角色)。
                        //    漏掉的話症狀是「同一個人坐著看得到家族、站到旁觀席就沒了」與「男的畫成女的」。
                        Guild = sp.Guild ?? "",
                        Gender = sp.Look != null ? sp.Look.Gender : 0,
                    });
                }

            return info;
        }

        /// <summary>單一座位的對映。</summary>
        public static SeatInfo ToSeatInfo(NetSeat seat, int hostUserId)
        {
            var s = new SeatInfo();
            if (seat == null) return s;

            s.IsClosed = seat.IsClosed;
            s.UserId = seat.UserId;

            if (!seat.IsTaken)
            {
                // 空位/關閉的座位:Player 留 null(IsEmpty 為 true),其餘中性值。
                s.Avail = Availability.Unknown;
                s.PlayState = PlayState.Idle;
                return s;
            }

            // 🔴 Guild 一定要帶過來 —— 玩家資訊視窗要顯示對方的家族,而這是唯一拿得到它的地方
            //    (以前這裡把 seat.Guild 丟掉了,於是視窗上的家族欄永遠是空的)。
            s.Player = new PlayerProfile(
                seat.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                seat.Name ?? "",
                seat.Level,
                seat.Guild ?? "");

            // 🔴 房主判定跟 hostUserId 比,不是看座位索引。
            s.IsHost = hostUserId != 0 && seat.UserId == hostUserId;

            // 房主沒有「準備」這個狀態(它的頭貼畫 host 徽章)—— 對 UI 而言它永遠是「可以開始」的,
            // 所以這裡把 IsReady 填成 true,讓既有的「全員準備了嗎」那類顯示邏輯不用特例。
            // 真正的權威判定在 server(NetRoom.IsClearedToStart)。
            s.IsReady = s.IsHost || seat.Ready;

            s.Team = seat.Team;
            s.Avail = seat.Avail;
            s.AvailProgress = seat.AvailProgress;
            s.PlayState = seat.PlayState;
            return s;
        }

        /// <summary>
        /// 房間列表的一列 → <see cref="RoomInfo"/>(只有列表要顯示的欄位;座位是空的)。
        /// 給「加入房間」畫面用。
        /// </summary>
        public static RoomInfo ToRoomInfo(NetRoomListEntry e)
        {
            var info = new RoomInfo();
            info.Id = e.Code;
            info.Seq = e.Seq;   // 大廳房卡顯示的門牌(3 位數);Id 是 5 位數的加入鑰匙,兩者不同
            info.Name = e.Name ?? "";
            info.HostName = e.HostName ?? "";
            info.Capacity = e.Capacity;
            info.Mode = e.Mode == 0 ? GameMode.Free : GameMode.Normal;
            info.Status = e.Status == NetRoomStatus.Open ? UiRoomStatus.Waiting : UiRoomStatus.InGame;
            info.SongTitle = string.IsNullOrEmpty(e.SongTitle) ? null : e.SongTitle;
            info.SongLevel = e.SongLevel;   // 0 = 不知道(見 NetRoomListEntry.SongLevel)
            info.SeatGenders = e.Genders;   // 房卡那排愛心的顏色(舊版 server 不送 → null,呼叫端退回一律粉紅)

            // 列表沒有逐座位的完整資料 —— 用「有幾個人」補出對應數量的佔位座位,
            // 讓既有的 RoomInfo.Count / IsFull 仍然算得出正確的數字。
            //
            // 🔴 名字與等級**能填就填**(<see cref="NetRoomListEntry.Members"/>):「房間信息」那個框是在
            //    大廳右鍵房卡開的,人還沒進房 → 沒有 roomSnapshot 可讀,這是它唯一的資料來源。
            //    舊版 server 不送 members → 留空名字與 0 等,那個框的列就只有格子沒有字
            //    (不要湊假資料 —— 寫「1 等」比留白更難發現是錯的)。
            //    Id 一律是 "?":列表不帶 userId,而 PlayerProfile.Id 在別處是拿來認人的,
            //    給一個明顯的假值比給一個看起來像真的數字安全。
            var mem = e.Members;
            for (int i = 0; i < e.Capacity; i++)
            {
                var s = new SeatInfo();
                if (i < e.Count)
                    s.Player = mem != null && i < mem.Length
                        ? new PlayerProfile("?", mem[i].Name ?? "", mem[i].Level)
                        : new PlayerProfile("?", "", 0);
                info.Seats.Add(s);
            }
            return info;
        }

        public static List<RoomInfo> ToRoomInfos(NetRoomListEntry[] entries)
        {
            var list = new List<RoomInfo>();
            if (entries == null) return list;
            for (int i = 0; i < entries.Length; i++) list.Add(ToRoomInfo(entries[i]));
            return list;
        }
    }
}
