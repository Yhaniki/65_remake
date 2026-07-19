using System.Collections.Generic;
using System.Text;

namespace Sdo.Shop
{
    /// <summary>One 套装 (outfit set) baked from the Taiwan client: its display name, gender, and component model ids.
    /// The remake registers these as extra outfit sets in the 套装 tab (see <see cref="Sdo.Game.AvatarItemCatalog"/>).</summary>
    public sealed class TwSetDef
    {
        public int SetId;          // the TW client's raw setId (remapped to a non-colliding id at registration time)
        public bool Male;          // outfit-row category 201 → male, 200 → female (203 mixed resolved by name at bake time)
        public string Name;        // Traditional-Chinese set name (from the TW iteminfo outfit row, category 200/201/203)
        public int[] Components;   // component garment model ids (from the TW setinfo.dat record) — each → an AVATAR/*.MSH
    }

    /// <summary>
    /// Parser + formatter for the Traditional-Chinese outfit-set sidecar <c>shop_sets_tw.tsv</c> — one
    /// <c>setId\tM|F\tcomp,comp,…\tname</c> line per set, UTF-8.
    ///
    /// WHY A SECOND SIDECAR (distinct from <see cref="ShopNameTwSidecar"/>): an outfit set's *name* lives in the TW
    /// <c>iteminfo.dat</c> (category 200/201/203 row, modelId == setId) while its *component list* lives in the TW
    /// <c>setinfo.dat</c>; <c>tools/build_shop_names_tw.py</c> joins the two and writes this file. Crucially the CN and
    /// TW clients renumber setIds INDEPENDENTLY (CN 500004 = 暗夜骑斗士, TW 500004 = 逍遙英雄 with different components),
    /// so — unlike single items, whose (category, modelId) identity is stable because it names the same on-disk mesh —
    /// a TW set must be ADDED as a new set under a remapped id, never overlaid onto a CN setId.
    /// </summary>
    public static class ShopSetTwSidecar
    {
        public const string FileName = "shop_sets_tw.tsv";

        /// <summary>Parse <c>setId\tM|F\tcomp,comp,…\tname</c> lines. Tolerant of CRLF, blank lines and malformed rows
        /// (non-integer setId, no components, or an empty name is skipped). The name is everything after the third tab,
        /// so a name containing a tab survives intact.</summary>
        public static List<TwSetDef> Parse(string text)
        {
            var list = new List<TwSetDef>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                int t1 = line.IndexOf('\t'); if (t1 <= 0) continue;
                int t2 = line.IndexOf('\t', t1 + 1); if (t2 < 0) continue;
                int t3 = line.IndexOf('\t', t2 + 1); if (t3 < 0) continue;
                if (!int.TryParse(line.Substring(0, t1), out int setId)) continue;
                string g = line.Substring(t1 + 1, t2 - t1 - 1);
                bool male = g == "M" || g == "m";
                var comps = ParseComps(line.Substring(t2 + 1, t3 - t2 - 1));
                if (comps.Length == 0) continue;
                string name = line.Substring(t3 + 1);
                if (string.IsNullOrEmpty(name)) continue;
                list.Add(new TwSetDef { SetId = setId, Male = male, Name = name, Components = comps });
            }
            return list;
        }

        private static int[] ParseComps(string csv)
        {
            var ids = new List<int>();
            foreach (var part in csv.Split(','))
                if (int.TryParse(part.Trim(), out int m) && m > 0) ids.Add(m);
            return ids.ToArray();
        }

        /// <summary>Format set defs into sidecar text (used by the bake tool + its tests). Rows with no name or no
        /// component survive-check are dropped — they carry no usable set.</summary>
        public static string Format(IEnumerable<TwSetDef> defs)
        {
            var sb = new StringBuilder();
            foreach (var d in defs)
            {
                if (d == null || string.IsNullOrEmpty(d.Name) || d.Components == null || d.Components.Length == 0) continue;
                sb.Append(d.SetId).Append('\t').Append(d.Male ? 'M' : 'F').Append('\t');
                for (int i = 0; i < d.Components.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(d.Components[i]);
                }
                sb.Append('\t').Append(d.Name).Append('\n');
            }
            return sb.ToString();
        }
    }
}
