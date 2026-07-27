using System.Globalization;
using System.Threading;
using NUnit.Framework;
using Sdo.Net;
using Sdo.Osu;

namespace Sdo.Tests
{
    /// <summary>
    /// 協定的 JSON 讀寫。
    ///
    /// 這裡有兩條「不測就一定會出事」的:
    ///   • **壞 JSON 必須失敗**,不能變成「一個所有欄位都是預設值的物件」。MiniJson 是為了
    ///     「解析可能壞掉的譜面」設計的，它的失敗全部靜默(Parse 回 null、ParseNumber 回 0.0)。
    ///     那對譜面是對的，對協定是災難。
    ///   • **小數點必須是 InvariantCulture**。系統 locale 是德文/法文時，預設的 double.ToString
    ///     會輸出逗號當小數點,產生的 JSON 直接壞掉 —— 而且只在那些機器上壞，本地永遠測不出來。
    /// </summary>
    public class NetJsonTests
    {
        private CultureInfo _saved;

        [SetUp]
        public void SaveCulture() { _saved = Thread.CurrentThread.CurrentCulture; }

        [TearDown]
        public void RestoreCulture() { Thread.CurrentThread.CurrentCulture = _saved; }

        // ---- writer → reader round-trip ----

        [Test]
        public void Object_Round_Trips_Through_MiniJson()
        {
            string json = JObj.New()
                .Str("t", NetProto.JoinRoom)
                .Int("rq", 7)
                .Int("code", 12345)
                .Bool("force", true)
                .Num("frac", 0.5)
                .Json();

            object node;
            Assert.IsTrue(NetJson.TryParse(json, out node));
            Assert.AreEqual(NetProto.JoinRoom, NetJson.Str(node, "t"));
            Assert.AreEqual(7, NetJson.Int(node, "rq"));
            Assert.AreEqual(12345, NetJson.Int(node, "code"));
            Assert.IsTrue(NetJson.Bool(node, "force"));
            Assert.AreEqual(0.5, NetJson.Num(node, "frac"), 1e-9);
        }

        [Test]
        public void Nested_Object_And_Array_Round_Trip()
        {
            // roomState 的形狀:seats 是物件陣列。
            var seats = JArr.New()
                .Add(JObj.New().Str("state", "taken").Int("userId", 1).Str("name", "玩家一"))
                .Add(JObj.New().Str("state", "open").Int("userId", 0).Str("name", ""));

            string json = JObj.New()
                .Str("t", NetProto.RoomState)
                .Int("rev", 3)
                .Put("seats", seats)
                .Json();

            object node;
            Assert.IsTrue(NetJson.TryParse(json, out node));
            Assert.AreEqual(3, NetJson.Int(node, "rev"));

            var arr = NetJson.Arr(node, "seats");
            Assert.IsNotNull(arr);
            Assert.AreEqual(2, arr.Count);
            Assert.AreEqual("taken", NetJson.Str(NetJson.At(arr, 0), "state"));
            Assert.AreEqual("玩家一", NetJson.Str(NetJson.At(arr, 0), "name"));
            Assert.AreEqual("open", NetJson.Str(NetJson.At(arr, 1), "state"));
        }

        [Test]
        public void Null_Child_Writes_Json_Null()
        {
            // 「房間還沒選歌」就是 song: null。
            string json = JObj.New().Str("t", NetProto.RoomState).Put("song", (JObj)null).Json();
            Assert.IsTrue(json.Contains("\"song\":null"));

            object node;
            Assert.IsTrue(NetJson.TryParse(json, out node));
            Assert.IsNull(NetJson.Sub(node, "song"));
        }

        // ---- escaping ----

        [Test]
        public void Quotes_And_Backslashes_Are_Escaped_And_Survive_Round_Trip()
        {
            // 玩家名字/房名/聊天內容都是自由文字，一定會出現這些字元。
            const string nasty = "he said \"hi\" \\ then left";
            string json = JObj.New().Str("text", nasty).Json();

            object node;
            Assert.IsTrue(NetJson.TryParse(json, out node));
            Assert.AreEqual(nasty, NetJson.Str(node, "text"));
        }

