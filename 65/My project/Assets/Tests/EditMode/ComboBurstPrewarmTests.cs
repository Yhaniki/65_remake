using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 100/200/300… COMBO 特效的「開場預熱」對照表。
    ///
    /// 預熱(<c>PrewarmComboBursts</c>)是在載入蓋板後面把每一檔的 .EFT / 貼圖 / 語音先讀進快取,好讓第一次達成
    /// combo 里程碑的那一幀只剩指標搬移。預熱能不能生效,完全取決於它跟「真的放特效時」讀的是不是同一份東西 ——
    /// 兩邊都走 <see cref="ScreenGameplay.ComboEftFileName"/> / <see cref="ScreenGameplay.MilestoneVoice"/>,
    /// 所以這兩張表就是預熱的正確性本身,一旦被改歪就是靜靜地退回「第一次會卡」。
    /// </summary>
    public class ComboBurstPrewarmTests
    {
        [Test]
        public void EftFileName_IsTheOfficialPerTierName()
        {
            Assert.AreEqual("100COMBO.EFT", ScreenGameplay.ComboEftFileName(0));
            Assert.AreEqual("200COMBO.EFT", ScreenGameplay.ComboEftFileName(1));
            Assert.AreEqual("300COMBO.EFT", ScreenGameplay.ComboEftFileName(2));
            Assert.AreEqual("400COMBO.EFT", ScreenGameplay.ComboEftFileName(3));
            Assert.AreEqual("500COMBO.EFT", ScreenGameplay.ComboEftFileName(4));
        }

        /// <summary>decompiled 的語音對照:100→0008 200→0007 300→0006 400→0005 500→0004(檔位越高編號越小)。</summary>
        [Test]
        public void MilestoneVoice_MatchesTheDecompiledTable()
        {
            Assert.AreEqual("VOICE_0008", ScreenGameplay.MilestoneVoice(100));
            Assert.AreEqual("VOICE_0007", ScreenGameplay.MilestoneVoice(200));
            Assert.AreEqual("VOICE_0006", ScreenGameplay.MilestoneVoice(300));
            Assert.AreEqual("VOICE_0005", ScreenGameplay.MilestoneVoice(400));
            Assert.AreEqual("VOICE_0004", ScreenGameplay.MilestoneVoice(500));
        }

        /// <summary>官方只有這五顆語音:50 那種半檔的里程碑、以及 500 以上都沒有聲音(特效則是夾在 500 檔)。
        /// 回 null 而不是硬給一個名字 —— 預熱迴圈就是靠這個判斷「這一檔沒有語音要暖」。</summary>
        [Test]
        public void MilestoneVoice_IsNullWhereTheOfficialHasNone()
        {
            foreach (var combo in new[] { 50, 150, 250, 600, 1000 })
                Assert.IsNull(ScreenGameplay.MilestoneVoice(combo), "combo " + combo);
        }
    }
}
