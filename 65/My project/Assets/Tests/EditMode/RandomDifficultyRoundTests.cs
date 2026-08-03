using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Game;
using Sdo.Net;
using Sdo.UI.Catalog;
using Sdo.UI.Core;

namespace Sdo.Tests
{
    /// <summary>
    /// 「隨機難度」這一局的三件事:抽哪一首(池)、怎麼套進 session、以及**不要**因此把房間當成換歌。
    ///
    /// 為什麼三件一起放:它們是同一個需求的三段 —— 隨機難度是**房間設定**(房間永遠只顯示
    /// 「隨機難度 X」標籤),每一局開場才抽一首真的歌來打,而抽的結果不能回頭改動房間設定。
    /// </summary>
    public class RandomDifficultyRoundTests
    {
        private static List<SongCatalog.Entry> Pool() => new List<SongCatalog.Entry>
        {
            // 官方歌(external=false):notesXxx > 0 才算「這個難度有譜」(HasChart)
            new SongCatalog.Entry { gn = "sdom0001k.gn", fileId = 1, title = "官方一", artist = "A",
                                    diffEasy = 4, notesEasy = 300 },
            new SongCatalog.Entry { gn = "sdom0002k.gn", fileId = 2, title = "官方二", artist = "B",
                                    diffNormal = 4, notesNormal = 500 },
            // 外部歌:同一個等級區間,但別人不一定有
            new SongCatalog.Entry { gn = "ext_abcd.gn", fileId = -1001, title = "外部一", artist = "C", external = true,
                                    diffEasy = 4, notesEasy = 400, chartFormat = 1,
                                    chartEasy = @"C:\Songs\x\easy.osu", folderPath = @"C:\Songs\x",
                                    audioPath = @"C:\Songs\x\a.mp3", packId = "sha256:ff", songKey = "x", bpm = 175 },
        };

        /// <summary>RandRanges[0] = 難度 1..5 —— 三首都在範圍內。</summary>
        private const int Range1To5 = 0;

        [Test]
        public void Offline_Pool_Includes_External_Songs()
        {
            var pool = SongListModel.RandomCandidates(Pool(), Range1To5);
            Assert.AreEqual(3, pool.Count, "離線抽自己的歌庫,外部歌本來就該抽得到");
        }

        [Test]
        public void Online_Pool_Is_Official_Only()
        {
            // 🔴 抽出來的歌不會再過缺歌檢查(大家的 avail 是對「房間那首」回報的)→ 線上只能抽大家一定都有的官方歌。
            var pool = SongListModel.RandomCandidates(Pool(), Range1To5, officialOnly: true);
            Assert.AreEqual(2, pool.Count);
            foreach (var c in pool) Assert.IsFalse(c.Song.external, "線上抽到外部歌 → 別人載不到譜,卡在載入畫面等逾時");
        }

        [Test]
        public void Rerolling_From_External_To_Official_Clears_The_External_Fields()
        {
            // 上一局打的是外部歌 → 這一局抽到官方歌。只寫 gn 的話 IsExternalSong 還是 true,
            // 進場會照舊放上一首外部歌的譜與音檔(這正是每局重抽以前的寫法)。
            var s = new GameSession();
            s.SetSongFromCatalog(Pool()[2], 0);
            Assert.IsTrue(s.IsExternalSong);

            s.SetSongFromCatalog(Pool()[1], 1, keepTitle: true);
            Assert.IsFalse(s.IsExternalSong);
            Assert.AreEqual("sdom0002k.gn", s.SongGn);
            Assert.AreEqual(2, s.SongFileId);
            Assert.AreEqual("", s.ExternalChartPath);
            Assert.AreEqual("", s.ExternalAudioPath);
            Assert.AreEqual("", s.ExternalPackId);
            Assert.AreEqual(Difficulty.Normal, s.Difficulty);
        }

