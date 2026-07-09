using Sdo.UI.Services;

namespace Sdo.UI.Core
{
    /// <summary>Single holder for session state, the flow machine, and the (mock) back-end services.</summary>
    public sealed class AppContext
    {
        public GameSession Session { get; }
        public FlowManager Flow { get; }
        public IRoomService Rooms { get; }
        public IPlayerService Players { get; }
        public IChatService Chat { get; }

        public AppContext(GameSession session, FlowManager flow, IRoomService rooms, IPlayerService players, IChatService chat)
        {
            Session = session;
            Flow = flow;
            Rooms = rooms;
            Players = players;
            Chat = chat;
        }

        /// <summary>Build an app context backed by the offline mock services.</summary>
        public static AppContext CreateMock()
        {
            var session = new GameSession();
            // 本機身分(id/名字/性別)由 active 使用者(DATA/PROFILE)帶入 —— ProfileManager.Boot() 已在開機時跑過。
            var prof = Sdo.Settings.ProfileManager.Active;
            if (prof != null)
            {
                session.LocalPlayerId = prof.id;
                session.LocalPlayerName = prof.name;
                session.Gender = prof.gender;
            }
            session.SeedRoomDefaults();   // 房間面板預設值(速度/note/組隊/掉落/模式)從 active user 的 config.ini 種入
            var flow = new FlowManager();
            var clock = new SystemClock();
            var players = new MockPlayerService();
            var rooms = new MockRoomService(session);
            // 聊天列表本機發言者顯示 active 使用者的名字/id（跟頭頂名字一致），不再寫死「我」。
            var chat = new MockChatService(clock, () => session.Gender == 1, () => session.LocalPlayerName);
            return new AppContext(session, flow, rooms, players, chat);
        }
    }
}
