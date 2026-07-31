using System.IO;
using NUnit.Framework;
using Sdo.Game;
using Sdo.Settings;

namespace Sdo.Tests
{
    public class RoomConfigSongFolderTests
    {
        [Test]
        public void ParseStringList_Trims_Drops_Empty_Normalises_Slashes()
        {
            var r = RoomConfig.ParseStringList(@"D:/a, E:/b ,, C:\x\y ");
            Assert.AreEqual(new[] { "D:/a", "E:/b", "C:/x/y" }, r);
        }

        [Test]
        public void ParseStringList_Empty_Or_Null_Is_Empty()
        {
            Assert.AreEqual(0, RoomConfig.ParseStringList("").Length);
            Assert.AreEqual(0, RoomConfig.ParseStringList(null).Length);
            Assert.AreEqual(0, RoomConfig.ParseStringList("  ,  ,").Length);
        }

        [Test]
        public void ParseStringList_Splits_On_Semicolons()
        {
            var r = RoomConfig.ParseStringList("D:/a; E:/b ; C:/c");
            Assert.AreEqual(new[] { "D:/a", "E:/b", "C:/c" }, r);
        }

        [Test]
        public void ParseStringList_Accepts_Both_Semicolons_And_Commas()
        {
            var r = RoomConfig.ParseStringList("D:/a;E:/b,F:/c");
            Assert.AreEqual(new[] { "D:/a", "E:/b", "F:/c" }, r);
        }

        [Test]
        public void ParseStringList_Strips_Leading_Slashes_Before_A_Drive()
        {
            // The StepMania-style config the user pastes: leading slashes in front of the drive are noise.
            var r = RoomConfig.ParseStringList("////D:/StepMania/Songs");
            Assert.AreEqual(new[] { "D:/StepMania/Songs" }, r);
        }

        [Test]
        public void ParseStringList_Leaves_A_Unc_Path_Alone()
        {
            var r = RoomConfig.ParseStringList(@"\\server\share\Songs");
            Assert.AreEqual(new[] { "//server/share/Songs" }, r);
        }

        [Test]
        public void ParseInto_Reads_AdditionalSongFolders()
        {
            RoomConfig.additionalSongFolders = new string[0];
            RoomConfig.ParseInto("AdditionalSongFolders=D:/test,E:/more\n");
            Assert.AreEqual(new[] { "D:/test", "E:/more" }, RoomConfig.additionalSongFolders);
            RoomConfig.additionalSongFolders = new string[0];   // leave clean for other tests
        }

        [Test]
        public void ParseInto_Reads_Semicolon_AdditionalSongFolders_With_Leading_Slashes()
        {
            RoomConfig.additionalSongFolders = new string[0];
            RoomConfig.ParseInto("AdditionalSongFolders=////D:/test;E:/more\n");
            Assert.AreEqual(new[] { "D:/test", "E:/more" }, RoomConfig.additionalSongFolders);
            RoomConfig.additionalSongFolders = new string[0];   // leave clean for other tests
        }

        [Test]
        public void ParseInto_Reads_AddonFolder()
        {
            RoomConfig.addonFolder = "";
            RoomConfig.ParseInto("AddonFolder=////D:/SdoAddon\n");
            Assert.AreEqual("D:/SdoAddon", RoomConfig.addonFolder, "same cleaning as the song folders (drive-prefix slashes stripped)");
            RoomConfig.addonFolder = "";   // leave clean for other tests
        }

        [Test]
        public void AddonDir_Defaults_Under_Root_When_No_Config_Override()
        {
            RoomConfig.addonFolder = "";
            Assert.AreEqual(Path.Combine(SdoExtracted.Root, "ADDON"), SdoExtracted.AddonDir);
            StringAssert.EndsWith(Path.Combine("ADDON", "SONG"), SdoExtracted.AddonSongsDir);
        }

        [Test]
        public void AddonDir_Honours_The_Config_Override()
        {
            RoomConfig.addonFolder = "D:/SdoAddon";
            // The whole plugin tree relocates under the custom folder — SONG lives directly beneath it.
            Assert.AreEqual(Path.GetFullPath("D:/SdoAddon"), SdoExtracted.AddonDir);
            StringAssert.EndsWith(Path.Combine("SdoAddon", "SONG"), SdoExtracted.AddonSongsDir);
            RoomConfig.addonFolder = "";   // leave clean for other tests
        }

        // 外部歌「分類瀏覽」面板不透明度（config.ini 的 SongUiAlpha，預設 0.6）。
        [Test]
        public void ParseInto_Reads_SongUiAlpha()
        {
            RoomConfig.songUiAlpha = 0.6f;
            RoomConfig.ParseInto("SongUiAlpha=0.35\n");
            Assert.AreEqual(0.35f, RoomConfig.songUiAlpha, 1e-4f);
            RoomConfig.songUiAlpha = 0.6f;   // leave clean for other tests
        }

