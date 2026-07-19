using System.Collections.Generic;
using System.Text;

namespace Sdo.Shop
{
    /// <summary>
    /// Parser + formatter for the Traditional-Chinese item-name sidecar <c>shop_names_tw.tsv</c> — one
    /// <c>category\tmodelId\tname</c> line per item, UTF-8.
    ///
    /// WHY THIS EXISTS (distinct from <see cref="ShopNameSidecar"/>): the shipped CN <c>iteminfo.dat</c> names
    /// only a fraction of the meshes on disk (the rest show as a bare 6-digit serial) and its names are Simplified
    /// Chinese. The Taiwan「櫻式搖滾」client's <c>iteminfo.dat</c> is a DIFFERENT format (headA=1, 152-byte records,
    /// Big5 Traditional names) that our <see cref="IteminfoReader"/> deliberately does NOT read; instead
    /// <c>tools/build_shop_names_tw.py</c> decodes it once offline into this UTF-8 sidecar. The runtime overlays it in
    /// <see cref="Sdo.Game.AvatarItemCatalog"/> to (a) fill in names for otherwise-unnamed mesh-only rows and
    /// (b) replace the CN Simplified name with the official Traditional one.
    ///
    /// KEYED BY (category, modelId) — NOT the item id — because the two clients renumber ids independently, and the
    /// unnamed rows we most want to fill are synthesised mesh-only rows that have no iteminfo id at all. The category
    /// (1..9 male / 101..109 female / 50·150 連身) encodes both the equip slot AND the gender, so a modelId shared by
    /// two slots or two genders never collides. <see cref="Key"/> is the single source of truth for that composite key.
    /// </summary>
    public static class ShopNameTwSidecar
    {
        public const string FileName = "shop_names_tw.tsv";

        /// <summary>Composite lookup key: category (slot+gender) in the high bits, modelId in the low 32.</summary>
        public static long Key(int category, int modelId) => ((long)category << 32) | (uint)modelId;

        /// <summary>Parse <c>category\tmodelId\tname</c> lines into a (category,modelId) → name map. Tolerant of CRLF,
        /// blank lines and malformed rows (fewer than two tabs, or a non-integer category/modelId, is skipped). The
        /// name — which may itself contain tabs or be empty — is everything after the second tab.</summary>
        public static Dictionary<long, string> Parse(string text)
        {
            var map = new Dictionary<long, string>();
            if (string.IsNullOrEmpty(text)) return map;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                int t1 = line.IndexOf('\t');
                if (t1 <= 0) continue;                              // no category column
                int t2 = line.IndexOf('\t', t1 + 1);
                if (t2 < 0) continue;                               // no modelId column
                if (!int.TryParse(line.Substring(0, t1), out int cat)) continue;
                if (!int.TryParse(line.Substring(t1 + 1, t2 - t1 - 1), out int modelId)) continue;
                map[Key(cat, modelId)] = line.Substring(t2 + 1);   // name may legitimately be empty
            }
            return map;
        }

        /// <summary>Format (key → name) entries into sidecar text (used by the tests for round-trip symmetry; the real
        /// file is written by <c>tools/build_shop_names_tw.py</c>). Keys are <see cref="Key"/> values; category and
        /// modelId are recovered from the key. Entries with an empty/null name are omitted — a blank name carries no
        /// override.</summary>
        public static string Format(IEnumerable<KeyValuePair<long, string>> rows)
        {
            var sb = new StringBuilder();
            foreach (var r in rows)
            {
                if (string.IsNullOrEmpty(r.Value)) continue;
                int cat = (int)(r.Key >> 32);
                int modelId = (int)(r.Key & 0xFFFFFFFF);
                sb.Append(cat).Append('\t').Append(modelId).Append('\t').Append(r.Value).Append('\n');
            }
            return sb.ToString();
        }
    }
}
