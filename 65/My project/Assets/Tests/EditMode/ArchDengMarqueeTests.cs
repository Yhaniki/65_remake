using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// SCN0006 遊樂場拱門 72 顆燈泡跑馬燈的狀態機。官方 Scene_UpdateSceneObjects_004baef0 case 6
    /// (0x4bafa3..0x4bb156，用 capstone 逐指令核過 —— Ghidra 把「設哪一顆燈」的 index 參數吃掉了)。
    /// 兩組共用一個 300 ms 計時器,A 組 56 顆 % 59、B 組 16 顆 % 19,語意都是
    ///   c=0 全暗 / c=k 前 k 顆亮 / c=N 全亮 / c=N+1 全暗 / c=N+2 全亮。
    /// </summary>
    public class ArchDengMarqueeTests
    {
        [Test]
        public void Groups_Match_The_Disassembled_Counts_And_Interval()
        {
            Assert.AreEqual(56, ArchDengMarquee.GroupACount, "拱門封閉迴路 56 顆(座標表前半)");
            Assert.AreEqual(16, ArchDengMarquee.GroupBCount, "上方的環 16 顆(座標表後半)");
            Assert.AreEqual(72, ArchDengMarquee.Bulbs, "單一連續 72×vec3 座標表");
            Assert.AreEqual(300f, ArchDengMarquee.IntervalMs, 1e-3f, "計時器參數 300");
        }

        [Test]
        public void Fill_Phase_Lights_Every_Bulb_Before_The_Head_And_Leaves_The_Tail_Lit()
        {
            const int n = 56;
            // c = 0 → 全暗
            for (int i = 0; i < n; i++) Assert.IsFalse(ArchDengMarquee.IsLit(0, i, n), "c=0 第 " + i + " 顆該暗");
            // c = 12 → 前 12 顆亮、其餘暗。★ 走過的燈保持亮著,不是單顆追逐光
            for (int i = 0; i < n; i++)
                Assert.AreEqual(i < 12, ArchDengMarquee.IsLit(12, i, n), "c=12 第 " + i + " 顆");
            // c = 55 → 只差最後一顆
            Assert.IsTrue(ArchDengMarquee.IsLit(55, 54, n));
            Assert.IsFalse(ArchDengMarquee.IsLit(55, 55, n));
            // c = N → 全亮(第一個迴圈跑滿,第二個被跳過)
            for (int i = 0; i < n; i++) Assert.IsTrue(ArchDengMarquee.IsLit(n, i, n), "c=N 第 " + i + " 顆該亮");
        }

        [Test]
        public void After_Filling_It_Blinks_Off_Then_On_Before_Restarting()
        {
            const int n = 56;
            for (int i = 0; i < n; i++)
            {
                Assert.IsFalse(ArchDengMarquee.IsLit(n + 1, i, n), "c=N+1 是全暗那一閃");
                Assert.IsTrue(ArchDengMarquee.IsLit(n + 2, i, n), "c=N+2 是全亮那一閃");
            }
        }

        [Test]
        public void The_Two_Groups_Cycle_At_Their_Own_Prime_Periods()
        {
            // 模數是 N+3(59 與 19),兩者互質 → 合成大週期 1121 tick ≈ 336 秒。
            const int modA = 56 + 3, modB = 16 + 3;
            Assert.AreEqual(59, modA);
            Assert.AreEqual(19, modB);
            // 同一顆燈在 tick 與 tick+modA 的狀態必須相同(A 組),但 tick+modB 不一定。
            for (int i = 0; i < 56; i += 7)
                Assert.AreEqual(ArchDengMarquee.IsLit(3, i, 56), ArchDengMarquee.IsLit(3 + 0, i, 56));
            // B 組在第 16 拍全亮、17 全暗、18 全亮,然後回到 0 全暗。
            for (int i = 0; i < 16; i++)
            {
                Assert.IsTrue(ArchDengMarquee.IsLit(16, i, 16));
                Assert.IsFalse(ArchDengMarquee.IsLit(17, i, 16));
                Assert.IsTrue(ArchDengMarquee.IsLit(18, i, 16));
                Assert.IsFalse(ArchDengMarquee.IsLit(0, i, 16));
            }
        }

        [Test]
        public void Negative_And_Large_Ticks_Fold_Safely()
        {
            // ApplyTick 用取模而不是累加,所以任何 tick(含負數/很大的數)都要落在合法相位。
            var go = new UnityEngine.GameObject("arch-test");
            try
            {
                var m = go.AddComponent<ArchDengMarquee>();
                m.SetFrames(null, null);
                Assert.IsFalse(m.HasFrames, "沒有貼圖時不該當成備妥");
                m.ApplyTick(-1);          // 不能丟例外
                m.ApplyTick(1000000);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }
    }
}
