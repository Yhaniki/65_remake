using NUnit.Framework;
using Sdo.Game.Net;
using Sdo.Osu;
using Sdo.UI.Core;

namespace Sdo.Tests
{
    /// <summary>
    /// 外部歌上線時,譜面路徑要從「本機絕對路徑」翻成「相對歌曲資料夾」。
    ///
    /// 🔴 這是實機驗證抓到的真 bug:<c>GameSession.ExternalChartPath</c> 是絕對路徑,
    /// 原本直接塞進 <c>NetSongRef.ChartRelPath</c> → server 的 <c>SafeRelPath.IsSafe</c> 擋掉
    /// (它不收磁碟機代號)→ 整個 setSong 回 badState,而畫面上只是「選了歌但房間沒歌」。
    /// 當時所有單元測試都是綠的 —— 沒有一條測到「絕對路徑進 wire」。這幾條就是補那個洞。
    /// </summary>
    public class NetSongRefChartPathTests
    {
        [Test]
        public void An_Absolute_Chart_Path_Becomes_Relative_To_The_Song_Folder()
        {
            Assert.AreEqual("song.osu",
                NetSongPublisher.ToChartRelPath(@"H:\Songs\My Pack", @"H:\Songs\My Pack\song.osu"));
        }

        [Test]
        public void A_Chart_In_A_Subfolder_Keeps_The_Subfolder()
        {
            Assert.AreEqual("sub/song.osu",
                NetSongPublisher.ToChartRelPath(@"H:\Songs\My Pack", @"H:\Songs\My Pack\sub\song.osu"));
        }

        [Test]
        public void The_Result_Passes_The_Servers_Safety_Check()
        {
            // 這才是重點:server 會用這個函式擋。翻出來的東西一定要過得了。
            var rel = NetSongPublisher.ToChartRelPath(@"H:\Songs\My Pack", @"H:\Songs\My Pack\song.osu");
            Assert.IsTrue(SafeRelPath.IsSafe(rel), rel + " 過不了 SafeRelPath.IsSafe → server 會拒絕整個 setSong");

            // 反例:絕對路徑本來就過不了 —— 這正是那個 bug 的形狀。
            Assert.IsFalse(SafeRelPath.IsSafe(@"H:\Songs\My Pack\song.osu"), "絕對路徑不該被當成安全的相對路徑");
        }

        [Test]
        public void Forward_And_Back_Slashes_Both_Work()
        {
            Assert.AreEqual("song.osu",
                NetSongPublisher.ToChartRelPath("H:/Songs/My Pack", @"H:\Songs\My Pack\song.osu"));
            Assert.AreEqual("song.osu",
                NetSongPublisher.ToChartRelPath(@"H:\Songs\My Pack\", "H:/Songs/My Pack/song.osu"));
        }

        [Test]
        public void A_Chart_Outside_The_Folder_Falls_Back_To_The_File_Name()
        {
            // 切不出相對路徑時退回檔名 —— 那仍是合法且能用的相對路徑。
            // 回空字串會讓 server 拒絕整個 setSong(房間就沒歌了),那比退回檔名糟得多。
            var rel = NetSongPublisher.ToChartRelPath(@"H:\Other", @"H:\Songs\My Pack\song.osu");
            Assert.AreEqual("song.osu", rel);
            Assert.IsTrue(SafeRelPath.IsSafe(rel));
        }

        [Test]
        public void No_Chart_Path_Stays_Empty()
        {
            Assert.AreEqual("", NetSongPublisher.ToChartRelPath(@"H:\Songs\My Pack", null));
            Assert.AreEqual("", NetSongPublisher.ToChartRelPath(null, ""));
        }
    }

    /// <summary>
    /// 下載來的歌要放在哪個資料夾 —— 唯一的純函式,所以唯一能單元測試的部分。
    /// (真正的傳輸有 server 那邊的端到端測試守著:BlobTransferTests。)
    ///
    /// 為什麼這條值得測:這個字串會**直接變成檔案系統上的資料夾名**。裡面有非法字元的話,
    /// 症狀是「下載完成但歌沒出現」——因為 CreateDirectory 丟了例外、而那被當成一般的傳輸失敗。
    /// 歌名裡有 <c>:</c> 或 <c>?</c> 的 osu 圖非常常見。
    /// </summary>
    public class NetSongTransferTests
    {
        private const string Pack = "sha256:0123456789abcdef0123456789abcdef";

        [Test]
        public void The_Folder_Name_Is_Title_Artist_And_A_Pack_Tag()
        {
            Assert.AreEqual("夜に駆ける - YOASOBI [01234567]",
                NetSongFetcher.ConnectFolderName("夜に駆ける", "YOASOBI", Pack));
        }

        [Test]
        public void Characters_Windows_Forbids_Become_Underscores()
        {
            // \ / : * ? " < > | —— 這幾個在 Windows 上是非法的,而歌名裡出現冒號與問號很常見。
            var name = NetSongFetcher.ConnectFolderName("A:B/C*D?E\"F<G>H|I\\J", "art", Pack);
            foreach (var c in "\\/:*?\"<>|")
                Assert.IsFalse(name.IndexOf(c) >= 0, "資料夾名還留著非法字元 " + c + ":" + name);
            StringAssert.StartsWith("A_B_C_D_E_F_G_H_I_J", name);
        }

        [Test]
        public void A_Trailing_Space_Or_Dot_Is_Trimmed()
        {
            // 🔴 Windows 會**靜默**去掉結尾的空白與句點:建出來的資料夾名與你要求的不一樣,
            //    之後拿原字串去比對就永遠對不上。
            var name = NetSongFetcher.ConnectFolderName("結尾有點...", "", Pack);
            StringAssert.StartsWith("結尾有點 [", name);

            var name2 = NetSongFetcher.ConnectFolderName("結尾有空白   ", "", Pack);
            StringAssert.StartsWith("結尾有空白 [", name2);
        }

        [Test]
        public void The_Pack_Tag_Makes_Two_Same_Named_Songs_Land_In_Different_Folders()
        {
            // 一律加上 packId 前 8 碼 → 撞名問題直接消失(同名但內容不同的歌是不同的資料夾),
            // 而且看資料夾就知道它來自哪一份包。
            var a = NetSongFetcher.ConnectFolderName("同名歌", "同一個人", Pack);
            var b = NetSongFetcher.ConnectFolderName("同名歌", "同一個人", SongPackId.Prefix + "ffffffffffffffffffffffffffffffff");
            Assert.AreNotEqual(a, b);
        }

        [Test]
        public void An_Empty_Title_Still_Produces_A_Usable_Name()
        {
            var name = NetSongFetcher.ConnectFolderName("", "", Pack);
            Assert.AreEqual("song [01234567]", name);
        }

        [Test]
        public void A_Missing_Pack_Id_Does_Not_Produce_A_Broken_Name()
        {
            Assert.AreEqual("歌 [unknown]", NetSongFetcher.ConnectFolderName("歌", "", null));
            Assert.AreEqual("歌 [unknown]", NetSongFetcher.ConnectFolderName("歌", "", "短"));
        }

        [Test]
        public void A_Very_Long_Title_Is_Truncated()
        {
            // Windows 的路徑長度上限是真的會踩到的(ADDON/SONG/connect/<這個名字>/<檔名>)。
            var name = NetSongFetcher.ConnectFolderName(new string('長', 300), new string('人', 300), Pack);
            Assert.LessOrEqual(name.Length, 60 + 11, "名字要截短,不然整條路徑會超過 Windows 的上限");
            StringAssert.EndsWith("[01234567]", name);
        }
    }
}
