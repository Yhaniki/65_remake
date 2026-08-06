using System.Collections;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.TestTools;
using Sdo.Localization;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>
    /// 輸入框的提示字要跟著語言換 —— 選歌畫面搜尋框那句「輸入關鍵字並送出」。
    ///
    /// 🔴 為什麼在 PlayMode:靠的是 <see cref="LocalizedText"/> 在 <c>OnEnable</c> 訂閱 LanguageChanged,
    ///    而 EditMode 根本不呼叫 MonoBehaviour 的生命週期回呼 —— 同一條測試放 EditMode 會是假紅。
    ///    EditMode 那半(提示字綁的是 key、四份語言表都有這個 key)在
    ///    Tests/EditMode/LocalizedInputPlaceholderTests。
    ///
    /// Run: -runTests -testPlatform PlayMode -testFilter Sdo.Tests.LocalizedInputPlaceholderPlayTests
    /// </summary>
    public class LocalizedInputPlaceholderPlayTests
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
            LocalizationManager.Init(_saved);   // 還原出貨的語言表,別把狀態留給同場次的其它測試
        }

        private TMP_Text Placeholder()
        {
            var f = UIKit.AddLocInputField(_canvasGo.transform, "SearchBox", Key, 13);
            Assert.IsInstanceOf<TMP_Text>(f.placeholder, "placeholder 應該是 TMP 文字");
            return (TMP_Text)f.placeholder;
        }

        [UnityTest]
        public IEnumerator Placeholder_Follows_A_Language_Switch()
        {
            var ph = Placeholder();
            Assert.AreEqual("Type a keyword and press Enter", ph.text);

            LocalizationManager.LoadFromTables(Language.TraditionalChinese, Tw(), En());
            yield return null;

            Assert.AreEqual("輸入關鍵字並送出", ph.text,
                "換語言後提示字沒跟著換 —— 這正是綁 key 而不是綁解好的字串的理由");
        }

        [UnityTest]
        public IEnumerator Placeholder_Catches_Up_When_Shown_Again()
        {
            // 選歌畫面 focus 時把 placeholder 關掉、失焦(欄位還空著)再開回來。
            // 關著的那段期間換的語言,要在重新顯示時補上(LocalizedText 在 OnEnable 再解一次)。
            var ph = Placeholder();
            ph.gameObject.SetActive(false);
            LocalizationManager.LoadFromTables(Language.TraditionalChinese, Tw(), En());
            yield return null;

            ph.gameObject.SetActive(true);
            yield return null;

            Assert.AreEqual("輸入關鍵字並送出", ph.text);
        }
    }
}
