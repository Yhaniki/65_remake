using System;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Sdo.Localization;
using Sdo.Settings;
using Sdo.UI.Services;
using Sdo.UI.Util;

namespace Sdo.UI.Screens
{
    /// <summary>
    /// 「玩家資訊」視窗 —— 房間裡右鍵某個人選「玩家信息」時彈出,也用來看自己。
    ///
    /// 這是 **Modal 不是 Screen**:它疊在目前畫面上,不進 <c>FlowManager</c>(切畫面會把背後那張砍掉,
    /// 而看完別人資料要回到原本的房間)。生命週期照 <see cref="JoinRoomModal"/>:
    /// <c>Build</c> 一次、<c>Open</c>/<c>Close</c> 只切 CanvasGroup。
    ///
    /// 美術是官方 <c>UI/PLAYERINFORMATIONDLG</c>,版位逐字取自 <c>PLAYERINFORMATIONDLG_MAN.XML</c> 的
    /// <c>&lt;Window name="WinPlayerInfo"&gt;</c>。官方那個框有五格分頁、3D 角色預覽、天使/魅力/幸運/星座/QQ
    /// 一堆欄位 —— 這個重製版後端只有「名字/家族/等級」與**本機**的累計統計,所以只留兩個分頁,其餘不畫。
    ///
    /// 🔴 **底圖固定用男版**(使用者指定 <c>BASEBOARD_MAN.PNG</c>),不再依性別換皮。以前是「圖換男版、
    ///    座標留女版」,關閉鈕、分頁條、底部那排全部差幾個 px —— 現在圖與座標統一取自男版 XML。
    /// 🔴 官方的按鈕**全部照版位擺出來**,但這個重製版真正接得上的只有「關閉/確定/私聊/加好友↔刪好友」;
    ///    其餘(VIP、手鐲、認證、榮譽、天使、合成書、EC、寵物、寄信、黑名單、買對方裝扮、三顆開關)一律
    ///    **handler 傳 null**,按下去安靜地沒反應。這是使用者明確要求的:不要用 toast 假裝功能存在。
    ///
    /// 🔴 男版底圖是深藍星空,官方那些欄位字是直接寫在有底紋的板子上的。我們的字是動態的、長度不定,
    ///    直接壓在星空上會有讀不清的段落,所以內容一律鋪一層半透明深色底(<see cref="Scrim"/>)再放亮色字。
    /// </summary>
    public sealed class PlayerInfoModal : MonoBehaviour
    {
        // ---------------------------------------------------------------- 版位(PLAYERINFORMATIONDLG_MAN.XML,800×600 左上原點)
        // 🔴 那份 XML 是**整合檔**,十幾個視窗擠在同一個 <Screen> 底下;個人檔案的版位只在
        //    <Window name="WinPlayerInfo" x="0" y="0" w="800" h="600"> 裡面 —— winRightGames / WinCapture /
        //    ZoTask 那些同樣有 close、有一排鈕的視窗與這裡無關,抄錯了會整組偏掉而且看起來很合理。
        private const float BoardX = 93f, BoardY = 56f;          // <Label name="DailogBg" x="93" y="56" background="PlayerInformationDlg0_man.an"/>
        private const float CloseX = 662f, CloseY = 73f;         // <Button name="close" x="662" y="73"/>

        private const float TabX = 333f, TabY = 116f;            // <CheckBox name="playerTabCheck0..3" x="333" y="116"/>(四格疊在同一點)
        // 每一格的可點範圍(相對分頁條左緣)。量自 BaseBoard2_man.png 上未選那四張圖各自的不透明範圍:
        // 5-73 / 73-142 / 143-212 / 213-282(283 之後是官方第五格「星座」的位置,我們不畫)。
        private static readonly float[] TabPillX = { 4f, 73f, 143f, 213f };
        private const float TabPillW = 70f, TabPillH = 39f;
        private const int TabBasic = 0, TabStats = 1, TabCount = 2;

        // 分頁內容板。官方兩頁的板子差 1-2 px(基本頁 PlayerInformationDlg34_man.an 掛在 playerTabWindow0(-1,+6)
        // → 絕對 (335,153) 348×337;技术统计頁 PlayerInformationDlg43_man.an 掛在 playerTabWindow1(+1,+6)
        // → 絕對 (337,152) 347×338)。我們只鋪一層共用的底,所以取兩者的聯集。
        private const float PanelX = 335f, PanelY = 152f;
        private const float PanelW = 349f, PanelH = 339f;

        // 身分區。官方在這塊放 <AvtShow name="AvatarShow" x="105" y="111" w="230" h="391"> 的 3D 角色,
        // 名字/等級疊在它左上角(name 132,129 / level 132,144 —— 這幾個男女版同座標)。我們不做 3D 預覽
        // (要生一整套骨骼+貼圖,開個資料視窗不值得),所以下半塊是刻意留白的,只保留官方那兩行字的位置感。
        private const float IdX = 114f, IdY = 118f, IdW = 214f, IdH = 76f;

