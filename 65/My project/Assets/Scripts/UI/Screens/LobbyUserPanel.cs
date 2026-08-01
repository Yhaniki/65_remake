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
    /// 黑名單這個重製版沒有 → 分頁照官方擺著(少一個分頁比空著更難認,而且擋著之後真的要做時的版位),
    /// 但點下去就是**一頁空白**。
    ///
    /// 🔴 這個面板**一個字的空狀態說明都不寫**、**一個 Toast 都不彈**(「請先選一個人」「已經是好友了」
    ///    那些通通拿掉了)——**使用者明講的要求**:大廳按了沒做的事就是安靜地沒反應,不要跳字。
    ///    回饋改成看得見的畫面變化:加好友成功 → <see cref="Rebuild"/> 讓那個人立刻出現在「好友」分頁。
    ///    (舊註解寫過「靜靜地沒反應會讓人以為是壞了」,那是上一輪的判斷,已被使用者推翻。)
    /// </summary>
    internal sealed class LobbyUserPanel : MonoBehaviour
    {
        public enum Tab { All = 0, Friends = 1, Family = 2, Blacklist = 3 }

        // ---- 版位(win3,絕對座標) ----
        private const float BgX = 7f, BgY = 82f;             // Lobby0.an 名單底板
        // 🔴 **不要**貼 friendbold.an。它不是「分頁列的底」——切出來看是 stage.png (928,500,71,38) 的
        //    一個**純黃色圓角框**,官方拿它去高亮某一格。以前把它當底圖貼在 (70,47),正好蓋在「好友」那格上,
        //    就成了使用者連兩輪回報的「好友後面那個拿不掉的黃框」(找了半天以為是 UGUI 的 focus 視覺)。
        private const float IntimacyX = 30f, IntimacyY = 87f;// qinmidu.an「位置 / 暱稱」欄頭
        private const float TabY = 52f;
        private const float TabAllX = 7f, TabFriendX = 73f, TabFamilyX = 139f, TabBlackX = 205f;
        private const float ListX = 26f, ListY = 110f, ListW = 233f, ListH = 246f;
        private const float AddFriendX = 106f, AddFriendY = 347f;
        private const float RowH = 28f;

        // 捲軸握把(官方 ReportList 的 <ScrollBar need2bt="false"><Handle background="Lobby12.an"/>)。
        // 素材 LOBBY12.AN = stage.png (843,590,14,28) → 14×28,與大廳房間列表的 Lobby38 是同一塊圖。
        // 軌道 x:底板(Lobby0)把凹槽烤在圖裡,實測絕對 x 235-259、中央深溝 247-249 → 241 讓 14px 的握把
        // 落在 241..255、中心 248,正好坐進深溝。y 走 ListY .. ListY+(ListH-HandleH) = 110 .. 328。
        private const float HandleX = 241f, HandleH = 28f;

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

        // 官方 ReportList 的欄寬與顏色(AllUserList 的 Columns)。第一欄 width=0(隱藏的 id 欄)不畫。
        private const float ColIconX = 4f, ColIconW = 16f;
        private const float ColLevelX = 22f, ColLevelW = 26f;
        private const float ColWhereX = 50f, ColWhereW = 44f;
        private const float ColNameX = 96f, ColNameW = 130f;
        private static readonly Color32 LevelColor = new Color32(0xba, 0xf6, 0x84, 0xff);   // 0xffbaf684
        private static readonly Color32 WhereColor = new Color32(0xec, 0xff, 0xac, 0xff);   // 0xffecffac
        private static readonly Color32 NameColor = new Color32(0xff, 0xfb, 0xe0, 0xff);    // 0xfffffbe0

        private RectTransform _root;
        private RectTransform _content;
        private ScrollRect _scroll;
        private Image _handle;
        private readonly Button[] _tabBtn = new Button[4];
        private readonly Image[] _tabImg = new Image[4];
        private Tab _tab = Tab.All;

        private readonly List<NetUserListEntry> _users = new List<NetUserListEntry>();
        private readonly List<Row> _rows = new List<Row>();
        private int _selectedUserId;
        private string _selectedName = "";

        /// <summary>名單裡「自己」那一列 —— 用來把自己標出來,也避免把自己加成好友。</summary>
        private int _selfUserId;
        private string _selfName = "", _selfGuild = "";

        /// <summary>**目標**狀態(不是當下畫面):滑動途中就已經是最終值,所以連按兩下只是把補間反向,不會卡住。</summary>
        private bool _visible;
        /// <summary>root 目前的 x(補間中的中間值)。</summary>
        private float _slideX = HiddenX;
        /// <summary>名單重建過了,握把要重擺 —— 但**不能在 Rebuild 當下擺**,見 <see cref="Update"/>。</summary>
        private bool _handleDirty;

        /// <summary>開始滑入(true)/ 開始滑出(false) 的**那一刻**通知(不是動畫結束)。
        /// 大廳靠它同步藏/顯示左邊那尊 3D 角色 —— 要跟面板一起動,晚一拍會看到角色從名單底下鑽出來。</summary>
        public System.Action<bool> VisibilityChanged;

        /// <summary>目標狀態;滑動動畫進行中也回目標值(見 <see cref="_visible"/>)。</summary>
        public bool Visible => _visible;

        private static string L(string k) => LocalizationManager.Get(k);
        private static string L(string k, params object[] a) => LocalizationManager.Get(k, a);
        private static Sprite An(string n) => LobbyArt.AnSoloAA(n);

        // ================================================================ 版面

        public void Build(Transform parent)
        {
            _root = UIKit.NewRect(parent, "LobbyUserPanel");
            UIKit.Stretch(_root);

            // 名單底板。官方把欄頭(位置/暱稱)與分頁列的底烤在 friendbold/qinmidu 兩張圖裡 → 只擺圖,不重畫字。
            UIKit.AddSprite(_root, "PanelBg", LobbyArt.An("Lobby0"), BgX, BgY);
            UIKit.AddSprite(_root, "ColHeader", LobbyArt.An("qinmidu"), IntimacyX, IntimacyY);

            BuildTab(0, "TabAll", "Lobby15", "Lobby13", TabAllX);
            BuildTab(1, "TabFriends", "Lobby18", "Lobby16", TabFriendX);
            BuildTab(2, "TabFamily", "Lobby143", "Lobby141", TabFamilyX);
            BuildTab(3, "TabBlacklist", "Lobby21", "Lobby19", TabBlackX);

            // 名單本體。底板已經畫好凹槽 → 捲動區自己不要再上底色。
            _scroll = UIKit.AddVerticalScroll(_root, "UserScroll", out _content, 1f, 0, new Color(0f, 0f, 0f, 0f));
            Place(_scroll.GetComponent<RectTransform>(), ListX, ListY, ListW, ListH);

            // 捲軸握把。🔴 兩個位置條件都是必要的:
            //   1. 掛在 _root 底下(不是外面那層畫布)—— 否則面板收起來/滑出去時,握把會孤零零留在畫面上。
            //   2. 建在 _scroll **之後** = 後面的兄弟畫在上面,不然會被捲動區的 viewport 底蓋掉。
            // 用 AnSoloAA:這張圖在 stage.png 裡緊貼著鄰居,共用圖集會把隔壁的不透明像素拖成白邊。
            // 🔴 **不要**改用 AnSoloCircleAA —— 那是給圓盤鈕的,會把這根膠囊的上下兩端當光暈剪掉。
            _handle = UIKit.AddSprite(_root, "ScrollHandle", An("Lobby12"), HandleX, ListY);
            // ScrollRect 自己動(滾輪、拖曳、慣性)時沒有人會通知我們 → 這條是唯一即時的來源。
            _scroll.onValueChanged.AddListener(_ => PlaceHandle());

            // 🔴 握把要**拉得動**。它只是一張 Image、不是 Unity 的 Scrollbar,沒有人會幫它接拖曳。
            //    上一版用 EventTrigger 沒有用:EventSystem 是在 PointerDown 當下用
            //    ExecuteEvents.GetEventHandler&lt;IBeginDragHandler&gt; 往上找 handler 的,
            //    **EventTrigger 只註冊 Drag 而沒有 BeginDrag 時不會被選成 pointerDrag** → 拖曳事件永遠不會送到它,
            //    只剩滾輪能捲(使用者回報)。改成自己的元件、把 IBeginDragHandler 一起實作就對了。
            _handle.raycastTarget = true;
            _handle.gameObject.AddComponent<DragProxy>().Dragged = DragHandle;

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
        /// 🔴 驅動方式與大廳房間列表**完全不同**:那邊是自己算的整數 row offset,這邊是真的 ScrollRect
        ///    (<c>UIKit.AddVerticalScroll</c> 只給 ScrollRect + Viewport + Content,**沒有** Scrollbar 元件,
        ///    所以 Unity 不會幫我們動任何東西,得自己接)。而且方向相反:
        ///    <c>verticalNormalizedPosition</c> 1 = 最上、0 = 最下 → 要 <c>1 - v</c> 才是「往下走了多少」。
        /// 🔴 內容比視窗短時 Unity 回的 v 不可信(這種情況它算不出比例)→ 自己比高度,直接停在最上面。
        ///    使用者要求握把**永遠顯示**(就算一個人都沒有),所以這裡不做隱藏。
        /// </summary>
        /// <summary>
        /// 拖握把 → 捲名單。<paramref name="dy"/> 是滑鼠這一幀的垂直位移(Unity:往上為正)。
        /// 握把往下拖 = 內容往後捲,所以 <c>verticalNormalizedPosition</c>(1=最上、0=最下)要跟著減。
        /// 軌道可跑的長度是 <c>ListH - HandleH</c>,用它把「移動幾像素」換成「捲了幾成」。
        /// </summary>
        private void DragHandle(float dy)
        {
            if (_scroll == null) return;
            float travel = ListH - HandleH;
            if (travel <= 0f) return;
            _scroll.verticalNormalizedPosition = Mathf.Clamp01(_scroll.verticalNormalizedPosition + dy / travel);
            PlaceHandle();
        }

        private void PlaceHandle()
        {
            if (_handle == null || _scroll == null) return;
            var vp = _scroll.viewport;
            float t = 0f;
            if (_content != null && vp != null && _content.rect.height > vp.rect.height)
                t = Mathf.Clamp01(1f - _scroll.verticalNormalizedPosition);
            _handle.rectTransform.anchoredPosition = new Vector2(HandleX, -(ListY + (ListH - HandleH) * t));
        }

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
        {
            _selfUserId = selfUserId;
            _selfName = selfName ?? "";
            _selfGuild = selfGuild ?? "";
            _users.Clear();
            if (users != null) for (int i = 0; i < users.Count; i++) _users.Add(users[i]);
            if (Visible) Rebuild();
        }

        /// <summary>
        /// 依目前分頁重畫名單。**空的就是一片空白**(沒好友 / 沒家族 / 沒人在線 / 黑名單還沒做,四種都不寫字)——
        /// 使用者明講「那些字都不要寫」。所以黑名單這一頁就是清空後直接 return。
        /// </summary>
        private void Rebuild()
        {
            if (_content == null) return;
            UIKit.Clear(_content);
            _rows.Clear();
            _handleDirty = true;   // 高度變了,握把要重擺(延到下一幀,見 Update)

            if (_tab == Tab.Blacklist) return;

            var owner = ProfileManager.Active;
            for (int i = 0; i < _users.Count; i++)
            {
                var u = _users[i];
                if (!PassesFilter(u, owner)) continue;
                AddRow(u);
            }
        }

        private bool PassesFilter(NetUserListEntry u, UserProfile owner)
        {
            switch (_tab)
            {
                // 好友:名字在本機清單裡(名字**就是**身分,見 FriendList 的註解)。自己不算好友,也不會被列進來。
                case Tab.Friends: return FriendList.IsFriend(owner, u.Name);
                // 家族:與自己同一個家族名。自己沒有家族時這一頁一定是空的(不要變成「列出所有沒家族的人」)。
                case Tab.Family:
                    return _selfGuild.Length > 0 && !string.IsNullOrEmpty(u.Guild)
                           && string.Equals(u.Guild.Trim(), _selfGuild.Trim(), System.StringComparison.OrdinalIgnoreCase);
                default: return true;
            }
        }

        private void AddRow(NetUserListEntry u)
        {
            var row = UIKit.NewRect(_content, "user" + u.UserId);
            row.sizeDelta = new Vector2(ListW, RowH);
            UIKit.Layout(row.gameObject, RowH);

            // 整列的點擊接盤(透明)。🔴 一定要有:Button 需要一個吃射線的 Graphic 才點得到,
            // 而選中底平常是關著的、文字全部 raycastTarget=false → 沒有接盤的話整列點不動。
            var hit = UIKit.AddImage(row, "hit", new Color(0f, 0f, 0f, 0f), raycast: true);
            Place(hit.rectTransform, 0f, 0f, ListW, RowH);

            // 選中框:官方是**一圈黃色外框**(見實機截圖),不是換一張底圖。用四條 1px 的線畫,
            // 這樣不論列多寬都不會被拉伸變形。預設不畫,選到才亮 —— 「添加好友」按的就是這一列。
            var hi = UIKit.AddImage(row, "sel", new Color(0f, 0f, 0f, 0f));
            Place(hi.rectTransform, 0f, 0f, ListW, RowH);
            MakeOutline(hi.rectTransform, SelectedFrameCol);
            hi.gameObject.SetActive(false);

            // 性別小人頭。列表封包只帶得到性別,帶不到穿搭 → 用房卡那張通用剪影(同一張圖,同一個意思:「一個人」)。
            var icon = UIKit.AddSprite(row, "icon", LobbyArt.An("man"), ColIconX, 6f);
            if (icon != null) icon.color = u.Gender == 1 ? new Color(0.65f, 0.82f, 1f) : new Color(1f, 0.72f, 0.88f);

            Label(row, "lv", ColLevelX, ColLevelW, LevelColor, TextAlignmentOptions.MidlineRight)
                .text = u.Level > 0 ? u.Level.ToString() : "";
            Label(row, "where", ColWhereX, ColWhereW, WhereColor, TextAlignmentOptions.Midline)
                .text = u.InLobby ? L("lobby.userlist_in_lobby") : L("lobby.userlist_in_room", u.RoomSeq);

            var name = Label(row, "name", ColNameX, ColNameW, NameColor, TextAlignmentOptions.MidlineLeft);
            name.text = u.Name ?? "";
            name.overflowMode = TextOverflowModes.Ellipsis;   // 名字沒有長度上限,不截會蓋出欄外
            // 自己那一列標粗體 —— 一整排名字裡要一眼找得到自己在哪。
            if (u.UserId == _selfUserId || string.Equals(u.Name, _selfName, System.StringComparison.OrdinalIgnoreCase))
                name.fontStyle = FontStyles.Bold;

            var btn = row.gameObject.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            int uid = u.UserId;
            string uname = u.Name ?? "";
            btn.onClick.AddListener(() => Select(uid, uname));
            UiSfx.AttachClick(btn);

            _rows.Add(new Row { UserId = uid, Name = uname, Highlight = hi });
            if (uid == _selectedUserId && hi != null) hi.gameObject.SetActive(true);
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

        /// <summary>官方那圈選中框的黃(取自實機截圖)。</summary>
        private static readonly Color SelectedFrameCol = new Color32(0xFF, 0xE0, 0x4A, 0xFF);

        /// <summary>在一個 rect 的四邊各畫一條 1px 的線 = 一圈外框(不會因為列寬不同而被拉伸變形)。</summary>
        private static void MakeOutline(RectTransform parent, Color col)
        {
            AddEdge(parent, "T", col, 0f, 1f, 1f, 1f, 0f, 1f);
            AddEdge(parent, "B", col, 0f, 0f, 1f, 0f, 0f, 1f);
            AddEdge(parent, "L", col, 0f, 0f, 0f, 1f, 1f, 0f);
            AddEdge(parent, "R", col, 1f, 0f, 1f, 1f, 1f, 0f);
        }

        private static void AddEdge(RectTransform parent, string name, Color col,
                                    float ax0, float ay0, float ax1, float ay1, float w, float h)
        {
            var img = UIKit.AddImage(parent, name, col);
            var rt = img.rectTransform;
            rt.anchorMin = new Vector2(ax0, ay0);
            rt.anchorMax = new Vector2(ax1, ay1);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.sizeDelta = new Vector2(w, h);   // 0 = 跟著 anchor 撐滿那一邊,1 = 這條線的厚度
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
