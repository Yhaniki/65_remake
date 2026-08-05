using NUnit.Framework;
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
    }
}
