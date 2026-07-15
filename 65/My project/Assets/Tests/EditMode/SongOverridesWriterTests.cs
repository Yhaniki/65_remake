using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// SongOverridesWriter.TrySetOffsetInText —— 譜面編輯器 Ctrl+S 寫回 offsetMs 的字串手術。
    /// 它不重新序列化整份 JSON（會吃掉沒宣告的欄位、把 diff 炸掉），而是**外科手術式**改那一首。
    /// 這裡最重要的性質：不管怎麼改，產出的仍必須是**合法 JSON** —— 一次壞掉，整份 override 就 parse 失敗，
    /// 全部歌名都套不上（sdom1033「搗蛋天使」變回 catalog 的「搗蛋天使 - 証聲」就是這樣來的）。
    /// </summary>
    public class SongOverridesWriterTests
    {
        // json.dumps(indent=1) 的排版：物件用兩格縮排、欄位三格；跟工具產出的檔一模一樣。
        private const string Doc =
            "{\n \"songs\": [\n" +
            "  {\n   \"gn\": \"sdom0001\",\n   \"fileId\": 10001,\n   \"bpm\": 80.0,\n" +
            "   \"src\": \"kgn\",\n   \"title\": \"歌一\",\n   \"artist\": \"手一\"\n  },\n" +
            "  {\n   \"gn\": \"sdom0002\",\n   \"fileId\": 10002,\n   \"bpm\": 90.0,\n" +
            "   \"offsetMs\": 5,\n   \"src\": \"kgn\",\n   \"title\": \"歌二\",\n   \"artist\": \"手二\"\n  }\n" +
            " ]\n}\n";

        // 極小的 JSON 驗證 + 抽值器（EditMode 沒有 System.Text.Json / JsonUtility 又不認 List 根，就自己走一遍）。
        private static void AssertValidJson(string json)
        {
            int depthObj = 0, depthArr = 0;
            bool inStr = false, esc = false;
            foreach (char c in json)
            {
                if (inStr) { if (esc) esc = false; else if (c == '\\') esc = true; else if (c == '"') inStr = false; continue; }
                switch (c)
                {
                    case '"': inStr = true; break;
                    case '{': depthObj++; break;
                    case '}': depthObj--; Assert.GreaterOrEqual(depthObj, 0, "多了一個 }"); break;
                    case '[': depthArr++; break;
                    case ']': depthArr--; Assert.GreaterOrEqual(depthArr, 0, "多了一個 ]"); break;
                }
            }
            Assert.IsFalse(inStr, "字串沒收尾");
            Assert.AreEqual(0, depthObj, "{} 不平衡");
            Assert.AreEqual(0, depthArr, "[] 不平衡");
            // 孤兒欄位偵測：任何 "offsetMs" 前面最近的非空白結構字元不該是 '}'（代表它掉在物件外面）
            int idx = 0;
            while ((idx = json.IndexOf("\"offsetMs\"", idx, System.StringComparison.Ordinal)) >= 0)
            {
                int j = idx - 1;
                while (j >= 0 && char.IsWhiteSpace(json[j])) j--;
                // 物件內 offsetMs 前面一定接著某個欄位的收尾（',' 或 '"'/數字）；若前面是 '}' 就代表它掉到物件外了
                Assert.AreNotEqual('}', json[j], "offsetMs 掉在物件外面（孤兒欄位）");
                idx += 1;
            }
        }

        [Test]
        public void Insert_Into_Object_Without_Offset_Stays_Valid_Json()
        {
            Assert.IsTrue(SongOverridesWriter.TrySetOffsetInText(Doc, "sdom0001", 20, out string outText, out _));
            AssertValidJson(outText);
            Assert.IsTrue(outText.Contains("\"offsetMs\": 20"));
            // 新欄位必須在 sdom0001 物件之內：出現在它的 title 之後、它的 '}' 之前
            int gn = outText.IndexOf("\"sdom0001\"", System.StringComparison.Ordinal);
            int close = outText.IndexOf('}', gn);
            int off = outText.IndexOf("\"offsetMs\": 20", System.StringComparison.Ordinal);
            Assert.Greater(off, gn);
            Assert.Less(off, close, "offsetMs 必須插在該物件的 } 之內");
        }

        /// <summary>回歸測試：sdom1033「搗蛋天使」變成「搗蛋天使 - 証聲」的元凶。
        /// 舊版把插入點算到 '}' 本身，欄位掉到物件外，連存兩次就多出兩行孤兒 offsetMs，整份 JSON 壞掉。</summary>
        [Test]
        public void Saving_Twice_Does_Not_Produce_Orphan_Fields()
        {
            Assert.IsTrue(SongOverridesWriter.TrySetOffsetInText(Doc, "sdom0001", 20, out string once, out _));
            AssertValidJson(once);
            Assert.IsTrue(SongOverridesWriter.TrySetOffsetInText(once, "sdom0001", 30, out string twice, out _));
            AssertValidJson(twice);
            // 第二次是「改值」不是「再插一條」：整份只有一個 offsetMs 屬於 sdom0001（sdom0002 本來就有一個）
            Assert.AreEqual(1, CountOffsetsBetween(twice, "sdom0001", "sdom0002"));
            Assert.IsTrue(twice.Contains("\"offsetMs\": 30"));
            Assert.IsFalse(twice.Contains("\"offsetMs\": 20"));
        }

        [Test]
        public void Replace_Existing_Offset_Keeps_Single_Field()
        {
            Assert.IsTrue(SongOverridesWriter.TrySetOffsetInText(Doc, "sdom0002", -12, out string outText, out _));
            AssertValidJson(outText);
            Assert.IsTrue(outText.Contains("\"offsetMs\": -12"));
            Assert.IsFalse(outText.Contains("\"offsetMs\": 5"));
            Assert.AreEqual(1, CountOffsetsBetween(outText, "sdom0002", null));
        }

        [Test]
        public void Clear_Removes_The_Field_And_Its_Comma()
        {
            Assert.IsTrue(SongOverridesWriter.TrySetOffsetInText(Doc, "sdom0002", 0, out string outText, out _));
            AssertValidJson(outText);
            Assert.AreEqual(0, CountOffsetsBetween(outText, "sdom0002", null));
        }

        [Test]
        public void Clear_When_Absent_Is_A_Successful_NoOp()
        {
            // sdom0001 沒有 offsetMs → 清它 = 成功但 updated 為 null（不必寫檔）
            Assert.IsTrue(SongOverridesWriter.TrySetOffsetInText(Doc, "sdom0001", 0, out string outText, out _));
            Assert.IsNull(outText);
        }

        [Test]
        public void Missing_Song_Or_Empty_Stem_Fails_Without_Text()
        {
            Assert.IsFalse(SongOverridesWriter.TrySetOffsetInText(Doc, "sdom9999", 10, out string a, out _));
            Assert.IsNull(a);
            Assert.IsFalse(SongOverridesWriter.TrySetOffsetInText(Doc, "", 10, out string b, out _));
            Assert.IsNull(b);
        }

        /// <summary>數 [afterStem 物件開頭, 下一首 stem 之前) 這段裡有幾個 "offsetMs"。beforeStem 為 null = 數到檔尾。</summary>
        private static int CountOffsetsBetween(string json, string afterStem, string beforeStem)
        {
            int start = json.IndexOf("\"" + afterStem + "\"", System.StringComparison.Ordinal);
            int end = beforeStem == null ? json.Length
                                         : json.IndexOf("\"" + beforeStem + "\"", System.StringComparison.Ordinal);
            int n = 0, i = start;
            while ((i = json.IndexOf("\"offsetMs\"", i, System.StringComparison.Ordinal)) >= 0 && i < end) { n++; i += 1; }
            return n;
        }
    }
}
