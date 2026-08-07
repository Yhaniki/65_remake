using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Ruleset;

namespace Sdo.Tests
{
    /// <summary>
    /// 右側名單的組裝。守的是使用者回報的症狀:「明明有多個玩家,進去只顯示本機玩家的名字,
    /// 開始遊戲後約 5~6 秒別人的名字才出現」—— 名單以前是照 server 推來的分數 frame 建的,
    /// 沒收到 frame 的人整個不存在,而名單又只在每 8 拍結算時重建(慢歌一個 8 拍好幾秒)。
    /// </summary>
    public class GameplayRosterTests
    {
        private static RosterSeat[] Seats(params (int id, string name)[] rows)
        {
            var arr = new RosterSeat[rows.Length];
            for (int i = 0; i < rows.Length; i++) arr[i] = new RosterSeat(rows[i].id, rows[i].name);
            return arr;
        }

        private static RosterScore[] Live(params (int id, string name, long score)[] rows)
        {
            var arr = new RosterScore[rows.Length];
            for (int i = 0; i < rows.Length; i++) arr[i] = new RosterScore(rows[i].id, rows[i].name, rows[i].score);
            return arr;
        }

        private static List<string> Names(List<PlayerEntry> r)
        {
            var names = new List<string>();
            foreach (var p in r) names.Add(p.Name);
            return names;
        }

        [Test]
        public void OneFrameYet_EveryoneIsAlreadyOnTheList()
        {
            // 🔴 這一條就是那個 bug:開場一筆 frame 都還沒到,名單也必須是完整的四個人(分數 0)。
            var roster = new List<PlayerEntry>();
            GameplayRoster.Build(roster, Seats((7, "我"), (8, "阿華"), (9, "小明"), (10, "阿美")),
                                 localSeat: 0, localName: "我", localScore: 0, live: null,
                                 leaderUserId: 0, maxRows: 6);

            CollectionAssert.AreEqual(new[] { "我", "阿華", "小明", "阿美" }, Names(roster));
            for (int i = 0; i < roster.Count; i++)
            {
                Assert.AreEqual(i, roster[i].Seat, "平手序要照座位序");
                Assert.AreEqual(i == 0, roster[i].IsLocal);
            }
        }

        [Test]
        public void FramesFillInScores_ByUserId_NotByListOrder()
        {
            // frame 的順序是字典順序(誰先送誰先到),絕不能拿它當名單順序。
            var roster = new List<PlayerEntry>();
            GameplayRoster.Build(roster, Seats((7, "我"), (8, "阿華"), (9, "小明")),
                                 localSeat: 0, localName: "我", localScore: 500,
                                 live: Live((9, "小明", 3000), (8, "阿華", 1200)),
                                 leaderUserId: 0, maxRows: 6);

            CollectionAssert.AreEqual(new[] { "我", "阿華", "小明" }, Names(roster));
            CollectionAssert.AreEqual(new long[] { 500, 1200, 3000 }, new[] { roster[0].Score, roster[1].Score, roster[2].Score });
        }

        [Test]
        public void PartialFrames_KnownScoresShow_UnknownStayZero()
        {
            var roster = new List<PlayerEntry>();
            GameplayRoster.Build(roster, Seats((7, "我"), (8, "阿華"), (9, "小明")),
                                 localSeat: 0, localName: "我", localScore: 400,
                                 live: Live((8, "阿華", 900)), leaderUserId: 0, maxRows: 6);

            CollectionAssert.AreEqual(new long[] { 400, 900, 0 }, new[] { roster[0].Score, roster[1].Score, roster[2].Score });
        }

        [Test]
        public void Spectator_IsNotOnTheList_ButEveryoneElseIs()
        {
            // 旁觀者沒有自己那一列(否則名次裡會多一個沒下場的人),但別人的名字一進場就要齊。
            var roster = new List<PlayerEntry>();
            GameplayRoster.Build(roster, Seats((8, "阿華"), (9, "小明")),
                                 localSeat: -1, localName: "旁觀的我", localScore: 0, live: null,
                                 leaderUserId: 0, maxRows: 6);

            CollectionAssert.AreEqual(new[] { "阿華", "小明" }, Names(roster));
            foreach (var p in roster) Assert.IsFalse(p.IsLocal);
        }

