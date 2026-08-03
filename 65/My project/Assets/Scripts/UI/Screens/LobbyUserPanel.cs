using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Sdo.Localization;
using Sdo.Net;
using Sdo.Settings;
using Sdo.UI.Util;

namespace Sdo.UI.Screens
{
    /// <summary>
    /// 大廳左側的**玩家名單**(官方 <c>STATECOMMUNITYHALL.XML</c> 的 <c>win3</c>):
    /// 上面一排「全部 / 好友 / 家族 / 黑名單」四個分頁,中間是名單,底下一顆「添加好友」。
    /// 由下方面板那顆三人頭鈕(<c>ListShow</c>)開關 —— 官方那顆與單人頭(<c>AvtShow</c>)疊在同一格輪換。
    ///
    /// 座標逐字取自 XML(800×600、左上原點、y 向下)。win3 的 <c>x=-300</c> 是**動畫起點**
    /// (有一組 TransForm 把它滑到 0),所以裡面那些座標就是最終的絕對座標 —— 會動的是 root 自己
    /// (<see cref="HiddenX"/> ↔ 0),子物件一律照 XML 的絕對值擺、永遠不要跟著動。
    ///
    /// 🔴 **四個分頁吃的是同一份名單**(server 的 <c>userList</c>),差別只在濾鏡:
    ///    全部 = 全服在線、好友 = 名字在本機好友清單裡的那些、家族 = 與自己同一個家族名的那些。
    ///    好友清單存在**玩家自己那台機器**上(server 沒有帳號持久化,見 <see cref="FriendList"/>),
    ///    所以那個比對只能在這裡做,server 幫不上忙。
    ///
    /// 黑名單分頁列的是**本機黑名單上、而且現在在線**的那些人(<see cref="BlockList"/>,與好友清單同一種東西)。
    /// 加進去的入口是名單上**右鍵那個人**的選單(玩家信息 / 私聊 / 加為好友 / 加入黑名單)。
    ///
    /// 🔴 這個面板**一個字的空狀態說明都不寫**、**一個 Toast 都不彈**(「請先選一個人」「已經是好友了」
    ///    那些通通拿掉了)——**使用者明講的要求**:大廳按了沒做的事就是安靜地沒反應,不要跳字。
    ///    回饋改成看得見的畫面變化:加好友成功 → <see cref="Rebuild"/> 讓那個人立刻出現在「好友」分頁。
    ///    (舊註解寫過「靜靜地沒反應會讓人以為是壞了」,那是上一輪的判斷,已被使用者推翻。)
    /// </summary>
    internal sealed class LobbyUserPanel : MonoBehaviour
    {
        public enum Tab { All = 0, Friends = 1, Family = 2, Blacklist = 3 }

        // ---- 版位(win3,絕對座標;逐字取自 DATA/UI/STATECOMMUNITYHALL/STATECOMMUNITYHALL.XML)----
        private const float BgX = 7f, BgY = 82f;             // Lobby0.an 名單底板
        // 🔴 **不要**貼 friendbold.an。它不是「分頁列的底」——切出來看是 stage.png (928,500,71,38) 的
        //    一個**純黃色圓角框**,官方拿它去高亮某一格。以前把它當底圖貼在 (70,47),正好蓋在「好友」那格上,
        //    就成了使用者連兩輪回報的「好友後面那個拿不掉的黃框」(找了半天以為是 UGUI 的 focus 視覺)。
        //
        // 🔴 **也不要貼 qinmidu.an。** 官方 XML 確實有 `<Label x="30" y="87" background="qinmidu.an"/>`,
        //    但那三個字是**「親密度」**(qin-mi-du),是**好友**分頁才有的欄頭 —— 切出來量過:
        //    stage.png (968,193,43,15),43px 剛好三個字。以前把它當成「位置 / 暱稱」貼在全部分頁,
        //    等於在欄頭左邊多印三個不該出現的字。
        //    真正的「位置 / 暱稱」是**烤在 Lobby0 底板上**的(連同下面那八條圓角凹槽),不必也不能重畫。
        private const float TabY = 52f;
        // 四個分頁**並排**,每格 66×31(stage.png 的 x 依序 506/572/638/704,間隔正好 66)。
        private const float TabAllX = 7f, TabFriendX = 73f, TabFamilyX = 139f, TabBlackX = 205f;
        // 🔴 高度用 **8 × RowH = 224**,不是 XML 寫的 246。底板烤死的凹槽就是 **8 條**,
        //    246 會多露出 0.79 列 —— 第 9 列被切一半掛在最底下、還壓到「添加好友」那顆鈕
        //    (使用者回報的「多顯示了一排,最下面會超出框」)。官方那個 246 是連捲動區外框一起算的。
        private const float ListX = 26f, ListY = 110f, ListW = 233f;
        private const int VisibleRows = 8;
        private const float ListH = VisibleRows * RowH;
        private const float AddFriendX = 106f, AddFriendY = 347f;
        /// <summary>列高。官方 <c>&lt;ReportList … height="28"&gt;</c>,而底板烤死的那八條圓角凹槽
        /// 實測間距也是 ~28(板內 y=58/85/113/141/169/196/224)—— 列就是一格一條凹槽。
        /// 🔴 列與列之間**不能再加 spacing**,加了就會一路歪出凹槽(見 <see cref="Build"/> 的捲動區)。</summary>
        private const float RowH = 28f;

