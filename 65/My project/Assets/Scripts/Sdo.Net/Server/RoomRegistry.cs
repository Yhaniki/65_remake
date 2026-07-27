using System.Collections.Generic;

namespace Sdo.Net.Server
{
    /// <summary>離開房間的後果 —— Hub 要照這個發訊息。</summary>
    public struct LeaveResult
    {
        /// <summary>離開的是哪間房(null = 這個人本來就不在任何房裡)。</summary>
        public NetRoom Room;

        /// <summary>房間關掉了(座位全空)。</summary>
        public bool RoomClosed;

        /// <summary>
        /// 因為關房而被清出去的其他人 —— 他們要收到 <c>kicked{roomClosed}</c>。
        ///
        /// 正常路徑下**這個陣列是空的**:關房的條件是「一個人都不剩」,所以沒有人需要被清。
        /// 它存在是為了兩件事 —— (a) 防禦性:萬一狀態不同步,關房時還有人在,那些人要被通知;
        /// (b) 將來的「server 關機 / 房間閒置太久」那類強制關房路徑。
        /// </summary>
        public int[] EvictedUserIds;

        /// <summary>房主換人了(0 = 沒換)。</summary>
        public int NewHostUserId;

        public static LeaveResult None => new LeaveResult { EvictedUserIds = NetRoomTick.EmptyIds };
    }

    /// <summary>一間房在這一拍發生的事。</summary>
    public struct RoomTickResult
    {
        public NetRoom Room;
        public NetRoomTick Tick;
    }

    /// <summary>
    /// 所有房間的集合 + 「玩家在哪間房」的索引。
    ///
    /// **為什麼索引要集中在這裡**:一個玩家只能在一間房。散在各處維護的話,
    /// 漏更新一次就會出現「玩家離開了但 server 還以為他在房裡」的幽靈狀態 ——
    /// 那種 bug 的症狀是別人的頭貼永遠停在某個狀態,而且很難查。
    /// 所以所有進出房間的路徑(建房/加入/旁觀/離開/被踢)都必須經過這個類別。
    ///
    /// 同樣是純邏輯:零 IO、零 socket、零 UnityEngine。Hub 在單執行緒 actor loop 裡獨佔它。
    /// </summary>
    public sealed class RoomRegistry
    {
        private readonly Dictionary<int, NetRoom> _rooms = new Dictionary<int, NetRoom>();

        /// <summary>userId → 房號。<b>唯一</b>的「這個人在哪」的真相。</summary>
        private readonly Dictionary<int, int> _userRoom = new Dictionary<int, int>();

        private readonly RoomCodePool _codes;
        private readonly int _maxRooms;

        public RoomRegistry(int maxRooms = NetLimits.DefaultMaxRooms, int seed = 0)
        {
            _maxRooms = maxRooms > 0 ? maxRooms : NetLimits.DefaultMaxRooms;
            _codes = new RoomCodePool(seed);
        }

        public int RoomCount => _rooms.Count;

        public int MaxRooms => _maxRooms;

        /// <summary>所有房間(順序不保證穩定 —— 房間列表要自己排序)。</summary>
        public IEnumerable<NetRoom> Rooms => _rooms.Values;

        /// <summary>依房號找房。找不到回 null。</summary>
        public NetRoom Find(int code)
        {
            NetRoom room;
            return _rooms.TryGetValue(code, out room) ? room : null;
        }

        /// <summary>這個人在哪間房?不在任何房回 null。</summary>
        public NetRoom RoomOf(int userId)
        {
            int code;
            if (!_userRoom.TryGetValue(userId, out code)) return null;
            return Find(code);
        }

        /// <summary>這個人在房裡嗎?</summary>
        public bool IsInAnyRoom(int userId) => _userRoom.ContainsKey(userId);

