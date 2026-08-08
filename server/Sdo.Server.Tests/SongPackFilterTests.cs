using System.Collections.Generic;
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
            Assert.AreEqual(PackFileVerdict.Generated, SongPackFilter.Classify("cd.png", Small));
            Assert.AreEqual(PackFileVerdict.Generated, SongPackFilter.Classify("cd_foo_1a2b.png", Small));
            Assert.AreEqual(PackFileVerdict.Generated, SongPackFilter.Classify("dance.dps", Small));
            Assert.AreEqual(PackFileVerdict.Generated, SongPackFilter.Classify("dance_foo.dps", Small));
        }

        // ---- 客製編舞(使用者自己放進歌曲資料夾的)----

        [Test]
        public void Custom_Choreography_Is_Transferred()
        {
            // 🔴 sidecar 的 #DPS/#MOT/#CAMERA 指名的那些檔。不傳的話收端只會拿到自動生成的舞 ——
            // 同一場裡房主跳作者編的、別人跳亂數生的。
            Assert.AreEqual(PackFileVerdict.Include, SongPackFilter.Classify("12951.dps", Small));
            Assert.AreEqual(PackFileVerdict.Include, SongPackFilter.Classify("WDANCE0272.MOT", Small));
            Assert.AreEqual(PackFileVerdict.Include, SongPackFilter.Classify("stage.cdt", Small));
            // 客製 CD 圖也可以是 .dds(見 ExternalCdImage.LoadDdsSprite:#CDIMAGE:12956.DDS)。
            Assert.AreEqual(PackFileVerdict.Include, SongPackFilter.Classify("12956.dds", Small));
        }

        [Test]
        public void Generated_Dance_Is_Still_Not_Transferred()
        {
            // .dps 進了白名單,但**遊戲自己生的**那一支仍然不傳:收端用同一個 seed 重生成一份一樣的。
            Assert.AreEqual(PackFileVerdict.Generated, SongPackFilter.Classify("dance.dps", Small));
            Assert.AreEqual(PackFileVerdict.Generated, SongPackFilter.Classify("dance_audio_a_1a2b.dps", Small));
            Assert.IsFalse(SongPackFilter.IsTransferable("dance.dps", Small));
            Assert.IsTrue(SongPackFilter.IsTransferable("12951.dps", Small));
        }

        // ---- companion:側車檔 ----

        [Test]
        public void Sidecar_Is_A_Companion_Transferred_But_Not_Identity()
        {
            // 它是「這首歌用哪一支編舞/哪張碟/offset 多少」的唯一指標 → 要傳;
            // 但它是執行期會被改寫的檔 → 不能算進 packId。
            Assert.AreEqual(PackFileVerdict.Companion, SongPackFilter.Classify("sdoinfo.dat", Small));
            Assert.AreEqual(PackFileVerdict.Companion, SongPackFilter.Classify("SDOINFO.DAT", Small));
            Assert.AreEqual(PackFileVerdict.Companion, SongPackFilter.Classify("sdo.header", Small));
            Assert.IsTrue(SongPackFilter.IsTransferable("sdoinfo.dat", Small));
        }

        [Test]
        public void Sidecar_Does_Not_Change_The_PackId()
        {
            // 🔴 播一次歌就會改寫 sdoinfo.dat。它若進了身分,送端玩過一次自己的 packId 就變了。
            var bare = new List<PackFileEntry> { new PackFileEntry("a.osu", 10, Sha("a")) };
            var withSidecar = new List<PackFileEntry>
            {
                new PackFileEntry("a.osu", 10, Sha("a")),
                new PackFileEntry("sdoinfo.dat", 200, Sha("s")),
            };
            var rewritten = new List<PackFileEntry>
            {
                new PackFileEntry("a.osu", 10, Sha("a")),
                new PackFileEntry("sdoinfo.dat", 340, Sha("t")),   // 玩過一次 → 多了 #DPS/#DPSVER
            };
            Assert.AreEqual(SongPackId.Compute(bare), SongPackId.Compute(withSidecar));
            Assert.AreEqual(SongPackId.Compute(bare), SongPackId.Compute(rewritten));
        }

        [Test]
        public void Custom_Dance_Does_Change_The_PackId()
        {
            // 反過來:客製編舞是「這首歌長什麼樣」的一部分 —— 換一支就是換一份歌。
            var without = new List<PackFileEntry> { new PackFileEntry("a.osu", 10, Sha("a")) };
            var with = new List<PackFileEntry>
            {
                new PackFileEntry("a.osu", 10, Sha("a")),
                new PackFileEntry("12951.dps", 5000, ""),
            };
            Assert.AreNotEqual(SongPackId.Compute(without), SongPackId.Compute(with));
        }

        private static string Sha(string seed) => seed.PadRight(64, '0');

        [Test]
        public void The_Editor_Backup_Folder_Is_Skipped_Whole()
        {
            // 🔴 StepMania/ArrowVortex 每存一次譜就往 <歌>/FileBackup/ 丟一個帶時間戳的 .sm。
            // 那是編輯歷史,不是這首歌:算進 packId 的話每存一次就換一個身分,房裡每個人都得重下一遍。
            Assert.AreEqual(PackFileVerdict.Generated,
                SongPackFilter.Classify("filebackup/2026-04-26_200351.sm", Small));
            Assert.AreEqual(PackFileVerdict.Generated,
                SongPackFilter.Classify("FileBackup/2026-04-26_200351.sm", Small), "資料夾名不分大小寫");
            Assert.AreEqual(PackFileVerdict.Generated,
                SongPackFilter.Classify("FileBackup\\old.sm", Small), "反斜線也要認得");
            Assert.IsFalse(SongPackFilter.IsTransferable("filebackup/old.sm", Small));

            // 別誤殺:同名的**檔案**、名字只是開頭像的資料夾、以及其他一層子夾(osu 的 sb/)都要照傳。
            Assert.AreEqual(PackFileVerdict.Include, SongPackFilter.Classify("sb/overlay.png", Small));
            Assert.AreEqual(PackFileVerdict.Include, SongPackFilter.Classify("filebackups/a.sm", Small));
            Assert.IsFalse(SongPackFilter.IsEditorBackup("filebackup.sm"), "根目錄下的檔案不是備份夾");
            Assert.IsFalse(SongPackFilter.IsEditorBackup("sb/filebackup/x.png"));
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

        // ---- 檔案數上限 ----

        [Test]
        public void KeysoundedMapsFitUnderTheFileCountLimit()
        {
            // key 音的圖每個 note 一個 wav,幾百個檔是正常的。實機案例:STAGER 有 291 個檔,
            // 舊上限 200 直接把它擋成「檔案數不合理」,缺歌的人永遠補不到。
            Assert.GreaterOrEqual(NetPackLimits.MaxPackFiles, 291,
                "上限要容得下實際存在的 key 音圖,否則那些歌永遠傳不出去");
        }

        [Test]
        public void TheWholeManifestStillFitsInOneMessage()
        {
            // manifest 是**一個訊息**送的。上限乘上單項最壞情況必須留在 frame payload 之內,
            // 否則「檔案數放寬」會換來一個更難查的失敗:傳輸在送清單那一刻就爆掉。
            const int worstCaseBytesPerEntry = 300;   // {"path":<長路徑>,"len":…,"sha256":<64 hex>}
            long manifestBytes = (long)NetPackLimits.MaxPackFiles * worstCaseBytesPerEntry;
            Assert.Less(manifestBytes, Sdo.Net.NetLimits.MaxFramePayload,
                "MaxPackFiles 調過頭了 —— 整份清單會超過單一訊息的上限");
        }

        [Test]
        public void NetLimitsMirrorsThePackLimit()
        {
            // 兩邊同名常數必須相等(NetLimits 那顆是指過來的,這條防的是有人把它改成獨立的字面值)。
            Assert.AreEqual(NetPackLimits.MaxPackFiles, Sdo.Net.NetLimits.MaxPackFiles);
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
