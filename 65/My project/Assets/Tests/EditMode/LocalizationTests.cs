using NUnit.Framework;
using Sdo.Localization;

namespace Sdo.Tests
{
    public class LocalizationTests
    {
        private static StringTable En() => StringTable.Parse(
            "{\"language\":\"en\",\"name\":\"English\",\"culture\":\"en-US\",\"entries\":[" +
            "{\"k\":\"a\",\"v\":\"Hello\"},{\"k\":\"bpm\",\"v\":\"BPM {0}\"}]}");

        private static StringTable Tw() => StringTable.Parse(
            "{\"language\":\"zh-TW\",\"name\":\"繁中\",\"culture\":\"zh-TW\",\"entries\":[" +
            "{\"k\":\"a\",\"v\":\"你好\"}]}");

        [Test]
        public void StringTable_Parses_Entries_And_Meta()
        {
            var t = En();
            Assert.AreEqual("en", t.LanguageCode);
            Assert.AreEqual("en-US", t.Culture);
            Assert.IsTrue(t.TryGet("a", out var v));
            Assert.AreEqual("Hello", v);
        }

        [Test]
        public void Get_Returns_Value_For_Known_Key()
        {
            LocalizationManager.LoadFromTables(Language.English, En(), En());
            Assert.AreEqual("Hello", LocalizationManager.Get("a"));
        }

        [Test]
        public void Get_Missing_Key_Returns_Bracketed()
        {
            LocalizationManager.LoadFromTables(Language.English, En(), En());
            Assert.AreEqual("[zzz]", LocalizationManager.Get("zzz"));
        }

        [Test]
        public void Get_Interpolates_Placeholder()
        {
            LocalizationManager.LoadFromTables(Language.English, En(), En());
            Assert.AreEqual("BPM 128", LocalizationManager.Get("bpm", 128));
        }

        [Test]
        public void Falls_Back_To_English_When_Missing_In_Current()
        {
            LocalizationManager.LoadFromTables(Language.TraditionalChinese, Tw(), En());
            Assert.AreEqual("你好", LocalizationManager.Get("a"));      // present in current
            Assert.AreEqual("BPM 128", LocalizationManager.Get("bpm", 128)); // only in fallback
        }

        [Test]
        public void LanguageChanged_Fires_On_Load()
        {
            int n = 0;
            void H() => n++;
            LocalizationManager.LanguageChanged += H;
            LocalizationManager.LoadFromTables(Language.English, En(), En());
            LocalizationManager.LanguageChanged -= H;
            Assert.GreaterOrEqual(n, 1);
        }

        [Test]
        public void LanguageInfo_Code_And_FromCode_RoundTrip()
        {
            Assert.AreEqual(Language.TraditionalChinese, LanguageInfo.FromCode("zh-TW"));
            Assert.AreEqual(Language.SimplifiedChinese, LanguageInfo.FromCode("zh-Hans"));
            Assert.AreEqual(Language.Japanese, LanguageInfo.FromCode("ja"));
            Assert.AreEqual("ja", LanguageInfo.Code(Language.Japanese));
            Assert.AreEqual("zh-TW", LanguageInfo.Code(Language.TraditionalChinese));
        }
    }
}
