using NUnit.Framework;
using Sdo.Net;
using Sdo.UI.Services;

namespace Sdo.Tests
{
    /// <summary>
    /// 頭貼下緣那一條徽章的優先序:PLAYING &gt; NO MAP &gt; HOST &gt; READY。
    ///
    /// 為什麼值得一條測試:四張圖疊在**同一個位置**,所以「畫哪一張」是這條規則唯一的產出 ——
    /// 弄錯不會報錯,只會讓房主在場中時那格還寫著 HOST(而留在房間的人正是靠這格判斷「還要不要等他」)。
    /// 需求原文:NO MAP / PLAYING 這兩種狀態「在原本的 host / ready 權限之上」。
    /// </summary>
    public class RoomBadgeChoiceTests
    {
        private static RoomSeatBadge For(bool taken, bool isHost, bool isReady,
                                        PlayState play = PlayState.Idle,
                                        Availability avail = Availability.Have,
                                        bool isLocal = false)
            => RoomBadgeChoice.For(taken, isHost, isReady, play, avail, isLocal);

        [Test]
        public void An_Empty_Seat_Draws_Nothing()
        {
            // 空位就算 server 的殘留欄位長得像「房主又缺歌」也不畫 —— 沒有人的格子沒有狀態。
            Assert.AreEqual(RoomSeatBadge.None,
                For(false, isHost: true, isReady: true, PlayState.Playing, Availability.Missing));
        }

        [Test]
        public void The_Official_Two_Still_Work_On_Their_Own()
        {
            Assert.AreEqual(RoomSeatBadge.Host, For(true, isHost: true, isReady: false));
            Assert.AreEqual(RoomSeatBadge.Ready, For(true, isHost: false, isReady: true));
            Assert.AreEqual(RoomSeatBadge.None, For(true, isHost: false, isReady: false));
        }

        [Test]
        public void Host_Wins_Over_Ready()
        {
            // 房主沒有「準備」這個狀態(NetSeat.Ready 對房主恆 false),但真的兩個都來時要畫 HOST。
            Assert.AreEqual(RoomSeatBadge.Host, For(true, isHost: true, isReady: true));
        }

        [Test]
        public void No_Map_Wins_Over_Host_And_Ready()
        {
            Assert.AreEqual(RoomSeatBadge.NoMap,
                For(true, isHost: true, isReady: false, PlayState.Idle, Availability.Missing));
            Assert.AreEqual(RoomSeatBadge.NoMap,
                For(true, isHost: false, isReady: true, PlayState.Idle, Availability.Missing));
        }

        [Test]
        public void Playing_Wins_Over_Everything()
        {
            Assert.AreEqual(RoomSeatBadge.Playing,
                For(true, isHost: true, isReady: true, PlayState.Playing, Availability.Missing));
        }

        [Test]
        public void Downloading_And_Importing_Still_Count_As_No_Map()
        {
            // 🔴 回歸(實機:房裡一個人 no map + 已準備,房主一按開始就觸發上傳 → 對方開始下載 →
            //    那格從 NO MAP 翻成 READY,歌卻一個位元組都還沒進歌庫)。
            //    NO MAP 要一路掛到 have 為止 —— downloading / importing 期間他仍然打不了這首歌,
            //    而 R17 保留的 sticky Ready 會在徽章讓位的瞬間冒出來冒充「準備好了」。
            //    下載進度畫在另一條(RoomScreen.RenderSlotBar),兩者同時出現才是預期畫面。
            foreach (var a in new[] { Availability.Downloading, Availability.Importing })
            {
                Assert.AreEqual(RoomSeatBadge.NoMap,
                    For(true, isHost: false, isReady: true, PlayState.Idle, a),
                    a + ":sticky ready 不該冒充成 READY");
                Assert.AreEqual(RoomSeatBadge.NoMap,
                    For(true, isHost: true, isReady: false, PlayState.Idle, a),
                    a + ":房主自己在下載時也還沒有這首歌");
            }
        }

