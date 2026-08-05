using NUnit.Framework;
using Sdo.Net;
using Sdo.UI.Core;

namespace Sdo.Tests
{
    /// <summary>
    /// 「房主選了 hard,進遊戲還是 easy」——**難度算不算房間選的東西**這一題。
    ///
    /// 官方歌三個難度共用同一個 gn,所以 <see cref="NetSongRef.SameChartAs"/> 對
    /// 「同一首歌 easy → hard」會回 true(識別層是對的:確實是同一個檔)。
    /// 但拿它當「房間選的有沒有變」就會把換難度整個吃掉:client 不送 → server 手上的難度
    /// 永遠停在進房預設的 easy → 開場 matchStarting 帶著它回來,把房主**自己**的難度蓋回 easy。
    /// 換一首別的歌就正常,因為那時 gn 變了 —— 這正是實機那個「只有同一首歌改難度會中」的症狀。
    /// </summary>
    public class NetSongDifficultyPublishTests
    {
        private static NetSongRef Official(int difficulty, string gn = "sdom1435k.gn")
            => new NetSongRef
            {
                Official = true, Gn = gn, FileId = 11435, Title = "測試歌",
                Difficulty = difficulty, ChartIndex = difficulty,
            };

        [Test]
        public void Changing_Only_The_Difficulty_Must_Be_Published()
        {
            // 🔴 這一條就是那個 bug 的回歸測試。
            var current = Official(0);   // server 手上:進房時預設發布的 easy
            var want = Official(2);      // 房主去選歌畫面把同一首切成 hard

            Assert.IsFalse(NetSongPublisher.SameRoomSelection(want, current),
                "同一首歌換難度是真的改了房間選的東西,不送出去 server 的難度就永遠停在 easy");
        }

        [Test]
        public void The_Same_Song_At_The_Same_Difficulty_Is_Still_Not_A_Change()
        {
            // 房主每次進房間畫面(含結算回房)都會發布一次 —— 那不是換歌,送出去會把全房的
            // avail 打回 unknown,缺歌的人就「莫名其妙又傳一次歌」(R9)。
            var a = Official(1);
            var b = Official(1);
            b.Title = "測試歌(簡繁不同)";
            b.Level = 7;                 // 顯示欄位不算識別

            Assert.IsTrue(NetSongPublisher.SameRoomSelection(a, b), "識別 + 難度都一樣 = 沒變");
        }

        [Test]
        public void External_Song_Difficulty_Slot_Also_Counts()
        {
            // 外部歌大多是「不同難度不同譜面檔」→ SameChartAs 就抓得到了;但 .gn 歌曲包那種
            // 三個難度同一個檔的情況只有 Difficulty 會變,一樣不能漏掉。
            var easy = new NetSongRef { Official = false, PackId = "sha256:ff", SongKey = "x",
                                        ChartRelPath = "a.gn", ChartIndex = 0, Difficulty = 0 };
            var hard = new NetSongRef { Official = false, PackId = "sha256:ff", SongKey = "x",
                                        ChartRelPath = "a.gn", ChartIndex = 0, Difficulty = 2 };

            Assert.IsFalse(NetSongPublisher.SameRoomSelection(hard, easy));
        }

        [Test]
        public void Random_Difficulty_Rounds_Still_Ignore_The_Drawn_Chart()
        {
            // 隨機難度是**房間設定**:每一局抽到的那首(gn 與難度都會變)不算換歌,
            // 送出去只會把全房的準備與 avail 清掉。這一條守著上面的修改沒有踩到它。
            var current = new NetSongRef { Official = true, Gn = "sdom0001k.gn", Title = "隨機難度 3",
                                           RandomTitle = true, Difficulty = 0, ChartIndex = 0 };
            var want = new NetSongRef { Official = true, Gn = "sdom0002k.gn", Title = "隨機難度 3",
                                        RandomTitle = true, Difficulty = 2, ChartIndex = 2 };

            Assert.IsTrue(NetSongPublisher.SameRoomSelection(want, current),
                "隨機難度只看標籤(哪個難度區間),抽到什麼歌/什麼難度都不是房間設定的改變");
        }

        [Test]
        public void SameChartAs_Stays_Identity_Only()
        {
            // 🔴 SameChartAs 是**識別**(收端拿它在自己的歌庫裡定位同一張譜),不能因為這次修改
            // 把難度塞進去 —— 那會讓「同一首歌換難度」在收端變成「另一張譜」。難度走 SameChoiceAs。
            var easy = Official(0);
            var hard = Official(2);

            Assert.IsTrue(easy.SameChartAs(hard), "同一個 gn 就是同一張譜");
            Assert.IsFalse(easy.SameChoiceAs(hard), "但不是同一個選擇");
        }
    }
}