        [Test]
        public void LeaderFlag_FollowsUserId_LocalIncluded()
        {
            var roster = new List<PlayerEntry>();
            GameplayRoster.Build(roster, Seats((7, "我"), (8, "阿華")),
                                 localSeat: 0, localName: "我", localScore: 0, live: null,
                                 leaderUserId: 7, maxRows: 6);
            Assert.IsTrue(roster[0].IsLeader, "領隊是本機時,本機那一列也要標");
            Assert.IsFalse(roster[1].IsLeader);

            GameplayRoster.Build(roster, Seats((7, "我"), (8, "阿華")),
                                 localSeat: 0, localName: "我", localScore: 0, live: null,
                                 leaderUserId: 8, maxRows: 6);
            Assert.IsFalse(roster[0].IsLeader);
            Assert.IsTrue(roster[1].IsLeader);
        }

        [Test]
        public void LeaderUserZero_MarksNobody()
        {
            // 離線 / 還沒有領隊:userId 全是 0,不能因為「0 == 0」把整排都標成領隊。
            var roster = new List<PlayerEntry>();
            GameplayRoster.Build(roster, Seats((0, "我"), (0, "路人")),
                                 localSeat: 0, localName: "我", localScore: 0, live: null,
                                 leaderUserId: 0, maxRows: 6);
            foreach (var p in roster) Assert.IsFalse(p.IsLeader);
        }

        [Test]
        public void UserZero_NeverMatchesAFrame()
        {
            // 座位表沒有 server id(離線)時,不能跟 frame 的 userId 0 配成同一個人。
            var roster = new List<PlayerEntry>();
            GameplayRoster.Build(roster, Seats((0, "我"), (0, "路人")),
                                 localSeat: 0, localName: "我", localScore: 100,
                                 live: Live((0, "誰", 8888)), leaderUserId: 0, maxRows: 6);
            Assert.AreEqual(0, roster[1].Score, "userId 0 不是身分,不能拿來配對");
        }

        [Test]
        public void FrameWithoutASeat_StillGetsARow()
        {
            // 協定上不該發生,但真的發生時他的分數不能憑空消失(沒有座位 → 平手序退回名單順序)。
            var roster = new List<PlayerEntry>();
            GameplayRoster.Build(roster, Seats((7, "我"), (8, "阿華")),
                                 localSeat: 0, localName: "我", localScore: 0,
                                 live: Live((8, "阿華", 100), (99, "幽靈", 777)),
                                 leaderUserId: 0, maxRows: 6);

            CollectionAssert.AreEqual(new[] { "我", "阿華", "幽靈" }, Names(roster));
            Assert.AreEqual(PlayerEntry.NoSeat, roster[2].Seat);
            Assert.AreEqual(777, roster[2].Score);
        }

        [Test]
        public void FrameNameWins_WhenTheSeatTableHasNone()
        {
            var roster = new List<PlayerEntry>();
            GameplayRoster.Build(roster, Seats((7, "我"), (8, "")),
                                 localSeat: 0, localName: "我", localScore: 0,
                                 live: Live((8, "阿華", 10)), leaderUserId: 0, maxRows: 6);
            Assert.AreEqual("阿華", roster[1].Name);
        }

        [Test]
        public void MaxRows_ClipsTheList_LocalFirst()
        {
            // HUD 的 PKSCORE 位數只到 6 列 —— 超出的座位不畫,但本機那一列一定要在。
            var roster = new List<PlayerEntry>();
            GameplayRoster.Build(roster, Seats((1, "a"), (2, "b"), (3, "c"), (4, "d"), (5, "e"), (6, "f"), (7, "g")),
                                 localSeat: 6, localName: "g", localScore: 0, live: null,
                                 leaderUserId: 0, maxRows: 6);
            Assert.AreEqual(6, roster.Count);
            Assert.IsTrue(roster[0].IsLocal);
        }

        [Test]
        public void Rebuild_ClearsThePreviousContents()
        {
            var roster = new List<PlayerEntry> { new PlayerEntry("上一場的人", 999, false) };
            GameplayRoster.Build(roster, Seats((7, "我")), localSeat: 0, localName: "我", localScore: 0,
                                 live: null, leaderUserId: 0, maxRows: 6);
            CollectionAssert.AreEqual(new[] { "我" }, Names(roster));
        }
    }
}
