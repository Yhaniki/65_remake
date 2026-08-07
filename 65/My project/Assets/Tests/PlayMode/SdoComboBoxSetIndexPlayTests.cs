using NUnit.Framework;
using TMPro;
using UnityEngine;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>
    /// 從外部改下拉的值(<see cref="SdoComboBox.SetIndexWithoutNotify"/>)。
    ///
    /// 🔴 為什麼需要它:選歌面板只建一次版面,那幾個下拉的值是建版面當下從 session 讀的。
    ///    線上非房主每份房間快照都會把房間設定收進 session —— 不從外部把下拉拉回來的話,
    ///    「房主設 ShowTime 再把房主讓給我,我打開選歌選單還是自由模式」。
    ///
    /// 🔴 **不能觸發 onPick**:選歌那三個下拉的 onPick 會寫 session、存 config.ini、推給 server。
    ///    這裡是拿 session 對齊 UI,回頭再寫一次只是白工(而且場景那個會多送一次房間設定)。
    ///
    /// 在 PlayMode 是因為要真的建 MonoBehaviour + TMP 文字物件。
    ///
    /// Run: -runTests -testPlatform PlayMode -testFilter Sdo.Tests.SdoComboBoxSetIndexPlayTests
    /// </summary>
    public class SdoComboBoxSetIndexPlayTests
    {
        private static readonly string[] Options = { "自由模式", "普通模式", "SHOWTIME" };

        private GameObject _canvasGo;
        private RectTransform _root;

        [SetUp]
        public void SetUp()
        {
            _canvasGo = new GameObject("ComboSetIndexTestCanvas", typeof(RectTransform), typeof(Canvas));
            _root = (RectTransform)_canvasGo.transform;
            _root.sizeDelta = new Vector2(800f, 600f);
        }

        [TearDown]
        public void TearDown()
        {
            if (_canvasGo != null) Object.DestroyImmediate(_canvasGo);
        }

        private SdoComboBox Combo(int start, System.Action<int> onPick) => SdoComboBox.Create(
            _root, "modeCombo", 0f, 0f, 258f, 22f, 262f, null, null, null,
            Options, null, start, Color.white, Color.white, onPick);

        private string ValueText()
        {
            foreach (var t in _root.GetComponentsInChildren<TMP_Text>(true))
                if (t.gameObject.name == "modeCombo_val") return t.text;
            return null;
        }

        [Test]
        public void Setting_The_Index_Updates_The_Collapsed_Value()
        {
            var combo = Combo(0, _ => { });
            Assert.AreEqual("自由模式", ValueText(), "前提:一開始停在第 0 個");

            combo.SetIndexWithoutNotify(2);

            Assert.AreEqual(2, combo.Index);
            Assert.AreEqual("SHOWTIME", ValueText(), "🔴 使用者回報的就是這個:值改了,那格的字沒跟著換");
        }

        [Test]
        public void Setting_The_Index_Does_Not_Fire_OnPick()
        {
            int picks = 0;
            var combo = Combo(0, _ => picks++);

            combo.SetIndexWithoutNotify(1);
            combo.SetIndexWithoutNotify(0);

            Assert.AreEqual(0, picks, "onPick 會寫 session + 存 config.ini + 推給 server,對齊 UI 不該碰它");
        }

        [Test]
        public void Out_Of_Range_Is_Clamped_Not_Thrown()
        {
            // session 的值可能來自 config.ini 或協定 —— 壞值要夾回來,不是丟例外把面板整個開不起來。
            var combo = Combo(0, _ => { });

            combo.SetIndexWithoutNotify(99);
            Assert.AreEqual(Options.Length - 1, combo.Index);

            combo.SetIndexWithoutNotify(-5);
            Assert.AreEqual(0, combo.Index);
        }
    }
}
