using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Sdo.Osu;

namespace Sdo.Net
{
    /// <summary>
    /// 協定用的 JSON:寫入端是自己的 builder(<see cref="JObj"/> / <see cref="JArr"/>)，
    /// 讀取端包住 <see cref="MiniJson"/> 並補齊它缺的東西。零反射、零多型。
    ///
    /// **為什麼不用 System.Text.Json**:Unity 這個專案是 Mono + .NET Standard 2.1
    /// (ProjectSettings apiCompatibilityLevel: 6)，STJ 不在 netstandard2.1 裡。硬塞 vendored DLL
    /// 會把 System.Memory / Buffers / Unsafe / Encodings.Web 一起拖進來，Unity assembly 衝突風險很高。
    ///
    /// **為什麼要包一層而不是直接用 MiniJson**:MiniJson 是給「解析可能壞掉的譜面」設計的，
    /// 它的失敗全部是靜默的 ——
    ///   • <c>Parse()</c> 壞資料回 <c>null</c>(不拋)
    ///   • <c>ParseNumber()</c> 解不出來回 <c>0.0</c>(不拋)
    ///   • 沒有 GetLong / GetBool
    ///   • 所有數字都是 <c>double</c>
    /// 對譜面那是對的設計(一張壞譜不該中斷整個資料夾掃描)，對網路協定則相反:
    /// 壞掉的訊息必須變成明確的錯誤並斷線，不能當成「一個所有欄位都是預設值的物件」繼續跑。
    /// 所以 <see cref="TryParse"/> 一律把 null 當 protocol error。
    ///
    /// 已知的殘留限制(無法從外面偵測，但不影響安全):像 <c>{"score": abc}</c> 這種
    /// 「數字位置放了裸 token」的壞 JSON，MiniJson 內部會把它讀成 <c>0.0</c> 而不告訴我們。
    /// 因為協定對每個數值欄位都有獨立的範圍驗證，最壞情況只是那筆訊息被當成不合法而拒絕。
    /// </summary>
    public static class NetJson
    {
        // ---- 讀取 ----

        /// <summary>
        /// 解析一段 JSON 文字。**回 false 就是 protocol error，呼叫端應該斷線**，
        /// 不要當成空物件繼續處理。
        ///
        /// 🔴 為什麼不能只靠 <c>MiniJson.Parse(...) != null</c>:MiniJson 的 <c>ParseString</c>
        /// 不檢查開頭是不是引號 —— 它假設當前字元就是開引號直接跳過。所以餵它
        /// <c>"{ not json at all"</c> 會得到一個**非 null 的** Dictionary(整段被當成一個 key，
        /// 值是 null),不是 null。只檢查 null 的話這種垃圾會被當成合法訊息放行。
        /// (這是寫測試時才發現的,不是憑空防禦。)
        ///
        /// 所以這裡先做結構檢查:trim 之後必須真的被一對大括號包住。
        /// </summary>
        public static bool TryParse(string json, out object node)
        {
            node = null;
            if (string.IsNullOrEmpty(json)) return false;

            // 前後空白不算數(對方可能有 pretty-print 的殘留)。
            int a = 0, b = json.Length - 1;
            while (a <= b && IsWs(json[a])) a++;
            while (b > a && IsWs(json[b])) b--;
            if (a > b) return false;
            if (json[a] != '{' || json[b] != '}') return false;

            node = MiniJson.Parse(json);
            // MiniJson 對壞資料回 null；我們也要求最外層一定是物件(協定的每個訊息都是 {...})。
            return node is Dictionary<string, object>;
        }

        private static bool IsWs(char c) => c == ' ' || c == '\t' || c == '\n' || c == '\r';

        /// <summary>
        /// 協定訊息的入口:解析 + 要求帶一個非空的 <c>"t"</c>(訊息型別)。
        ///
        /// 這是比 <see cref="TryParse"/> 更嚴的一層,而且是連線層真正該用的那個 ——
        /// 每個協定訊息都必須自報型別，沒有 <c>t</c> 就無從 dispatch，那就是壞訊息。
        /// 順便把「大括號包著但內容是垃圾」那類(例如 <c>{ garbage }</c>,MiniJson 會產生
        /// 一個 key 是垃圾字串、值是 null 的物件)也擋掉。
        /// </summary>
        public static bool TryParseMessage(string json, out object node, out string type)
        {
            type = null;
            if (!TryParse(json, out node)) return false;

            type = Str(node, FieldTypeKey, null);
            return !string.IsNullOrEmpty(type);
        }

