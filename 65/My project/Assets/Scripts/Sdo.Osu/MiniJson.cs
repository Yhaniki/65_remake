using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Sdo.Osu
{
    /// <summary>
    /// Tiny, dependency-free JSON reader (System-only, no UnityEngine) so <see cref="MalodyChart"/> can parse Malody
    /// <c>.mc</c> files inside the engine-free Sdo.Osu assembly (Unity's JsonUtility needs typed classes and can't read
    /// Malody's mixed note arrays / [int,int,int] beats). Parse-only: values come back as <see cref="Dictionary{TKey,TValue}"/>
    /// (object), <see cref="List{T}"/> (array), <see cref="string"/>, <see cref="double"/> (every number), <see cref="bool"/>
    /// or null. Malformed input returns null rather than throwing, so one bad chart never aborts a whole folder scan. Pure/testable.
    /// </summary>
    public static class MiniJson
    {
        public static object Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int i = 0;
            try
            {
                var v = ParseValue(json, ref i);
                return v;
            }
            catch { return null; }
        }

        // ---- typed accessors (null / wrong-type safe) ----

        public static Dictionary<string, object> AsObject(object v) => v as Dictionary<string, object>;
        public static List<object> AsArray(object v) => v as List<object>;

        public static object Get(object obj, string key)
            => obj is Dictionary<string, object> d && d.TryGetValue(key, out var v) ? v : null;

        public static string GetString(object obj, string key, string fallback = "")
            => Get(obj, key) is string s ? s : fallback;

        public static double GetDouble(object obj, string key, double fallback = 0.0)
            => Get(obj, key) is double n ? n : fallback;

        public static int GetInt(object obj, string key, int fallback = 0)
            => Get(obj, key) is double n ? (int)n : fallback;

        public static bool Has(object obj, string key)
            => obj is Dictionary<string, object> d && d.ContainsKey(key);

        // ---- recursive-descent parser ----

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return null;
            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't': i += 4; return true;    // true
                case 'f': i += 5; return false;   // false
                case 'n': i += 4; return null;    // null
                default:  return ParseNumber(s, ref i);
            }
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>();
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (i < s.Length)
            {
                SkipWs(s, ref i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ':') i++;
                var val = ParseValue(s, ref i);
                d[key] = val;
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; break; }
                break;
            }
            return d;
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var list = new List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }
            while (i < s.Length)
            {
                list.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; break; }
                break;
            }
            return list;
        }

        private static string ParseString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++; // opening quote
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c == '\\' && i < s.Length)
                {
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 <= s.Length &&
                                int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp))
                            { sb.Append((char)cp); i += 4; }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E' || (c >= '0' && c <= '9')) i++;
                else break;
            }
            var span = s.Substring(start, i - start);
            return double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : (object)0.0;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') i++;
                else break;
            }
        }
    }
}