        // 底部那一排動作鈕(93×31,確定是 101×37)。官方大多在 y=507,只有 DelFriend / AddEnemy 落在 508。
        private const float BtnY = 507f, DelFriendY = 508f;
        private const float WhisperX = 108f,                     // <CheckBox name="Dialog" x="108" y="507"/>
                            FriendX = 208f,                      // <Button name="AddFriend" x="208" y="507"/> / DelFriend y=508
                            MailX = 308f,                        // <Button name="SendMail" x="308" y="507"/>
                            EnemyX = 408f,                       // <Button name="AddEnemy" x="408" y="508"/>
                            BuyLookX = 508f,                     // <Button name="BuyOtherEquipedButton" x="508" y="507"/>
                            OkX = 608f;                          // <Button name="Confirm" x="608" y="507"/>

        // 左側那一直排功能鈕(官方由上而下)。
        private const float VipX = 296f, VipY = 212f;            // BtnVipSystem
        private const float BangleX = 295f, BangleY = 249f;      // BtnBangleDlg
        private const float CertX = 298f, CertY = 286f;          // BtnCertificateDlg
        private const float HonourX = 296f, HonourY = 318f;      // BtnHonourShow
        private const float AngelX = 298f, AngelY = 353f;        // PlayerAngelButton
        private const float CraftX = 298f, CraftY = 388f;        // hechengshu
        private const float EcX = 298f, EcY = 421f;              // btn_ec
        private const float PetX = 298f, PetY = 455f;            // Showpet

        // 底部三顆開關(105×21)。<CheckBox name="OpenBill/OpenInvite/OpenInfo" y="454"/>
        private const float SwitchY = 454f;
        private const float SwBillX = 351f, SwInviteX = 460f, SwInfoX = 570f;

        // 分頁內容的欄位排版(絕對座標,與版位常數同一個座標系)。內縮量沿用原本那組,整體跟著內容板挪了 (-1,+5)。
        private const float RowX = 351f, RowW = 318f, RowLabelW = 100f;
        private const float BasicRow0Y = 179f, RowStep = 30f, RowH = 20f, RowFont = 13f;
        private const int BasicRowMax = 7;                       // 自己:名稱/性別/家族/等級/M/G/P

        private const float RateRow0Y = 177f, RateStep = 30f, RateFont = 12f;
        private const float RateLabelW = 78f;
        private const float BarX = 84f, BarW = 176f, BarH = 11f, BarDy = 5f;   // 相對 row 左上角
        private const float RateValX = 268f, RateValW = 50f;
        private const int RateRowMax = 6;                        // 命中/Perfect/Cool/Bad/Miss/勝率
        private const float StatsTextRow0Y = 367f;               // 比率之後那三行純文字
        private const int StatsRowMax = 3;                       // 判定數 / 遊玩次數 / 戰績
        // 判定數那一列會塞四組數字(「P 12,345 / C 6,789 / B 123 / M 45」),用基本頁那組字級/欄寬會撐出板外,
        // 所以這三行自己一組:標籤欄窄一點、字級小一號。
        private const float StatsLabelW = 76f, StatsFont = 12f;

        private const float NoteX = 351f, NoteY = 215f, NoteW = 318f, NoteH = 120f, NoteFont = 13f;

        // ---------------------------------------------------------------- 顏色
        private static readonly Color Scrim = new Color(0.10f, 0.06f, 0.16f, 0.62f);
        private static readonly Color BarBack = new Color(0f, 0f, 0f, 0.45f);
        private static readonly Color32 LabelCol = new Color32(0xC9, 0xB6, 0xE8, 255);
        private static readonly Color32 ValueCol = new Color32(0xFF, 0xFF, 0xFF, 255);
        private static readonly Color32 NoteCol = new Color32(0xE6, 0xD8, 0xF0, 255);
        private static readonly Color32 NameFace = new Color32(0xFA, 0xFF, 0x74, 255);   // 官方 name/level 的 0xfffaff74
        private static readonly Color32 NameEdge = new Color32(0x2A, 0x18, 0x38, 255);

        // ---------------------------------------------------------------- 狀態
        private CanvasGroup _cg;
        private RectTransform _window;
        private CanvasGroup _windowCg;
        private WindowAnim _anim;

        private Image[] _tabImg;
        private RectTransform[] _tabBody;
        private int _tab;

        private OutlinedLabel _idName;
        private TextMeshProUGUI _idLevel;

        private TextRow[] _basicRows;
        private RateRow[] _rateRows;
        private TextRow[] _statsRows;
        private TextMeshProUGUI _basicNote, _statsNote;

        private Button _whisperBtn, _friendBtn;
        private Image _friendImg;

        private bool _isSelf;
        private bool _closing;                 // 關閉動畫跑到一半(見 Close)
        private string _targetName = "", _targetId = "";
        private Action<string> _onWhisper;

        /// <summary>
        /// 視窗開著嗎?<c>FrontendApp.AnyModalOpen</c> 拿它去擋房間的 ESC 與聊天欄搶 focus,所以這個值是有責任的。
        ///
        /// 🔴 關閉**動畫跑完之前**它仍然是 true,這是刻意的:ESC 關窗那一幀,RoomScreen.Update 與這裡誰先跑
        ///    是不保證的,若這裡先跑又立刻回報「已關」,同一顆 ESC 會被房間再收一次 → 直接退出房間。
        /// </summary>
        public bool IsOpen => _cg != null && _cg.alpha > 0f && _cg.blocksRaycasts;

