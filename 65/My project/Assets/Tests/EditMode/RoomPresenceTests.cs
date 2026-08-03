using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Net;
using Sdo.UI.Services;

namespace Sdo.Tests
{
    /// <summary>
    /// 左下訊息欄那兩行藍字廣播的依據。重點是**切旁觀不是進出房間** ——
    /// 這條錯掉的話,每按一次旁觀鈕就會多出一對根本沒發生的「離開/進入舞台遊戲」。
    /// </summary>
    public class RoomPresenceTests
    {
        private const int Me = 1;

        // 座位 seats[] 給 (userId, name);旁觀席 specs[] 同。userId 0 = 空。
        private static NetRoomSnapshot Snap(int[] seatIds, string[] seatNames, int[] specIds, string[] specNames)
        {
            var snap = new NetRoomSnapshot();
            for (int i = 0; i < seatIds.Length && i < snap.Seats.Length; i++)
            {
                if (seatIds[i] == 0) continue;
                snap.Seats[i].State = SeatState.Taken;   // IsTaken 看的是 State,不是有沒有 userId
                snap.Seats[i].UserId = seatIds[i];
                snap.Seats[i].Name = seatNames[i];
            }
            var specs = new List<NetSpectator>();
            for (int i = 0; i < specIds.Length; i++)
                specs.Add(new NetSpectator { UserId = specIds[i], Name = specNames[i] });
            snap.Spectators = specs.ToArray();
            return snap;
        }

        private static NetRoomSnapshot Seated(params (int id, string name)[] people)
        {
            var ids = new int[people.Length]; var names = new string[people.Length];
            for (int i = 0; i < people.Length; i++) { ids[i] = people[i].id; names[i] = people[i].name; }
            return Snap(ids, names, new int[0], new string[0]);
        }

        private static Dictionary<int, string> Collect(NetRoomSnapshot snap)
        {
            var d = new Dictionary<int, string>();
            RoomPresence.Collect(snap, Me, d);
            return d;
        }

        private static void Diff(NetRoomSnapshot before, NetRoomSnapshot now,
                                 out List<string> entered, out List<string> left)
        {
            entered = new List<string>();
            left = new List<string>();
            RoomPresence.Diff(Collect(before), Collect(now), entered, left);
        }

        [Test]
        public void Collect_Counts_Seats_And_Spectators()
        {
            var snap = Snap(new[] { Me, 2 }, new[] { "我", "淡藍" }, new[] { 3 }, new[] { "旁觀者" });
            var now = Collect(snap);
            Assert.AreEqual(2, now.Count, "座位 + 旁觀席都算人");
            Assert.AreEqual("淡藍", now[2]);
            Assert.AreEqual("旁觀者", now[3]);
        }

        [Test]
        public void Collect_Skips_Local_Player_In_Either_List()
        {
            Assert.IsFalse(Collect(Seated((Me, "我"), (2, "淡藍"))).ContainsKey(Me), "座位上的自己不算");
            var spectating = Snap(new[] { 2 }, new[] { "淡藍" }, new[] { Me }, new[] { "我" });
            Assert.IsFalse(Collect(spectating).ContainsKey(Me), "旁觀席上的自己也不算");
        }

        [Test]
        public void Seat_To_Spectator_Is_Not_A_Departure()
        {
            var before = Seated((Me, "我"), (2, "淡藍"));
            var after = Snap(new[] { Me }, new[] { "我" }, new[] { 2 }, new[] { "淡藍" });
            List<string> entered, left;
            Diff(before, after, out entered, out left);
            CollectionAssert.IsEmpty(left, "按旁觀鈕不該播「離開舞台遊戲」");
            CollectionAssert.IsEmpty(entered);
        }

        [Test]
        public void Spectator_Back_To_Seat_Is_Not_An_Arrival()
        {
            var before = Snap(new[] { Me }, new[] { "我" }, new[] { 2 }, new[] { "淡藍" });
            var after = Seated((Me, "我"), (2, "淡藍"));
            List<string> entered, left;
            Diff(before, after, out entered, out left);
            CollectionAssert.IsEmpty(entered, "從旁觀回座位不該播「進入舞台遊戲」");
            CollectionAssert.IsEmpty(left);
        }

        [Test]
        public void Real_Join_And_Leave_Still_Announce()
        {
            List<string> entered, left;
            Diff(Seated((Me, "我")), Seated((Me, "我"), (2, "淡藍")), out entered, out left);
            CollectionAssert.AreEqual(new[] { "淡藍" }, entered);
            CollectionAssert.IsEmpty(left);

            Diff(Seated((Me, "我"), (2, "淡藍")), Seated((Me, "我")), out entered, out left);
            CollectionAssert.AreEqual(new[] { "淡藍" }, left, "離開用上一份記到的名字");
            CollectionAssert.IsEmpty(entered);
        }

        [Test]
        public void Spectator_Leaving_The_Room_Announces()
        {
            var before = Snap(new[] { Me }, new[] { "我" }, new[] { 2 }, new[] { "淡藍" });
            List<string> entered, left;
            Diff(before, Seated((Me, "我")), out entered, out left);
            CollectionAssert.AreEqual(new[] { "淡藍" }, left, "旁觀者真的走人還是要播");
        }

        [Test]
        public void Joining_Straight_Into_The_Spectator_Bench_Announces()
        {
            var after = Snap(new[] { Me }, new[] { "我" }, new[] { 2 }, new[] { "淡藍" });
            List<string> entered, left;
            Diff(Seated((Me, "我")), after, out entered, out left);
            CollectionAssert.AreEqual(new[] { "淡藍" }, entered, "一進門就旁觀的人也算進來了");
        }

        [Test]
        public void Null_Snapshot_Collects_Nothing()
        {
            var d = new Dictionary<int, string> { { 9, "殘留" } };
            RoomPresence.Collect(null, Me, d);
            CollectionAssert.IsEmpty(d, "沒有快照 → 名單清空,不留上一份的殘影");
        }
    }
}
