using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Sdo.Game;
using Sdo.Localization;
using Sdo.Settings;
using Sdo.UI.Core;
using Sdo.UI.Services;
using Sdo.UI.Util;
// 只借名單那一個型別。整包 `using Sdo.Net` 會讓 RoomStatus 變成歧義
// (Sdo.Net 與 Sdo.UI.Services 各有一個,房卡繫結那邊用的是後者)。
using NetUserListEntry = Sdo.Net.NetUserListEntry;

namespace Sdo.UI.Screens
{
    /// <summary>
    /// 大廳 —— 忠實重製官方 <c>CStateCommunityHall</c> 的 <c>STATECOMMUNITYHALL.XML</c>:
    /// 星空底(LobbyBG)、上方頻道名牌、中間**兩欄三列**的房卡、右下一排「創建舞台 / 快速進入 / 全部舞台」、
    /// 下方聊天欄 + 自己的角色資料。座標逐字取自那份 XML(800×600、左上原點、y 向下),
    /// 所以 <c>UIKit.AddSprite(parent, name, sprite, x, y)</c> 直接餵原座標就對位。
    ///
    /// 🔴 **素材是 UI/STATECOMMUNITYHALL,不是 UI/LOBBY。** 兩個資料夾都叫「大廳」、.an 檔名還大量重複
    ///    (Lobby26/28/47/98…),但版面完全不同:UI/LOBBY 是單欄六列的青色版,這一套才是玩家實際看到的
    ///    紫色兩欄版(幾乎整包裁自同一張 STAGE.PNG)。照檔名分不出來,只能看它裁的是哪張圖。
    ///
    /// 🔴 **XML 的 window y 是動畫起點,不是版位**:win2 寫 y=-450、win4 寫 y=200,各自有一組 TransForm
    ///    把它滑到 (0,0) —— 所以裡面那些座標就是最終的絕對座標,不要再加 window 的 y。
    ///
    /// 🔴 **官方有的東西一律照擺,即使這個重製版還沒有那份資料**(使用者要求)。這推翻了本檔早期的兩條原則
    ///    (「畫一個永遠是 0 的欄位比不畫更糟」「按了沒反應的鈕直接不放」):版面對不上官方比欄位是 0 更難認,
    ///    而缺一整排鈕會讓大廳看起來像半成品。所以:
    ///      • 愛慕值 / 金葉子 固定顯示 0(同 <c>WardrobeScreen</c> 的金葉子,那邊也是使用者指定固定 0);
    ///      • 沒做的功能**照官方版位放鈕,按了安靜地什麼都不做** —— handler 傳 null。
    ///
    /// 🔴 **大廳一律不彈 Toast,也不寫聊天區**(使用者要求)。玩家該知道的事只靠畫面本身表達
    ///    (鈕換圖、名單多一列、進不去就是還留在大廳);其餘一律 <c>Debug.Log</c>。
    ///
    /// 🔴 **版面不依 <c>Ctx.Net</c> 分歧。** <c>Build(ctx)</c> 是開機時跑的,那時一定還是單機
    /// (連線由選角色畫面按「登入」才發動),所以在 <see cref="BuildUI"/> 判斷連線只會永遠走到離線那半邊。
    /// 線上/離線的差別全部收在 <see cref="OnShow"/> 之後的資料來源上。
    /// </summary>
    /// <remarks>partial:角色的即時調校面板(F4)另外收在 <c>LobbyScreen.AvatarDebug.cs</c> ——
    /// 那些 OnGUI 與 LocalPrefs 的雜事不該混進這份版位表。</remarks>
    public sealed partial class LobbyScreen : UIScreenBase
    {
        public override ScreenId Id => ScreenId.Lobby;

        // ---------------- STATECOMMUNITYHALL.XML 版位(全部是 800×600 絕對座標) ----------------
        //
        // 🔴 XML 裡 win2 / win4 的 y 是**動畫起點**(win2 y=-450、win4 y=200),各自有一組 TransForm
        //    把它滑到 (0,0) —— 所以裡面那些座標**就是**最終的絕對座標,不要再加 window 的 y。

        // 房卡:兩欄三列。卡片絕對位置直接取自 XML 裡那組裝飾空卡(window "normal" x=1 y=-14,
        // 內含 6 個 Lobby28 標在 (301,78)(530,78)(301,173)(530,173)(301,268)(530,268))——
        // 與六個 room window 自己算出來的落點一致(room0 x=53 y=25、卡片在 window 內 (248,38) → 301,63)。
        // ⚠️ 官方 XML 的 room1 座標與 room0 重複(x 都是 53),照 room2..5 的規律那顯然是右欄的筆誤。
        private const int VisibleRows = 6;
        private const float ColLX = 301f, ColRX = 530f;          // 左欄 / 右欄
        private const float Row0Y = 64f, RowStep = 95f;          // 三列
        private const float CardW = 226f, CardH = 89f;           // Lobby28 / Lobby98

        // 卡片內的元素 —— XML 是相對 room window 的,這裡換算成「相對卡片左上角 (248,38)」。
        private const float NameX = 16f, NameY = 11f, NameW = 190f, NameH = 14f;   // roomname
        private const float SongX = 139f, SongY = 40f, SongW = 76f, SongH = 16f;   // roommusic
        private const float NumX = 8f, NumY = 57f, NumStep = 17f;                  // roomnum:LobbyNum1,3 位(16×20)
        private const float CountX = 95f, SlashX = 107f, CapX = 116f, CountY = 45f;// roomuser / slash / roommaxuser:LobbyNum2
        private const float KeyX = 73f, KeyY = 42f;                                // password(Lobby93 鑰匙)
        // 狀態是**兩層**:Lobby26/27 的圓底(50×50)+ waiting/playing 的綠字牌(63×18)疊在上面。
        private const float StateX = 14f, StateY = 35f;                            // roomstate_waitting / _playing
        private const float BadgeX = 6f, BadgeY = 39f;                             // waiting.an / playing.an
        private const float HeadX = 66f, HeadY = 65f, HeadStep = 19f;              // roomplayer0..5(man.an)
        private const float KeysX = 183f, KeysY = 60f;                             // jianpan(Lobby97)

        // 左側的 3D 角色(官方 AvtShow)。與選角色畫面共用 GenderPreview3D。
        //
        // 🔴 XML 的 AvtShow 是一顆**按鈕**(x=206 y=379,Lobby157/158/159 的小人頭圖示),不是角色的畫框 ——
        //    角色是 3D 直接畫在畫面上、不受任何 UI 框限制。照那顆鈕旁邊的 285×430 擺會小掉一大圈
        //    (實機量到的角色是「頭頂 y≈32、腳底 y≈580」,約 550px 高、身體中線 x≈205)。
        //    這裡改成把 GenderPreview3D 的 2:3 槽位(400×600,與選角色畫面同一組投影)整個攤在左半邊,
        //    角色的落點與大小就由 <see cref="AvatarFillFrac"/> 與 <see cref="ShowAvatar"/> 裡歸零的兩個偏移決定。
        //
        // 🔴 **W:H 必須永遠是 2:3。** <c>GenderPreview3D.SlotW/SlotH</c> 是 const 400/600,同時決定 RT 像素數
        //    **與**釘死的 <c>_cam.aspect</c>。RawImage 的 rect 比例一旦不等於那個 aspect,角色立刻變形
        //    (而且是所有視窗都變形,不只非 4:3 的)。W/H **等比**放大縮小是可以的(= 把渲好的那張 RT 整張
        //    縮放,人與留白一起變、不會被裁),下面現行的 419.21×628.81 就是 400×600 的 1.048 倍;
        //    但**比例不准動** —— 419.21/628.81 一定要還是 0.6667。
        //
        // 落點(數字是**量出來的**,不是算出來的 —— 見 AvatarFillFrac 與 LobbyAvatarFramingTests):
        //   角色高 548、頭頂 y=30、腳底 y=578、身體中線 x=205,對上官方實機。
        // 🔴 AvatarX 用「RT 中心 = 官方身體中線」回推(205 − 400/2 = 5),**不要**照 alpha bounding box 的中心去校:
        //    相機正對角色原點,所以身體中線恆在 RT 正中;bounding box 的中心會隨當下抽到的 idle 姿勢
        //    (手臂張開、抬腳、甩裙擺)左右跳三四十 px —— 照那個調會越調越偏。
        //
        // 🔴 **不要再走「改常數 → 重編 → 進大廳看」那條路**:大廳裡按 <c>F4</c> 有一塊即時調校面板
        //    (editor 限定,見 <c>LobbyScreen.AvatarDebug.cs</c>)—— 拖滑桿當場看落點與大小,滿意了按
        //    「複製 const」就把下面這幾行產生好貼回來。這裡的值只是**預設**:面板調過的會存進 LocalPrefs 蓋掉它。
        //
        // 🔴 **現在這組是使用者用那塊面板調出來的落點,不是上面那些官方量測值**(上面那些留著是為了記住
        //    「怎麼量、踩過哪些坑」,不是現行版位)。相對最早那版(-30,-91,400×600):
        //      • 往左 46、往下 49 → 身體中線 x≈124(原 170)、腳底 y≈460(原 411)——
        //        腳踩進下方紫色面板(y=437)上緣約 23px,就是官方實機那個「鞋子壓在面板上」的樣子;
        //      • 再整張放大 1.048 倍(面板的「縮放」,固定腳底中心 → 中線與腳底不動、只有人變高):
        //        角色高 ≈430px(原 410)、頭頂 y≈30(原 4)。
        //    <see cref="AvatarFillFrac"/> 全程沒動 —— 這一版的「變大」走的是 RT 縮放那條,不是改相機取景。
        private const float AvatarX = -85.95f, AvatarY = -64.32f, AvatarW = 419.21f, AvatarH = 628.81f;

        /// <summary>
        /// 角色佔預覽高度的比例。選角色畫面用 0.68(那邊的框留白多),大廳的角色幾乎頂天立地。
        ///
        /// 🔴 這個值只有在 <see cref="ShowAvatar"/> 把 <c>avatarYOffset</c> 與 <c>verticalBias</c> **一起歸零**
        ///    之後才算得準。那兩個偏移(官方 LOBBYSEL 的 -5 與 +2)會讓角色相對取景窗往下偏 7 個 model unit
        ///    ≈ 0.123×角色像素高 —— 0.9 配上那個偏移時腳底會算到 y=642,**掉出畫面被切掉 36px**
        ///    (使用者回報「人變得太大」的真正成因),而且在 400×600 的槽位裡 fillFrac 上限只到 0.803。
        ///    歸零之後才做得到官方那個 548px。
        ///
        /// 🔴 **fillFrac 不等於「角色佔畫面的比例」,不要照那個直覺算。** <c>FrameTo</c> 是拿
        ///    <c>bodyTop = (頭骨 y − 腳底 y) × (1 + framePadTop)</c> 去填 fillFrac 的,而**實際看得到的人**
        ///    比那個 bodyTop 還高約 9%(髮型高過頭骨、idle 動作會抬腳/甩裙擺)。照「fillFrac = 548/600 = 0.913」
        ///    算下去,實測是角色高 **599px、整個填滿畫布**(頭頂 y=4、腳底 y=603)——正是使用者回報的「太大」。
        ///
        /// 這個值是**量出來的**:0.913 → 實測 599px;0.835 → 571px;0.80 → 546px。
        ///
        /// 🔴 **目標值是直接從官方實機截圖量出來的**(800×630 的視窗截圖,標題列 26px,畫布從 y=26 起):
        ///    髮髻頂 y≈30 → 畫布 y≈4;白鞋底 y≈437 → 畫布 y≈411(正好落在房卡列表框的下緣 410);
        ///    身體中線 x≈170。**高 ≈410px。**
        ///    前三次(599 / 571 / 546)都太大,是因為我拿截圖「目測」而不是逐點量,而且每次只縮 9% 一直在原地打轉。
        ///    這一版連**落點**也一起修:官方那隻是「頂到畫面最上緣、腳落在列表框下緣」,以前整個往下掉了 80px。
        ///    要再調就跑 <c>Assets/Tests/PlayMode/LobbyAvatarFramingTests.cs</c>,它會把實測的
        ///    頭頂/腳底/高度印出來(Debug.Log 前綴 <c>[lobby-avatar]</c>);高度與 fillFrac 是線性的,
        ///    想要 N px 就寫 <c>目前的 fillFrac × N ÷ 目前實測高度</c>。
        /// 要再調就跑 <c>Assets/Tests/PlayMode/LobbyAvatarFramingTests.cs</c>,它會把實際的
        /// 頭頂/腳底/高度/中線印出來(Debug.Log 前綴 <c>[lobby-avatar]</c>),照那個數字換算就好 ——
        /// 別再用幾何推導,那條路已經錯過兩次。
        /// </summary>
        private const float AvatarFillFrac = 0.605f;

        // 「按住拖動轉身」的命中區。跟著角色走 —— F4 面板挪動/縮放角色時,這塊是照「與 AvatarX/Y/W/H 的
        // 相對比例」一起搬的(見 LobbyScreen.AvatarDebug.cs 的 AvDragRel*),所以這裡四個值不要單獨手改:
        // 改了角色卻沒搬熱區,就會變成「人在這、但要去旁邊那塊空氣上按住才轉得動」。
        //
        // 🔴 真正的規則是**這塊矩形不能與三人頭(<see cref="UserListX"/> 206,378)、个人资料(244,378)
        //    那兩顆鈕相交** —— 上一版開到 (90,0,150,430) 就是壓在它們身上,名單鈕整顆按不動(使用者回報)。
        //    舊註解寫「下緣一定要停在 370」是**那時 x 從 90 起**才推得出來的說法,不是通則:
        //    現在這組 x 只到 39.82+157.2 = 197.02,右緣比那兩顆鈕的 206 還左邊 9px → 垂直上就算蓋到
        //    418.8(378 以下)也碰不到它們。左半邊 y<437(下方紫色面板上緣)那片本來就只有星空與角色。
        // 涵蓋的是角色的上半身(拖不到腳),轉身照樣拖得動。
        private const float AvatarDragX = 39.82f, AvatarDragY = 31.05f, AvatarDragW = 157.2f, AvatarDragH = 387.77f;

        // 房間列表底板(NormalBG = LobbyChannelBG,506×364)+ 捲軸
        //
        // 🔴 HandleH 是**握把圖的實際高度**,不是隨便一個數:LOBBY38.AN = stage.png (843,590,14,28) → 14×28。
        //    以前寫 42 → 拉到底時握把底緣停在 y=341,離軌道底 355 差 14px。
        // RailX / RailTop 都**不是** XML 的那組(760,35):XML 給的是 ScrollBarV 整條(25 寬)的框,
        //    而握把要坐進「底板烘死的凹槽」裡。實測 LobbyChannelBG 貼在 (286,46) 之後,
        //    凹槽在絕對 x 760-781(中央深溝 769-772)、y 49-349 —— 所以 14 寬的握把置中是 x=764,
        //    軌道從 y=49 起、可跑 300(349-49)。照 XML 的 35 會讓握把浮在列表框上緣外面(使用者回報「拉桿太高」)。
        private const float ListBgX = 286f, ListBgY = 46f;
        private const float RailX = 764f, RailTop = 49f, RailH = 300f, HandleW = 14f, HandleH = 28f;

        // 左下聊天記錄的捲軸(官方 win4 的 TextList AllChatList,Handle 也是 Lobby12)。
        // 🔴 握把要**壓在溝的正中央**。那條溝是 Lobby53 烤死的,在 stage.png 上實測(見 ChatX 那段的表)
        //    落在絕對 x 434-436 → 溝心 435,14 寬的握把置中 = 435-7 = 428。
        //    以前寫 426 是對著**另一個框**(RecordChatBG 的溝心 430)算的,那張圖現在不貼了。
        private const float ChatRailX = 428f, ChatRailTop = 447f, ChatRailH = 108f;

        // 右下角那一排功能鈕。創建/快速/篩選同一個 y(363);活動查詢與夥伴在 365(官方就差這 2px)。
        private const float ActionY = 363f, SideActionY = 365f;
        private const float ActX = 306f, PartnerX = 373f, CreateX = 526f, QuickX = 615f, FilterX = 703f;

        // 上方(win2):頻道名底圖 + 右上角那排圓鈕。
        //
        // 五顆鈕的 .an 全部裁自 My3dHouseSmall.png 同一列 (y=274),圖案由左到右是:
        //   hall23(x=95) 六角 → hall10(x=0) 放大鏡 → hall13(x=31) NEW筆 → hall16(x=126) 返回箭頭,
        //   另外 hall26(x=62) 是**問號**。
        // 版位對照官方實機截圖:六角(651) / 放大鏡(688) / NEW筆(722) / 返回(759),而問號那顆(723,44)
        // 官方**根本沒畫**(使用者也要求拿掉)→ 這裡不放,設定改從放大鏡那顆的下拉選單進去(見 BuildHallMenu)。
        private const float ChannelBgX = 285f, ChannelBgY = 7f;
        // 🔴 官方 XML 是 288/385,但實機那排字整體要再往右 —— 使用者分兩次比對加了 4px 與 5px,
        //    共 +9 → 297/394。名牌(ChannelBgX=285)不動,只有字往右;兩個標籤是**一起**移的,
        //    它們在名牌上的相對位置(伺服器名 / 頻道號各自置中)才對得上官方。
        private const float ServerX = 301f, ChannelX = 398f, TopLabelY = 9f;
        private const float TopWeddingX = 651f, TopHouseX = 688f, TopRankX = 722f, TopIconY = 8f;
        private const float TopLogoutX = 759f, TopLogoutY = 8f;

        // 放大鏡那顆(TopHouseX)拉開的下拉選單 —— 版位逐字取自官方 POPMENU.XML 的 Formal_Pop_Menu:
        // 五個項目在選單內的 (14, 13/39/65/91/117),每項 135×26,**pushed = normal**(官方只給兩態)。
        // 選單原點是靠右對齊算出來的:項目寬 135、右緣貼齊畫面 → 651;y 讓第一項落在按鈕列正下方 → 27。
        //
        // NEW筆那顆(TopRankX)拉開的是同一個 XML 裡的 Apply_Pop_Menu(舞台/商店/E模式小屋/游樂場)。
        // 官方兩個 PopMenu 的 x/y **完全一樣**(都是 x="-135" y="0")→ 這裡共用 HallMenuX/HallMenuY
        // 與同一組項目間距,只是項目少一列。
        private const float HallMenuX = 651f, HallMenuY = 27f;
        private const float HallMenuItemX = 14f, HallMenuRow0Y = 13f, HallMenuRowStep = 26f;
        // Apply_Pop_Menu 裡那張 New.an 標籤(官方 <Label name="NewLabel" x="22" y="83">),疊在「E模式小屋」上。
        private const float ApplyNewX = 22f, ApplyNewY = 83f;

        // 左下「當前」拉開的頻道選單 —— 逐字取自官方 LOBBYPOPMENU.XML 的 chatmodemenu (21,466)。
        private const float ChatMenuX = 21f, ChatMenuY = 466f;

        // 表情盤(官方 LOBBYPOPMENU.XML 的 expression PopMenu,165×152)。XML 給的是選單自己的 (0,0),
        // 實際位置對齊表情鈕:水平置中(458+16.5−82.5=392)、底邊貼鈕的上緣(566−152=414)。
        private const float ExprMenuX = 392f, ExprMenuY = 414f;

