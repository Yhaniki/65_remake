using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// <see cref="SongTable"/> —— StreamingAssets/song_table.csv 的解析（全部歌曲資料的唯一來源）。
    ///
    /// 這份表整併掉了以前的四份 JSON（gn_header_catalog / gn_keytable / song_catalog /
    /// song_name_overrides）。解析壞掉的後果是整個歌單消失或整排欄位錯位，所以這裡把
    /// 「CSV 真的會遇到的鳥事」全部釘住：歌名裡有逗號、有引號、表頭欄位順序被調過、
    /// 少了幾欄、CRLF、BOM。欄位一律**照表頭名字**取，不是照位置。
    /// </summary>
    public class SongTableTests
    {
        private const string Header =
            "fileId,title,artist,producer,gn,mode,bpm,offsetMs,src,lvEasy,lvNormal,lvHard," +
            "notesEasy,notesNormal,notesHard,durEasy,durNormal,durHard,measEasy,measNormal,measHard," +
            "chartBpm,titleZhCn,artistZhCn,titleEn,artistEn,origName,enc,seed,seed1,seed2,innerOff,size\n";

        private const string RowK =
            "10001,野蠻遊戲,蔡依林,YO!610,sdom0001k.gn,K,139,-25,songname,3,4,5,510,598,744," +
            "188,188,188,357,384,417,139,野蛮游戏,蔡依林,,,sdom0001K.gm,sdom,9458173,,,456,78312\n";

        private const string RowT =
            "1,野蠻遊戲,蔡依林,Yo!610,sdom0001t.gn,T,139,-25,songname,1,3,5,284,448,682," +
            "188,188,188,862,869,864,139,野蛮游戏,蔡依林,,,sdom0001T.gm,sdom,14955976,,,456,499560\n";

        [Test]
        public void Parses_Every_Column_Of_A_Real_Row()
        {
            var rows = SongTable.Parse(Header + RowK);
            Assert.AreEqual(1, rows.Count);
            var r = rows[0];

            Assert.AreEqual("sdom0001k.gn", r.gn);
            Assert.AreEqual(10001, r.fileId);
            Assert.AreEqual("野蠻遊戲", r.title);
            Assert.AreEqual("蔡依林", r.artist);
            Assert.AreEqual("YO!610", r.producer);
            Assert.AreEqual("K", r.mode);
            Assert.AreEqual(139f, r.bpm, 0.001f);
            Assert.AreEqual(-25f, r.offsetMs, 0.001f);
            Assert.AreEqual(new[] { 3, 4, 5 }, r.levels);
            Assert.AreEqual(new[] { 510, 598, 744 }, r.noteCounts);
            Assert.AreEqual(new[] { 188, 188, 188 }, r.durations);
            Assert.AreEqual(new[] { 357, 384, 417 }, r.measurements);
            Assert.AreEqual("野蛮游戏", r.titleZhCn);          // 原文（簡中）仍在，UI 要切語言用得到
            Assert.AreEqual("sdom0001K.gm", r.origName);
            Assert.AreEqual("sdom", r.enc);
            Assert.AreEqual(9458173L, r.seed);
            Assert.AreEqual(456, r.innerOff);
            Assert.AreEqual(78312, r.size);
        }

        /// <summary>歌名裡有逗號是真的會發生的（"Yes, I do"）。照 Split(',') 切會把歌名切兩半、
        /// 後面每一欄一路錯位 —— 難度變成歌手、seed 變成小節數，整首歌就解不開了。</summary>
        [Test]
        public void Quoted_Field_Keeps_Commas_And_Quotes_Together()
        {
            var csv = "gn,title,artist,seed\n" +
                      "sdom9k.gn,\"Yes, I do\",\"He said \"\"hi\"\"\",42\n";
            var r = SongTable.Parse(csv)[0];
            Assert.AreEqual("Yes, I do", r.title);
            Assert.AreEqual("He said \"hi\"", r.artist);
            Assert.AreEqual(42L, r.seed);       // 錯位的話這格會變成別的東西
        }

        [Test]
        public void Columns_Are_Matched_By_Name_Not_Position()
        {
            var csv = "seed,gn,artist,title\n7,sdom9k.gn,歌手,歌名\n";
            var r = SongTable.Parse(csv)[0];
            Assert.AreEqual("sdom9k.gn", r.gn);
            Assert.AreEqual("歌名", r.title);
            Assert.AreEqual("歌手", r.artist);
            Assert.AreEqual(7L, r.seed);
        }

        [Test]
        public void Missing_Columns_And_Blank_Cells_Fall_Back_To_Defaults()
        {
            var r = SongTable.Parse("gn,title,bpm,offsetMs\nsdom9k.gn,歌名,,\n")[0];
            Assert.AreEqual(-1f, r.bpm, 0.001f);       // 沒有 BPM = -1（不是 0，0 會被當成真的 0 BPM）
            Assert.AreEqual(0f, r.offsetMs, 0.001f);   // 沒有 offset = 不位移
            Assert.AreEqual(new[] { -1, -1, -1 }, r.levels);
            Assert.AreEqual(new[] { 0, 0, 0 }, r.noteCounts);
            Assert.AreEqual("", r.enc);
        }

        [TestCase("\n")]
        [TestCase("\r\n")]
        public void Handles_Both_Line_Endings_And_A_Bom(string eol)
        {
            var csv = "﻿gn,title\nsdom9k.gn,歌名\nsdom9t.gn,歌名\n".Replace("\n", eol);
            var rows = SongTable.Parse(csv);
            Assert.AreEqual(2, rows.Count);
            Assert.AreEqual("sdom9k.gn", rows[0].gn);          // BOM 沒被吃進第一個欄位名 → 查得到 gn 欄
            Assert.AreEqual("歌名", rows[1].title);
        }

        [Test]
        public void Gn_Is_Lowercased_And_Rows_Without_It_Are_Dropped()
        {
            var rows = SongTable.Parse("gn,title\nSDOM9K.GN,歌名\n,沒有gn\n");
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual("sdom9k.gn", rows[0].gn);
        }

        [TestCase("")]
        [TestCase(null)]
        [TestCase("這不是 CSV")]                    // 沒有 gn 欄 → 不是歌曲表，不要瞎解
        public void Junk_Input_Yields_No_Rows(string csv)
            => Assert.AreEqual(0, SongTable.Parse(csv).Count);

        // ─────────────────── 視圖：三個舊 catalog 現在都是這張表的投影 ───────────────────

        [Test]
        public void SongCatalog_Entry_Comes_From_The_Row()
        {
            var r = SongTable.Parse(Header + RowK)[0];
            var e = SongCatalog.FromRow(r);

            Assert.AreEqual("sdom0001k.gn", e.gn);
            Assert.AreEqual(10001, e.fileId);
            Assert.AreEqual("野蠻遊戲", e.title);
            Assert.AreEqual(5, e.Diff(2));
            Assert.AreEqual(510, e.NoteCount(0));
            Assert.AreEqual(188, e.DurationSec(1));
            Assert.AreEqual(-25f, e.offsetMs, 0.001f);
            Assert.IsTrue(e.HasChart(0));
        }

        /// <summary>音符數 0 = 那個難度根本沒譜（有些歌 level 有值但一顆音符都沒有），選歌畫面要 grey out。</summary>
        [Test]
        public void HasChart_Follows_The_Note_Count_Not_The_Level()
        {
            var r = SongTable.Parse("gn,lvHard,notesHard\nsdom9k.gn,8,0\n")[0];
            var e = SongCatalog.FromRow(r);
            Assert.AreEqual(8, e.Diff(2));
            Assert.IsFalse(e.HasChart(2));
        }

        /// <summary>手滑多打一個 0（30 → 300000）不該把音樂推到歌曲之外 —— 夾在 ±MaxOffsetMs。</summary>
        [TestCase(300000f, 1f)]
        [TestCase(-300000f, -1f)]
        public void Absurd_OffsetMs_Is_Clamped(float raw, float sign)
        {
            var r = SongTable.Parse($"gn,offsetMs\nsdom9k.gn,{raw}\n")[0];
            Assert.AreEqual(sign * SongCatalog.MaxOffsetMs, SongCatalog.FromRow(r).offsetMs, 0.001f);
        }

        /// <summary>k 譜與 t 譜的曲師常常是不同人（sdom0001k=YO!610 / t=Yo!610、sdom0002k=Tina / t=S.Q.H），
        /// 所以 producer 是**每一列自己的**資料，不會跟著顯示名同步。</summary>
        [Test]
        public void GnHeaderCatalog_View_Keeps_Per_Chart_Producer_And_Both_Languages()
        {
            var rows = SongTable.Parse(Header + RowK + RowT);
            var k = GnHeaderCatalog.FromRow(rows[0]);
            var t = GnHeaderCatalog.FromRow(rows[1]);

            Assert.AreEqual("YO!610", k.producer);
            Assert.AreEqual("Yo!610", t.producer);
            Assert.AreEqual("野蛮游戏", k.title.For(GnHeaderCatalog.Lang.ZhCN));
            Assert.AreEqual("野蠻遊戲", k.title.For(GnHeaderCatalog.Lang.ZhTW));
            Assert.AreEqual("野蛮游戏", k.title.For(GnHeaderCatalog.Lang.En));   // 沒有英文名 → 退回原文
        }

        [TestCase("sdom0001k.gn", true)]
        [TestCase("sdom0001t.gn", false)]
        [TestCase("H:/music/SDOM0001K.GN", true)]
        [TestCase("", false)]
        public void Primary_Variant_Is_The_Keyboard_Chart(string gn, bool expected)
            => Assert.AreEqual(expected, SongCatalog.IsPrimaryVariant(gn));

        /// <summary>主音樂 .ogg 一律由 gn 詞幹決定（開局與選歌試聽共用同一支）。重點是 sdom1234_1k.gn
        /// （號碼撞號時手動插隊用的名字）要指向自己的 sdom1234_1.ogg，而不是原本 sdom1234 那首的音樂。</summary>
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
    }
}
