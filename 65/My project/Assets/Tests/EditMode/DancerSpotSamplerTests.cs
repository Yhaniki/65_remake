using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 房間裡其他舞者的站位取樣(座位 1..5)。
    ///
    /// 這一組守的是**「每台算出來一樣」**。那件事在實機上很難發現(要兩台並排比對站位),
    /// 但在測試裡是一行 assert —— 而且它壞掉的方式很隱蔽:只要有人把 System.Random 換成
    /// UnityEngine.Random(全域狀態,別的系統抽一次就漂開)、或把兩個 NextDouble 的順序換掉,
    /// 遊戲照跑、畫面照出,只是「你看他站沙發旁、他看自己站中間」。
    /// </summary>
    public class DancerSpotSamplerTests
    {
        private static readonly Vector2 Center = new Vector2(-25f, -75f);
        private const float Radius = 65f;
        private const float Spacing = 24f;
        private static readonly Vector2 Host = new Vector2(-100f, -26f);   // RoomLayout.HostSpawn 的 XZ

        private static Vector3[] Sample(int n, System.Func<float, float, bool> walkable = null)
            => DancerSpotSampler.Sample(n, Center, Radius, Spacing, Host, 0f, walkable);

        [Test]
        public void Same_Inputs_Give_The_Same_Points()
        {
            // 🔴 這就是「兩台看到的站位一樣」的前提。固定種子 + 不碰全域狀態 → 每次呼叫都一樣。
            var a = Sample(5);
            var b = Sample(5);
            Assert.AreEqual(5, a.Length);
            CollectionAssert.AreEqual(a, b, "同樣的輸入必須得到逐點相同的結果");
        }

        [Test]
        public void An_Unrelated_Unity_Random_Draw_Does_Not_Move_The_Points()
        {
            // UnityEngine.Random 是全域狀態:如果取樣改用它,別的系統(特效/場景)抽一次
            // 就會讓每台的序列漂開,而且完全沒有徵兆。這條測試把那個誘惑釘死。
            var before = Sample(5);
            Random.InitState(12345);
            float burn = 0f;
            for (int i = 0; i < 50; i++) burn += Random.value;   // 真的抽,把全域狀態推進
            Assert.Greater(burn, 0f);
            var after = Sample(5);
            CollectionAssert.AreEqual(before, after, "站位不可以被 UnityEngine.Random 的全域狀態影響");
        }

        [Test]
        public void Nobody_Stands_On_Top_Of_Anybody()
        {
            var pts = Sample(5);
            for (int i = 0; i < pts.Length; i++)
            {
                var vi = new Vector2(pts[i].x, pts[i].z);
                Assert.GreaterOrEqual((vi - Host).magnitude, Spacing - 0.001f,
                    "第 " + i + " 個點離房主太近");
                for (int j = i + 1; j < pts.Length; j++)
                {
                    var vj = new Vector2(pts[j].x, pts[j].z);
                    Assert.GreaterOrEqual((vi - vj).magnitude, Spacing - 0.001f,
                        "第 " + i + " 與第 " + j + " 個點疊在一起");
                }
            }
        }

        [Test]
        public void Points_Stay_Inside_The_Sampling_Disk_And_On_The_Floor()
        {
            foreach (var p in Sample(5))
            {
                Assert.LessOrEqual((new Vector2(p.x, p.z) - Center).magnitude, Radius + 0.001f);
                Assert.AreEqual(0f, p.y, 1e-4f, "站位的 Y 由呼叫端給(腳底貼地在別處處理)");
            }
        }

        [Test]
        public void The_Walkable_Test_Is_Honoured()
        {
            // 房間的可走判定是 MASK.MSK。這裡用一條假的「只有 x >= 0 能站」來確認它真的有被問。
            var pts = DancerSpotSampler.Sample(5, Center, Radius, Spacing, Host, 0f, (x, z) => x >= 0f);
            foreach (var p in pts) Assert.GreaterOrEqual(p.x, 0f, "取樣器沒有尊重可走判定");
        }

        [Test]
        public void Nowhere_To_Stand_Returns_Empty_Instead_Of_Hanging()
        {
            // 全部不可走 → 不能無限迴圈(有 MaxTries 上限),也不能回 null(呼叫端會 NRE)。
            var pts = DancerSpotSampler.Sample(5, Center, Radius, Spacing, Host, 0f, (x, z) => false);
            Assert.IsNotNull(pts);
            Assert.AreEqual(0, pts.Length);
        }

        [Test]
        public void Asking_For_Fewer_Points_Gives_The_Same_Prefix()
        {
            // 座位 1..5 拿 5 個點,而 dev 的填充路徑也拿 5 個 —— 兩邊必須是同一組點。
            // (舊寫法一邊拿 6 個用 index 1..5、一邊拿 5 個用 0..4,同一個房間有兩套站位。)
            var five = Sample(5);
            var three = Sample(3);
            Assert.AreEqual(3, three.Length);
            for (int i = 0; i < three.Length; i++)
                Assert.AreEqual(five[i], three[i], "少要幾個點時,前面幾個必須一樣");
        }

        [Test]
        public void An_Impossible_Spacing_Still_Fills_Every_Slot()
        {
            // 🔴 呼叫端是 `_remoteSpots[(seat - 1) % Length]`:長度不足時兩個座位會繞回同一個 index,
            // 那是**確定的完全重疊**(兩隻角色疊在一起)。所以間距湊不出來時寧可站近一點,
            // 也一定要湊滿 —— 這條測試就是守那件事。
            var pts = DancerSpotSampler.Sample(5, Center, Radius, 9999f, Host, 0f, null);
            Assert.AreEqual(5, pts.Length, "間距要求不可能滿足時,仍要湊滿(放寬間距而不是給少)");
            // 而且補上來的點彼此不是同一個座標(第二輪沿用同一條亂數序列,不是重跑一次)。
            for (int i = 0; i < pts.Length; i++)
                for (int j = i + 1; j < pts.Length; j++)
                    Assert.AreNotEqual(pts[i], pts[j], "補的點不可以是重複座標");
        }

        [Test]
        public void Relaxing_Only_Kicks_In_When_Needed()
        {
            // 一般情況(房間的實際參數)湊得滿,所以放寬那一輪根本不會跑 → 間距仍然嚴格成立。
            var pts = Sample(5);
            Assert.AreEqual(5, pts.Length);
            for (int i = 0; i < pts.Length; i++)
                for (int j = i + 1; j < pts.Length; j++)
                    Assert.GreaterOrEqual((new Vector2(pts[i].x, pts[i].z) - new Vector2(pts[j].x, pts[j].z)).magnitude,
                                          Spacing - 0.001f, "正常參數下不該退到放寬那一輪");
        }

        [Test]
        public void Zero_Or_Negative_Count_Is_Empty()
        {
            Assert.AreEqual(0, Sample(0).Length);
            Assert.AreEqual(0, Sample(-3).Length);
        }
    }
}
