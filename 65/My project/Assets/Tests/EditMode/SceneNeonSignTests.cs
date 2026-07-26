using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// SCN0001 新天地 兩面霓虹招牌的逐字閃爍狀態機。官方 Effect_Tick_004a63e0 + BlinkUpdate(700ms) /
    /// WipeUpdate(500ms)。Ghidra 幾乎把這條線上所有 this/參數都吃掉了(連 0x4ad920 那個每幀函式都沒生出來)，
    /// 常數與語意是逐指令反組譯來的。
    /// </summary>
    public class SceneNeonSignTests
    {
        [Test]
        public void Catalog_Has_Both_Signs_In_Reading_Order()
        {
            var signs = SceneNeonSignCatalog.ForFolder("SCN0001");
            Assert.AreEqual(2, signs.Count, "兩面招牌");
            Assert.AreEqual(8, signs[0].Length, "LA MAISON — 表 0x588240 共 8 對");
            Assert.AreEqual(9, signs[1].Length, "SN❄WFLAKE — 表 0x588280 共 9 對");
            // 表序 = 招牌上的閱讀順序，逐字掃照這個跑，不能重排。
            CollectionAssert.AreEqual(
                new[] { "l_.dds", "aa_.dds", "m_.dds", "a_.dds", "i_.dds", "s_.dds", "o_.dds", "n_.dds" },
                signs[0].LitDds);
            Assert.AreEqual("bt1_.dds", signs[1].LitDds[2], "第 3 格不是字母，是一顆雪花");
            // ★ 亮 = 帶底線那張(也就是 MSH 材質名);暗 = 去掉底線。做反的話會變成平常暗、閃才亮。
            CollectionAssert.AreEqual(
                new[] { "l.dds", "aa.dds", "m.dds", "a.dds", "i.dds", "s.dds", "o.dds", "n.dds" },
                signs[0].DarkDds);
            Assert.AreEqual("e1.dds", signs[1].DarkDds[8]);
            Assert.AreEqual(0, SceneNeonSignCatalog.ForFolder("SCN0002").Count, "別的場景沒有");
        }

        [Test]
        public void Blink_Mode_Is_All_On_Then_All_Off()
        {
            const int n = 8;
            Assert.AreEqual(2, SceneNeonSign.StepCount(SceneNeonSign.Mode.Blink, n), "亮一拍、暗一拍");
            Assert.AreEqual(700f, SceneNeonSign.BlinkMs, 1e-3f, "0x2bc");
            for (int i = 0; i < n; i++)
            {
                Assert.IsTrue(SceneNeonSign.IsLit(SceneNeonSign.Mode.Blink, 0, i, n), "第 0 拍全亮");
                Assert.IsFalse(SceneNeonSign.IsLit(SceneNeonSign.Mode.Blink, 1, i, n), "第 1 拍全暗");
            }
        }

        [Test]
        public void Wipe_Mode_Lights_Up_Then_Unlights_From_The_Far_End()
        {
            const int n = 8;
            Assert.AreEqual(500f, SceneNeonSign.WipeMs, 1e-3f, "0x1f4");
            Assert.AreEqual(2 * n + 1, SceneNeonSign.StepCount(SceneNeonSign.Mode.Wipe, n), "亮滿 N + 熄 N + 收尾 1");
            var W = SceneNeonSign.Mode.Wipe;
            // 起手全暗
            for (int i = 0; i < n; i++) Assert.IsFalse(SceneNeonSign.IsLit(W, 0, i, n));
            // 每拍多亮一個(表序)
            for (int i = 0; i < n; i++) Assert.AreEqual(i < 3, SceneNeonSign.IsLit(W, 3, i, n), "第 3 拍亮前 3 個");
            // 第 N 拍亮滿
            for (int i = 0; i < n; i++) Assert.IsTrue(SceneNeonSign.IsLit(W, n, i, n), "第 N 拍全亮");
            // ★ 亮滿之後是「從最後一個往回熄」，不是直接跳下一輪
            for (int i = 0; i < n; i++) Assert.AreEqual(i < n - 1, SceneNeonSign.IsLit(W, n + 1, i, n), "第 N+1 拍熄掉最後一個");
            for (int i = 0; i < n; i++) Assert.AreEqual(i < 2, SceneNeonSign.IsLit(W, 2 * n - 2, i, n));
            // 熄完
            for (int i = 0; i < n; i++) Assert.IsFalse(SceneNeonSign.IsLit(W, 2 * n, i, n), "第 2N 拍全暗");
        }

        [Test]
        public void Wipe_Never_Reports_A_Negative_Or_Overrun_Index()
        {
            const int n = 9;
            var W = SceneNeonSign.Mode.Wipe;
            // 收尾那一拍(step = 2N)與任何超出的 step 都必須是安全的全暗，不能算出負的 idx。
            for (int step = 0; step <= 2 * n + 5; step++)
                for (int i = 0; i < n; i++)
                {
                    bool lit = SceneNeonSign.IsLit(W, step, i, n);
                    if (step > 2 * n) Assert.IsFalse(lit, "超出一輪之後不該再有字亮著 (step " + step + ")");
                }
        }
    }
}
