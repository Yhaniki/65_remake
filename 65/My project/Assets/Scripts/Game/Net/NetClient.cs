using System;
using System.Collections.Generic;
using Sdo.Net;
using UnityEngine;

namespace Sdo.Game.Net
{
    /// <summary>
    /// 遊戲端的房間狀態維護層 —— 對應 osu! 的 <c>MultiplayerClient</c>。
    ///
    /// **它不知道 socket 存在。** 三層切分:
    ///   <see cref="NetConnection"/> 只搬位元組 → 這一層維護狀態並發事件 →
    ///   <c>OnlineRoomService</c> 把它接到 UI 既有的 <c>IRoomService</c> 介面。
    ///
    /// 這個切分是照抄 osu 最值得抄的工程決策:因為狀態層不碰 socket,
    /// 同一份房間規則(<c>Sdo.Net.Server.NetRoom</c>)可以被「同 process 的假伺服器」驅動,
    /// 於是 UI 開發完全不需要真 server,而假伺服器的行為與線上逐位元組相同。
    ///
    /// 狀態的唯一真相是 server 推來的 <c>roomState</c> 快照 —— 這一層**絕不**自己推測狀態
    /// (例如「我按了準備所以我現在是準備好的」)。樂觀更新是 divergence bug 的溫床:
    /// 一旦本機猜的與 server 的不一致,畫面就會停在一個永遠不會被修正的錯誤狀態。
    /// </summary>
    public sealed class NetClient
    {
        private readonly NetConnection _link = new NetConnection();
        private readonly Dictionary<int, Action<object>> _pending = new Dictionary<int, Action<object>>();
        private int _nextRq = 1;
        private long _lastPingSentMs;
        private int _lastSeenRev;
        private bool _helloSent;

        // ---- 狀態 ----

        public NetLinkState LinkState => _link.State;
        public string LastError => _link.LastError;
        public bool IsConnected => _link.IsConnected && UserId != 0;

        /// <summary>server 配的使用者 id。0 = 還沒握手完成。</summary>
        public int UserId { get; private set; }

        public string SessionKey { get; private set; } = "";

        /// <summary>目前所在房間的快照。null = 不在任何房間。**只讀** —— 唯一作者是 server。</summary>
        public NetRoomSnapshot Room { get; private set; }

        public bool InRoom => Room != null;

        /// <summary>本機是這間房的房主嗎?</summary>
        public bool IsHost => Room != null && Room.IsHost(UserId);

        /// <summary>本機的座位(不在座位上 → null)。</summary>
        public NetSeat LocalSeat => Room != null ? Room.SeatOf(UserId) : null;

        /// <summary>本機是旁觀者嗎?</summary>
        public bool IsSpectating => Room != null && Room.SpectatorIndexOf(UserId) >= 0;

        /// <summary>最近一次 ping 的往返時間(ms)。-1 = 還沒量到。</summary>
        public float RttMs { get; private set; } = -1f;

        /// <summary>診斷資訊(除錯面板用)。</summary>
        public string Diagnostics =>
            string.Format("link={0} user={1} room={2} rev={3} rtt={4:0}ms sent={5} recv={6} pending={7}",
                _link.State, UserId, Room != null ? Room.Code.ToString() : "-",
                _lastSeenRev, RttMs, _link.SentCount, _link.RecvCount, _link.PendingInbound);

        // ---- 事件(全部在主執行緒上觸發 —— Pump 裡) ----

        /// <summary>房間狀態變了(加入/離開/準備/選歌/座位…都會來一次)。</summary>
        public event Action<NetRoomSnapshot> RoomUpdated;

        /// <summary>離開了房間(自己離開、被踢、房間關閉)。參數是原因。</summary>
        public event Action<string> RoomLeft;

        /// <summary>被踢出房間。參數是原因(host / seatClosed / roomClosed)。</summary>
        public event Action<string> Kicked;

        /// <summary>server 回了一個錯誤。參數:code, msg。</summary>
        public event Action<string, string> ErrorReceived;

        /// <summary>連線掛了(含握手失敗)。參數是人看得懂的原因。</summary>
        public event Action<string> Disconnected;

