using System.IO;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using Sdo.Localization;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>
    /// 輸入框的提示字要跟著語言走。
    ///
    /// 🔴 <c>UIKit.AddInputField</c> 收的是**解好的字串**,而版面只建一次(選歌畫面的搜尋框就是
    ///    <c>BuildUI</c> 建完就不再重建)—— OPTION 可以在遊戲中途換語言,於是整個畫面都換好了,
    ///    只有「輸入關鍵字並送出」那句提示停在舊語言。<c>AddLocInputField</c> 把 placeholder 綁到 key,
    ///    由 <see cref="LocalizedText"/> 在 LanguageChanged 時重解。
    /// </summary>
    public class LocalizedInputPlaceholderTests
    {
        private const string Key = "songselect.search";

        private GameObject _canvasGo;
        private Language _saved;

        private static StringTable En() => StringTable.Parse(
            "{\"language\":\"en\",\"name\":\"English\",\"culture\":\"en-US\",\"entries\":[" +
            "{\"k\":\"" + Key + "\",\"v\":\"Type a keyword and press Enter\"}]}");

        private static StringTable Tw() => StringTable.Parse(
            "{\"language\":\"zh-TW\",\"name\":\"繁中\",\"culture\":\"zh-TW\",\"entries\":[" +
            "{\"k\":\"" + Key + "\",\"v\":\"輸入關鍵字並送出\"}]}");

        [SetUp]
        public void SetUp()
        {
            _saved = LocalizationManager.Current;
            _canvasGo = new GameObject("LocInputTestCanvas", typeof(RectTransform), typeof(Canvas));
            ((RectTransform)_canvasGo.transform).sizeDelta = new Vector2(800f, 600f);
            LocalizationManager.LoadFromTables(Language.English, En(), En());
        }

        [TearDown]
        public void TearDown()
        {
            if (_canvasGo != null) Object.DestroyImmediate(_canvasGo);
            LocalizationManager.Init(_saved);   // 還原成出貨的語言表,別把狀態留給下一條測試
        }

        private TMP_InputField Field() =>
            UIKit.AddLocInputField(_canvasGo.transform, "SearchBox", Key, 13);

        private static TMP_Text Placeholder(TMP_InputField f)
        {
            Assert.IsInstanceOf<TMP_Text>(f.placeholder, "placeholder 應該是 TMP 文字");
            return (TMP_Text)f.placeholder;
        }

        [Test]
        public void Placeholder_Starts_In_The_Current_Language()
        {
            Assert.AreEqual("Type a keyword and press Enter", Placeholder(Field()).text);
        }

        [Test]
        public void Placeholder_Is_Bound_To_The_Key_Not_A_Baked_String()
        {
            // 🔴 「換語言時真的會跟著變」只能在 PlayMode 驗(EditMode 不呼叫 MonoBehaviour 的 OnEnable,
            //    LocalizedText 根本沒訂閱到 LanguageChanged)—— 見 Tests/PlayMode/LocalizedInputPlaceholderPlayTests。
            //    這裡守的是它的前提:提示字上面掛著綁對 key 的 LocalizedText。
            var lt = Placeholder(Field()).GetComponent<LocalizedText>();
            Assert.IsNotNull(lt, "placeholder 沒掛 LocalizedText → 換語言時會停在建版面當下的那個語言");
            Assert.AreEqual(Key, lt.key);
        }

        [Test]
        public void Shipped_Tables_All_Have_The_Search_Hint()
        {
            var dir = Path.Combine(Application.dataPath, "StreamingAssets", "Localization");
            foreach (var code in new[] { "en", "zh-TW", "zh-Hans", "ja" })
            {
                var path = Path.Combine(dir, code + ".json");
                Assert.IsTrue(File.Exists(path), path);
                var t = StringTable.Parse(File.ReadAllText(path));
                Assert.IsTrue(t.TryGet(Key, out var v) && !string.IsNullOrEmpty(v),
                    code + ".json 缺少 " + Key + "(跑 tools/build_localization.py)");
            }
        }
    }
}
