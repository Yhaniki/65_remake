using NUnit.Framework;
using Sdo.Game;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>
    /// SCN0010 花車街景橫向捲動。官方 StageScene_UpdateScrollPair_004b40e0(我自己反組譯 0x4b40e0 逐行核過,
    /// 因為 Ghidra 把物件寫入的 this 掉了):每 30 ms 兩個累加器各 += −1.0,**<= −2168 就重設成 +2168**,
    /// 分別寫進 objects[0]/[1] 的 X,y/z 每 tick 都寫 0。
    /// 這裡測的是 tick → 座標的純函式,所以不需要真的等 30 ms。
    /// </summary>
    public class MapobjPositionScrollTests
    {
        private static MapobjPositionScroll NewScroll(out GameObject go, out Transform[] t)
        {
            var s = SceneMapobjPositionScrollCatalog.Find("SCN0010", "HOUSE");
            Assert.IsNotNull(s, "SCN0010 HOUSE 的捲動設定不見了");
            go = new GameObject("posscroll-test");
            t = new Transform[s.Start.Length];
            for (int i = 0; i < t.Length; i++) t[i] = new GameObject("h" + i).transform;
            var c = go.AddComponent<MapobjPositionScroll>();
            c.Init(t, s.Start, s.Axis, s.Step, s.TickMs, s.WrapAt, s.WrapTo);
            return c;
        }

        private static void Cleanup(GameObject go, Transform[] t)
        {
            foreach (var x in t) if (x != null) Object.DestroyImmediate(x.gameObject);
            if (go != null) Object.DestroyImmediate(go);
        }

        [Test]
        public void Catalog_Scn0010_House_Matches_The_Disassembled_Constants()
        {
            var s = SceneMapobjPositionScrollCatalog.Find("SCN0010", "HOUSE");
            Assert.AreEqual(-1f, s.Step, 1e-6f, "每 tick 位移 = [0x589060]");
            Assert.AreEqual(30f, s.TickMs, 1e-6f, "計時器 0x1e = 30 ms");
            Assert.AreEqual(-2168f, s.WrapAt, 1e-3f, "fcomp [0x558770]");
            Assert.AreEqual(2168f, s.WrapTo, 1e-3f, "重設值 0x45078000");
            Assert.AreEqual(Vector3.right, s.Axis, "官方只寫 X,y/z 每 tick 歸 0");
            Assert.AreEqual(2, s.Start.Length, "兩棟");
            Assert.AreEqual(0f, s.Start[0], 1e-6f, "objects[0] 的累加器在 .bss,初值 0");
            Assert.AreEqual(2168f, s.Start[1], 1e-6f, "objects[1] 的累加器在 .data,初值 2168");
            Assert.AreEqual(-33.3333f, s.PerSecond, 1e-3f);
            Assert.AreEqual(130.08f, s.LapSeconds, 1e-2f, "4336 單位 / 33.333 = 130.08 秒");
            // 沒有其他場景用這個機制 —— 別讓它外溢。
            Assert.IsNull(SceneMapobjPositionScrollCatalog.Find("SCN0010", "MAO"));
            Assert.IsNull(SceneMapobjPositionScrollCatalog.Find("SCN0004", "HOUSE"));
        }

        [Test]
        public void Coords_Step_Down_One_Unit_Per_Tick_And_Snap_At_The_Boundary()
        {
            var c = NewScroll(out var go, out var t);
            try
            {
                Assert.AreEqual(0f, c.CoordAt(0, 0), 1e-3f);
                Assert.AreEqual(2168f, c.CoordAt(1, 0), 1e-3f);
                Assert.AreEqual(-1f, c.CoordAt(0, 1), 1e-3f, "每 tick 走 −1");
                Assert.AreEqual(-100f, c.CoordAt(0, 100), 1e-3f);
                // 第 0 棟從 0 走到 −2168 要 2168 tick;那一刻(<= 邊界)就跳回 +2168。
                Assert.AreEqual(-2167f, c.CoordAt(0, 2167), 1e-3f);
                Assert.AreEqual(2168f, c.CoordAt(0, 2168), 1e-3f, "官方是 <= 就重設,不是 <");
                Assert.AreEqual(2167f, c.CoordAt(0, 2169), 1e-3f);
                // 完整一圈 4336 tick 回到起點。
                Assert.AreEqual(0f, c.CoordAt(0, 4336), 1e-3f);
                Assert.AreEqual(2168f, c.CoordAt(1, 4336), 1e-3f);
            }
            finally { Cleanup(go, t); }
        }

        [Test]
        public void The_Two_Houses_Stay_Exactly_Half_A_Lap_Apart_So_There_Is_Never_A_Gap()
        {
            var c = NewScroll(out var go, out var t);
            try
            {
                // 兩棟永遠相距 2168(= 4336 跨度的一半),否則街景會出現空隙。逐 tick 抽樣驗證。
                for (int tick = 0; tick <= 4336; tick += 271)
                {
                    float a = c.CoordAt(0, tick), b = c.CoordAt(1, tick);
                    float gap = Mathf.Abs(Mathf.Repeat(b - a, 4336f));
                    Assert.AreEqual(2168f, gap, 1e-2f, "tick " + tick + " 兩棟間距跑掉了");
                }
            }
            finally { Cleanup(go, t); }
        }

        [Test]
        public void Apply_Writes_X_Only_And_Zeroes_Y_And_Z()
        {
            var c = NewScroll(out var go, out var t);
            try
            {
                t[0].position = new Vector3(999f, 55f, -77f);   // 先弄髒,確認每 tick 真的重寫
                c.Apply(500);
                Assert.AreEqual(-500f, t[0].position.x, 1e-3f);
                Assert.AreEqual(0f, t[0].position.y, 1e-4f, "官方每 tick 把 y 寫 0");
                Assert.AreEqual(0f, t[0].position.z, 1e-4f, "官方每 tick 把 z 寫 0");
                Assert.AreEqual(1668f, t[1].position.x, 1e-3f);
            }
            finally { Cleanup(go, t); }
        }
    }
}
