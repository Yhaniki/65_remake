using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Sdo.UI.Util
{
    /// <summary>
    /// Traditional → Simplified Chinese single-char folding for search. The shop item names come from the GBK
    /// <c>iteminfo.dat</c> and are SIMPLIFIED; a user typing TRADITIONAL (繁體) otherwise matches nothing (使用者:
    /// 「搜尋打繁體可是可以搜到簡體」). We fold both the query AND the candidate name to Simplified before comparing.
    ///
    /// The map is baked at packaging time (tools, via OpenCC t2s over the CJK BMP, ~2.9k chars that actually change)
    /// into <c>StreamingAssets/trad2simp.txt</c>, one mapping per line as <c>繁\t简</c> (TAB-separated).
    /// Loaded once, lazily. Missing file → <see cref="ToSimp"/> is a no-op (search still works for simplified input).
    ///
    /// NOTE: an earlier two-line format (line1=繁 chars, line2=aligned 简 chars, indexed pairwise) was SILENTLY BROKEN
    /// in the player: OpenCC emits some simplified chars OUTSIDE the BMP (astral, e.g. rare hanzi), which are surrogate
    /// PAIRS in C#'s UTF-16 <c>string</c> — so index-alignment drifted after the first astral char and every later
    /// mapping (紅→红, 動→动, 藍→蓝 …) resolved to garbage. Python tooling counts code points so it looked fine. The
    /// per-line format below is immune: each line carries its own 繁/简 pair, no cross-line index alignment.
    /// </summary>
    public static class TradSimp
    {
        private static Dictionary<char, char> _map;

        private static void Ensure()
        {
            if (_map != null) return;
            _map = new Dictionary<char, char>();
            try
            {
                var path = Path.Combine(Application.streamingAssetsPath, "trad2simp.txt");
                if (File.Exists(path))
                {
                    foreach (var raw in File.ReadAllLines(path))
                    {
                        // "繁\t简" — both single BMP chars (astral/multi-char entries were filtered out at bake time).
                        if (raw.Length >= 3 && raw[1] == '\t') _map[raw[0]] = raw[2];
                    }
                }
            }
            catch { /* missing/unreadable → empty map → ToSimp is a no-op */ }
        }

        /// <summary>Fold every traditional char in <paramref name="s"/> to its simplified form. Returns the SAME string
        /// (no allocation) when nothing changes — the common case for already-simplified item names.</summary>
        public static string ToSimp(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            Ensure();
            if (_map.Count == 0) return s;
            char[] a = null;
            for (int i = 0; i < s.Length; i++)
                if (_map.TryGetValue(s[i], out var c)) { if (a == null) a = s.ToCharArray(); a[i] = c; }
            return a == null ? s : new string(a);
        }
    }
}