        private static string L(string k) => LocalizationManager.Get(k);
        private static string L(string k, params object[] a) => LocalizationManager.Get(k, a);

        // ---------------------------------------------------------------- build

        public void Build(RectTransform parent)
        {
            var root = UIKit.NewRect(parent, "PlayerInfoModal");
            UIKit.Stretch(root);
            _cg = root.gameObject.AddComponent<CanvasGroup>();

            // 半透明黑幕:擋住背後房間的點擊(不然還看得到房間的鈕、按得下去),順便把底下壓暗讓框跳出來。
            var dim = UIKit.AddImage(root, "Dim", new Color(0f, 0f, 0f, 0.5f), true);
            UIKit.Stretch(dim.rectTransform);

            // 除了黑幕以外都掛在 _window 底下 → 開闔動畫(WindowAnim)只轉框、黑幕不跟著轉。
            _window = UIKit.NewRect(root, "Window");
            UIKit.Stretch(_window);
            _window.pivot = new Vector2(0.5f, 0.5f);
            _windowCg = _window.gameObject.AddComponent<CanvasGroup>();
            _anim = _window.gameObject.AddComponent<WindowAnim>();

            UIKit.AddSprite(_window, "Board", PlayerInfoArt.Board, BoardX, BoardY);

            BuildIdentity(_window);
            BuildTabs(_window);
            BuildBasicTab(_tabBody[TabBasic]);
            BuildStatsTab(_tabBody[TabStats]);
            BuildButtons(_window);

            var close = AddOfficialButton(_window, "Close", PlayerInfoArt.CloseN,
                                          PlayerInfoArt.CloseH, PlayerInfoArt.CloseP, CloseX, CloseY, Close);
            UIKit.SetAlphaHit(close.targetGraphic);   // 是顆圓 X,四角透明處不該吃到點擊

            SetVisible(false);
        }

        private void BuildIdentity(RectTransform parent)
        {
            var scrim = UIKit.AddImage(parent, "IdScrim", Scrim);
            Place(scrim.rectTransform, IdX, IdY, IdW, IdH);

            _idName = OutlinedLabel.Create(parent, "IdName", IdX + 10f, IdY + 8f, IdW - 20f, 22f,
                                           15f, NameFace, NameEdge, 1f, true, TextAlignmentOptions.Left);
            _idLevel = UIKit.AddText(parent, "IdLevel", "", 13f, ValueCol, TextAlignmentOptions.Left);
            Place(_idLevel.rectTransform, IdX + 10f, IdY + 38f, IdW - 20f, 20f);
        }

        private void BuildTabs(RectTransform parent)
        {
            // 官方把四格分頁疊在同一個座標,未選的圖只畫自己那一格、其餘透明 —— 所以「一條 tab bar」是把每格的
            // 狀態圖疊起來,而不是一張圖切四段。我們只畫實作得出來的兩格(段位勋章/拼图卡片 沒有後端,畫了也按不動)。
            // 分頁圖自己一個容器:選中那格的圖除了自己那格還畫滿整條底線,要蓋在鄰居上面,而排序是用
            // SetAsLastSibling 做的 —— 直接掛在 parent 上會把它排到「整個視窗」的最上層,語意就錯了。
            var bar = UIKit.NewRect(parent, "TabBar");
            UIKit.Stretch(bar);

            _tabImg = new Image[TabCount];
            _tabBody = new RectTransform[TabCount];
            for (int i = 0; i < TabCount; i++)
            {
                _tabImg[i] = UIKit.AddSprite(bar, "Tab" + i, null, TabX, TabY);
                ApplyTabArt(i, false);
            }

            // 內容板的底(半透明深色)。放在分頁本體之前建 → 排在文字後面。
            var scrim = UIKit.AddImage(parent, "PanelScrim", Scrim);
            Place(scrim.rectTransform, PanelX, PanelY, PanelW, PanelH);

            for (int i = 0; i < TabCount; i++)
            {
                _tabBody[i] = UIKit.NewRect(parent, "TabBody" + i);
                UIKit.Stretch(_tabBody[i]);   // 撐滿 800×600,子物件用絕對座標擺(與版位常數同一個座標系)
            }

            // 點擊區另外做:選中那格的圖是整條寬(見上面),拿圖本身當按鈕會四格互相蓋掉。
            for (int i = 0; i < TabCount; i++)
            {
                int idx = i;
                var hit = UIKit.AddImage(parent, "TabHit" + i, new Color(0f, 0f, 0f, 0f), true);
                Place(hit.rectTransform, TabX + TabPillX[i], TabY, TabPillW, TabPillH);
                var btn = hit.gameObject.AddComponent<Button>();
                btn.targetGraphic = hit;
                btn.transition = Selectable.Transition.None;
                btn.onClick.AddListener(() => ShowTab(idx));
                UiSfx.AttachClick(btn);
            }
        }