        // 捲軸握把(官方 ReportList 的 <ScrollBar need2bt="false"><Handle background="Lobby12.an"/>)。
        // 素材 LOBBY12.AN = stage.png (843,590,14,28) → 14×28,與大廳房間列表的 Lobby38 是同一塊圖。
        // 軌道 x:底板(Lobby0)把凹槽烤在圖裡,實測絕對 x 235-259、中央深溝 247-249 → 241 讓 14px 的握把
        // 落在 241..255、中心 248,正好坐進深溝。y 走 ListY .. ListY+(ListH-HandleH) = 110 .. 328。
        private const float HandleX = 241f, HandleW = 14f, HandleH = 28f;

        // ---- 滑入 / 滑出(官方 win3 的那組 TransForm:targetx=0 ↔ targetx=-300)----
        /// <summary>收起來時 root 停的 x —— 官方 <c>&lt;Window name="win3" x="-300"&gt;</c> 就是這個值:
        /// 整塊面板(連底板)推到畫面左外側,而不是原地消失。</summary>
        private const float HiddenX = -300f;
        /// <summary>官方那組 TransForm 的 period 是 **1000ms**。照抄會變成「按了要等一秒才看得到名單」——
        /// 這顆鈕是拿來反覆開關對照角色的,不是場景轉場,所以刻意加快:0.28s 還看得出是滑進來,又不擋手。</summary>
        private const float SlideSeconds = 0.28f;
        // 🔴 直接寫 SE 資料夾裡的檔名,不用 UiSfx 現成的常數:UiSfx 確實已經有這兩個字串,但掛的語意是
        //    ScreenFadeIn(轉場漸亮)/ WindowSlide(房間 UI 收合),與「名單滑進滑出」不是同一件事 ——
        //    哪天有人改那兩個常數的值,不該連帶把這裡的音效換掉。(UiSfx.cs 不歸這一軌改,不在那邊加常數。)
        private const string SfxSlideIn = "Interfacein";     // SE\Interfacein.wav
        private const string SfxSlideOut = "Interfaceout";   // SE\Interfaceout.wav

        // 官方 AllUserList 的 `<Columns count="6">` —— 欄位是**從列首依序累加**的,不是各自訂 x:
        //   width=0(隱藏的 id 欄) / 16 / 16 / 18 / 35 / 135  → 0,0,16,32,50,85
        // 兩個 16px 的圖示欄:第一欄放性別(male/female 那顆心),第二欄官方留給身份標記
        // (喇叭 / VIP 之類,見 XML 的 tip_xml="SpeakerTip.xml"),這個重製版沒有 → 空著但**版位要留**,
        // 少留這 16px 的話後面三欄整排左移,「大厅」就會壓到等級上。
        private const float ColIconX = 0f, ColIconW = 16f;
        private const float ColIcon2X = 16f, ColIcon2W = 16f;
        private const float ColLevelX = 32f, ColLevelW = 18f;
        // 位置欄(「大厅」/ 三位房號)比官方欄位累加值再**往右 8px**(使用者指定,對實機微調過)。
        private const float ColWhereX = 50f + 8f, ColWhereW = 35f;
        private const float ColNameX = 85f, ColNameW = 135f;
        // 顏色與粗細照實機取色(使用者逐格對過)——**不要**改回 XML 那組:XML 寫的是
        // baf684 / ecffac / fffbe0,但實機畫出來是下面這組,而且暱稱是**純白細體**、
        // 只有等級與位置是粗體。
        private static readonly Color32 LevelColor = new Color32(0xba, 0xf5, 0x84, 0xff);   // #baf584 bold
        private static readonly Color32 WhereColor = new Color32(0xeb, 0xfe, 0xac, 0xff);   // #ebfeac bold
        private static readonly Color32 NameColor = new Color32(0xff, 0xff, 0xff, 0xff);    // #ffffff 細體

