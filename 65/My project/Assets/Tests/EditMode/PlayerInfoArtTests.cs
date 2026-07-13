using NUnit.Framework;
using UnityEngine;
using Sdo.Game;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>
    /// 個人資訊面板的官方素材真的從 data root 載得起來，而且裁出來的尺寸跟官方 .an 寫的一模一樣。
    ///
    /// 這一包 (UI/PLAYERINFORMATIONDLG) **離線版沒有**，只有線上 DatasSDO 有 → 這組測試同時也是
    /// 「data root 有沒有接上 link farm」的守門（沒接 → 整組 Ignore，不會假性失敗）。
    /// 接法見 tools/link_data_root.ps1 與 docs/USED_ASSETS_PLAYERINFO.md。
    /// </summary>
    public class PlayerInfoArtTests
    {
        [SetUp]
        public void SkipIfNotLinked()
        {
            if (!PlayerInfoArt.Available)
                Assert.Ignore("UI/PLAYERINFORMATIONDLG 不在 data root (" + SdoExtracted.Root + ") — 跑 tools/link_data_root.ps1");
        }

        private static void AssertSize(Sprite s, int w, int h, string what)
        {
            Assert.IsNotNull(s, what + " 載不到");
            Assert.AreEqual(w, Mathf.RoundToInt(s.rect.width), what + " 寬");
            Assert.AreEqual(h, Mathf.RoundToInt(s.rect.height), what + " 高");
        }

        [Test]
        public void Frame_And_Boards_MatchOfficialCrops()
        {
            AssertSize(PlayerInfoArt.Frame, 629, 512, "外框 PlayerInformationDlg0");
            AssertSize(PlayerInfoArt.Board0, 350, 340, "基本信息底圖 PlayerInformationDlg34");
            AssertSize(PlayerInfoArt.Board1, 350, 340, "技术统计底圖 PlayerInformationDlg43");
        }

        [Test]
        public void TabStrips_AreFullWidth_SoStackingThemFormsTheTabBar()
        {
            // 每個分頁的 .an 都是整條 350×39、只畫自己那一格（其餘透明）→ 疊起來才是完整分頁列
            AssertSize(PlayerInfoArt.Tab0N, 350, 39, "分頁0 暗");
            AssertSize(PlayerInfoArt.Tab0P, 350, 39, "分頁0 亮");
            AssertSize(PlayerInfoArt.Tab1N, 350, 39, "分頁1 暗");
            AssertSize(PlayerInfoArt.Tab1P, 350, 39, "分頁1 亮");
        }

        [Test]
        public void SkillTab_Art_MatchesOfficialCrops()
        {
            AssertSize(PlayerInfoArt.SkillBg, 322, 190, "六列底板 SkillBg");
            AssertSize(PlayerInfoArt.BarFill, 232, 19, "進度條填充 PlayerInformationDlg65");
            AssertSize(PlayerInfoArt.EffortBtnN, 109, 30, "成就鈕");
            AssertSize(PlayerInfoArt.SkillBtnP, 109, 30, "统计明细鈕(選中)");
        }

        [Test]
        public void Buttons_Load()
        {
            AssertSize(PlayerInfoArt.CloseN, 29, 29, "關閉鈕");
            AssertSize(PlayerInfoArt.ConfirmN, 86, 35, "確定鈕");
        }
    }
}
