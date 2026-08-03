using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Net;
using Sdo.Net.Server;

namespace Sdo.Tests
{
    /// <summary>
    /// R1:5 位數房號的配發池。
    ///
    /// 為什麼用池而不是「隨機抽 + 撞了重試」:重試法在房間數多的時候會變慢(生日問題)，
    /// 而且最糟情況沒有上界。池子是 O(1) 配發/回收，而且天然保證不重複。
    /// </summary>
    public class RoomCodePoolTests
    {
        [Test]
        public void All_Codes_Are_Five_Digits()
        {
            var pool = new RoomCodePool(seed: 1);
            for (int i = 0; i < 500; i++)
            {
                int code;
                Assert.IsTrue(pool.TryRent(out code));
                Assert.GreaterOrEqual(code, 10000, "房號必須是 5 位數");
                Assert.LessOrEqual(code, 99999);
                Assert.AreEqual(5, code.ToString().Length);
            }
        }

        [Test]
        public void Capacity_Covers_The_Whole_Range()
        {
            var pool = new RoomCodePool();
            Assert.AreEqual(90000, pool.Capacity);
            Assert.AreEqual(NetLimits.MaxRoomCode - NetLimits.MinRoomCode + 1, pool.Capacity);
            Assert.AreEqual(pool.Capacity, pool.Available);
            Assert.AreEqual(0, pool.Rented);
        }

        [Test]
        public void Rented_Codes_Are_Unique()
        {
            // 兩間房拿到同一個號碼是最糟的 bug —— 玩家會進到別人的房間。
            var pool = new RoomCodePool(seed: 7);
            var seen = new HashSet<int>();
            for (int i = 0; i < 5000; i++)
            {
                int code;
                Assert.IsTrue(pool.TryRent(out code));
                Assert.IsTrue(seen.Add(code), "房號 " + code + " 被配發兩次");
            }
        }

        [Test]
        public void Exhausting_The_Pool_Fails_Cleanly()
        {
            var pool = new RoomCodePool(seed: 3);
            int code;
            for (int i = 0; i < pool.Capacity; i++)
                Assert.IsTrue(pool.TryRent(out code), "第 " + i + " 個應該還配得出來");

            Assert.AreEqual(0, pool.Available);
            Assert.IsFalse(pool.TryRent(out code), "池子空了要乾淨地失敗,不要拋例外");
            Assert.AreEqual(0, code);
        }

        [Test]
        public void Returned_Codes_Become_Available_Again()
        {
            var pool = new RoomCodePool(seed: 5);
            int code;
            pool.TryRent(out code);
            int before = pool.Available;

            Assert.IsTrue(pool.Return(code));
            Assert.AreEqual(before + 1, pool.Available);
            Assert.IsFalse(pool.IsRented(code));
        }

        [Test]
        public void Returned_Codes_Are_Not_Immediately_Reissued()
        {
            // 🔴 這條是刻意的設計:剛關掉的房號若馬上被下一間房拿走，手上還留著舊房號的玩家
            // 會**進到一間完全陌生的房**。用 FIFO(queue 尾端)而不是 LIFO(stack)。
            var pool = new RoomCodePool(seed: 11);

            int first;
            pool.TryRent(out first);
            pool.Return(first);

            // 接下來配好幾個,都不該是剛歸還的那個。
            for (int i = 0; i < 100; i++)
            {
                int code;
                pool.TryRent(out code);
                Assert.AreNotEqual(first, code, "剛歸還的號碼不該在第 " + i + " 次就被重發");
            }
        }

        [Test]
        public void Returning_The_Same_Code_Twice_Does_Not_Duplicate_It()
        {
            // 沒有這道檢查,同一個號碼被歸還兩次就會在池子裡出現兩份,
            // 然後被配給兩間不同的房 —— 跟 unique 那條測試守的是同一個災難。
            var pool = new RoomCodePool(seed: 13);
            int code;
            pool.TryRent(out code);

            Assert.IsTrue(pool.Return(code));
            int after = pool.Available;
            Assert.IsFalse(pool.Return(code), "重複歸還應該被拒絕");
            Assert.AreEqual(after, pool.Available, "池子大小不該變");
        }