        private RectTransform _root;
        private RectTransform _content;
        private ScrollRect _scroll;
        private Scrollbar _bar;
        private readonly Button[] _tabBtn = new Button[4];
        private readonly Image[] _tabImg = new Image[4];
        private Tab _tab = Tab.All;

        private readonly List<NetUserListEntry> _users = new List<NetUserListEntry>();
        private readonly List<Row> _rows = new List<Row>();
        private int _selectedUserId;
        private string _selectedName = "";

        /// <summary>名單裡「自己」那一列 —— 用來擋掉「把自己加成好友」。
        /// (以前還拿它把自己那列加粗,那個標記已經拿掉了:暱稱一律細體。)</summary>
        private int _selfUserId;
        private string _selfName = "", _selfGuild = "", _selfGuildEmblem = "";

        /// <summary>**目標**狀態(不是當下畫面):滑動途中就已經是最終值,所以連按兩下只是把補間反向,不會卡住。</summary>
        private bool _visible;
        /// <summary>root 目前的 x(補間中的中間值)。</summary>
        private float _slideX = HiddenX;
        /// <summary>名單重建過了,握把要重擺 —— 但**不能在 Rebuild 當下擺**,見 <see cref="Update"/>。</summary>
        private bool _handleDirty;

        /// <summary>開始滑入(true)/ 開始滑出(false) 的**那一刻**通知(不是動畫結束)。
        /// 大廳靠它同步藏/顯示左邊那尊 3D 角色 —— 要跟面板一起動,晚一拍會看到角色從名單底下鑽出來。</summary>
        public System.Action<bool> VisibilityChanged;

        /// <summary>右鍵名單上的某個人。選單本體由**大廳**畫(<c>LobbyScreen.ShowUserMenu</c>)——
        /// 面板自己畫的話選單會跟著 root 一起滑出畫面,而且會被捲動區的遮罩裁掉。</summary>
        public System.Action<NetUserListEntry> RowRightClicked;

        /// <summary>目標狀態;滑動動畫進行中也回目標值(見 <see cref="_visible"/>)。</summary>
        public bool Visible => _visible;

        /// <summary>名單上現在有幾個人(**未**套分頁濾鏡)。大廳靠它判斷「這份名單是不是還空著、
        /// 要不要先墊一份本機的上去」—— 見 <c>LobbyScreen.RequestOnlineUsers</c>。</summary>
        public int UserCount => _users.Count;

        private static string L(string k) => LocalizationManager.Get(k);
        private static string L(string k, params object[] a) => LocalizationManager.Get(k, a);
        private static Sprite An(string n) => LobbyArt.AnSoloAA(n);

        // ================================================================ 版面

