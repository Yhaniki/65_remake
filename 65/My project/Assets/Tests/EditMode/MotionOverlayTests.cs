using System.IO;
using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 歌曲外掛包的動作 overlay（<see cref="MotionOverlay"/>）：一個歌包把它自帶的 <c>.dps</c> 和它引用的
    /// <c>.mot</c> 用跟 base 資料根一樣的樹狀結構擺在一起（<c>…/patch Datas/DANCE</c> + <c>…/patch Datas/MOTION|AUMOTION</c>）。
    /// 因此「這首歌的 dps 從哪棵樹讀出來，它的 mot 就在那棵樹」——當那不是 base 根時就當 overlay，查 mot 時先贏。
    /// 純路徑運算，不碰磁碟。
    /// </summary>
    public class MotionOverlayTests
    {
        // 以工作目錄為錨組一個絕對、正規化的路徑，測試就不必綁死某個磁碟機代號。
        private static string Abs(params string[] parts)
        {
            var all = new string[parts.Length + 1];
            all[0] = Path.GetFullPath(".");
            for (int i = 0; i < parts.Length; i++) all[i + 1] = parts[i];
            return Path.GetFullPath(Path.Combine(all));
        }

        private static string BaseRoot => Abs("clean", "DATA");

        [Test]
        public void PackDps_YieldsItsOwnTreeAsOverlay()
        {
            string packTree = Abs("songs", "MyPack", "patch Datas");
            string dps = Path.Combine(packTree, "DANCE", "10082.DPS");
            Assert.AreEqual(packTree, MotionOverlay.RootForDps(dps, BaseRoot));
        }

        [Test]
        public void PackDps_WithDotDot_NormalisesTheTree()
        {
            // sdo_pack.tsv 的相對 dps（"../patch Datas/DANCE/…"）經 Path.Combine 後夾帶 ".."（"…/patch music/../patch Datas/…"）
            // —— overlay 樹要收乾淨成 "…/patch Datas"。
            string dps = Path.Combine(Abs("songs", "MyPack", "patch music"),
                                      "..", "patch Datas", "DANCE", "10082.DPS");
            Assert.AreEqual(Abs("songs", "MyPack", "patch Datas"),
                            MotionOverlay.RootForDps(dps, BaseRoot));
        }

        [Test]
        public void BaseDps_IsNotAnOverlay()
        {
            // 官方歌的 dps 就在 base 根的 DANCE/ 下 → 不是外掛（overlay 只會重複探同一批檔）。
            string dps = Path.Combine(BaseRoot, "DANCE", "10040.DPS");
            Assert.AreEqual("", MotionOverlay.RootForDps(dps, BaseRoot));
        }

        [Test]
        public void BaseDps_WithTrailingSeparatorOnBase_StillNotAnOverlay()
        {
            string dps = Path.Combine(BaseRoot, "DANCE", "10040.DPS");
            Assert.AreEqual("", MotionOverlay.RootForDps(dps, BaseRoot + Path.DirectorySeparatorChar));
        }

        [Test]
        public void GeneratedExternalDps_NotInADanceDir_HasNoOverlay()
        {
            // 外部 osu/SM 歌臨時生成、直接躺在歌資料夾的 .dps —— 其上層不是 "DANCE"，不套外掛（用 base 一般舞步）。
            string dps = Path.Combine(Abs("songs", "MySong"), "ext_1a2b3c4d.gn.dps");
            Assert.AreEqual("", MotionOverlay.RootForDps(dps, BaseRoot));
        }

        [TestCase(null)]
        [TestCase("")]
        public void EmptyOrNull_HasNoOverlay(string dps)
            => Assert.AreEqual("", MotionOverlay.RootForDps(dps, BaseRoot));

        [Test]
        public void SamePath_NormalisesCaseAndTrailingSeparator()
        {
            string a = Abs("a", "B");
            Assert.IsTrue(MotionOverlay.SamePath(a, a + Path.DirectorySeparatorChar));   // 尾分隔符不影響
            Assert.IsTrue(MotionOverlay.SamePath(a, a.ToUpperInvariant()));              // 大小寫不影響（Windows 檔案系統）
            Assert.IsFalse(MotionOverlay.SamePath(a, Abs("a", "C")));
            Assert.IsFalse(MotionOverlay.SamePath("", a));
        }

        // ---- MatchFileName：歌曲資料夾裡不分大小寫找同名 .mot（讓使用者丟進歌旁的 .mot 覆蓋 dps 片段）----

        [Test]
        public void MatchFileName_IsCaseInsensitiveOnFileNameOnly()
        {
            var files = new[]
            {
                Abs("songs", "MySong", "WDANCE0531.MOT"),
                Abs("songs", "MySong", "Gokuraku Jodo.mp3"),
            };
            // dps 點名的是小寫 "wdance0531.mot"；資料夾裡是大寫 → 仍要命中，且回傳資料夾內的原始寫法。
            Assert.AreEqual(files[0], MotionOverlay.MatchFileName(files, "wdance0531.mot"));
        }

        [Test]
        public void MatchFileName_MatchesBareNameAgainstFullPaths_AndIgnoresDirComponent()
        {
            var files = new[] { Abs("songs", "MySong", "WDANCE0542.MOT") };
            // wanted 帶了路徑分量也只比檔名（dps 列的名字有時帶目錄）。
            Assert.AreEqual(files[0], MotionOverlay.MatchFileName(files, "MOTION/wdance0542.mot"));
        }

        [Test]
        public void MatchFileName_ReturnsNullWhenAbsent()
        {
            var files = new[] { Abs("songs", "MySong", "WDANCE0531.MOT") };
            Assert.IsNull(MotionOverlay.MatchFileName(files, "wdance9999.mot"));   // 資料夾沒有這顆
            Assert.IsNull(MotionOverlay.MatchFileName(files, ""));                 // 空名
            Assert.IsNull(MotionOverlay.MatchFileName(null, "wdance0531.mot"));    // 空清單
        }
    }
}