        /// <summary>
        /// 建房。呼叫者成為房主。
        ///
        /// 已經在別間房的話會**先隱式離房** —— 這是實務上必要的:玩家在房間裡直接按
        /// 「建立房間」不該失敗,而是該離開現在這間再開新的。
        /// </summary>
        public NetRoomOp TryCreate(NetJoinUser host, string name, out NetRoom room, out LeaveResult left)
        {
            room = null;
            left = LeaveResult.None;

            if (_rooms.Count >= _maxRooms) return NetRoomOp.Full;

            if (IsInAnyRoom(host.UserId)) left = Leave(host.UserId);

            int code;
            if (!_codes.TryRent(out code)) return NetRoomOp.Full;   // 90000 間房都開滿了

            room = new NetRoom(code, host, name);
            _rooms[code] = room;
            _userRoom[host.UserId] = code;
            return NetRoomOp.Ok;
        }

        /// <summary>
        /// 加入房間。已經在別間房會先隱式離房。
        /// 房號不存在回 <see cref="NetRoomOp.NotInRoom"/>(Hub 轉成 <c>joinResult{notFound}</c>)。
        /// </summary>
        public NetRoomOp TryJoin(int code, NetJoinUser user, out NetRoom room, out int seat, out LeaveResult left)
        {
            seat = -1;
            left = LeaveResult.None;
            room = Find(code);
            if (room == null) return NetRoomOp.NotInRoom;

            // 已經在這間房 → 不用做事。
            if (room.State.Contains(user.UserId)) return NetRoomOp.BadState;

            if (IsInAnyRoom(user.UserId))
            {
                left = Leave(user.UserId);
                // 隱式離房可能把目標房關掉了(如果他本來就是那間房唯一的人)——
                // 重新查一次,別用已經失效的參照。
                room = Find(code);
                if (room == null) return NetRoomOp.NotInRoom;
            }

            var op = room.TryJoin(user, out seat);
            if (op == NetRoomOp.Ok) _userRoom[user.UserId] = code;
            return op;
        }

        /// <summary>以旁觀身分加入(或把座位換成旁觀)。</summary>
        public NetRoomOp TrySpectate(int code, NetJoinUser user, out NetRoom room, out LeaveResult left)
        {
            left = LeaveResult.None;
            room = Find(code);
            if (room == null) return NetRoomOp.NotInRoom;

            // 在別間房 → 先離開那間。
            if (IsInAnyRoom(user.UserId) && RoomOf(user.UserId) != room)
            {
                left = Leave(user.UserId);
                room = Find(code);
                if (room == null) return NetRoomOp.NotInRoom;
            }

            var op = room.TrySpectate(user);
            if (op == NetRoomOp.Ok) _userRoom[user.UserId] = code;
            return op;
        }

        /// <summary>旁觀者搶回座位。</summary>
        public NetRoomOp TryUnspectate(NetJoinUser user, out NetRoom room, out int seat)
        {
            seat = -1;
            room = RoomOf(user.UserId);
            if (room == null) return NetRoomOp.NotInRoom;
            return room.TryUnspectate(user, out seat);
        }

        /// <summary>
        /// 離開房間。**斷線也走這條**(R6:斷線 == leaveRoom,idempotent)。
        ///
        /// **只有「一個人都不剩」時才關房並回收房號** —— 旁觀者算人,所以
        /// 「六個座位全空但還有旁觀者」的房間會繼續存在(只是沒有房主)。
        /// 房主離開時若還有座位玩家就自動轉移(<see cref="LeaveResult.NewHostUserId"/>)。
        /// </summary>
        public LeaveResult Leave(int userId)
        {
            var result = LeaveResult.None;

            int code;
            if (!_userRoom.TryGetValue(userId, out code)) return result;

            var room = Find(code);
            _userRoom.Remove(userId);

            if (room == null) return result;   // 索引與房間集合不同步(理論上不該發生)

            result.Room = room;
            int hostBefore = room.HostUserId;

            bool shouldClose = room.Leave(userId);

            if (shouldClose)
            {
                result.RoomClosed = true;
                result.EvictedUserIds = CloseRoom(room);
            }
            else if (room.HostUserId != hostBefore)
            {
                result.NewHostUserId = room.HostUserId;
            }

            return result;
        }