        public void Build(Transform parent)
        {
            _root = UIKit.NewRect(parent, "LobbyUserPanel");
            UIKit.Stretch(_root);

            // 名單底板。欄頭(位置/暱稱)與那八條圓角凹槽都**烤在 Lobby0 上** → 只擺這一張圖,不重畫字,
            // 也不要再貼 qinmidu(那三個字是「親密度」,是好友分頁的欄頭 —— 見上面常數區的 🔴)。
            UIKit.AddSprite(_root, "PanelBg", LobbyArt.An("Lobby0"), BgX, BgY);

            BuildTab(0, "TabAll", "Lobby15", "Lobby13", TabAllX);
            BuildTab(1, "TabFriends", "Lobby18", "Lobby16", TabFriendX);
            BuildTab(2, "TabFamily", "Lobby143", "Lobby141", TabFamilyX);
            BuildTab(3, "TabBlacklist", "Lobby21", "Lobby19", TabBlackX);

            // 名單本體。底板已經畫好凹槽 → 捲動區自己不要再上底色。
            // 🔴 spacing **必須是 0**:列高 28 就是官方的 `height="28"`,也正好是底板凹槽的間距。
            //    以前給了 1,八列下來就累積歪掉 8px,最底下那列整個坐在凹槽外面。
            _scroll = UIKit.AddVerticalScroll(_root, "UserScroll", out _content, 0f, 0, new Color(0f, 0f, 0f, 0f));
            Place(_scroll.GetComponent<RectTransform>(), ListX, ListY, ListW, ListH);

            // 捲軸。🔴 兩個位置條件都是必要的:
            //   1. 掛在 _root 底下(不是外面那層畫布)—— 否則面板收起來/滑出去時,握把會孤零零留在畫面上。
            //   2. 建在 _scroll **之後** = 後面的兄弟畫在上面,不然會被捲動區的 viewport 底蓋掉。
            // 用 AnSoloAA:這張圖在 stage.png 裡緊貼著鄰居,共用圖集會把隔壁的不透明像素拖成白邊。
            // 🔴 **不要**改用 AnSoloCircleAA —— 那是給圓盤鈕的,會把這根膠囊的上下兩端當光暈剪掉。
            //
            // 🔴 用 Unity 內建的 Scrollbar,不要再自己接拖曳:自畫 Image + 自訂拖曳元件連續三輪都被回報
            //    「滑鼠左鍵拉不動,只有滾輪能動」。Scrollbar 是 Selectable,拖曳與**點軌道跳到那個位置**
            //    (使用者後來追加的要求)都由它處理。
            _bar = FixedScrollbar.Create(_root, "UserScrollbar", An("Lobby12"),
                                         HandleX, ListY, HandleW, HandleH, ListH);
            FixedScrollbar.Bind(_bar, _scroll);

            // 添加好友:把選中的那一列加進本機好友清單(與房間座位選單的「加好友」同一條路)。
            var add = UIKit.AddSpriteButton(_root, "AddFriend", An("Lobby131"), An("Lobby132"), An("Lobby133"),
                                            AddFriendX, AddFriendY);
            add.onClick.AddListener(OnAddFriend);
            UiSfx.AttachClick(add);

            // 預設收著:先擺到畫面外(官方 win3 的起點 x=-300)再關掉,免得下次開啟時第一幀閃一下 x=0。
            _root.anchoredPosition = new Vector2(HiddenX, 0f);
            _root.gameObject.SetActive(false);
            ApplyTabSprites();
        }

        private void BuildTab(int index, string name, string normal, string selected, float x)
        {
            // 官方是 CheckBox(只有 normal / pushed 兩張,沒有 hover)—— 選中就換成 pushed 那張。
            var b = UIKit.AddSpriteButton(_root, name, An(normal), An(normal), An(selected), x, TabY);
            // 🔴 分頁圖**只能**由 ApplyTabSprite 手動控制,所以把 UGUI 的 transition 整個關掉。
            //    留著 SpriteSwap 會出現「按一個動兩個」的怪 bug:AddSpriteButton 設了
            //    spriteState.selectedSprite = normal(未選圖),而點擊後那顆鈕會進入 UGUI 的 **Selected** 狀態
            //    → Selectable 用 overrideSprite=未選圖 蓋掉我們剛手動設上去的「已選」圖;
            //    同時上一顆退回 Normal、露出我們設的 sprite → 選中的樣式看起來跑到上一顆去了。
            //    (這坑會重犯:任何人把 transition 改回 SpriteSwap 就復發。)
            b.transition = Selectable.Transition.None;
            // 🔴 連 Navigation 也要關掉:UGUI 會給「目前被選取的那顆」畫一圈黃色外框(EventSystem 的
            //    focus 指示,與 transition 是兩回事)。使用者回報「好友後面有個黃框」講的就是它 ——
            //    點過的那顆會一直帶著框,而分頁的選取狀態我們已經用換圖表達了,不需要第二套指示。
            b.navigation = new Navigation { mode = Navigation.Mode.None };
            // 🔴 連 spriteState 也清空:transition=None 理論上會忽略它,但 UGUI 的 focus 視覺
            //    (點過之後那一圈黃框)仍然吃得到 selectedSprite。素材本身沒有黃框 —— 那圈是 Unity 畫的。
            b.spriteState = default;
            var captured = (Tab)index;
            b.onClick.AddListener(() => SetTab(captured));
            UiSfx.AttachClick(b);
            _tabBtn[index] = b;
            _tabImg[index] = b.targetGraphic as Image;
        }

        // ================================================================ 開關 / 分頁

