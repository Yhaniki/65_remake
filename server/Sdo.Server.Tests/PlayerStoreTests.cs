using System.IO;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Sdo.Net;
using Sdo.Server.Store;

namespace Sdo.Tests
{
    /// <summary>
    /// 玩家公開資料的落地層(<c>&lt;data&gt;/players.db</c>)。
    ///
    /// 這裡真的開一顆 SQLite(臨時目錄),因為要守的正是「重開之後那份資料還在不在」——
    /// 用假的儲存介面測的話,唯一會出錯的那一段(SQL 與往返)剛好就是沒被測到的那一段。
    /// </summary>
    public class PlayerStoreTests
    {
        private string _dir;
        private string _db;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "sdo_players_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_dir);
            _db = Path.Combine(_dir, "players.db");
        }

        [TearDown]
        public void TearDown()
        {
            // SQLite 的連線池會把檔案 handle 留著 → Windows 上刪不掉,而且下一個測試會開到同一顆。
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        }

        private PlayerStore Open()
        {
            string err;
            var store = PlayerStore.TryOpen(_db, out err);
            Assert.NotNull(store, "開不起來:" + err);
            return store;
        }

        private static PlayerSnapshot Sample(string name)
            => new PlayerSnapshot
            {
                Name = name,
                PlayerId = "00000001",
                Guild = "夜貓子",
                GuildEmblem = "SMALL43",
                Level = 27,
                Look = new NetAvatarLook { Gender = 1, BodyIndex = 2, Parts = new[] { "COAT_1", "HAIR_9" } },
                Card = new NetPlayerCard
                {
                    Perfect = 12345, Cool = 678, Bad = 9, Miss = 3,
                    Plays = 42, Wins = 30, Losses = 12,
                    ExpPercent = 61, Fame = 15,
                    City = "台北", Im = "1234", Constellation = "獅子", Age = "18",
                },
                UpdatedUtcMs = 1_700_000_000_000L,
            };

        [Test]
        public void A_Saved_Snapshot_Survives_Reopening_The_File()
        {
            // 這條就是整個功能的理由:人下線了、server 也重開了,那份資料還在。
            using (var store = Open())
            {
                string err;
                Assert.IsTrue(store.Save(Sample("小明"), out err), err);
            }
            SqliteConnection.ClearAllPools();

            using (var store = Open())
            {
                PlayerSnapshot got;
                Assert.IsTrue(store.TryLoad("小明", out got));
                Assert.AreEqual("小明", got.Name);
                Assert.AreEqual("00000001", got.PlayerId);
                Assert.AreEqual("夜貓子", got.Guild);
                Assert.AreEqual("SMALL43", got.GuildEmblem);
                Assert.AreEqual(27, got.Level);
                Assert.AreEqual(1_700_000_000_000L, got.UpdatedUtcMs);

                // 名片的每一個數字都要原封不動 —— 這些正是資料頁上那幾行。
                Assert.AreEqual(12345, got.Card.Perfect);
                Assert.AreEqual(678, got.Card.Cool);
                Assert.AreEqual(9, got.Card.Bad);
                Assert.AreEqual(3, got.Card.Miss);
                Assert.AreEqual(42, got.Card.Plays);
                Assert.AreEqual(30, got.Card.Wins);
                Assert.AreEqual(12, got.Card.Losses);
                Assert.AreEqual(61, got.Card.ExpPercent);
                Assert.AreEqual(15, got.Card.Fame);
                Assert.AreEqual("台北", got.Card.City);
                Assert.AreEqual("1234", got.Card.Im);
                Assert.AreEqual("獅子", got.Card.Constellation);
                Assert.AreEqual("18", got.Card.Age);

                // 外觀是整包 JSON 存的 —— 它決定資料頁上那尊 3D 角色穿什麼。
                Assert.AreEqual(1, got.Look.Gender);
                Assert.AreEqual(2, got.Look.BodyIndex);
                CollectionAssert.AreEqual(new[] { "COAT_1", "HAIR_9" }, got.Look.Parts);
            }
        }

