using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// <see cref="SongTableWriter.TrySetOffsetInText"/> —— 譜面編輯器 Ctrl+S 把單首 offset 寫回
    /// song_table.csv 的字串手術。
    ///
    /// 它**只換那一格**，不重寫整份表：4325 列一旦被重新序列化，引號、小數寫法、欄位順序全都會被
    /// 重新產生一次，一個「存 offset」的動作不該把整個檔案炸掉。所以這裡釘住兩件事：
    /// (1) 同一首歌的 k/t 兩列都要改到（兩份譜共用同一個音檔 → 同一個 offset）；
    /// (2) 其餘每一個位元組原封不動 —— 包含帶逗號的歌名那種引號欄。
    /// </summary>
    public class SongTableWriterTests
    {
        private const string Csv =
            "fileId,title,artist,gn,bpm,offsetMs,src\n" +
            "10001,\"Yes, I do\",蔡依林,sdom0001k.gn,139,-25,songname\n" +
            "1,\"Yes, I do\",蔡依林,sdom0001t.gn,139,-25,songname\n" +
            "10002,算你狠,陳小春,sdom0002k.gn,93,0,songname\n" +
            "2,算你狠,陳小春,sdom0002t.gn,93,0,songname\n";

        private static string[] Lines(string csv) => csv.Split('\n');

        [Test]
        public void Sets_The_Offset_On_Both_Charts_Of_The_Song()
        {
            Assert.IsTrue(SongTableWriter.TrySetOffsetInText(Csv, "sdom0002", -12.5, out string outText, out _));
            var l = Lines(outText);
            Assert.AreEqual("10002,算你狠,陳小春,sdom0002k.gn,93,-12.5,songname", l[3]);
            Assert.AreEqual("2,算你狠,陳小春,sdom0002t.gn,93,-12.5,songname", l[4]);
        }

        /// <summary>只有那一格會變 —— 其他列、表頭、行尾一個位元組都不能動。</summary>
        [Test]
        public void Leaves_Every_Other_Byte_Alone()
        {
            Assert.IsTrue(SongTableWriter.TrySetOffsetInText(Csv, "sdom0002", 7, out string outText, out _));
            var before = Lines(Csv);
            var after = Lines(outText);
            Assert.AreEqual(before.Length, after.Length);
            Assert.AreEqual(before[0], after[0]);      // 表頭
            Assert.AreEqual(before[1], after[1]);      // 另一首歌的 k
            Assert.AreEqual(before[2], after[2]);      // 另一首歌的 t
            Assert.IsTrue(outText.EndsWith("\n"));
        }

        /// <summary>歌名裡的逗號在引號欄裡 —— 位置若照 Split(',') 算，會改到歌手那一格去。</summary>
        [Test]
        public void Quoted_Title_With_A_Comma_Does_Not_Shift_The_Target_Cell()
        {
            Assert.IsTrue(SongTableWriter.TrySetOffsetInText(Csv, "sdom0001", 33, out string outText, out _));
            var l = Lines(outText);
            Assert.AreEqual("10001,\"Yes, I do\",蔡依林,sdom0001k.gn,139,33,songname", l[1]);
            Assert.AreEqual("1,\"Yes, I do\",蔡依林,sdom0001t.gn,139,33,songname", l[2]);
        }

        /// <summary>連按兩次 Ctrl+S 不該讓格子越長越多（早期 JSON 版就是這樣長出孤兒欄位、
        /// 整份檔案 parse 失敗，所有歌名 override 一起失效）。</summary>
        [Test]
        public void Saving_Twice_Replaces_Instead_Of_Appending()
        {
            Assert.IsTrue(SongTableWriter.TrySetOffsetInText(Csv, "sdom0001", 20, out string once, out _));
            Assert.IsTrue(SongTableWriter.TrySetOffsetInText(once, "sdom0001", 30, out string twice, out _));
            Assert.AreEqual(Lines(Csv)[1].Split(',').Length, Lines(twice)[1].Split(',').Length);
            Assert.IsTrue(Lines(twice)[1].Contains(",30,"));
            Assert.IsFalse(Lines(twice)[1].Contains(",20,"));
        }

        [Test]
        public void Clearing_Writes_A_Plain_Zero()
        {
            Assert.IsTrue(SongTableWriter.TrySetOffsetInText(Csv, "sdom0001", 0, out string outText, out _));
            Assert.AreEqual("10001,\"Yes, I do\",蔡依林,sdom0001k.gn,139,0,songname", Lines(outText)[1]);
        }

        /// <summary>值本來就一樣 → 成功但 updated 為 null（不必寫檔，也就不會動到 mtime）。</summary>
        [Test]
        public void No_Change_Means_Nothing_To_Write()
        {
            Assert.IsTrue(SongTableWriter.TrySetOffsetInText(Csv, "sdom0002", 0, out string outText, out _));
            Assert.IsNull(outText);
        }

        [Test]
        public void Unknown_Song_Or_Empty_Stem_Fails_Without_Text()
        {
            Assert.IsFalse(SongTableWriter.TrySetOffsetInText(Csv, "sdom9999", 10, out string a, out _));
            Assert.IsNull(a);
            Assert.IsFalse(SongTableWriter.TrySetOffsetInText(Csv, "", 10, out string b, out _));
            Assert.IsNull(b);
            Assert.IsFalse(SongTableWriter.TrySetOffsetInText(null, "sdom0001", 10, out string c, out _));
            Assert.IsNull(c);
        }

        /// <summary>表頭缺欄（換了格式 / 檔案被截斷）一律不寫 —— 寧可存不進去，也不要盲改某一格。</summary>
        [Test]
        public void Refuses_To_Write_When_The_Header_Has_No_OffsetMs_Column()
        {
            Assert.IsFalse(SongTableWriter.TrySetOffsetInText(
                "gn,title\nsdom0001k.gn,歌名\n", "sdom0001", 10, out string outText, out _));
            Assert.IsNull(outText);
        }

        /// <summary>k/t 是同一首歌的兩份譜 → 詞幹相同就都要改到，就算檔名帶底線（撞號插隊的 sdom1234_1）。</summary>
        [Test]
        public void Stem_Matching_Handles_Underscore_Suffixed_Names()
        {
            var csv = "gn,offsetMs\nsdom1234k.gn,0\nsdom1234_1k.gn,0\nsdom1234_1t.gn,0\n";
            Assert.IsTrue(SongTableWriter.TrySetOffsetInText(csv, "sdom1234_1", 9, out string outText, out _));
            var l = Lines(outText);
            Assert.AreEqual("sdom1234k.gn,0", l[1]);          // 不是同一首歌，別動它
            Assert.AreEqual("sdom1234_1k.gn,9", l[2]);
            Assert.AreEqual("sdom1234_1t.gn,9", l[3]);
        }

        [Test]
        public void Handles_Crlf_Files()
        {
            Assert.IsTrue(SongTableWriter.TrySetOffsetInText(Csv.Replace("\n", "\r\n"), "sdom0002", 5,
                                                            out string outText, out _));
            Assert.IsTrue(outText.Contains("sdom0002k.gn,93,5,songname\r\n"));
            Assert.IsTrue(outText.Contains("sdom0002t.gn,93,5,songname\r\n"));
        }
    }
}
