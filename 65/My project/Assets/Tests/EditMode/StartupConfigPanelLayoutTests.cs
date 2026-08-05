using NUnit.Framework;
using UnityEngine;
using Sdo.UI.Screens;

namespace Sdo.Tests
{
    /// <summary>
    /// 開場設定面板（<see cref="StartupConfigPanel"/>）的**直向預算**。
    ///
    /// 起因：面板底下的「儲存設定」被裁掉一半 —— 上面那疊（標題/名稱/體型/分頁/清單/說明）加起來剛好把
    /// <c>BeginArea</c> 的高度用光，IMGUI 於是從最後一個元素開始裁，剛好裁到這塊面板唯一的落地入口。
    /// 現在那一列改成釘在面板底的固定矩形、清單高度由剩餘空間算出來，這裡盯著那筆算式：**改任何一個版面常數，
    /// 只要餘裕不夠或清單被壓扁就會紅**。純算術，不畫 UI、不碰 Unity 執行期。
    /// </summary>
    public class StartupConfigPanelLayoutTests
    {
        /// <summary>展開時 BeginArea 拿到的高度（面板扣掉上下內距，再扣掉自己畫的底部那一列）。</summary>
        private const float LayoutH =
            StartupConfigPanel.ExpandedH - 2f * StartupConfigPanel.Pad
            - (StartupConfigPanel.FooterH + StartupConfigPanel.Pad);

        /// <summary>layout 區裡實際排進去的東西：上面那疊 + 清單 + 說明列。</summary>
        private const float ContentH =
            StartupConfigPanel.TopH + StartupConfigPanel.Gap
            + StartupConfigPanel.ListH + StartupConfigPanel.Gap + StartupConfigPanel.HelpH;

        [Test]
        public void Expanded_Layout_Fits_With_Slack()
        {
            float slack = LayoutH - ContentH;
            Assert.That(slack, Is.GreaterThanOrEqualTo(8f),
                        "版面已經吃滿 BeginArea：最後一個元素（說明列）會被裁掉。餘裕＝" + slack);
            Assert.That(slack, Is.EqualTo(StartupConfigPanel.Safety).Within(0.01f),
                        "ListH 應該是由剩餘空間算出來的，餘裕就該剛好是 Safety");
        }

        [Test]
        public void Footer_Rect_Stays_Inside_Panel()
        {
            // 跟 Draw 同一組算式：底部那一列釘在內距框的下緣。
            float innerTop = StartupConfigPanel.PanelY + StartupConfigPanel.Pad;
            float innerBottom = StartupConfigPanel.PanelY + StartupConfigPanel.ExpandedH - StartupConfigPanel.Pad;
            float footerTop = innerBottom - StartupConfigPanel.FooterH;

            Assert.That(footerTop, Is.GreaterThan(innerTop), "底部那一列不能翻到面板上緣去");
            Assert.That(innerBottom, Is.LessThanOrEqualTo(StartupConfigPanel.PanelY + StartupConfigPanel.ExpandedH),
                        "底部那一列要整條落在面板方框內（先前就是超出去被裁掉半顆鈕）");
        }

        [Test]
        public void List_Still_Shows_A_Dozen_Rows()
        {
            // MMD 是列最多的一頁（12 列）。清單縮得再小，也要一眼看得到大半頁，不然只是把「看不到」從
            // 底下那顆鈕搬到清單裡。
            float rowPitch = StartupConfigPanel.RowH + StartupConfigPanel.Gap;
            Assert.That(StartupConfigPanel.ListH / rowPitch, Is.GreaterThanOrEqualTo(12f),
                        "設定清單放不下 12 列了（ListH＝" + StartupConfigPanel.ListH + "）");
        }

        [Test]
        public void Expanded_Panel_Stops_Above_Gender_Checkboxes()
        {
            // 男/女核取方塊在 y=530（設計像素）。面板下緣壓過去就會擋到選性別。
            Assert.That(StartupConfigPanel.PanelY + StartupConfigPanel.ExpandedH, Is.LessThanOrEqualTo(530f));
        }

        // ---------------------------------------------------------------- 一列裡的直向對齊
        [Test]
        public void Slider_Track_Sits_In_The_Vertical_Middle_Of_Its_Row()
        {
            // 起因：滑桿的 style 有 fixedHeight（GUILayout.Height 對它無效），交給 layout 排就黏在列的上緣
            // —— 使用者回報「橫的拉桿應該要在欄位的上下中間」。現在每一列自己排，軌道拿 CenterV 擺。
            var row = new Rect(10f, 100f, 120f, StartupConfigPanel.RowH);
            var track = StartupConfigPanel.CenterV(row, row.x, 60f, 12f);
            Assert.That(track.center.y, Is.EqualTo(row.center.y).Within(0.01f), "拉桿要在那一列的上下正中間");
            Assert.That(track.height, Is.EqualTo(12f).Within(0.01f), "軌道自己的高度不該被改掉");
            Assert.That(track.y, Is.GreaterThanOrEqualTo(row.y - 0.01f));
            Assert.That(track.yMax, Is.LessThanOrEqualTo(row.yMax + 0.01f));
        }