        /// <summary>要開場了(參與者 + 有歌的旁觀者收到)。</summary>
        public event Action<NetMatchStart> MatchStarting;

        /// <summary>所有人都載入完成,現在開始跑。</summary>
        public event Action<long> GameplayStarted;

        /// <summary>本場被中止(載入逾時 / 沒有參與者)。參數:matchId, reason。</summary>
        public event Action<long, string> GameplayAborted;

        /// <summary>結算資料到了。</summary>
        public event Action<NetResultRow[]> ResultsReady;

        /// <summary>遊玩中的分數流(房內所有人的最新一筆)。</summary>
        public event Action<NetFrameRow[]> FramesReceived;

        /// <summary>聊天訊息。</summary>
        public event Action<NetChatMessage> ChatReceived;

        // ---- 連線 ----

        /// <summary>
        /// 開始連線並握手。非阻塞 —— 呼叫端輪詢 <see cref="LinkState"/> / <see cref="IsConnected"/>。
        /// </summary>
        public void Connect(string host, int port, string password, NetHelloIdentity identity)
        {
            _identity = identity;
            _password = password ?? "";
            _helloSent = false;
            UserId = 0;
            Room = null;
            _lastSeenRev = 0;
            _link.BeginConnect(host, port);
        }

        public void Disconnect(string reason = "userQuit")
        {
            _link.Close(reason);
            UserId = 0;
            Room = null;
        }

        private NetHelloIdentity _identity;
        private string _password = "";

        // ---- 每幀 pump ----

        /// <summary>
        /// 由主執行緒每幀呼叫一次(<c>FrontendApp.Update</c>)。
        /// 處理收到的訊息、送心跳、偵測斷線。**所有事件都從這裡觸發**,
        /// 所以 UI 端不需要考慮執行緒安全。
        /// </summary>
        public void Pump()
        {
            // 剛連上 → 送 hello。
            if (_link.IsConnected && !_helloSent)
            {
                _helloSent = true;
                SendHello();
            }

            // 收訊息
            byte kind;
            byte[] payload;
            int guard = 0;
            while (_link.Poll(out kind, out payload))
            {
                // 一幀處理上限:避免大量積壓的訊息把一幀拖成幾百 ms(那會看起來像卡頓)。
                if (++guard > 256) break;
                if (kind != NetLimits.FrameKindJson) continue;   // 檔案 chunk 在 M5 才處理

                object node;
                string type;
                if (!NetJson.TryParseMessage(payload, 0, payload.Length, out node, out type)) continue;
                Handle(type, node);
            }

            // 心跳
            if (IsConnected)
            {
                long now = NowMs();
                if (now - _lastPingSentMs >= NetLimits.PingIntervalMs)
                {
                    _lastPingSentMs = now;
                    _link.Send(JObj.New().Str(NetProto.FieldType, NetProto.Ping).Num("t0", now));
                }
            }

            // 斷線偵測
            if ((_link.State == NetLinkState.Closed || _link.State == NetLinkState.Failed) && _reportedDown == false)
            {
                _reportedDown = true;
                var wasInRoom = Room != null;
                Room = null;
                UserId = 0;
                if (wasInRoom) Raise(RoomLeft, "disconnected");
                Raise(Disconnected, string.IsNullOrEmpty(_link.LastError) ? "連線中斷" : _link.LastError);
            }
        }

        private bool _reportedDown;

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private void SendHello()
        {
            var look = JObj.New()
                .Int("gender", _identity.Gender)
                .Int("bodyIndex", _identity.BodyIndex);
            var parts = JArr.New();
            if (_identity.AvatarParts != null)
                for (int i = 0; i < _identity.AvatarParts.Length; i++) parts.Add(_identity.AvatarParts[i]);
            look.Put("parts", parts);

            var hello = JObj.New()
                .Str(NetProto.FieldType, NetProto.Hello)
                .Int(NetProto.FieldRequest, NextRq(null))
                .Int("proto", NetProto.Version)
                .Str("role", NetProto.RoleControl)
                .Str("playerId", _identity.PlayerId ?? "")
                .Str("name", _identity.Name ?? "")
                .Str("guild", _identity.Guild ?? "")
                .Int("level", _identity.Level)
                .Put("look", look);

            if (!string.IsNullOrEmpty(_password)) hello.Str("password", _password);
            _link.Send(hello);
        }