        // 下方(win4)。聊天顯示區官方是可開關的浮動面板(recordchatmode/closerecordchatmode 一對開關鈕),
        // 它的 XML 位置 (21,296) 會壓在第三列房卡上 —— 這裡當常駐聊天區用,所以下移到輸入列正上方。
        // 🔴 聊天區**不貼底框**。Lobby53(下方那一整條 bar)的圖裡就**已經烤好了聊天區的框**,
        //    連右邊的捲軸溝都在。以前又額外貼一張 RecordChatBG 疊上去,兩個框差 5~6px →
        //    左右各兩條線、捲軸溝變三條(使用者回報的「殘影 / shift 的框線」)。
        //    在 stage.png 上量出來的兩個框(畫面絕對座標):
        //        Lobby53 烤死的框: 左 33  右 449  溝 434-436  上 444  下 556
        //        RecordChatBG    : 左 27  右 445  溝 ~433     上 444  下 559
        //    留 Lobby53 那個(官方就是這樣一張整圖),RecordChatBG 從此沒人用。
        //    下面這組座標全部對齊 Lobby53 的框:框內是 x 34-448、y 446-555。
        //    ChatX 不是貼齊框內緣(34)而是 40:框從 27 換成 33 之後,34 只離左框線 1px,
        //    加上 layout 的 2px padding 字也才離框 3px —— 官方那張圖第一個字離框線約 8px。
        //    右緣 40+390=430,停在捲軸溝(434)左邊,不與握把打架。
        private const float ChatX = 40f, ChatY = 446f, ChatW = 390f, ChatH = 110f;
        private const float ChatInputX = 156f, ChatInputY = 570f, ChatInputW = 250f, ChatInputH = 20f;
        private const float ChanX = 23f, ChanY = 570f;            // chatmode「當前」
        private const float RecordChatX = 75f, RecordChatY = 570f;// recordchatmode「聊天記錄」開關
        private const float ExprX = 458f, ExprY = 566f;           // expression 表情
        private const float SendX = 493f, SendY = 566f;           // ChatSendButton
        private const float LoudX = 525f, LoudY = 566f;           // LoudSpeaker 大聲公
        private const float PetX = 558f, PetY = 565f;             // Pet 寵物
        private const float HelpX = 751f, HelpY = 566f;           // help「?」
        private const float UserListX = 206f, UserListY = 378f;   // ListShow / AvtShow(三人頭 ↔ 單人頭,疊同一格)
        private const float DetailX = 244f, DetailY = 378f;       // 个人资料
        private const float ItemsX = 595f, NotesX = 671f, BottomBtnY = 570f;

        // 自己的角色資料(win4 的 char* 標籤;背板已經把「等級/經驗值/G幣/M幣/P幣」與
        // 「超舞戰績/知名度/勝率/愛慕值/金葉子」兩排標題烤進圖裡了 → 每一格只放數值)
        // 🔴 名字這行**只能自己往下移**去貼近下一排:下一排(等級 / 超舞戰績,y=467/470)是照背板 Lobby53
        //    烤死的標題字對位的,動它就會與烤字錯開。官方 XML 是 y=446,與下排之間空了 5px,
        //    使用者要求再貼近 → 下移 3px(名字高 16 → 底邊 465,離下排仍留 2px,不會壓到字)。
        private const float SelfNameX = 492f, SelfNameY = 446f, SelfNameW = 130f, SelfNameH = 16f;
        private const float LevelX = 513f, LevelY = 467f;
        private const float ExpX = 522f, ExpY = 489f, ExpW = 86f, ExpH = 14f;
        // 經驗值的百分比字(<Label name="charexprate" x="522" y="488" w="84" h="10" align="center"
        // valign="bottom" color="0xffffe4da" fontheight="12"/>)—— 官方把它**壓在經驗條上面**,不是擺在旁邊。
        private const float ExpRateX = 522f, ExpRateY = 488f, ExpRateW = 84f, ExpRateH = 10f;
        private const float PointY = 504f, CoinY = 522f, BonusY = 539f, MoneyX = 513f, MoneyW = 128f, StatH = 10f;
        // 🔴 右排那五行的**烤字**(實測 STAGE.PNG 的 Lobby53 那塊)由上而下是
        //    「超舞战绩 / 知名度 / 胜率 / 爱慕值 / 金叶子」。XML 的 label 名字會誤導
        //    (charduanwei「段位」烤的其實是愛慕值、charrank「排名」烤的是金葉子、AUcharperformance 烤的是知名度)——**以底圖為準**。
        //    這裡以前把「命中率」放在知名度那格,是照著一段錯註解做的;命中率在個人資料頁已經有了,直接拿掉。
        private const float RecordX = 674f, RecordY = 470f, PerfW = 90f, PerfH = 10f;
        private const float FameX = 674f, FameY = 487f;            // AUcharperformance → 知名度「LV n (m)」
        private const float WinX = 672f, WinY = 505f;
        private const float LoveX = 672f, LoveY = 521f;            // charduanwei → 愛慕值(固定 0)
        // 🔴 金葉子與愛慕值要**同一個 x**:官方 XML 寫 676/672,但那 4px 差在實機看起來就是沒對齊
        //    (兩行上下相鄰、都是靠左的數字)。統一取 672。
        private const float LeafX = 672f, LeafY = 538f, LeafW = 100f;   // charrank → 金葉子(固定 0)

        // XML 的顏色(0xAARRGGBB)
        private static readonly Color32 RoomNameColor = new Color32(0x82, 0x14, 0x38, 0xff);   // roomname
        // 房名的白邊厚度。官方實機那行字的邊相當厚(整個字看起來是「白描邊的粗體」)——
        // 1.2 太細、看起來只是普通粗體,使用者比對官方後回報「還是不夠粗」。2.2 才有官方那個份量。
        private const float RoomNameEdgePx = 1.5f;
        // 🔴 白邊夠厚之後,**裡面那圈深紅字反而顯得太細**(使用者比對官方後回報)。原因是字重只有 TMP 的
        //    faux-bold —— 執行期建的動態 CJK 圖集沒有真正的粗體字面,而官方那行字的字心明顯比 faux-bold 重。
        //    所以再疊一圈**深紅色**的複本把字心撐開(OutlinedLabel 的 facePx),白邊會跟著外推同樣的量,
        //    看得到的白邊仍然是 2.2 —— 只有字心變粗,外框厚度不變(使用者說外框已經夠厚了)。
        // 🔴 半徑要**幾乎是 0**:16 向的環是往**每個方向**長,0.4 就等於整個字寬多 0.8px —— 12px 的字這樣
        //    「短短屋」那種密筆畫會糊成一團(使用者回報「變成太厚」)。真正需要的只是把 anti-alias 的
        //    半透明邊緣填實一點,所以半徑留 0.01(等於原地疊)、粗細改由下面的份數調。
        private const float RoomNameFacePx = 0.01f;
        // 🔴 字心複本疊**幾份**(可填小數,最後一份用部分不透明度)。粗細真正的旋鈕是「份數」,不是半徑、
        //    也不是整體透明度:半徑接近 0 時每一份都落在同樣的像素上,而每一份只會把字的 anti-alias
        //    邊緣加深(字心本來就不透明)—— 疊 2~3 份就飽和。所以 16 份永遠是「全滿」(facePx 0 與 0.01
        //    之間字重用「跳」的),把 16 份一起調淡則要接近不透明才看得出來(使用者實測 0.1~0.4 沒差)。
        //    1 份 ≈ 邊緣填一半;要更細填 0.5,要更粗填 1.5、2。
        private const float RoomNameFaceCopies = 2.5f;
        private static readonly Color32 SongColor = new Color32(0xed, 0xec, 0xa0, 0xff);       // roommusic
        private static readonly Color32 SelfNameColor = new Color32(0xf2, 0x86, 0x4b, 0xff);   // charname
        // 名字的白描邊(使用者要求)。官方 XML 只給得出顏色與 bold,描邊是那個引擎畫字時自己加的 ——
        // 橘字直接壓在深紫背板上,筆畫邊緣會與底色糊在一起;白邊一圈才跳得出來(同房名 roomname 的處理)。
        // 1.2px:14px 的字用房名那種 2.2 會把「飄」這類密筆畫的字心糊住,用聊天那種 0.7 又看不出有邊。
        private static readonly Color32 SelfNameEdge = new Color32(0xff, 0xff, 0xff, 0xff);
        private const float SelfNameEdgePx = 1.2f;
        private static readonly Color32 StatColor = new Color32(0xff, 0xff, 0xff, 0xff);
        private static readonly Color32 ExpRateColor = new Color32(0xff, 0xe4, 0xda, 0xff);   // charexprate 0xffffe4da

        /// <summary>線上房間列表的輪詢間隔。server 沒有「房間列表變了」的推播,只能自己回頭問。</summary>
        private const float PollSeconds = 4f;

        // 聊天行的描邊(與房間畫面同一組:細髮絲邊、正十字四向 —— 小字太厚會像粗體、也會糊)。
        private static readonly Color32 ChatEdgeCol = new Color32(0x20, 0x14, 0x30, 0xFF);
        private const float ChatEdgePx = 0.7f;
        private const int ChatEdgeDirs = 4;

        // 🔴 聊天字級/行高是**算出來的,不是喜好** —— 使用者指定聊天區一次放**8 行**(上一版是 9 行,
        //    字太小了)。視窗 ChatH=110,VerticalLayoutGroup 的 spacing=1、上下 padding 各 2
        //    (見 BuildBottomPanel 的 AddVerticalScroll 參數),所以算式是:
        //        2(底 pad) + N*ChatLineH + (N-1)*spacing ≤ 110
        //    N=8 → 2 + 8×12.5 + 7 = **109 ≤ 110** ✓,而第 9 行的上緣要 2 + 9×12.5 + 8 = 122.5 > 110,
        //    永遠擠不進來 → 不會出現半截字。(9 行那版是 11px 字;再往前 13px/15px 只裝得下 6 行。)
        //    🔴 改這兩個數字之前先把上面那條算式重算一遍,不然 ChatLineClip 會再度出現「半截字」。
        private const float ChatFontSize = 12.5f;
        private const float ChatLineH = 12.5f;
        // VerticalLayoutGroup 的四邊內距(見 BuildBottomPanel 的 AddVerticalScroll 參數)與由它推出的排字寬 ——
        // 長訊息就是折在這個寬度上,量折了幾行要用同一個數字(見 ChatLine)。
        private const int ChatPad = 2;
        private const float ChatLineWrapW = ChatW - ChatPad * 2;

        // ---------------- 狀態 ----------------

        private readonly RoomRow[] _rows = new RoomRow[VisibleRows];
        private readonly List<RoomInfo> _rooms = new List<RoomInfo>();   // 來源(線上=server 回的;離線=Ctx.Rooms)
        private readonly List<RoomInfo> _view = new List<RoomInfo>();    // 套用「只顯示等待中」之後的
        private int _scroll;

        /// <summary>
        /// 只顯示「等待中」的房間。<b>預設 true</b> —— 官方大廳一進來就不列遊戲中的房,
        /// 要按右下那顆才會把它們也放出來(所以那顆鈕預設寫的是「全部舞台」,見
        /// <see cref="ApplyFilterSprites"/>;使用者提供的實機截圖就是這個狀態)。
        ///
        /// 這也解釋了官方截圖裡門牌 000 之後直接跳 010 的空洞:001..009 不是不存在,
        /// 是那幾間**正在遊戲中**被這個預設篩掉了(或已經關掉、號碼還沒被重用)。
        /// </summary>
        private bool _waitingOnly = true;
        private Scrollbar _roomBar;

        private Button _filterBtn;
        private Image _filterImg;

        private RectTransform _chatContent;
        private TMP_InputField _chatInput;
        private ScrollRect _chatScroll;
        private ChatLineClip _chatClip;
        private Scrollbar _chatBar;           // 聊天記錄的捲軸(官方 AllChatList 的 Handle)
        private Image _chatCaret;             // 自畫的輸入游標(TMP 內建的在這裡畫不出來,見 ConfigureChatInput)
        private readonly Vector3[] _caretCorners = new Vector3[4];   // 餵給 IME 候選視窗的座標暫存(每幀用,不要每次配置)

        // 四個下拉選單(右上角功能選單 / 右上角 NEW筆的舞台選單 / 左下角頻道選單 / 表情盤)。
        // 都是 lazily build、再按一次收起來,而且**互斥** —— 開一個就把其它收掉
        // (照 RoomScreen 的 chatmode ↔ expression 那個模式)。
        private RectTransform _hallMenu, _applyMenu, _chatMenu, _exprMenu;
        // 四個選單各自的觸發鈕 —— 「點外面收選單」要把它們排除在外(見 CloseMenusOnOutsideClick)。
        private Button _hallBtn, _applyBtn, _exprBtn;
        private int _exprPage;
        private Button _chatChannelBtn;
        private Image _chatChannelImg;
        private ChatChannel _chatChannel = ChatChannel.Current;

        // 玩家名單(官方 win3)。開關鈕在下方面板(ListShow ↔ AvtShow 同一格輪換)。
        private LobbyUserPanel _userPanel;
        private Button _userListBtn;
        private Image _userListImg;
        /// <summary>離線時餵給名單的那一列(只有自己)。重複用同一個 List —— 名單每 4 秒刷一次,每次配置會一直跳 GC。</summary>
        private readonly List<NetUserListEntry> _offlineUsers = new List<NetUserListEntry>();

        private OutlinedLabel _selfName;
        private TextMeshProUGUI _selfLevel, _selfWin, _selfFame, _selfRecord, _selfCoins, _selfPoints, _selfBonus;
        private TextMeshProUGUI _selfExpRate;   // 經驗條上面那個百分比(官方 charexprate)
        private Image _expFill;                 // 經驗條的紅色前景(Lobby60,Filled 由左往右)

        // 左側 3D 角色(官方 AvtShow)。與選角色畫面同一套 GenderPreview3D:它自己開一台相機
        // 渲到 RenderTexture,顯示時要把那個 layer 從前端 UI 相機的 cullingMask 遮掉,OnHide 還原。
        private GenderPreview3D _preview;
        private RawImage _previewImg;
        /// <summary>「按住拖動轉身」的透明命中區。角色被 F4 面板挪動/縮放時它要跟著走(不然人搬走了就拖不動),
        /// 而且面板可以把它染紅顯示出來 —— 所以留成欄位,見 <c>LobbyScreen.AvatarDebug.cs</c>。</summary>
        private Image _avatarDrag;
        private Camera _maskedCam;
        private int _savedMask;

        /// <summary>OnShow 當下訂到的那兩份服務。登入/登出會**就地換掉** Ctx.Rooms / Ctx.Chat,
        /// 用 Ctx 的現值去退訂會退到新的那份身上 → 舊的那份永遠留著一條死訂閱。</summary>
        private IRoomService _subRooms;
        private IChatService _subChat;
        private bool _subscribed;

        /// <summary>房間列表請求的世代。回呼可能在離開大廳(或登出)之後才回來 —— 那份資料屬於上一次的畫面。</summary>
        private int _listGen;
        private float _nextPoll;

        private static string L(string k) => LocalizationManager.Get(k);
        private static Sprite An(string n) => LobbyArt.An(n);

        // ================================================================ 版面

        protected override void BuildUI()
        {
            // 官方的大廳底是一張 800×600 的星空圖(LobbyBG)。缺圖時退回不透明底色 ——
            // 沒有它的話 UI 之間的空隙會直接看到上一個畫面的殘影。
            var bg = An("LobbyBG");
            if (bg != null) UIKit.AddSprite(Root, "Bg", bg, 0f, 0f);
            else UIKit.Stretch(UIKit.AddImage(Root, "Bg", UITheme.Bg).rectTransform);

            BuildRoomList();     // 底板 + 兩欄三列房卡
            BuildTopBar();
            BuildActionBar();
            BuildBottomPanel();

            // 左側 3D 角色的畫布(貼圖在 OnShow 接上 —— RenderTexture 那時才存在)。
            // 🔴 加在 2D UI **之後** = 疊在它們之上,因為官方的角色是直接畫在畫面上的 3D:
            //    實機那雙鞋子明顯壓在下方紫色面板的上緣。放在最底層的話腳會被面板切掉半截。
            //    RawImage 不吃射線(見 AddRaw),所以蓋過去也不會擋住底下那些鈕。
            _previewImg = AddRaw("AvatarView", AvatarX, AvatarY, AvatarW, AvatarH);
            _previewImg.color = new Color(1f, 1f, 1f, 0f);   // 還沒有 RT 之前不要畫一塊白

            // 在角色身上按住拖動 → 轉身 / 抬頭(與商城左側那隻同一組官方參數,見 GenderPreview3D.Orbit)。
            // 🔴 命中區**不能**用 AvatarView 本身:那張 RawImage 是 400×600、右緣蓋到 x=370,
            //    會把房卡列表(x 從 286 起)整片吃掉。這裡另外開一塊只涵蓋角色的透明區
            //    (實測角色橫向落在 x≈110-230、縱向 0-420),留一點餘裕又不碰到房卡。
            _avatarDrag = UIKit.AddImage(Root, "AvatarDrag", new Color(0f, 0f, 0f, 0f), raycast: true);
            PlaceTopLeft(_avatarDrag.rectTransform, AvatarDragX, AvatarDragY, AvatarDragW, AvatarDragH);
            var trig = _avatarDrag.gameObject.AddComponent<EventTrigger>();
            var entry = new EventTrigger.Entry { eventID = EventTriggerType.Drag };
            entry.callback.AddListener(ev =>
            {
                if (_preview != null && ev is PointerEventData p) _preview.Orbit(p.delta);
            });

            // 角色的版位改由調校值決定(存在 LocalPrefs,預設就是上面那組常數)——
            // F4 面板調完就是這個畫面看到的樣子,見 LobbyScreen.AvatarDebug.cs。
            ApplyAvatarTuning();
            trig.triggers.Add(entry);

            // 捲軸(在角色之後 —— 兩者不重疊,只是維持「列表零件疊在最上」)。
            // 走 AnSoloAA 而不是共用圖集:握把在 stage.png 裡左邊 x=840-842 是完全不透明的鄰居,
            // 共用圖集取樣會把那片拖進邊緣變成白邊(見 SpriteBtn 的註解)。
            //
            // 🔴 用 Unity 內建的 Scrollbar,不要再自己接拖曳:自畫 Image + 自訂拖曳元件連續三輪都被回報
            //    「滑鼠左鍵拉不動」。Scrollbar 連「點軌道跳到那個位置」都是內建的(使用者後來也要這個)。
            _roomBar = FixedScrollbar.Create(Root, "RoomScrollbar", LobbyArt.AnSoloAA("Lobby38"),
                                             RailX, RailTop, HandleW, HandleH, RailH);
            // 🔴 房間列表是**整數分頁**(一次捲一列),不是連續捲動,所以不接 ScrollRect ——
            //    把 Scrollbar 的 0..1 換算成第幾列。BottomToTop 的 1 = 最上 = 第 0 列。
            _roomBar.onValueChanged.AddListener(OnRoomBarChanged);

            // 玩家名單(官方 win3)最後建 = 疊在最上面,展開時蓋住角色與左半邊 —— 官方就是這樣。
            BuildUserPanel();
        }

        // ---- 上方:頻道名 + 右上角圓鈕 ----

        private void BuildTopBar()
        {
            // 頻道名牌(ChannelName.an,198×30)。官方在上面疊「伺服器名」與「頻道號」兩個置中標籤。
            UIKit.AddSprite(Root, "ChannelPlate", An("ChannelName"), ChannelBgX, ChannelBgY);

            // server / channel:官方是「浓情蜜意 / 沈阳」那種分區名,我們只有一台伺服器一個頻道
            // → 沿用房間畫面同一組字,兩邊講的是同一件事。
            var server = Label(Root, "Server", ServerX, TopLabelY, 72f, 28f, 13f, StatColor, TextAlignmentOptions.Midline);
            server.text = RoomLabels.ServerName(1);
            var channel = Label(Root, "Channel", ChannelX, TopLabelY, 90f, 28f, 13f, StatColor, TextAlignmentOptions.Midline);
            channel.text = RoomLabels.Channel(1);

            // 右上角那排圓鈕(版位與圖案的對照見上方常數區)。實機還藏著拼圖/彩虹/BOSS/權益那幾顆
            // (要開活動才亮),不放。
            //
            // 🔴 六角這個重製版沒有對應功能 → 鈕照擺、**按了安靜地什麼都不做**(handler 傳 null)。
            //    以前這裡會彈一句「這個功能還沒做」的 Toast,使用者要求大廳一律不要 Toast。
            TopIcon("Wedding", "hall23", "hall24", "hall25", TopWeddingX, null);
            // 放大鏡 = 官方那顆拉開功能選單的鈕(家族/奖励兑换/情侣密友证/排行榜/设置),見 BuildHallMenu。
            _hallBtn = TopIcon("MyHouse", "hall10", "hall11", "hall12", TopHouseX, ToggleHallMenu);
            // NEW筆 = 官方 Apply_Pop_Menu 那顆(舞台/商店/E模式小屋/游乐场),見 BuildApplyMenu。
            _applyBtn = TopIcon("Rank", "hall13", "hall14", "hall15", TopRankX, ToggleApplyMenu);

            // returnlubbysel(回頻道選擇)= 我們的「登出」:斷線退回單機並回選角色畫面。
            TopIcon("Logout", "hall16", "hall17", "hall18", TopLogoutX, OnLogout);
        }

