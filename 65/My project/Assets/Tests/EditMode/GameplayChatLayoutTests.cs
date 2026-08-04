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
            // 版面走**視覺**上緣 = 官方 friendchatmode / Familychatmode 的 y=568
            Assert.AreEqual(568f, l.BarItemY(GameplayChatLayout.ModeBtnDy), 0.01f);
            // 「當前」那張 51×30 換回 sprite 左上角,就是官方 chatmode 那行的 (545, 565)
            GameplayChatLayout.ModeArtTopLeft(2, l.BarItemX(GameplayChatLayout.ModeBtnDx),
                                              l.BarItemY(GameplayChatLayout.ModeBtnDy),
                                              out float cx, out float cy);
            Assert.AreEqual(544f, cx, 0.01f);
            Assert.AreEqual(565f, cy, 0.01f);   // ← XML 的 chatmode y
            // 49×25 那三張原樣擺在視覺位置(官方 x=545 / y=568)
            foreach (int ch in new[] { 0, 1, 3 })
            {
                GameplayChatLayout.ModeArtTopLeft(ch, l.BarItemX(GameplayChatLayout.ModeBtnDx),
                                                  l.BarItemY(GameplayChatLayout.ModeBtnDy),
                                                  out float bx, out float by);
                Assert.AreEqual(545f, bx, 0.01f);
                Assert.AreEqual(568f, by, 0.01f);
            }
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
            // 最新一行貼著底板上緣,舊的往上疊
            const float lh = GameplayChatLayout.LineH;
            Assert.AreEqual(near - lh, l.LineTopY(0, 3), 0.01f);
            Assert.AreEqual(near - lh * 2f, l.LineTopY(1, 3), 0.01f);
            Assert.AreEqual(near - lh * 3f, l.LineTopY(2, 3), 0.01f);
        }

        [Test]
        public void Top_Anchored_Lines_Fill_Downward_With_The_Newest_Last()
        {
            var l = GameplayChatLayout.Resolve(panelLeft: false, bottomDrop: true);
            float near = l.ListNearY;                       // 0 + 38 + 4
            Assert.AreEqual(42f, near, 0.01f);
            // 由上往下填:最舊的貼底板,最新的在最下面
            const float lh = GameplayChatLayout.LineH;
            Assert.AreEqual(near, l.LineTopY(2, 3), 0.01f);              // 最舊
            Assert.AreEqual(near + lh, l.LineTopY(1, 3), 0.01f);
            Assert.AreEqual(near + lh * 2f, l.LineTopY(0, 3), 0.01f);    // 最新
        }

        [Test]
        public void Messages_Start_Flush_With_The_Chatmode_Button()
        {
            // 字的左緣切齊 chatmode 鈕(545),而不是 XML TextList 的外框 550(那是框、不是字的起點)。
            Assert.AreEqual(545f, GameplayChatLayout.ListX, 0.01f);
            Assert.AreEqual(GameplayChatLayout.ModeBtnX, GameplayChatLayout.ListX, 0.01f);
            Assert.Greater(GameplayChatLayout.ListX, GameplayChatLayout.BarX);   // 在底板範圍內
            // 14 行(官方 TextList h=196 ÷ 原行高 14)。行高後來為了讓字叢更緊收到 13,整區只會更矮。
            Assert.AreEqual(14, GameplayChatLayout.MaxLines);
            Assert.LessOrEqual(GameplayChatLayout.MaxLines * GameplayChatLayout.LineH, 196f);
            // 選單槽位照官方 ROOMPOPMENU:2 / 27 / 52 / 77;
            // 四顆的視覺上緣補償只有「當前」那張(索引 2)非零 —— 它在圖裡留了透明上緣。
            CollectionAssert.AreEqual(new[] { 2f, 27f, 52f, 77f }, GameplayChatLayout.ModeMenuSlotY);
            Assert.AreEqual(4, GameplayChatLayout.ModeArtTopPad.Length);
            Assert.AreEqual(4, GameplayChatLayout.ModeArtLeftPad.Length);
            // 3 = 官方 XML 的 chatmode(565) 與 friendchatmode/Familychatmode(568) 之差
            Assert.AreEqual(3f, GameplayChatLayout.ModeArtTopPad[2], 0.01f);
            Assert.AreEqual(1f, GameplayChatLayout.ModeArtLeftPad[2], 0.01f);
            foreach (int ch in new[] { 0, 1, 3 })
            {
                Assert.AreEqual(0f, GameplayChatLayout.ModeArtTopPad[ch], 0.01f);
                Assert.AreEqual(0f, GameplayChatLayout.ModeArtLeftPad[ch], 0.01f);
            }
        }

        [Test]
        public void Chatmode_Menu_Buttons_Are_Evenly_Stacked_With_No_Gap()
        {
            // 使用者回報「家族/好友/當前/回復 間距一樣,unity 上的間距很亂」、「官方是全部五個都同樣間格」——
            // 亂的來源是「當前」那張圖多留的透明邊,扣掉之後四顆的**視覺**框必須首尾相接、左緣切齊,
            // 而且整柱要接上條上那顆 chatmode 鈕(第 5 顆),中間不留斷點。
            var l = GameplayChatLayout.Resolve(panelLeft: true, bottomDrop: false);
            float top = l.ModeMenuTopY;
            float menuX = l.BarItemX(GameplayChatLayout.ModeBtnDx);

            var visualTop = new float[4];
            for (int i = 0; i < 4; i++)
            {
                GameplayChatLayout.ModeArtTopLeft(i, menuX, top + GameplayChatLayout.ModeMenuSlotY[i],
                                                  out float x, out float y);
                // sprite 左上角加回自己的透明邊 = 視覺左上角,四顆都要落在官方槽位上
                Assert.AreEqual(menuX, x + GameplayChatLayout.ModeArtLeftPad[i], 0.01f);
                visualTop[i] = y + GameplayChatLayout.ModeArtTopPad[i];
                Assert.AreEqual(top + GameplayChatLayout.ModeMenuSlotY[i], visualTop[i], 0.01f);
            }
            // 相鄰兩顆:間距一致,而且剛好等於視覺高度 → 零縫、零重疊
            for (int i = 1; i < 4; i++)
                Assert.AreEqual(GameplayChatLayout.ModeArtVisualH, visualTop[i] - visualTop[i - 1], 0.01f);
            // 四顆疊起來正好塞滿官方選單框(2 上邊距 + 4×25 + 2 下邊距 = 104)
            Assert.AreEqual(GameplayChatLayout.ModeMenuH,
                            GameplayChatLayout.ModeMenuSlotY[0] * 2f + 4f * GameplayChatLayout.ModeArtVisualH, 0.01f);

            // 第 5 顆:條上那顆 chatmode 鈕接在「回復」正下方,同一個間距(選單因此壓過底板上緣幾 px)
            float barBtnTop = l.BarItemY(GameplayChatLayout.ModeBtnDy);
            Assert.AreEqual(GameplayChatLayout.ModeArtVisualH, barBtnTop - visualTop[3], 0.01f);
            Assert.AreEqual(568f, barBtnTop, 0.01f);
            Assert.AreEqual(466f, top, 0.01f);                       // 568 − 77 − 25
            Assert.Greater(top + GameplayChatLayout.ModeMenuH, l.BarY);   // 確實會疊到底板上(這是對的)

            // 右上版位:條上那顆在最上面,選單往下接,一樣是五顆同間距
            var t = GameplayChatLayout.Resolve(panelLeft: false, bottomDrop: true);
            float tBarBtnTop = t.BarItemY(GameplayChatLayout.ModeBtnDy);
            Assert.AreEqual(tBarBtnTop + GameplayChatLayout.ModeArtVisualH,
                            t.ModeMenuTopY + GameplayChatLayout.ModeMenuSlotY[0], 0.01f);
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