        /// <summary>
        /// <see cref="TryParseMessage"/> 的 UTF-8 位元組版本(socket 收到的就是這個形狀)。
        /// </summary>
        public static bool TryParseMessage(byte[] utf8, int offset, int count, out object node, out string type)
        {
            node = null;
            type = null;
            if (utf8 == null || count < 0 || offset < 0 || offset + count > utf8.Length) return false;
            string s;
            try { s = Encoding.UTF8.GetString(utf8, offset, count); }
            catch { return false; }
            return TryParseMessage(s, out node, out type);
        }

        /// <summary>訊息型別的欄位名。與 <c>NetProto.FieldType</c> 同值 —— 那邊是給組訊息用的常數，
        /// 這裡不 reference 它是為了讓 NetJson 保持「不認識協定內容」的通用元件。</summary>
        private const string FieldTypeKey = "t";

        /// <summary>從 UTF-8 位元組解析(socket 收到的就是這個形狀)。</summary>
        public static bool TryParse(byte[] utf8, int offset, int count, out object node)
        {
            node = null;
            if (utf8 == null || count < 0 || offset < 0 || offset + count > utf8.Length) return false;
            string s;
            try { s = Encoding.UTF8.GetString(utf8, offset, count); }
            catch { return false; }
            return TryParse(s, out node);
        }

        public static bool Has(object o, string key) => MiniJson.Has(o, key);

        public static object Sub(object o, string key) => MiniJson.Get(o, key);

        public static string Str(object o, string key, string fallback = "")
            => MiniJson.GetString(o, key, fallback);

        public static int Int(object o, string key, int fallback = 0)
            => MiniJson.GetInt(o, key, fallback);

        public static double Num(object o, string key, double fallback = 0.0)
            => MiniJson.GetDouble(o, key, fallback);

        /// <summary>
        /// MiniJson 沒有這個。所有數字都是 double，安全整數上限 2^53 —— 協定裡的每個 long
        /// (分數 &lt; 10^7、userId/房號 &lt; 10^5、timestamp ms &lt; 10^13)都在範圍內。
        /// </summary>
        public static long Long(object o, string key, long fallback = 0L)
            => MiniJson.Get(o, key) is double n ? (long)n : fallback;

        /// <summary>MiniJson 沒有這個。</summary>
        public static bool Bool(object o, string key, bool fallback = false)
            => MiniJson.Get(o, key) is bool b ? b : fallback;

        /// <summary>取陣列。找不到或型別不對回 null(不是空陣列 —— 呼叫端要能分辨「沒帶」與「帶了空的」)。</summary>
        public static List<object> Arr(object o, string key)
            => MiniJson.AsArray(MiniJson.Get(o, key));

        /// <summary>陣列元素當物件讀(seats[] / rows[] / files[] 都是這個形狀)。</summary>
        public static object At(List<object> arr, int i)
            => (arr != null && i >= 0 && i < arr.Count) ? arr[i] : null;

        // ---- 寫入的底層 helper(給 JObj / JArr 用) ----

