using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// 相對路徑的安全驗證。
    ///
    /// 🔴 這是整個傳檔功能最重要的一道安全關卡:上傳端送來的每個路徑，**收端會直接拿它在自己的
    /// 磁碟上建檔**。一個沒擋住的 <c>..\..\..\Windows\System32\</c> 就是任意檔案覆寫。
    /// server 端還會再獨立驗一次(絕不信任 host)，但 client 這邊也不能放行。
    /// </summary>
    public class SafeRelPathTests
    {
        // ---- 正常的路徑要放行 ----

        [Test]
        public void Ordinary_Song_Files_Are_Safe()
        {
            Assert.IsTrue(SafeRelPath.IsSafe("song.osu"));
            Assert.IsTrue(SafeRelPath.IsSafe("audio.mp3"));
            Assert.IsTrue(SafeRelPath.IsSafe("Kalopsia [Insane].osu"), "osu 的檔名常有方括號與空白");
            Assert.IsTrue(SafeRelPath.IsSafe("危險的演出.sm"), "CJK 檔名是常態");
            Assert.IsTrue(SafeRelPath.IsSafe("sb/overlay.png"), "一層子夾(osu 的分鏡素材)");
            Assert.IsTrue(SafeRelPath.IsSafe("a.b.c.osu"), "多個句點沒問題");
            Assert.IsTrue(SafeRelPath.IsSafe("track..1.osu"), "檔名中間的連續句點是合法的 —— 只有整段等於 .. 才危險");
        }

        // ---- 🔴 path traversal ----

        [Test]
        public void Dot_Dot_Segments_Are_Rejected()
        {
            Assert.IsFalse(SafeRelPath.IsSafe("../evil.osu"));
            Assert.IsFalse(SafeRelPath.IsSafe("..\\evil.osu"));
            Assert.IsFalse(SafeRelPath.IsSafe("a/../../evil.osu"));
            Assert.IsFalse(SafeRelPath.IsSafe("a/.."));
            Assert.IsFalse(SafeRelPath.IsSafe(".."));
        }

        [Test]
        public void Single_Dot_Segments_Are_Rejected()
        {
            // 無害但無意義,而且會讓同一個檔案有多種寫法 → packId 可能不一致。
            Assert.IsFalse(SafeRelPath.IsSafe("./a.osu"));
            Assert.IsFalse(SafeRelPath.IsSafe("a/./b.osu"));
        }

        [Test]
        public void Absolute_Paths_Are_Rejected()
        {
            Assert.IsFalse(SafeRelPath.IsSafe("/etc/passwd"));
            Assert.IsFalse(SafeRelPath.IsSafe("\\Windows\\System32\\evil.dll"));
        }

        [Test]
        public void Drive_Prefixes_Are_Rejected()
        {
            Assert.IsFalse(SafeRelPath.IsSafe("C:\\Windows\\evil.dll"));
            Assert.IsFalse(SafeRelPath.IsSafe("C:evil.osu"), "沒有分隔符的 drive 相對路徑也算");
            Assert.IsFalse(SafeRelPath.IsSafe("z:/x"));
        }

        [Test]
        public void Unc_Paths_Are_Rejected()
        {
            Assert.IsFalse(SafeRelPath.IsSafe("\\\\server\\share\\evil.dll"));
            Assert.IsFalse(SafeRelPath.IsSafe("//server/share/evil.dll"));
        }

        [Test]
        public void Empty_Segments_Are_Rejected()
        {
            // "a//b" 在不同 OS 上的解讀不一致,而且會讓 packId 對同一份檔案不穩定。
            Assert.IsFalse(SafeRelPath.IsSafe("a//b.osu"));
            Assert.IsFalse(SafeRelPath.IsSafe("a/"));
            Assert.IsFalse(SafeRelPath.IsSafe(""));
            Assert.IsFalse(SafeRelPath.IsSafe(null));
        }

        // ---- Windows 的特殊行為 ----

        [Test]
        public void Trailing_Dots_And_Spaces_Are_Rejected()
        {
            // Windows 會默默把結尾的句點與空白 trim 掉 —— 也就是 "evil." 與 "evil" 會開到同一個檔。
            // 放行的話,同一份 manifest 在不同 OS 上會產生不同的檔案集合(Linux 上是兩個檔,
            // Windows 上是一個檔被覆寫兩次)。
            Assert.IsFalse(SafeRelPath.IsSafe("evil.osu."));
            Assert.IsFalse(SafeRelPath.IsSafe("evil.osu "));
            Assert.IsFalse(SafeRelPath.IsSafe("dir./a.osu"));
            Assert.IsFalse(SafeRelPath.IsSafe(" leading.osu"), "開頭空白同理");
        }

        [Test]
        public void Reserved_Device_Names_Are_Rejected()
        {
            // 在這些名字上開檔會變成操作裝置而不是檔案。
            Assert.IsFalse(SafeRelPath.IsSafe("CON"));
            Assert.IsFalse(SafeRelPath.IsSafe("nul"));
            Assert.IsFalse(SafeRelPath.IsSafe("COM1"));
            Assert.IsFalse(SafeRelPath.IsSafe("LPT9"));
            Assert.IsFalse(SafeRelPath.IsSafe("aux/a.osu"), "資料夾名也算");
        }

        [Test]
        public void Reserved_Names_With_Extensions_Are_Also_Rejected()
        {
            // 🔴 容易漏的一條:CON.txt 一樣被保留 —— 判斷要看第一個句點之前的部分,
            // 不是拿整個檔名去比對。
            Assert.IsFalse(SafeRelPath.IsSafe("CON.txt"));
            Assert.IsFalse(SafeRelPath.IsSafe("nul.osu"));
            Assert.IsFalse(SafeRelPath.IsSafe("com1.mp3"));

            Assert.IsTrue(SafeRelPath.IsSafe("console.osu"), "只是開頭像而已，不是保留名");
            Assert.IsTrue(SafeRelPath.IsSafe("nullify.osu"));
            Assert.IsTrue(SafeRelPath.IsSafe("com10.osu"), "只有 COM1..COM9 是保留的");
        }

        [Test]
        public void Illegal_Filename_Characters_Are_Rejected()
        {
            Assert.IsFalse(SafeRelPath.IsSafe("a.ogg:hidden"), "NTFS alternate data stream");
            Assert.IsFalse(SafeRelPath.IsSafe("a*.osu"));
            Assert.IsFalse(SafeRelPath.IsSafe("a?.osu"));
            Assert.IsFalse(SafeRelPath.IsSafe("a<b.osu"));
            Assert.IsFalse(SafeRelPath.IsSafe("a>b.osu"));
            Assert.IsFalse(SafeRelPath.IsSafe("a|b.osu"));
            Assert.IsFalse(SafeRelPath.IsSafe("a\"b.osu"));
        }

        [Test]
        public void Control_Characters_Are_Rejected()
        {
            Assert.IsFalse(SafeRelPath.IsSafe("a\u0001b.osu"));
            Assert.IsFalse(SafeRelPath.IsSafe("a\u0000b.osu"));
            Assert.IsFalse(SafeRelPath.IsSafe("a\u001Fb.osu"), "0x1F 是 manifest 的欄位分隔符,絕不能出現在路徑裡");
            Assert.IsFalse(SafeRelPath.IsSafe("a\u007Fb.osu"), "DEL");
            Assert.IsFalse(SafeRelPath.IsSafe("a\nb.osu"), "換行會破壞 manifest 的行結構");
        }

        // ---- 長度 ----

        [Test]
        public void Overlong_Paths_Are_Rejected()
        {
            Assert.IsFalse(SafeRelPath.IsSafe(new string('a', SafeRelPath.MaxLength + 1)));
        }

        [Test]
        public void Overlong_Segments_Are_Rejected()
        {
            Assert.IsFalse(SafeRelPath.IsSafe(new string('a', SafeRelPath.MaxSegmentLength + 1) + ".osu"));
        }

        /// <summary>
        /// 🔴 回歸:osu! 的檔名格式是「曲師 - 曲名 (製譜者) [難度名].osu」,破百是**常態**。
        /// 片段上限曾經是 100,於是這一份(實機抓到的,119~129 字)整首歌都動不了:
        /// 譜面被判 UnsafePath 排除在 pack 之外,而房主送 setSong 直接被 server 回
        /// badState「bad song ref」—— 畫面上只是「選了歌但按開始沒反應」。
        /// </summary>
        [Test]
        public void Real_Osu_Filenames_Over_100_Chars_Are_Accepted()
        {
            const string longest =
                "Jeff Williams & Casey Lee Williams - This Will Be the Day (James Landino's Magical Girl Remix) (Fullerene-) [Blocko's 7K NM+].osu";
            Assert.Greater(longest.Length, 100, "這條測試的前提就是它超過 100 字");

            string reason;
            Assert.IsTrue(SafeRelPath.IsSafe(longest, out reason), reason);
            Assert.AreEqual(PackFileVerdict.Include, SongPackFilter.Classify(longest, 53654),
                            "過得了 IsSafe 還要真的能跟著歌一起傳出去");
        }

        [Test]
        public void Reason_Explains_Which_Rule_Rejected_It()
        {
            // 有原因才有辦法回報給 host「哪個檔為什麼不能傳」，也讓測試失敗時看得出是哪條規則。
            string reason;
            Assert.IsFalse(SafeRelPath.IsSafe("../x", out reason));
            Assert.IsNotNull(reason);
            Assert.IsTrue(reason.Length > 0);

            Assert.IsTrue(SafeRelPath.IsSafe("ok.osu", out reason));
            Assert.IsNull(reason, "通過時不該有原因");
        }

        // ---- 正規化 ----

        [Test]
        public void Normalize_Lowercases_And_Unifies_Separators()
        {
            Assert.AreEqual("sb/overlay.png", SafeRelPath.Normalize("SB\\Overlay.PNG"));
            Assert.AreEqual("song.osu", SafeRelPath.Normalize("Song.osu"));
        }

        [Test]
        public void Normalize_Makes_Case_Only_Differences_Identical()
        {
            // 🔴 這就是為什麼要正規化:Windows 的檔案系統大小寫不敏感,同一個資料夾在兩台機器上
            // 可能是 BGM.ogg 與 bgm.ogg。不統一的話 packId 會不一樣 → 明明是同一首歌卻判定成
            // 缺歌並重新傳一次。
            Assert.AreEqual(SafeRelPath.Normalize("BGM.OGG"), SafeRelPath.Normalize("bgm.ogg"));
        }

        [Test]
        public void Normalize_Handles_Empty_Input()
        {
            Assert.AreEqual("", SafeRelPath.Normalize(null));
            Assert.AreEqual("", SafeRelPath.Normalize(""));
        }
    }
}
