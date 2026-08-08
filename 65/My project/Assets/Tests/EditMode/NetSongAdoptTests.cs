using NUnit.Framework;
using Sdo.Net;
using Sdo.UI.Core;

namespace Sdo.Tests
{
    /// <summary>
    /// 「房主把房間給我 → 我跳出去再回房間 → 歌名變成前一次那首」。
    ///
    /// 病因:非房主的 <see cref="GameSession"/> 從頭到尾都還是**他自己**上次選的歌,而
    /// <see cref="NetSongPublisher.Publish"/> 是拿本機 session 去跟 server 比對的 ——
    /// 一旦他接手房主(被轉讓、原房主離開遞補、座位全空後第一個坐下),下一次走到發布路徑
    /// 就把那首舊歌推上去,把房間真正選好的歌蓋掉。房間設定早就修過同一種病
    /// (<see cref="NetRoomSettingsPublisher.AdoptIfNotHost"/>:「把房主給別人之後變成自由模式」),
    /// 歌卻沒有對應的那一半。
    ///
    /// 這裡守的是修法的核心約束:<c>AdoptToSession</c> 必須是 <c>FromSession</c> 的**左反元** ——
    /// 收完之後再算一次要送什麼,答案必須是「跟 server 手上那份一樣 → 不用送」。
    /// 不成立的話症狀會從「蓋掉別人的歌」變成「每一份房間快照都重送一次歌」,
    /// 而重送會把全房的 ready/avail 打回去(R9),缺歌的人於是不停地重新傳歌。
    /// </summary>
    public class NetSongAdoptTests
    {
        // 目錄裡不存在的 gn —— 這幾條測試要驗的是欄位往返,不依賴本機有沒有這首歌。
        private const string DancePrince = "sdom9101k.gn";
        private const string IceCream = "sdom9102k.gn";

        private static NetSongRef Official(string gn, int difficulty, string title)
            => new NetSongRef
            {
                Official = true, Gn = gn, FileId = 19101, Title = title,
                Difficulty = difficulty, ChartIndex = difficulty,
            };

        /// <summary>上一場玩完留在 session 裡的那首歌(= 使用者說的 ice cream)。</summary>
        private static GameSession StaleSession()
        {
            var s = new GameSession();
            s.SetOfficialSong(IceCream, 19102, "ice cream", "someone");
            s.Difficulty = Difficulty.Normal;
            return s;
        }

        [Test]
        public void Without_Adopting_The_New_Host_Overwrites_The_Room_Song()
        {
            // 這就是 bug 本身:接手房主的那台會判定「房間選的東西變了」→ 送出自己上次那首。
            var s = StaleSession();
            var roomSong = Official(DancePrince, 2, "dance prince");

            Assert.IsFalse(NetSongPublisher.SameRoomSelection(NetSongPublisher.FromSession(s), roomSong),
                "沒有收下房間的歌時,新房主手上的 session 與房間不一致 —— 這正是覆蓋的來源");
        }

        [Test]
        public void Adopting_The_Room_Song_Stops_The_New_Host_From_Republishing()
        {
            var s = StaleSession();
            var roomSong = Official(DancePrince, 2, "dance prince");

            Assert.IsTrue(NetSongPublisher.AdoptToSession(s, roomSong));

            Assert.AreEqual(DancePrince, s.SongGn);
            Assert.AreEqual(Difficulty.Hard, s.Difficulty, "難度也是房間選的一部分(SameChoiceAs 會比)");
            Assert.IsTrue(NetSongPublisher.SameRoomSelection(NetSongPublisher.FromSession(s), roomSong),
                "收下之後升房主不能再送 —— 送一次就把全房的 ready/avail 打回 unknown(R9)");
        }

