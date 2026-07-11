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
    /// The map is baked at packaging time (tools, via OpenCC t2s over the CJK BMP, ~3.5k chars that actually change)
    /// into <c>StreamingAssets/trad2simp.txt</c> — line 1 = traditional chars, line 2 = the aligned simplified chars.
    /// Loaded once, lazily. Missing file → <see cref="ToSimp"/> is a no-op (search still works for simplified input).
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
                    var lines = File.ReadAllText(path).Split('\n');
                    if (lines.Length >= 2)
                    {
                        string t = lines[0], s = lines[1];
                        int n = Mathf.Min(t.Length, s.Length);
                        for (int i = 0; i < n; i++) _map[t[i]] = s[i];
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