        // ---- 右上角兩顆鈕拉開的下拉選單(官方 POPMENU.XML 的 Formal_Pop_Menu / Apply_Pop_Menu) ----

        /// <summary>放大鏡那顆的五個項目({normal, hover})。
        /// 滑過那張**不直接用**,是與 normal 合成的(見 <see cref="LobbyArt.AnSoloHover"/>):只取黃字/黃圖示/三角,
        /// 底不動 —— 使用者要求「底圖不要變色,但三角形和字變黃還是要有」。
        /// 兩態的底再一起壓成同一片粉紫(<see cref="LobbyArt.AnSoloFlatBg"/>):官方五張切的是**一整片垂直漸層**,
        /// 照原樣疊起來最後一條會透到發黑,看起來就像那條被選中染深了(見 <see cref="LobbyArt.PopMenuBg"/>)。
        /// 只有「设置」接得上東西(與房間同一個 OptionDlg);其餘四項是官方有、這裡還沒做的功能 → 按了只收選單。</summary>
        private static readonly string[,] HallMenuItems =
        {
            { "FamilyPopMenu1",  "FamilyPopMenu2"  },   // 家族
            { "ChangePopMenu1",  "ChangePopMenu2"  },   // 奖励兑换
            { "WeddingPopMenu1", "WeddingPopMenu2" },   // 情侣密友证
            { "RankPopMenu1",    "RankPopMenu2"    },   // 排行榜
            { "SetPopMenu1",     "SetPopMenu2"     },   // 设置
        };

        /// <summary>NEW筆那顆的四個項目(官方 Apply_Pop_Menu),排法同 <see cref="HallMenuItems"/>。
        /// 「商店」接到商城(<see cref="Nav.OpenShop"/>);其餘三項這裡還沒做 → 按了只收選單。</summary>
        private static readonly string[,] ApplyMenuItems =
        {
            { "StagePopMenu1",    "StagePopMenu2"    },   // 舞台
            { "ShoppingPopMenu1", "ShoppingPopMenu2" },   // 商店 → 商城
            { "HousePopMenu1",    "HousePopMenu2"    },   // E模式小屋
            { "PlayingPopMenu1",  "PlayingPopMenu2"  },   // 游乐场
        };

        private void ToggleHallMenu()
        {
            if (_hallMenu == null) BuildHallMenu();
            bool show = !_hallMenu.gameObject.activeSelf;
            HideApplyMenu();         // 四個選單互斥
            HideChatMenu();
            HideExpressionMenu();
            _hallMenu.gameObject.SetActive(show);
        }

        private void HideHallMenu()
        {
            if (_hallMenu != null) _hallMenu.gameObject.SetActive(false);
        }

        private void ToggleApplyMenu()
        {
            if (_applyMenu == null) BuildApplyMenu();
            bool show = !_applyMenu.gameObject.activeSelf;
            HideHallMenu();          // 四個選單互斥
            HideChatMenu();
            HideExpressionMenu();
            _applyMenu.gameObject.SetActive(show);
        }

        private void HideApplyMenu()
        {
            if (_applyMenu != null) _applyMenu.gameObject.SetActive(false);
        }

        private void BuildHallMenu()
        {
            // 最後一項是「设置」→ 開房間那個 OptionDlg;其餘四項按了只把選單收起來(沒有功能)。
            _hallMenu = BuildPopMenu("hallmenu", HallMenuItems, i =>
            {
                HideHallMenu();
                if (i == HallMenuItems.GetLength(0) - 1) Nav.OpenSettings?.Invoke();
            });
        }

        private void BuildApplyMenu()
        {
            // 第二項是「商店」→ 商城(與房間衣櫥旁那顆同一個 ShopScreen);其餘三項按了只把選單收起來。
            _applyMenu = BuildPopMenu("applymenu", ApplyMenuItems, i =>
            {
                HideApplyMenu();
                if (i == 1) Nav.OpenShop?.Invoke();
            });
            // 官方在「E模式小屋」那條上疊一張 New.an(<Label name="NewLabel" x="22" y="83">)。
            // raycast 預設關掉 → 不會擋住底下那條的點擊。
            UIKit.AddSprite(_applyMenu, "NewLabel", LobbyArt.AnSolo("New"), ApplyNewX, ApplyNewY);
        }

        /// <summary>
        /// 官方 PopMenu 的共用建構:一疊 135×26 的項目圖,**沒有背板**(XML 寫 background="empty.an")——
        /// 畫面上那片「整片粉色選單框」其實就是這些條無縫疊起來的效果。
        ///
        /// 🔴 走 <see cref="LobbyArt.AnSolo"/> 而**不是** AnSoloAA:AnSoloAA 會把外圈的透明/低 alpha 邊裁掉
        ///    → 每條變窄一點,疊起來就出現一條條裂縫、整體還會位移(使用者回報「沒把官方底圖做出來」)。
        ///    AnSolo 是 pad:0 的自貼圖裁切,尺寸與原圖完全一致 → 條與條才接得起來。
        ///
        /// 滑過態不是官方那張 hover 圖,而是 <see cref="LobbyArt.AnSoloHover"/> 合出來的
        /// 「normal 的底 + hover 的黃字/黃圖示/三角」。pushed 也用它:滑鼠一定在項目上,按下去再閃回白字很跳。
        ///
        /// 兩態最後都再過一次 <see cref="LobbyArt.AnSoloFlatBg"/>:官方五張切自一整片垂直漸層(alpha 140→40、
        /// 粉紅→紫),沒有背板擋著就會透出背後的大廳畫面 —— 最下面那條深到像是「被選中變色」。壓成同一片底之後
        /// 五條長得一模一樣,滑過只剩字/圖示/三角變黃。
        /// </summary>
        private RectTransform BuildPopMenu(string name, string[,] items, System.Action<int> onPick)
        {
            var menu = UIKit.NewRect(Root, name);
            int rows = items.GetLength(0);
            PlaceTopLeft(menu, HallMenuX, HallMenuY,
                         HallMenuItemX + 135f, HallMenuRow0Y + rows * HallMenuRowStep);

            for (int i = 0; i < rows; i++)
            {
                var art = LobbyArt.AnSoloFlatBg(items[i, 0]);
                var hov = LobbyArt.AnSoloHoverFlatBg(items[i, 0], items[i, 1]);
                var b = UIKit.AddSpriteButton(menu, name + i, art, hov, hov,
                                              HallMenuItemX, HallMenuRow0Y + i * HallMenuRowStep);
                UiHoverSfx.Attach(b, UiSfx.Menufloat);
                UiSfx.AttachClick(b);
                int idx = i;
                b.onClick.AddListener(() => onPick(idx));
            }
            menu.gameObject.SetActive(false);
            return menu;
        }

        /// <summary>
        /// 點到選單以外的地方 → 把展開中的下拉選單收掉(官方就是這個行為)。四個選單(放大鏡/NEW筆/頻道/表情)一起管。
        ///
        /// 🔴 各自的**觸發鈕要排除**:那幾顆的 onClick 是 toggle,這裡若先收掉,同一次點擊接著跑的 onClick
        ///    會立刻再打開一次 → 點鈕永遠關不掉自己的選單。
        ///
        /// 用矩形判定而不是「蓋一張全螢幕 overlay 吃點擊」:點外面那一下**還是要傳到底下的鈕**
        /// (點「創建舞台」= 收選單 + 開創房視窗,不必點兩次)。
        /// </summary>
        private void CloseMenusOnOutsideClick()
        {
            if (!Input.GetMouseButtonDown(0) && !Input.GetMouseButtonDown(1)) return;

            // 世界空間畫布要把相機餵給 RectangleContainsScreenPoint(與 UpdateChatCaret 同一個取法)。
            var canvas = Root != null ? Root.GetComponentInParent<Canvas>() : null;
            Camera cam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay) ? canvas.worldCamera : null;
            Vector2 p = Input.mousePosition;

            CloseIfOutside(_hallMenu, _hallBtn, p, cam, HideHallMenu);
            CloseIfOutside(_applyMenu, _applyBtn, p, cam, HideApplyMenu);
            CloseIfOutside(_chatMenu, _chatChannelBtn, p, cam, HideChatMenu);
            CloseIfOutside(_exprMenu, _exprBtn, p, cam, HideExpressionMenu);
        }

        private static void CloseIfOutside(RectTransform menu, Component trigger, Vector2 screenPos, Camera cam,
                                           System.Action hide)
        {
            if (menu == null || !menu.gameObject.activeSelf) return;
            if (RectTransformUtility.RectangleContainsScreenPoint(menu, screenPos, cam)) return;
            var t = trigger != null ? trigger.transform as RectTransform : null;
            if (t != null && RectTransformUtility.RectangleContainsScreenPoint(t, screenPos, cam)) return;
            hide();
        }

        /// <summary>
        /// ESC 逐層退出:選單(放大鏡/NEW筆/頻道/表情)開著 → 先收選單;打字框有草稿(或正在 IME 組字) → 只清草稿;
        /// 都不是 → 等同右上那顆「登出」鈕,退回選男女(見 <see cref="OnLogout"/>)。
        ///
        /// 🔴 **不能像房間那樣拿「輸入框有 focus」當守門** —— 大廳的打字框在送出訊息／換頻道／點人名之後
        ///    都會自己 ActivateInputField 搶回 focus(見 SendChat / SetChatChannel),等於幾乎永遠是 focused,
        ///    以 focus 守門的話 ESC 一輩子不會有反應。這裡改看「草稿是不是空的」:有字先清、空了才退畫面。
        ///
        /// modal(商城/儲物櫃/設定/玩家資訊/輸入房號/創建舞台/房間信息)疊在上面時整條讓路 —— ESC 屬於最上層那個視窗。
        /// </summary>
        private void HandleEscape()
        {
            if (!Input.GetKeyDown(KeyCode.Escape)) return;
            if (ScreenTransition.Busy) return;
            if (Ctx != null && Ctx.Flow != null && Ctx.Flow.Current != ScreenId.Lobby) return;
            if (FrontendApp.Instance != null && FrontendApp.Instance.AnyModalOpen) return;

            if (AnyMenuOpen())
            {
                HideHallMenu();
                HideApplyMenu();
                HideChatMenu();
                HideExpressionMenu();
                return;
            }

            // TMP_InputField 自己也吃 ESC(取消編輯 → 把字還原成進入編輯前那份),所以草稿要由我們**明確**清成空字串,
            // 否則它的還原會跟這裡打架,看起來像「按了 ESC 字還在」。清完把 focus 拿回來,接著打不用再點一次。
            bool composing = !string.IsNullOrEmpty(Input.compositionString);
            if (composing || (_chatInput != null && !string.IsNullOrEmpty(_chatInput.text)))
            {
                if (_chatInput != null)
                {
                    _chatInput.text = "";
                    _chatInput.ActivateInputField();
                }
                return;
            }

            OnLogout();
        }

        private static bool MenuOpen(RectTransform menu) => menu != null && menu.gameObject.activeSelf;

        private bool AnyMenuOpen()
            => MenuOpen(_hallMenu) || MenuOpen(_applyMenu) || MenuOpen(_chatMenu) || MenuOpen(_exprMenu);

        /// <summary>右上角那排 34px 圓盤鈕:圓形去白邊 + 命中判定貼齊可見圓(透明四角不吃點擊)。</summary>
        private Button TopIcon(string name, string normal, string hover, string pushed, float x,
                               UnityEngine.Events.UnityAction onClick)
        {
            var b = SpriteBtn(name, normal, hover, pushed, x, TopIconY, onClick, circle: true);
            UIKit.SetAlphaHit(b.targetGraphic);
            return b;
        }

        // ---- 右下角那一排功能鈕 ----

        private void BuildActionBar()
        {
            // 活動查詢(actandprize)與夥伴(partner):這個重製版兩個系統都沒有 → 鈕照官方版位擺,
            // 按了安靜地什麼都不做。版位是官方的(306,365)/(373,365),之後真的做出來只要換掉 handler。
            SpriteBtn("ActAndPrize", "huodong1", "huodong2", "huodong3", ActX, SideActionY, null);
            SpriteBtn("Partner", "partner0", "partner1", "partner2", PartnerX, SideActionY, null);

            // 創建舞台 —— 大廳的主要動作。素材沒複製到時整個大廳就廢了,
            // 所以額外給一個純色 + 文字的 fallback(其餘的鈕缺圖就讓它缺)。
            var create = SpriteBtn("CreateRoom", "Lobby47", "Lobby48", "Lobby81", CreateX, ActionY, OnCreate);
            if (An("Lobby47") == null) FallbackButtonSkin(create, "lobby.create_room", 81f, 40f);

            SpriteBtn("QuickJoin", "Lobby49", "Lobby50", "Lobby82", QuickX, ActionY, OnQuickJoin);

            // 等待舞台 / 全部舞台:官方是兩個疊在同一格(x=703)的 CheckBox,
            // 這裡用一顆鈕換圖表達同一件事。
            _filterBtn = SpriteBtn("RoomFilter", "Lobby51", "Lobby52", "Lobby83", FilterX, ActionY, OnToggleFilter);
            _filterImg = _filterBtn.targetGraphic as Image;
            ApplyFilterSprites();
        }

        // ---- 六列房卡 ----

        private void BuildRoomList()
        {
            var listRoot = UIKit.NewRect(Root, "RoomList");
            UIKit.Stretch(listRoot);

            // 列表底板(NormalBG = LobbyChannelBG,506×364)。官方在 XML 裡把它排在房卡**之後**
            // = 畫在房卡後面,所以這裡先加。
            UIKit.AddSprite(listRoot, "ListBg", An("LobbyChannelBG"), ListBgX, ListBgY);

            // 整片列表區的透明接盤:滾輪事件會沿著階層往上冒泡,所以掛在這裡的話,
            // 游標壓在任何一張卡上滾都收得到;而卡與卡之間的縫也不會漏掉。
            var catcher = UIKit.AddImage(listRoot, "WheelCatcher", new Color(0f, 0f, 0f, 0f), raycast: true);
            var crt = catcher.rectTransform;
            crt.anchorMin = crt.anchorMax = new Vector2(0f, 1f);
            crt.pivot = new Vector2(0f, 1f);
            crt.sizeDelta = new Vector2(ColRX - ColLX + CardW, 3f * RowStep);
            crt.anchoredPosition = new Vector2(ColLX, -Row0Y);

            var wheel = listRoot.gameObject.AddComponent<WheelScroll>();
            wheel.Scrolled = OnWheel;

            for (int i = 0; i < VisibleRows; i++) _rows[i] = BuildRow(listRoot, i);
        }

        /// <summary>第 <paramref name="index"/> 格房卡的絕對位置。**兩欄三列**:偶數在左欄、奇數在右欄。</summary>
        private static Vector2 CardPos(int index)
        {
            float x = (index % 2 == 0) ? ColLX : ColRX;
            float y = Row0Y + (index / 2) * RowStep;
            return new Vector2(x, y);
        }

        private RoomRow BuildRow(Transform parent, int index)
        {
            var pos = CardPos(index);

            var root = UIKit.NewRect(parent, "Room" + index);
            root.anchorMin = root.anchorMax = new Vector2(0f, 1f);
            root.pivot = new Vector2(0f, 1f);
            root.sizeDelta = new Vector2(CardW, CardH);
            root.anchoredPosition = new Vector2(pos.x, -pos.y);

            var row = new RoomRow();

            // 卡片本體是**單張** 226×89(Lobby98 空房 / Lobby28 有人),不像另一套是左右兩張拼的。
            row.Card = UIKit.AddSprite(root, "Card", An("Lobby98"), 0f, 0f, raycast: true);

            // 狀態圓底:等待(Lobby26)/ 遊戲中(Lobby27)/ 空房(LobbyRoomNone),三張疊同一格。
            row.State = UIKit.AddSprite(root, "State", null, StateX, StateY);
            // 疊在圓底上的 WAITING / PLAYING 綠字牌(官方是分開的兩個 Label)。
            row.Badge = UIKit.AddSprite(root, "Badge", null, BadgeX, BadgeY);

            row.Digits = new Image[3];
            for (int d = 0; d < 3; d++)
                row.Digits[d] = UIKit.AddSprite(root, "Num" + d, null, NumX + d * NumStep, NumY);

            // 🔴 房名是**置中 + 白色描邊**(照官方實機):XML 只寫 color=0xff821438 與 bold,
            //    但那個深紅字直接壓在粉紫標題列上根本讀不清 —— 官方實機那行字有一圈白邊、而且置中。
            //    用 OutlinedLabel(與房間畫面的頭上名字同一套描邊)才做得出來。
            row.Name = OutlinedLabel.Create(root, "Name", NameX, NameY, NameW, NameH, 12f,
                                            RoomNameColor, Color.white, RoomNameEdgePx, true,
                                            TextAlignmentOptions.Midline, facePx: RoomNameFacePx,
                                            faceCopies: RoomNameFaceCopies);
            // 房名是玩家自訂的,長的話會整條蓋過右邊的圖示 → 截斷加省略號(官方的欄寬也是硬邊界)。
            // 🔴 走 SetOverflow(**所有複本一起**)而不是只設 Face:只截 face 的話,描邊與字心那兩圈複本
            //    還是會把 face 已經切掉的字畫出來 —— 變成一圈沒有臉的框漏在欄位外面(見 SetOverflow 的註解)。
            row.Name.SetOverflow(TextOverflowModes.Ellipsis);

            row.CountD = UIKit.AddSprite(root, "Count", null, CountX, CountY);
            row.Slash = UIKit.AddSprite(root, "Slash", An("slash"), SlashX, CountY);
            row.CapD = UIKit.AddSprite(root, "Cap", null, CapX, CountY);

            // 密碼房的鑰匙(password = Lobby93)。這個重製版還沒有房間密碼 → 永遠隱藏,
            // 但位置先擺好,之後接上密碼房只要把它 SetActive(true)。
            row.Key = UIKit.AddSprite(root, "Key", null, KeyX, KeyY);

            row.Heads = new Image[6];
            for (int h = 0; h < 6; h++)
                row.Heads[h] = UIKit.AddSprite(root, "Head" + h, null, HeadX + h * HeadStep, HeadY);

            // 官方 XML 把這格叫 roommusic,但實機顯示的是**遊戲模式**(見 Bind)。
            row.Song = Label(root, "Song", SongX, SongY, SongW, SongH, 11f,
                             SongColor, TextAlignmentOptions.MidlineLeft);
            row.Song.overflowMode = TextOverflowModes.Ellipsis;

            // 鍵盤圖示:這個重製版只有鍵盤能玩,所以它永遠成立(不是「沒資料的裝飾」)。
            row.Keyboard = UIKit.AddSprite(root, "Keys", null, KeysX, KeysY);

            // 點擊/hover 都掛在 root 上 —— UGUI 的 click 與 pointerEnter 都會從命中的子物件往上找,
            // 所以一顆 Button 就能涵蓋左右兩張圖,而 hover 換圖也能兩張一起換(分開掛會出現「只亮一半」)。
            row.Btn = root.gameObject.AddComponent<Button>();
            row.Btn.transition = Selectable.Transition.None;
            int captured = index;
            UiSfx.AttachClick(row.Btn);

            // 🔴 進房要**點兩下**(使用者指定)。單擊只有 hover 與點擊音效,不進房 ——
            //    房卡一頁六張、彼此貼著,單擊就進去很容易誤觸;而且「先右鍵看房間信息再決定」
            //    那條路會被一次誤觸整個跳過。
            // 🔴 <c>Button.onClick</c> 收不到「這是第幾下」(它連右鍵都吃不到),所以雙擊一定要走
            //    <see cref="PointerClickProxy"/> 看 <c>clickCount</c> —— 與房間座位的雙擊鎖格同一個做法。
            //    Button 留著是為了 hover 換圖與點擊音效。
            // 🔴 但要知道 <c>row.Btn.interactable = false</c>(空房卡)**擋不住這個 proxy** —— 那是 Button
            //    自己的規矩,EventSystem 照樣把 pointerClick 送給同物件上其他的 IPointerClickHandler
            //    (右鍵那條早就知道這件事,所以它自己另外關一次 <c>RightClick.Enabled</c>)。
            //    空位的守門因此**只剩** <see cref="OnRowClicked"/> 裡那句 null 檢查,刪它之前先讀那邊的註解。
            var click = root.gameObject.AddComponent<PointerClickProxy>();
            click.Clicked = ev =>
            {
                if (ev == null || ev.button != PointerEventData.InputButton.Left) return;
                if (ev.clickCount >= 2) OnRowClicked(captured);
            };

            // 右鍵 → 房間信息。🔴 Button.onClick 只吃左鍵,右鍵一定要另外接(見 RightClickProxy)。
            //    官方版面檔裡查不到這個觸發(房卡那六個 CheckBox 三態全是 empty.an、沒有任何 popmenu 屬性)——
            //    右鍵開房間信息是寫在引擎程式碼裡的,所以這條是我們自己接的。
            row.RightClick = root.gameObject.AddComponent<RightClickProxy>();
            row.RightClick.Clicked = () => OnRowRightClicked(captured);

            row.Hover = root.gameObject.AddComponent<RowHover>();
            row.Hover.Left = row.Card;
            return row;
        }