        /// <summary>
        /// 開/關名單 —— 觸發滑入/滑出補間 + 音效,**不是**直接 SetActive。
        /// 🔴 滑出時這裡只把目標改掉,<c>SetActive(false)</c> 要等 <see cref="Update"/> 滑到底才做;
        ///    在這裡就關掉的話整段滑出去的過程根本看不到,等於沒有動畫。
        /// </summary>
        public void SetVisible(bool on)
        {
            if (_root == null || _visible == on) return;   // 已經朝那個方向在動了 → 不要重播音效、不要重發事件
            _visible = on;
            _root.gameObject.SetActive(true);
            if (on) Rebuild();
            UiSfx.Play(on ? SfxSlideIn : SfxSlideOut);
            VisibilityChanged?.Invoke(on);
        }

        private void Update()
        {
            if (_root == null || !_root.gameObject.activeSelf) return;   // 收好了就沒事做

            float target = _visible ? 0f : HiddenX;
            if (!Mathf.Approximately(_slideX, target))
            {
                // 速度固定 = 全程 / SlideSeconds:中途反向(連按)時速度一致,不會因為只剩一小段就變慢動作。
                // unscaledDeltaTime:前端不吃 timeScale(暫停、慢動作時 UI 還是要正常滑)。
                _slideX = Mathf.MoveTowards(_slideX, target, (-HiddenX / SlideSeconds) * Time.unscaledDeltaTime);
                _root.anchoredPosition = new Vector2(_slideX, 0f);
                if (!_visible && Mathf.Approximately(_slideX, HiddenX)) _root.gameObject.SetActive(false);
            }

            // 🔴 握把不能在 Rebuild() 當下擺:UIKit.Clear 用的 Object.Destroy 是**幀末**才真的拆掉,
            //    ContentSizeFitter 也要等 canvas 的 layout pass 才收斂 → 那一刻量到的 content 高度還是舊名單的。
            //    延到下一幀再擺,量到的才是新名單的高度。
            if (_handleDirty) { _handleDirty = false; PlaceHandle(); }
        }

        /// <summary>
        /// 把握把擺到對應捲動位置的高度。官方 ReportList 的 <c>&lt;ScrollBar need2bt="false"&gt;</c> 只有一顆 Handle
        /// (沒有上下箭頭鈕),四個分頁共用同一條軌道 —— 所以這裡也只有一根,換分頁不重建。
        ///
        /// 換分頁 / 名單重建之後 Content 的高度要等下一次 layout 才正確,所以是延後一幀由 _handleDirty 觸發的。
        /// 內容比視窗短時 <see cref="FixedScrollbar.Sync"/> 會把它停在最上面 —— 使用者要求握把**永遠顯示**
        /// (就算一個人都沒有),所以不做隱藏。
        /// </summary>
        private void PlaceHandle() => FixedScrollbar.Sync(_bar, _scroll);

        public void SetTab(Tab tab)
        {
            if (_tab == tab) return;
            _tab = tab;
            ApplyTabSprites();
            Rebuild();
        }

        private void ApplyTabSprites()
        {
            ApplyTabSprite(0, "Lobby15", "Lobby13");
            ApplyTabSprite(1, "Lobby18", "Lobby16");
            ApplyTabSprite(2, "Lobby143", "Lobby141");
            ApplyTabSprite(3, "Lobby21", "Lobby19");
        }

        private void ApplyTabSprite(int index, string normal, string selected)
        {
            bool on = (int)_tab == index;
            UIKit.ApplySprite(_tabImg[index], An(on ? selected : normal));
        }

        // ================================================================ 資料

        /// <summary>
        /// 換一份名單。<paramref name="selfUserId"/> / <paramref name="selfName"/> / <paramref name="selfGuild"/>
        /// 是本機這個人 —— 名單要把自己標出來(而且「加好友」不能加到自己)。
        /// </summary>
        public void SetUsers(IList<NetUserListEntry> users, int selfUserId, string selfName, string selfGuild)
            => SetUsers(users, selfUserId, selfName, selfGuild, "");

        /// <summary>同上,多帶本機的家族徽章 —— 同族的判定是**名字+徽章**兩者(見 <see cref="GuildIdentity"/>)。</summary>
        public void SetUsers(IList<NetUserListEntry> users, int selfUserId, string selfName, string selfGuild,
                             string selfGuildEmblem)
        {
            _selfUserId = selfUserId;
            _selfName = selfName ?? "";
            _selfGuild = selfGuild ?? "";
            _selfGuildEmblem = selfGuildEmblem ?? "";
            _users.Clear();
            if (users != null) for (int i = 0; i < users.Count; i++) _users.Add(users[i]);
            if (Visible) Rebuild();
        }

