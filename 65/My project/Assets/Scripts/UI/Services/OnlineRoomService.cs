using System;
using System.Collections.Generic;
using Sdo.Game.Net;
using Sdo.Net;
using Sdo.UI.Core;
using UnityEngine;

namespace Sdo.UI.Services
{
    /// <summary>
    /// 把 <see cref="NetClient"/> 接到 UI 既有的 <see cref="IRoomService"/> 介面上。
    ///
    /// **只負責「讀狀態」的相容。** 整個前端已經圍繞 <c>RoomInfo</c> 寫好了
    /// (`RoomScreen.Render` 讀 `Ctx.Rooms.CurrentRoom`、聊天讀 `SeatInfo.Player.DisplayName`…),
    /// 有了這層 adapter,那些畫面**一行都不用改**就能顯示線上資料。
    ///
    /// 線上專屬的操作(鎖格子 / 踢人 / 轉移房主 / 旁觀 / 開場)**不塞進這個介面** ——
    /// 那些走 <c>AppContext.Net</c>(<see cref="NetClient"/> 本身就是完整的 API)。
    /// 硬要把它們塞進 IRoomService 只會讓離線實作被迫長出一堆「這個模式不支援」的空方法。
    ///
    /// ⚠️ <see cref="CreateRoom"/> / <see cref="JoinRoom"/> 這兩個**同步**簽章在連線下做不到 ——
    /// 它們要等 server 回應。呼叫端請改用 <c>Ctx.Net.CreateRoom(name, callback)</c> /
    /// <c>Ctx.Net.JoinRoom(code, callback)</c>。這裡保留簽章只為了滿足介面,被呼叫時會記錯誤。
    /// </summary>
    public sealed class OnlineRoomService : IRoomService
    {
        private readonly NetClient _net;
        private readonly GameSession _session;

        /// <summary>目前房間的 UI 視圖。每次 server 推新快照就重建。</summary>
        private RoomInfo _current;

        /// <summary>房間列表(只在按了「重新整理」之類的時候更新)。</summary>
        private List<RoomInfo> _rooms = new List<RoomInfo>();

        public event Action RoomsChanged;
        public event Action<int> RoomUpdated;

        public OnlineRoomService(NetClient net, GameSession session)
        {
            _net = net;
            _session = session;

            _net.RoomUpdated += OnRoomUpdated;
            _net.RoomLeft += OnRoomLeft;
        }

        public void Dispose()
        {
            _net.RoomUpdated -= OnRoomUpdated;
            _net.RoomLeft -= OnRoomLeft;
        }

        // ---- 讀 ----

        public IReadOnlyList<RoomInfo> GetRooms() => _rooms;

        public RoomInfo GetRoom(int id)
        {
            if (_current != null && _current.Id == id) return _current;
            for (int i = 0; i < _rooms.Count; i++) if (_rooms[i].Id == id) return _rooms[i];
            return null;
        }

        public RoomInfo CurrentRoom => _current;

        public bool IsHost => _net.IsHost;

        /// <summary>房間列表刷新(非同步 —— 結果透過 <see cref="RoomsChanged"/> 通知)。</summary>
        public void RefreshRooms()
        {
            _net.RequestRoomList(entries =>
            {
                _rooms = NetRoomMapping.ToRoomInfos(entries);
                if (RoomsChanged != null) RoomsChanged();
            });
        }

        // ---- 事件轉接 ----

        private void OnRoomUpdated(NetRoomSnapshot snap)
        {
            _current = NetRoomMapping.ToRoomInfo(snap);
            _session.CurrentRoomId = _current.Id;
            if (RoomUpdated != null) RoomUpdated(_current.Id);
        }

        private void OnRoomLeft(string reason)
        {
            int wasId = _current != null ? _current.Id : -1;
            _current = null;
            _session.CurrentRoomId = -1;
            if (RoomUpdated != null && wasId >= 0) RoomUpdated(wasId);
            if (RoomsChanged != null) RoomsChanged();
        }

        // ---- 寫(fire-and-forget:server 是唯一權威,狀態等 roomState 回來才變) ----

        public void SetReady(bool ready) => _net.SetReady(ready);

        public void SetSong(string title)
        {
            // 線上選歌要送完整的 NetSongRef(packId / 譜面路徑…),不是只有標題。
            // 這條路徑留給離線;線上請走 Ctx.Net.SetSong(NetSongRef)。
            Debug.LogWarning("[net] OnlineRoomService.SetSong(string) 不適用於連線模式 —— " +
                             "請用 Ctx.Net.SetSong(NetSongRef)");
        }

        public void SetMode(GameMode mode)
        {
            var settings = CurrentSettingsOrDefault();
            settings.GameMode = mode == GameMode.Free ? 0 : 1;
            _net.SetRoomSettings(settings);
        }

        public void LeaveRoom() => _net.LeaveRoom();

        /// <summary>全員都準備好了嗎?(房主不算 —— 它沒有準備狀態)</summary>
        public bool AllReady()
        {
            if (_current == null) return false;
            for (int i = 0; i < _current.Seats.Count; i++)
            {
                var s = _current.Seats[i];
                if (s.IsEmpty || s.IsHost) continue;
                if (!s.IsReady) return false;
            }
            return true;
        }

        /// <summary>
        /// 可以按開始了嗎?**這只是 UI 的預檢** —— 真正的權威判定在 server
        /// (<c>NetRoom.RequestStart</c>),它會連組隊版型都驗過。
        /// </summary>
        public bool CanStart()
        {
            if (!_net.IsHost || _current == null) return false;
            if (string.IsNullOrEmpty(_current.SongTitle)) return false;
            if (_current.Status != RoomStatus.Waiting) return false;
            if (!AllReady()) return false;
            for (int i = 0; i < _current.Seats.Count; i++)
            {
                var s = _current.Seats[i];
                if (s != null && !s.IsEmpty && s.Avail != Availability.Have) return false;
            }
            return true;
        }

        // ---- 介面要求但連線下做不到的同步簽章 ----

        public RoomInfo CreateRoom(GameMode mode)
        {
            Debug.LogError("[net] OnlineRoomService.CreateRoom 是同步簽章,連線下做不到 —— " +
                           "請用 Ctx.Net.CreateRoom(name, onResult)");
            return null;
        }

        public JoinResult JoinRoom(int id)
        {
            Debug.LogError("[net] OnlineRoomService.JoinRoom 是同步簽章,連線下做不到 —— " +
                           "請用 Ctx.Net.JoinRoom(code, onResult)");
            return JoinResult.NotFound;
        }

        // ---- 內部 ----

        /// <summary>目前的房間設定(拿不到就給預設值)—— 改設定時要送完整的一份。</summary>
        private NetRoomSettings CurrentSettingsOrDefault()
        {
            var snap = _net.Room;
            return snap != null && snap.Settings != null ? snap.Settings.Clone() : new NetRoomSettings();
        }
    }
}
