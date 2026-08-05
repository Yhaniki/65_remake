using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using NUnit.Framework;
using Sdo.Settings;

namespace Sdo.Tests
{
    /// <summary>LocalPrefs —— PlayerPrefs 的替代品（PlayerPrefs 在 Windows 上寫的是登錄檔，
    /// 那在 build 資料夾外面）。這裡釘死的是格式（UTF-8 無 BOM + LF）與 culture 無關的數值往返。</summary>
    public class LocalPrefsTests
    {
        private string _dir;
        private string _file;
        private CultureInfo _culture;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "sdo_prefs_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _file = Path.Combine(_dir, "prefs.ini");
            _culture = Thread.CurrentThread.CurrentCulture;
            LocalPrefs.OverridePath(_file);
        }

        [TearDown]
        public void TearDown()
        {
            Thread.CurrentThread.CurrentCulture = _culture;
            LocalPrefs.OverridePath(null);
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        // ---------------- 基本往返 ----------------

        [Test]
        public void Defaults_WhenKeyMissing()
        {
            Assert.AreEqual(3.5f, LocalPrefs.GetFloat("nope", 3.5f));
            Assert.AreEqual(7, LocalPrefs.GetInt("nope", 7));
            Assert.AreEqual("d", LocalPrefs.GetString("nope", "d"));
            Assert.IsTrue(LocalPrefs.GetBool("nope", true));
            Assert.IsFalse(LocalPrefs.HasKey("nope"));
        }

        [Test]
        public void SetThenGet_RoundTripsInMemory()
        {
            LocalPrefs.SetFloat("lobby.avatar.scale", 1.048f);
            LocalPrefs.SetInt("editor.diff", 2);
            LocalPrefs.SetString("editor.scope", "All");
            LocalPrefs.SetBool("flag", true);

            Assert.AreEqual(1.048f, LocalPrefs.GetFloat("lobby.avatar.scale", 0f), 1e-6f);
            Assert.AreEqual(2, LocalPrefs.GetInt("editor.diff", 0));
            Assert.AreEqual("All", LocalPrefs.GetString("editor.scope", ""));
            Assert.IsTrue(LocalPrefs.GetBool("flag", false));
        }

        [Test]
        public void SurvivesSaveAndReload()
        {
            LocalPrefs.SetFloat("lobby.avatar.x", -12.25f);
            LocalPrefs.SetString("editor.lastGn", "00123.gn");
            LocalPrefs.Save();
            LocalPrefs.Reload();

            Assert.AreEqual(-12.25f, LocalPrefs.GetFloat("lobby.avatar.x", 0f), 1e-6f);
            Assert.AreEqual("00123.gn", LocalPrefs.GetString("editor.lastGn", ""));
        }

        [Test]
        public void DeleteKey_FallsBackToDefault()
        {
            LocalPrefs.SetFloat("k", 9f);
            LocalPrefs.DeleteKey("k");
            LocalPrefs.Save();
            LocalPrefs.Reload();

            Assert.IsFalse(LocalPrefs.HasKey("k"));
            Assert.AreEqual(1f, LocalPrefs.GetFloat("k", 1f));   // AvatarTuner 的「重設」就靠這個回到程式碼裡的值
        }

        // ---------------- 檔案格式 ----------------

        [Test]
        public void File_IsUtf8WithoutBom_AndLfOnly()
        {
            // BOM + CRLF 讓設定默默失效已經踩過一次（config.ini）—— 這裡釘死。
            LocalPrefs.SetString("a", "1");
            LocalPrefs.SetString("b", "2");
            LocalPrefs.Save();

            var bytes = File.ReadAllBytes(_file);
            Assert.IsFalse(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "不該有 BOM");
            CollectionAssert.DoesNotContain(bytes, (byte)0x0D, "不該有 CR");
        }

        [Test]
        public void File_IsSortedForReadableDiffs()
        {
            LocalPrefs.SetString("zzz", "1");
            LocalPrefs.SetString("aaa", "2");
            LocalPrefs.Save();

            var lines = File.ReadAllLines(_file);
            int ia = Array.FindIndex(lines, l => l.StartsWith("aaa=", StringComparison.Ordinal));
            int iz = Array.FindIndex(lines, l => l.StartsWith("zzz=", StringComparison.Ordinal));
            Assert.Greater(ia, -1);
            Assert.Less(ia, iz, "鍵要排序過，diff 才看得懂");
        }

        [Test]
        public void ReadsCommentsAndBlankLinesWithoutChoking()
        {
            File.WriteAllText(_file,
                "# comment\n; another\n\nlobby.avatar.y = 42.5 \nbroken-no-equals\n=novalue\n",
                new UTF8Encoding(false));
            LocalPrefs.Reload();

            Assert.AreEqual(42.5f, LocalPrefs.GetFloat("lobby.avatar.y", 0f), 1e-6f);   // 鍵與值都要 trim
            Assert.IsFalse(LocalPrefs.HasKey("broken-no-equals"));
            Assert.IsFalse(LocalPrefs.HasKey(""));
        }

        [Test]
        public void MissingFile_IsEmptyNotAnError()
        {
            LocalPrefs.Reload();
            Assert.IsFalse(LocalPrefs.HasKey("anything"));
            Assert.AreEqual(5, LocalPrefs.GetInt("anything", 5));
        }

        // ---------------- culture ----------------

        [Test]
        public void Floats_AreCultureInvariant()
        {
            // 逗號當小數點的地區設定下，1.25 存進去會變成 "1,25"，讀回來就成了 125 —— AvatarTuner 的
            // 縮放倍率被這樣一乘，角色會直接飛出畫面。所以讀寫一律 InvariantCulture。
            LocalPrefs.SetFloat("scale", 1.25f);
            LocalPrefs.Save();

            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            LocalPrefs.Reload();
            Assert.AreEqual(1.25f, LocalPrefs.GetFloat("scale", 0f), 1e-6f);

            LocalPrefs.SetFloat("scale2", 2.5f);
            LocalPrefs.Save();
            StringAssert.Contains("scale2=2.5", File.ReadAllText(_file));
        }

        // ---------------- 結構防呆 ----------------

        [Test]
        public void ValuesWithNewlinesCannotSplitAnEntry()
        {
            LocalPrefs.SetString("k", "line1\nline2=evil");
            LocalPrefs.Save();
            LocalPrefs.Reload();

            Assert.IsFalse(LocalPrefs.HasKey("line2"), "值裡的換行不該長出第二筆");
            Assert.AreEqual("line1 line2=evil", LocalPrefs.GetString("k", ""));
        }

        [Test]
        public void KeysWithEqualsOrNewlineAreRejected()
        {
            LocalPrefs.SetString("bad=key", "v");
            LocalPrefs.SetString("bad\nkey", "v");
            Assert.IsFalse(LocalPrefs.HasKey("bad=key"));
            Assert.IsFalse(LocalPrefs.HasKey("bad\nkey"));
        }

        [Test]
        public void SaveIsNoOpWhenNothingChanged()
        {
            LocalPrefs.SetString("k", "v");
            LocalPrefs.Save();
            var first = File.GetLastWriteTimeUtc(_file);

            LocalPrefs.SetString("k", "v");   // 同值 → 不該標髒
            LocalPrefs.Save();

            Assert.AreEqual(first, File.GetLastWriteTimeUtc(_file));
        }
    }
}
