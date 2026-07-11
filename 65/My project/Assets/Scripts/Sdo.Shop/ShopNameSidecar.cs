using System.Collections.Generic;
using System.Text;

namespace Sdo.Shop
{
    /// <summary>
    /// Parser + formatter for the item-name sidecar <c>shop_names.tsv</c> — one <c>id\tname</c> line per item, UTF-8.
    ///
    /// WHY THIS EXISTS: item names in <see cref="IteminfoReader"/>'s <c>iteminfo.dat</c> are GBK/CP936 Simplified
    /// Chinese. The Unity editor has that codepage so names decode fine there, but a Mono standalone build strips
    /// I18N.CJK, <c>Encoding.GetEncoding(936)</c> throws, and the names fall back to Latin1 → mojibake. Rather than
    /// depend on a runtime codepage, <c>tools/package_build.ps1</c> decodes the names once at packaging time (Windows
    /// PowerShell has CP936) and writes them here as plain UTF-8; the runtime overlays this file and needs no encoding.
    /// This is the authoritative <see cref="FileName"/> shared by the bake step and the runtime loader.
    /// </summary>
    public static class ShopNameSidecar
    {
        public const string FileName = "shop_names.tsv";

        /// <summary>Parse <c>id\tname</c> lines into an id → name map. Tolerant of CRLF, blank lines and malformed
        /// rows (a row with no tab, a non-integer id, or an empty id column is skipped).</summary>
        public static Dictionary<int, string> Parse(string text)
        {
            var map = new Dictionary<int, string>();
            if (string.IsNullOrEmpty(text)) return map;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                int tab = line.IndexOf('\t');
                if (tab <= 0) continue;                         // no id, or leading tab
                if (int.TryParse(line.Substring(0, tab), out int id))
                    map[id] = line.Substring(tab + 1);          // name may legitimately be empty
            }
            return map;
        }

        /// <summary>Format an id → name map into sidecar text (used by the bake step and its tests). Entries with an
        /// empty/null name are omitted — a blank name carries no information and the runtime keeps the parsed name.</summary>
        public static string Format(IEnumerable<KeyValuePair<int, string>> names)
        {
            var sb = new StringBuilder();
            foreach (var kv in names)
            {
                if (string.IsNullOrEmpty(kv.Value)) continue;
                sb.Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
            }
            return sb.ToString();
        }
    }
}