        // ---- 下方面板(聊天 + 自己的資料) ----

        private void BuildBottomPanel()
        {
            // 下方整條背板(Lobby53..56,各 256 寬 + 一片 21 寬的收尾)。
            // 官方把「等級 / 經驗值 / G幣 / M幣 / P幣」與「超舞戰績 / 知名度 / 勝率 / 愛慕值 / 金葉子」
            // 兩排**標題字烤死在圖裡**,所以下面每一格只放數值,標題一律不重畫。
            // 🔴 官方是**一整張** Lobby53(stage.png 0,364,787×169)貼在 (8,435) —— XML 只有這一個 Label。
            //    以前拆成四張(Lobby53..56 各 256 寬)貼在 (5,433) 起,而 Lobby54/55/56 根本是**別的圖**
            //    (各自獨立的 Lobby54.png…,不是背板的續接),整條 bar 因此位置偏了、右段還是拼接的。
            //    bar 位置一偏,照官方座標擺的那排鈕看起來就沒對齊(使用者回報)。
            UIKit.AddSprite(Root, "BottomPanel", An("Lobby53"), 8f, 435f);

            // 聊天顯示區**沒有自己的底框** —— 框是上面那張 Lobby53 烤死的(見 ChatX 那段的實測表)。
            // 再貼一張 RecordChatBG 就會變成兩個框疊在一起、左右各兩條線。

            // 聊天記錄。背板已經畫好框了 → ScrollRect 自己不要再上底色。
            _chatScroll = UIKit.AddVerticalScroll(Root, "ChatScroll", out _chatContent, 1f, ChatPad, new Color(0f, 0f, 0f, 0f));
            PlaceTopLeft(_chatScroll.GetComponent<RectTransform>(), ChatX, ChatY, ChatW, ChatH);
            _chatClip = _chatScroll.gameObject.AddComponent<ChatLineClip>();   // 只露整行,不留半截字

            // 聊天記錄的捲軸(官方 AllChatList 的 Handle,與另外兩條同一張 Lobby12)。
            // 建在捲動區之後 = 疊在它上面,不然會被 viewport 的底蓋掉。
            // 🔴 這條以前**根本沒接拖曳** —— DragChatHandle 寫好了卻沒有任何人呼叫,所以永遠只有滾輪能捲。
            //    現在與另外兩條一樣用內建的 Scrollbar,拖曳與點軌道跳位都由它處理。
            _chatBar = FixedScrollbar.Create(Root, "ChatScrollbar", LobbyArt.AnSoloAA("Lobby12"),
                                             ChatRailX, ChatRailTop, HandleW, HandleH, ChatRailH);
            FixedScrollbar.Bind(_chatBar, _chatScroll);

            // 頻道切換(chatmode「當前」)。按了拉開四選一的頻道選單,行為與房間畫面同一套。
            _chatChannelBtn = SpriteBtn("ChatChannel", "Lobby57", "Lobby58", "Lobby59", ChanX, ChanY, ToggleChatMenu);
            _chatChannelImg = _chatChannelBtn.targetGraphic as Image;

            // 聊天記錄(recordchatmode)。🔴 這顆**按了不做事**(使用者要求把功能拿掉):
            //    以前它是聊天區的收合開關,但收合是把底框關掉、捲動區關掉、鈕再換一組圖,
            //    幾個東西各關各的 → 一收一開之間會在面板上留殘影(使用者回報)。
            //    聊天區從此常駐,鈕照官方擺著、維持三態外觀而已(closerecordchatmode 那組圖從此沒人用)。
            SpriteBtn("RecordChat", "RecordChatBtn_1", "RecordChatBtn_2", "RecordChatBtn_3",
                      RecordChatX, RecordChatY, null);

            // 輸入框:**placeholder 傳空字串**。以前放「輸入訊息後按 Enter…」的提示字,官方那格是空的,
            // 而且提示字在沒有游標的情況下會讓人以為那不是輸入框(使用者回報)。設定整套照搬房間那顆
            // (見 RoomScreen.ConfigureRoomChatInput),游標與 IME 行為兩邊才會一致。
            // (loc key lobby.chat_hint 從此沒人用,留在表裡不影響。)
            _chatInput = UIKit.AddInputField(Root, "ChatInput", "", 13f);
            PlaceTopLeft(_chatInput.GetComponent<RectTransform>(), ChatInputX, ChatInputY, ChatInputW, ChatInputH);
            if (_chatInput.targetGraphic != null)
                _chatInput.targetGraphic.color = new Color(1f, 1f, 1f, 0f);   // 底圖已經有凹槽了,不要再蓋一層灰
            ConfigureChatInput();

            // 表情(expression):按了拉開 6×4 的表情盤,點一格就把那個表情送進聊天(與房間同一套表情系統)。
            _exprBtn = SpriteBtn("Expression", "Lobby102", "Lobby117", "Lobby118", ExprX, ExprY, ToggleExpressionMenu, circle: true);

            // 送出鈕很小,不套 alpha 命中判定 —— 那麼小的鈕給滿一個矩形反而好按(見 UIKit.SetAlphaHit 的註解)。
            // ⚠️ 官方這顆的 normal/hover 是**對調**的(bgnormal=Lobby100、bghover=Lobby99),照抄。
            SpriteBtn("ChatSend", "Lobby100", "Lobby99", "Lobby101", SendX, SendY, SendChat);

            // 大聲公(全服廣播)與寵物:兩個系統都沒有 → 鈕照擺,按了不做事。
            SpriteBtn("LoudSpeaker", "LoudSpeaker_1", "LoudSpeaker_2", "LoudSpeaker_3", LoudX, LoudY, null, circle: true);
            SpriteBtn("Pet", "Lobby147", "Lobby148", "Lobby149", PetX, PetY, null, circle: true);

            // 道具包(ItemsButton)。官方這顆是道具包,不是衣櫥 —— 以前接到儲物櫃是接錯了(使用者回報)。
            // 儲物櫃的入口在房間(RoomScreen 的 ClosetButton),沒有因此少掉。
            SpriteBtn("Items", "Lobby94", "Lobby95", "Lobby96", ItemsX, BottomBtnY, null);
            // 个人资料(DetailButton)→ 自己的資料頁。房間畫面是「點自己的頭貼」進去的,大廳沒有頭貼可點,
            // 這顆美術字面本來就寫著「个人资料」→ 正好是同一個去處,不接的話大廳就沒有入口。
            SpriteBtn("Detail", "Lobby61", "Lobby62", "Lobby72", DetailX, DetailY,
                      () => Nav.OpenSelfInfo?.Invoke());
            // NotesButton(郵件)。這個重製版沒有信箱 → 按了不做事。
            // 🔴 這顆以前借去開「音符外觀選擇器」,使用者要求拿掉 → **NoteSkinPicker 從此沒有任何入口**
            //    (全專案只有這一處接過 Nav.OpenNoteSkinPicker)。那支選擇器本身還在,之後要接的話
            //    最合理的去處是設定視窗(OptionDlg),不是這顆美術上寫著「郵件」的鈕。
            SpriteBtn("NoteSkin", "Lobby63", "Lobby64", "Lobby73", NotesX, BottomBtnY, null);
            // 右下角的「?」(help)。沒有說明系統 → 鈕照擺,按了不做事。
            var help = SpriteBtn("Help", "Lobby65", "Lobby66", "Lobby74", HelpX, HelpY, null, circle: true);
            UIKit.SetAlphaHit(help.targetGraphic);

            // 玩家名單開關(ListShow 三人頭 ↔ AvtShow 單人頭,官方疊在同一格輪換)。
            // 展開時鈕換成 AvtShow(= 按了切回看角色),見 ToggleUserPanel / ApplyUserPanelSprites。
            _userListBtn = SpriteBtn("UserList", "Lobby160", "Lobby161", "Lobby162", UserListX, UserListY, ToggleUserPanel);
            _userListImg = _userListBtn.targetGraphic as Image;

            BuildSelfInfo();
        }

        private void BuildSelfInfo()
        {
            // 名字:官方 charname 是這份 XML 裡**唯一**標了 bold="true" 的 Label,另外**描一圈白邊**
            // (使用者要求)—— 橘字壓在深紫背板上,筆畫邊緣會與底色糊在一起。
            // 用 OutlinedLabel(偏移複本)而不是 TMP 的 SDF outline:執行期建的 CJK 動態圖集吃不到
            // SDF 描邊,不管 outlineWidth 給多少都畫不出來(見 OutlinedLabel 的註解)。
            _selfName = OutlinedLabel.Create(Root, "SelfName", SelfNameX, SelfNameY, SelfNameW, SelfNameH, 14f,
                                             SelfNameColor, SelfNameEdge, SelfNameEdgePx, true,
                                             TextAlignmentOptions.MidlineLeft);
            // 名字長度沒有上限,不截會蓋到右邊的欄位。🔴 描邊複本也要一起截,只截 face 的話
            // 邊會繼續畫那幾個被截掉的字 —— 變成一圈沒有字心的白影(見 SetOverflow)。
            _selfName.SetOverflow(TextOverflowModes.Ellipsis);

            // 右排五行(烤字順序見常數區):超舞戰績 / 知名度 / 勝率 / 愛慕值 / 金葉子。
            _selfRecord = StatLabel(Root, "SelfRecord", RecordX, RecordY, PerfW, PerfH, 11f, StatColor, TextAlignmentOptions.Midline);
            _selfFame = StatLabel(Root, "SelfFame", FameX, FameY, PerfW, PerfH, 11f, StatColor, TextAlignmentOptions.Midline);

            // 愛慕值 / 金葉子:這個重製版沒有這兩套系統,但官方那兩行**固定顯示 0**(使用者要求照擺)。
            // 同 WardrobeScreen 的金葉子 —— 那邊也是使用者指定固定 0。
            StatLabel(Root, "SelfLove", LoveX, LoveY, MoneyW, StatH, 11f, StatColor, TextAlignmentOptions.MidlineLeft).text = "0";
            StatLabel(Root, "SelfLeaf", LeafX, LeafY, LeafW, StatH, 11f, StatColor, TextAlignmentOptions.MidlineLeft).text = "0";

            _selfLevel = StatLabel(Root, "SelfLevel", LevelX, LevelY, MoneyW, StatH, 11f, StatColor, TextAlignmentOptions.MidlineLeft);
            _selfWin = StatLabel(Root, "SelfWin", WinX, WinY, MoneyW, StatH, 11f, StatColor, TextAlignmentOptions.MidlineLeft);
            _selfPoints = StatLabel(Root, "SelfPoints", MoneyX, PointY, MoneyW, StatH, 11f, StatColor, TextAlignmentOptions.MidlineLeft);
            _selfCoins = StatLabel(Root, "SelfCoins", MoneyX, CoinY, MoneyW, StatH, 11f, StatColor, TextAlignmentOptions.MidlineLeft);
            _selfBonus = StatLabel(Root, "SelfBonus", MoneyX, BonusY, MoneyW, StatH, 11f, StatColor, TextAlignmentOptions.MidlineLeft);

            // 經驗條(官方 <ProgressBar name="exp_progress" x="522" y="489" w="86" h="14"
            //   backname="Lobby137.an" forename="Lobby60.an" minrange="0" maxrange="100"/>)。
            //
            // 🔴 **Lobby137.png 整張 84×10 全是 alpha=0**(逐點量過)—— 官方的凹槽是**烤在背板 Lobby53 上**的,
            //    backname 只是佔個位。以前這裡貼的就是它,所以那格永遠是空的:貼上去的是一張看不見的圖。
            //    現在改貼真正有像素的前景 Lobby60(紅色漸層:上 219,48,68 / 中 228,67,88 / 下 164,5,33),
            //    用 Filled 由左往右填 —— 那就是使用者說的「紅色容量」。
            _expFill = UIKit.AddSprite(Root, "ExpFill", An("Lobby60"), ExpX, ExpY);
            _expFill.type = Image.Type.Filled;
            _expFill.fillMethod = Image.FillMethod.Horizontal;
            _expFill.fillOrigin = (int)Image.OriginHorizontal.Left;
            _expFill.fillAmount = 0f;

            // 百分比壓在條上面(官方 charexprate,置中的淡粉白)。建在條**之後** = 畫在它上面,
            // 條填過半時數字才不會被紅色蓋掉。
            _selfExpRate = StatLabel(Root, "SelfExpRate", ExpRateX, ExpRateY, ExpRateW, ExpRateH, 11f,
                                     ExpRateColor, TextAlignmentOptions.Midline);
        }

        // ================================================================ 生命週期

        public override void OnShow()
        {
            Subscribe();
            Ctx.Chat?.SetScope(ChatScope.Lobby);   // 大廳作用域:只顯示大廳訊息(密語跨場另計)

            ShowAvatar();
            _scroll = 0;
            RefreshSelf();
            ReloadRooms();
            RequestOnlineUsers();   // 名單開著就回來的情況(從房間回大廳)—— 自己 no-op 掉收起來的那種
            RebuildChat();
            _nextPoll = Time.unscaledTime + PollSeconds;
        }

        public override void OnHide()
        {
            _listGen++;   // 還在路上的 roomList 回呼作廢
            Unsubscribe();
            HideAvatar();
            // 下拉選單不能掛著離開:回來時它們會還開著,而且蓋在剛滑入的房卡上。
            HideHallMenu();
            HideApplyMenu();
            HideChatMenu();
            HideExpressionMenu();
            FlushAvatarTuning();   // 調校值真的落到磁碟(改的當下只寫進 LocalPrefs 的記憶體副本)
        }

        /// <summary>
        /// 左側的 3D 角色(官方 AvtShow)。做法與選角色畫面**完全相同**:GenderPreview3D 自己開一台相機
        /// 把角色渲到 RenderTexture,我們只是把那張貼圖掛到 RawImage 上。
        ///
        /// 🔴 一定要把預覽的 layer 從前端 UI 相機的 cullingMask 遮掉 —— 那台相機幾乎什麼都照,
        ///    沒遮的話角色會被它用正交投影再畫一次(扁扁的疊在畫面上)。OnHide 還原。
        ///
        /// 大廳只顯示**目前登入的那個角色**,所以 Build 之後照 session 的性別切一次。
        /// (GenderPreview3D 內部男女兩隻都建起來、靠 SetGender 切換顯示,這裡沿用同一套 API。)
        /// </summary>
        private void ShowAvatar()
        {
            int gender = Ctx != null && Ctx.Session != null && Ctx.Session.Gender == 1 ? 1 : 0;
            string[] fParts = PartsForGender(0), mParts = PartsForGender(1);

            if (_preview == null)
            {
                var go = new GameObject("LobbyAvatar3D");
                _preview = go.AddComponent<GenderPreview3D>();
                // 🔴 取景參數要在 Build **之前**設:Build 最後會 SetGender → FrameTo,那一刻就把相機距離定下來了。
                //    (avatarYOffset 更早 —— BuildAvatar 擺位時就讀它。)
                //
                // 🔴 那兩個偏移一定要**歸零**:官方 LOBBYSEL 的 avatarYOffset=-5 是「那個 400×600 預覽框內」的偏移,
                //    verticalBias=+2 也是為那個框校的。大廳沒有那個框,兩者加起來會讓角色相對取景窗往下偏 7 個
                //    model unit ≈ 0.123×角色像素高 —— fillFrac 0.9 時腳底會算到 y=642 直接掉出畫面
                //    (使用者回報的「人變得太大」),而且在 400×600 的槽位裡 fillFrac 上限被壓到 0.803、
                //    永遠做不到官方那個 548px。歸零之後版位改由 AvatarY 吸收,男女落點也才會一致
                //    (那個偏移與身高成反比,男角比女角高 8% → 不歸零的話兩性會差 5px)。
                _preview.avatarYOffset = 0f;
                _preview.verticalBias = 0f;
                _preview.fillFrac = AvFill;   // 預設 = AvatarFillFrac,F4 面板調過就用調過的

                _preview.Build(gender, fParts, mParts, BodyIndexForGender(0), BodyIndexForGender(1));
            }
            else
            {
                // 回到大廳時穿搭可能已經變了(去商城買了東西、或換過帳號)→ 每次顯示都重套一次。
                _preview.SetOutfits(gender, fParts, mParts, BodyIndexForGender(0), BodyIndexForGender(1));
            }
            _preview.SetGender(gender);
            // 重新進大廳(或換過性別/穿搭)→ 轉身角度歸零,回到官方那個朝左 30° 的預設姿。
            _preview.ResetOrbit();

            if (_previewImg != null && _preview.PreviewTexture != null)
            {
                _previewImg.texture = _preview.PreviewTexture;
                _previewImg.color = Color.white;
            }

            var ui = FrontendApp.Instance != null ? FrontendApp.Instance.UiCam : null;
            if (ui != null) { _maskedCam = ui; _savedMask = ui.cullingMask; ui.cullingMask &= ~(1 << GenderPreview3D.PreviewLayer); }
        }

        private void HideAvatar()
        {
            if (_maskedCam != null) { _maskedCam.cullingMask = _savedMask; _maskedCam = null; }
            if (_previewImg != null) { _previewImg.texture = null; _previewImg.color = new Color(1f, 1f, 1f, 0f); }
            if (_preview != null) { Destroy(_preview.gameObject); _preview = null; }
        }

        // 穿搭 / 體型:個人資料視窗那尊也要用同一套 → 抽到 AvatarOutfits(見那邊的註解)。
        private static string[] PartsForGender(int gender) => AvatarOutfits.PartsForGender(gender);
        private static int BodyIndexForGender(int gender) => AvatarOutfits.BodyIndexForGender(gender);

        private RawImage AddRaw(string name, float x, float y, float w, float h)
        {
            var rt = UIKit.NewRect(Root, name);
            var ri = rt.gameObject.AddComponent<RawImage>();
            ri.raycastTarget = false;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            return ri;
        }

        private void Subscribe()
        {
            if (_subscribed) return;
            _subRooms = Ctx.Rooms;
            _subChat = Ctx.Chat;
            if (_subRooms != null) _subRooms.RoomsChanged += OnRoomsChanged;
            if (_subChat != null) _subChat.MessageReceived += OnChatMessage;
            Ctx.OnlineChanged += OnOnlineChanged;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            if (_subRooms != null) _subRooms.RoomsChanged -= OnRoomsChanged;
            if (_subChat != null) _subChat.MessageReceived -= OnChatMessage;
            Ctx.OnlineChanged -= OnOnlineChanged;
            _subRooms = null;
            _subChat = null;
            _subscribed = false;
        }

        /// <summary>登入/登出會就地換掉 Ctx.Rooms / Ctx.Chat → 訂閱要跟著搬,資料來源也要重讀。</summary>
        private void OnOnlineChanged()
        {
            _listGen++;
            Unsubscribe();
            if (!Visible) return;
            Subscribe();
            // 換掉的是**另一個** IChatService 實例,作用域要重設一次 —— 不設的話它還停在自己的預設值,
            // 我們送出的大廳訊息可能被標成別的作用域,結果自己送的字自己看不到。
            Ctx.Chat?.SetScope(ChatScope.Lobby);
            _scroll = 0;
            ReloadRooms();
            RebuildChat();
        }