        private void BuildBasicTab(RectTransform body)
        {
            _basicRows = new TextRow[BasicRowMax];
            for (int i = 0; i < BasicRowMax; i++)
                _basicRows[i] = TextRow.Create(body, "BasicRow" + i, RowX, BasicRow0Y + i * RowStep,
                                               RowW, RowH, RowLabelW, RowFont);
            _basicNote = MakeNote(body, "BasicNote");
        }

        private void BuildStatsTab(RectTransform body)
        {
            _rateRows = new RateRow[RateRowMax];
            for (int i = 0; i < RateRowMax; i++)
                _rateRows[i] = RateRow.Create(body, "RateRow" + i, RowX, RateRow0Y + i * RateStep, RowW, RowH);
            _statsRows = new TextRow[StatsRowMax];
            for (int i = 0; i < StatsRowMax; i++)
                _statsRows[i] = TextRow.Create(body, "StatRow" + i, RowX, StatsTextRow0Y + i * RowStep,
                                               RowW, RowH, StatsLabelW, StatsFont);
            _statsNote = MakeNote(body, "StatsNote");
        }

        private TextMeshProUGUI MakeNote(RectTransform body, string name)
        {
            var t = UIKit.AddText(body, name, "", NoteFont, NoteCol, TextAlignmentOptions.TopLeft, true);
            Place(t.rectTransform, NoteX, NoteY, NoteW, NoteH);
            t.gameObject.SetActive(false);
            return t;
        }

        private void BuildButtons(RectTransform parent)
        {
            // 左側那一直排:官方這八顆各自開一個獨立系統(VIP / 手鐲 / 認證 / 榮譽 / 天使 / 合成書 / EC / 寵物),
            // 這個重製版一個都沒有 → handler 全是 null,按了安靜地沒反應(理由見 AddOfficialButton)。
            AddOfficialButton(parent, "Vip", PlayerInfoArt.VipN, PlayerInfoArt.VipH, PlayerInfoArt.VipP, VipX, VipY, null);
            AddOfficialButton(parent, "Bangle", PlayerInfoArt.BangleN, PlayerInfoArt.BangleH, null, BangleX, BangleY, null);
            AddOfficialButton(parent, "Certificate", PlayerInfoArt.CertN, PlayerInfoArt.CertH, PlayerInfoArt.CertP, CertX, CertY, null);
            AddOfficialButton(parent, "Honour", PlayerInfoArt.HonourN, PlayerInfoArt.HonourH, PlayerInfoArt.HonourP, HonourX, HonourY, null);
            AddOfficialButton(parent, "Angel", PlayerInfoArt.AngelN, PlayerInfoArt.AngelH, PlayerInfoArt.AngelP, AngelX, AngelY, null);
            AddOfficialButton(parent, "Craft", PlayerInfoArt.CraftN, PlayerInfoArt.CraftH, PlayerInfoArt.CraftP, CraftX, CraftY, null);
            AddOfficialButton(parent, "Ec", PlayerInfoArt.EcN, PlayerInfoArt.EcH, PlayerInfoArt.EcP, EcX, EcY, null);
            AddOfficialButton(parent, "Pet", PlayerInfoArt.PetN, PlayerInfoArt.PetH, PlayerInfoArt.PetP, PetX, PetY, null);

            // 底部三顆開關(帳單/邀請/資料的公開與否)。三態圖都只給同一張,原因見 PlayerInfoArt.SwitchBox。
            AddOfficialButton(parent, "SwitchBill", PlayerInfoArt.SwitchBox, PlayerInfoArt.SwitchBox, PlayerInfoArt.SwitchBox, SwBillX, SwitchY, null);
            AddOfficialButton(parent, "SwitchInvite", PlayerInfoArt.SwitchBox, PlayerInfoArt.SwitchBox, PlayerInfoArt.SwitchBox, SwInviteX, SwitchY, null);
            AddOfficialButton(parent, "SwitchInfo", PlayerInfoArt.SwitchBox, PlayerInfoArt.SwitchBox, PlayerInfoArt.SwitchBox, SwInfoX, SwitchY, null);

            // 底部那一排。寄信/黑名單/買對方裝扮這個重製版都沒有後端 → 一樣 null。
            _whisperBtn = AddOfficialButton(parent, "Whisper", PlayerInfoArt.WhisperN,
                                            PlayerInfoArt.WhisperH, PlayerInfoArt.WhisperP, WhisperX, BtnY, OnWhisper);

            _friendBtn = AddOfficialButton(parent, "Friend", PlayerInfoArt.AddFriendN,
                                           PlayerInfoArt.AddFriendH, PlayerInfoArt.AddFriendP, FriendX, BtnY, OnToggleFriend);
            _friendImg = _friendBtn.targetGraphic as Image;

            AddOfficialButton(parent, "Mail", PlayerInfoArt.MailN, PlayerInfoArt.MailH, PlayerInfoArt.MailP, MailX, BtnY, null);
            AddOfficialButton(parent, "Enemy", PlayerInfoArt.EnemyN, PlayerInfoArt.EnemyH, PlayerInfoArt.EnemyP, EnemyX, DelFriendY, null);
            AddOfficialButton(parent, "BuyLook", PlayerInfoArt.BuyLookN, PlayerInfoArt.BuyLookH, PlayerInfoArt.BuyLookP, BuyLookX, BtnY, null);

            // 確定鈕做的事就是關窗(官方也是),沒有人要事後改它 → 不留欄位。
            AddOfficialButton(parent, "Ok", PlayerInfoArt.OkN, PlayerInfoArt.OkH, PlayerInfoArt.OkP, OkX, BtnY, Close);
        }