        /// <summary>
        /// 把字串以 JSON 字面值(含前後雙引號)寫進 sb。
        /// CJK 直接輸出 UTF-8 **不轉義成 \u** —— 這很重要:MiniJson 的 <c>\u</c> 解析不處理
        /// surrogate pair，所以我們永遠不輸出 <c>\u</c>(除了 &lt; 0x20 的控制字元，那些不可能是 surrogate)。
        /// </summary>
        internal static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            if (s != null)
            {
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else sb.Append(c);
                            break;
                    }
                }
            }
            sb.Append('"');
        }

        /// <summary>
        /// 浮點數字面值。**一定要 InvariantCulture** —— 系統 locale 是德文/法文之類的話，
        /// 預設的 ToString 會輸出逗號小數點，產生的 JSON 直接壞掉(而且只在那些機器上壞)。
        /// </summary>
        internal static void WriteNum(StringBuilder sb, double v)
        {
            // "R" 會產生像 0.30000000000000004 的東西；"0.######" 對協定夠精確(進度值、時間戳都不需要更多)。
            sb.Append(v.ToString("0.######", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// JSON 物件 builder。用法:
    /// <code>
    /// byte[] payload = JObj.New()
    ///     .Str(NetProto.FieldType, NetProto.JoinRoom)
    ///     .Int(NetProto.FieldRequest, rq)
    ///     .Int("code", 12345)
    ///     .Utf8();
    /// </code>
    /// 一個 builder 只能收成一次(<see cref="Json"/> / <see cref="Utf8"/> 之後不能再 Put)。
    /// </summary>
    public sealed class JObj
    {
        private readonly StringBuilder _sb;
        private bool _first = true;
        private bool _closed;

        private JObj() { _sb = new StringBuilder(128); _sb.Append('{'); }

        public static JObj New() => new JObj();

        private JObj Key(string k)
        {
            if (_closed) throw new System.InvalidOperationException("JObj 已收成，不能再加欄位");
            if (!_first) _sb.Append(',');
            _first = false;
            NetJson.WriteString(_sb, k);
            _sb.Append(':');
            return this;
        }

        public JObj Str(string k, string v) { Key(k); NetJson.WriteString(_sb, v); return this; }
        public JObj Int(string k, int v) { Key(k); _sb.Append(v.ToString(CultureInfo.InvariantCulture)); return this; }
        public JObj Long(string k, long v) { Key(k); _sb.Append(v.ToString(CultureInfo.InvariantCulture)); return this; }
        public JObj Num(string k, double v) { Key(k); NetJson.WriteNum(_sb, v); return this; }
        public JObj Bool(string k, bool v) { Key(k); _sb.Append(v ? "true" : "false"); return this; }
        public JObj Null(string k) { Key(k); _sb.Append("null"); return this; }

        /// <summary>嵌入子物件。傳 null 會寫出 JSON null(協定裡「沒選歌」就是這樣表達)。</summary>
        public JObj Put(string k, JObj child) { Key(k); _sb.Append(child == null ? "null" : child.Json()); return this; }

        /// <summary>嵌入陣列。</summary>
        public JObj Put(string k, JArr child) { Key(k); _sb.Append(child == null ? "null" : child.Json()); return this; }

        /// <summary>收成。之後不能再加欄位;可以重複呼叫拿同一份字串。</summary>
        public string Json()
        {
            if (!_closed) { _sb.Append('}'); _closed = true; }
            return _sb.ToString();
        }

        public byte[] Utf8() => Encoding.UTF8.GetBytes(Json());

        public override string ToString() => Json();
    }

    /// <summary>JSON 陣列 builder。見 <see cref="JObj"/>。</summary>
    public sealed class JArr
    {
        private readonly StringBuilder _sb;
        private bool _first = true;
        private bool _closed;

        private JArr() { _sb = new StringBuilder(128); _sb.Append('['); }

        public static JArr New() => new JArr();

        private void Sep()
        {
            if (_closed) throw new System.InvalidOperationException("JArr 已收成，不能再加元素");
            if (!_first) _sb.Append(',');
            _first = false;
        }

        public JArr Add(JObj v) { Sep(); _sb.Append(v == null ? "null" : v.Json()); return this; }
        public JArr Add(JArr v) { Sep(); _sb.Append(v == null ? "null" : v.Json()); return this; }
        public JArr Add(string v) { Sep(); NetJson.WriteString(_sb, v); return this; }
        public JArr Add(int v) { Sep(); _sb.Append(v.ToString(CultureInfo.InvariantCulture)); return this; }
        public JArr Add(long v) { Sep(); _sb.Append(v.ToString(CultureInfo.InvariantCulture)); return this; }
        public JArr Add(double v) { Sep(); NetJson.WriteNum(_sb, v); return this; }
        public JArr Add(bool v) { Sep(); _sb.Append(v ? "true" : "false"); return this; }

        public string Json()
        {
            if (!_closed) { _sb.Append(']'); _closed = true; }
            return _sb.ToString();
        }

        public byte[] Utf8() => Encoding.UTF8.GetBytes(Json());

        public override string ToString() => Json();
    }
}