        /// <summary>
        /// 踢人(host only)。權限檢查在 <see cref="NetRoom.KickUser"/>,這裡負責維護索引。
        /// </summary>
        public NetRoomOp KickUser(int actorId, int targetId, out NetRoom room, out LeaveResult left)
        {
            left = LeaveResult.None;
            room = RoomOf(actorId);
            if (room == null) return NetRoomOp.NotInRoom;

            // 先問權限(不改狀態),過了才真的踢 —— 否則非 host 的請求會先把索引改掉。
            if (!room.State.IsHost(actorId)) return NetRoomOp.NotHost;
            if (targetId == actorId) return NetRoomOp.BadSeat;
            if (!room.State.Contains(targetId)) return NetRoomOp.NotInRoom;

            left = Leave(targetId);
            return NetRoomOp.Ok;
        }

        /// <summary>
        /// 關閉座位(host only)。會把該座位的人踢掉並維護索引。
        /// </summary>
        public NetRoomOp SetSeatClosed(int actorId, int seat, bool closed, out NetRoom room, out int kickedUserId)
        {
            kickedUserId = 0;
            room = RoomOf(actorId);
            if (room == null) return NetRoomOp.NotInRoom;

            var op = room.SetSeatClosed(actorId, seat, closed, out kickedUserId);
            if (op == NetRoomOp.Ok && kickedUserId != 0) _userRoom.Remove(kickedUserId);
            return op;
        }

        /// <summary>
        /// 改房間設定(host only)。<c>lookerCount</c> 縮小可能踢掉旁觀者,這裡維護索引。
        /// </summary>
        public NetRoomOp SetRoomSettings(int actorId, object patchNode, out NetRoom room, out int[] kickedSpectators)
        {
            kickedSpectators = NetRoomTick.EmptyIds;
            room = RoomOf(actorId);
            if (room == null) return NetRoomOp.NotInRoom;

            var op = room.SetRoomSettings(actorId, patchNode, out kickedSpectators);
            if (op == NetRoomOp.Ok)
                for (int i = 0; i < kickedSpectators.Length; i++) _userRoom.Remove(kickedSpectators[i]);
            return op;
        }

        /// <summary>
        /// 推進所有房間的狀態機。Hub 定期呼叫(至少每秒一次,讓載入逾時能準時觸發)。
        /// 只回傳「真的有事發生」的房間 —— 一般情況下是空清單。
        /// </summary>
        public List<RoomTickResult> TickAll(long nowMs)
        {
            var results = new List<RoomTickResult>();

            // 先收集房間清單再走訪 —— Tick 不會改變集合,但關房路徑會,保守一點。
            var rooms = new List<NetRoom>(_rooms.Values);
            for (int i = 0; i < rooms.Count; i++)
            {
                var tick = rooms[i].Tick(nowMs);
                if (tick.Changed || tick.GameplayStarted || tick.ResultsReady
                    || tick.MatchAborted || tick.LoadTimedOutUserIds.Length > 0)
                {
                    results.Add(new RoomTickResult { Room = rooms[i], Tick = tick });
                }
            }
            return results;
        }

        /// <summary>
        /// 房間列表(給 <c>roomList</c>)。只列還開著的房,依房號排序讓輸出穩定。
        /// </summary>
        public List<NetRoom> ListOpenRooms()
        {
            var list = new List<NetRoom>();
            foreach (var r in _rooms.Values)
                if (r.Status != RoomStatus.Closed) list.Add(r);
            list.Sort(CompareByCode);
            return list;
        }

        private static int CompareByCode(NetRoom a, NetRoom b) => a.Code.CompareTo(b.Code);

        /// <summary>拆房:清索引、回收房號。回傳還留在房裡的人(要收 kicked{roomClosed})。</summary>
        private int[] CloseRoom(NetRoom room)
        {
            var evicted = new List<int>();

            // 座位理論上已經空了(那是關房的條件),但保守起見兩邊都掃。
            for (int i = 0; i < room.State.Seats.Length; i++)
            {
                var s = room.State.Seats[i];
                if (s.IsTaken) { evicted.Add(s.UserId); _userRoom.Remove(s.UserId); }
            }
            var specs = room.State.Spectators;
            if (specs != null)
                for (int i = 0; i < specs.Length; i++)
                {
                    evicted.Add(specs[i].UserId);
                    _userRoom.Remove(specs[i].UserId);
                }

            _rooms.Remove(room.Code);
            _codes.Return(room.Code);
            return evicted.ToArray();
        }
    }
}
