using System.Reflection;
using NUnit.Framework;
using Sdo.Game.Net;
using Sdo.Net;

namespace Sdo.Tests
{
    /// <summary>
    /// 位置流的 rev 閘門。釘住的是實際踩到的回歸:這個閘門原本要求 <c>roomRev</c> 與當前
    /// <c>Room.Rev</c> **完全相等**,結果只要 client 丟棄過任何一份 roomState(進房/旁觀的等待狀態、
    /// 或 rev 沒前進的重送),Room.Rev 就永久落後 server 一截 —— 之後每一批 moves 都「不等於」而被丟掉,
    /// 房間裡的遠端玩家從此定在原地不動。
    ///
    /// 正確的規則是「**比現在舊**的才丟」:那樣仍然擋得住座位↔旁觀切換前發出的舊批次(它的 rev 更小),
    /// 又不會因為 server 走在前面就餓死。
    /// </summary>
    public class NetClientMovesRevTests
    {
        private const int RemoteUser = 88;
        private const int Code = 4242;

        [Test]
        public void SameRevIsAccepted()
        {
            var net = NetIn(Code, rev: 10);
            try
            {
                Deliver(net, Moves(Code, rev: 10, x: 5f));
                Assert.AreEqual(1, net.Moves.Count);
                Assert.AreEqual(5f, net.Moves[RemoteUser].X);
            }
            finally { net.Disconnect("test"); }
        }

        [Test]
        public void NewerRevIsAcceptedInsteadOfStarvingThePositionStream()
        {
            var net = NetIn(Code, rev: 10);
            try
            {
                // server 已經前進到 rev 11(對應的 roomState 還在路上,或被 client 丟棄了)。
                Deliver(net, Moves(Code, rev: 11, x: 7f));

                Assert.AreEqual(1, net.Moves.Count,
                    "比當前新的批次不能丟 —— 丟了就是遠端玩家從此不動的那個 bug");
                Assert.AreEqual(7f, net.Moves[RemoteUser].X);
            }
            finally { net.Disconnect("test"); }
        }

        [Test]
        public void StaleRevIsStillDropped()
        {
            var net = NetIn(Code, rev: 10);
            try
            {
                Deliver(net, Moves(Code, rev: 9, x: 99f));
                Assert.AreEqual(0, net.Moves.Count,
                    "切換座位/旁觀之前發出的舊批次不能把舊的插值位置放回去");
            }
            finally { net.Disconnect("test"); }
        }

        [Test]
        public void OtherRoomIsDropped()
        {
            var net = NetIn(Code, rev: 10);
            try
            {
                Deliver(net, Moves(Code + 1, rev: 10, x: 99f));
                Assert.AreEqual(0, net.Moves.Count, "別間房的位置流不能套到這間房的人身上");
            }
            finally { net.Disconnect("test"); }
        }

        // ---- helpers ----

        private static NetClient NetIn(int code, int rev)
        {
            var net = new NetClient();
            var snapshot = new NetRoomSnapshot { Code = code, Rev = rev };
            var setter = typeof(NetClient)
                .GetProperty(nameof(NetClient.Room), BindingFlags.Instance | BindingFlags.Public)
                .GetSetMethod(true);
            Assert.IsNotNull(setter, "NetClient.Room 的 private setter 不見了");
            setter.Invoke(net, new object[] { snapshot });
            return net;
        }

        private static JObj Moves(int code, int rev, float x)
            => JObj.New()
                .Str(NetProto.FieldType, NetProto.Moves)
                .Int("roomCode", code)
                .Int("roomRev", rev)
                .Put("m", JArr.New().Add(JObj.New()
                    .Int("userId", RemoteUser)
                    .Num("x", x).Num("z", 0f).Num("f", 0f).Bool("w", true)));

        private static void Deliver(NetClient net, JObj message)
        {
            object node;
            string type;
            string json = message.Json();
            Assert.IsTrue(NetJson.TryParseMessage(json, out node, out type), json);
            var handle = typeof(NetClient).GetMethod("Handle", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(handle);
            handle.Invoke(net, new[] { type, node });
        }
    }
}
