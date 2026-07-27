using NUnit.Framework;
using Sdo.Net;

namespace Sdo.Tests
{
    /// <summary>
    /// 房間裡的走路同步 —— 送出節流與位置消毒。
    ///
    /// 這兩塊是整個走路同步裡**最容易寫錯又最難用眼睛驗**的部分:
    /// 少送一筆「停下」對方會看到一個原地跑步的人;多送就是每幀灌 60 個封包;
    /// 而一個 NaN 位置會污染整棵 avatar hierarchy,連房間畫面都會變黑
    /// (症狀看起來像「渲染壞了」,完全指不到「某人送了壞位置」這個原因)。
    /// </summary>
    public class RoomMoveTests
    {
        // ==================== 送出節流 ====================

        [Test]
        public void First_Report_Is_Always_Sent()
        {
            // 第一筆是「我在這裡」的宣告。不送的話別人只能把我放在座位算出來的 fallback 點,
            // 而那個點與我實際站的位置不一樣 → 同一間房每台看到的站位都不同。
            var t = new MoveThrottle();
            Assert.IsTrue(t.ShouldSend(10f, 20f, 0f, walking: false, nowMs: 0));
        }

        [Test]
        public void Standing_Still_Never_Sends()
        {
            // 站著不動的人不需要每 100ms 提醒別人他還在那裡。
            var t = new MoveThrottle();
            Assert.IsTrue(t.ShouldSend(10f, 20f, 0f, false, 0));      // 第一筆
            for (long ms = 100; ms <= 5000; ms += 100)
                Assert.IsFalse(t.ShouldSend(10f, 20f, 0f, false, ms), "站著不動不該送(t=" + ms + ")");
        }

        [Test]
        public void Starting_And_Stopping_Send_Immediately()
        {
            // 這兩個瞬間是別人看得出來的:走路 clip 的開始與結束。晚一格都看得到。
            var t = new MoveThrottle();
            Assert.IsTrue(t.ShouldSend(0f, 0f, 0f, false, 0));
            Assert.IsTrue(t.ShouldSend(1f, 0f, 0f, true, 10), "開始走 → 立刻送");
            Assert.IsTrue(t.ShouldSend(5f, 0f, 0f, false, 20), "停下 → 立刻送(不能等下一個間隔)");
        }

        [Test]
        public void Turning_Sends_Immediately()
        {
            var t = new MoveThrottle();
            Assert.IsTrue(t.ShouldSend(0f, 0f, 0f, true, 0));
            Assert.IsFalse(t.ShouldSend(1f, 0f, 0f, true, 30), "同方向、還沒到間隔 → 不送");
            Assert.IsTrue(t.ShouldSend(1f, 0f, 90f, true, 40), "轉向 → 立刻送");
        }

        [Test]
        public void Walking_Sends_At_The_Configured_Interval()
        {
            var t = new MoveThrottle();
            Assert.IsTrue(t.ShouldSend(0f, 0f, 0f, true, 0));
            int interval = NetLimits.ClientMoveIntervalMs;

            Assert.IsFalse(t.ShouldSend(1f, 0f, 0f, true, interval - 1));
            Assert.IsTrue(t.ShouldSend(2f, 0f, 0f, true, interval));
            Assert.IsFalse(t.ShouldSend(3f, 0f, 0f, true, interval + 1));
            Assert.IsTrue(t.ShouldSend(4f, 0f, 0f, true, interval * 2));
        }

        [Test]
        public void Walking_Into_A_Wall_Still_Reports()
        {
            // 撞牆時位置不會變,但**還在走**。不送的話對方的 RemoteWalkTimeout 會把他畫成停下,
            // 而他明明還在推著牆走 —— 那是使用者看得出來的不一致。
            var t = new MoveThrottle();
            Assert.IsTrue(t.ShouldSend(0f, 0f, 0f, true, 0));
            Assert.IsTrue(t.ShouldSend(0f, 0f, 0f, true, NetLimits.ClientMoveIntervalMs),
                "位置沒動但還在走 → 照送");
        }