        /// <summary>
        /// 建一顆官方版位的三態鈕。<paramref name="onClick"/> 傳 **null** = 這個功能這個重製版沒有,按下去
        /// **安靜地什麼都不做**:不彈 toast、不出聲、也不留一行沒人會看的 log。
        ///
        /// 🔴 這是使用者這一輪明確的要求(「按了沒做的功能就是安靜地沒反應」)。舊的判斷是「靜靜地沒反應會讓人
        ///    以為是壞了,所以要彈個 toast 說明」—— **那個判斷已經被推翻**,不要再加回來。null 的那幾顆連
        ///    <c>UiSfx.AttachClick</c> 都不掛:會出聲就不叫安靜。滑鼠移上去/按下去仍然照官方換圖,那是按鈕本身的
        ///    美術狀態,不是對「功能」的回應。
        /// </summary>
        private static Button AddOfficialButton(RectTransform parent, string name, Sprite normal, Sprite hover,
                                                Sprite pushed, float x, float y, UnityAction onClick)
        {
            var btn = UIKit.AddSpriteButton(parent, name, normal, hover, pushed, x, y);
            if (onClick != null)
            {
                btn.onClick.AddListener(onClick);
                UiSfx.AttachClick(btn);
            }
            return btn;
        }

        // ---------------------------------------------------------------- open / close

        /// <summary>
        /// 看別人。<paramref name="who"/> 只有 <c>Id / DisplayName / Level / Guild</c>(座位快照帶得到的全部)。
        /// <paramref name="onWhisper"/> 收到對方的顯示名字(呼叫端負責把「[名字] 」塞進聊天輸入框)。
        ///
        /// 🔴 <paramref name="gender"/>(0=女 1=男)**現在完全沒有作用**,留著只是因為呼叫端 <c>RoomScreen</c>
        ///    還在傳。以前它用來決定底圖換哪張皮,但版位統一走男版 XML 之後底圖就只有一張(見類別註解);
        ///    而且這個值本來就不可信 —— <c>RoomScreen.SeatGender</c> 查不到時會退回**本機**的性別,拿它當資料
        ///    顯示會把一整批人標成跟自己同一個性別。
        /// </summary>
        public void Open(PlayerProfile who, int gender, Action<string> onWhisper)
        {
            if (who == null || _cg == null) return;   // _cg == null ⇒ 還沒 Build(),沒有東西可以開
            _isSelf = false;
            _targetName = (who.DisplayName ?? "").Trim();
            _targetId = (who.Id ?? "").Trim();
            _onWhisper = onWhisper;

            string level = who.Level > 0
                ? RoomConfig.LevelLabel(who.Level.ToString(CultureInfo.InvariantCulture))
                : "";
            SetIdentity(_targetName, level);
            FillBasicOther(who, level);
            FillStatsOther();

            _whisperBtn.gameObject.SetActive(onWhisper != null);
            _friendBtn.gameObject.SetActive(true);
            RefreshFriendButton();

            ShowTab(TabBasic);
            Reveal();
        }

        /// <summary>看自己。資料全部來自 <see cref="ProfileManager.Active"/>。</summary>
        public void OpenSelf()
        {
            if (_cg == null) return;
            var p = ProfileManager.Active;
            _isSelf = true;
            _targetName = (p.name ?? "").Trim();
            _targetId = (p.id ?? "").Trim();
            _onWhisper = null;

            SetIdentity(_targetName, ProfileFields.LevelLabel(p));
            FillBasicSelf(p);
            FillStatsSelf(p.stats);

            // 看自己不放私聊/加好友 —— 兩顆按了都沒有意義(FriendList.Add 也會擋掉加自己)。
            _whisperBtn.gameObject.SetActive(false);
            _friendBtn.gameObject.SetActive(false);

            ShowTab(TabBasic);
            Reveal();
        }

        public void Close()
        {
            if (_cg == null || _closing) return;   // _closing:動畫期間 IsOpen 還是 true(見它的 doc),
                                                   // 不擋的話按住 ESC 會每幀重跑一次 PlayOut,框就永遠關不掉
            _closing = true;
            if (_anim == null) { SetVisible(false); _onWhisper = null; _closing = false; return; }
            if (_windowCg != null) _windowCg.blocksRaycasts = false;   // 動畫期間不吃點擊
            UiSfx.Play(UiSfx.FrameRound);
            _anim.PlayOut(() => { SetVisible(false); _onWhisper = null; _closing = false; });
        }