        [Test]
        public void Rerolling_From_Official_To_External_Fills_The_Whole_External_Set()
        {
            // 反方向:官方歌 → 外部歌。少填一個欄位就是「載不到譜」或「跳到別首歌的舞」。
            var s = new GameSession();
            s.SetSongFromCatalog(Pool()[0], 0);
            s.SetSongFromCatalog(Pool()[2], 0, keepTitle: true);

            Assert.IsTrue(s.IsExternalSong);
            Assert.AreEqual(@"C:\Songs\x\easy.osu", s.ExternalChartPath);
            Assert.AreEqual(@"C:\Songs\x\a.mp3", s.ExternalAudioPath);
            Assert.AreEqual(@"C:\Songs\x", s.ExternalFolderPath);
            Assert.AreEqual("sha256:ff", s.ExternalPackId, "生成舞蹈的 seed —— 空的話兩台會跳不同的舞");
            Assert.AreEqual("x", s.ExternalSongKey);
            Assert.AreEqual(1, s.ExternalChartFormat);
            Assert.AreEqual(175, s.ExternalSongBpm, 1e-6);
            Assert.AreEqual(3, s.ExternalSongChartPaths.Length, "生成編舞要量所有難度的頭尾");
        }

        [Test]
        public void KeepTitle_Preserves_The_Random_Label()
        {
            // 房間顯示的是「隨機難度 3」,不是抽到的歌名 —— 蓋掉就等於提前揭曉。
            var s = new GameSession { SongTitle = "隨機難度 3", SongIsRandom = true };
            s.SetSongFromCatalog(Pool()[0], 0, keepTitle: true);
            Assert.AreEqual("隨機難度 3", s.SongTitle);
            Assert.AreEqual("sdom0001k.gn", s.SongGn, "標籤留著,但真的要打的那首歌換了");

            s.SetSongFromCatalog(Pool()[1], 1);   // keepTitle: false(一般選歌)→ 顯示真歌名
            Assert.AreEqual("官方二", s.SongTitle);
        }

        [Test]
        public void A_Fresh_Random_Pick_Is_Not_A_Room_Song_Change()
        {
            // 房主打完一局回房:session 裡的 gn 已經是這一局抽到的那首,而 server 手上還是上一首。
            // 送出去會被當成換歌 → 全房的準備與 avail 被清掉(R9)。房間設定其實沒變。
            var current = new NetSongRef { Official = true, Gn = "sdom0001k.gn", Title = "隨機難度 3", RandomTitle = true };
            var want = new NetSongRef { Official = true, Gn = "sdom0002k.gn", Title = "隨機難度 3", RandomTitle = true };
            Assert.IsTrue(NetSongPublisher.SameRoomSelection(want, current));
        }

        [Test]
        public void Changing_The_Random_Range_Really_Is_A_Change()
        {
            // 🔴 gn 故意相同:重抽剛好又抽到同一首歌時就是這個樣子。先問「同一張譜嗎」的話,
            // 「隨機難度 3 → 隨機難度 5」會被當成沒變 → server 手上的標籤永遠停在舊區間。
            var current = new NetSongRef { Official = true, Gn = "sdom0001k.gn", Title = "隨機難度 3", RandomTitle = true };
            var want = new NetSongRef { Official = true, Gn = "sdom0001k.gn", Title = "隨機難度 5", RandomTitle = true };
            Assert.IsFalse(NetSongPublisher.SameRoomSelection(want, current), "換難度區間是真的改了房間設定,一定要送");
        }

        [Test]
        public void Switching_Away_From_Random_Is_A_Change()
        {
            var current = new NetSongRef { Official = true, Gn = "sdom0001k.gn", Title = "隨機難度 3", RandomTitle = true };
            var want = new NetSongRef { Official = true, Gn = "sdom0009k.gn", Title = "指定的那首", RandomTitle = false };
            Assert.IsFalse(NetSongPublisher.SameRoomSelection(want, current));
            // 反過來也一樣(從指定歌改成隨機難度)
            Assert.IsFalse(NetSongPublisher.SameRoomSelection(current, want));
        }

        [Test]
        public void The_Same_Chart_Is_Still_The_Same_Selection()
        {
            var a = new NetSongRef { Official = true, Gn = "sdom0001k.gn", Title = "官方一" };
            var b = new NetSongRef { Official = true, Gn = "sdom0001k.gn", Title = "官方一(簡繁不同)" };
            Assert.IsTrue(NetSongPublisher.SameRoomSelection(a, b), "識別看 gn,顯示欄位不算");
        }
    }
}
