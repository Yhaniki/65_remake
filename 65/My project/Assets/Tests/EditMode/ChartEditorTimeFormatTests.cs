using System.Globalization;
using System.Threading;
using NUnit.Framework;
using Sdo.Game;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// 編輯器頂欄的音樂時間顯示：一律「秒」，不再換算成 分:秒。
    /// 對拍/offset 都是以毫秒在算，心算 01:07.6 → 67.6 秒是多餘的一步；
    /// 播放位置給 6 位小數是為了跟 StepMania 的 CURRENT SECOND 逐位對照。
    /// </summary>
    public class ChartEditorTimeFormatTests
    {
        [Test]
        public void Fmt_IsPlainSeconds_WithMillisecondPrecision()
        {
            Assert.AreEqual("0.000", ChartEditorScreen.Fmt(0));
            Assert.AreEqual("67.616", ChartEditorScreen.Fmt(67616.375));   // 舊格式會顯示 01:07.6
            Assert.AreEqual("126.912", ChartEditorScreen.Fmt(126912));
            Assert.AreEqual("605.500", ChartEditorScreen.Fmt(605500));     // 超過 10 分鐘也只是變大的秒數
        }

        [Test]
        public void Fmt_SixDecimals_MatchesStepManiaCurrentSecond()
        {
            Assert.AreEqual("67.616375", ChartEditorScreen.Fmt(67616.375, 6));
            Assert.AreEqual("0.000000", ChartEditorScreen.Fmt(0, 6));
        }

        [Test]
        public void Fmt_NeverShowsMinutes()
        {
            foreach (double ms in new[] { 0.0, 999.0, 60000.0, 61000.0, 3600000.0 })
                StringAssert.DoesNotContain(":", ChartEditorScreen.Fmt(ms));
        }

        // 播放位置在 seek 之後可能短暫是小負數（時鐘含 count-in）→ 夾成 0，不要顯示 -0.001。
        [Test]
        public void Fmt_ClampsNegativeToZero()
        {
            Assert.AreEqual("0.000", ChartEditorScreen.Fmt(-1));
            Assert.AreEqual("0.000000", ChartEditorScreen.Fmt(-2500, 6));
        }

        // 頂欄顯示的是「小節內第幾拍」(1~4)，不是從曲首起算的絕對拍。120 BPM → 一拍 500ms。
        [Test]
        public void BeatInMeasure_WrapsToOneThroughFour()
        {
            var g = new BeatGrid(null, 120.0);
            Assert.AreEqual(1.0, ChartEditorScreen.BeatInMeasure(g, 0), 1e-6);
            Assert.AreEqual(1.5, ChartEditorScreen.BeatInMeasure(g, 250), 1e-6);
            Assert.AreEqual(2.0, ChartEditorScreen.BeatInMeasure(g, 500), 1e-6);
            Assert.AreEqual(4.0, ChartEditorScreen.BeatInMeasure(g, 1500), 1e-6);
            Assert.AreEqual(1.0, ChartEditorScreen.BeatInMeasure(g, 2000), 1e-6);   // 下一小節第一拍
        }

        // 板子往下讓出頂欄：頂欄是螢幕像素，板子是 800×600 design px，比例隨視窗大小變 →
        // 換算一定要走相機的 pixelHeight，不能寫死一個 design 位移。
        [Test]
        public void EditorViewShift_ConvertsScreenPixelsToDesignPixels()
        {
            const float pad = ScreenGameplay.EditorViewShiftPad;
            // 600px 高的視窗：1 design px = 1 螢幕 px
            Assert.AreEqual(74f + pad, ScreenGameplay.EditorViewShiftFor(74f, 300f, 600f, +1), 1e-3f);
            // 1200px 高：同一條頂欄只值一半的 design px
            Assert.AreEqual(37f + pad, ScreenGameplay.EditorViewShiftFor(74f, 300f, 1200f, +1), 1e-3f);
        }

        // 向下捲時受擊線在板底（design 530），頂欄擋不到它 —— 這時再往下推只會把它推出畫面下緣。
        [Test]
        public void EditorViewShift_IsZero_WhenScrollingDown()
        {
            Assert.AreEqual(0f, ScreenGameplay.EditorViewShiftFor(74f, 300f, 600f, -1), 1e-3f);
        }

        [Test]
        public void EditorViewShift_IsClamped_SoTheBoardCantBePushedOffScreen()
        {
            Assert.AreEqual(ScreenGameplay.EditorViewShiftMax, ScreenGameplay.EditorViewShiftFor(9999f, 300f, 600f, +1), 1e-3f);
            Assert.AreEqual(ScreenGameplay.EditorViewShiftPad, ScreenGameplay.EditorViewShiftFor(0f, 300f, 600f, +1), 1e-3f);
            Assert.AreEqual(0f, ScreenGameplay.EditorViewShiftFor(-50f, 300f, 600f, +1), 1e-3f);   // 負值（不該發生）→ 完全不推
        }

        // 小數點固定用「.」：跑在把逗號當小數點的系統語系時，這一欄不能變成 67,616。
        [Test]
        public void Fmt_UsesDotDecimalSeparator_RegardlessOfCulture()
        {
            var prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.AreEqual("67.616", ChartEditorScreen.Fmt(67616));
            }
            finally { Thread.CurrentThread.CurrentCulture = prev; }
        }
    }
}