        [Test]
        public void Control_Characters_Are_Escaped_And_Survive_Round_Trip()
        {
            const string withControls = "line1\nline2\ttab\rcr";
            string json = JObj.New().Str("text", withControls).Json();

            // 原始的控制字元不該直接出現在 JSON 裡(那是不合法的 JSON)。
            Assert.IsFalse(json.Contains("\n"), "換行必須被轉義成 \\n");
            Assert.IsFalse(json.Contains("\t"));

            object node;
            Assert.IsTrue(NetJson.TryParse(json, out node));
            Assert.AreEqual(withControls, NetJson.Str(node, "text"));
        }

        [Test]
        public void Cjk_Is_Written_Raw_Not_As_Unicode_Escapes()
        {
            // 🔴 刻意不輸出 \uXXXX:MiniJson 的 \u 解析不處理 surrogate pair
            // (它做 (char)cp,一個 code point 只吃一個 char)。只要 writer 永遠不產生 \u，
            // 那個限制就永遠碰不到。這條測試就是在守住這個前提。
            const string cjk = "危險的演出";
            string json = JObj.New().Str("title", cjk).Json();

            Assert.IsTrue(json.Contains(cjk), "CJK 應該原樣輸出(UTF-8),不要轉成 \\u");
            Assert.IsFalse(json.Contains("\\u"), "writer 不該產生 \\u 轉義(除了控制字元)");

            object node;
            Assert.IsTrue(NetJson.TryParse(json, out node));
            Assert.AreEqual(cjk, NetJson.Str(node, "title"));
        }

        [Test]
        public void Emoji_Outside_Bmp_Survives_Round_Trip()
        {
            // emoji 是 surrogate pair。因為 writer 原樣輸出(不轉 \u)，parser 也就原樣讀回，
            // MiniJson 那個 \u 不支援 surrogate 的限制碰不到。
            const string emoji = "跳舞 \U0001F57A";
            string json = JObj.New().Str("text", emoji).Json();

            object node;
            Assert.IsTrue(NetJson.TryParse(json, out node));
            Assert.AreEqual(emoji, NetJson.Str(node, "text"));
        }

        [Test]
        public void Null_String_Writes_Empty_String()
        {
            string json = JObj.New().Str("name", null).Json();
            object node;
            Assert.IsTrue(NetJson.TryParse(json, out node));
            Assert.AreEqual("", NetJson.Str(node, "name"));
        }

        // ---- 🔴 locale ----

        [Test]
        public void Decimal_Point_Is_Invariant_Under_A_Comma_Decimal_Locale()
        {
            // 德文 locale 的 double.ToString() 會給 "0,5" —— 那會產生壞掉的 JSON,
            // 而且只在那種機器上壞。writer 一律用 InvariantCulture。
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

            // 單一欄位 → 可以完整比對整個輸出，這是最強的斷言:
            // 如果小數點跑掉，這裡會直接看到 {"frac":0,5}。
            Assert.AreEqual("{\"frac\":0.5}", JObj.New().Num("frac", 0.5).Json());

            // 多欄位時欄位之間本來就有逗號分隔，所以只能比對每個 key:value 片段。
            string json = JObj.New().Num("frac", 0.5).Num("t0", 1234.75).Json();
            Assert.IsTrue(json.Contains("\"frac\":0.5"), "得到的是:" + json);
            Assert.IsTrue(json.Contains("\"t0\":1234.75"), "得到的是:" + json);
        }

        [Test]
        public void Integers_Are_Invariant_Under_A_Thousands_Separator_Locale()
        {
            // 有些 locale 的 int.ToString() 會加千分位 —— "1.234.567" 一樣是壞 JSON。
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

            string json = JObj.New().Int("score", 1234567).Long("ms", 1785139047286L).Json();

            Assert.IsTrue(json.Contains("\"score\":1234567"), "得到的是:" + json);
            Assert.IsTrue(json.Contains("\"ms\":1785139047286"), "得到的是:" + json);
        }

        // ---- 數字精度 ----