        private void Update()
        {
            if (!Visible) return;

            AvatarDebugUpdate();   // F4 開關角色調校面板 + 方向鍵微調(editor 限定,見 AvatarDebug.cs)

            // 🔴 游標要**每幀**更新(閃爍 + 跟著字尾跑),所以擺在下面那個 4 秒節拍的早期返回**之前**。
            UpdateChatCaret();

            CloseMenusOnOutsideClick();   // 同理:點外面要**當下**就收選單,不能等到下一個 poll 節拍
            CloseUserMenuOnOutsideClick();   // 名單的右鍵選單是動態建的,不在上面那四個之列
            HandleEscape();               // ESC 逐層退出(收選單 / 清草稿 / 退回選男女) —— 同樣要每幀收,不能等節拍

            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + PollSeconds;

            // 商城/儲物櫃是**疊在大廳上的 modal**,關掉不會重跑 OnShow —— 買完東西回來錢包不會自己更新,
            // 所以跟房間列表同一個節拍順手重讀一次。
            RefreshSelf();

            // server 沒有「房間列表變了」的推播,只能自己回頭問(離線那份靠 RoomsChanged 事件就夠了)。
            if (Ctx != null && Ctx.Net != null)
            {
                RequestOnlineRooms();
                // 順手把自己的公開名片推上去,別人點開才看得到命中率那些數字。掛在這個節拍是因為
                // 名片會變的時機很散(打完一局、逛完商城、改了自我介紹四格),與其在每個地方補一次
                // 呼叫、漏一個就永遠不更新,不如跟著大廳既有的心跳走。內容沒變的話 PublishCard 自己會擋掉。
                Ctx.Net.PublishCard();
            }

            // 玩家名單同理(沒有上下線推播)。只在名單開著時才問 —— 收起來的時候沒人看,不必占頻寬。
            RequestOnlineUsers();
        }

        // ================================================================ 房間列表

        /// <summary>依現在的模式重讀一次列表:線上跟 server 要,離線讀本機那份。</summary>
        private void ReloadRooms()
        {
            if (Ctx != null && Ctx.Net != null) { RequestOnlineRooms(); return; }
            LoadOfflineRooms();
        }

        private void LoadOfflineRooms()
        {
            _rooms.Clear();
            var src = Ctx != null && Ctx.Rooms != null ? Ctx.Rooms.GetRooms() : null;
            if (src != null) for (int i = 0; i < src.Count; i++) _rooms.Add(src[i]);
            RefreshRows();
        }