        /// <summary>名單重畫(好友/黑名單被右鍵選單改過之後,大廳叫它讓變動立刻看得見)。收著時不做 ——
        /// 下次 <see cref="SetVisible"/> 打開本來就會重畫。</summary>
        public void Refresh() { if (_visible) Rebuild(); }

        /// <summary>
        /// 依目前分頁重畫名單。**空的就是一片空白**(沒好友 / 沒家族 / 沒人在線 / 黑名單上沒人在線,都不寫字)——
        /// 使用者明講「那些字都不要寫」。
        /// </summary>
        private void Rebuild()
        {
            if (_content == null) return;
            UIKit.Clear(_content);
            _rows.Clear();
            _handleDirty = true;   // 高度變了,握把要重擺(延到下一幀,見 Update)

            var owner = ProfileManager.Active;
            for (int i = 0; i < _users.Count; i++)
            {
                var u = _users[i];
                if (!PassesFilter(u, owner)) continue;
                AddRow(u);
            }
            EnsureSelection();   // 一定要有一列是框起來的(預設 = 最上面的自己)
        }

        private bool PassesFilter(NetUserListEntry u, UserProfile owner)
        {
            switch (_tab)
            {
                // 好友:名字在本機清單裡(名字**就是**身分,見 FriendList 的註解)。自己不算好友,也不會被列進來。
                case Tab.Friends: return FriendList.IsFriend(owner, u.Name);
                // 家族:與自己**同名同徽章**(規則本體在 GuildIdentity —— server 的家族頻道用同一份,
                // 兩邊不一致就會變成「名單上看得到他、他卻收不到我的家族發言」)。
                // 自己沒有家族時這一頁一定是空的(不要變成「列出所有沒家族的人」)。
                case Tab.Family:
                    return GuildIdentity.Same(_selfGuild, _selfGuildEmblem, u.Guild, u.GuildEmblem);
                // 黑名單:名字在本機黑名單裡的那些(與好友分頁同一種比對,見 BlockList)。
                // 🔴 這一頁只列得出**現在在線**的人 —— 四個分頁吃的是同一份 server 名單,離線的人根本不在裡面。
                //    要列出「所有封鎖過的人」得另外拿 BlockList.Names 生一份假 entry,那與其他三頁的語意不一致。
                case Tab.Blacklist: return BlockList.IsBlocked(owner, u.Name);
                default: return true;
            }
        }