        [Test]
        public void Row_Content_Never_Spills_Out_Of_Its_Row()
        {
            // 比一列還高的東西（換了字型之後 button/textField 都可能）只會填滿這一列，不會溢出去把上下兩列
            // 擠開 —— 列與列的高度一致就是「欄位高度不跑版」的定義。
            var row = new Rect(10f, 100f, 120f, StartupConfigPanel.RowH);
            var big = StartupConfigPanel.CenterV(row, row.x, 60f, StartupConfigPanel.RowH + 6f);
            Assert.That(big.y, Is.EqualTo(row.y).Within(0.01f));
            Assert.That(big.height, Is.EqualTo(row.height).Within(0.01f));
        }

        // ---------------------------------------------------------------- 下拉選單（Choice 那種列）
        private static Rect Row(float y) => new Rect(140f, y, 120f, StartupConfigPanel.RowH);
        private static float PanelBottom => StartupConfigPanel.PanelY + StartupConfigPanel.ExpandedH - StartupConfigPanel.Pad;
        private static float PanelTop => StartupConfigPanel.PanelY + StartupConfigPanel.Pad;

        [Test]
        public void Drop_Hangs_Under_The_Row_And_Keeps_Its_Width()
        {
            var row = Row(120f);
            var d = StartupConfigPanel.DropRect(row, 3);
            Assert.That(d.y, Is.EqualTo(row.yMax).Within(0.01f), "預設就掛在那一列下面");
            Assert.That(d.x, Is.EqualTo(row.x).Within(0.01f));
            Assert.That(d.width, Is.EqualTo(row.width).Within(0.01f), "寬度跟著那一列 —— 長模型名才有地方顯示");
            Assert.That(d.height, Is.EqualTo(3f * StartupConfigPanel.DropItemH + 2f * StartupConfigPanel.DropPad).Within(0.01f));
        }

        [Test]
        public void Drop_Flips_Above_The_Row_Near_The_Bottom()
        {
            // 清單那一區的最後幾列：往下展開會伸出面板外（而且蓋掉「儲存設定」）→ 要往上翻。
            var row = Row(PanelBottom - StartupConfigPanel.RowH - 4f);
            var d = StartupConfigPanel.DropRect(row, StartupConfigPanel.DropMaxRows);
            Assert.That(d.yMax, Is.LessThanOrEqualTo(row.y + 0.01f), "應該翻到那一列上面");
            Assert.That(d.y, Is.GreaterThanOrEqualTo(PanelTop - 0.01f), "翻上去也不能跑出面板");
        }

        [Test]
        public void Drop_Never_Shows_More_Than_DropMaxRows()
        {
            // 裝了 40 個 MMD 模型也一樣：清單一路長下去會蓋掉整個畫面、還會伸出面板外。
            var full = StartupConfigPanel.DropRect(Row(120f), StartupConfigPanel.DropMaxRows);
            var many = StartupConfigPanel.DropRect(Row(120f), 40);
            Assert.That(many.height, Is.EqualTo(full.height).Within(0.01f));
            Assert.That(many.y, Is.GreaterThanOrEqualTo(PanelTop - 0.01f));
            Assert.That(many.yMax, Is.LessThanOrEqualTo(PanelBottom + 0.01f));
        }

        [Test]
        public void Drop_Index_Follows_The_Mouse_And_The_Scroll()
        {
            var d = StartupConfigPanel.DropRect(Row(120f), 40);
            float firstMid = d.y + StartupConfigPanel.DropPad + StartupConfigPanel.DropItemH * 0.5f;
            Assert.AreEqual(0, StartupConfigPanel.DropIndexAt(d, firstMid, 0, 40));
            Assert.AreEqual(2, StartupConfigPanel.DropIndexAt(d, firstMid + 2f * StartupConfigPanel.DropItemH, 0, 40));
            Assert.AreEqual(12, StartupConfigPanel.DropIndexAt(d, firstMid + 2f * StartupConfigPanel.DropItemH, 10, 40),
                            "捲過之後，同一個位置對到的是後面的選項");
            // 邊界：外框那 1px 上、或最後一列下面一點點，都不能算出越界的 index
            Assert.AreEqual(0, StartupConfigPanel.DropIndexAt(d, d.y, 0, 40));
            Assert.AreEqual(39, StartupConfigPanel.DropIndexAt(d, d.yMax + 5f, 32, 40));
        }

        [Test]
        public void Drop_Scroll_Stops_At_Both_Ends()
        {
            Assert.AreEqual(0, StartupConfigPanel.ClampDropTop(-3, 40));
            Assert.AreEqual(40 - StartupConfigPanel.DropMaxRows, StartupConfigPanel.ClampDropTop(999, 40));
            Assert.AreEqual(0, StartupConfigPanel.ClampDropTop(3, 2), "選項不到一頁就沒得捲");
        }
    }
}