        /// <summary>
        /// 大廳的測試假資料開關(假房間 + 假玩家)。
        ///
        /// 🔴 **editor 預設開、打包版預設關** —— 開發時要看得到滿版的房卡與名單才能校版位,
        ///    而打包出去的版本絕不能出現假人。要在 editor 關掉就設環境變數(或 EditorPrefs)
        ///    <c>SDO_LOBBY_FAKE=0</c>;要在打包版臨時打開就設 <c>=1</c>。
        ///    讀法照本專案的 dev 慣例(<see cref="Sdo.Game.ScreenGameplay.DevVar"/>:環境變數優先、editor 退回 EditorPrefs)。
        /// </summary>
        private static bool FakeLobbyData
        {
            get
            {
                var v = Sdo.Game.ScreenGameplay.DevVar("SDO_LOBBY_FAKE");
                if (!string.IsNullOrEmpty(v)) return v != "0";
#if UNITY_EDITOR
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>官方風格的假房名(「XXX的舞蹈室」)。用真的像房名的字,「測試1測試2」看不出版位對不對。</summary>
        private static readonly string[] FakeRoomHosts =
            { "煙煙羅", "寶貝BOY", "邦·瑋", "不知道", "勇敢的心", "月光之城", "櫻花雨", "小舞", "風之舞", "舞星" };

        /// <summary>假房間的座位性別樣式(0=女 1=男)。刻意不規則 —— 真實房間就是誰先進來坐誰的。</summary>
        private static readonly int[] FakeSeatGender = { 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 1, 0, 0, 1 };

        private static readonly string[] FakeSongs =
            { "Butterfly", "Dancing Queen", "極彩色", "Sandstorm", "Bad Apple", "月與蓮", "" };

        private void AddFakeRooms(List<RoomInfo> into)
        {
            // 十間 —— 一頁只放得下 6 張卡,多出來的才會讓捲軸握把有得跑(這正是要測的東西之一)。
            for (int i = 0; i < FakeRoomHosts.Length; i++)
            {
                string host = FakeRoomHosts[i];
                var room = new RoomInfo
                {
                    Id = 90000 + i,          // 假房號不會與本機那間(RoomEntry 給的)撞號
                    Seq = i,                 // 門牌從 000 起算(官方第一格就是 000)
                    Name = host + "的舞蹈室",
                    HostName = host,
                    Status = (i % 4 == 3) ? RoomStatus.InGame : RoomStatus.Waiting,
                    Capacity = 6,
                    SongTitle = FakeSongs[i % FakeSongs.Length],
                    SongLevel = 5 + (i % 6),   // 假難度 5..10:房間信息那格要看得出「歌名 (N級)」的樣子
                };
                // 🔴 RoomInfo.Count 是**由 Seats 算出來的唯讀屬性**,不能直接指派 —— 人數要靠真的塞座位。
                int taken = 1 + (i % 5);
                for (int s = 0; s < room.Capacity; s++)
                    room.Seats.Add(new SeatInfo { Player = s < taken ? new PlayerProfile("", host, 1 + i) : null });
                // 假房間也給性別 —— 不然那排愛心永遠全是粉紅,校不出藍色那顆對不對。
                // 🔴 **不要男女交叉**:真實房間是「第 N 個座位坐的是誰就畫誰的性別」,
                //    交叉排列會讓人以為那排愛心有固定規律。用一個不規則但固定的樣式。
                room.SeatGenders = new int[taken];
                for (int s = 0; s < taken; s++) room.SeatGenders[s] = FakeSeatGender[(i * 6 + s) % FakeSeatGender.Length];
                into.Add(room);
            }
        }

        private void RequestOnlineRooms()
        {
            var net = Ctx != null ? Ctx.Net : null;
            if (net == null || !net.IsConnected) return;
            int gen = _listGen;
            net.RequestRoomList(entries =>
            {
                // 回呼可能在離開大廳/登出之後才回來 —— 那份資料屬於上一次的畫面,丟掉。
                if (this == null || gen != _listGen) return;
                _rooms.Clear();
                _rooms.AddRange(NetRoomMapping.ToRoomInfos(entries));
                RefreshRows();
            });
        }

        /// <summary>離線的房間服務自己會喊「列表變了」;線上那份不會(它的列表是我們問來的)。</summary>
        private void OnRoomsChanged()
        {
            if (Ctx != null && Ctx.Net != null) return;
            LoadOfflineRooms();
        }

        private void RefreshRows()
        {
            _view.Clear();
            // dev 假房間補在**檢視層**(_view)而不是資料來源(_rooms):
            //   • 線上/離線都看得到 —— 上一版只在離線那條路加,結果連著 server 時整排卡還是空的;
            //   • 不會被下一次輪詢洗掉,也不會混進「快速進入」的挑選(那邊讀的是 _rooms)。
            if (FakeLobbyData) AddFakeRooms(_view);
            for (int i = 0; i < _rooms.Count; i++)
            {
                var r = _rooms[i];
                if (r != null) _view.Add(r);
            }

            // 「等待舞台 / 全部舞台」篩選。🔴 **連 dev 假房間一起吃** —— 以前這條寫在上面那個迴圈裡,
            // 只濾得到 _rooms,假房間那幾張「遊戲中」的卡片會穿過預設檢視,看起來像篩選壞掉。
            // 原地壓縮而不是 RemoveAll(lambda):RefreshRows 每次輪詢都跑,不要每次配一個 delegate。
            if (_waitingOnly)
            {
                int keep = 0;
                for (int i = 0; i < _view.Count; i++)
                    if (_view[i].Status == RoomStatus.Waiting) _view[keep++] = _view[i];
                _view.RemoveRange(keep, _view.Count - keep);
            }

            int max = Mathf.Max(0, _view.Count - VisibleRows);
            _scroll = Mathf.Clamp(_scroll, 0, max);

            for (int i = 0; i < VisibleRows; i++)
            {
                int idx = _scroll + i;
                Bind(_rows[i], idx < _view.Count ? _view[idx] : null, idx);
            }
            PlaceHandle(max);

            // DEV: SDO_ROOMINFO=1 → 房間列表一有資料就直接開第一間房的房間信息。
            //      那個框只有**右鍵房卡**才叫得出來,而截圖工具點不了滑鼠,只能從這裡開。
            //      只開一次(_devInfoShown)—— 列表每 4 秒刷一次,不擋住就會每次都彈。
            if (!_devInfoShown && _view.Count > 0 && Nav.OpenRoomInfo != null
                && !string.IsNullOrEmpty(Sdo.Game.ScreenGameplay.DevVar("SDO_ROOMINFO")))
            {
                _devInfoShown = true;
                Nav.OpenRoomInfo(_view[0], () => { });
            }
        }

        private bool _devInfoShown;

        private void Bind(RoomRow row, RoomInfo r, int absoluteIndex)
        {
            row.Data = r;

            bool has = r != null;
            // 🔴 卡片底的兩張圖是「**常態 vs 滑過**」,不是「空房 vs 有人」——
            //    XML 兩個 Label 疊在同一格:emptyroom0 = Lobby98(**紫**,stage.png 506,0)、
            //    room_chk0 = Lobby28(**粉紅**,506,89)。名字裡的 chk 就是 check/hover 的意思。
            //    以前照字面把「有人」畫成粉紅、「空房」畫成紫,結果整排卡變粉紅色(使用者回報
            //    「官方本來是紫色為什麼做成粉紅色」)。官方是一律紫、滑鼠移上去才變粉紅。
            row.Hover.SetSkin(An("Lobby98"), has ? An("Lobby28") : null);
            row.Btn.interactable = has;
            if (row.RightClick != null) row.RightClick.Enabled = has;   // 空位:官方左右鍵都點不動

            // 狀態:圓底 + 綠字牌兩層。有人 → 等待(Lobby26 + waiting)/ 遊戲中(Lobby27 + playing);
            // 空位 → 只有 LobbyRoomNone 的圓底,沒有字牌。
            bool waiting = has && r.Status == RoomStatus.Waiting;
            UIKit.ApplySprite(row.State, !has
                ? An("LobbyRoomNone")
                : An(waiting ? "Lobby26" : "Lobby27"));
            UIKit.ApplySprite(row.Badge, !has ? null : An(waiting ? "waiting" : "playing"));

            // 🔴 門牌是 **3 位數的 Seq**,不是 5 位數的 Id。Id 是「加入房間的鑰匙」,
            //    官方大廳從來不顯示它(要進房就在這裡點那張卡)。
            //    門牌**從 000 起算**(官方實機截圖第一格就是 000),所以「沒有門牌」的哨兵是 -1 不是 0。
            //    真的拿不到 seq 時(舊 server 的 roomList 不帶這個欄位)退回用列表位置當門牌 ——
            //    那正是官方那個數字的意思:「第幾間房」。
            int door = !has ? 0 : (r.Seq >= 0 ? r.Seq : absoluteIndex);
            door = Mathf.Clamp(door, 0, 999);
            for (int d = 0; d < row.Digits.Length; d++)
            {
                int digit = d == 0 ? door / 100 : (d == 1 ? (door / 10) % 10 : door % 10);
                UIKit.ApplySprite(row.Digits[d], has ? LobbyArt.Digit("LobbyNum1", digit) : null);
            }

            row.Name.SetText(has ? RoomLabels.DisplayName(r.Name, r.HostName) : "");
            // 🔴 這一格(官方 roommusic,卡片右側)顯示的是**遊戲模式**,不是歌名 ——
            //    官方實機那裡寫的是「自由模式 / 普通模式」。以前放歌名,結果多數房間是空白
            //    (大廳的房間列表封包本來就常常沒帶歌名),整格看起來像壞掉。
            row.Song.text = has ? ModeLabel(r.Mode) : "";

            int count = has ? Mathf.Clamp(r.Count, 0, 9) : 0;
            int cap = has ? Mathf.Clamp(r.Capacity, 0, 9) : 0;
            UIKit.ApplySprite(row.CountD, has ? LobbyArt.Digit("LobbyNum2", count) : null);
            UIKit.ApplySprite(row.CapD, has ? LobbyArt.Digit("LobbyNum2", cap) : null);
            if (row.Slash != null) row.Slash.enabled = has;

            // 密碼房的鑰匙:這個重製版還沒有房間密碼 → 永遠不畫(版位已經留好)。
            if (row.Key != null) row.Key.enabled = false;

            // 🔴 官方那排是**六顆愛心**,不是人頭,而且**六格永遠都畫**,顏色分三種:
            //    女 → FEMALE.AN(粉紅,stage.png 1000,89)、男 → MALE.AN(藍,982,89)、空位 → MAN.AN(灰,1001,105)。
            //    以前只畫有人的那幾格、而且用灰心去畫「有人」—— 三個都錯。
            //    性別由 server 的 roomList 逐座位送過來(見 RoomInfo.SeatGenders);舊版 server 不送 → 退回粉紅。
            var female = An("female");
            var male = An("male");
            var free = An("man");
            for (int h = 0; h < row.Heads.Length; h++)
            {
                Sprite heart = null;
                if (has) heart = h < r.Count ? (SeatIsMale(r, h) ? male : female) : free;
                UIKit.ApplySprite(row.Heads[h], heart);
            }

            UIKit.ApplySprite(row.Keyboard, has ? An("Lobby97") : null);
        }


        /// <summary>房卡上第 <paramref name="index"/> 顆愛心的主人是不是男生。
        /// 資料缺了(舊版 server / 離線)一律當女生 —— 那是官方唯一那顆彩色心的顏色,退化得最不突兀。</summary>
        private static bool SeatIsMale(RoomInfo r, int index)
        {
            var g = r.SeatGenders;
            return g != null && index >= 0 && index < g.Length && g[index] == 1;
        }

        /// <summary>房卡上那格的模式字。與選歌畫面/房間畫面共用同一組 loc key —— 同一件事在三個地方
        /// 不該有三種講法。(<c>GameMode</c> 只有 Free / Normal 兩個值;ShowTime 是房間裡另外一個開關,
        /// 大廳的房間列表帶不到,所以這裡不分。)</summary>
        private static string ModeLabel(GameMode mode)
            => L(mode == GameMode.Normal ? "songselect.mode_normal" : "songselect.mode_free");

        /// <summary>
        /// 捲軸被拖(或被點軌道)之後 → 換算成第幾列。房卡是**整數分頁**的,所以 0..1 要乘上可捲列數
        /// 再四捨五入。<c>value</c> 是 BottomToTop:1 = 最上 = 第 0 列,所以要 <c>1 - value</c>。
        ///
        /// 🔴 只在真的換列時才 RefreshRows —— 拖曳時每幀都會回呼,同一列重建七張房卡是白工。
        /// </summary>
        private void OnRoomBarChanged(float value)
        {
            int max = Mathf.Max(0, _view.Count - VisibleRows);
            if (max <= 0) return;
            int row = Mathf.Clamp(Mathf.RoundToInt((1f - value) * max), 0, max);
            if (row == _scroll) return;
            _scroll = row;
            RefreshRows();
        }

        /// <summary>
        /// 把捲軸挪到目前這一列。
        ///
        /// 🔴 **永遠顯示**(使用者要求):以前沒東西可捲時整顆 <c>enabled = false</c>,結果房間少於七間
        /// 就看不到滑桿頭,看起來像忘了做。沒得捲時停在軌道最上面,那就是官方的樣子。
        /// 🔴 一定要用 <c>SetValueWithoutNotify</c>:這裡是「資料 → 捲軸」方向,發通知會反過來又叫一次
        /// <see cref="OnRoomBarChanged"/> 造成迴圈。
        /// </summary>
        private void PlaceHandle(int max)
        {
            if (_roomBar == null) return;
            _roomBar.SetValueWithoutNotify(max > 0 ? 1f - _scroll / (float)max : 1f);
        }

        private void OnWheel(float dy)
        {
            if (Mathf.Approximately(dy, 0f)) return;
            int max = Mathf.Max(0, _view.Count - VisibleRows);
            if (max == 0) return;
            _scroll = Mathf.Clamp(_scroll + (dy > 0f ? -1 : 1), 0, max);
            RefreshRows();
        }

        // ================================================================ 動作

        /// <summary>
        /// 「創建舞台」→ 先開官方的創建遊戲房間對話框(房名 / 密碼 / 模式 / 房型),按確定才真的建房。
        /// 以前是按了直接建一間無名的普通房 —— 官方從來沒有那條捷徑。
        ///
        /// 🔴 走 <c>Nav</c> 而不是直接抓 modal:那個 modal 建在 FrontendApp 的 modalLayer 上,
        ///    大廳不該知道它的存在(同 OpenPlayerInfo 的理由)。沒接上時 <c>?.Invoke</c> 什麼都不做。
        /// </summary>
        private void OnCreate()
        {
            if (ScreenTransition.Busy) return;
            if (Nav.OpenRoomCreate == null) { CreateRoomNow("", GameMode.Normal); return; }
            Nav.OpenRoomCreate((name, password, mode) => CreateRoomNow(name, mode));
        }

        /// <summary>
        /// 真的把房間建出來。<paramref name="password"/> 目前沒有用到 —— server 的 createRoom
        /// 只吃房名,房間上鎖那套協定還沒有,所以密碼欄先收著不送(官方那個欄位照擺,見 RoomCreateModal)。
        /// </summary>
        private void CreateRoomNow(string name, GameMode mode)
        {
            var net = Ctx.Net;
            if (net != null)
            {
                // 從大廳按建房時理論上不在任何房裡;真的還掛著就先送 leaveRoom,把**本機**的認知
                // (OnlineRoomService._current / Session.CurrentRoomId)一起清掉。
                // 這不是進房的前提 —— server 的 RoomRegistry.TryCreate/TryJoin 本來就會隱式離房,
                // 而且是房主也照離(那間房會轉手或關掉),所以這裡不必分房主/客人。
                if (net.InRoom) net.LeaveRoom();
                net.CreateRoom(name ?? "", (result, code) =>
                {
                    if (this == null) return;
                    if (result == Sdo.Net.NetProto.JoinOk) { EnterRoom(); return; }
                    // 失敗只寫 log(大廳不彈 Toast)。玩家看得到的是「按了沒進房、還留在大廳」,
                    // 而失敗的細節(server 回的協定代碼)本來就只有查問題的人需要。
                    Debug.LogWarning("[lobby] createRoom 失敗:" + result);
                });
                return;
            }

            Ctx.Rooms.CreateRoom(mode);
            EnterRoom();
        }

        /// <summary>房卡**雙擊** → 進那間房(見 <see cref="MakeRow"/> 那條為什麼不是單擊)。</summary>
        private void OnRowClicked(int rowIndex)
        {
            var r = _rows[rowIndex].Data;
            // 空位:官方也是點不動的。🔴 這個檢查現在是唯一的守門 —— 雙擊走的是 PointerClickProxy,
            // 而 Button.interactable=false **擋不住它**(EventSystem 照樣把 pointerClick 送給同物件上
            // 其他的 IPointerClickHandler)。
            if (r == null) return;
            JoinRoom(r);
        }

        /// <summary>
        /// 房卡右鍵 → 官方的房間信息對話框(房名/模式/人數/觀戰/歌曲 + 玩家列表 + 進入/取消)。
        /// 框裡按「進入」走的是與雙擊卡片**同一條** <see cref="JoinRoom"/>,不要另外寫一份進房邏輯。
        /// </summary>
        private void OnRowRightClicked(int rowIndex)
        {
            var r = _rows[rowIndex].Data;
            if (r == null || ScreenTransition.Busy) return;
            if (Nav.OpenRoomInfo == null) return;   // modal 還沒接上 → 安靜地什麼都不做
            Nav.OpenRoomInfo(r, () => JoinRoom(r));
        }

        private void JoinRoom(RoomInfo r)
        {
            if (r == null || ScreenTransition.Busy) return;
            var net = Ctx.Net;
            if (net != null)
            {
                if (net.InRoom) net.LeaveRoom();   // 同 OnCreate:只是把本機認知清掉,server 自己會隱式離房
                int code = r.Id;   // 🔴 進房的鑰匙是 5 位數房號,不是卡片上那個 3 位數門牌
                net.JoinOrSpectate(code,
                    (result, asSpectator) =>
                    {
                        if (this == null) return;
                        if (result == Sdo.Net.NetProto.JoinOk) { EnterRoom(); return; }
                        // 同 OnCreate:大廳不彈 Toast,失敗只寫 log(畫面上就是「沒進去、還在大廳」)。
                        Debug.Log("[lobby] joinRoom 失敗:" + result + " —— " + L(JoinErrorText.KeyFor(result)));
                    },
                    // 座位滿了會自動改用旁觀身分進去。這件事只寫 log —— 使用者要求大廳不彈 Toast,
                    // 也不要寫進聊天區。玩家看得出來的線索是進房之後那顆「旁觀 / 進入」鈕的狀態。
                    trigger => Debug.Log("[lobby] " + L("lobby.joined_as_spectator")));
                return;
            }

            switch (Ctx.Rooms.JoinRoom(r.Id))
            {
                // 進不去的三種原因**只寫 log,不彈 toast**:房卡上就寫著人數與「PLAYING」,
                // 滿了/開打了在按下去之前就看得到,再跳一句只是把畫面弄髒。
                case JoinResult.Ok: EnterRoom(); break;
                case JoinResult.Full: Debug.Log("[lobby] " + L("join.full")); break;
                case JoinResult.InGame: Debug.Log("[lobby] " + L("join.ingame")); break;
                default: Debug.Log("[lobby] " + L("join.notfound")); break;
            }
        }

        /// <summary>進房間轉場:漸黑 → 切畫面 → 漸亮時房間 UI 從四邊滑入(與選角色畫面同一條路)。</summary>
        private void EnterRoom()
            => ScreenTransition.Run(() => GoTo(ScreenId.Room), onReveal: Nav.PlayRoomEntrance);

        private void OnQuickJoin()
        {
            RoomInfo pick = null;
            for (int i = 0; i < _rooms.Count; i++)
            {
                var r = _rooms[i];
                if (r == null || r.Status != RoomStatus.Waiting || r.IsFull) continue;
                // 挑**人最多**的那間,不是第一間 —— 測試留下的空房會排在前面,挑到那間的話
                // 玩家會進到一間只有自己的房,症狀跟「加入失敗」長得一樣(FrontendApp 的 dev hook 踩過同一個坑)。
                if (pick == null || r.Count > pick.Count) pick = r;
            }
            // 沒有可加入的房 → 只寫 log(大廳不彈 Toast)。列表本來就攤在眼前,有沒有等待中的房看得出來。
            if (pick == null) { Debug.Log("[lobby] " + L("lobby.no_quick_room")); return; }
            JoinRoom(pick);
        }

        private void OnToggleFilter()
        {
            _waitingOnly = !_waitingOnly;
            ApplyFilterSprites();
            _scroll = 0;
            RefreshRows();
        }

        /// <summary>官方是兩個疊在同一格的 CheckBox:現在顯示全部 → 鈕上寫「等待舞台」(按了就篩);反之亦然。</summary>
        private void ApplyFilterSprites()
            => Reskin(_filterBtn, _filterImg,
                      _waitingOnly ? "Lobby103" : "Lobby51",
                      _waitingOnly ? "Lobby104" : "Lobby52",
                      _waitingOnly ? "Lobby105" : "Lobby83");

        /// <summary>把一顆已經建好的鈕整組換皮(三態一起換)。官方常把「兩個狀態」做成兩顆疊在同一格的鈕,
        /// 我們用一顆換圖表達 —— 換皮要走與 <see cref="SpriteBtn"/> 相同的 solo 載入,否則換完那一顆又長回白邊。</summary>
        private static void Reskin(Button btn, Image img, string normal, string hover, string pushed)
        {
            if (btn == null) return;
            var n = LobbyArt.AnSoloAA(normal);
            var h = LobbyArt.AnSoloAA(hover);
            var p = LobbyArt.AnSoloAA(pushed);
            UIKit.ApplySprite(img, n);
            var st = btn.spriteState;
            st.highlightedSprite = h != null ? h : n;
            st.pressedSprite = p != null ? p : (h != null ? h : n);
            st.selectedSprite = n;
            btn.spriteState = st;
        }

        // ---- 左下角「當前」拉開的頻道選單(官方 LOBBYPOPMENU.XML 的 chatmodemenu) ----

        /// <summary>
        /// 四個頻道,由上而下 家族 / 好友 / 當前 / 回复(官方在選單內的 y 是 5/29/53/77)。
        ///
        /// 🔴 「回复」那一項官方**借的是房間的素材**(Room206/207/208)—— 大廳這一包裡沒有對應的圖,
        ///    所以那一項要走 <see cref="RoomUiArt"/> 而不是 <see cref="LobbyArt"/>。
        /// </summary>
        private static readonly ChatChannel[] ChatMenuOrder =
            { ChatChannel.Family, ChatChannel.Friend, ChatChannel.Current, ChatChannel.Reply };

        private static readonly float[] ChatMenuRowY = { 5f, 29f, 53f, 77f };

        private void ToggleChatMenu()
        {
            if (_chatMenu == null) BuildChatMenu();
            bool show = !_chatMenu.gameObject.activeSelf;
            HideHallMenu();          // 四個選單互斥
            HideApplyMenu();
            HideExpressionMenu();
            _chatMenu.gameObject.SetActive(show);
        }

        private void HideChatMenu()
        {
            if (_chatMenu != null) _chatMenu.gameObject.SetActive(false);
        }

        private void BuildChatMenu()
        {
            _chatMenu = UIKit.NewRect(Root, "chatmodemenu");
            PlaceTopLeft(_chatMenu, ChatMenuX, ChatMenuY, 45f, 104f);

            for (int i = 0; i < ChatMenuOrder.Length; i++)
            {
                var ch = ChatMenuOrder[i];
                ChatChannelArt(ch, out var n, out var h, out var p, out bool fromRoom);
                // 同 BuildHallMenu:選單項目要用 AnSolo(尺寸不變),AnSoloAA 會裁掉透明邊讓四條之間裂開。
                System.Func<string, Sprite> res = fromRoom
                    ? (System.Func<string, Sprite>)RoomUiArt.AnSolo
                    : LobbyArt.AnSolo;
                var b = UIKit.AddSpriteButton(_chatMenu, "chatmode" + i, res(n), res(h), res(p), 2f, ChatMenuRowY[i]);
                UiHoverSfx.Attach(b, UiSfx.Menufloat);
                UiSfx.AttachClick(b);
                b.onClick.AddListener(() => SetChatChannel(ch));
            }
            _chatMenu.gameObject.SetActive(false);
        }

        /// <summary>
        /// 換頻道。🔴 **只換圖,不改真正的送出行為** —— 大廳的作用域固定是 <see cref="ChatScope.Lobby"/>
        /// (見 <see cref="OnShow"/>),這個重製版的大廳只有一條公頻。官方那四個頻道背後是家族/好友系統,
        /// 我們沒有;硬把訊息標成別的頻道只會變成「送出去自己看不到」。
        /// 密語仍然走 <c>/w 名字</c>,不佔頻道。
        /// </summary>
        /// <summary>
        /// 切聊天頻道。**與房間同一套動作**(見 <c>RoomScreen.SetChatChannel</c>)——
        /// 以前這裡只換了鈕的圖,所以按下家族/好友之後畫面上什麼都沒發生:聊天區還是全部訊息、
        /// 輸入框也沒有進家族模式(使用者回報「按了沒有跳轉、也沒有跳到家族頻道」)。
        /// 少的是這兩件:**依頻道重畫聊天區**、**進家族時自動填「/家族 」前綴**。
        /// </summary>
        private void SetChatChannel(ChatChannel channel)
        {
            var prev = _chatChannel;
            _chatChannel = channel;
            ChatChannelArt(channel, out var n, out var h, out var p, out bool fromRoom);
            if (fromRoom)
            {
                // 「回复」用的是房間的素材 → 換皮不能走 Reskin(它固定吃 LobbyArt)。
                UIKit.ApplySprite(_chatChannelImg, RoomUiArt.AnSolo(n));
                var st = _chatChannelBtn.spriteState;
                st.highlightedSprite = RoomUiArt.AnSolo(h);
                st.pressedSprite = RoomUiArt.AnSolo(p);
                st.selectedSprite = RoomUiArt.AnSolo(n);
                _chatChannelBtn.spriteState = st;
            }
            else Reskin(_chatChannelBtn, _chatChannelImg, n, h, p);
            HideChatMenu();
            RebuildChat();                        // 這一頻道要看的那些訊息(見 ShouldShowChatMessage)
            SyncChannelInputPrefix(prev, channel);
            if (_chatInput != null) _chatInput.ActivateInputField();
        }

        /// <summary>
        /// 換頻道時同步輸入框的指令前綴:進家族 → 自動填「/家族 」,游標接在後面。
        /// **離開家族不清掉** —— 「當前」是綜合台,留著前綴讓人接著打家族訊息(明打 /家族 一樣送家族)。
        /// 草稿不是空的就不動它:蓋掉使用者打到一半的字比少一個前綴糟得多。
        /// (房間那邊同名方法還要處理頭上泡與回顯,大廳沒有那兩樣東西,所以只剩這一段。)
        /// </summary>
        private void SyncChannelInputPrefix(ChatChannel from, ChatChannel to)
        {
            if (_chatInput == null || from == to || to != ChatChannel.Family) return;
            if (!string.IsNullOrWhiteSpace(_chatInput.text)) return;
            _chatInput.text = RoomChatCommand.GuildCommandPrefix;
            _chatInput.ActivateInputField();
            _chatInput.MoveTextEnd(false);
        }

        /// <summary>
        /// 這一則訊息在**目前這個頻道**看不看得到 —— 規則逐條照房間那份
        /// (<c>RoomScreen.ShouldShowChatMessage</c>),差別只有作用域:那邊留房間的、這邊留大廳的。
        ///
        /// 「當前」= 綜合台:家族、密語、系統、一般聊天全都看得到;家族/好友/回覆則只看各自那一類。
        /// </summary>
        private bool ShouldShowChatMessage(ChatMessage m)
        {
            if (m == null) return false;
            bool all = _chatChannel == ChatChannel.Current;

            // 這三種是本機產生的提示行/家族訊息,**不看作用域**(在房間打的家族話回大廳也該看得到)。
            if (m.Notice == ChatNotice.SelfTalk) return all || _chatChannel == ChatChannel.Friend;
            if (m.Notice == ChatNotice.NoGuild) return all || _chatChannel == ChatChannel.Family;
            if (m.Guild) return all || _chatChannel == ChatChannel.Family;
            // 密語跨大廳/房間,出現在「當前」與「好友」。
            if (m.Whisper != WhisperKind.None) return all || _chatChannel == ChatChannel.Friend;

            // 其餘:進出舞台廣播只屬房間;一般/系統訊息只留大廳作用域(隔離房間與別房的話)。
            if (m.Stage != StageEventKind.None) return false;
            if (m.Scope != ChatScope.Lobby) return false;
            if (m.System) return true;
            return all || m.Channel == _chatChannel;
        }

        private static void ChatChannelArt(ChatChannel channel, out string nrm, out string hov, out string psh,
                                           out bool fromRoom)
        {
            fromRoom = false;
            switch (channel)
            {
                case ChatChannel.Family: nrm = "Lobby127"; hov = "Lobby128"; psh = "Lobby129"; break;
                case ChatChannel.Friend: nrm = "Lobby106"; hov = "Lobby107"; psh = "Lobby108"; break;
                case ChatChannel.Reply: nrm = "Room206"; hov = "Room207"; psh = "Room208"; fromRoom = true; break;
                default: nrm = "Lobby57"; hov = "Lobby58"; psh = "Lobby59"; break;
            }
        }

        // ---- 表情盤(官方 LOBBYPOPMENU.XML 的 expression PopMenu,165×152) ----

        private void ToggleExpressionMenu()
        {
            if (_exprMenu == null) BuildExpressionMenu();
            bool show = !_exprMenu.gameObject.activeSelf;
            HideHallMenu();
            HideApplyMenu();
            HideChatMenu();
            _exprMenu.gameObject.SetActive(show);
            if (show) RebuildExpressionMenu();   // 換過頁之後再打開要回到當下那一頁的內容
        }

        private void HideExpressionMenu()
        {
            if (_exprMenu != null) _exprMenu.gameObject.SetActive(false);
        }

        /// <summary>
        /// 表情盤的框。官方 XML 給的是 PopMenu 自己的 (0,0) 165×152,實際位置要對齊表情鈕:
        /// 水平置中於鈕(鈕 33 寬 → 中心 458+16.5=474.5 → 選單左緣 474.5−82.5=392)、
        /// 底邊貼著鈕的上緣(566−152=414)。與房間那盤同一個擺法(見 RoomScreen.BuildExpressionMenu)。
        ///
        /// 🔴 素材走 <see cref="RoomUiArt"/> 而不是 LobbyArt:表情盤的圖(EXPRESSIONINFO.PNG)在 ROOMPOPMENU
        ///    那一包,大廳與房間**共用同一份**(表情系統本來就是同一套,連 id 都是同一組)。
        /// </summary>
        private void BuildExpressionMenu()
        {
            _exprMenu = UIKit.NewRect(Root, "expression");
            PlaceTopLeft(_exprMenu, ExprMenuX, ExprMenuY, 165f, 152f);
            _exprMenu.gameObject.SetActive(false);
            RebuildExpressionMenu();
        }

        private void RebuildExpressionMenu()
        {
            if (_exprMenu == null) return;
            UIKit.Clear(_exprMenu);

            UIKit.AddSprite(_exprMenu, "ExpressionInfo", RoomUiArt.ExpressionInfoPage(_exprPage), 0f, 20f);
            UIKit.AddSprite(_exprMenu, "NormalExp", RoomUiArt.ExpressionNormalTab(selected: true), 5f, 3f);

            var left = RoomUiArt.ExpressionPageArrowFrames(left: true);
            var right = RoomUiArt.ExpressionPageArrowFrames(left: false);
            var prev = UIKit.AddSpriteButton(_exprMenu, "preexp", left[0], left[1], left[2], 103f, 131f);
            UiSfx.AttachClick(prev);
            prev.onClick.AddListener(() => StepExpressionPage(-1));
            var next = UIKit.AddSpriteButton(_exprMenu, "nextexp", right[0], right[1], right[2], 146f, 131f);
            UiSfx.AttachClick(next);
            next.onClick.AddListener(() => StepExpressionPage(1));

            int pages = Mathf.Max(1, RoomChatCommand.TotalExpressionPages);
            var pageColor = new Color32(0xBB, 0x20, 0x77, 0xFF);   // XML 的 0xffbb2077
            var cur = UIKit.AddText(_exprMenu, "CurrentPage", Mathf.Clamp(_exprPage + 1, 1, pages).ToString(),
                                    12f, pageColor, TextAlignmentOptions.Center);
            PlaceTopLeft(cur.rectTransform, 118f, 133f, 12f, 12f);
            var sep = UIKit.AddText(_exprMenu, "PageSlash", "/", 12f, pageColor, TextAlignmentOptions.Center);
            PlaceTopLeft(sep.rectTransform, 127f, 133f, 10f, 12f);
            var total = UIKit.AddText(_exprMenu, "TotalPage", pages.ToString(), 12f, pageColor, TextAlignmentOptions.Center);
            PlaceTopLeft(total.rectTransform, 136f, 133f, 12f, 12f);

            // 6×4 的格子。圖是烤在背板上的 → 每一格只放一塊透明的命中區(官方 XML 的 BtExpSel_* 也是 empty.an)。
            for (int slot = 0; slot < RoomChatCommand.ExpressionsPerPage; slot++)
            {
                int id = RoomChatCommand.ExpressionAtMenuSlot(_exprPage, slot);
                if (id <= 0) continue;
                var hit = UIKit.AddImage(_exprMenu, "BtExpSel_" + slot, new Color(1f, 1f, 1f, 0.001f), raycast: true);
                PlaceTopLeft(hit.rectTransform, 4f + (slot % 6) * 26f, 24f + (slot / 6) * 26f, 24f, 24f);
                var b = hit.gameObject.AddComponent<Button>();
                b.targetGraphic = hit;
                b.transition = Selectable.Transition.None;
                UiSfx.AttachClick(b);
                int captured = id;
                b.onClick.AddListener(() =>
                {
                    // 🔴 點表情是**把它的指令字插進打字框**,不是直接送出(使用者要求,與房間一致):
                    //    玩家常常要「表情 + 一句話」一起送,直接送出就沒機會補字了。
                    //    送出時 MockChatService / OnlineChatService 會把那段指令解析回 expressionId
                    //    (見 RoomChatCommand.TryParseExpression),所以走這條路與直接送的結果相同。
                    InsertIntoChatInput(RoomChatCommand.ExpressionDisplayText(captured));
                    HideExpressionMenu();
                });
            }
        }

        /// <summary>把一段文字插到打字框目前游標的位置(沒有 focus 就接在最後),然後把 focus 交回輸入框。</summary>
        private void InsertIntoChatInput(string text)
        {
            if (_chatInput == null || string.IsNullOrEmpty(text)) return;
            string cur = _chatInput.text ?? "";
            int at = Mathf.Clamp(_chatInput.stringPosition, 0, cur.Length);
            // 前面已經有字時補一個空白,免得表情與前一個字黏在一起變成別的指令。
            string sep = at > 0 && !char.IsWhiteSpace(cur[at - 1]) ? " " : "";
            _chatInput.text = cur.Substring(0, at) + sep + text + " " + cur.Substring(at);
            _chatInput.stringPosition = at + sep.Length + text.Length + 1;
            _chatInput.ActivateInputField();
        }

        private void StepExpressionPage(int delta)
        {
            int pages = Mathf.Max(1, RoomChatCommand.TotalExpressionPages);
            _exprPage = (_exprPage + delta) % pages;
            if (_exprPage < 0) _exprPage += pages;
            RebuildExpressionMenu();
        }

        /// <summary>
        /// 輸入框的設定 —— **與房間那顆完全同一套**(見 <c>RoomScreen.ConfigureRoomChatInput</c>)。
        /// 以前大廳只設了 onSubmit,結果沒有游標、又留著一行提示字,看起來不像可以打字的地方(使用者回報)。
        ///
        /// 其中兩項不是美觀而是行為:
        ///   • <c>onFocusSelectAll = false</c> —— 預設 true 時每次重新取得 focus 都會整行反白、游標跳回最前面。
        ///   • <c>richText = true</c> —— TMP 只有在 richText 開著時才會把 IME 組字串包成 &lt;u&gt;…&lt;/u&gt;,
        ///     也就是注音選字階段那條底線。送出走的是 raw text,所以不影響內容。
        /// </summary>
        private void ConfigureChatInput()
        {
            if (_chatInput == null) return;
            _chatInput.characterLimit = 50;
            _chatInput.onFocusSelectAll = false;
            _chatInput.customCaretColor = true;
            _chatInput.caretColor = Color.white;
            _chatInput.caretWidth = 2;
            _chatInput.caretBlinkRate = 0.85f;
            _chatInput.richText = true;
            if (_chatInput.textComponent != null) _chatInput.textComponent.richText = true;
            _chatInput.selectionColor = new Color(1f, 1f, 1f, 0.28f);
            _chatInput.onSubmit.AddListener(_ => SendChat());

            if (_chatInput.textViewport != null)
            {
                _chatInput.textViewport.offsetMin = new Vector2(5f, 4f);
                _chatInput.textViewport.offsetMax = new Vector2(-5f, -4f);
            }
            if (_chatInput.textComponent != null)
            {
                _chatInput.textComponent.color = Color.white;
                _chatInput.textComponent.fontSize = 13f;
                _chatInput.textComponent.alignment = TextAlignmentOptions.MidlineLeft;
                _chatInput.textComponent.margin = Vector4.zero;
            }

            // 🔴 **自畫游標**。TMP 內建的 caret 在這個專案的執行期組合(執行期載入的 CJK 字型 + world-space canvas)
            //    算不出可見的寬高,畫不出來 —— 使用者回報「打字時沒有光標」就是它。房間畫面早就踩過同一個坑
            //    (見 RoomScreen 的 _chatCaret),這裡照抄:把內建 caret 設成全透明,自己擺一根白色細長條。
            _chatInput.caretColor = new Color(1f, 1f, 1f, 0f);
            if (_chatInput.textViewport != null)
            {
                _chatCaret = UIKit.AddImage(_chatInput.textViewport, "TypingCaret", Color.white, raycast: false);
                var caretRt = _chatCaret.rectTransform;
                caretRt.anchorMin = caretRt.anchorMax = new Vector2(0f, 0.5f);
                caretRt.pivot = new Vector2(0f, 0.5f);
                caretRt.sizeDelta = new Vector2(2f, 15f);
                caretRt.anchoredPosition = new Vector2(2f, 0f);
                _chatCaret.gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// 自畫游標的每幀更新(擺在目前輸入位置、閃爍、順便告訴系統 IME 候選視窗該出現在哪)。
        /// 與 <c>RoomScreen.UpdateChatCaret</c> 同一套,但大廳沒有「頭上泡打字模式」→ 少一組狀態判斷。
        /// </summary>
        private void UpdateChatCaret()
        {
            if (_chatCaret == null || _chatInput == null) return;
            if (!_chatInput.isFocused)
            {
                if (_chatCaret.gameObject.activeSelf) _chatCaret.gameObject.SetActive(false);
                return;
            }

            // 游標擺在「已上屏的字(到 stringPosition 為止)+ IME 組字串」的尾端 —— 往回移或中間刪都跟得上。
            string committed = _chatInput.text ?? "";
            int caretPos = Mathf.Clamp(_chatInput.stringPosition, 0, committed.Length);
            string comp = Input.compositionString ?? "";
            string upTo = committed.Substring(0, caretPos) + comp;
            float w = (_chatInput.textComponent != null && upTo.Length > 0)
                ? _chatInput.textComponent.GetPreferredValues(upTo).x : 0f;
            // 字比框寬時 TMP 會把整段文字往左推,游標要跟著那個位移走(見 InputCaretMetrics)。
            float shift = _chatInput.textComponent != null
                ? _chatInput.textComponent.rectTransform.anchoredPosition.x : 0f;
            float viewW = _chatInput.textViewport != null ? _chatInput.textViewport.rect.width : 0f;
            _chatCaret.rectTransform.anchoredPosition =
                new Vector2(InputCaretMetrics.CaretX(2f, w, shift, viewW, _chatCaret.rectTransform.sizeDelta.x), 0f);
            if (!_chatCaret.gameObject.activeSelf) _chatCaret.gameObject.SetActive(true);

            // 閃爍:0.55 秒亮、0.45 秒暗(與房間同拍)。
            bool on = Mathf.Repeat(Time.unscaledTime, 1f) < 0.55f;
            var c = _chatCaret.color; c.a = on ? 1f : 0f; _chatCaret.color = c;

            // 自製輸入框要自己告訴系統「文字游標在螢幕哪裡」,注音/拼音的候選視窗才會跟著游標跑。
            var canvas = _chatCaret.rectTransform.GetComponentInParent<Canvas>();
            Camera cam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay) ? canvas.worldCamera : null;
            _chatCaret.rectTransform.GetWorldCorners(_caretCorners);
            Input.compositionCursorPos = RectTransformUtility.WorldToScreenPoint(cam, _caretCorners[0]);
        }

        // ---- 玩家名單(官方 win3) ----

        private void BuildUserPanel()
        {
            _userPanel = Root.gameObject.AddComponent<LobbyUserPanel>();
            _userPanel.Build(Root);
            // 名單滑進來時把左邊的 3D 角色藏起來、滑出去時放回來(使用者要求)。
            // 🔴 只關 RawImage,**不要**拆掉 GenderPreview3D —— 那是一整隻 3D 角色 + 相機 + RT,
            //    每開關一次名單就重建會卡一下,而且穿搭要重新解析。
            _userPanel.VisibilityChanged = on =>
            {
                if (_previewImg != null) _previewImg.enabled = !on;
                if (!on) CloseUserMenu();   // 名單滑走了,選單不能留在畫面上
            };
            _userPanel.RowRightClicked = ShowUserMenu;
        }

        // ---- 名單右鍵選單(官方:玩家信息 / 私聊 / 加為好友 / 加入黑名單)----
        //
        // 🔴 選單建在 **Root** 而不是名單面板底下:面板整塊會滑進滑出(win3 的 TransForm),選單掛在它身上
        //    會跟著飄走;而列本身還在捲動區的遮罩裡,掛在列上會被裁掉。
        // 🔴 官方選單裡還有「發送短信」與「使用迷魂藥 / 清醒藥」—— 這個重製版沒有簡訊也沒有道具效果,
        //    所以不畫(見 PlayerContextMenu 的註解:按了沒反應的項目比少一項更糟)。
        private GameObject _userMenu;
        private int _userMenuFrame = -1;

        private void ShowUserMenu(NetUserListEntry u)
        {
            CloseUserMenu();
            string who = (u.Name ?? "").Trim();
            if (who.Length == 0) return;

            var me = ProfileManager.Active;
            var net = Ctx != null ? Ctx.Net : null;
            bool online = net != null && net.IsConnected;
            // 「是不是自己」兩個條件都要看:userId 是這次連線的唯一編號(最可靠),
            // 但離線時大家都是 0 → 那時只有名字比對得準(同 OnAddFriend 的判斷)。
            bool isSelf = (online && u.UserId == net.UserId)
                          || string.Equals(who, SelfName(), System.StringComparison.OrdinalIgnoreCase);
            var actions = PlayerContextMenu.For(online, isSelf, FriendList.IsFriend(me, who),
                                                BlockList.IsBlocked(me, who));
            if (actions.Length == 0) return;

            var cam = FrontendApp.Instance != null ? FrontendApp.Instance.UiCam : null;
            var profile = new PlayerProfile("", who, u.Level, u.Guild ?? "");
            _userMenu = SdoPopupMenu.Build(Root, "UserMenu", Input.mousePosition, cam, actions.Length,
                                           i => PlayerMenuLabels.Of(actions[i]),
                                           i =>
                                           {
                                               bool changed = PlayerMenuActions.Run(actions[i], who, profile,
                                                                                    u.Gender, u.UserId, isSelf);
                                               CloseUserMenu();
                                               // 加好友/封鎖之後名單要立刻反映(那個人出現在好友頁、或從
                                               // 好友頁消失、或出現在黑名單頁)—— 這就是「成功了」的回饋,
                                               // 大廳不彈 Toast(使用者要求)。
                                               if (changed && _userPanel != null) _userPanel.Refresh();
                                           });
            _userMenuFrame = Time.frameCount;
        }

        private void CloseUserMenu()
        {
            if (_userMenu != null) { Destroy(_userMenu); _userMenu = null; }
        }

        /// <summary>點到選單外面就收掉(彈出那一幀不算 —— 觸發它的正是那一次點擊)。</summary>
        private void CloseUserMenuOnOutsideClick()
        {
            if (_userMenu == null || Time.frameCount == _userMenuFrame) return;
            if (!Input.GetMouseButtonDown(0) && !Input.GetMouseButtonDown(1)) return;
            var cam = FrontendApp.Instance != null ? FrontendApp.Instance.UiCam : null;
            if (SdoPopupMenu.ClickedOutside(_userMenu, Input.mousePosition, cam)) CloseUserMenu();
        }

        /// <summary>三人頭(ListShow)開名單、單人頭(AvtShow)收回去看角色 —— 官方兩顆疊在同一格輪換。</summary>
        private void ToggleUserPanel()
        {
            if (_userPanel == null) return;
            bool show = !_userPanel.Visible;
            _userPanel.SetVisible(show);
            // 展開時鈕換成 AvtShow(單人頭)= 「按了回去看角色」,收起來時換回 ListShow(三人頭)。
            Reskin(_userListBtn, _userListImg,
                   show ? "Lobby157" : "Lobby160",
                   show ? "Lobby158" : "Lobby161",
                   show ? "Lobby159" : "Lobby162");
            if (show) RequestOnlineUsers();   // 開的那一刻先要一份新的,不必等下一個輪詢節拍
        }

        /// <summary>
        /// 跟 server 要一份線上名單。與房間列表同一個模式:沒有上下線推播,只能自己回頭問
        /// (所以 <see cref="Update"/> 的輪詢也順手要一次)。
        ///
        /// 離線時 server 不存在 → 名單裡只有自己那一列。空著會讓人以為是壞了,而「只有你自己在線上」
        /// 正是單機模式的事實。
        ///
        /// 🔴 **線上時也要先墊一份本機名單**(使用者回報「名單什麼都沒有,連自己都沒有」)。
        ///    <see cref="NetClient.RequestUserList"/> 是問答式的:回呼要等 server 回話才觸發,而它
        ///    **可能永遠不觸發** —— 連線半死、封包掉了、server 那頭忙著,都會讓名單一直停在空的狀態
        ///    (第一次打開面板時 <c>_users</c> 本來就是空的,沒人補就是一片空白)。所以只要名單還空著,
        ///    就先把「自己 + dev 假人」擺上去,server 回話之後再整份換掉。
        ///    用 <see cref="LobbyUserPanel.UserCount"/> 判斷而不是自己記旗標:名單一旦有東西就不再墊,
        ///    4 秒一次的輪詢才不會出現「server 名單 ↔ 本機名單」來回閃。
        /// </summary>
        private void RequestOnlineUsers()
        {
            if (_userPanel == null || !_userPanel.Visible) return;

            var net = Ctx != null ? Ctx.Net : null;
            if (net == null || !net.IsConnected) { _userPanel.SetUsers(SelfPlusFakes(0), 0, SelfName(), SelfGuild(), SelfGuildEmblem()); return; }

            if (_userPanel.UserCount == 0)
                _userPanel.SetUsers(SelfPlusFakes(net.UserId), net.UserId, SelfName(), SelfGuild(), SelfGuildEmblem());

            int gen = _listGen;
            net.RequestUserList(users =>
            {
                // 同房間列表:回呼可能在離開大廳/登出之後才到,那份資料屬於上一次的畫面。
                if (this == null || gen != _listGen || _userPanel == null) return;
                _offlineUsers.Clear();
                _offlineUsers.AddRange(users);
                // server 照理會把自己也算進去(見 Hub.OnUserList),但**不能賭** —— 名單裡沒有自己
                // 就等於使用者回報的那個症狀。少了就自己補一列擺在最上面。
                if (!ListHasUser(_offlineUsers, net.UserId)) _offlineUsers.Insert(0, SelfEntry(net.UserId));
                // dev 假玩家線上也要補(同 RefreshRows 的假房間)—— 不然連著 server 時名單只有自己一列,
                // 四個分頁的版位一樣校不了。
                if (FakeLobbyData) AddFakeUsers();
                _userPanel.SetUsers(_offlineUsers, net.UserId, SelfName(), SelfGuild(), SelfGuildEmblem());
            });
        }

        /// <summary>本機湊出來的一份名單:自己 + (dev 開著時的)假玩家。離線時這就是全部,
        /// 線上時它只是 server 回話之前的墊檔。</summary>
        private List<NetUserListEntry> SelfPlusFakes(int selfUserId)
        {
            _offlineUsers.Clear();
            _offlineUsers.Add(SelfEntry(selfUserId));
            if (FakeLobbyData) AddFakeUsers();
            return _offlineUsers;
        }

        /// <summary>「自己」那一列。離線時 <paramref name="userId"/> 給 0(沒有連線編號),
        /// 線上時給 <c>net.UserId</c> —— 名單靠它把自己標成粗體、也靠它擋掉「把自己加成好友」。</summary>
        private NetUserListEntry SelfEntry(int userId)
        {
            var p = ProfileManager.Active;
            return new NetUserListEntry
            {
                UserId = userId,
                Name = SelfName(),
                Guild = SelfGuild(),
                GuildEmblem = SelfGuildEmblem(),
                Level = ProfileFields.PlayerLevelValue(p),
                Gender = Ctx != null && Ctx.Session != null && Ctx.Session.Gender == 1 ? 1 : 0,
                RoomSeq = -1,   // -1 = 在大廳(門牌從 000 起算,0 是一間真的房)
            };
        }

        private static bool ListHasUser(List<NetUserListEntry> list, int userId)
        {
            for (int i = 0; i < list.Count; i++) if (list[i].UserId == userId) return true;
            return false;
        }

        /// <summary>
        /// 假玩家(<see cref="FakeLobbyData"/> 開著時才有)。四個分頁都要有東西可看,所以這批人刻意做成:
        ///   • 十幾個 → 名單塞得滿、捲軸握把有得跑;
        ///   • 前三個名字與**本機好友清單**比對得上(FriendList 認的是名字)→「好友」分頁不會是空的;
        ///   • 中間三個家族設成與自己相同 →「家族」分頁不會是空的;
        ///   • 位置一半在大廳(RoomSeq=-1)、一半在房裡 → 那一欄的兩種樣子都看得到。
        /// </summary>
        private void AddFakeUsers()
        {
            var owner = ProfileManager.Active;
            var friends = FriendList.Names(owner);
            string myGuild = SelfGuild();
            string myEmblem = SelfGuildEmblem();

            for (int i = 0; i < FakeUserNames.Length; i++)
            {
                // 前三個優先借用真的好友名字(好友清單有多少就借多少),其餘用假名字池。
                string name = i < friends.Length && i < 3 ? friends[i] : FakeUserNames[i];
                _offlineUsers.Add(new NetUserListEntry
                {
                    UserId = 1000 + i,
                    Name = name,
                    // 中間三個掛自己的家族(自己沒家族時就留空 —— 那時「家族」分頁本來就該是空的)。
                    Guild = (i >= 3 && i <= 5) ? myGuild : "",
                    // 徽章也要跟著,否則同族判定(名字+徽章)會把這批假同族篩掉。
                    GuildEmblem = (i >= 3 && i <= 5) ? myEmblem : "",
                    Level = 1 + i * 7,
                    Gender = i % 2,
                    RoomSeq = (i % 2 == 0) ? -1 : (i / 2),
                });
            }
        }

        private static readonly string[] FakeUserNames =
        {
            "ゲ鱼鳞ゲ", "司狼†v撩冰", "派头姑娘つ", "ㄘ--宪◎岁", "ㄣЕ†n.ㄋ月光", "..·狞 摞..",
            "Elodie yin", "一呸,臭狗ト的。", "大熊舞咪匠★", "櫻花", "舞星", "風之舞", "Neo",
        };

        private string SelfName()
        {
            string name = Ctx != null && Ctx.Session != null ? Ctx.Session.LocalPlayerName : null;
            if (string.IsNullOrEmpty(name))
            {
                var p = ProfileManager.Active;
                if (p != null) name = p.name;
            }
            return name ?? "";
        }

        private static string SelfGuild() => ProfileFields.FamilyName(ProfileManager.Active);

        /// <summary>本機的家族徽章。同族 = 名字+徽章都一樣(見 <see cref="Sdo.Net.GuildIdentity"/>),
        /// 所以名單那邊要連它一起拿到,不然「家族」分頁會把同名不同族的人也列進來。</summary>
        private static string SelfGuildEmblem() => ProfileFields.FamilyEmblem(ProfileManager.Active);

        private void OnLogout()
        {
            if (ScreenTransition.Busy) return;
            // AppContext.Logout 會斷線、把房間/聊天換回單機那份,並發 OnlineChanged。
            //
            // 🔴 Logout 必須跟 GoTo **一起關在黑幕底下**(ScreenTransition.Run 的 swap 是全黑那一刻才跑的)。
            //    先 Logout 再開轉場的話,OnOnlineChanged 會當場重讀列表 → 大廳這一幀就換成單機那份
            //    (MockRoomService 的示範房「DanceKing的舞蹈盒」那批),而漸黑要 0.2 秒才蓋滿 → 玩家看得到
            //    測試房閃一下才進黑幕。擺進 swap 就完全看不見:重畫的大廳被黑幕蓋著,緊接著 GoTo 就切走了。
            ScreenTransition.Run(() =>
            {
                Ctx.Logout("userLogout");
                GoTo(ScreenId.GenderSel);
            });
        }

        // ================================================================ 自己的角色資料

        private void RefreshSelf()
        {
            var p = ProfileManager.Active;

            string name = Ctx != null && Ctx.Session != null ? Ctx.Session.LocalPlayerName : null;
            if (string.IsNullOrEmpty(name) && p != null) name = p.name;
            _selfName.SetText(name ?? "");   // face + 16 個描邊複本一起換(見 OutlinedLabel)

            // 等級走 ProfileFields(config.ini 是 Default、角色自己設過就以角色的為準);沒設 → 這一格留白。
            // 這裡刻意**不**用 LevelLabel:那個會回「LV:11」,而背板左邊已經烤了「等级」→ 會變成「等级 LV:11」。
            _selfLevel.text = ProfileFields.PlayerLevel(p);

            // 背板左排烤的是「等级 / 经验值 / G币 / M币 / P币」→ 每一格只放數值。
            var w = p != null ? p.wallet : null;
            _selfPoints.text = w != null ? w.points.ToString(CultureInfo.InvariantCulture) : "";   // G幣
            _selfCoins.text = w != null ? w.coins.ToString(CultureInfo.InvariantCulture) : "";     // M幣
            _selfBonus.text = w != null ? w.bonus.ToString(CultureInfo.InvariantCulture) : "";     // P幣

            // 勝率那格背板已經寫著「胜率」→ 只放數字。
            var st = p != null ? p.stats : null;
            _selfWin.text = st != null ? Two(st.WinRate) + "%" : "";
            _selfRecord.text = st != null ? LocalizationManager.Get("lobby.record", st.wins, st.losses) : "";

            // 知名度:購物累加的那個值,顯示成官方的「LV 2 (15)」——等級由累計值查表(FameLevel)。
            // (以前這一格放的是命中率,那是照著一段錯註解擺的;命中率在個人資料頁本來就有。)
            _selfFame.text = p != null ? FameLevel.Label(p.fame) : "";

            // 經驗值:每局結算加進 profile.json(ProfileManager.AddExperience),這裡只是把「本級已得 / 本級所需」
            // 換成百分比。式子在 ProfileFields.ExpPercent —— 個人資料頁那條經驗條讀的是同一個入口,
            // 兩個地方對同一份存檔算出不同的百分比是不能接受的。
            SetExpRate(ProfileFields.ExpPercent(p));
        }

        /// <summary>
        /// 經驗條 + 壓在它上面的百分比(<paramref name="percent"/> 0..100,官方 ProgressBar 的 minrange/maxrange)。
        /// 條是 Filled 的 Lobby60,由左往右填。
        ///
        /// 🔴 文字是**整數,不帶小數點**(使用者指定)—— 那格只有 84px 寬、字 11px,「99.9%」比「99%」
        ///    多兩個字元,壓在條上就開始擠。取整用**無條件捨去**不是四捨五入:99.6% 顯示成 100%
        ///    會變成「條滿了卻還沒升級」。勝率那格是小數兩位(見 <see cref="Two"/>),兩者無關。
        /// </summary>
        private void SetExpRate(float percent)
        {
            percent = Mathf.Clamp(percent, 0f, 100f);
            if (_expFill != null) _expFill.fillAmount = percent * 0.01f;
            if (_selfExpRate != null)
                _selfExpRate.text = Mathf.FloorToInt(percent).ToString(CultureInfo.InvariantCulture) + "%";
        }

        /// <summary>小數兩位。用 InvariantCulture —— 跟著系統地區走的話,同一個畫面會出現「62.50%」與「62,50%」兩種寫法。</summary>
        private static string Two(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

        // ================================================================ 聊天

        private void RebuildChat()
        {
            UIKit.Clear(_chatContent);
            var hist = Ctx != null && Ctx.Chat != null ? Ctx.Chat.History : null;
            if (hist != null) foreach (var m in hist) AddChatLine(m);
            ScrollChatToBottom();
        }

        private void OnChatMessage(ChatMessage m)
        {
            // 🔴 只有「原本就貼在最底」時才跟著捲到底。玩家往上拉去看舊訊息的時候,
            //    新訊息不該把畫面搶回底部(使用者回報)—— 那會讓人根本讀不完一句話。
            //    要恢復自動跟隨,把捲軸拉回最底即可(與大多數聊天視窗的慣例一致)。
            // 🔴 訊息還沒塞滿聊天區時 Unity 回的 vNP 不可信(它算不出比例,通常固定回 1),
            //    照字面判會變成「一開始就不自動捲」→ 這種情況一律當作貼在最底。
            bool wasAtBottom = _chatScroll == null || !ChatOverflows()
                               || _chatScroll.verticalNormalizedPosition <= 0.02f;
            AddChatLine(m);
            if (wasAtBottom) ScrollChatToBottom();
            else if (_chatClip != null) _chatClip.Refresh();
        }

        /// <summary>
        /// 畫一行聊天。**分類與配色與房間完全一致**(見 <c>RoomScreen.AddRoomChatLine</c> 與
        /// <see cref="ChatPalette"/>)—— 家族綠(帶 <c>&lt;家族&gt;</c> 前綴)、密語青、系統金、其餘白。
        ///
        /// 🔴 這推翻了本檔早期的「大廳整區白字」:那是上一輪使用者的要求,**現在使用者改要求兩邊同色**。
        ///    (所以不要看到 &lt;color&gt; 就以為是誤加的。)
        ///
        /// 🔴 看得看不到由 <see cref="ShouldShowChatMessage"/> 一處決定(頻道 + 作用域),不要在這裡另外
        ///    加條件 —— 那正是以前「切了家族但畫面沒變」的成因:過濾散在畫圖的路上,切頻道時沒有一條會重跑。
        /// </summary>
        private void AddChatLine(ChatMessage m)
        {
            if (!ShouldShowChatMessage(m)) return;

            if (m.Notice != ChatNotice.None) { AddNoticeLine(m); return; }
            if (m.Guild) { AddGuildLine(m); return; }
            // 密語跨大廳/房間 → 大廳也顯示(單行,青字)。
            if (m.Whisper != WhisperKind.None)
            {
                ChatLine("whisper", Wrap(ChatPalette.WhisperHex, Esc(ChatDisplay.WhisperText(m))));
                return;
            }
            // 一般行:名字白、可點 → 密語;系統訊息整行金字。
            string line = m.System
                ? Wrap(ChatPalette.SystemHex, Esc(m.Text))
                : WhisperNameLink(m) + ": " + Esc(BodyText(m));
            EnableWhisperNameClicks(ChatLine("line", line), m);
        }

        /// <summary>
        /// 一行的內容文字。**大廳這一區是純文字行**(房間/遊戲畫面才畫 emoji 小動畫),所以表情訊息要退回它的
        /// 指令文字(<c>/翻</c>)—— 表情訊息的 <c>Text</c> 只裝「指令後面的字」,直接印會變成一行只有名字。
        /// </summary>
        private static string BodyText(ChatMessage m)
        {
            if (m == null) return "";
            if (m.ExpressionId <= 0) return m.Text ?? "";
            string lead = (m.LeadingText ?? "").Trim();
            string trail = (m.Text ?? "").Trim();
            // 舊訊息把指令本身當 Text(如 "/無聊")→ 那不是尾隨字,別再印一次。
            if (RoomChatCommand.TryParseExpression(trail, out var id, out var rest)
                && id == m.ExpressionId && string.IsNullOrEmpty(rest)) trail = "";
            var parts = new System.Collections.Generic.List<string>(3);
            if (lead.Length > 0) parts.Add(lead);
            parts.Add(RoomChatCommand.ExpressionDisplayText(m.ExpressionId));
            if (trail.Length > 0) parts.Add(trail);
            return string.Join(" ", parts);
        }

        /// <summary>本機提示行:「你說: …」(白)/「你沒有家族」(綠 —— 與家族訊息同色,同房間)。</summary>
        private void AddNoticeLine(ChatMessage m)
        {
            string text, hex;
            if (m.Notice == ChatNotice.NoGuild)
            {
                text = L("room.no_guild");
                hex = ChatPalette.GuildHex;
            }
            else
            {
                text = LocalizationManager.Get("room.selftalk", m.Text ?? "");
                hex = ChatPalette.PlainHex;
            }
            ChatLine("noticeLine", Wrap(hex, Esc(text)));
        }

        /// <summary>
        /// 家族頻道的綠字行:「&lt;家族&gt;名字: 內容」。
        /// 🔴 固定前綴用 <c>&lt;noparse&gt;</c> 包住原字,**不能**走 <see cref="Esc"/>:這個環境的 TMP
        ///    不會把 <c>&amp;lt;</c> 解回 <c>&lt;</c>,跳脫過的前綴會原封不動印出「&amp;lt;家族&amp;gt;」
        ///    (房間那邊踩過同一個坑)。名字與內容照樣跳脫。
        /// </summary>
        private void AddGuildLine(ChatMessage m)
        {
            string tag = "<noparse>" + RoomChatCommand.GuildTag + "</noparse>";
            string line = "<color=#" + ChatPalette.GuildHex + ">" + tag + WhisperNameLink(m)
                        + ": " + Esc(BodyText(m)) + "</color>";
            EnableWhisperNameClicks(ChatLine("guildLine", line), m);
        }

        /// <summary>
        /// 一行的外觀:粗體 + 描邊(使用者要求,官方實機那些字都有一圈深邊)——
        /// 這一區的底是星空與 3D 角色,細字直接壓上去會有整段讀不清。
        /// </summary>
        private OutlinedLabel ChatLine(string name, string rich)
        {
            // 長串英數對 TMP 是「一個單字」,塞不下會整串跳下一排、這一排卻空著 → 先給它可折點(見 ChatSoftWrap)。
            rich = ChatSoftWrap.Apply(rich);
            var t = OutlinedLabel.CreateRich(_chatContent, name, rich, ChatFontSize, ChatEdgeCol,
                                             ChatEdgePx, ChatEdgeDirs, true, TextAlignmentOptions.TopLeft);
            // 🔴 高度**不能寫死一行** —— 長訊息折到第二行時,一行的位置容不下兩行:第二行會壓在下一則訊息上,
            //    捲到底也只捲得到第一行(房間那邊使用者回報的症狀,這裡是同一份版面)。折幾行由 TMP 量。
            float one = t.Face != null ? t.Face.GetPreferredValues(rich).y : 0f;
            float wrapped = t.Face != null ? t.Face.GetPreferredValues(rich, ChatLineWrapW, 0f).y : 0f;
            UIKit.Layout(t.gameObject, ChatLineMetrics.BlockHeight(wrapped, one, ChatLineH));
            return t;
        }

        private static string Wrap(string hex, string text) => "<color=#" + hex + ">" + text + "</color>";

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        /// <summary>聊天記錄有沒有多到要捲。差一點點(0.5px)當作沒有,避免邊界抖動。</summary>
        private bool ChatOverflows()
        {
            if (_chatScroll == null) return false;
            var c = _chatScroll.content; var v = _chatScroll.viewport;
            return c != null && v != null && c.rect.height > v.rect.height + 0.5f;
        }

        private void ScrollChatToBottom()
        {
            if (_chatScroll == null) return;
            Canvas.ForceUpdateCanvases();
            _chatScroll.verticalNormalizedPosition = 0f;
            FixedScrollbar.Sync(_chatBar, _chatScroll);   // ForceUpdateCanvases 之後才量得到正確高度
            if (_chatClip != null) _chatClip.Refresh();
        }

        /// <summary>
        /// 送出打好的那句話 —— **路由規則逐條照房間**(<c>RoomScreen.SendRoomChat</c>),
        /// 只是拿掉房間專屬的頭上泡與回顯模式。
        ///
        /// 🔴 **光把 channel 帶進 <c>Send()</c> 是不夠的**(上一版就是這樣,所以在家族頻道打字送出去
        ///    還是白字的「名字: /家族 2」):家族訊息走的是**另一個 API** <c>SendGuild</c> ——
        ///    只有它會剝掉「/家族 」前綴、標上 <c>Guild</c>(綠字 <c>&lt;家族&gt;…</c>),
        ///    也只有它在本機沒有家族時會回一行「你沒有家族」。<c>Send()</c> 不管前綴,整串當普通話送出去。
        ///
        /// 各頻道送什麼、送完在輸入框留什麼前綴(postDraft):
        ///   • 家族:剝掉「/家族」→ SendGuild;前綴留著,接著打下一句還是家族。
        ///   • 好友:帶 [名字] → 密語(前綴留著,繼續密語同一人);沒帶名字 → SendSelfTalk(白字「你說: …」)。
        ///   • 當前 / 回覆:明打「/家族 …」照樣送家族;否則 密語 &gt; 表情 &gt; 一般說話。
        /// </summary>
        /// <summary>家族頻道的內容 —— 是表情指令(<c>/翻</c>)就帶著 expressionId 送,否則當純文字。
        /// 房間(<c>RoomScreen.SendGuildText</c>)與遊戲畫面(<c>FrontendApp.SendGuildText</c>)同一套規則。</summary>
        private void SendGuildText(string body)
        {
            if (RoomChatCommand.TryParseExpression(body, out var eid, out var lead, out var trail))
                Ctx.Chat.SendGuildExpression(eid, lead, trail);
            else Ctx.Chat.SendGuild(body);
        }

        private void SendChat()
        {
            if (_chatInput == null || Ctx == null || Ctx.Chat == null) return;
            string txt = _chatInput.text;
            if (string.IsNullOrWhiteSpace(txt)) return;

            System.Action send = null;
            string postDraft = "";
            switch (_chatChannel)
            {
                case ChatChannel.Family:
                {
                    string body = RoomChatCommand.StripGuildCommand(txt);
                    if (string.IsNullOrWhiteSpace(body)) return;   // 只有「/家族 」還沒打內容 → 續打
                    send = () => SendGuildText(body);
                    postDraft = RoomChatCommand.GuildCommandPrefix;
                    break;
                }
                case ChatChannel.Friend:
                {
                    if (RoomChatCommand.TryParseWhisper(txt, out var target, out var body))
                    {
                        if (string.IsNullOrWhiteSpace(body)) return;   // 只選了對象還沒打內容 → 續打
                        send = () => Ctx.Chat.SendWhisper(target, body, ChatChannel.Friend);
                        postDraft = "[" + target + "] ";
                    }
                    else send = () => Ctx.Chat.SendSelfTalk(txt);
                    break;
                }
                default:
                {
                    if (RoomChatCommand.TryStripGuildCommand(txt, out var guildBody))
                    {
                        if (string.IsNullOrWhiteSpace(guildBody)) return;
                        send = () => SendGuildText(guildBody);
                        postDraft = RoomChatCommand.GuildCommandPrefix;
                        break;
                    }
                    bool isWhisper = RoomChatCommand.TryParseWhisper(txt, out var target, out var body);
                    if (isWhisper && string.IsNullOrWhiteSpace(body)) return;
                    if (isWhisper) send = () => Ctx.Chat.SendWhisper(target, body, _chatChannel);
                    else if (RoomChatCommand.TryParseExpression(txt, out var eid, out var lead, out var trail))
                        send = () => Ctx.Chat.SendExpression(eid, _chatChannel, lead, trail);
                    else send = () => Ctx.Chat.Send(txt, _chatChannel);
                    break;
                }
            }
            if (send == null) return;

            send();
            HideChatMenu();
            HideExpressionMenu();
            _chatInput.text = postDraft;
            _chatInput.ActivateInputField();
            if (postDraft.Length > 0) _chatInput.MoveTextEnd(false);   // 游標接在前綴後面
        }

        // ---- 點聊天區的人名 → 密語(與房間同一套:<link="w|名字">) ----

        private const string WhisperLinkId = "w|";

        /// <summary>別人講的那行,名字包成可點的 TMP link;自己說的話不用(不能對自己密語)。</summary>
        private string WhisperNameLink(ChatMessage m)
        {
            string name = Esc(m.Sender);
            if (m.Local || string.IsNullOrEmpty(m.Sender)) return name;
            return "<link=\"" + WhisperLinkId + name + "\">" + name + "</link>";
        }

        /// <summary>讓這一行吃得到點擊,並把點到的 link 解回名字。</summary>
        private void EnableWhisperNameClicks(OutlinedLabel label, ChatMessage m)
        {
            if (label == null || label.Face == null || m == null || m.Local || string.IsNullOrEmpty(m.Sender)) return;
            var t = label.Face;
            t.raycastTarget = true;
            var h = t.gameObject.AddComponent<ChatWhisperLinkHandle>();
            h.Owner = this;
            h.Text = t;
        }

        private void OnChatWhisperLinkClick(TextMeshProUGUI text, PointerEventData eventData)
        {
            if (text == null || eventData == null) return;
            var canvas = text.canvas;
            Camera cam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay) ? canvas.worldCamera : null;
            int idx = TMP_TextUtilities.FindIntersectingLink(text, eventData.position, cam);
            if (idx < 0 || idx >= text.textInfo.linkCount) return;
            string id = text.textInfo.linkInfo[idx].GetLinkID();
            if (string.IsNullOrEmpty(id) || !id.StartsWith(WhisperLinkId, System.StringComparison.Ordinal)) return;
            InsertWhisperTarget(id.Substring(WhisperLinkId.Length));
        }

        /// <summary>
        /// 把「[名字] 」放到打字框最前面 —— 送出時 <c>RoomChatCommand.TryParseWhisper</c> 會把它解成密語。
        /// 已經打到一半的內容留著(若已有舊的 [名字] 前綴就換掉,不會疊成兩個)。
        ///
        /// 🔴 只有「當前 / 好友」頻道才插入,與房間同一條規則:家族/回覆頻道有自己的前綴語意,
        ///    插進去會變成「[名字] /家族 …」那種誰也送不出去的東西。
        /// </summary>
        /// <summary>
        /// 「對這個人開始密語」—— 玩家**主動選**私聊的那條路(名單/房間信息的右鍵選單、玩家資訊視窗的私聊鈕,
        /// 都經由 <see cref="Nav.WhisperTo"/> 進來)。與點聊天列的人名同一條路,只是入口不同。
        ///
        /// 🔴 頻道守門與 <see cref="InsertWhisperTarget"/> 相同(家族/回覆頻道不插前綴)—— 這是刻意的:
        ///    在那兩個頻道插進去會變成送不出去的 "[名字] /家族 …"。房間那邊的 BeginWhisperTo 會先切頻道,
        ///    但大廳的頻道是玩家用下拉選單挑的、還會影響整片訊息的過濾,替他改掉太粗暴。
        /// </summary>
        public void BeginWhisperTo(string name) => InsertWhisperTarget(name);

        private void InsertWhisperTarget(string name)
        {
            if (_chatInput == null || string.IsNullOrWhiteSpace(name)) return;
            if (_chatChannel != ChatChannel.Current && _chatChannel != ChatChannel.Friend)
            {
                _chatInput.ActivateInputField();
                return;
            }
            string draft = _chatInput.text ?? "";
            string body = RoomChatCommand.TryParseWhisper(draft, out _, out var existing) ? existing : draft.Trim();
            string prefix = "[" + name.Trim() + "] ";
            _chatInput.text = string.IsNullOrEmpty(body) ? prefix : prefix + body;
            _chatInput.ActivateInputField();
            _chatInput.MoveTextEnd(false);   // 游標擺到結尾,接著打內容
        }

        private sealed class ChatWhisperLinkHandle : MonoBehaviour, IPointerClickHandler
        {
            public LobbyScreen Owner;
            public TextMeshProUGUI Text;

            public void OnPointerClick(PointerEventData eventData)
            {
                if (Owner != null) Owner.OnChatWhisperLinkClick(Text, eventData);
            }
        }

        // ================================================================ 小工具

        /// <summary>
        /// 官方三態鈕(normal/hover/pushed)+ 點擊音效,座標是 XML 的左上角像素。
        ///
        /// 🔴 三態一律走 <see cref="LobbyArt.AnSoloAA"/>(自貼圖 + 超取樣)而不是 <see cref="LobbyArt.An"/>:
        ///    大廳這一包幾乎整包裁自同一張 STAGE.PNG,鈕與鈕在圖裡是**貼著**的 → 共用圖集取樣時會把隔壁那顆鈕
        ///    的像素拖進邊緣,每顆鈕就鑲了一圈白邊(使用者回報的 #3)。切到自己的貼圖上就沒有鄰居可滲。
        ///
        /// <paramref name="circle"/> = 真正的圓盤鈕(右上角那排 hall*、下排的表情/喇叭/寵物/幫助):
        ///    它們的圓邊是寬軟 AA 邊,AnSoloAA 的 α&lt;128→0 硬裁會把它裁成 1-bit 圓 → 邊緣破碎;
        ///    改走 CircleMask 平滑圓邊。長方形/膠囊的鈕不要開(圓遮罩會把兩端裁掉)。
        /// </summary>
        private Button SpriteBtn(string name, string normal, string hover, string pushed, float x, float y,
                                 UnityEngine.Events.UnityAction onClick, bool circle = false)
        {
            System.Func<string, Sprite> res = circle ? (System.Func<string, Sprite>)LobbyArt.AnSoloCircleAA
                                                     : LobbyArt.AnSoloAA;
            var btn = UIKit.AddSpriteButton(Root, name, res(normal), res(hover), res(pushed), x, y);
            if (onClick != null) btn.onClick.AddListener(onClick);
            UiSfx.AttachClick(btn);
            // 滑過大廳的鈕 → SE\Buttonfloat.wav(與房間那排鈕同一個音,使用者要求大廳也要有)。
            // 選單項目不走這條(它們自己掛 Menufloat,見 BuildPopMenu)。
            UiHoverSfx.Attach(btn, UiSfx.ButtonFloat);
            return btn;
        }

        /// <summary>素材缺了的保險:把鈕變成純色塊 + 一行字,至少還按得下去。只給「創建舞台」用。</summary>
        private static void FallbackButtonSkin(Button btn, string locKey, float w, float h)
        {
            var img = btn.targetGraphic as Image;
            if (img == null) return;
            img.color = UITheme.Primary;
            img.rectTransform.sizeDelta = new Vector2(w, h);
            var label = UIKit.AddLocText(img.rectTransform, "Label", locKey, 12f, UITheme.OnPrimary,
                                         TextAlignmentOptions.Center);
            UIKit.Stretch(label, 2f, 0f, 2f, 0f);
        }

        /// <summary>XML 的 Label:左上角 (x,y)、大小 (w,h)、y 向下。</summary>
        private static TextMeshProUGUI Label(Transform parent, string name, float x, float y, float w, float h,
                                             float size, Color color, TextAlignmentOptions align)
        {
            var t = UIKit.AddText(parent, name, "", size, color, align);
            PlaceTopLeft(t.rectTransform, x, y, w, h);
            return t;
        }

        /// <summary>
        /// 右下角個人資料那兩排**填進去的數值** —— 一律粗體(使用者要求)。
        /// 官方 XML 只在 charname 標了 bold="true",其餘那些 Label 都沒有;但那塊背板的底是深紫星空、
        /// 字只有 10-11px,細體壓上去邊緣會被底色吃掉。粗體是使用者看過實機後指定的。
        /// (只給這一區用 —— <see cref="Label"/> 本身還有房卡等處在用,不要把粗體推到那邊去。)
        /// </summary>
        private static TextMeshProUGUI StatLabel(Transform parent, string name, float x, float y, float w, float h,
                                                 float size, Color color, TextAlignmentOptions align)
        {
            var t = Label(parent, name, x, y, w, h, size, color, align);
            t.fontStyle = FontStyles.Bold;
            return t;
        }

        private static void PlaceTopLeft(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x, -y);
        }

        // ================================================================ 內部型別

        /// <summary>一列房卡的所有零件。六列在 <see cref="BuildUI"/> 時就建好,之後只換圖/換字 ——
        /// 線上每 4 秒刷一次,每次都重建的話 hover 狀態會被吃掉,而且 GC 會一直跳。</summary>
        private sealed class RoomRow
        {
            public Image Card, State, Badge, Keyboard, CountD, CapD, Slash, Key;
            public Image[] Digits, Heads;
            public OutlinedLabel Name;
            public TextMeshProUGUI Song;
            public Button Btn;
            public RowHover Hover;
            public RightClickProxy RightClick;
            public RoomInfo Data;
        }

        /// <summary>
        /// 房卡的 hover 換圖。掛在「整張卡的 root」上 —— UGUI 的 pointerEnter/Exit 會沿著階層通知每一層,
        /// 所以游標壓在卡片上任何一個子元素(房名、人頭、狀態牌…)都收得到,不會出現「壓在字上就不亮」。
        /// </summary>
        private sealed class RowHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
        {
            public Image Left;
            private Sprite _normal, _hover;
            private bool _hot;

            public void SetSkin(Sprite normal, Sprite hover)
            {
                _normal = normal; _hover = hover;
                Apply();
            }

            public void OnPointerEnter(PointerEventData _) { _hot = true; Apply(); }
            public void OnPointerExit(PointerEventData _) { _hot = false; Apply(); }

            private void Apply()
            {
                UIKit.ApplySprite(Left, _hot && _hover != null ? _hover : _normal);
            }
        }
    }
}