        [Test]
        public void Returning_A_Code_That_Was_Never_Rented_Is_Rejected()
        {
            var pool = new RoomCodePool(seed: 17);
            int cap = pool.Available;
            Assert.IsFalse(pool.Return(50000), "沒配出去的號碼不能歸還");
            Assert.AreEqual(cap, pool.Available);
        }

        [Test]
        public void Out_Of_Range_Codes_Are_Rejected()
        {
            var pool = new RoomCodePool();
            Assert.IsFalse(pool.Return(9999));
            Assert.IsFalse(pool.Return(100000));
            Assert.IsFalse(pool.Return(0));
            Assert.IsFalse(pool.Return(-1));

            Assert.IsFalse(RoomCodePool.IsValidCode(9999));
            Assert.IsTrue(RoomCodePool.IsValidCode(10000));
            Assert.IsTrue(RoomCodePool.IsValidCode(99999));
            Assert.IsFalse(RoomCodePool.IsValidCode(100000));
        }

        [Test]
        public void IsRented_Tracks_State()
        {
            var pool = new RoomCodePool(seed: 19);
            int code;
            pool.TryRent(out code);

            Assert.IsTrue(pool.IsRented(code));
            pool.Return(code);
            Assert.IsFalse(pool.IsRented(code));

            Assert.IsFalse(pool.IsRented(9999), "範圍外一律不算 rented");
        }

        [Test]
        public void Codes_Are_Shuffled_Not_Sequential()
        {
            // 房號要看起來是隨意的 —— 順序發的話第一間房永遠是 10000,
            // 隨便猜就能猜中別人的房(而且 MVP 沒有密碼保護)。
            var pool = new RoomCodePool(seed: 23);
            var first20 = new List<int>();
            for (int i = 0; i < 20; i++)
            {
                int code;
                pool.TryRent(out code);
                first20.Add(code);
            }

            bool sequential = true;
            for (int i = 1; i < first20.Count; i++)
                if (first20[i] != first20[i - 1] + 1) { sequential = false; break; }

            Assert.IsFalse(sequential, "配發順序不該是連號:" + string.Join(",", first20));
        }

        [Test]
        public void Same_Seed_Gives_The_Same_Order()
        {
            // 可重現性:測試與除錯都需要。
            var a = new RoomCodePool(seed: 42);
            var b = new RoomCodePool(seed: 42);
            for (int i = 0; i < 50; i++)
            {
                int ca, cb;
                a.TryRent(out ca);
                b.TryRent(out cb);
                Assert.AreEqual(ca, cb);
            }
        }

        [Test]
        public void Different_Seeds_Give_Different_Orders()
        {
            var a = new RoomCodePool(seed: 1);
            var b = new RoomCodePool(seed: 2);

            bool anyDifferent = false;
            for (int i = 0; i < 20; i++)
            {
                int ca, cb;
                a.TryRent(out ca);
                b.TryRent(out cb);
                if (ca != cb) { anyDifferent = true; break; }
            }
            Assert.IsTrue(anyDifferent);
        }

        [Test]
        public void Rented_Plus_Available_Always_Equals_Capacity()
        {
            var pool = new RoomCodePool(seed: 29);
            var held = new List<int>();

            for (int i = 0; i < 100; i++)
            {
                int code;
                pool.TryRent(out code);
                held.Add(code);
                Assert.AreEqual(pool.Capacity, pool.Rented + pool.Available);
            }
            for (int i = 0; i < held.Count; i++)
            {
                pool.Return(held[i]);
                Assert.AreEqual(pool.Capacity, pool.Rented + pool.Available);
            }
            Assert.AreEqual(0, pool.Rented);
        }
    }
}
