using Sdo.Game;
using Sdo.Game.Net;
using Sdo.Net;
using Sdo.Settings;
using Sdo.UI.Services;

namespace Sdo.UI.Core
{
    /// <summary>Single holder for session state, the flow machine, and the back-end services.</summary>
    public sealed class AppContext
    {
        public GameSession Session { get; }
        public FlowManager Flow { get; }
        public IRoomService Rooms { get; }
        public IPlayerService Players { get; }
        public IChatService Chat { get; }

        /// <summary>
        /// 連線層。**null = 單機模式**(<c>config.ini [Net] serverAddress</c> 留空)。
        ///
        /// 讀房間狀態一律走 <see cref="Rooms"/> —— 兩個模式的形狀一樣,所以畫面程式碼不必分辨。
        /// **線上專屬的操作**(鎖格子 / 踢人 / 轉移房主 / 旁觀 / 開場 / 缺歌回報)走這裡,
        /// 呼叫前判斷 null;那就是「這個功能只有連線才有」最自然的表達。
        /// </summary>
        public NetClient Net { get; }

        public bool IsOnline => Net != null;

        public AppContext(GameSession session, FlowManager flow, IRoomService rooms,
                          IPlayerService players, IChatService chat, NetClient net = null)
        {
            Session = session;
            Flow = flow;
            Rooms = rooms;
            Players = players;
            Chat = chat;
            Net = net;
        }

        /// <summary>
        /// 依 <c>config.ini</c> 建 app context:填了 <c>[Net] serverAddress</c> 走連線,留空走單機。
        /// **這是唯一的分流點** —— 其餘程式碼不需要知道自己在哪個模式。
        /// </summary>
        public static AppContext Create()
            => RoomConfig.OnlineEnabled ? CreateOnline() : CreateOffline();

        /// <summary>
        /// 連線模式。**連線是非同步的** —— 這裡只把連線層建起來並開始連,
        /// 等待與逾時由呼叫端(<c>FrontendApp.BootCo</c>)處理,連不上就提示並改用單機。
        /// </summary>
        private static AppContext CreateOnline()
        {
            var offline = CreateOffline();   // session / chat / players 先照單機那套建好

            var net = new NetClient();
            var identity = new NetHelloIdentity
            {
                PlayerId = offline.Session.LocalPlayerId,
                Name = offline.Session.LocalPlayerName,
                Guild = offline.Session.GuildName,
                Level = ParseLevel(RoomConfig.playerLevel),
                Gender = offline.Session.Gender,
                // 握手就帶上真的體型與穿搭 —— 用 profile 的**快取**那份(EquippedAvatarParts),
                // 不要在開機時去碰 AvatarItemCatalog(那會提早觸發整份商城目錄載入,很貴)。
                // 飾品之類需要 catalog 才算得出來的差異,由進房前的 setLook 修正(見 net.LocalLook)。
                BodyIndex = Sdo.Settings.ProfileManager.Active != null
                    ? Sdo.Settings.ProfileManager.Active.bodyShapeIndex : 0,
                AvatarParts = Sdo.Settings.ProfileManager.Active != null
                    ? Sdo.Settings.ProfileManager.Active.EquippedAvatarParts() : null,
            };
            net.Connect(RoomConfig.serverAddress, RoomConfig.serverPort, RoomConfig.serverPassword, identity);

            // 「我現在長什麼樣」的唯一來源。NetClient 在建房/加入/旁觀的第一行呼叫它(PublishLook),
            // 所以第一份廣播出去的房間快照就已經帶對的外觀 —— 別人不會先看到一隻預設的女角。
            // 放在 AppContext 是因為它是唯一的離線/連線分流點,也是唯一該知道 profile/穿搭怎麼解析的地方。
            net.LocalLook = () => LocalLookNow(offline.Session);

            var rooms = new OnlineRoomService(net, offline.Session);
            // 聊天:同房的公開發言走 server 廣播;密語/家族/系統/「你說」那些本機專屬的行仍由離線實作產生
            // (見 OnlineChatService 的註解)。所以是「包在外面」而不是整個換掉。
            var chat = new OnlineChatService(net, offline.Chat, () => offline.Session.Gender == 1);
            return new AppContext(offline.Session, offline.Flow, rooms, offline.Players, chat, net);
        }

        /// <summary>
        /// 現在的本機外觀:性別看 session(選角色畫面可能剛切過)、體型與穿搭看 active profile。
        ///
        /// 穿搭走 <c>WardrobeStore.ResolveEquippedParts</c> 而不是 profile 的 <c>equippedParts</c> 快取 ——
        /// 前者會把合成的翅膀/表情/項鍊算進去(那條路與房間、選角色畫面建本機 avatar 用的是同一個函式,
        /// 所以「別人看到的我」與「我看到的我」保證一致)。
        /// </summary>
        private static NetAvatarLook LocalLookNow(GameSession session)
        {
            var p = Sdo.Settings.ProfileManager.Active;
            int gender = session != null && session.Gender == 1 ? 1 : 0;
            var look = new NetAvatarLook { Gender = gender, BodyIndex = p != null ? p.bodyShapeIndex : 0 };
            if (p != null)
                look.Parts = WardrobeStore.ResolveEquippedParts(p, gender, id => AvatarItemCatalog.Instance.ById(id));
            return look;
        }

        private static int ParseLevel(string s)
        {
            int v;
            return int.TryParse((s ?? "").Trim(), out v) && v > 0 ? v : 1;
        }

        /// <summary>Build an app context backed by the offline mock services.</summary>
        public static AppContext CreateMock() => CreateOffline();

        /// <summary>單機模式。</summary>
        private static AppContext CreateOffline()
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
            session.SeedRoomDefaults();   // 房間面板預設值(速度/note/組隊/掉落/模式)從共用 config.ini 種入
            WardrobeStore.Load(session);  // 錢包 + 擁有衣物 + 穿搭 從 active user 的 profile.json 載入 (首次自動發起始金額)
            var flow = new FlowManager();
            var clock = new SystemClock();
            var players = new MockPlayerService();
            var rooms = new MockRoomService(session);
            // 聊天列表本機發言者顯示 active 使用者的名字/id（跟頭頂名字一致），不再寫死「我」。
            // localGuild 讓家族頻道知道本機有沒有家族（空 → 「你沒有家族」；見 RoomScreen F3 除錯切換）。
            // simulateOthers：模擬他人聊天（bot 閒聊／同族閒聊／罐頭回覆）只在編輯器測試時開；打包 build 一律關閉（見 SdoDebugFeatures）。
            var chat = new MockChatService(clock, () => session.Gender == 1, () => session.LocalPlayerName,
                localGuild: () => session.GuildName, simulateOthers: SdoDebugFeatures.Enabled);
            return new AppContext(session, flow, rooms, players, chat);
        }
    }
}