        [Test]
        public void Sanitize_Clamps_SongUiAlpha_To_0_1()
        {
            RoomConfig.songUiAlpha = 2f;  RoomConfig.Sanitize();  Assert.AreEqual(1f, RoomConfig.songUiAlpha, 1e-4f);
            RoomConfig.songUiAlpha = -1f; RoomConfig.Sanitize();  Assert.AreEqual(0f, RoomConfig.songUiAlpha, 1e-4f);
            RoomConfig.songUiAlpha = 0.6f;   // leave clean for other tests
        }

        [Test]
        public void Serialize_RoundTrips_SongUiAlpha()
        {
            RoomConfig.songUiAlpha = 0.42f;
            string ini = RoomConfig.Serialize();
            StringAssert.Contains("SongUiAlpha=", ini);
            RoomConfig.songUiAlpha = 0.6f;                 // clobber, then read the serialized value back
            RoomConfig.ParseInto(ini);
            Assert.AreEqual(0.42f, RoomConfig.songUiAlpha, 1e-3f);
            RoomConfig.songUiAlpha = 0.6f;   // leave clean for other tests
        }

        // ---- LoadExternalSongs：外部歌曲(osu/StepMania/Malody)載入總開關 ----

        [Test]
        public void Old_Config_Without_The_Key_Keeps_External_Songs_On()
        {
            // 升級時的關鍵行為：舊 config.ini 沒有這個鍵 → 維持「載入」，不能讓既有玩家的外部歌憑空消失。
            RoomConfig.loadExternalSongs = true;
            RoomConfig.ParseInto("AdditionalSongFolders=D:/test\n");   // 一份沒有 LoadExternalSongs 的舊檔
            Assert.IsTrue(RoomConfig.loadExternalSongs);
            RoomConfig.additionalSongFolders = new string[0];   // leave clean for other tests
        }

        [Test]
        public void ParseInto_Reads_LoadExternalSongs()
        {
            RoomConfig.ParseInto("LoadExternalSongs=0\n");
            Assert.IsFalse(RoomConfig.loadExternalSongs);
            RoomConfig.ParseInto("LoadExternalSongs=1\n");
            Assert.IsTrue(RoomConfig.loadExternalSongs);
        }

        [Test]
        public void Serialize_RoundTrips_LoadExternalSongs()
        {
            RoomConfig.loadExternalSongs = false;
            string ini = RoomConfig.Serialize();
            StringAssert.Contains("LoadExternalSongs=0", ini);
            RoomConfig.loadExternalSongs = true;               // clobber, then read the serialized value back
            RoomConfig.ParseInto(ini);
            Assert.IsFalse(RoomConfig.loadExternalSongs);
            RoomConfig.loadExternalSongs = true;   // leave clean for other tests
        }

        [Test]
        public void Schema_Upgrade_Notices_A_Missing_LoadExternalSongs()
        {
            // 舊 config.ini 沒有這個鍵 → Load() 的升級路徑要補寫一次，玩家才找得到可改的鍵。
            string full = RoomConfig.Serialize();
            var lines = full.Split('\n');
            var trimmed = string.Join("\n", System.Array.FindAll(lines, l => !l.TrimStart().StartsWith("LoadExternalSongs=")));
            Assert.IsTrue(RoomConfig.IsMissingCurrentKey(trimmed), "少了 LoadExternalSongs 的舊檔應被判為需要補寫");
        }

        // 關掉之後真的一個資料夾都不掃：掃描協程第一次 MoveNext 就結束（沒有起 worker、沒碰磁碟），
        // 且進度回報直接跳 1（開機/更新兩邊的載入畫面才不會卡在 0%）。這是唯一的守門點。
        [Test]
        public void ScanAndRegister_Is_A_No_Op_When_Loading_Is_Off()
        {
            bool prev = RoomConfig.loadExternalSongs;
            try
            {
                RoomConfig.loadExternalSongs = false;
                float reported = -1f;
                var it = ExternalSongLibrary.ScanAndRegisterCo((f, folder, detail) => reported = f);
                Assert.IsFalse(it.MoveNext(), "關掉時協程不該有任何一步要等（沒掃描可跑）");
                Assert.AreEqual(1f, reported, 1e-4f, "進度應直接回報完成");
                Assert.IsFalse(ExternalSongLibrary.Scanning, "不該起掃描 worker");
            }
            finally { RoomConfig.loadExternalSongs = prev; }
        }

        [Test]
        public void Schema_Upgrade_Notices_A_Missing_SongUiAlpha()
        {
            // 完整 config 不缺 key；把 SongUiAlpha 那行單獨拿掉後就該被判為需要補寫（證明它確實在 schema 裡，
            // 缺了會被 Load() 的升級路徑補回）。
            string full = RoomConfig.Serialize();
            Assert.IsFalse(RoomConfig.IsMissingCurrentKey(full), "完整序列化不該缺任何 key");
            var lines = full.Split('\n');
            var trimmed = string.Join("\n", System.Array.FindAll(lines, l => !l.TrimStart().StartsWith("SongUiAlpha=")));
            Assert.IsTrue(RoomConfig.IsMissingCurrentKey(trimmed), "少了 SongUiAlpha 的舊檔應被判為需要補寫");
        }
    }
}
