using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Net.Server;

namespace Sdo.Tests
{
    /// <summary>
    /// blob 清理的決策邏輯(需求:「檔案最多留一天,定期清除」)。
    ///
    /// 為什麼這一塊值得完整測試:它是**會刪使用者磁碟上的東西**的程式碼,而且錯的方向不對稱 ——
    /// 少刪只是浪費空間,多刪會讓「正在等人下載的那一場」中途失去來源,而症狀是
    /// 「別人下載完譜面對不上」,完全指不到清理程序。所以每一條規則各一個測試。
    /// </summary>
    public class BlobIndexTests
    {
        private const long Hour = 3600L * 1000L;
        private const long Now = 1_700_000_000_000L;   // 固定的「現在」,不用真時鐘(注入才可測)

        private static BlobPackRecord Pack(string id, long ageHours, long bytes = 1000, params string[] shas)
            => new BlobPackRecord
            {
                PackId = id,
                LastUsedUtcMs = Now - ageHours * Hour,
                Shas = shas.Length > 0 ? shas : new[] { id + "-sha" },
                TotalBytes = bytes,
            };

        private static ICollection<string> Pinned(params string[] ids) => new HashSet<string>(ids);

        [Test]
        public void A_Pack_Older_Than_The_Ttl_Is_Deleted()
        {
            var plan = BlobIndex.Plan(new[] { Pack("old", 25) }, new[] { "old-sha" },
                                      Now, BlobIndex.DefaultTtlHours, Pinned(), 0);
            CollectionAssert.AreEqual(new[] { "old" }, plan.PacksToDelete);
            CollectionAssert.AreEqual(new[] { "old-sha" }, plan.BlobsToDelete, "包走了,它的檔案也沒人引用了");
        }

        [Test]
        public void A_Pack_Inside_The_Ttl_Is_Kept()
        {
            var plan = BlobIndex.Plan(new[] { Pack("fresh", 23) }, new[] { "fresh-sha" },
                                      Now, BlobIndex.DefaultTtlHours, Pinned(), 0);
            Assert.IsTrue(plan.Empty, "還沒過期,什麼都不該動");
        }

        [Test]
        public void A_Pinned_Pack_Is_Never_Deleted_Even_When_Ancient()
        {
            // 🔴 pin = 存活房間「現在選的那首歌」。少了這條,一場正在等人下載的比賽會被自己的
            //    清理程序把來源刪掉。
            var plan = BlobIndex.Plan(new[] { Pack("inuse", 999) }, new[] { "inuse-sha" },
                                      Now, BlobIndex.DefaultTtlHours, Pinned("inuse"), 0);
            Assert.IsTrue(plan.Empty, "被 pin 的包再舊也不能刪");
        }

        [Test]
        public void A_Blob_Still_Referenced_By_Another_Pack_Survives()
        {
            // 內容尋址 → 兩首歌可以共用同一個音檔。刪掉其中一首**不能**動到共用的那個檔案,
            // 否則另一首歌之後傳給人就少一個檔。
            var packs = new[]
            {
                Pack("old", 25, 1000, "shared", "only-old"),
                Pack("new", 1, 1000, "shared", "only-new"),
            };
            var plan = BlobIndex.Plan(packs, new[] { "shared", "only-old", "only-new" },
                                      Now, BlobIndex.DefaultTtlHours, Pinned(), 0);
            CollectionAssert.AreEqual(new[] { "old" }, plan.PacksToDelete);
            CollectionAssert.AreEqual(new[] { "only-old" }, plan.BlobsToDelete,
                "只有『只有過期那個包在用』的檔案該刪;shared 還被 new 引用著");
        }

        [Test]
        public void An_Orphan_Blob_With_No_Pack_At_All_Is_Deleted()
        {
            // 上傳中途斷線之類會留下孤兒檔案(沒有任何 pack json 引用它)。
            var plan = BlobIndex.Plan(new BlobPackRecord[0], new[] { "orphan" },
                                      Now, BlobIndex.DefaultTtlHours, Pinned(), 0);
            CollectionAssert.IsEmpty(plan.PacksToDelete);
            CollectionAssert.AreEqual(new[] { "orphan" }, plan.BlobsToDelete);
        }

        [Test]
        public void Over_The_Size_Cap_Evicts_The_Least_Recently_Used_First()
        {
            // 三個包各 1000 bytes,上限 2500 → 要丟一個,而且必須是最久沒用的那個。
            var packs = new[] { Pack("a", 10), Pack("b", 5), Pack("c", 1) };
            var plan = BlobIndex.Plan(packs, new[] { "a-sha", "b-sha", "c-sha" },
                                      Now, 0 /* 不依時間 */, Pinned(), 2500);
            CollectionAssert.AreEqual(new[] { "a" }, plan.PacksToDelete, "最久沒用的先走");
            CollectionAssert.AreEqual(new[] { "a-sha" }, plan.BlobsToDelete);
        }

        [Test]
        public void The_Size_Cap_Still_Respects_Pins()
        {
            // 就算超過上限,pinned 的包也不能動 —— 寧可暫時超過配額,也不要把進行中的一場弄壞。
            var packs = new[] { Pack("a", 10), Pack("b", 5) };
            var plan = BlobIndex.Plan(packs, new[] { "a-sha", "b-sha" },
                                      Now, 0, Pinned("a", "b"), 500);
            Assert.IsTrue(plan.Empty, "全部 pinned → 超過上限也不能刪");
        }

        [Test]
        public void Ttl_Zero_Means_Time_Based_Deletion_Is_Off()
        {
            var plan = BlobIndex.Plan(new[] { Pack("ancient", 10000) }, new[] { "ancient-sha" },
                                      Now, 0, Pinned(), 0);
            Assert.IsTrue(plan.Empty, "ttl<=0 → 不依時間刪(只做配額)");
        }

        [Test]
        public void Exactly_At_The_Ttl_Boundary_Is_Kept()
        {
            // 邊界用「嚴格大於」判定:剛好滿 24 小時的還留著。門檻寫成 >= 的話
            // 每次掃描都可能把「剛好整點」的包刪掉,而那是個隨機出現的 bug。
            Assert.IsFalse(BlobIndex.IsExpired(Now - 24 * Hour, Now, 24), "剛好 24 小時 → 還沒過期");
            Assert.IsTrue(BlobIndex.IsExpired(Now - 24 * Hour - 1, Now, 24), "多 1ms → 過期");
        }

        [Test]
        public void Null_And_Empty_Inputs_Do_Not_Throw()
        {
            var plan = BlobIndex.Plan(null, null, Now, 24, null, 0);
            Assert.IsTrue(plan.Empty);
            // 沒有 packId 的壞紀錄要被忽略,不要變成「刪掉一個叫空字串的包」
            var plan2 = BlobIndex.Plan(new[] { new BlobPackRecord { PackId = "" } }, new string[0], Now, 24, null, 0);
            Assert.IsTrue(plan2.Empty);
        }

        [Test]
        public void Blob_Path_Shards_By_The_First_Two_Hex_Chars()
        {
            Assert.AreEqual("ab/abcdef0123", BlobIndex.BlobRelPath("abcdef0123"));
            Assert.IsNull(BlobIndex.BlobRelPath(null));
            Assert.IsNull(BlobIndex.BlobRelPath("ab"), "太短的 sha 不該產生路徑");
        }
    }
}
