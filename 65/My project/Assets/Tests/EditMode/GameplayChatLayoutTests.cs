using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>遊戲中聊天框的版位:預設右下(官方 winchat 座標),「屏幕中央 + 向下」才搬到右上。</summary>
    public class GameplayChatLayoutTests
    {
        [Test]
        public void Default_Is_Bottom_Right_At_The_Official_Coordinates()
        {
            var l = GameplayChatLayout.Resolve(panelLeft: true, bottomDrop: false);
            Assert.IsFalse(l.TopAnchored);
            // 底板座標直接來自 XML 的 winchat2(487+50, 569−8);它是兩片拼的,右端收尾在 487+305
            Assert.AreEqual(537f, GameplayChatLayout.BarX, 0.01f);
            Assert.AreEqual(561f, l.BarY, 0.01f);
            Assert.AreEqual(792f, GameplayChatLayout.BarTailX, 0.01f);
            Assert.AreEqual(798f, GameplayChatLayout.BarTailX + GameplayChatLayout.BarTailW, 0.01f);
            // 條上元件回到 XML 的絕對座標
            Assert.AreEqual(545f, l.BarItemX(GameplayChatLayout.ModeBtnDx), 0.01f);   // chatmode x=545
            Assert.AreEqual(565f, l.BarItemY(GameplayChatLayout.ModeBtnDy), 0.01f);   // chatmode y=565
            Assert.AreEqual(606f, l.BarItemX(GameplayChatLayout.EditDx), 0.01f);      // ChatEdit x=606
            Assert.AreEqual(726f, l.BarItemX(GameplayChatLayout.SendBtnDx), 0.01f);   // ChatSendButton x=726
            Assert.AreEqual(760f, l.BarItemX(GameplayChatLayout.ExprBtnDx), 0.01f);   // expression1 x=760
        }

        [Test]
        public void Only_Centre_Plus_Down_Moves_To_The_Top_Right()
        {
            Assert.IsFalse(GameplayChatLayout.Resolve(panelLeft: true, bottomDrop: false).TopAnchored);   // 左邊 向上
            Assert.IsFalse(GameplayChatLayout.Resolve(panelLeft: true, bottomDrop: true).TopAnchored);    // 左邊 向下
            Assert.IsFalse(GameplayChatLayout.Resolve(panelLeft: false, bottomDrop: false).TopAnchored);  // 置中 向上
            Assert.IsTrue(GameplayChatLayout.Resolve(panelLeft: false, bottomDrop: true).TopAnchored);    // 置中 向下 ← 只有這個
        }

        [Test]
        public void Top_Anchored_Bar_Sits_Flush_With_The_Top_Edge()
        {
            var l = GameplayChatLayout.Resolve(panelLeft: false, bottomDrop: true);
            Assert.AreEqual(0f, l.BarY, 0.01f);
            Assert.AreEqual(GameplayChatLayout.ModeBtnDy, l.BarItemY(GameplayChatLayout.ModeBtnDy), 0.01f);
        }

        [Test]
        public void Bottom_Anchored_Lines_Stack_Upward_From_The_Bar()
        {
            var l = GameplayChatLayout.Resolve(panelLeft: true, bottomDrop: false);
            float near = l.ListNearY;                       // 561 − 4
            Assert.AreEqual(557f, near, 0.01f);
            // 最新一行貼著底板上緣,舊的往上疊(行高 14)
            Assert.AreEqual(near - 14f, l.LineTopY(0, 3), 0.01f);
            Assert.AreEqual(near - 28f, l.LineTopY(1, 3), 0.01f);
            Assert.AreEqual(near - 42f, l.LineTopY(2, 3), 0.01f);
        }

        [Test]
        public void Top_Anchored_Lines_Fill_Downward_With_The_Newest_Last()
        {
            var l = GameplayChatLayout.Resolve(panelLeft: false, bottomDrop: true);
            float near = l.ListNearY;                       // 0 + 38 + 4
            Assert.AreEqual(42f, near, 0.01f);
            // 由上往下填:最舊的貼底板,最新的在最下面
            Assert.AreEqual(near, l.LineTopY(2, 3), 0.01f);        // 最舊
            Assert.AreEqual(near + 14f, l.LineTopY(1, 3), 0.01f);
            Assert.AreEqual(near + 28f, l.LineTopY(0, 3), 0.01f);  // 最新
        }

        [Test]
        public void Messages_Start_Left_Of_The_Chatmode_Button_Like_The_Official_Shot()
        {
            // 官方截圖(1:1 的 800×600 裁切)量出來字的左緣在 535,比 chatmode 鈕的 545 再往左 10px。
            Assert.AreEqual(535f, GameplayChatLayout.ListX, 0.01f);
            Assert.Less(GameplayChatLayout.ListX, GameplayChatLayout.BarX);   // 一定比按鈕列的左緣更左
            // 官方 TextList h=196 ÷ 行高 14 = 14 行。
            Assert.AreEqual(14, GameplayChatLayout.MaxLines);
            // 選單四顆的視覺上緣補償:只有「當前」那張(索引 2)在圖裡留了透明上緣
            // 選單槽位照官方 ROOMPOPMENU:2 / 27 / 52 / 77
            CollectionAssert.AreEqual(new[] { 2f, 27f, 52f, 77f }, GameplayChatLayout.ModeMenuSlotY);
            Assert.AreEqual(4, GameplayChatLayout.ModeArtTopPad.Length);
            Assert.AreEqual(2f, GameplayChatLayout.ModeArtTopPad[2], 0.01f);
            Assert.AreEqual(0f, GameplayChatLayout.ModeArtTopPad[0], 0.01f);
            Assert.AreEqual(196f, GameplayChatLayout.MaxLines * GameplayChatLayout.LineH, 0.01f);
        }

        [Test]
        public void Popups_Open_On_The_Message_Side_Never_Over_The_Input_Bar()
        {
            const float h = GameplayChatLayout.ExprPanelH;
            var bottom = GameplayChatLayout.Resolve(panelLeft: true, bottomDrop: false);
            // 右下:面板底邊**貼齊**底板上緣(不留空隙,否則看起來浮在半空)
            Assert.AreEqual(561f - h, bottom.PopupTopY(h), 0.01f);
            Assert.AreEqual(bottom.BarY, bottom.PopupTopY(h) + h, 0.01f);

            var top = GameplayChatLayout.Resolve(panelLeft: false, bottomDrop: true);
            // 右上:面板頂邊貼齊底板下緣
            Assert.AreEqual(GameplayChatLayout.BarH, top.PopupTopY(h), 0.01f);
            Assert.AreEqual(top.BarY + GameplayChatLayout.BarH, top.PopupTopY(h), 0.01f);
        }

        [Test]
        public void Expression_Panel_Right_Edge_Lines_Up_With_The_Expression_Button()
        {
            var l = GameplayChatLayout.Resolve(panelLeft: true, bottomDrop: false);
            float btnRight = l.BarItemX(GameplayChatLayout.ExprBtnDx) + GameplayChatLayout.BtnW;   // 760 + 33
            Assert.AreEqual(btnRight, l.ExprPanelX + GameplayChatLayout.ExprPanelW, 0.01f);
            Assert.AreEqual(793f, btnRight, 0.01f);
            // 條上四顆元件都落在底板(537..798)裡面
            Assert.GreaterOrEqual(GameplayChatLayout.ModeBtnX, GameplayChatLayout.BarX);
            Assert.LessOrEqual(btnRight, GameplayChatLayout.BarTailX + GameplayChatLayout.BarTailW);
        }

        [Test]
        public void A_Full_Screen_Of_Lines_Stays_Inside_The_800x600_Frame()
        {
            int n = GameplayChatLayout.MaxLines;
            var bottom = GameplayChatLayout.Resolve(panelLeft: true, bottomDrop: false);
            Assert.GreaterOrEqual(bottom.LineTopY(n - 1, n), 0f);                        // 最舊那行沒有頂出畫面
            Assert.LessOrEqual(bottom.LineTopY(0, n) + GameplayChatLayout.LineH, 561f);  // 最新那行沒有壓到底板

            var top = GameplayChatLayout.Resolve(panelLeft: false, bottomDrop: true);
            Assert.GreaterOrEqual(top.LineTopY(n - 1, n), 38f);                         // 最舊那行在底板下面
            Assert.LessOrEqual(top.LineTopY(0, n) + GameplayChatLayout.LineH, 600f);    // 最新那行沒有掉出畫面
        }
    }
}
