using System.IO;
using NUnit.Framework;
using UnityEngine;
using Sdo.Game;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>
    /// 房間頭貼徽章條上的 NO MAP / PLAYING 四色幀真的到得了執行期嗎。
    ///
    /// 素材是延續官方編號的 <c>C06..C09</c>(NO MAP)與 <c>D06..D09</c>(PLAYING),對齊 READY 的
    /// <c>a06..a09</c>(Room66.an)與 HOST 的 <c>b06..b09</c>(master.an):幀序同為 自由 / A / B / C。
    /// 這兩組官方沒有包成 .an(資料夾裡只有裸 PNG),所以 <c>RoomScreen.StateBadgeFrames</c> 是逐張讀的。
    ///
    /// 🔴 為什麼要有這個測試:原版 Extracted 沒有這幾張,它們住在 <c>art/generated</c>,要靠**兩支**打包腳本
    /// 各自 overlay 一次(<c>package_build.ps1</c> 與 <c>build_clean_data.ps1</c>)。只接一支的症狀是
    /// 「編輯器裡有圖、打包版沒圖」—— 那要等到出貨才會被發現,而且看起來像程式 bug 而不是打包漏了一步。
    ///
    /// 四張都畫在同一個 100×30 的矩形裡(<c>RoomScreen.BadgeW</c>/<c>BadgeH</c>),矮 3px 的 PLAYING 是被拉伸的。
    ///
    /// 分工同 <c>RoomButtonArtTests</c>:overlay 樹那幾條是硬性的(只讀 repo,與 DATA root 無關);
    /// 「載得進來嗎」那條依 DATA root,root 裡連官方 HOST 徽章都沒有就 Ignore(CI 上沒有資料夾很正常)。
    /// </summary>
    public class RoomStateBadgeArtTests
    {
        /// <summary>NO MAP = C06..C09、PLAYING = D06..D09(四色:黑 / 橘 / 綠 / 藍)。</summary>
        private static string[] BadgeFiles()
        {
            var names = new string[8];
            for (int i = 0; i < 4; i++)
            {
                names[i] = "C0" + (6 + i) + ".png";
                names[4 + i] = "D0" + (6 + i) + ".png";
            }
            return names;
        }

        // Application.dataPath = <repo>/65/My project/Assets(EditMode run)
        private static string RepoRoot
            => new DirectoryInfo(Application.dataPath).Parent.Parent.Parent.FullName;

        private static string OverlayDir
            => Path.Combine(RepoRoot, "art", "generated", "UI", "ROOM");

        [Test]
        public void Both_Build_Scripts_Overlay_The_Generated_Art_Tree()
        {
            // 與 RoomButtonArtTests 同一條守則,但兩邊都要有:哪天有人只修一支腳本,壞掉的會是這一組徽章。
            foreach (var name in new[] { "package_build.ps1", "build_clean_data.ps1" })
            {
                string p = Path.Combine(RepoRoot, "tools", name);
                Assert.IsTrue(File.Exists(p), "找不到打包腳本:" + p);
                StringAssert.Contains(@"art\generated", File.ReadAllText(p),
                    name + " 沒有 overlay art\\generated —— NO MAP / PLAYING 徽章在這條管線裡會缺圖");
            }
        }

        [Test]
        public void Generated_Overlay_Tree_Holds_All_Eight_Frames()
        {
            // 測的是 repo 裡的 overlay 樹(打包的來源),不是已經 copy 進 DATA 的結果 ——
            // 「只把圖放進自己的 DATA 就 commit」在這裡就會被抓到。
            Assert.IsTrue(Directory.Exists(OverlayDir), "art/generated/UI/ROOM 不存在");
            foreach (var name in BadgeFiles())
                Assert.IsTrue(File.Exists(Path.Combine(OverlayDir, name)),
                    name + " 不在 art/generated/UI/ROOM —— 少一色就是那一隊的人身上少一個徽章");
        }

        /// <summary>
        /// 徽章條上的尺寸假設。四張畫在**同一個 100×30 的矩形**裡(<c>RoomScreen.StretchToBadgeRow</c>),
        /// 素材本身則是 NO MAP 100×30、PLAYING 100×27 —— 也就是 PLAYING 那張是被垂直拉伸 3px 顯示的。
        ///
        /// 這條在意的是「拉伸幅度還在可接受範圍」:寬度必須正好是版位的 100(橫向被拉就會整條變形),
        /// 高度要在 27..30 之間(素材哪天換成 60 高,拉伸就會變成把圖壓扁一半而不是補 3px)。
        /// 讀 repo 的檔,不經 DATA root。
        /// </summary>
        [Test]
        public void The_Badge_Sizes_Match_What_The_Layout_Assumes()
        {
            Assert.IsTrue(Directory.Exists(OverlayDir), "art/generated/UI/ROOM 不存在");

            for (int i = 0; i < 4; i++)
            {
                AssertPngFitsBadgeRow("C0" + (6 + i) + ".png", 30);   // NO MAP:與版位同高,拉伸幅度 0
                AssertPngFitsBadgeRow("D0" + (6 + i) + ".png", 27);   // PLAYING:矮 3px,顯示時被拉到 30
            }
        }

        private static void AssertPngFitsBadgeRow(string name, int expectedHeight)
        {
            const int RowW = 100, RowH = 30;   // = RoomScreen.BadgeW / BadgeH
            string path = Path.Combine(OverlayDir, name);
            Assert.IsTrue(File.Exists(path), name + " 缺檔");
            var tex = new Texture2D(2, 2);
            Assert.IsTrue(tex.LoadImage(File.ReadAllBytes(path)), name + " 不是能解碼的 PNG");
            Assert.AreEqual(RowW, tex.width, name + " 寬度變了 —— 徽章條的版位是照 100px 算的");
            Assert.AreEqual(expectedHeight, tex.height, name + " 高度變了 —— 顯示時會被拉伸到 " + RowH);
            Assert.GreaterOrEqual(tex.height, RowH - 3,
                name + " 比版位矮太多 —— 拉伸到 " + RowH + " 會明顯變形,該調 RoomScreen.BadgeH 而不是硬拉");
            Object.DestroyImmediate(tex);
        }

        [Test]
        public void The_Badges_Load_As_Sprites_From_The_Data_Root()
        {
            // 官方 HOST 徽章(b06,同一個資料夾)當基準:連它都沒有 = 這台沒有資料夾 / root 解錯了 → Ignore。
            // 有它卻沒有 C/D = overlay 沒 copy 進這個 root(重跑 tools/build_clean_data.ps1)→ 該紅。
            if (RoomUiArt.Image("B06.PNG") == null)
                Assert.Ignore("DATA/UI/ROOM 不在這個環境裡(沒有 DATA root)。");

            foreach (var name in BadgeFiles())
            {
                var sprite = RoomUiArt.Image(name);
                Assert.IsNotNull(sprite,
                    name + " 載不到 —— DATA/UI/ROOM 少了這張(重跑 tools/build_clean_data.ps1)");
                Assert.Greater(sprite.rect.width, 0f);
                Assert.Greater(sprite.rect.height, 0f);
            }
        }
    }
}
