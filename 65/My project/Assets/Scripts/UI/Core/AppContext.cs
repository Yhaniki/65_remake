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
            session.SeedRoomDefaults();   // 房間面板預設值(速度/note/組隊/掉落/模式)從 settings.json 種入
            var flow = new FlowManager();
            var clock = new SystemClock();
            var players = new MockPlayerService();
            var rooms = new MockRoomService(session);
            var chat = new MockChatService(clock);
            return new AppContext(session, flow, rooms, players, chat);
        }
    }
}