        private void AddRow(NetUserListEntry u)
        {
            var row = UIKit.NewRect(_content, "user" + u.UserId);
            row.sizeDelta = new Vector2(ListW, RowH);
            UIKit.Layout(row.gameObject, RowH);

            // 整列的點擊接盤(透明)。🔴 一定要有:Button 需要一個吃射線的 Graphic 才點得到,
            // 而高亮框平常是關著的、文字全部 raycastTarget=false → 沒有接盤的話整列點不動。
            var hit = UIKit.AddImage(row, "hit", new Color(0f, 0f, 0f, 0f), raycast: true);
            Place(hit.rectTransform, 0f, 0f, ListW, RowH);

            // 選中 / 滑過的高亮:官方 `<ReportList … hoverpic="ListCheck.an">` 那張現成圖 ——
            // stage.png (0,992,214,28) 是一圈**黃色圓角框**,圓角與底板烤死的那條凹槽剛好同形。
            // 🔴 以前是用四條 1px 的線自己畫一個**直角**框,套在圓角凹槽上四個角會凸出來。
            //    素材本來就有,不要自己畫。
            var hi = UIKit.AddSprite(row, "sel", An("ListCheck"), 0f, 0f);
            if (hi != null)
            {
                // AddSprite 會把 rect 縮成素材原生大小(214×28);列寬 233 比它寬,靠左貼齊凹槽即可。
                hi.raycastTarget = false;
                hi.gameObject.SetActive(false);
            }

            // 第一欄 = 性別小人:**綠色男 / 粉紅女**,19×23。
            // 🔴 素材要走 <c>player_male</c> / <c>player_female</c>(那兩個 .an 指向獨立的
            //    male.png / female.png)—— **不是** <c>male</c> / <c>female</c>:同名的
            //    MALE.AN 指到 stage.png (982,89) 是一顆**藍心**,與小人完全是兩個東西,
            //    接錯了整排就變成心。
            // 🔴 也**不要**用 Accepter_*(戴帽子)或 Spreader_*(帶金星)那兩組 —— 那是活動身份的
            //    變體版,一般玩家用的是這個最乾淨的無裝飾版(使用者指定)。
            var icon = UIKit.AddSprite(row, "icon", LobbyArt.An(u.Gender == 1 ? "player_male" : "player_female"),
                                       ColIconX, (RowH - 23f) * 0.5f);
            if (icon != null) icon.raycastTarget = false;

            // 第二欄 = **親密度**(qinmi.png 的 QINMI1..5:一根直立的溫度計柱 + 底下一顆小紅心,13×24)。
            // 這一欄的存在正是 qinmidu.an「親密度」那三個字的由來,也是 AllUserList 比 FriendList
            // **多出來的那一欄**(6 欄 vs 5 欄)。
            //
            // 🔴 **不是** female.an 那顆心。那顆是 18×16 的胖心,擺上去會與旁邊 19×23 的小人一樣大 ——
            //    官方實機那個明顯**小一截而且是細長的**(使用者連兩輪回報「愛心跟官方根本不一樣」)。
            //    female / male / man 那三顆心(藍/粉/灰)在這份 UI 裡另有用途,不歸名單用。
            //
            // 這個重製版沒有親密度系統 → 一律 QINMI1(最低那級,柱子是灰的)。非好友本來就是 0,
            // 官方「全部」分頁看到的也正是這一級;真做出親密度時把 1 換成 1..5 就好,其餘不用動。
            //
            // 🔴 走 AnSolo:QINMI1..5 在 qinmi.png 裡是**橫向緊貼**的(x = 52/39/26/13/0,間隔正好 13),
            //    共用圖集的雙線性取樣會把隔壁那一級的柱子拖進來。
            var qinmi = UIKit.AddSprite(row, "qinmi", LobbyArt.AnSolo("qinmi1"), ColIcon2X, (RowH - 24f) * 0.5f);
            if (qinmi != null) qinmi.raycastTarget = false;

            // 等級 / 位置:官方這兩欄是粗體(名字那欄不是)。
            var lv = Label(row, "lv", ColLevelX, ColLevelW, LevelColor, TextAlignmentOptions.MidlineRight);
            lv.text = u.Level > 0 ? u.Level.ToString() : "";
            lv.fontStyle = FontStyles.Bold;

            // 位置:在大廳寫「大厅」,在房裡就是**三位補零的房號**(官方實機是 015 / 014 / 000)。
            // 🔴 房號不走 LocalizationManager —— 純數字沒有什麼好翻譯的,而舊的
            //    `lobby.userlist_in_room` = "{0} 号房" 在這個 35px 寬的欄位裡根本擺不下。
            var where = Label(row, "where", ColWhereX, ColWhereW, WhereColor, TextAlignmentOptions.Midline);
            where.text = u.InLobby ? L("lobby.userlist_in_lobby") : u.RoomSeq.ToString("000");
            where.fontStyle = FontStyles.Bold;

            // 名字欄**置中**,不是靠左 —— 官方實機截圖裡長短名字的**左右緣都不齊**
            // (「ㄣЕ†n.ㄋ月光」比「Eithwa」既更靠左也更靠右),只有視覺重心對得上,那就是置中。
            // 靠左的話短名字會整個貼到「大厅」右邊,官方那道明顯的空隙就沒了。
            var name = Label(row, "name", ColNameX, ColNameW, NameColor, TextAlignmentOptions.Midline);
            name.text = u.Name ?? "";
            name.overflowMode = TextOverflowModes.Ellipsis;   // 名字沒有長度上限,不截會蓋出欄外
            // 🔴 暱稱一律**細體**(使用者指定)。以前會把「自己」那列加粗當標記 —— 拿掉了:
            //    官方沒有那個設計,而且一加粗就與旁邊那兩欄的粗體混在一起,反而看不出是誰。

            var btn = row.gameObject.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            int uid = u.UserId;
            string uname = u.Name ?? "";
            btn.onClick.AddListener(() => Select(uid, uname));
            UiSfx.AttachClick(btn);

            // 右鍵 → 那個人的社交選單。🔴 Button.onClick **只吃左鍵**(UGUI 的 Button.OnPointerClick 第一行就把
            // 非左鍵擋掉),所以一定要另外接;掛在同一個物件上不會打架(見 RightClickProxy)。
            // 順手把那一列選起來:官方右鍵誰,黃框就跟到誰身上 —— 不然選單講的是 A、框卻還在 B 上。
            var captured = u;
            var rc = row.gameObject.AddComponent<RightClickProxy>();
            rc.Clicked = () =>
            {
                Select(uid, uname);
                RowRightClicked?.Invoke(captured);
            };

            // 🔴 **不接 hover**。XML 雖然有 hoverpic,但實機那圈黃框是「選中」的標記,不是滑過的回饋 ——
            //    滑過就亮的話,滑鼠掃過名單會一路閃,而且分不出真正選中的是誰(使用者指定:只有點了才亮)。
            _rows.Add(new Row { UserId = uid, Name = uname, Highlight = hi });
            if (uid == _selectedUserId && hi != null) hi.gameObject.SetActive(true);
        }

