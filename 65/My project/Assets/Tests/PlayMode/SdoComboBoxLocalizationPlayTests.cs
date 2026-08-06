using System.Collections;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.TestTools;
using Sdo.Localization;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>
    /// 下拉選單(選歌的模式/隊形、房間的掉落方式)要跟著語言換。
    ///
    /// 🔴 這幾塊面板都只建一次,而 OPTION 可以在遊戲中途換語言 —— 選項字若是「建版面當下解好的字串」,
    ///    整個畫面換完就它們沒換。<see cref="SdoComboBox"/> 收 optionKeys 之後由自己在 LanguageChanged
    ///    時重解(收合值、展開中的清單一起)。
    ///
    /// 🔴 為什麼在 PlayMode:靠的是 MonoBehaviour 的 OnEnable 訂閱事件,EditMode 不跑生命週期回呼。
    ///
    /// Run: -runTests -testPlatform PlayMode -testFilter Sdo.Tests.SdoComboBoxLocalizationPlayTests
    /// </summary>
    public class SdoComboBoxLocalizationPlayTests
    {
        private static readonly string[] Keys = { "songselect.mode_free", "songselect.mode_normal" };

        private GameObject _canvasGo;
        private RectTransform _root;
        private Language _saved;
        private Texture2D _tex;

        private static StringTable En() => StringTable.Parse(
            "{\"language\":\"en\",\"name\":\"English\",\"culture\":\"en-US\",\"entries\":[" +
            "{\"k\":\"songselect.mode_free\",\"v\":\"Free\"},{\"k\":\"songselect.mode_normal\",\"v\":\"Normal\"}]}");

        private static StringTable Tw() => StringTable.Parse(
            "{\"language\":\"zh-TW\",\"name\":\"繁中\",\"culture\":\"zh-TW\",\"entries\":[" +
            "{\"k\":\"songselect.mode_free\",\"v\":\"自由模式\"},{\"k\":\"songselect.mode_normal\",\"v\":\"普通模式\"}]}");

        [SetUp]
        public void SetUp()
        {
            _saved = LocalizationManager.Current;
            _canvasGo = new GameObject("ComboLocTestCanvas", typeof(RectTransform), typeof(Canvas));
            _root = (RectTransform)_canvasGo.transform;
            _root.sizeDelta = new Vector2(800f, 600f);
            LocalizationManager.LoadFromTables(Language.TraditionalChinese, Tw(), En());
        }

        [TearDown]
        public void TearDown()
        {
            if (_canvasGo != null) Object.DestroyImmediate(_canvasGo);
            if (_tex != null) Object.DestroyImmediate(_tex);
            LocalizationManager.Init(_saved);
        }

        /// <summary>文字值的下拉(隊形/掉落方式那一型)。</summary>
        private SdoComboBox TextCombo() => SdoComboBox.Create(
            _root, "formCombo", 0f, 0f, 56f, 22f, 60f, null, null, null,
            null, null, 0, Color.white, Color.white, _ => { }, optionKeys: Keys);

        /// <summary>值是官方中文圖的下拉(選歌「模式」那一型)。</summary>
        private SdoComboBox SpriteCombo()
        {
            _tex = new Texture2D(4, 4);
            var s = Sprite.Create(_tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f));
            return SdoComboBox.Create(
                _root, "modeCombo", 0f, 0f, 258f, 22f, 262f, null, null, null,
                null, new[] { s, s }, 0, Color.white, Color.white, _ => { },
                listAsText: true, optionKeys: Keys, chineseValueSprites: true);
        }

        private TMP_Text Find(string name)
        {
            foreach (var t in _root.GetComponentsInChildren<TMP_Text>(true))
                if (t.gameObject.name == name) return t;
            return null;
        }

        private static bool Shown(Component c) => c != null && c.gameObject.activeInHierarchy;

        [UnityTest]
        public IEnumerator Collapsed_Value_Follows_A_Language_Switch()
        {
            TextCombo();
            Assert.AreEqual("自由模式", Find("formCombo_val").text);

            LocalizationManager.LoadFromTables(Language.English, En(), En());
            yield return null;

            Assert.AreEqual("Free", Find("formCombo_val").text, "換語言後下拉的值沒跟著換");
        }

        [UnityTest]
        public IEnumerator Expanded_List_Rebuilds_In_The_New_Language()
        {
            var combo = TextCombo();
            combo.GetComponent<Button>().onClick.Invoke();   // 展開
            yield return null;
            Assert.IsTrue(combo.IsOpen);

            LocalizationManager.LoadFromTables(Language.English, En(), En());
            yield return null;

            Assert.IsTrue(combo.IsOpen, "換語言只重建清單,不該把它收起來");
            var rows = new System.Collections.Generic.List<string>();
            foreach (var t in _root.GetComponentsInChildren<TMP_Text>(true))
                if (t.gameObject.name == "t") rows.Add(t.text);
            CollectionAssert.AreEqual(new[] { "Free", "Normal" }, rows, "展開中的清單沒有換語言");
        }

        [UnityTest]
        public IEnumerator Catches_Up_When_The_Panel_Comes_Back()
        {
            // 房間那幾塊 UI 會整塊收起來(SetActive(false))—— 藏著的時候收不到 LanguageChanged,
            // 所以重新顯示時要自己補解一次。
            var combo = TextCombo();
            combo.gameObject.SetActive(false);
            LocalizationManager.LoadFromTables(Language.English, En(), En());
            yield return null;

            combo.gameObject.SetActive(true);
            yield return null;

            Assert.AreEqual("Free", Find("formCombo_val").text);
        }

        [UnityTest]
        public IEnumerator Chinese_Value_Art_Gives_Way_To_Text_In_Other_Languages()
        {
            // 模式的收合值是官方圖(LABEL_SDO,中文烘死)。中文語系用圖;英文/日文要退成翻譯過的文字,
            // 否則那一格永遠是中文。
            SpriteCombo();
            Image img = null;
            foreach (var i in _root.GetComponentsInChildren<Image>(true))
                if (i.gameObject.name == "modeCombo_val") img = i;
            var txt = Find("modeCombo_valtxt");
            Assert.IsNotNull(img); Assert.IsNotNull(txt);

            Assert.IsTrue(Shown(img), "中文語系該顯示官方值圖");
            Assert.IsFalse(Shown(txt));

            LocalizationManager.LoadFromTables(Language.English, En(), En());
            yield return null;

            Assert.IsFalse(Shown(img), "英文語系不該繼續掛中文值圖");
            Assert.IsTrue(Shown(txt));
            Assert.AreEqual("Free", txt.text);

            LocalizationManager.LoadFromTables(Language.TraditionalChinese, Tw(), En());
            yield return null;

            Assert.IsTrue(Shown(img), "切回中文,官方值圖要回來");
            Assert.IsFalse(Shown(txt));
        }
    }
}