        [Test]
        public void Long_Survives_Up_To_The_Double_Safe_Integer_Limit()
        {
            // MiniJson 全部走 double,安全整數上限 2^53。協定裡的每個 long 都遠低於此:
            // 分數 < 10^7、userId/房號 < 10^5、timestamp ms < 10^13。
            // 這條測試是在守住「2^53 以內不會掉精度」這個前提。
            const long safeMax = 9007199254740992L;   // 2^53
            const long timestampish = 1785139047286L; // 真實的 Unix ms 量級

            string json = JObj.New().Long("a", safeMax).Long("b", timestampish).Long("c", -12345L).Json();

            object node;
            Assert.IsTrue(NetJson.TryParse(json, out node));
            Assert.AreEqual(safeMax, NetJson.Long(node, "a"));
            Assert.AreEqual(timestampish, NetJson.Long(node, "b"));
            Assert.AreEqual(-12345L, NetJson.Long(node, "c"));
        }

        // ---- 🔴 壞資料必須失敗 ----

        [Test]
        public void Malformed_Json_Fails_Instead_Of_Returning_An_Empty_Object()
        {
            // MiniJson.Parse 對壞資料回 null。TryParse 必須把它當成 protocol error,
            // 呼叫端才會斷線;若當成空物件繼續跑，每個欄位都會靜默變成預設值 ——
            // 例如 userId=0、state=idle —— 然後在某個完全無關的地方出怪事。
            object node;
            Assert.IsFalse(NetJson.TryParse("", out node));
            Assert.IsFalse(NetJson.TryParse(null, out node));
            Assert.IsFalse(NetJson.TryParse("   ", out node));
        }

        [Test]
        public void Unterminated_Object_Fails()
        {
            // 🔴 這條是寫測試時抓到的真實缺陷。MiniJson 的 ParseString 不檢查開頭是不是引號,
            // 它假設當前字元就是開引號直接跳過 —— 所以 "{ not json at all" 會得到一個
            // **非 null** 的 Dictionary(整段被當成一個 key,值是 null)。
            // 只檢查 `Parse(...) != null` 的實作會把這種垃圾當成合法訊息放行。
            // 現在 TryParse 會先做結構檢查:trim 後必須真的被一對大括號包住。
            object node;
            Assert.IsFalse(NetJson.TryParse("{ not json at all", out node));
            Assert.IsFalse(NetJson.TryParse("\"t\":\"ping\"}", out node), "缺開頭的大括號");
            Assert.IsFalse(NetJson.TryParse("{", out node));
        }

        [Test]
        public void Leading_And_Trailing_Whitespace_Is_Tolerated()
        {
            // 結構檢查不能嚴到把正常的 pretty-print 殘留也擋掉。
            object node;
            Assert.IsTrue(NetJson.TryParse("  \r\n{\"t\":\"ping\"}\n  ", out node));
            Assert.AreEqual("ping", NetJson.Str(node, "t"));
        }

        // ---- 協定訊息入口(比 TryParse 更嚴:必須自報型別) ----

        [Test]
        public void Message_Requires_A_Non_Empty_Type_Field()
        {
            object node;
            string type;

            Assert.IsTrue(NetJson.TryParseMessage("{\"t\":\"ping\",\"t0\":1}", out node, out type));
            Assert.AreEqual("ping", type);

            // 沒有 t → 無從 dispatch,就是壞訊息。
            Assert.IsFalse(NetJson.TryParseMessage("{\"rq\":1}", out node, out type));
            // t 是空字串 → 同上。
            Assert.IsFalse(NetJson.TryParseMessage("{\"t\":\"\"}", out node, out type));
            // t 型別不對。
            Assert.IsFalse(NetJson.TryParseMessage("{\"t\":123}", out node, out type));
        }

        [Test]
        public void Braced_Garbage_Is_Rejected_As_A_Message()
        {
            // "{ garbage }" 通得過結構檢查(首尾是大括號),MiniJson 會產生一個
            // key 是垃圾字串、值是 null 的物件 —— 但它沒有 t,所以在訊息層被擋掉。
            // 這是兩層防護的第二層。
            object node;
            string type;
            Assert.IsFalse(NetJson.TryParseMessage("{ garbage }", out node, out type));
            Assert.IsFalse(NetJson.TryParseMessage("{}", out node, out type));
        }

        [Test]
        public void Message_Parses_From_Utf8_Bytes()
        {
            var payload = JObj.New().Str("t", NetProto.SetReady).Bool("ready", true).Utf8();
            object node;
            string type;
            Assert.IsTrue(NetJson.TryParseMessage(payload, 0, payload.Length, out node, out type));
            Assert.AreEqual(NetProto.SetReady, type);
            Assert.IsTrue(NetJson.Bool(node, "ready"));
        }