        [Test]
        public void Reset_Makes_The_Next_Report_Unconditional()
        {
            // 離房再進房 / 重連:一定要重送一次「我在這裡」。
            var t = new MoveThrottle();
            Assert.IsTrue(t.ShouldSend(0f, 0f, 0f, false, 0));
            Assert.IsFalse(t.ShouldSend(0f, 0f, 0f, false, 500));
            t.Reset();
            Assert.IsTrue(t.ShouldSend(0f, 0f, 0f, false, 600));
        }

        // ==================== 位置消毒 ====================

        private static object Node(string json)
        {
            object n;
            Assert.IsTrue(NetJson.TryParse(json, out n), "測試用 JSON 應合法:" + json);
            return n;
        }

        [Test]
        public void Decode_Keeps_A_Normal_Position()
        {
            var r = NetMoveRow.Decode(Node("{\"userId\":7,\"x\":-100.5,\"z\":-26.25,\"f\":180,\"w\":true}"));
            Assert.AreEqual(7, r.UserId);
            Assert.AreEqual(-100.5f, r.X, 1e-3f);
            Assert.AreEqual(-26.25f, r.Z, 1e-3f);
            Assert.AreEqual(180f, r.Facing, 1e-3f);
            Assert.IsTrue(r.Walking);
        }

        [Test]
        public void Decode_Clamps_To_The_Room_Walk_Box()
        {
            // 被改過的 client 可以送任意數字。夾進房間框 —— 讓角色卡在牆上,而不是飛到世界外面
            // (飛出去的話相機會跟著他跑,整個房間畫面就沒東西了)。
            var r = NetMoveRow.Decode(Node("{\"userId\":1,\"x\":99999,\"z\":-99999}"));
            Assert.AreEqual(NetLimits.RoomWalkMaxX, r.X, 1e-3f);
            Assert.AreEqual(NetLimits.RoomWalkMinZ, r.Z, 1e-3f);
        }

        [Test]
        public void Decode_Turns_Garbage_Into_Zero_Not_NaN()
        {
            // 🔴 一個 NaN 寫進 Transform.position 會污染整棵 avatar hierarchy,
            // 而 Camera.LookAt 吃到 NaN 之後整個房間 RT 變黑。
            // JSON 裡不合法的 token(NaN/Infinity)會被解析成 0/失敗,這裡確認結果一定是有限數。
            foreach (var json in new[]
                     {
                         "{\"userId\":1,\"x\":\"abc\",\"z\":\"abc\"}",   // 型別不對
                         "{\"userId\":1}",                               // 缺欄位
                         "{\"userId\":1,\"x\":1e308,\"z\":-1e308}",       // 大到溢出 float
                     })
            {
                var r = NetMoveRow.Decode(Node(json));
                Assert.IsFalse(float.IsNaN(r.X) || float.IsInfinity(r.X), "X 必須是有限數:" + json);
                Assert.IsFalse(float.IsNaN(r.Z) || float.IsInfinity(r.Z), "Z 必須是有限數:" + json);
                Assert.IsFalse(float.IsNaN(r.Facing) || float.IsInfinity(r.Facing), "Facing 必須是有限數:" + json);
                Assert.LessOrEqual(r.X, NetLimits.RoomWalkMaxX);
                Assert.GreaterOrEqual(r.X, NetLimits.RoomWalkMinX);
            }
        }

        [Test]
        public void DecodeAll_Handles_An_Empty_Or_Missing_Array()
        {
            Assert.AreEqual(0, NetMoveRow.DecodeAll(null).Length);
            Assert.AreEqual(0, NetMoveRow.DecodeAll(NetJson.Arr(Node("{\"m\":[]}"), "m")).Length);
        }
    }
}
