using NUnit.Framework;
using Sdo.UI.Core;

namespace Sdo.Tests
{
    public class GameSessionTests
    {
        private static readonly float[] Steps = { 1.0f, 1.5f, 2.0f, 2.5f, 3.0f, 4.0f, 5.0f, 6.0f, 8.0f };

        [Test]
        public void NearestSpeed_Exact_Match_Returns_Same()
        {
            Assert.AreEqual(2.5f, GameSession.NearestSpeed(Steps, 2.5f), 1e-4f);
            Assert.AreEqual(8.0f, GameSession.NearestSpeed(Steps, 8.0f), 1e-4f);
        }

        [Test]
        public void NearestSpeed_Snaps_To_Closest_Step()
        {
            Assert.AreEqual(2.5f, GameSession.NearestSpeed(Steps, 2.6f), 1e-4f);  // closer to 2.5 than 3.0
            Assert.AreEqual(4.0f, GameSession.NearestSpeed(Steps, 4.4f), 1e-4f);  // 4.0 vs 5.0
        }

        [Test]
        public void NearestSpeed_Out_Of_Range_Clamps_To_Ends()
        {
            Assert.AreEqual(1.0f, GameSession.NearestSpeed(Steps, 0.1f), 1e-4f);
            Assert.AreEqual(8.0f, GameSession.NearestSpeed(Steps, 99f), 1e-4f);
        }

        [Test]
        public void NearestSpeed_Empty_Or_Null_Returns_Want()
        {
            Assert.AreEqual(3.3f, GameSession.NearestSpeed(null, 3.3f), 1e-4f);
            Assert.AreEqual(3.3f, GameSession.NearestSpeed(new float[0], 3.3f), 1e-4f);
        }

        /// <summary>
        /// 換成官方歌 → 外部歌那一整組欄位要關掉。
        ///
        /// 這是實機那個「房主選 sdom2530k,旁觀者進去卻放到自己上次玩的外部歌」的回歸測試:
        /// FrontendApp.StartGameplay 只看 IsExternalSong 決定走 gn 還是 External* 那組值。
        /// </summary>
        [Test]
        public void SetOfficialSong_Clears_External_Song_State()
        {
            var s = new GameSession();
            // 先讓 session 處在「上次玩過一首外部歌」的狀態
            s.SongGn = "ext-12345";
            s.SongTitle = "Yes, my master my lord";
            s.IsExternalSong = true;
            s.ExternalChartPath = @"C:\Songs\yes\hard.osu";
            s.ExternalChartIndex = 2;
            s.ExternalChartFormat = 1;
            s.ExternalChartSeed = 999;
            s.ExternalDpsPath = @"C:\Songs\yes\gen.dps";
            s.ExternalAudioPath = @"C:\Songs\yes\audio.mp3";
            s.ExternalLevel = 12;
            s.ExternalFolderPath = @"C:\Songs\yes";
            s.ExternalSongKey = "yes";
            s.ExternalPackId = "abcdef";
            s.ExternalSongBpm = 175;
            s.ExternalSongChartPaths = new[] { "a", "b", "c" };
            s.ExternalSongChartIndices = new[] { 0, 1, 2 };

            s.SetOfficialSong("sdom2530k.gn", 2530, "官方歌", "官方作者");

            Assert.AreEqual("sdom2530k.gn", s.SongGn);
            Assert.AreEqual(2530, s.SongFileId);
            Assert.AreEqual("官方歌", s.SongTitle);
            Assert.AreEqual("官方作者", s.SongArtist);
            Assert.IsFalse(s.IsExternalSong, "沒關掉 → 進場會照舊放上一首外部歌");
            Assert.AreEqual("", s.ExternalChartPath);
            Assert.AreEqual(0, s.ExternalChartIndex);
            Assert.AreEqual(0, s.ExternalChartFormat);
            Assert.AreEqual(0, s.ExternalChartSeed);
            Assert.AreEqual("", s.ExternalDpsPath);
            Assert.AreEqual("", s.ExternalAudioPath);
            Assert.AreEqual(0, s.ExternalLevel);
            Assert.AreEqual("", s.ExternalFolderPath);
            Assert.AreEqual("", s.ExternalSongKey);
            Assert.AreEqual("", s.ExternalPackId);
            Assert.AreEqual(0, s.ExternalSongBpm, 1e-6);
            Assert.AreEqual(0, s.ExternalSongChartPaths.Length);
            Assert.AreEqual(0, s.ExternalSongChartIndices.Length);
        }

        /// <summary>顯示欄位傳 null = 不動(呼叫端查不到本機目錄時,寧可留舊的也不要清成空白)。</summary>
        [Test]
        public void SetOfficialSong_Null_Display_Fields_Keep_Previous()
        {
            var s = new GameSession { SongTitle = "舊歌名", SongArtist = "舊作者", IsExternalSong = true };
            s.SetOfficialSong("sdom0001k.gn", 1, null, null);
            Assert.AreEqual("舊歌名", s.SongTitle);
            Assert.AreEqual("舊作者", s.SongArtist);
            Assert.IsFalse(s.IsExternalSong);
        }

        [Test]
        public void Default_Session_Starts_On_Random_Scene_And_Free_Team()
        {
            var s = new GameSession();
            Assert.IsTrue(s.StageRandom);        // 房間第二層圖預設顯示 RANDOM
            Assert.AreEqual(3, s.Team);          // 組隊預設自由
            Assert.AreEqual(0, s.DropDirection); // 掉落方式預設向上
            Assert.AreEqual(0, s.GameMode);      // 預設自由模式
            Assert.AreEqual(-1, s.NoteType);     // note 種類預設隨機
        }
    }
}