        [Test]
        public void Unknown_Is_Not_No_Map()
        {
            // Unknown = 還沒回報(換歌會把全房打回 unknown,R9),只有一瞬。
            // 當成缺歌的話,每次房主換歌全房徽章都會閃一下 NO MAP。
            Assert.AreEqual(RoomSeatBadge.Ready,
                For(true, isHost: false, isReady: true, PlayState.Idle, Availability.Unknown));
            Assert.AreEqual(RoomSeatBadge.Host,
                For(true, isHost: true, isReady: false, PlayState.Idle, Availability.Unknown));
        }

        [Test]
        public void Every_State_From_Start_To_Results_Counts_As_Playing()
        {
            // 從被納入載入到停在結算畫面,那個人都還沒回房間 → 那一格要一路畫 PLAYING。
            foreach (var s in new[]
                     {
                         PlayState.WaitingForLoad, PlayState.Loaded, PlayState.ReadyForGameplay,
                         PlayState.Playing, PlayState.Finished, PlayState.Results,
                     })
            {
                Assert.IsTrue(RoomBadgeChoice.IsInMatch(s), s + " 應該算在場中");
                Assert.AreEqual(RoomSeatBadge.Playing, For(true, false, false, s), s + " 那格應該畫 PLAYING");
            }
        }

        [Test]
        public void My_Own_Slot_Never_Draws_Playing()
        {
            // 🔴 「我看得到房間」就代表我已經不在場上了。中離(Esc)或打完先回房時 server 把我標成
            //    finished/results,那要等**全場**都打完才會被 ClearResults 打回 idle —— 中間那段
            //    (別人還在跳完整首歌)我人就站在房間裡走動,那一格卻掛著 PLAYING。
            //    使用者的需求原話:「自己照理來說不會看到 playing」。
            foreach (var s in new[]
                     {
                         PlayState.WaitingForLoad, PlayState.Loaded, PlayState.ReadyForGameplay,
                         PlayState.Playing, PlayState.Finished, PlayState.Results,
                     })
                Assert.AreNotEqual(RoomSeatBadge.Playing, For(true, false, false, s, isLocal: true),
                                   s + ":自己那格不該畫 PLAYING");

            // 但**別人**那格照畫 —— 那正是留在房間的人需要的資訊(還要不要等他)。
            Assert.AreEqual(RoomSeatBadge.Playing, For(true, false, false, PlayState.Finished));
        }

        [Test]
        public void My_Own_Slot_Still_Shows_Everything_Else()
        {
            // 本機例外只吃掉 PLAYING 那一張,其餘三張照原本的優先序走 ——
            // 不然自己回房後會連「我是房主」「我缺這首歌」都看不到。
            Assert.AreEqual(RoomSeatBadge.Host,
                For(true, isHost: true, isReady: false, PlayState.Finished, Availability.Have, isLocal: true));
            Assert.AreEqual(RoomSeatBadge.NoMap,
                For(true, isHost: false, isReady: false, PlayState.Results, Availability.Missing, isLocal: true));
            Assert.AreEqual(RoomSeatBadge.Ready,
                For(true, isHost: false, isReady: true, PlayState.Idle, Availability.Have, isLocal: true));
        }

        [Test]
        public void Idle_And_Ready_And_Spectating_Are_Not_Playing()
        {
            // 🔴 Results 與 NetState.IsInMatch 的差異是刻意的(見 RoomBadgeChoice.IsInMatch 的註解),
            //    但 Idle / Ready / Spectating 兩邊一致:旁觀的人坐在房間裡,不該頂著 PLAYING。
            foreach (var s in new[] { PlayState.Idle, PlayState.Ready, PlayState.Spectating })
                Assert.IsFalse(RoomBadgeChoice.IsInMatch(s), s + " 不該算在場中");
        }
    }
}
