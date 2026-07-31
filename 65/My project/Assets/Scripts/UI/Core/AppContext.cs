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
        public IPlayerService Players { get; }

        /// <summary>
        /// 房間服務。**登入前後會換人**(單機的 MockRoomService ↔ 線上的 OnlineRoomService),
        /// 但 AppContext 這個**物件本身不換** —— 畫面在 <c>Build(ctx)</c> 時把 ctx 抓成自己的欄位就不再更新,
        /// 換掉整個 ctx 會讓已建好的畫面全指著死掉的那份(RoomScreen 有 50 處 <c>Ctx.Net</c>)。
        /// </summary>
        public IRoomService Rooms { get; private set; }

        /// <summary>聊天服務。同 <see cref="Rooms"/>,登入/登出時就地換掉。</summary>
        public IChatService Chat { get; private set; }

        /// <summary>
        /// 連線層。**null = 單機模式**(還沒登入、沒有伺服器可連、或登入失敗退回單機)。
        ///
        /// 讀房間狀態一律走 <see cref="Rooms"/> —— 兩個模式的形狀一樣,所以畫面程式碼不必分辨。
        /// **線上專屬的操作**(鎖格子 / 踢人 / 轉移房主 / 旁觀 / 開場 / 缺歌回報)走這裡,
        /// 呼叫前判斷 null;那就是「這個功能只有連線才有」最自然的表達。
        /// </summary>
        public NetClient Net { get; private set; }

        public bool IsOnline => Net != null;

        /// <summary>線上/單機切換時通知畫面重畫(版面依模式而異,例如大廳的房間列表來源)。</summary>
        public event System.Action OnlineChanged;

        /// <summary>單機那份聊天實作。線上的 OnlineChatService 是**包在它外面**的,登出時要換回這一份
        /// (不能重新 new 一個 —— 歷史訊息會整串消失)。</summary>
        private readonly IChatService _offlineChat;

        public AppContext(GameSession session, FlowManager flow, IRoomService rooms,
                          IPlayerService players, IChatService chat, NetClient net = null)
        {
            Session = session;
            Flow = flow;
            Rooms = rooms;
            Players = players;
            Chat = chat;
            Net = net;
            _offlineChat = chat;
        }

        /// <summary>
        /// 建 app context。**開機一律是單機** —— 連線改由玩家在選角色畫面按「登入」才發動
        /// (<see cref="BeginLogin"/> → <see cref="CompleteLogin"/>)。
        ///
        /// 以前這裡會依 <c>config.ini [Net] serverAddress</c> 直接開連線,於是連「只想單機玩」的人
        /// 開機都要先等一次握手。現在開機一行連線程式碼都不會跑。
        /// </summary>
        public static AppContext Create() => CreateOffline();

        /// <summary>
        /// 發動連線。**非同步** —— 這裡只把連線層建起來並開始連,握手完成與否由呼叫端輪詢
        /// (<c>FrontendApp.LoginCo</c>);握手成功要再呼叫 <see cref="CompleteLogin"/> 才會真的切成線上模式。
        ///
        /// 回 <c>null</c> = <c>config.ini</c> 沒填 <c>[Net] serverAddress</c>(根本沒有伺服器可連)。
        /// </summary>
        public NetClient BeginLogin()
        {
            if (!RoomConfig.OnlineEnabled) return null;

            var net = new NetClient();
            var identity = new NetHelloIdentity
            {
                PlayerId = Session.LocalPlayerId,
                Name = Session.LocalPlayerName,
                Guild = Session.GuildName,
                Level = ProfileFields.PlayerLevelValue(Sdo.Settings.ProfileManager.Active),
                Gender = Session.Gender,
                // 握手就帶上真的體型與穿搭 —— 用 profile 的**快取**那份(EquippedAvatarParts),
                // 不要在開機時去碰 AvatarItemCatalog(那會提早觸發整份商城目錄載入,很貴)。
                // 飾品之類需要 catalog 才算得出來的差異,由進房前的 setLook 修正(見 net.LocalLook)。
                BodyIndex = Sdo.Settings.ProfileManager.Active != null
                    ? Sdo.Settings.ProfileManager.Active.bodyShapeIndex : 0,
                AvatarParts = Sdo.Settings.ProfileManager.Active != null
                    ? Sdo.Settings.ProfileManager.Active.EquippedAvatarParts() : null,
            };
            net.Connect(RoomConfig.serverAddress, RoomConfig.serverPort, RoomConfig.serverPassword, identity,
                        RoomConfig.serverTls, RoomConfig.serverCertFingerprint);

            // 「我現在長什麼樣」的唯一來源。NetClient 在建房/加入/旁觀的第一行呼叫它(PublishLook),
            // 所以第一份廣播出去的房間快照就已經帶對的外觀 —— 別人不會先看到一隻預設的女角。
            // 放在 AppContext 是因為它是唯一的離線/連線分流點,也是唯一該知道 profile/穿搭怎麼解析的地方。
            net.LocalLook = () => LocalLookNow(Session);
            // 「我現在是誰」的唯一來源,與 LocalLook 成對(NetClient 在建房/加入/旁觀一起呼叫)。
            // 握手報的名字是**按登入那刻**的 active profile —— 而選性別 == 選帳號,女角與男角的名字不一樣,
            // 所以進房前一定要再報一次,否則別人看到的是「新的男角」配「舊的女角名字」。
            net.LocalIdentity = () => LocalIdentityNow(Session);
            return net;
        }

        /// <summary>
        /// 握手成功 → **就地**切成線上模式:房間服務換成 OnlineRoomService、聊天包上 OnlineChatService、
        /// <see cref="Net"/> 指過去。
        ///
        /// 🔴 <see cref="Session"/> / <see cref="Flow"/> / <see cref="Players"/> 原封不動 ——
        /// 玩家在選角色畫面已經選好的性別與身分不能在這一步蒸發。
        /// </summary>
        public void CompleteLogin(NetClient net)
        {
            if (net == null || Net == net) return;
            Net = net;
            Rooms = new OnlineRoomService(net, Session);
            // 聊天:同房的公開發言走 server 廣播;密語/家族/系統/「你說」那些本機專屬的行仍由離線實作產生
            // (見 OnlineChatService 的註解)。所以是「包在外面」而不是整個換掉。
            Chat = new OnlineChatService(net, _offlineChat, () => Session.Gender == 1);
            if (OnlineChanged != null) OnlineChanged();
        }

        /// <summary>
        /// 退回單機:斷線、房間/聊天換回單機那份、<see cref="Net"/> 設回 null。
        /// 連不上、被踢下線、玩家自己登出都走這裡。
        ///
        /// 🔴 同一個 <see cref="Session"/> 留著 —— 這正是不能用「重建一個 AppContext」代替的原因
        /// (那會把玩家已選好的性別/身分一起換掉)。
        /// </summary>
        public void Logout(string reason)
        {
            bool wasOnline = Net != null;
            if (Net != null)
            {
                Net.Disconnect(reason);
                Net = null;
            }
            // OnlineRoomService 在 NetClient 上掛了事件;不退訂的話反覆登入/登出會累積重複訂閱。
            var disposable = Rooms as System.IDisposable;
            if (disposable != null) disposable.Dispose();
            Rooms = new MockRoomService(Session);
            Chat = _offlineChat;
            if (wasOnline && OnlineChanged != null) OnlineChanged();
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

        /// <summary>
        /// 現在的本機身分:名字 / playerId / 性別看 session(選角色畫面可能剛切過帳號),
        /// 家族與等級走 <see cref="ProfileFields"/> —— 那兩項的 Default 在 config.ini,
        /// 但這個角色自己設過就以角色的為準(所以切帳號時會跟著換)。
        /// </summary>
        private static NetPlayerIdentity LocalIdentityNow(GameSession session)
        {
            var p = Sdo.Settings.ProfileManager.Active;
            return new NetPlayerIdentity
            {
                Name = session != null ? session.LocalPlayerName : "",
                PlayerId = session != null ? session.LocalPlayerId : "",
                // 家族名走 profile/config 那條(而不是 GameSession 寫死的示範字串)——
                // 這樣「別人在房間看到的我的家族」與「我頭上名牌的家族」是同一個來源。
                Guild = ProfileFields.FamilyName(p),
                Level = ProfileFields.PlayerLevelValue(p),
            };
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
