using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine.TestTools;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// StreamingAssets/song_name_overrides.json 的套用規則（SongCatalog.ApplyOverrides）。
    /// 覆蓋「顯示值」title / artist / bpm，外加一個真的會進遊戲的 offsetMs（每首歌的音訊校正）。
    /// 它不決定歌單內容 —— 歌單來自 song_catalog.json，所以沒列在清單裡的歌照樣存在，只是沿用 .gn 內嵌名。
    /// </summary>
    public class SongCatalogOverrideTests
    {
        private static List<SongCatalog.Entry> Sample() => new List<SongCatalog.Entry>
        {
            new SongCatalog.Entry { gn = "sdom0001k.gn", fileId = 10001, title = "kgn名", artist = "kgn歌手", bpm = 120f },
            new SongCatalog.Entry { gn = "sdom0001t.gn", fileId = 10001, title = "kgn名", artist = "kgn歌手", bpm = 120f },
            new SongCatalog.Entry { gn = "sdom0002k.gn", fileId = 10002, title = "沒列到", artist = "原歌手", bpm = 99f },
        };

        private const string Json = @"{""songs"":[{""gn"":""sdom0001"",""title"":""正確歌名"",""artist"":""正確歌手"",""bpm"":174.5}]}";

        [Test]
        public void Overrides_Replace_Title_Artist_And_Bpm_For_Both_Charts()
        {
            var list = Sample();
            SongCatalog.ApplyOverrides(list, Json);

            foreach (var e in new[] { list[0], list[1] })   // K/T 共用一筆 override（key = 去掉 k/t 的詞幹）
            {
                Assert.AreEqual("正確歌名", e.title);
                Assert.AreEqual("正確歌手", e.artist);
                Assert.AreEqual(174.5f, e.bpm, 0.001f);
            }
        }

        [Test]
        public void Song_Missing_From_Overrides_Keeps_Its_Kgn_Values()
        {
            var list = Sample();
            SongCatalog.ApplyOverrides(list, Json);

            Assert.AreEqual("沒列到", list[2].title);      // 沒列到 ≠ 被跳過：這首歌仍在歌單裡
            Assert.AreEqual("原歌手", list[2].artist);
            Assert.AreEqual(99f, list[2].bpm, 0.001f);
        }

        [Test]
        public void Blank_Title_Artist_And_NonPositive_Bpm_Leave_The_Chart_Values_Alone()
        {
            var list = Sample();
            SongCatalog.ApplyOverrides(list, @"{""songs"":[{""gn"":""sdom0001"",""title"":"""",""artist"":"""",""bpm"":0}]}");

            Assert.AreEqual("kgn名", list[0].title);
            Assert.AreEqual("kgn歌手", list[0].artist);
            Assert.AreEqual(120f, list[0].bpm, 0.001f);
        }

        [Test]
        public void Bpm_Only_Override_Keeps_The_Names()
        {
            var list = Sample();
            SongCatalog.ApplyOverrides(list, @"{""songs"":[{""gn"":""sdom0001"",""bpm"":88}]}");

            Assert.AreEqual(88f, list[0].bpm, 0.001f);
            Assert.AreEqual("kgn名", list[0].title);
        }

        /// <summary>offsetMs 是唯一會影響遊戲本身的覆蓋欄：每首歌的音訊校正（正 = 音樂晚一點進來）。
        /// 沒填 / 0 = 不位移；K/T 兩譜共用同一筆。</summary>
        [Test]
        public void OffsetMs_Applies_To_Both_Charts_And_Defaults_To_Zero()
        {
            var list = Sample();
            SongCatalog.ApplyOverrides(list, @"{""songs"":[{""gn"":""sdom0001"",""offsetMs"":-42.5}]}");

            Assert.AreEqual(-42.5f, list[0].offsetMs, 0.001f);
            Assert.AreEqual(-42.5f, list[1].offsetMs, 0.001f);
            Assert.AreEqual(0f, list[2].offsetMs, 0.001f);          // 沒列到 → 不位移
        }

        [Test]
        public void OffsetMs_Absent_From_The_Row_Means_No_Shift()
        {
            var list = Sample();
            SongCatalog.ApplyOverrides(list, Json);                  // 舊格式的一列（沒有 offsetMs 欄）
            Assert.AreEqual(0f, list[0].offsetMs, 0.001f);
        }

        /// <summary>手滑多打一個 0（30 → 300000）不該把音樂推到歌曲之外 —— 夾在 ±MaxOffsetMs。</summary>
        [Test]
        public void Absurd_OffsetMs_Is_Clamped()
        {
            var list = Sample();
            SongCatalog.ApplyOverrides(list, @"{""songs"":[{""gn"":""sdom0001"",""offsetMs"":300000}]}");
            Assert.AreEqual(SongCatalog.MaxOffsetMs, list[0].offsetMs, 0.001f);

            list = Sample();
            SongCatalog.ApplyOverrides(list, @"{""songs"":[{""gn"":""sdom0001"",""offsetMs"":-300000}]}");
            Assert.AreEqual(-SongCatalog.MaxOffsetMs, list[0].offsetMs, 0.001f);
        }

        /// <summary>主音樂 .ogg 一律由 gn 詞幹決定（開局與選歌試聽共用同一支，見 SongCatalog.MainOggName）。
        /// 重點是 sdom1234_1k.gn（號碼撞號時手動插隊用的名字）要指向自己的 sdom1234_1.ogg，
        /// 而不是原本 sdom1234 那首的音樂。</summary>
        [TestCase("sdom1197k.gn", "sdom1197.ogg")]
        [TestCase("sdom1197T.GN", "sdom1197.ogg")]
        [TestCase("H:/music/SDOM1197K.gn", "sdom1197.ogg")]
        [TestCase("sdom1234_1k.gn", "sdom1234_1.ogg")]
        [TestCase("sdom0.gn", "sdom0.ogg")]
        [TestCase("", null)]
        [TestCase(null, null)]
        public void MainOggName_Comes_From_The_Gn_Stem(string gn, string expected)
            => Assert.AreEqual(expected, SongCatalog.MainOggName(gn));

        /// <summary>開局(FrontendApp/譜面編輯器)走 SongPaths.Ogg，選歌試聽走 SongCatalog.MainOggName —— 兩邊
        /// 必須解到同一個檔名。SongPaths 早期自己用 <c>sdom\d+</c> regex 取號，遇到撞號插隊的
        /// sdom1234_1k.gn 會在底線斷掉、開局放成 sdom1234.ogg（原本那首的音樂），試聽卻是對的。
        /// 只比對檔名，不比對目錄（目錄隨 data root 變）。</summary>
        [TestCase("sdom1197k.gn", "sdom1197.ogg")]
        [TestCase("sdom1197T.GN", "sdom1197.ogg")]
        [TestCase("sdom1234_1k.gn", "sdom1234_1.ogg")]
        [TestCase("sdom1116_1k.gn", "sdom1116_1.ogg")]
        public void SongPaths_Ogg_Agrees_With_MainOggName(string gn, string expected)
        {
            Assert.AreEqual(expected, System.IO.Path.GetFileName(SongPaths.Ogg(gn)));
            Assert.AreEqual(SongCatalog.MainOggName(gn), System.IO.Path.GetFileName(SongPaths.Ogg(gn)));
        }

        [TestCase("")]
        [TestCase(null)]
        public void SongPaths_Ogg_Is_Null_For_Empty_Name(string gn)
            => Assert.IsNull(SongPaths.Ogg(gn));

        [Test]
        public void Empty_Or_Malformed_Json_Is_A_NoOp()
        {
            LogAssert.ignoreFailingMessages = true;   // 壞檔會 LogError（見 ApplyOverrides 的 catch），那是預期行為
            try
            {
                foreach (var json in new[] { null, "", "   ", "{}", @"{""songs"":[]}", "not json at all" })
                {
                    var list = Sample();
                    SongCatalog.ApplyOverrides(list, json);
                    Assert.AreEqual("kgn名", list[0].title, $"json={json ?? "null"}");
                    Assert.AreEqual(120f, list[0].bpm, 0.001f, $"json={json ?? "null"}");
                }
            }
            finally { LogAssert.ignoreFailingMessages = false; }
        }
    }
}
