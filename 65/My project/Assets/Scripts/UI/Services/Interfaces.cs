using System;
using System.Collections.Generic;

namespace Sdo.UI.Services
{
    /// <summary>Abstracted time source so chat/bot timing is deterministic in tests.</summary>
    public interface IClock { double NowMs { get; } }

    public sealed class SystemClock : IClock
    {
        public double NowMs => UnityEngine.Time.realtimeSinceStartupAsDouble * 1000.0;
    }

    /// <summary>
    /// Room list + current-room operations. The mock impl is local/offline; a future FishNet impl
    /// swaps in behind the same interface without any UI change.
    /// </summary>
    public interface IRoomService
    {
        IReadOnlyList<RoomInfo> GetRooms();
        RoomInfo GetRoom(int id);
        RoomInfo CurrentRoom { get; }
        bool IsHost { get; }

        event Action RoomsChanged;
        event Action<int> RoomUpdated;

        RoomInfo CreateRoom(GameMode mode);
        JoinResult JoinRoom(int id);
        void LeaveRoom();
        void SetReady(bool ready);
        bool AllReady();
        bool CanStart();
        void SetSong(string title);
        void SetMode(GameMode mode);
    }

    public interface IPlayerService
    {
        IReadOnlyList<PlayerProfile> GetOnlinePlayers();
        event Action PlayersChanged;
    }

    public interface IChatService
    {
        IReadOnlyList<ChatMessage> History { get; }
        event Action<ChatMessage> MessageReceived;
        void Send(string text, ChatChannel channel = ChatChannel.Current);
        void SendExpression(int expressionId, ChatChannel channel = ChatChannel.Current);
        void SendExpression(int expressionId, ChatChannel channel, string trailingText);
        // leadingText / trailingText = 指令前/後的字，保留 emoji 在輸入中的位置（見 RoomChatCommand.TryParseExpression）。
        void SendExpression(int expressionId, ChatChannel channel, string leadingText, string trailingText);
        // 密語（私聊）：送「你對X說」+對方收到「X對你說」；不在頻道→「X不在當前頻道」；查無帳號→「X無此id」。
        // 單機：對象查離線實作的假名冊（MockChatService）。連線：對象由 server 照名字在全服找（跨房），
        // 三行都等 server 回 whisperMsg 才畫，本機不先畫（見 OnlineChatService.SendWhisper）。
        void SendWhisper(string target, string body, ChatChannel channel = ChatChannel.Current);
        // 家族頻道：有家族 → 綠字「<家族>名字: 內容」+ 同族偶爾回話；沒有家族 → 紅字「你沒有家族」。皆本機專屬、不彈頭上泡。
        void SendGuild(string text);
        // 家族頻道的表情（在家族頻道打 /翻）：守門與 SendGuild 相同（沒家族 → 「你沒有家族」），但帶 expressionId 走。
        // 少了這一支，家族頻道的表情只能當純文字送，收端就只印得出 "/翻" 而不是 emoji 小動畫。
        void SendGuildExpression(int expressionId, string leadingText, string trailingText);
        // 好友頻道沒帶 [名字] 對象就送出 → 白字「你說: 內容」，只有自己看得到、不送給任何人、不彈泡。
        void SendSelfTalk(string text);
        // 系統提示行（除錯/狀態回饋用）：金黃字，本機專屬。
        void SendSystem(string text);
        // 玩家進出舞台遊戲的廣播（顏色 72c1fe）：「X 進入舞台遊戲」/「X 離開舞台」。
        void AnnounceStageEnter(string name);
        void AnnounceStageLeave(string name);
        // 設定目前作用域：之後送出的訊息會標記成大廳或該房間（密語除外，永遠跨場）。畫面在 OnShow 設定。
        void SetScope(ChatScope scope, int roomId = 0);
        void Clear();  // 清空訊息歷史（換場地時呼叫：大廳→房間、房間→遊戲、遊戲→房間）
        // 只清掉進出舞台廣播（「X 進入舞台遊戲」/「X 離開舞台遊戲」），玩家講的話留著。
        // 進舞台時呼叫一次、回房重建訊息欄前再呼叫一次 —— 那幾行是「誰還在房裡等」的即時提示,
        // 打完一首回來早就過期了;不清的話回房 RebuildRoomChat 會把它們整批重播（使用者回報的症狀）。
        void ClearStageAnnouncements();
        void Tick();   // drive scripted bot traffic (call each frame)
    }
}
