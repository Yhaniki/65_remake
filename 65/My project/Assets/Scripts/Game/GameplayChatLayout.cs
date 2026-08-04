namespace Sdo.Game
{
    /// <summary>
    /// 遊戲畫面右下角聊天框的版位 —— 純幾何(沒有任何 Unity 型別,可單元測試)。
    ///
    /// 座標全部是官方 <c>UI/GAMEPLAY/PLAYNEWLEAN/GAMEPLAYNEWLEAN.XML</c> 的 <c>winchat</c> 那組(800×600 design、
    /// 左上原點):底板 <c>GamePlay3.an</c> 255×38 貼右緣(x 545..800)、<c>chatmode</c>(当前/家族/好友)在 x 545、
    /// <c>ChatEdit</c> 文字從 x 606、<c>ChatSendButton</c> x 726、<c>expression1</c> x 760;訊息列 <c>ChatList</c>
    /// x 550 欄寬 224、行高 14。
    ///
    /// 唯一偏離官方的是 <see cref="TopAnchored"/>:玩家把 NOTES 面板調成「屏幕中央 + 向下」時,受擊線與整塊板子
    /// 壓在畫面下半,右下角那條聊天列會跟判定字/受擊線擠在一起 —— 這種組合就把整組(底板＋按鈕＋訊息列)搬到
    /// 畫面右上角(使用者指定)。其餘三種組合維持官方的右下角。
    /// </summary>
    public readonly struct GameplayChatLayout
    {
        /// <summary>底板 GamePlay3.an 的原圖尺寸(255×38);它就是整條聊天列的寬與高。</summary>
        public const float BarW = 255f;
        public const float BarH = 38f;

        // ---- 條上四個元件的 XML 絕對座標(GAMEPLAYNEWLEAN.XML winchat) ----
        public const float ModeBtnX = 545f, ModeBtnY = 565f;    // chatmode 51×30
        public const float EditX = 606f, EditY = 572f;          // ChatEdit w=100 h=14
        public const float SendBtnX = 726f, BtnRowY = 564f;     // ChatSendButton 33×32
        public const float ExprBtnX = 760f;                     // expression1 33×32
        /// <summary>兩顆圓鈕的原圖尺寸(GamePlay5 / Room69 都是 33×32)。</summary>
        public const float BtnW = 33f, BtnH = 32f;

        /// <summary>
        /// 底板左緣 = <b>537</b>,直接來自官方 XML —— 底板**不在** <c>winchat</c> 裡,它自己一個 Window:
        /// <code>
        /// &lt;Window name="winchat2" x="487" y="569"&gt;
        ///   &lt;Label x="50"  y="-8" background="GamePlay3.an"/&gt;   → 絕對 (537, 561)
        ///   &lt;Label x="305" y="-8" background="GamePlay4.an"/&gt;   → 絕對 (792, 561)
        /// &lt;/Window&gt;
        /// </code>
        /// (先前用「照按鈕置中」推出來的 541.5 / 541 都是猜的,差 4px。)
        /// </summary>
        public const float BarX = 537f;

        /// <summary>底板的**右端收尾**(GamePlay4.an,6×38)。官方底板是兩片拼的:GamePlay3 畫 537..792,
        /// 這一小片接在 792..798。少畫它右端就會缺一角。</summary>
        public const float BarTailX = 792f, BarTailW = 6f;

        // 條上各元件相對「底板左上角」的位移(= XML 絕對座標 − BarX / − 底板 y)。
        public const float ModeBtnDx = ModeBtnX - BarX;         // 3.5
        public const float EditDx = EditX - BarX;               // 64.5
        public const float SendBtnDx = SendBtnX - BarX;         // 184.5
        public const float ExprBtnDx = ExprBtnX - BarX;         // 218.5
        public const float EditW = 115f;                        // 文字可用寬:到送出鈕左緣前留一點空
        public const float ModeBtnDy = ModeBtnY - BottomBarY;   // 4
        public const float EditDy = EditY - BottomBarY;         // 11
        public const float SendBtnDy = BtnRowY - BottomBarY;    // 3
        public const float ExprBtnDy = SendBtnDy;

        /// <summary>右下版位時底板的頂緣 = <b>561</b>,同樣來自 XML 的 <c>winchat2</c>(569 − 8)。</summary>
        public const float BottomBarY = 561f;

        /// <summary>訊息列單欄寬(XML Column width=224)。</summary>
        public const float ListW = 224f;
        /// <summary>訊息列外框左緣(XML TextList x=550)與外框寬(w=301)。</summary>
        public const float ListFrameX = 550f, ListFrameW = 301f;
        /// <summary>
        /// 訊息欄左緣 —— **切齊 chatmode 鈕的左緣(545)**。
        ///
        /// 🔴 不要拿 XML 的 <c>TextList x=550</c> 當字的左緣:那是**外框**,不是字的起點。這個值是實機比對
        /// 出來的 —— 550 偏左、把欄置中(588.5)整片偏右、照官方截圖量的 535 又太左,最後定在與按鈕列切齊。
        /// </summary>
        public const float ListX = ModeBtnX;   // 545
        /// <summary>訊息行高(XML TextList height=14)。</summary>
        public const float LineH = 14f;
        /// <summary>訊息列與底板之間的空隙。</summary>
        public const float ListGap = 4f;
        /// <summary>最多同時顯示幾行 —— 官方 TextList h=196 ÷ 行高 14 = 14 行。</summary>
        public const int MaxLines = 14;

        /// <summary>底板左上角的 design y。右下 = 562(貼畫面底),右上 = 0(貼畫面頂)。</summary>
        public readonly float BarY;
        /// <summary><c>true</c> = 整組搬到畫面右上(置中＋向下);<c>false</c> = 官方的右下角。</summary>
        public readonly bool TopAnchored;

        /// <summary>chatmode 彈出選單的框(官方 <c>ROOMPOPMENU.XML</c> 的 <c>chatmodemenu</c>,41×104)。
        /// 按鈕比框寬(49/51),官方就是讓它溢出的。</summary>
        public const float ModeMenuW = 41f, ModeMenuH = 104f;
        /// <summary>選單裡四顆的槽位 y(官方值:家族 2 / 好友 27 / 當前 52 / 回復 77,相對選單框左上角)。
        /// x 一律是 2,與條上那顆 chatmode 鈕的左緣對齊。</summary>
        public static readonly float[] ModeMenuSlotY = { 2f, 27f, 52f, 77f };
        public const float ModeMenuSlotX = 2f;

        /// <summary>
        /// 每張 chatmode 圖在自己 sprite 裡的**視覺上緣**(量測值,索引同 ChatChannel 0..3)。
        ///
        /// 🔴 「當前」那張(Room4,51×30)上下各留了約 2px 全透明,其餘三張(SmallButton 的 49×25)是滿版。
        /// 選單若照 sprite 矩形每 25px 排,「好友↔當前」就會多出 2px 空隙、「當前↔回復」又互相疊 2px ——
        /// 使用者回報的「那些按鈕沒有並排、中間有很大空隙」就是這個。擺放時把這個偏移扣掉,四顆才真的貼齊。
        /// </summary>
        public static readonly float[] ModeArtTopPad = { 0f, 0f, 2f, 0f };   // 家族 / 好友 / 當前 / 回復
        /// <summary>表情面板(ROOMPOPMENU 的 EXPRESSIONINFO)整塊的尺寸 —— 與房間同一組素材、同一份版面。</summary>
        public const float ExprPanelW = 165f, ExprPanelH = 152f;

        public GameplayChatLayout(bool topAnchored)
        {
            TopAnchored = topAnchored;
            BarY = topAnchored ? 0f : BottomBarY;   // 0 或 561
        }

        /// <summary>依這一局實際生效的 NOTES 面板設定決定聊天框擺哪。</summary>
        /// <param name="panelLeft">面板位置:<c>true</c>=屏幕左邊 / <c>false</c>=屏幕中央(已套 ShowTime 強制靠左)。</param>
        /// <param name="bottomDrop">受擊線在底部嗎(＝<c>NotePanelLayout.Bottom</c>;目前只有「向下」是)。</param>
        public static GameplayChatLayout Resolve(bool panelLeft, bool bottomDrop)
            => new GameplayChatLayout(!panelLeft && bottomDrop);

        /// <summary>條上某個元件的絕對 design 座標(x = BarX + dx, y = BarY + dy)。</summary>
        public float BarItemX(float dx) => BarX + dx;
        public float BarItemY(float dy) => BarY + dy;

        /// <summary>訊息區靠底板那一側的邊界(右下:訊息在底板**上方**;右上:訊息在底板**下方**)。</summary>
        public float ListNearY => TopAnchored ? BarY + BarH + ListGap : BarY - ListGap;

        /// <summary>
        /// 第 <paramref name="indexFromNewest"/> 新的那一行(0 = 最新)的**行頂** design y。
        ///
        /// 兩種版位都遵守「新的在下面」的聊天慣例,差別只在往哪邊填:
        ///   • 右下(底板在下):訊息由下往上長,最新一行貼著底板上緣 —— 訊息少的時候就只有底板旁邊那幾行。
        ///   • 右上(底板在上):訊息由上往下長,最舊的貼著底板下緣、最新的在最下面 —— 所以要知道 <paramref name="count"/>。
        /// </summary>
        public float LineTopY(int indexFromNewest, int count)
            => TopAnchored
                ? ListNearY + (count - 1 - indexFromNewest) * LineH
                : ListNearY - (indexFromNewest + 1) * LineH;

        /// <summary>彈出面板(chatmode 選單 / 表情面板)左上角的 y:永遠開在「訊息那一側」,而且**貼齊底板**
        /// —— 房間的表情選單就是底邊壓在紫條上緣(Place 411+152 = 563 = win3 紫條頂),中間留空隙會看起來像
        /// 浮在半空、跟按鈕對不上。</summary>
        public float PopupTopY(float height)
            => TopAnchored ? BarY + BarH : BarY - height;

        /// <summary>表情面板左緣:**右緣對齊表情鈕的右緣**(expression1 x=760 + 33),這樣面板與聊天列右端切齊。</summary>
        public float ExprPanelX => BarItemX(ExprBtnDx) + BtnW - ExprPanelW;
    }
}