        [Test]
        public void Saving_The_Same_Name_Twice_Updates_In_Place()
        {
            // 同一個人打了第二局 → 更新那一列,不是再長一列出來
            // (不然一個玩過一百首歌的人會在表裡有一百份,而查詢只會拿到其中一份)。
            using (var store = Open())
            {
                string err;
                var first = Sample("小明");
                Assert.IsTrue(store.Save(first, out err), err);

                var second = Sample("小明");
                second.Card.Plays = 43;
                second.Level = 28;
                second.UpdatedUtcMs = 1_700_000_999_000L;
                Assert.IsTrue(store.Save(second, out err), err);

                Assert.AreEqual(1, store.Count);

                PlayerSnapshot got;
                Assert.IsTrue(store.TryLoad("小明", out got));
                Assert.AreEqual(43, got.Card.Plays);
                Assert.AreEqual(28, got.Level);
                Assert.AreEqual(1_700_000_999_000L, got.UpdatedUtcMs);
            }
        }

        [Test]
        public void The_Name_Key_Ignores_Case_And_Surrounding_Space()
        {
            // 🔴 必須與 Hub.ControlByName 的 OrdinalIgnoreCase 同一套規則。兩邊不一致的話,
            //    同一個人會在「線上查得到」與「離線查得到」之間漂移 —— 症狀只是「有時候查不到」。
            using (var store = Open())
            {
                string err;
                Assert.IsTrue(store.Save(Sample("DanceKing"), out err), err);

                PlayerSnapshot got;
                Assert.IsTrue(store.TryLoad("danceking", out got), "小寫應該查得到");
                Assert.IsTrue(store.TryLoad("  DANCEKING  ", out got), "前後空白應該被吃掉");

                // 存的時候大小寫也要歸一 —— 不然「DanceKing」與「danceking」會變成兩列。
                var again = Sample("DANCEKING");
                again.Level = 99;
                Assert.IsTrue(store.Save(again, out err), err);
                Assert.AreEqual(1, store.Count);
                Assert.IsTrue(store.TryLoad("DanceKing", out got));
                Assert.AreEqual(99, got.Level);
                // 顯示用的名字是**最後寫的那個大小寫**(那是他最後一次自報的樣子)。
                Assert.AreEqual("DANCEKING", got.Name);
            }
        }

        [Test]
        public void A_Nameless_Snapshot_Is_Refused()
        {
            // 名字是主鍵,而「還沒 hello 完就斷線」的連線名字是空的 ——
            // 讓那些人共用一列 "" 的話,那列會被每個過客輪流覆寫成毫無意義的東西。
            using (var store = Open())
            {
                string err;
                var s = Sample("");
                Assert.IsFalse(store.Save(s, out err));
                Assert.IsNotNull(err);
                Assert.AreEqual(0, store.Count);

                s.Name = "   ";
                Assert.IsFalse(store.Save(s, out err), "只有空白的名字也一樣");
                Assert.AreEqual(0, store.Count);
            }
        }

        [Test]
        public void Looking_Up_Someone_We_Never_Saw_Just_Returns_False()
        {
            // 「沒見過這個人」是正常情況(cardQuery 會回 found=false),不是例外。
            using (var store = Open())
            {
                PlayerSnapshot got;
                Assert.IsFalse(store.TryLoad("查無此人", out got));
                Assert.IsNull(got);
                Assert.IsFalse(store.TryLoad("", out got), "空名字不該查出任何東西");
            }
        }

        [Test]
        public void Opening_A_Path_That_Cannot_Be_A_File_Fails_Softly()
        {
            // 🔴 開不起來**不能丟例外**:它跑在 Hub 的建構子上,而玩家快照只是顯示用的加分項 ——
            //    把 server 起不來的原因擴張到這張表是很糟的交易(見 PlayerStore.TryOpen)。
            //    拿一個「已經存在的目錄」當 db 路徑:任何平台上那都開不成檔案。
            string err;
            var store = PlayerStore.TryOpen(_dir, out err);
            Assert.IsNull(store);
            Assert.IsNotNull(err);
        }

        [Test]
        public void A_Store_That_Was_Disposed_Refuses_Quietly()
        {
            // server 收攤(Hub.Run 結尾)之後還飛進來一筆的話,要安靜地失敗而不是炸掉。
            var store = Open();
            store.Dispose();

            string err;
            Assert.IsFalse(store.Save(Sample("小明"), out err));
            PlayerSnapshot got;
            Assert.IsFalse(store.TryLoad("小明", out got));
            Assert.AreEqual(-1, store.Count);
        }
    }
}