        [Test]
        public void Top_Level_Non_Object_Fails()
        {
            // 協定的每個訊息都是 {...}。收到陣列或裸值代表對方在講別的協定。
            object node;
            Assert.IsFalse(NetJson.TryParse("[1,2,3]", out node));
            Assert.IsFalse(NetJson.TryParse("\"hello\"", out node));
            Assert.IsFalse(NetJson.TryParse("42", out node));
        }

        [Test]
        public void Missing_Fields_Return_The_Fallback()
        {
            object node;
            Assert.IsTrue(NetJson.TryParse("{\"t\":\"ping\"}", out node));

            Assert.AreEqual("", NetJson.Str(node, "nope"));
            Assert.AreEqual(-1, NetJson.Int(node, "nope", -1));
            Assert.AreEqual(-1L, NetJson.Long(node, "nope", -1L));
            Assert.IsTrue(NetJson.Bool(node, "nope", true));
            Assert.IsNull(NetJson.Arr(node, "nope"), "缺陣列要回 null,呼叫端才能分辨『沒帶』與『帶了空的』");
        }

        [Test]
        public void Wrong_Typed_Fields_Return_The_Fallback()
        {
            // 對方送了型別不對的東西(可能是舊版 client)。要退回 fallback 而不是拋例外。
            object node;
            Assert.IsTrue(NetJson.TryParse("{\"n\":\"not a number\",\"s\":123,\"b\":\"yes\"}", out node));

            Assert.AreEqual(-1, NetJson.Int(node, "n", -1));
            Assert.AreEqual("", NetJson.Str(node, "s"));
            Assert.IsFalse(NetJson.Bool(node, "b", false));
        }

        [Test]
        public void Empty_Array_Is_Distinguishable_From_Missing()
        {
            // 「房裡沒有旁觀者」是空陣列;「舊版 client 沒帶這個欄位」是 null。兩者語意不同。
            string json = JObj.New().Put("spectators", JArr.New()).Json();
            object node;
            Assert.IsTrue(NetJson.TryParse(json, out node));

            var arr = NetJson.Arr(node, "spectators");
            Assert.IsNotNull(arr);
            Assert.AreEqual(0, arr.Count);
        }

        // ---- UTF-8 位元組入口(socket 收到的形狀) ----

        [Test]
        public void Parses_From_Utf8_Bytes_With_Offset()
        {
            var payload = JObj.New().Str("t", NetProto.Ping).Num("t0", 12.5).Utf8();
            // 前面故意墊 3 個 byte,模擬 buffer 裡的 offset。
            var buf = new byte[payload.Length + 3];
            System.Array.Copy(payload, 0, buf, 3, payload.Length);

            object node;
            Assert.IsTrue(NetJson.TryParse(buf, 3, payload.Length, out node));
            Assert.AreEqual(NetProto.Ping, NetJson.Str(node, "t"));
            Assert.AreEqual(12.5, NetJson.Num(node, "t0"), 1e-9);
        }

        [Test]
        public void Out_Of_Range_Byte_Window_Fails_Instead_Of_Throwing()
        {
            object node;
            Assert.IsFalse(NetJson.TryParse(new byte[4], 0, 99, out node));
            Assert.IsFalse(NetJson.TryParse(new byte[4], -1, 2, out node));
            Assert.IsFalse(NetJson.TryParse(null, 0, 0, out node));
        }

        // ---- builder 的使用約束 ----

        [Test]
        public void Builder_Rejects_Fields_After_Harvest()
        {
            // 收成後再加欄位是呼叫端的邏輯錯誤(會產生 "{...}extra" 這種壞字串)，
            // 寧可當場炸掉也不要默默產生壞 JSON。
            var o = JObj.New().Str("t", "x");
            o.Json();
            Assert.Throws<System.InvalidOperationException>(() => o.Str("late", "y"));
        }

        [Test]
        public void Json_Can_Be_Read_Twice()
        {
            var o = JObj.New().Str("t", "x");
            Assert.AreEqual(o.Json(), o.Json());
        }
    }
}