        private void Reveal()
        {
            _closing = false;
            SetVisible(true);
            if (_windowCg != null) _windowCg.blocksRaycasts = true;
            if (_anim != null) { _anim.ResetOpen(); _anim.PlayIn(); }
            UiSfx.Play(UiSfx.FrameRound);
        }

        private void SetVisible(bool on)
        {
            if (_cg == null) return;
            _cg.alpha = on ? 1f : 0f;
            _cg.blocksRaycasts = on;
            _cg.interactable = on;
        }

        private void Update()
        {
            if (!IsOpen) return;
            if (Input.GetKeyDown(KeyCode.Escape)) Close();
        }

        // ---------------------------------------------------------------- 內容

        private void SetIdentity(string name, string levelLabel)
        {
            if (_idName != null) _idName.SetText(name);
            if (_idLevel != null) _idLevel.text = levelLabel ?? "";
        }

        private void FillBasicSelf(UserProfile p)
        {
            _basicNote.gameObject.SetActive(false);
            int n = 0;
            _basicRows[n++].Set(L("room.info_name"), _targetName);
            _basicRows[n++].Set(L("room.info_gender"), L(p.gender == 1 ? "room.info_gender_male" : "room.info_gender_female"));
            _basicRows[n++].Set(L("room.info_family"), Or(ProfileFields.FamilyName(p)));
            _basicRows[n++].Set(L("room.info_level"), Or(ProfileFields.LevelLabel(p)));
            _basicRows[n++].Set(L("room.info_coins"), Num(p.wallet.coins));
            _basicRows[n++].Set(L("room.info_points"), Num(p.wallet.points));
            _basicRows[n++].Set(L("room.info_bonus"), Num(p.wallet.bonus));
            HideFrom(_basicRows, n);
        }

        private void FillBasicOther(PlayerProfile who, string levelLabel)
        {
            int n = 0;
            _basicRows[n++].Set(L("room.info_name"), _targetName);
            _basicRows[n++].Set(L("room.info_family"), Or(who.Guild));
            _basicRows[n++].Set(L("room.info_level"), Or(levelLabel));
            // 🔴 沒有「性別」這一列:SeatInfo 沒帶性別,呼叫端傳進來的那個值查不到時會退回**本機**的性別
            //    (見 Open 的 doc),當成資料顯示會把一整批人標成跟自己同一個性別。
            HideFrom(_basicRows, n);

            _basicNote.text = L("room.info_remote_basic");
            _basicNote.gameObject.SetActive(true);
            Place(_basicNote.rectTransform, NoteX, BasicRow0Y + n * RowStep + 12f, NoteW, NoteH);
        }

        private void FillStatsSelf(PlayStats s)
        {
            if (s == null || s.Judged == 0)
            {
                // 一顆音符都還沒判過:全部顯示 0.0% 會讓人以為「我的命中率是 0」。
                HideFrom(_rateRows, 0);
                HideFrom(_statsRows, 0);
                ShowStatsNote(L("room.info_no_stats"));
                return;
            }

            _statsNote.gameObject.SetActive(false);
            int r = 0;
            _rateRows[r++].Set(L("room.info_accuracy"), s.Accuracy);
            _rateRows[r++].Set(L("room.info_perfect"), s.PerfectRate);
            _rateRows[r++].Set(L("room.info_cool"), s.CoolRate);
            _rateRows[r++].Set(L("room.info_bad"), s.BadRate);
            _rateRows[r++].Set(L("room.info_miss"), s.MissRate);
            _rateRows[r++].Set(L("room.info_winrate"), s.WinRate);
            HideFrom(_rateRows, r);

            int n = 0;
            _statsRows[n++].Set(L("room.info_judged"),
                                L("room.info_judged_value", Num(s.perfect), Num(s.cool), Num(s.bad), Num(s.miss)));
            _statsRows[n++].Set(L("room.info_plays"), L("room.info_plays_value", Num(s.plays)));
            _statsRows[n++].Set(L("room.info_record"), L("room.info_record_value", Num(s.wins), Num(s.losses)));
            HideFrom(_statsRows, n);
        }

        /// <summary>
        /// 看別人的「技术统计」頁。
        ///
        /// 🔴 這頁**永遠是一段說明,不是數字**。原因不是還沒做:server 根本沒有玩家統計的持久化 ——
        ///    它把一局的結果廣播出去就丟掉,連線斷了什麼都不剩(見 <see cref="FriendList"/> 的同一段說明:
        ///    好友也是因為這樣才存在自己的 profile.json)。<see cref="PlayStats"/> 是**本機**這台機器的累計,
        ///    只描述「我」。所以這裡絕對不能退回去讀 ProfileManager.Active.stats —— 那會把自己的命中率
        ///    掛上別人的名字,而且看起來完全正常,沒有人會發現。
        /// </summary>
        private void FillStatsOther()
        {
            HideFrom(_rateRows, 0);
            HideFrom(_statsRows, 0);
            ShowStatsNote(L("room.info_remote_stats"));
        }

        private void ShowStatsNote(string text)
        {
            _statsNote.text = text ?? "";
            _statsNote.gameObject.SetActive(true);
            Place(_statsNote.rectTransform, NoteX, NoteY, NoteW, NoteH);
        }

