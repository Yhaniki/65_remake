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

        // 🔴 XML 寫 333,但那幾張分頁圖左邊各有幾 px 的透明邊 —— 照 333 擺出來,**可見**的左緣落在 338,
        //    比內容板(335)往右凸 3px(使用者回報「tab 往右歪」)。往左 4px 讓可見邊與內容板切齊。
        private const float TabX = 329f, TabY = 116f;            // <CheckBox name="playerTabCheck0..3" x="333" y="116"/>(四格疊在同一點)
        // 每一格的可點範圍(相對分頁條左緣)。量自 BaseBoard2_man.png 上未選那四張圖各自的不透明範圍:
        // 5-73 / 73-142 / 143-212 / 213-282(283 之後是官方第五格「星座」的位置,我們不畫)。
        private static readonly float[] TabPillX = { 4f, 73f, 143f, 213f, 283f };
        private const float TabPillW = 70f, TabPillH = 39f;
        // 官方男版分頁條有**五格**:Dlg4/7/10/158(基本信息/技術統計/賽事信息/拼圖卡片)在 BaseBoard2_man.png,
        // 第五格「星座守護」(ZoSelect_a/b)在**另一個圖集** Zodiac.png —— 見 PlayerInfoArt.TabStrip。
        private const int TabBasic = 0, TabStats = 1, TabMatch = 2, TabCards = 3, TabZodiac = 4, TabCount = 5;

        // 分頁內容板。官方兩頁的板子差 1-2 px(基本頁 PlayerInformationDlg34_man.an 掛在 playerTabWindow0(-1,+6)
        // → 絕對 (335,153) 348×337;技术统计頁 PlayerInformationDlg43_man.an 掛在 playerTabWindow1(+1,+6)
        // → 絕對 (337,152) 347×338)。我們只鋪一層共用的底,所以取兩者的聯集。
        private const float PanelX = 335f, PanelY = 152f;
        private const float PanelW = 349f, PanelH = 339f;

        // 內容區凹槽的右緣補條 —— 使用者連續四輪回報的「框旁邊那條黑色切錯的雜訊」。
        //
        // 🔴 **我們沒有切錯,那 12px 是官方 layout 的固有結果**(逐項量過):
        //    ・DailogBg 是 BaseBoard_man.png (0,0,625,502) 貼在 (93,56);那張圖**自己烤了一個星空凹槽**,
        //      凹槽在圖內 x=24..602 → 絕對 x **117..695**(截圖實測與素材量測完全一致)。
        //    ・分頁內容板最寬的是基本頁 348 貼在 335 → 只到 **683**。
        //    ・把 WinPlayerInfo 底下**每一個** Label 的 .an 尺寸都算過絕對右緣,最大是星座頁 ZoBG 的 686 ——
        //      官方沒有任何元素蓋得到 683..695。純用官方素材+官方座標合成出來的圖,那 12px 就是深藍星空。
        //
        // 🔴 官方之所以看不到它,是因為官方還有一個 <Label name="CharBack" x="114" y="110" w="584"
        //    background="empty.an"/> —— 執行期填入的「角色背景圖」,584 寬正好蓋滿整個凹槽(114..698),
        //    那 12px 於是變成背景圖的自然延續。我們沒有那張圖(empty.an 是空的,圖由伺服器給),
        //    所以露出底板烤死的星空;它夾在分頁板與外框之間、又緊鄰大廳那些高彩度房卡,看起來就像切壞了。
        //
        // 🔴 為什麼是補一條而不是把分頁板右移 12px:分頁板上的欄位標題(天使等級/TP值/…)全是**烤在圖上**的,
        //    板子一移,程式放的每一個數值、進度條、按鈕都要跟著移 12px 才不會與烤字錯位 ——
        //    那等於把整份逐字抄自官方 XML 的版位常數全部推翻,風險遠大於補一條 12px 的框。
        //    補條顏色取自底板凹槽自己的框線 (97,72,168),補完視覺上就是「內框粗了一點」,與右側 696..697
        //    那兩欄原生框線連成一體。
        // 🔴 範圍是**整條凹槽**(y 110..499)而不是只有分頁板那一段(152..491):凹槽在分頁板上方還露 42px,
        //    那截落在分頁條右邊,只補中間會在分頁條旁留一小塊深色,等於沒修完。
        // 🔴 左緣 679 是**量出來的,不是算出來的**:照 TabX+TabPillX[4]+TabPillW(=682)會在分頁條與補條之間
        //    留一條 3px 的縫(實測螢幕 x=877..881、y=181..226 仍是深藍)—— TabPillX/TabPillW 描述的是
        //    **可點範圍**,分頁條圖本身的可見右緣比它窄。分頁板(335..683)建在補條之後會把 679..683
        //    這段蓋回去,所以左移不會讓補條在分頁區露出來。
        // 🔴 右緣取到 697(凹槽框線的右緣)而不是 695:696..697 本來就是同色框線,蓋掉毫無差別,
        //    但可以吃掉縮放到 1024×768 時最後那一欄的抗鋸齒殘影(實測 x=897 會留一條)。
        private const float GrooveFillX = 679f, GrooveFillY = 110f;
        private const float GrooveFillW = 18f, GrooveFillH = 389f;
        private static readonly Color GrooveFrameCol = new Color32(97, 72, 168, 255);

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

        // ---- 基本信息頁(官方 playerTabWindow0,容器偏移 x=-1 y=+6;下面全是**加過偏移的絕對座標**) ----
        // 每一格的標題都烤在底圖 PlayerInformationDlg34_man.an 上,程式只放數值。
        private const float BasicBgX = 335f, BasicBgY = 153f;        // <Label background="PlayerInformationDlg34_man.an" x=336 y=147/>
        private const float ProgressW = 236f, ProgressH = 19f;
        private const float WeightBarX = 431f, WeightBarY = 124f;    // pro_weight  (432,118) TP值
        private const float AngelBarX = 432f, AngelBarY = 190f;      // pro_angel   (433,184) 天使等級
        private const float ExpBarX = 432f, ExpBarY = 221f;          // pro_exp     (433,215) 經驗值(黃)
        private const float ExpValX = 437f, ExpValY = 176f;          // exp         (438,170)
        private const int CharmCount = 12;                           // 🔴 官方 Charm1..24 是「12 個位置 × 亮/暗兩張」,不是 24 顆(x 只排到 646)
        private const float CharmX = 425f, CharmY = 249f, CharmStep = 20f;
        private const float LuckyX = 428f, LuckyY = 278f;            // lucky1..24 (429,272) 同款 12 個位置

        // 知名度那一排。官方 <zhimingdu1..10>(432,299) 22×22,加上 playerTabWindow0 的 (-1,+6) → (431,305)。
        // 🔴 step **22 = 沒有間隙,彼此貼著**(魅力值/幸運值是 20 step、圖 20 寬 —— 那兩排才有縫)。
        //    格子數就是 10,官方沒有 zhimingdu0 也沒有 11。
        private const int FameSlots = 10;
        private const float FameX = 431f, FameY = 305f, FameStep = 22f;
        private const float FamilyValX = 431f, FamilyValY = 339f;    // familyname  (432,333)
        private const float OfferValX = 602f, OfferValY = 340f;      // offer       (603,334) 家族榮譽度
        private const float IntimateValX = 429f, IntimateValY = 367f;// intimate    (430,361) 密友度
        private const float SocialValX = 426f, SocialValY = 394f;    // SendNum     (427,388) 社交值
        private const float LuckValX = 419f, LuckValY = 379f;        // luckvalue   (420,373)

        // ---- 賽事信息頁(官方 playerTabWindow2,容器偏移 x=1 y=16;下面是加過偏移的絕對座標) ----
        private const float DuanweiBarX = 438f, DuanweiBarY = 162f;   // pro_duanwei (437,146) 236×19
        private const int DuanweiCount = 12;                          // duanwei1..24 = 12 個位置 × 亮/暗
        private const float DuanweiX = 434f, DuanweiY = 196f, DuanweiStep = 20f;   // (433,180) 起,每 20px
        // 🔴 XunzhangY 以前寫 320 是**錯的**:註解說「(356,304) → 容器再 -12」,但實際只加了 +16 沒減 12。
        //    playerxunzhang 這一層是 (0,-12),所以累加偏移是 (1,+4) → 304+16-12 = **308**。
        private const float XunzhangX = 357f, XunzhangY = 308f;       // AvtXunzhang1 (356,304) + (1,+4)
        private const float XunzhangStepX = 52f, XunzhangStepY = 53f; // 官方 356/408/460/512/564/616、304/357
        private const float XunzhangW = 49f, XunzhangH = 48f;

        // 大底板。星座/寵物頁是 PlayerInformationDlg54_man、家族頁是 playerfamilly1_man ——
        // 🔴 兩者是**同一塊裁切** (350,685,346,339),只是名字不同、y 差 1px(151 / 152)。
        //    官方切分頁時底板會抖一格,那是 bug,統一取 151 不要複製。
        private const float MatchBoardX = 337f, MatchBoardY = 151f;
        // 6×2 勳章格底圖(星座/寵物共用)與 5×2 家族徽章格底圖。
        private const float XunzhangBgX = 348f, XunzhangBgY = 295f;
        private const float FamillyBgX = 350f, FamillyBgY = 296f;
        // 三個子分頁。官方三個 CheckBox **全部擺在同一點**,各自的圖只畫自己那一格、其餘透明
        // (與最上面那條分頁條同一套疊圖法)。
        private const float SubTabX = 350f, SubTabY = 264f;
        private const int SubFamily = 0, SubZodiac = 1, SubPet = 2, SubTabCount = 3;
        // 家族徽章格:5×2,官方 Avtfamilly1..10 (356,304) 52×52。
        private const float FamillyX = 356f, FamillyY = 304f, FamillyStepX = 56f, FamillyStepY = 57f;
        private const float FamillySlot = 52f;
        private const float EmblemNumX = 380f, EmblemNumY = 344f, EmblemNumStepX = 58f, EmblemNumStepY = 57f;
        // 「0 /50」。🔴 那條斜線**是烤在 playerfamilly2_man 上的**(abs 498..502),只放兩個數字。
        private const float EmblemNowX = 477f, EmblemAllX = 504f, EmblemNumRowY = 424f;
        // 捲軸:元素 (639,297,25,137),但底圖畫死的軌道只有 abs x 647..651 / y 321..416。
        private const float EmblemRailX = 643f, EmblemRailTop = 321f, EmblemRailH = 96f, EmblemHandleH = 40f;
        // 這頁的文字欄(全部是官方原始座標,幾個看起來沒對齊的都是官方自己畫的)。
        private const float DuanweiDianX = 517f, DuanweiDianY = 168f;   // 段位點,坐在進度條上
        private const float WeeklyWinX = 614f, WeeklyWinY = 227f;
        private const float BestScoreX = 449f, BestScoreY = 249f;
        private const float EmblemLabX = 447f, EmblemLabY = 231f;
        // 三格子分頁的**可點範圍**(量出來的可見框,不是 .an 的整條寬度 —— 三張圖都是整條 322-328 寬、
        // 只有自己那一格不透明,所以命中區要自己給)。
        private static readonly float[] SubTabHitX = { 350f, 458f, 564f };
        private static readonly float[] SubTabHitW = { 108f, 106f, 108f };

        /// <summary>還沒接上官方素材的空格子(目前只剩拼圖頁在用)。官方那些格是 AvtShow(3D 模型位),
        /// 沒有模型就先用一層淡底把「這裡會有東西」畫出來 —— 使用者要求沒資料也要看得到版面。</summary>
        private static readonly Color XunzhangSlotCol = new Color(0f, 0f, 0f, 0.28f);

        // ---- 拼圖卡片頁(官方 playerTabWindow3 的 PinTuTab,容器偏移 y=-8;裡層 PinTuTabWindow 再 x=2 y=-6) ----
        private const int CardTabCount = 8;                            // PinTuTabCheck0..7
        private const float CardTabX = 348f, CardTabY = 154f, CardTabStep = 36f;   // (348,162) 起,每 36px
        private const float CardDoneX = 621f, CardAllX = 642f, CardDoneY = 157f;   // Complete0/All0 (619/640,171)
        /// <summary>六格拼圖的 x,y,w,h(官方 PinTu0_0..5,寬高不一致 —— 照抄,不要用公式)。</summary>
        private static readonly float[,] CardSlot =
        {
            { 405f, 193f, 122f, 111f }, { 485f, 193f, 128f, 111f }, { 573f, 193f, 96f, 111f },
            { 405f, 270f, 122f, 131f }, { 485f, 271f, 128f, 131f }, { 574f, 270f, 96f, 132f },
        };

        // ---- 星座守護頁(官方 playerTabWindow4,容器偏移 x=-1 y=+6;下面是加過偏移的絕對座標) ----
        // 12 個星座圍成一圈,座標逐字取自 ZoTabGrayCheck0..11。
        private static readonly string[] ZodiacNames =
        {
            "Baiyang", "Jinniu", "Shuangzi", "Juxie", "Shizi", "Chunv",
            "Tianping", "tianxie", "Sheshou", "Mojie", "Shuiping", "Shuangyu",
        };
        private static readonly float[,] ZodiacPos =
        {
            { 438f, 174f }, { 385f, 193f }, { 364f, 247f }, { 364f, 319f }, { 384f, 359f }, { 438f, 386f },
            { 510f, 386f }, { 549f, 358f }, { 578f, 320f }, { 578f, 247f }, { 551f, 193f }, { 510f, 174f },
        };

        // ---- 技術統計頁(官方 playerTabWindow1,容器偏移 x=1 y=6;下面全是**加過偏移的絕對座標**) ----
        //
        // 官方這一頁底下有兩個子頁,由上方兩顆 CheckBox 切換:
        //   EffortStat(成就)  —— EffortBtn (349,223)
        //   SkillStat(統計明細)—— SkillBtn  (458,227)
        // 🔴 六條的**標籤(勝率/命中率/Perfact率/Cool率/Bad率/Miss率)是烤在 SkillBg_man 背板圖上的**,
        //    不要另外畫字 —— 畫了就會與烤字疊在一起。程式只放「進度條 + 右邊那個百分比數值」。
        private const float StatsEffortBtnX = 349f, StatsEffortBtnY = 223f;
        private const float StatsSkillBtnX = 458f, StatsSkillBtnY = 227f;
        private const float SkillBgX = 351f, SkillBgY = 251f;                  // SkillBg_man 322×190

        // ---- 成就子頁(官方 EffortStat,容器偏移 +1/+6;下面全是加過偏移的絕對座標)----
        // 底圖 EffortBg_man.an = Effort_man.png (0,190,322,190),官方 (350,245) → (351,251)。
        private const float EffortBgX = 351f, EffortBgY = 251f;
        // 🔴 上排「当前装备」6 格與下面 6×2 收藏格**不同起點**:AvtEqipEffort0 在 (375,261)、
        //    AvtEffort0 在 (360,316) —— 官方就是錯開的(上排靠右,因為左邊讓給那條直幅)。step 兩者都是 49。
        private const int EquipSlots = 6, EffortSlots = 12;
        private const float EquipSlotX = 375f, EquipSlotY = 261f;
        private const float EffortSlotX = 360f, EffortSlotY = 316f;
        private const float EffortStep = 49f, EffortSlotSize = 45f;
        /// <summary>問號字形的實際大小(見 <see cref="PlayerInfoArt.EffortNone"/>:只切字、不切那張偏心的 45×45)。</summary>
        private const float QuestionW = 22f, QuestionH = 35f;
        // EffortNownum (491,418) / EffortAllnum (518,418),中間的斜線官方沒烤 → 自己補在兩者之間。
        private const float EffortNumY0X = 491f, EffortNumY1X = 518f, EffortNumY = 418f, EffortSlashX = 508f;
        private static readonly Color32 EffortNumCol = new Color32(0xFF, 0xFC, 0xA5, 0xFF);   // 官方 0xfffffca5
        private const float EffortOkX = 429f, EffortOffX = 541f, EffortBtnY = 413f;           // 装备 / 脱下 各 65×24
        private const float EffortRailX = 654f, EffortRailTop = 325f;                          // 見 BuildEffortSub 的註解
        private const float RateRow0Y = 264f, RateStep = 29f, RateFont = 12f;  // 官方 258/287/316/345/374/403 (+6)
        private const float RateBarX = 434f, RateBarW = 236f, RateBarH = 19f;  // ProgressBar 236×19
        private const float RateValDx = 7f;                                    // 數值文字相對條左緣(官方 440-433)
        // 這一頁上方那三格(熱舞戰績兩格 + 目前排名)。烤字同樣在底板上,只放值。
        private const float PerfX = 427f, PerfAuX = 592f, PerfY = 168f, PerfW = 77f, PerfH = 12f;
        private const float StatsRankX = 428f, StatsRankY = 200f, StatsRankW = 230f;
        private const int RateRowMax = 6;                        // 命中/Perfect/Cool/Bad/Miss/勝率

        private const float NoteX = 351f, NoteY = 215f, NoteW = 318f, NoteH = 120f, NoteFont = 13f;

        // ---------------------------------------------------------------- 顏色
        private static readonly Color Scrim = new Color(0.10f, 0.06f, 0.16f, 0.62f);
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

        private Image _angelBar, _expBar, _weightBar, _duanweiBar;
        private Image[] _fameSlots;                             // 知名度那 10 格(星 / 月 / 太陽)
        private Image _grooveFill;                              // 凹槽右緣的補條(見 GrooveFillX 的註解)
        private Image[] _subTab;                                // 賽事頁的三個子分頁(家族/星座/寵物)
        private RectTransform _xunzhangBody, _famillyBody;      // 星座+寵物共用 / 家族自己
        private int _matchSub;
        private TextMeshProUGUI _basicExp, _basicFamily, _basicOffer, _basicIntimate, _basicSocial, _basicLuck;
        private RateRow[] _rateRows;
        private RectTransform _skillBody, _effortBody;          // 技術統計的兩個子頁(統計明細 / 成就)
        private Button _skillBtn, _effortBtn;
        private TextMeshProUGUI _perfLabel, _perfAuLabel, _statsRankLabel;
        private TextMeshProUGUI _cardsDone, _cardsAll;   // 拼圖「完成數 / 總數」
        private TextMeshProUGUI _effortNow, _effortAll;  // 成就「已收藏 / 總數」
        private TextMeshProUGUI _basicNote, _statsNote;

        private Button _whisperBtn, _friendBtn, _mailBtn, _enemyBtn, _buyLookBtn;
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

            // 擋住背後房間的點擊(不然還看得到房間的鈕、按得下去)。
            // 🔴 **必須完全透明**:之前用 alpha 0.5 的黑幕想讓框跳出來,但官方開個人資料時背後的大廳是
            //    原本的亮度、沒有壓暗。而且黑幕壓在大廳那些高彩度的房卡上,沿著框邊看起來就像框被切壞、
            //    旁邊多了一條黑色雜訊 —— 使用者連續回報三次的「框旁邊的黑色雜訊」就是它。
            //    透明的 Image 一樣吃得到射線,擋點擊的功能完全不受影響。
            //
            // DEV:SDO_PI_BLACK=1 → 把這層變成**不透明黑**。這是查「框旁邊那條雜訊到底是誰畫的」的
            //     鑑別實驗:大廳整個被蓋掉之後,畫面上還剩的每一個非黑像素都一定是這個視窗自己畫的。
            //     雜訊還在 = 視窗多畫了東西;雜訊不見 = 那是從框的縫隙透出來的背景。
            bool devBlack = !string.IsNullOrEmpty(Sdo.Game.ScreenGameplay.DevVar("SDO_PI_BLACK"));
            var dim = UIKit.AddImage(root, "Dim", new Color(0f, 0f, 0f, devBlack ? 1f : 0f), true);
            UIKit.Stretch(dim.rectTransform);

            // 除了黑幕以外都掛在 _window 底下 → 開闔動畫(WindowAnim)只轉框、黑幕不跟著轉。
            _window = UIKit.NewRect(root, "Window");
            UIKit.Stretch(_window);
            _window.pivot = new Vector2(0.5f, 0.5f);
            _windowCg = _window.gameObject.AddComponent<CanvasGroup>();
            _anim = _window.gameObject.AddComponent<WindowAnim>();

            UIKit.AddSprite(_window, "Board", PlayerInfoArt.Board, BoardX, BoardY);

            // 補掉底板凹槽右緣那 12px(官方靠 CharBack 蓋掉,我們沒有那張圖 —— 見 GrooveFillX 的註解)。
            // 建在 Board 之後、分頁容器之前 → 分頁板疊在它上面,寬度不同的那幾頁(43_man 到 684、
            // ZoBG 到 686)各自蓋掉自己那幾 px,剩下的由補條接手,不會有哪一頁露出縫。
            _grooveFill = UIKit.AddImage(_window, "GrooveFill", GrooveFrameCol);
            Place(_grooveFill.rectTransform, GrooveFillX, GrooveFillY, GrooveFillW, GrooveFillH);

            BuildIdentity(_window);
            BuildTabs(_window);
            BuildBasicTab(_tabBody[TabBasic]);
            BuildStatsTab(_tabBody[TabStats]);
            // 賽事信息 / 拼圖卡片:系統都還沒有,但分頁要點得動、內容要看得到欄位(值 0)。
            BuildMatchTab(_tabBody[TabMatch]);
            BuildZodiacTab(_tabBody[TabZodiac]);
            BuildCardsTab(_tabBody[TabCards]);
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

            // 🔴 名字與等級**靠右對齊、同一個顏色**(官方 XML 兩個 Label 都是 0xfffaff74 = NameFace)。
            //    以前等級用 ValueCol(白)、兩者都靠左 —— 與官方對不上(使用者回報)。
            //    靠右是因為這一區左邊被 3D 角色佔著,官方把字貼在右緣才不會壓在人身上。
            _idName = OutlinedLabel.Create(parent, "IdName", IdX + 10f, IdY + 8f, IdW - 20f, 22f,
                                           15f, NameFace, NameEdge, 1f, true, TextAlignmentOptions.Right);
            _idLevel = UIKit.AddText(parent, "IdLevel", "", 13f, NameFace, TextAlignmentOptions.Right);
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

            // 🔴 內容板的底圖是**每頁一張官方圖**,不是我們自己畫的半透明底 ——
            //    官方把每一格的標題(天使等級 / TP值 / 經驗值 / 魅力值 / 幸運值 / 知名度 / 家族 / 城市…)
            //    整組**烤在那張圖上**,程式只負責在對應座標放數值。以前鋪一層 Scrim 再自己排文字列,
            //    所以不管座標怎麼調都不可能像官方(使用者連續三輪回報「tab 裡面的 layout 沒做」)。
            //    兩張圖各自貼在自己那一頁的容器裡(見 BuildBasicTab / BuildStatsTab)。

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
                // 🔴 賽事信息 / 拼圖卡片 / 星座守護三格**按了不切過去**(使用者指定):
                //    這三套系統這個重製版都沒有,版面畫得再像也只是一頁 0 —— 與其讓人切過去看一頁空的,
                //    不如照大廳那些沒實作的鈕的規矩:鈕照擺、按了安靜地什麼都不做(handler 不接)。
                //    分頁圖仍然畫,所以那三格看得到、也會 hover,只是點了不動。
                if (idx == TabBasic || idx == TabStats)
                {
                    btn.onClick.AddListener(() => ShowTab(idx));
                    UiSfx.AttachClick(btn);
                }
            }
        }

        /// <summary>
        /// 基本信息頁 —— 版位**逐字取自官方 <c>playerTabWindow0</c>**(容器偏移 x=-1 y=+6,下面都是加過偏移的絕對座標)。
        ///
        /// 🔴 這一頁的**每一個標題都烤在底圖 <c>PlayerInformationDlg34_man.an</c> 上**,程式只放數值。
        ///    官方欄位:天使等級 / TP值 / 經驗值 / 魅力值 / 幸運值 / 知名度 / 家族 / 家族榮譽度 / 密友度 /
        ///    城市 / 社交值 / MSN / 年齡 / 星座 / MVP。
        ///    這個重製版真正有資料的只有**家族**與**經驗值(恆 0)**;其餘照使用者要求**顯示 0 而不是留白**。
        /// </summary>
        private void BuildBasicTab(RectTransform body)
        {
            UIKit.AddSprite(body, "BasicBg", PlayerInfoArt.AnRaw("PlayerInformationDlg34_man"), BasicBgX, BasicBgY);

            // 三條進度條(天使等級 / 經驗值 / TP值)。經驗值那條官方用黃色前景,另外兩條用同一張粉紅條。
            _angelBar = AddProgress(body, "pro_angel", AngelBarX, AngelBarY, "PlayerInformationDlg65");
            _expBar = AddProgress(body, "pro_exp", ExpBarX, ExpBarY, "PlayerInformationDlgYellow65");
            _weightBar = AddProgress(body, "pro_weight", WeightBarX, WeightBarY, "PlayerInformationDlg285");

            // 魅力值:24 顆愛心,亮(Dlg68)疊在暗(Dlg38)上面 —— 有幾點就亮幾顆。沒有這套系統 → 全暗。
            for (int i = 0; i < CharmCount; i++)
                UIKit.AddSprite(body, "CharmOff" + i, PlayerInfoArt.An("PlayerInformationDlg38"),
                                CharmX + i * CharmStep, CharmY);

            // 幸運值:同魅力值,12 個位置 × 亮(Dlg268)/暗(Dlg238)。沒有這套系統 → 全暗。
            for (int i = 0; i < CharmCount; i++)
                UIKit.AddSprite(body, "LuckyOff" + i, PlayerInfoArt.An("PlayerInformationDlg238"),
                                LuckyX + i * CharmStep, LuckyY);

            // 知名度:10 個空格,圖在 FillFame 時才決定(星 / 月 / 太陽)。
            // 🔴 官方**沒有「空格子」的圖** —— 那 10 個 Label 在 XML 裡連 background 屬性都沒有,
            //    圖是執行期塞的。所以沒填到的格子就是不畫,不要自己補一顆灰星。
            _fameSlots = new Image[FameSlots];
            for (int i = 0; i < FameSlots; i++)
                _fameSlots[i] = UIKit.AddSprite(body, "zhimingdu" + (i + 1), null, FameX + i * FameStep, FameY);

            // 官方那幾格數值。有資料的只有家族;其餘固定 0(使用者要求:沒資料也要顯示 0,不要留白)。
            _basicExp = AddValue(body, "exp", ExpValX, ExpValY, 118f, Color.white, TextAlignmentOptions.Left);
            _basicFamily = AddValue(body, "familyname", FamilyValX, FamilyValY, 72f, Color.black, TextAlignmentOptions.Left);
            _basicOffer = AddValue(body, "offer", OfferValX, OfferValY, 72f, Color.black, TextAlignmentOptions.Left);
            _basicIntimate = AddValue(body, "intimate", IntimateValX, IntimateValY, 72f, Color.black, TextAlignmentOptions.Left);
            _basicSocial = AddValue(body, "SendNum", SocialValX, SocialValY, 80f, Color.black, TextAlignmentOptions.Left);
            _basicLuck = AddValue(body, "luckvalue", LuckValX, LuckValY, 48f, Color.white, TextAlignmentOptions.Left);

            _basicNote = MakeNote(body, "BasicNote");
        }

        /// <summary>底部那一排要顯示哪一組(看自己 = 推广员那組、看別人 = 私聊/好友那組)。</summary>
        private void ShowBottomRow(bool self)
        {
            _whisperBtn.gameObject.SetActive(!self);
            _friendBtn.gameObject.SetActive(!self);
            _mailBtn.gameObject.SetActive(!self);
            _enemyBtn.gameObject.SetActive(!self);
            _buyLookBtn.gameObject.SetActive(!self);

        }

        /// <summary>
        /// 拼圖卡片頁(官方 <c>playerTabWindow3</c> 的 <c>PinTuTab</c>,容器偏移 y=-8/-6)。
        ///
        /// 官方是左側 8 個系列分頁(PinTuTabCheck0..7)+ 右側 6 格拼圖(PinTu N_0..5)+「完成數/總數」。
        /// 這個重製版沒有拼圖收集系統 → 分頁按得動、格子是空的、完成數 0/6。
        /// </summary>
        private void BuildCardsTab(RectTransform body)
        {
            for (int i = 0; i < CardTabCount; i++)
            {
                var b = AddOfficialButton(body, "PinTuTab" + i, PlayerInfoArt.An("PlayerInformationDlg161"),
                    PlayerInfoArt.An("PlayerInformationDlg161"), PlayerInfoArt.An("PlayerInformationDlg161"),
                    CardTabX, CardTabY + i * CardTabStep, null);
                b.transition = Selectable.Transition.None;
            }

            // 六格拼圖。官方每格的寬高不一樣(122/128/96 × 111/131),照官方那組 rect 擺。
            for (int i = 0; i < 6; i++)
            {
                var slot = UIKit.AddImage(body, "PinTu" + i, XunzhangSlotCol);
                Place(slot.rectTransform, CardSlot[i, 0], CardSlot[i, 1], CardSlot[i, 2], CardSlot[i, 3]);
            }

            _cardsDone = AddValue(body, "Complete0", CardDoneX, CardDoneY, 16f, Color.black, TextAlignmentOptions.Left);
            _cardsAll = AddValue(body, "All0", CardAllX, CardDoneY, 16f, Color.black, TextAlignmentOptions.Left);
            _cardsDone.text = "0";
            _cardsAll.text = "6";
        }

        /// <summary>
        /// 賽事信息頁(官方 <c>playerTabWindow2</c>,容器偏移 x=1 y=16)。
        ///
        /// 官方這頁的三塊:段位進度條(pro_duanwei)、一排 12 顆段位星(duanwei1..24 = 12 個位置 × 亮/暗兩張)、
        /// 以及 12 格勳章(AvtXunzhang1..12)。這個重製版沒有段位也沒有勳章 →
        /// 條是空的、星全部暗、勳章格空著,那正是官方「什麼都還沒拿到」的樣子
        /// (使用者要求:沒資料也要把版面畫出來,不要整頁空白)。
        /// </summary>
        private void BuildMatchTab(RectTransform body)
        {
            // 大底板(段位條、星星那排、三個子分頁的框都烤在上面)。
            UIKit.AddSprite(body, "MatchBg", PlayerInfoArt.AnRaw("PlayerInformationDlg54_man"), MatchBoardX, MatchBoardY);

            _duanweiBar = AddProgress(body, "pro_duanwei", DuanweiBarX, DuanweiBarY, "PlayerInformationDlg186_man");

            // 段位星。🔴 官方 duanwei1..24 是**12 個位置 × 兩張**:整顆 Dlg175(18×18) 打底,
            //    左半顆 Dlg175_2(9×18) 疊上去 —— 所以「滿格 24」數的是**半格**,不是 24 顆星。
            //    這個重製版沒有段位系統,但那條欄位不能整條空白(使用者要求沒資料也要看得到版面)→
            //    只畫半顆那張當「空格」,與以前的行為一致;真接上段位時把整顆那張補在同一個位置即可。
            for (int i = 0; i < DuanweiCount; i++)
                UIKit.AddSprite(body, "Duanwei" + (i * 2 + 2), PlayerInfoArt.An("PlayerInformationDlg175_2"),
                                DuanweiX + i * DuanweiStep, DuanweiY);

            // 這頁的幾個數值。全部沒有那套系統 → 一律 0(使用者要求:沒資料也要顯示 0)。
            AddValue(body, "duanweidian", DuanweiDianX, DuanweiDianY, 70f, ValueCol, TextAlignmentOptions.Left).text = "0";
            AddValue(body, "weeklywin", WeeklyWinX, WeeklyWinY, 70f, Color.black, TextAlignmentOptions.Left).text = "0";
            AddValue(body, "bestscore", BestScoreX, BestScoreY, 70f, Color.black, TextAlignmentOptions.Left).text = "0";
            AddValue(body, "emblem", EmblemLabX, EmblemLabY, 70f, Color.black, TextAlignmentOptions.Left).text = "0";

            // 兩個子頁容器:星座與寵物**共用** playerxunzhang(切換只換要畫哪幾格),家族自己一個。
            _xunzhangBody = UIKit.NewRect(body, "playerxunzhang");
            UIKit.Stretch(_xunzhangBody);
            _famillyBody = UIKit.NewRect(body, "playerfamilly");
            UIKit.Stretch(_famillyBody);

            BuildXunzhangSub(_xunzhangBody);
            BuildFamillySub(_famillyBody);

            // 三個子分頁鈕。🔴 **_man 版的圖與 CheckBox 名字是錯開的**(xunzhang0 掛家族的圖、familly 掛寵物的圖)——
            //    女版對得上、男版重繪時把編號對調卻沒改 XML。所以這裡照**畫面上實際的左中右順序**綁圖,
            //    不要照 CheckBox 的名字綁,否則每個分頁會畫到別人的按鈕。
            _subTab = new Image[SubTabCount];
            for (int i = 0; i < SubTabCount; i++)
            {
                int idx = i;
                _subTab[i] = UIKit.AddSprite(body, "SubTab" + i, null, SubTabX, SubTabY);
                var hit = UIKit.AddImage(body, "SubTabHit" + i, new Color(0f, 0f, 0f, 0f), raycast: true);
                Place(hit.rectTransform, SubTabHitX[i], SubTabY + 3f, SubTabHitW[i], 28f);
                var btn = hit.gameObject.AddComponent<Button>();
                btn.targetGraphic = hit; btn.transition = Selectable.Transition.None;
                btn.onClick.AddListener(() => ShowMatchSub(idx));
                UiSfx.AttachClick(btn);
            }
            ShowMatchSub(SubFamily);   // 官方預設停在最左邊那格
        }

        /// <summary>星座 / 寵物共用的那個容器:6×2 格。我們沒有勳章 → 只畫底圖,格子空著。</summary>
        private void BuildXunzhangSub(RectTransform body)
        {
            UIKit.AddSprite(body, "XunzhangBg", PlayerInfoArt.AnRaw("PlayerInformationDlg55_man"), XunzhangBgX, XunzhangBgY);
            for (int i = 0; i < 12; i++)
                UIKit.AddSprite(body, "AvtXunzhang" + (i + 1), null,
                                XunzhangX + (i % 6) * XunzhangStepX, XunzhangY + (i / 6) * XunzhangStepY);
        }

        /// <summary>
        /// 家族徽章那一頁:5×2 格 + 每格右下的數量 + 「0 /50」+ 捲軸。
        /// 🔴 「/」**烤在 playerfamilly2_man 上**(abs 498..502),只放 nownum / allnum 兩個數字,不要補斜線。
        /// </summary>
        private void BuildFamillySub(RectTransform body)
        {
            UIKit.AddSprite(body, "FamillyBg", PlayerInfoArt.AnRaw("playerfamilly2_man"), FamillyBgX, FamillyBgY);
            for (int i = 0; i < 10; i++)
            {
                UIKit.AddSprite(body, "Avtfamilly" + (i + 1), null,
                                FamillyX + (i % 5) * FamillyStepX, FamillyY + (i / 5) * FamillyStepY);
                AddValue(body, "emblemnum" + (i + 1),
                         EmblemNumX + (i % 5) * EmblemNumStepX, EmblemNumY + (i / 5) * EmblemNumStepY,
                         20f, ValueCol, TextAlignmentOptions.Left).text = "0";
            }
            AddValue(body, "nownum", EmblemNowX, EmblemNumRowY, 20f, ValueCol, TextAlignmentOptions.Left).text = "0";
            AddValue(body, "allnum", EmblemAllX, EmblemNumRowY, 20f, ValueCol, TextAlignmentOptions.Left).text = "0";

            // 捲軸握把。軌道是底圖畫死的(abs x 647..651、y 321..416,只有 96 高),
            // 元素那個 (639,297,25,137) 與它對不齊 —— 官方原樣,握把要對軌道不是對元素。
            UIKit.AddSprite(body, "emblem_scroll", PlayerInfoArt.An("PlayerInformationDlg279"), EmblemRailX, EmblemRailTop);
        }

        /// <summary>
        /// 切子分頁。<paramref name="sub"/> = <see cref="SubFamily"/> / <see cref="SubZodiac"/> / <see cref="SubPet"/>。
        /// 星座與寵物共用同一個容器(官方切換時不換容器,只換要畫哪幾格),所以這裡只有兩個容器在開關。
        /// </summary>
        private void ShowMatchSub(int sub)
        {
            _matchSub = sub;
            if (_famillyBody != null) _famillyBody.gameObject.SetActive(sub == SubFamily);
            if (_xunzhangBody != null) _xunzhangBody.gameObject.SetActive(sub != SubFamily);
            for (int i = 0; i < SubTabCount; i++)
                UIKit.ApplySprite(_subTab[i], PlayerInfoArt.SubTab(i, i == sub));
        }

        /// <summary>
        /// 星座守護頁(官方 <c>playerTabWindow4</c>,容器偏移 x=-1 y=+6)。
        /// 12 個星座圍成一圈,每格都是 <c>Zo*Gray_b.an</c> 的灰圖 —— 官方沒擁有的星座就是灰的,
        /// 這個重製版沒有星座系統 → **全部都是灰的**,那正好就是「一個都還沒點亮」的官方樣子。
        /// </summary>
        private void BuildZodiacTab(RectTransform body)
        {
            for (int i = 0; i < ZodiacNames.Length; i++)
                UIKit.AddSprite(body, "Zodiac" + i, PlayerInfoArt.An("Zo" + ZodiacNames[i] + "Gray_b"),
                                ZodiacPos[i, 0], ZodiacPos[i, 1]);
        }

        /// <summary>官方 ProgressBar:236×19 的前景圖,用 Filled 由左往右填。</summary>
        private static Image AddProgress(RectTransform parent, string name, float x, float y, string an)
        {
            var img = UIKit.AddSprite(parent, name, PlayerInfoArt.An(an), x, y);
            Place(img.rectTransform, x, y, ProgressW, ProgressH);   // AddSprite 會縮成原圖大小,擺完再改回來
            img.type = Image.Type.Filled;
            img.fillMethod = Image.FillMethod.Horizontal;
            img.fillOrigin = (int)Image.OriginHorizontal.Left;
            img.fillAmount = 0f;
            return img;
        }

        private static TextMeshProUGUI AddValue(RectTransform parent, string name, float x, float y, float w,
                                                Color color, TextAlignmentOptions align)
        {
            var t = UIKit.AddText(parent, name, "", RowFont, color, align);
            Place(t.rectTransform, x, y, w, 14f);
            return t;
        }

        /// <summary>
        /// 技術統計頁 —— 版位**逐字取自官方 <c>playerTabWindow1</c>**(見上方常數區)。
        /// 官方這頁分成「成就」與「統計明細」兩個子頁,由上面兩顆鈕切換;統計明細就是那六條進度條。
        /// </summary>
        private void BuildStatsTab(RectTransform body)
        {
            UIKit.AddSprite(body, "StatsBg", PlayerInfoArt.AnRaw("PlayerInformationDlg43_man"), BasicBgX, BasicBgY - 1f);

            // 上方三格:熱舞戰績(兩格)與目前排名。烤字在底板上,這裡只放值。
            _perfLabel = UIKit.AddText(body, "performance", "", RateFont, Color.black, TextAlignmentOptions.Center);
            Place(_perfLabel.rectTransform, PerfX, PerfY, PerfW, PerfH);
            _perfAuLabel = UIKit.AddText(body, "performanceau", "", RateFont, Color.black, TextAlignmentOptions.Center);
            Place(_perfAuLabel.rectTransform, PerfAuX, PerfY, PerfW, PerfH);
            _statsRankLabel = UIKit.AddText(body, "rank", "", RateFont,
                                            new Color32(0x00, 0x4F, 0x7C, 0xFF), TextAlignmentOptions.Left);
            Place(_statsRankLabel.rectTransform, StatsRankX, StatsRankY, StatsRankW, PerfH);

            // 兩個子頁的容器。
            _effortBody = UIKit.NewRect(body, "EffortStat");
            UIKit.Stretch(_effortBody);
            _skillBody = UIKit.NewRect(body, "SkillStat");
            UIKit.Stretch(_skillBody);

            BuildEffortSub(_effortBody);

            // 統計明細:背板(六個標籤烤在上面)+ 六條進度條。
            UIKit.AddSprite(_skillBody, "SkillBg", PlayerInfoArt.AnRaw("SkillBg_man"), SkillBgX, SkillBgY);
            _rateRows = new RateRow[RateRowMax];
            for (int i = 0; i < RateRowMax; i++)
                _rateRows[i] = RateRow.Create(_skillBody, "RateRow" + i, RateBarX, RateRow0Y + i * RateStep);

            // 兩顆子頁鈕。官方是 CheckBox(normal/pushed 兩態),選中換 pushed 圖 —— 與分頁條同一個做法,
            // 所以 transition 關掉、圖由 ShowStatsSub 手動控制(留著 SpriteSwap 會出現「按一個動兩個」)。
            _effortBtn = AddOfficialButton(body, "EffortBtn", PlayerInfoArt.An("EffortBtn1_man"),
                PlayerInfoArt.An("EffortBtn1_man"), PlayerInfoArt.An("EffortBtn2_man"),
                StatsEffortBtnX, StatsEffortBtnY, () => ShowStatsSub(false));
            _skillBtn = AddOfficialButton(body, "SkillBtn", PlayerInfoArt.An("SkillBtn1_man"),
                PlayerInfoArt.An("SkillBtn1_man"), PlayerInfoArt.An("SkillBtn2_man"),
                StatsSkillBtnX, StatsSkillBtnY, () => ShowStatsSub(true));
            _effortBtn.transition = Selectable.Transition.None;
            _skillBtn.transition = Selectable.Transition.None;

            _statsNote = MakeNote(body, "StatsNote");
            ShowStatsSub(true);   // 預設停在「統計明細」——那才是有資料可看的那一頁
        }

        /// <summary>
        /// 「成就」子頁 —— 版位逐字取自官方 <c>playerTabWindow1 &gt; EffortStat</c>(容器偏移 +1/+6,下面都是絕對座標)。
        ///
        /// 官方的結構是三層疊在一起、位置互相錯開:
        ///   ・<c>AvtShow AvtEffortN</c>(45×45)= 真正放徽章圖的槽
        ///   ・<c>Label AvtEffortTipN</c>(同位置)= 只負責 tooltip,沒有背景
        ///   ・<c>CheckBox BtnEffortN</c>(比槽左上各外擴 2px)= 點選,normal/hover 是 <c>empty.an</c>(全透明),
        ///     只有 pushed 才畫 <c>EffortCheck_man</c> 那個實心高亮框
        /// 這個重製版沒有成就系統 → 只畫槽與問號,選中框與 tooltip 都不做。
        ///
        /// 🔴 「当前装备」那條直幅**烤在 EffortBg_man 上**(abs 354,260),與六條率的標籤同一個模式 ——
        ///    再畫一次會疊字。
        /// 🔴 空格的問號圖是 <c>EffortNone_man.an</c>,而**官方 XML 完全沒有引用它** —— 是程式端拿來填空槽的。
        ///    它的 45×45 框裡「?」字形落在 rel (18,1,22,35)、偏右偏上;所以這裡切**字形本身**
        ///    (Effort_man.png 340,121,22,35)再置中貼進槽,而不是直接貼那張偏心的 45×45。
        /// </summary>
        private void BuildEffortSub(RectTransform body)
        {
            UIKit.AddSprite(body, "EffortBg", PlayerInfoArt.AnRaw("EffortBg_man"), EffortBgX, EffortBgY);

            // 上排 6 格「当前装备」。沒有裝備任何成就 → 槽是空的(官方空槽也不畫問號,問號只在下面的收藏格)。
            for (int i = 0; i < EquipSlots; i++)
                UIKit.AddSprite(body, "AvtEqipEffort" + i, null, EquipSlotX + i * EffortStep, EquipSlotY);

            // 下面 6×2 = 12 格收藏。一個成就都沒有 → 全部畫問號。
            var q = PlayerInfoArt.EffortNone;
            for (int i = 0; i < EffortSlots; i++)
            {
                float x = EffortSlotX + (i % 6) * EffortStep;
                float y = EffortSlotY + (i / 6) * EffortStep;
                var slot = UIKit.AddSprite(body, "AvtEffort" + i, q, 0f, 0f);
                // 問號字形只有 22×35,要**置中**在 45×45 的槽裡 —— 直接貼在槽的左上角會整排偏左上。
                Place(slot.rectTransform, x + (EffortSlotSize - QuestionW) * 0.5f,
                                          y + (EffortSlotSize - QuestionH) * 0.5f, QuestionW, QuestionH);
            }

            // 「已收藏 / 總數」。🔴 官方**沒有**在底圖上烤那個斜線(那條帶子是純色),所以斜線要自己補一個 Label。
            _effortNow = AddValue(body, "EffortNownum", EffortNumY0X, EffortNumY, 20f, EffortNumCol, TextAlignmentOptions.Left);
            var slash = UIKit.AddText(body, "EffortSlash", "/", RowFont, EffortNumCol, TextAlignmentOptions.Center);
            Place(slash.rectTransform, EffortSlashX, EffortNumY, 12f, 14f);
            _effortAll = AddValue(body, "EffortAllnum", EffortNumY1X, EffortNumY, 20f, EffortNumCol, TextAlignmentOptions.Left);
            _effortNow.text = "0";
            _effortAll.text = "0";

            // 裝備 / 脫下。沒有成就系統 → 鈕照擺、**按了安靜地什麼都不做**(handler 傳 null)。
            // 🔴 官方 pushed 圖(EffortEquip3_man / EffortUnistall3_man)與 normal 是**同一個 crop**,
            //    所以按下去本來就不會變樣;真正有變化的只有 hover 與 disabled。
            AddOfficialButton(body, "EffortOk", PlayerInfoArt.An("EffortEquip1_man"),
                PlayerInfoArt.An("EffortEquip2_man"), PlayerInfoArt.An("EffortEquip1_man"),
                EffortOkX, EffortBtnY, null);
            AddOfficialButton(body, "EffortUninstall", PlayerInfoArt.An("EffortUnistall1_man"),
                PlayerInfoArt.An("EffortUnistall2_man"), PlayerInfoArt.An("EffortUnistall1_man"),
                EffortOffX, EffortBtnY, null);

            // 捲軸握把。🔴 XML 的 Effort_scroll 是 (652,302,25,125),但**底圖上畫死的軌道只有 abs x 661..664、
            //    y 325..409** —— 照 652 的左緣擺,握把會歪在軌道左邊。16 寬的握把要對齊軌道中心(662.5)→ 654。
            //    收藏格只有 12 個、我們又全是問號,實際捲不動;握把照官方永遠顯示,停在最上面。
            UIKit.AddSprite(body, "Effort_scroll", PlayerInfoArt.An("EffortScrollBar_man"), EffortRailX, EffortRailTop);
        }

        /// <summary>技術統計頁的兩個子頁:true = 統計明細(六條)、false = 成就。</summary>
        private void ShowStatsSub(bool skill)
        {
            if (_skillBody == null) return;
            _skillBody.gameObject.SetActive(skill);
            _effortBody.gameObject.SetActive(!skill);
            UIKit.ApplySprite(_skillBtn.targetGraphic as Image,
                              PlayerInfoArt.An(skill ? "SkillBtn2_man" : "SkillBtn1_man"));
            UIKit.ApplySprite(_effortBtn.targetGraphic as Image,
                              PlayerInfoArt.An(skill ? "EffortBtn1_man" : "EffortBtn2_man"));
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

            _mailBtn = AddOfficialButton(parent, "Mail", PlayerInfoArt.MailN, PlayerInfoArt.MailH, PlayerInfoArt.MailP, MailX, BtnY, null);
            _enemyBtn = AddOfficialButton(parent, "Enemy", PlayerInfoArt.EnemyN, PlayerInfoArt.EnemyH, PlayerInfoArt.EnemyP, EnemyX, DelFriendY, null);
            _buyLookBtn = AddOfficialButton(parent, "BuyLook", PlayerInfoArt.BuyLookN, PlayerInfoArt.BuyLookH, PlayerInfoArt.BuyLookP, BuyLookX, BtnY, null);

            // 🔴 官方底部那五個格子有**兩種模式**,不是一組鈕:
            //      看**別人** → 私聊(108) / 加好友(208) / 寄信(308) / 黑名單(408) / 買對方裝扮(508)
            //      看**自己** → 我要做推广员(108) / 接受推广(208) / … / 点数兑换(508)
            //    兩組疊在同樣的 x,靠「這是誰的資料」切換顯示(見 Open / OpenSelf)。
            //    以前不分模式一律畫「看別人」那組,所以開自己的資料會看到「刪除好友 / 購買搭配」——
            //    對著自己按毫無意義(使用者回報)。這兩顆同樣沒有後端 → handler null。
            // 🔴 官方看自己時那一組是「我要做推广员 / 接受推广 / 点数兑换」——**使用者要求整組拿掉**:
            //    這個重製版沒有推廣員制度也沒有點數兌換,那兩顆放上去只是兩塊按不動的裝飾。
            //    看自己時底部就只留「確定」。(座標記在這裡,之後真要做:Spreader 108,507 / PointChange 508,507。)

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

            ShowBottomRow(self: false);
            _whisperBtn.gameObject.SetActive(onWhisper != null);
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
            ShowBottomRow(self: true);

            // DEV: SDO_PLAYERINFO=<分頁編號> → 開機直接停在那一頁(0 基本 / 1 技術統計 / 2 賽事 / 3 拼圖 / 4 星座)。
            //      那幾頁只有點得到分頁條才看得到,而截圖工具點不了 —— 版位對不對只有實機截圖看得出來。
            int devTab;
            ShowTab(int.TryParse(Sdo.Game.ScreenGameplay.DevVar("SDO_PLAYERINFO"), out devTab)
                    && devTab >= 0 && devTab < TabCount ? devTab : TabBasic);
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
            if (_idLevel != null) _idLevel.text = FormatLevel(levelLabel);
        }

        /// <summary>
        /// 官方這一格寫的是「<c>Level:62</c>」。傳進來的 <paramref name="label"/> 是
        /// <c>RoomConfig.LevelLabel</c> 的「LV:11」格式(那是房間頭上名牌用的、要短),
        /// 這裡只把數字取出來重排 —— 兩個地方要的字面不一樣,但**等級的來源只有一個**,不另外開一條取值路徑。
        /// 整串沒有數字(例如角色刻意留白)就原樣顯示,不要憑空生一個「Level:0」。
        /// </summary>
        private static string FormatLevel(string label)
        {
            if (string.IsNullOrEmpty(label)) return "";
            var digits = new string(System.Array.FindAll(label.ToCharArray(), char.IsDigit));
            return digits.Length > 0 ? "Level:" + digits : label;
        }

        /// <summary>
        /// 基本信息頁的值(看自己)。欄位標題全部烤在底圖上 → 這裡只填數字。
        /// 這個重製版真正有的只有**家族**;天使等級 / TP / 經驗 / 魅力 / 幸運 / 榮譽 / 密友 / 社交
        /// 都沒有那套系統 → 一律 0(使用者要求:沒資料也要顯示 0,不要留白)。
        /// </summary>
        private void FillBasicSelf(UserProfile p)
        {
            _basicNote.gameObject.SetActive(false);
            _basicFamily.text = Or(ProfileFields.FamilyName(p));
            _basicExp.text = "0%";
            _basicOffer.text = "0";
            _basicIntimate.text = "0";
            _basicSocial.text = "0";
            _basicLuck.text = "0";
            _angelBar.fillAmount = 0f;
            _expBar.fillAmount = 0f;
            _weightBar.fillAmount = 0f;
            FillFame(p != null ? p.fame : 0);
        }

        /// <summary>
        /// 知名度那一排:先畫星星,五顆換一個月亮,五個月亮換一個太陽。
        ///
        /// 🔴 **「幾顆換一個」這條規則不在官方資料包裡** —— 全樹掃過 .xml/.txt/.ini/.dat/.lua/.cfg 找 zhimingdu,
        ///    再對 3328 個檔做三種編碼的「知名度」位元組掃描,零命中(那三個字只存在於烤進 PNG 的像素裡);
        ///    連 XML 掛的 tip_xml(SpeakerTip.xml / ShopItemTip.xml)在資料包裡都不存在。
        ///    換算寫在 client exe 或 server,查不到 → **五進位是我們定的**,不是抄來的,不要當成官方值。
        ///    唯一的硬事實是「一排 10 格」,而五進位配 <see cref="FameLevel.MaxLevel"/>=15 最多只會畫到
        ///    2 個月亮 + 4 顆星 = 6 格,永遠塞得下。
        ///
        /// 等級來源是 <see cref="FameLevel"/> —— 與大廳右下角那行「LV n (m)」同一份門檻表,
        /// 兩個地方不該對同一個累計值算出不同等級。
        /// </summary>
        private void FillFame(int fame)
        {
            if (_fameSlots == null) return;

            int lv = FameLevel.LevelFor(fame);
            int suns = lv / 25;                 // 5 月 = 25 星
            int moons = (lv % 25) / 5;
            int stars = lv % 5;

            int n = 0;
            for (int i = 0; i < suns && n < _fameSlots.Length; i++) UIKit.ApplySprite(_fameSlots[n++], PlayerInfoArt.FameSun);
            for (int i = 0; i < moons && n < _fameSlots.Length; i++) UIKit.ApplySprite(_fameSlots[n++], PlayerInfoArt.FameMoon);
            for (int i = 0; i < stars && n < _fameSlots.Length; i++) UIKit.ApplySprite(_fameSlots[n++], PlayerInfoArt.FameStar);
            for (; n < _fameSlots.Length; n++) UIKit.ApplySprite(_fameSlots[n], null);   // 剩下的格子不畫(官方沒有空格圖)
        }

        /// <summary>
        /// 看別人。座位快照只帶得到名字 / 等級 / 家族 —— 其餘欄位與看自己一樣是 0。
        /// 🔴 沒有「性別」:SeatInfo 沒帶性別,呼叫端傳進來的那個值查不到時會退回**本機**的性別
        ///    (見 Open 的 doc),當成資料顯示會把一整批人標成跟自己同一個性別。
        /// </summary>
        private void FillBasicOther(PlayerProfile who, string levelLabel)
        {
            _basicNote.gameObject.SetActive(false);
            _basicFamily.text = Or(who.Guild);
            _basicExp.text = "0%";
            _basicOffer.text = "0";
            _basicIntimate.text = "0";
            _basicSocial.text = "0";
            _basicLuck.text = "0";
            _angelBar.fillAmount = 0f;
            _expBar.fillAmount = 0f;
            _weightBar.fillAmount = 0f;
            // 座位快照帶不到別人的知名度 → 當 0,也就是 LV 1 = 一顆星。
            // 與「沒資料也顯示 0 而不是留白」同一個原則:大廳那行同樣的人會顯示 LV 1 (0)。
            FillFame(0);
        }

        private void FillStatsSelf(PlayStats s)
        {
            // 🔴 一顆音符都還沒判過時**照樣把每一列畫出來、值是 0**(使用者要求),不再改成一句
            //    「還沒有紀錄」把整頁清空。官方就是這樣:欄位永遠在,沒資料就是 0 ——
            //    空白的一頁看起來像功能沒做完,而一排 0 至少看得出「這裡會有什麼」。
            //    (PlayStats 的衍生比率在 Judged==0 時本來就回 0,所以直接往下走即可。)
            if (s == null) s = new PlayStats();

            _statsNote.gameObject.SetActive(false);
            // 順序照官方的「統計明細」那一頁:勝率 → 命中率 → Perfect → Cool → Bad → Miss。
            // (勝率排第一 —— 那是玩家最常看的那個數字,官方把它放最上面。)
            int r = 0;
            _rateRows[r++].Set(null, s.WinRate);
            _rateRows[r++].Set(null, s.Accuracy);
            _rateRows[r++].Set(null, s.PerfectRate);
            _rateRows[r++].Set(null, s.CoolRate);
            _rateRows[r++].Set(null, s.BadRate);
            _rateRows[r++].Set(null, s.MissRate);
            HideFrom(_rateRows, r);

            // 上方那三格(官方 performance / performanceau / rank)。官方兩格「熱舞戰績」是兩種模式各一份,
            // 這個重製版只累計一份 → 第一格放真的勝負,第二格放 0 勝 0 負(官方那格在沒打過時也是 0)。
            // 目前排名沒有排名系統 → 留空(官方那格沒資料時也是空的,不要編一個假名次)。
            _perfLabel.text = L("room.info_record_value", Num(s.wins), Num(s.losses));
            _perfAuLabel.text = L("room.info_record_value", "0", "0");
            _statsRankLabel.text = "";
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
            // 六條照樣畫出來(值 0)—— 使用者要求「沒資料也要顯示 0,不要整頁空白」。
            var empty = new PlayStats();
            int r = 0;
            _rateRows[r++].Set(null, empty.WinRate);
            _rateRows[r++].Set(null, empty.Accuracy);
            _rateRows[r++].Set(null, empty.PerfectRate);
            _rateRows[r++].Set(null, empty.CoolRate);
            _rateRows[r++].Set(null, empty.BadRate);
            _rateRows[r++].Set(null, empty.MissRate);
            _perfLabel.text = L("room.info_record_value", "0", "0");
            _perfAuLabel.text = L("room.info_record_value", "0", "0");
            _statsRankLabel.text = "";
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

            // 🔴 凹槽右緣的補條**只在底板是淺紫的那幾頁**顯示。
            //    它是一條實心的框線色(97,72,168),貼在基本/技術統計頁那種淺紫板子旁邊看起來就是「內框粗一點」;
            //    但星座守護頁的底板是整片深色星空,同一條就變成畫面上一條突兀的紫邊(使用者回報)。
            //    那幾頁的底板本來就是深色,凹槽露出來的星空與它融成一片,根本不需要補。
            if (_grooveFill != null) _grooveFill.enabled = _tab == TabBasic || _tab == TabStats;
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

            /// <summary>
            /// 官方 SkillStat 的一條:ProgressBar 236×19(前景 PlayerInformationDlg65.an)+ 疊在上面的百分比數值。
            /// 🔴 **不畫標籤** —— 「勝率/命中率/…」那幾個字是烤在 SkillBg_man 背板圖上的,再畫一次會疊字。
            /// </summary>
            public static RateRow Create(RectTransform parent, string name, float x, float y)
            {
                var r = new RateRow();
                r.Root = Place(UIKit.NewRect(parent, name), x, y, RateBarW, RateBarH);

                r.Fill = UIKit.AddSprite(r.Root, "BarFill", PlayerInfoArt.RateBar, 0f, 0f);
                Place(r.Fill.rectTransform, 0f, 0f, RateBarW, RateBarH);   // AddSprite 會縮成原圖大小,擺完再改回來
                r.Fill.type = Image.Type.Filled;
                r.Fill.fillMethod = Image.FillMethod.Horizontal;
                r.Fill.fillOrigin = (int)Image.OriginHorizontal.Left;

                // 官方那個數值是**白字、靠左、疊在條上**(x 比條多 7px),不是擺在右邊。
                r.Value = UIKit.AddText(r.Root, "V", "", RateFont, ValueCol, TextAlignmentOptions.Left);
                Place(r.Value.rectTransform, RateValDx, 0f, RateBarW - RateValDx, RateBarH);

                r.Root.gameObject.SetActive(false);
                return r;
            }

            public void Set(string label, double pct)
            {
                Value.text = Pct(pct);
                Fill.fillAmount = Mathf.Clamp01((float)pct / 100f);
                Root.gameObject.SetActive(true);
            }

            public void Hide() { Root.gameObject.SetActive(false); }
        }
    }
}