        /// <summary>
        /// 名單重建之後把「選中」補上去。**名單一定要有一列是選中的**(使用者指定:預設就框在最上面
        /// 那個自己身上)—— 空著的話「添加好友」按下去沒反應,而畫面上看不出為什麼。
        ///
        /// 上一次選的人還在名單裡就維持不動(換分頁、4 秒一次的輪詢重建都不該把選擇弄丟);
        /// 不在了才重挑:優先挑**自己**那列,名單裡沒有自己(好友/家族分頁本來就不列自己)就挑第一列。
        /// </summary>
        private void EnsureSelection()
        {
            if (_rows.Count == 0) { _selectedUserId = 0; _selectedName = ""; return; }

            for (int i = 0; i < _rows.Count; i++)
                if (_rows[i].UserId == _selectedUserId) return;   // 還在 → AddRow 已經把框亮好了

            int pick = 0;
            for (int i = 0; i < _rows.Count; i++)
                if (_rows[i].UserId == _selfUserId) { pick = i; break; }
            Select(_rows[pick].UserId, _rows[pick].Name);
        }

        private TextMeshProUGUI Label(Transform parent, string name, float x, float w, Color color,
                                      TextAlignmentOptions align)
        {
            var t = UIKit.AddText(parent, name, "", 12f, color, align);
            Place(t.rectTransform, x, 0f, w, RowH);
            return t;
        }

        private void Select(int userId, string name)
        {
            _selectedUserId = userId;
            _selectedName = name ?? "";
            for (int i = 0; i < _rows.Count; i++)
                if (_rows[i].Highlight != null) _rows[i].Highlight.gameObject.SetActive(_rows[i].UserId == userId);
        }

        /// <summary>
        /// 「添加好友」。加的是**本機**清單(server 不知道誰跟誰是好友),所以對方不會收到通知 ——
        /// 這正是目前這套連線做得到的語意,見 <see cref="FriendList"/>。加完要存檔,不然關掉遊戲就沒了。
        ///
        /// 🔴 全程**不彈任何 Toast**(使用者要求:大廳一律安靜)。四種失敗(沒選人 / 是自己 / 已經是好友 /
        ///    寫入失敗)一律安靜返回;成功的回饋是看得見的畫面變化 —— <see cref="Rebuild"/> 讓那個人
        ///    立刻出現在「好友」分頁。(在「全部」頁按成功時畫面確實不會變,那是使用者選的:寧可安靜也不要跳字。)
        /// </summary>
        private void OnAddFriend()
        {
            if (_selectedName.Length == 0) return;
            var owner = ProfileManager.Active;
            if (owner == null) return;
            // 不能加自己。**兩個條件都要看**:userId 是這次連線的唯一編號(最可靠),
            // 但離線時大家都是 0 → 那時只有名字比對得準。
            if (_selectedUserId == _selfUserId) return;
            if (string.Equals(_selectedName, _selfName, System.StringComparison.OrdinalIgnoreCase)) return;
            if (FriendList.IsFriend(owner, _selectedName)) return;

            // playerId 傳空字串:名單封包帶的 userId 是**這次連線**的編號,存起來下次就對不上人了
            // (FriendList 認的本來就是名字,id 只是備查)。
            if (!FriendList.Add(owner, _selectedName, "", System.DateTime.UtcNow.ToString("o")))
            {
                Debug.Log("[LobbyUserPanel] FriendList.Add 失敗:" + _selectedName);
                return;
            }
            ProfileManager.Save();
            // 正在看好友頁 → 新加的那個立刻出現,這就是「成功了」的回饋。
            // 其他分頁的濾鏡(全部/家族)根本不看好友清單 → 重畫出來一模一樣,不必白拆一次整份列表。
            if (_tab == Tab.Friends) Rebuild();
        }

        private static void Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x, -y);
        }


        private struct Row
        {
            public int UserId;
            public string Name;
            public Image Highlight;
        }
    }
}