        // ---- 訊息處理 ----

        private void Handle(string type, object node)
        {
            switch (type)
            {
                case NetProto.Welcome:
                    UserId = NetJson.Int(node, "userId");
                    SessionKey = NetJson.Str(node, "sessionKey");
                    Debug.Log("[net] 已連上 server,userId=" + UserId);
                    break;

                case NetProto.Pong:
                    RttMs = (float)(NowMs() - NetJson.Num(node, "t0"));
                    break;

                case NetProto.Bye:
                    {
                        string reason = NetJson.Str(node, "reason");
                        Debug.LogWarning("[net] server 要求斷線:" + reason);
                        _link.Close("bye:" + reason);
                        break;
                    }

                case NetProto.RoomState:
                    ApplyRoomState(node);
                    break;

                case NetProto.JoinResult:
                    CompletePending(node);
                    break;

                case NetProto.RoomListResult:
                    CompletePending(node);
                    break;

                case NetProto.Kicked:
                    {
                        string reason = NetJson.Str(node, "reason");
                        Room = null;
                        _lastSeenRev = 0;
                        Raise(Kicked, reason);
                        Raise(RoomLeft, "kicked:" + reason);
                        break;
                    }

                case NetProto.Error:
                    {
                        string code = NetJson.Str(node, "code");
                        string msg = NetJson.Str(node, "msg");
                        // 有帶 rq 的錯誤先交給發起那個請求的 callback。
                        if (!CompletePending(node))
                        {
                            if (ErrorReceived != null) ErrorReceived(code, msg);
                        }
                        break;
                    }

                case NetProto.MatchStarting:
                    Raise(MatchStarting, NetMatchStart.Decode(node));
                    break;

                case NetProto.GameplayStarted:
                    Raise(GameplayStarted, NetJson.Long(node, "matchId"));
                    break;

                case NetProto.GameplayAborted:
                    if (GameplayAborted != null)
                        GameplayAborted(NetJson.Long(node, "matchId"), NetJson.Str(node, "reason"));
                    break;

                case NetProto.ResultsReady:
                    Raise(ResultsReady, NetResultRow.DecodeAll(NetJson.Arr(node, "rows")));
                    break;

                case NetProto.Frames:
                    Raise(FramesReceived, NetFrameRow.DecodeAll(NetJson.Arr(node, "f")));
                    break;

                case NetProto.ChatMsg:
                    Raise(ChatReceived, NetChatMessage.Decode(node));
                    break;

                default:
                    // 不認得的訊息:可能是新版 server。忽略比斷線好。
                    break;
            }
        }

        private void ApplyRoomState(object node)
        {
            var snap = NetRoomSnapshot.Decode(node);

            // rev 單調遞增。丟掉過期的快照 —— TCP 有序所以正常不會發生,
            // 但 loopback 假伺服器與測試路徑需要這道保護。
            if (snap.Rev != 0 && snap.Rev <= _lastSeenRev) return;
            _lastSeenRev = snap.Rev;

            bool wasIn = Room != null;
            bool stillIn = snap.Contains(UserId);

            Room = stillIn ? snap : null;
            if (!stillIn)
            {
                _lastSeenRev = 0;
                if (wasIn) Raise(RoomLeft, "left");
                return;
            }

            Raise(RoomUpdated, snap);
        }

        // ---- request / response 配對 ----

        private int NextRq(Action<object> onReply)
        {
            int rq = _nextRq++;
            if (onReply != null) _pending[rq] = onReply;
            return rq;
        }

        /// <summary>把帶 rq 的回應交給發起者。回 true = 有人在等這個 rq。</summary>
        private bool CompletePending(object node)
        {
            int rq = NetJson.Int(node, NetProto.FieldRequest);
            if (rq == 0) return false;

            Action<object> cb;
            if (!_pending.TryGetValue(rq, out cb)) return false;
            _pending.Remove(rq);
            if (cb != null)
            {
                try { cb(node); }
                catch (Exception ex) { Debug.LogError("[net] 回應處理例外: " + ex); }
            }
            return true;
        }