        private void ShowTab(int tab)
        {
            _tab = Mathf.Clamp(tab, 0, TabCount - 1);
            for (int i = 0; i < TabCount; i++)
            {
                ApplyTabArt(i, i == _tab);
                _tabBody[i].gameObject.SetActive(i == _tab);
            }
            // 選中那格的圖除了自己那格還畫滿整條底線,要壓在鄰居上面才不會被隔壁的邊蓋掉(範圍限在 TabBar 容器內)。
            _tabImg[_tab].transform.SetAsLastSibling();
        }

        /// <summary>
        /// 換上第 <paramref name="index"/> 格分頁的「選中/未選」圖並擺好。
        ///
        /// 🔴 尺寸一律跟著圖走(<c>ApplySprite</c> 會把 rect 設成圖的原生大小),**不要寫死 350×39**:男版四格的
        ///    寬高各不相同(未選高 37、選中高 39,選中的第一格還是 356 寬),寫死會把圖拉歪。
        /// 🔴 位置要加 <c>dx</c>:選中的第 2/3 格在官方 .an 裡是負的 x,裁切時夾到 0 之後得往右補回來
        ///    (整段來龍去脈見 <see cref="PlayerInfoArt.TabStrip"/>)。
        /// </summary>
        private void ApplyTabArt(int index, bool selected)
        {
            var sprite = PlayerInfoArt.TabStrip(index, selected, out float dx);
            UIKit.ApplySprite(_tabImg[index], sprite);
            _tabImg[index].rectTransform.anchoredPosition = new Vector2(TabX + dx, -TabY);
        }

        // ---------------------------------------------------------------- 動作鈕

        private void OnWhisper()
        {
            var cb = _onWhisper;
            string name = _targetName;
            Close();                       // 先關窗:私聊要打字,框還在上面會擋住聊天輸入框
            if (cb != null && name.Length > 0) cb(name);
        }

        /// <summary>
        /// 加/刪好友。做的事與座位右鍵選單那兩項完全一樣(<c>RoomScreen.ToggleSeatFriend</c>)。
        ///
        /// 🔴 **這條路不彈 toast**(使用者要求:大廳一律不要 toast)。唯一的回饋是 <see cref="RefreshFriendButton"/>
        ///    把鈕換成另一張圖 —— 而那正是官方的做法:官方就是 AddFriend / DelFriend 兩顆疊在同一格互切,
        ///    鈕上寫著什麼就代表現在按下去會發生什麼。以前這裡與 RoomScreen「連提示文字的 key 都共用」的約定
        ///    已經不成立(<c>RoomScreen.ToggleSeatFriend</c> 那條路仍然會彈),別看到房間有彈就以為這裡漏了。
        /// </summary>
        private void OnToggleFriend()
        {
            if (_isSelf || _targetName.Length == 0) return;
            var me = ProfileManager.Active;
            bool add = !FriendList.IsFriend(me, _targetName);
            // 🔴 存進去的 id 是 server **這次連線**配發的 userId(NetRoomMapping.ToSeatInfo 就是拿它填
            //    PlayerProfile.Id),下次上線會換一個 —— 所以它只是備查,**絕不能拿來比對**;比對一律用名字
            //    (為什麼名字才是身分見 FriendList 的類別註解)。RoomScreen.ToggleSeatFriend 存的也是同一個值。
            bool ok = add
                ? FriendList.Add(me, _targetName, _targetId, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))
                : FriendList.Remove(me, _targetName);
            if (ok) ProfileManager.Save();
            RefreshFriendButton();
        }

        /// <summary>
        /// 把好友鈕切成「加好友」或「刪好友」。官方是兩顆鈕疊在同一格(AddFriend (208,507) / DelFriend (208,508)),
        /// 我們用一顆換圖 —— 所以 y 也要跟著換,不然刪好友那張會比官方高 1px。
        /// </summary>
        private void RefreshFriendButton()
        {
            if (_friendBtn == null) return;
            bool isFriend = _targetName.Length > 0 && FriendList.IsFriend(ProfileManager.Active, _targetName);
            SetSpriteStates(_friendBtn, _friendImg,
                            isFriend ? PlayerInfoArt.DelFriendN : PlayerInfoArt.AddFriendN,
                            isFriend ? PlayerInfoArt.DelFriendH : PlayerInfoArt.AddFriendH,
                            isFriend ? PlayerInfoArt.DelFriendP : PlayerInfoArt.AddFriendP);
            ((RectTransform)_friendBtn.transform).anchoredPosition =
                new Vector2(FriendX, -(isFriend ? DelFriendY : BtnY));
        }

        // ---------------------------------------------------------------- 小工具

