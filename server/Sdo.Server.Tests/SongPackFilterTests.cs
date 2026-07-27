using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// 「哪些檔可以跟著歌傳出去」的過濾規則。
    ///
    /// client 用它決定要上傳什麼;server 用它重新驗證上傳者送來的清單(絕不信任 host)。
    /// 使用者的明確要求之一是「host 上傳自動 filter 掉影片檔案」—— 影片有專屬的判定值，
    /// 不只是被白名單擋掉，這樣才能回報「跳過 3 個影片檔(共 87 MB)」。
    /// </summary>
    public class SongPackFilterTests
    {
        private const long Small = 1024;

        // ---- 放行 ----

        [Test]
        public void Chart_Audio_And_Image_Files_Are_Included()
        {
            Assert.AreEqual(PackFileVerdict.Include, SongPackFilter.Classify("song.osu", Small));
            Assert.AreEqual(PackFileVerdict.Include, SongPackFilter.Classify("song.sm", Small));
            Assert.AreEqual(PackFileVerdict.Include, SongPackFilter.Classify("song.gn", Small));
            Assert.AreEqual(PackFileVerdict.Include, SongPackFilter.Classify("song.mc", Small));
            Assert.AreEqual(PackFileVerdict.Include, SongPackFilter.Classify("sdo_pack.tsv", Small));
            Assert.AreEqual(PackFileVerdict.Include, SongPackFilter.Classify("audio.ogg", Small));
            Assert.AreEqual(PackFileVerdict.Include, SongPackFilter.Classify("audio.mp3", Small));
            Assert.AreEqual(PackFileVerdict.Include, SongPackFilter.Classify("audio.wav", Small));
            Assert.AreEqual(PackFileVerdict.Include, SongPackFilter.Classify("bg.png", Small));
            Assert.AreEqual(PackFileVerdict.Include, SongPackFilter.Classify("bg.jpg", Small));
            Assert.AreEqual(PackFileVerdict.Include, SongPackFilter.Classify("bg.jpeg", Small));
            Assert.AreEqual(PackFileVerdict.Include, SongPackFilter.Classify("bg.bmp", Small));
        }

        [Test]
        public void Osu_Storyboard_And_Skin_Ini_Are_Included()
        {
            // osu 的資料夾常有這兩個,少了譜面在某些情況會表現不同。
            Assert.AreEqual(PackFileVerdict.Include, SongPackFilter.Classify("song.osb", Small));
            Assert.AreEqual(PackFileVerdict.Include, SongPackFilter.Classify("skin.ini", Small));
        }

        [Test]
        public void Extension_Matching_Is_Case_Insensitive()
        {
            Assert.AreEqual(PackFileVerdict.Include, SongPackFilter.Classify("SONG.OSU", Small));
            Assert.AreEqual(PackFileVerdict.Video, SongPackFilter.Classify("BG.MP4", Small));
        }

        [Test]
        public void One_Level_Subfolder_Is_Allowed()
        {
            Assert.AreEqual(PackFileVerdict.Include, SongPackFilter.Classify("sb/overlay.png", Small));
        }

        // ---- 🔴 影片(使用者明確要求) ----

        [Test]
        public void Videos_Are_Classified_As_Video_Not_Just_Unknown()
        {
            // 分類要精確,才能回報「跳過了幾個影片、多少 MB」。
            string[] videos = { "bg.mp4", "bg.avi", "bg.flv", "bg.wmv", "bg.mkv", "bg.mov",
                                "bg.webm", "bg.mpg", "bg.mpeg", "bg.m4v", "bg.ts", "bg.rmvb",
                                "bg.asf", "bg.ogv", "bg.3gp" };
            foreach (var v in videos)
                Assert.AreEqual(PackFileVerdict.Video, SongPackFilter.Classify(v, Small), v);
        }

        // ---- 安全 ----

        [Test]
        public void Executables_And_Scripts_Are_Rejected()
        {
            // 絕不把可執行的東西搬到別人的磁碟上。
            string[] bad = { "a.exe", "a.dll", "a.bat", "a.cmd", "a.sh", "a.ps1",
                             "a.msi", "a.scr", "a.lnk", "a.com", "a.vbs", "a.js", "a.jar" };
            foreach (var b in bad)
                Assert.AreEqual(PackFileVerdict.Executable, SongPackFilter.Classify(b, Small), b);
        }

        [Test]
        public void Archives_Are_Rejected()
        {
            // 又大又冗餘 —— 內容通常就是旁邊那些檔。
            string[] bad = { "a.zip", "a.rar", "a.7z", "a.osz", "a.osk" };
            foreach (var b in bad)
                Assert.AreEqual(PackFileVerdict.Archive, SongPackFilter.Classify(b, Small), b);
        }

        [Test]
        public void Unsafe_Paths_Are_Rejected_Before_Anything_Else()
        {
            // 即使副檔名合法,路徑不安全就直接擋 —— 順序很重要。
            Assert.AreEqual(PackFileVerdict.UnsafePath, SongPackFilter.Classify("../evil.osu", Small));
            Assert.AreEqual(PackFileVerdict.UnsafePath, SongPackFilter.Classify("C:\\evil.osu", Small));
            Assert.AreEqual(PackFileVerdict.UnsafePath, SongPackFilter.Classify("CON.osu", Small));
        }

        [Test]
        public void Too_Deep_Paths_Are_Rejected()
        {
            Assert.AreEqual(PackFileVerdict.TooDeep, SongPackFilter.Classify("a/b/c.png", Small));
            Assert.AreEqual(PackFileVerdict.TooDeep, SongPackFilter.Classify("a/b/c/d.png", Small));
        }

        // ---- 生成物 ----

        [Test]
        public void Generated_Artifacts_Are_Skipped()
        {
            // 收端自己會重生這些 —— 傳了是浪費，而且可能蓋掉對方已經校正過的版本。
            Assert.AreEqual(PackFileVerdict.Generated, SongPackFilter.Classify("sdoinfo.dat", Small));
            Assert.AreEqual(PackFileVerdict.Generated, SongPackFilter.Classify("sdo.header", Small));
            Assert.AreEqual(PackFileVerdict.Generated, SongPackFilter.Classify("cd.png", Small));
            Assert.AreEqual(PackFileVerdict.Generated, SongPackFilter.Classify("cd_foo_1a2b.png", Small));
            Assert.AreEqual(PackFileVerdict.Generated, SongPackFilter.Classify("dance.dps", Small));
            Assert.AreEqual(PackFileVerdict.Generated, SongPackFilter.Classify("dance_foo.dps", Small));
        }

        [Test]
        public void IsGenerated_Is_Case_Insensitive()
        {
            Assert.IsTrue(SongPackFilter.IsGenerated("SDOINFO.DAT"));
            Assert.IsTrue(SongPackFilter.IsGenerated("CD.PNG"));
            Assert.IsTrue(SongPackFilter.IsGenerated("Dance_X.dps"));
        }

        [Test]
        public void Files_That_Merely_Look_Generated_Are_Not_Skipped()
        {
            // 真的歌可能就叫 "cdrom.mp3" 或 "dancing queen.osu" —— 別誤殺。
            Assert.IsFalse(SongPackFilter.IsGenerated("cdrom.mp3"));
            Assert.IsFalse(SongPackFilter.IsGenerated("dancing queen.osu"));
            Assert.IsFalse(SongPackFilter.IsGenerated("cd.jpg"), "只有 cd.png 是生成物");
        }

        // ---- 未知型別 ----

        [Test]
        public void Unknown_Extensions_Are_Rejected()
        {
            // 白名單制:不認得的東西一律不傳。
            Assert.AreEqual(PackFileVerdict.UnknownType, SongPackFilter.Classify("readme.txt", Small));
            Assert.AreEqual(PackFileVerdict.UnknownType, SongPackFilter.Classify("notes.docx", Small));
            Assert.AreEqual(PackFileVerdict.UnknownType, SongPackFilter.Classify("noext", Small));
        }

        // ---- 大小 ----

        [Test]
        public void Oversized_Single_File_Is_Rejected()
        {
            // 32 MB 上限主要擋的是「改名成 .ogg 的影片」。
            Assert.AreEqual(PackFileVerdict.Include,
                SongPackFilter.Classify("a.ogg", NetPackLimits.MaxSingleFileBytes));
            Assert.AreEqual(PackFileVerdict.TooBig,
                SongPackFilter.Classify("a.ogg", NetPackLimits.MaxSingleFileBytes + 1));
        }

        [Test]
        public void Oversized_Image_Is_Rejected_At_A_Lower_Limit()
        {
            // 圖片另有 4 MB 上限 —— 4K 背景圖對 800×600 的遊戲毫無意義。
            Assert.AreEqual(PackFileVerdict.Include,
                SongPackFilter.Classify("bg.png", NetPackLimits.MaxImageFileBytes));
            Assert.AreEqual(PackFileVerdict.TooBig,
                SongPackFilter.Classify("bg.png", NetPackLimits.MaxImageFileBytes + 1));

            // 但同樣大小的音檔是可以的(它只受 32 MB 那條限制)。
            Assert.AreEqual(PackFileVerdict.Include,
                SongPackFilter.Classify("a.mp3", NetPackLimits.MaxImageFileBytes + 1));
        }

        [Test]
        public void Unreadable_Size_Is_Treated_As_Not_Transferable()
        {
            // 讀不到大小(-1)代表檔案有問題 —— 不要冒險傳。
            Assert.AreEqual(PackFileVerdict.TooBig, SongPackFilter.Classify("a.osu", -1));
        }

        [Test]
        public void IsTransferable_Agrees_With_Classify()
        {
            Assert.IsTrue(SongPackFilter.IsTransferable("a.osu", Small));
            Assert.IsFalse(SongPackFilter.IsTransferable("a.mp4", Small));
            Assert.IsFalse(SongPackFilter.IsTransferable("../a.osu", Small));
        }

        // ---- 小工具的邊界 ----

        [Test]
        public void Depth_Counts_Separators()
        {
            Assert.AreEqual(0, SongPackFilter.Depth("a.osu"));
            Assert.AreEqual(1, SongPackFilter.Depth("sb/a.png"));
            Assert.AreEqual(1, SongPackFilter.Depth("sb\\a.png"), "兩種分隔符都要算");
            Assert.AreEqual(2, SongPackFilter.Depth("a/b/c.png"));
            Assert.AreEqual(0, SongPackFilter.Depth(""));
            Assert.AreEqual(0, SongPackFilter.Depth(null));
        }

        [Test]
        public void FileNameOf_Takes_The_Last_Segment()
        {
            Assert.AreEqual("a.osu", SongPackFilter.FileNameOf("a.osu"));
            Assert.AreEqual("a.png", SongPackFilter.FileNameOf("sb/a.png"));
            Assert.AreEqual("a.png", SongPackFilter.FileNameOf("sb\\a.png"));
            Assert.AreEqual("", SongPackFilter.FileNameOf(""));
            Assert.AreEqual("", SongPackFilter.FileNameOf(null));
        }

        [Test]
        public void ExtensionOf_Handles_Edge_Cases()
        {
            Assert.AreEqual(".osu", SongPackFilter.ExtensionOf("a.osu"));
            Assert.AreEqual(".osu", SongPackFilter.ExtensionOf("a.b.osu"), "取最後一個句點之後");
            Assert.AreEqual(".osu", SongPackFilter.ExtensionOf("A.OSU"), "回傳小寫");
            Assert.AreEqual("", SongPackFilter.ExtensionOf("noext"));
            Assert.AreEqual("", SongPackFilter.ExtensionOf("trailing."), "句點結尾不算副檔名");
            Assert.AreEqual("", SongPackFilter.ExtensionOf(""));
            Assert.AreEqual("", SongPackFilter.ExtensionOf(null));
        }
    }
}