        [Test]
        public void Adopting_An_Official_Song_Clears_The_External_Song_Flag()
        {
            // 自己上次玩的是外部歌 → IsExternalSong 留著 true 的話,進場放的還是那首
            // (FrontendApp.StartGameplay 只看這個旗標,見 GameSession.SetOfficialSong)。
            var s = new GameSession();
            s.IsExternalSong = true;
            s.SongGn = "ext_deadbeef";
            s.ExternalChartPath = @"C:\Songs\mine\hard.osu";
            s.ExternalPackId = "sha256:aa";

            Assert.IsTrue(NetSongPublisher.AdoptToSession(s, Official(DancePrince, 0, "dance prince")));

            Assert.IsFalse(s.IsExternalSong);
            Assert.AreEqual("", s.ExternalChartPath);
        }

        [Test]
        public void Adopting_A_Random_Difficulty_Room_Keeps_The_Label()
        {
            // 隨機難度是**房間設定**:Title 是「隨機難度 3」標籤,gn 是已經抽好的那首。
            // 拿 gn 去查目錄把標題換成抽到的歌名 = 在房間面板上提前揭曉。
            var s = StaleSession();
            var roomSong = Official(DancePrince, 1, "隨機難度 3");
            roomSong.RandomTitle = true;

            Assert.IsTrue(NetSongPublisher.AdoptToSession(s, roomSong));

            Assert.IsTrue(s.SongIsRandom);
            Assert.AreEqual("隨機難度 3", s.SongTitle, "面板顯示的必須還是標籤,不是抽到的歌名");
            Assert.IsTrue(NetSongPublisher.SameRoomSelection(NetSongPublisher.FromSession(s), roomSong));
        }

        [Test]
        public void Adopting_A_Normal_Song_Drops_A_Stale_Random_Flag()
        {
            // 🔴 漏掉 SongIsRandom = false 的話:FromSession 會送出 randomTitle=true,
            // SameRoomSelection 先比這個旗標 → 永遠不相等 → 升房主後**每一份快照都重送一次**。
            var s = StaleSession();
            s.SongIsRandom = true;
            s.SongTitle = "隨機難度 5";

            var roomSong = Official(DancePrince, 0, "dance prince");
            Assert.IsTrue(NetSongPublisher.AdoptToSession(s, roomSong));

            Assert.IsFalse(s.SongIsRandom);
            Assert.IsTrue(NetSongPublisher.SameRoomSelection(NetSongPublisher.FromSession(s), roomSong));
        }

        [Test]
        public void Adopting_Is_Idempotent()
        {
            var s = StaleSession();
            var roomSong = Official(DancePrince, 2, "dance prince");

            Assert.IsTrue(NetSongPublisher.AdoptToSession(s, roomSong));
            Assert.IsTrue(NetSongPublisher.AdoptToSession(s, roomSong), "已經一致 → 直接回 true,不重寫 session");
            Assert.AreEqual(DancePrince, s.SongGn);
            Assert.AreEqual(Difficulty.Hard, s.Difficulty);
        }

        [Test]
        public void A_Missing_External_Song_Is_Not_Adopted()
        {
            // 缺歌:本機沒有那份譜,寫進 session 只會讓它指向不存在的檔。
            // 不收的那台就算升房主也不會覆蓋 server —— 進房的自動發布只在「房間沒歌」時才送。
            var s = StaleSession();
            var roomSong = new NetSongRef
            {
                Official = false, PackId = "sha256:0123456789abcdef", SongKey = "not-on-this-machine",
                ChartRelPath = "chart.osu", ChartIndex = 0, Difficulty = 1, Title = "someone else's song",
            };

            Assert.IsFalse(NetSongPublisher.AdoptToSession(s, roomSong));
            Assert.AreEqual(IceCream, s.SongGn, "沒收下時 session 一個欄位都不能動");
            Assert.AreEqual(Difficulty.Normal, s.Difficulty);
        }

        [Test]
        public void Nothing_To_Adopt_When_The_Room_Has_No_Song()
        {
            var s = StaleSession();

            Assert.IsFalse(NetSongPublisher.AdoptToSession(s, null));
            Assert.IsFalse(NetSongPublisher.AdoptToSession(s, new NetSongRef { Official = true, Gn = "" }));
            Assert.AreEqual(IceCream, s.SongGn);
        }
    }
}