        /// <summary>
        /// 換掉一顆 SpriteSwap 鈕的三態。UIKit.AddSpriteButton 只在建立時設一次,而「加好友/刪好友」
        /// 是同一顆鈕在兩種圖之間切,所以要能事後改。
        ///
        /// 🔴 最後那行 <c>overrideSprite = null</c> 不能省:UGUI 的狀態切換(SpriteSwap)是寫進 overrideSprite 的,
        ///    而「按下去」這個動作的順序是 pointerUp(→ 轉成 Highlighted,把**舊的** hover 圖寫進 overrideSprite)
        ///    → pointerClick(才跑到這裡)。不清掉的話,按完「加好友」滑鼠還停在鈕上時,畫面會一直是舊的
        ///    「加好友(hover)」,要把滑鼠移開再移回來才變成「刪好友」—— 看起來像是沒加成功。
        /// </summary>
        private static void SetSpriteStates(Button btn, Image img, Sprite normal, Sprite hover, Sprite pushed)
        {
            if (btn == null) return;
            UIKit.ApplySprite(img, normal);
            var st = btn.spriteState;
            st.highlightedSprite = hover != null ? hover : normal;
            st.pressedSprite = pushed != null ? pushed : (hover != null ? hover : normal);
            st.selectedSprite = normal;
            btn.spriteState = st;
            if (img != null) img.overrideSprite = null;   // 見上面:不清就會停在舊三態的那張圖
        }

        /// <summary>把 rect 擺到 800×600 設計座標的 (x,y)(左上原點、y 向下),大小 (w,h)。</summary>
        private static RectTransform Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            return rt;
        }

        private static void HideFrom(TextRow[] rows, int from)
        {
            for (int i = from; i < rows.Length; i++) rows[i].Hide();
        }

        private static void HideFrom(RateRow[] rows, int from)
        {
            for (int i = from; i < rows.Length; i++) rows[i].Hide();
        }

        /// <summary>空字串顯示成「(無)」而不是留白 —— 留白看起來像是這一列壞掉沒填。</summary>
        private static string Or(string s) => string.IsNullOrEmpty(s) ? L("room.info_none") : s;

        private static string Num(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

        private static string Pct(double v) => v.ToString("0.0", CultureInfo.InvariantCulture) + "%";

        // ---------------------------------------------------------------- 列

        /// <summary>「標籤:值」一列。整列掛在一個 root 上,隱藏時整列一起關掉。</summary>
        private sealed class TextRow
        {
            public RectTransform Root;
            public TextMeshProUGUI Label;
            public TextMeshProUGUI Value;

            public static TextRow Create(RectTransform parent, string name, float x, float y,
                                         float w, float h, float labelW, float font)
            {
                var r = new TextRow();
                r.Root = Place(UIKit.NewRect(parent, name), x, y, w, h);
                r.Label = UIKit.AddText(r.Root, "L", "", font, LabelCol, TextAlignmentOptions.Left);
                Place(r.Label.rectTransform, 0f, 0f, labelW, h);
                r.Value = UIKit.AddText(r.Root, "V", "", font, ValueCol, TextAlignmentOptions.Left);
                Place(r.Value.rectTransform, labelW, 0f, w - labelW, h);
                r.Root.gameObject.SetActive(false);
                return r;
            }

            public void Set(string label, string value)
            {
                Label.text = label ?? "";
                Value.text = value ?? "";
                Root.gameObject.SetActive(true);
            }

            public void Hide() { Root.gameObject.SetActive(false); }
        }

        /// <summary>「標籤 + 長條 + 百分比」一列(技术统计那六行)。長條是官方 ProgressBar 的 forename 圖做 Filled 填充。</summary>
        private sealed class RateRow
        {
            public RectTransform Root;
            public TextMeshProUGUI Label;
            public TextMeshProUGUI Value;
            public Image Fill;

            public static RateRow Create(RectTransform parent, string name, float x, float y, float w, float h)
            {
                var r = new RateRow();
                r.Root = Place(UIKit.NewRect(parent, name), x, y, w, h);

                r.Label = UIKit.AddText(r.Root, "L", "", RateFont, LabelCol, TextAlignmentOptions.Left);
                Place(r.Label.rectTransform, 0f, 0f, RateLabelW, h);

                var back = UIKit.AddImage(r.Root, "BarBack", BarBack);
                Place(back.rectTransform, BarX, BarDy, BarW, BarH);

                r.Fill = UIKit.AddSprite(r.Root, "BarFill", PlayerInfoArt.RateBar, BarX, BarDy);
                Place(r.Fill.rectTransform, BarX, BarDy, BarW, BarH);   // AddSprite 會縮成原圖大小,擺完再改回來
                r.Fill.type = Image.Type.Filled;
                r.Fill.fillMethod = Image.FillMethod.Horizontal;
                r.Fill.fillOrigin = (int)Image.OriginHorizontal.Left;

                r.Value = UIKit.AddText(r.Root, "V", "", RateFont, ValueCol, TextAlignmentOptions.Right);
                Place(r.Value.rectTransform, RateValX, 0f, RateValW, h);

                r.Root.gameObject.SetActive(false);
                return r;
            }

            public void Set(string label, double pct)
            {
                Label.text = label ?? "";
                Value.text = Pct(pct);
                Fill.fillAmount = Mathf.Clamp01((float)pct / 100f);
                Root.gameObject.SetActive(true);
            }

            public void Hide() { Root.gameObject.SetActive(false); }
        }
    }
}