        // ---- 房間操作 ----

        /// <summary>要一份房間列表。</summary>
        public void RequestRoomList(Action<NetRoomListEntry[]> onList)
        {
            Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.RoomList)
                .Int(NetProto.FieldRequest, NextRq(node =>
                {
                    if (onList != null) onList(NetRoomListEntry.DecodeAll(NetJson.Arr(node, "rooms")));
                })));
        }

        /// <summary>建房。<paramref name="onResult"/> 收到 (result, code)。</summary>
        public void CreateRoom(string name, Action<string, int> onResult)
        {
            Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.CreateRoom)
                .Int(NetProto.FieldRequest, NextRq(node => ReportJoin(node, onResult)))
                .Str("name", name ?? ""));
        }

        /// <summary>用房號加入。<paramref name="onResult"/> 收到 (result, code)。</summary>
        public void JoinRoom(int code, Action<string, int> onResult)
        {
            Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.JoinRoom)
                .Int(NetProto.FieldRequest, NextRq(node => ReportJoin(node, onResult)))
                .Int("code", code));
        }

        private static void ReportJoin(object node, Action<string, int> onResult)
        {
            if (onResult == null) return;
            // 可能收到 joinResult,也可能收到 error(例如房間滿了以外的失敗)。
            string t = NetJson.Str(node, NetProto.FieldType);
            if (t == NetProto.Error) { onResult(NetProto.JoinNotFound, 0); return; }
            onResult(NetJson.Str(node, "result"), NetJson.Int(node, "code"));
        }

        public void LeaveRoom()
        {
            Send(JObj.New().Str(NetProto.FieldType, NetProto.LeaveRoom));
            Room = null;
            _lastSeenRev = 0;
        }

        public void SetReady(bool ready)
            => Send(JObj.New().Str(NetProto.FieldType, NetProto.SetReady)
                .Int(NetProto.FieldRequest, NextRq(null)).Bool("ready", ready));

        public void SetOwnTeam(int team)
            => Send(JObj.New().Str(NetProto.FieldType, NetProto.SetOwnTeam)
                .Int(NetProto.FieldRequest, NextRq(null)).Int("team", team));

        public void SetRoomName(string name)
            => Send(JObj.New().Str(NetProto.FieldType, NetProto.SetRoomName)
                .Int(NetProto.FieldRequest, NextRq(null)).Str("name", name ?? ""));

        public void SetSong(NetSongRef song)
            => Send(JObj.New().Str(NetProto.FieldType, NetProto.SetSong)
                .Int(NetProto.FieldRequest, NextRq(null))
                .Put("song", song != null ? song.Encode() : null));

        public void SetRoomSettings(NetRoomSettings settings)
            => Send(JObj.New().Str(NetProto.FieldType, NetProto.SetRoomSettings)
                .Int(NetProto.FieldRequest, NextRq(null))
                .Put("settings", settings != null ? settings.Encode() : null));

        public void AssignTeams(TeamLayout layout)
            => Send(JObj.New().Str(NetProto.FieldType, NetProto.AssignTeams)
                .Int(NetProto.FieldRequest, NextRq(null))
                .Str("layout", TeamLayoutRules.ToWire(layout)));

        public void KickUser(int userId)
            => Send(JObj.New().Str(NetProto.FieldType, NetProto.KickUser)
                .Int(NetProto.FieldRequest, NextRq(null)).Int("userId", userId));

        public void SetSeatClosed(int seat, bool closed)
            => Send(JObj.New().Str(NetProto.FieldType, NetProto.SetSeatClosed)
                .Int(NetProto.FieldRequest, NextRq(null)).Int("seat", seat).Bool("closed", closed));

        public void TransferHost(int userId)
            => Send(JObj.New().Str(NetProto.FieldType, NetProto.TransferHost)
                .Int(NetProto.FieldRequest, NextRq(null)).Int("userId", userId));

        public void Spectate(int code = 0)
            => Send(JObj.New().Str(NetProto.FieldType, NetProto.Spectate)
                .Int(NetProto.FieldRequest, NextRq(null)).Int("code", code));

        public void StopSpectate()
            => Send(JObj.New().Str(NetProto.FieldType, NetProto.StopSpectate)
                .Int(NetProto.FieldRequest, NextRq(null)));

        /// <summary>上報「我有沒有這首歌」。</summary>
        public void SetAvailability(string packId, Availability avail, float progress = 0f)
            => Send(JObj.New().Str(NetProto.FieldType, NetProto.SetAvailability)
                .Str("packId", packId ?? "")
                .Str("state", NetState.ToWire(avail))
                .Num("progress", progress));

        /// <summary>房主按開始。<paramref name="force"/> = 連按兩下的強制開始。</summary>
        public void RequestStart(bool force, NetResolvedRound resolved)
            => Send(JObj.New().Str(NetProto.FieldType, NetProto.RequestStart)
                .Int(NetProto.FieldRequest, NextRq(null))
                .Bool("force", force)
                .Put("resolved", resolved != null ? resolved.Encode() : null));

        public void SetPlayState(PlayState state, long matchId)
            => Send(JObj.New().Str(NetProto.FieldType, NetProto.SetPlayState)
                .Int(NetProto.FieldRequest, NextRq(null))
                .Str("state", NetState.ToWire(state))
                .Long("matchId", matchId));

        /// <summary>
        /// 遊玩中的一筆分數。**用 lossy 送** —— 送不出去就丟掉,絕不阻塞 gameplay。
        ///
        /// ⚠️ **呼叫端必須在每個 8 拍結算點送一筆**(除了固定的 200ms 節奏之外)。
        /// 收端要靠相鄰兩筆的判定計數差值推導出「這個 8 拍區塊有沒有斷、有沒有中」,
        /// 才能重現遠端舞者的跳/停 gate(見 <c>Sdo.Ruleset.DanceGate</c>)。
        /// 少了 8 拍邊界那筆,一筆 frame 會跨越多個區塊,中間的資訊就丟失了 ——
        /// 症狀是遠端舞者該停的時候還在跳。
        /// </summary>
        public void SendFrame(long matchId, double tMs, long score, int combo, int maxCombo,
                              float hp, int p, int c, int b, int m)
        {
            if (!IsConnected) return;
            _link.SendLossy(JObj.New()
                .Str(NetProto.FieldType, NetProto.Frame)
                .Long("matchId", matchId)
                .Num("tMs", tMs)
                .Long("score", score)
                .Int("combo", combo)
                .Int("maxCombo", maxCombo)
                .Num("hp", hp)
                .Int("p", p).Int("c", c).Int("b", b).Int("m", m));
        }

        public void SendPlayFinished(long matchId, long score, int combo, int maxCombo,
                                     int p, int c, int b, int m)
            => Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.PlayFinished)
                .Long("matchId", matchId)
                .Long("score", score)
                .Int("combo", combo).Int("maxCombo", maxCombo)
                .Int("p", p).Int("c", c).Int("b", b).Int("m", m));

        public void SendChat(string text, string channel = "current", int expressionId = 0, string leading = null)
            => Send(JObj.New()
                .Str(NetProto.FieldType, NetProto.ChatSay)
                .Str("text", text ?? "")
                .Str("channel", channel ?? "current")
                .Int("expressionId", expressionId)
                .Str("leading", leading ?? ""));

        private void Send(JObj msg)
        {
            if (!_link.IsConnected) return;
            _link.Send(msg);
        }

        // ---- 事件觸發的小 helper(把 null 檢查與例外隔離集中在一處) ----

        private static void Raise<T>(Action<T> ev, T arg)
        {
            if (ev == null) return;
            try { ev(arg); }
            catch (Exception ex) { Debug.LogError("[net] 事件處理例外: " + ex); }
        }
    }

    /// <summary>握手時要送給 server 的本機身分。</summary>
    public struct NetHelloIdentity
    {
        public string PlayerId;
        public string Name;
        public string Guild;
        public int Level;
        public int Gender;
        public int BodyIndex;
        public string[] AvatarParts;
    }
}
