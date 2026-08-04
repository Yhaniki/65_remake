using System;
using System.Collections.Generic;
using System.IO;

namespace Sdo.Game
{
    /// <summary>
    /// The list of MMD models available to <see cref="MmdAvatarSwap"/> — one entry per folder that directly holds a ".pmx".
    /// Drop a model (the whole extracted folder: the .pmx plus its textures/Toon/Sph sub-folders) under
    /// <c>DATA/MODEL/&lt;name&gt;/</c> and it shows up in the 設定面板的 MMD 分頁; nothing is hardcoded to any one model.
    ///
    /// The scan is deliberately forgiving about how a downloaded model unzips: the root itself may hold the .pmx, or a
    /// sub-folder, or a sub-sub-folder (<c>MODEL/pack/miku/miku.pmx</c>). A folder that has a .pmx is a LEAF — we never
    /// descend into it, so a model's own sub-folders (textures/, Toon/, …) can't turn into phantom entries.
    ///
    /// A folder with several .pmx files is ONE model (they are almost always CN/EN/JP name variants of the same mesh):
    /// the JP file wins, because <see cref="MmdBoneMap"/> keys on the Japanese bone names — an EN-named .pmx parses but
    /// no bone maps to the SDO skeleton, so the model would just stand still.
    ///
    /// Pure logic (the filesystem is injected) so it can be unit-tested without touching disk; see
    /// <see cref="Discover(IEnumerable{string})"/> for the real-disk entry point.
    /// </summary>
    public static class MmdModelCatalog
    {
        /// <summary>One installed model: its display name (the folder name) and the .pmx to parse.</summary>
        public sealed class Entry
        {
            public string Name;      // folder name, e.g. "IkaHatunemiku2025"
            public string Dir;       // folder holding the .pmx (also the texture base dir)
            public string PmxPath;   // the .pmx to load
            public override string ToString() => Name;
        }

        /// <summary>How deep below a root we look for the folder that holds the .pmx.</summary>
        public const int MaxDepth = 2;

        /// <summary>Scan <paramref name="roots"/> on the real filesystem, in order (earlier roots win a name clash).</summary>
        public static List<Entry> Discover(IEnumerable<string> roots)
            => Discover(roots, Directory.Exists, SafeDirs, SafeFiles);

        private static IEnumerable<string> SafeDirs(string d)
        { try { return Directory.GetDirectories(d); } catch { return new string[0]; } }

        private static IEnumerable<string> SafeFiles(string d)
        { try { return Directory.GetFiles(d); } catch { return new string[0]; } }

        /// <summary>Scan with an injected filesystem (the unit-testable core). Entries are sorted by name; a model found
        /// under an earlier root shadows a same-named one under a later root (so a dev-tree model beats a packaged one).</summary>
        public static List<Entry> Discover(IEnumerable<string> roots, Func<string, bool> dirExists,
                                           Func<string, IEnumerable<string>> subDirs, Func<string, IEnumerable<string>> files)
        {
            var found = new List<Entry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // by name: first root wins
            if (roots != null)
                foreach (var root in roots)
                {
                    if (string.IsNullOrEmpty(root) || !dirExists(root)) continue;
                    Walk(root, 0, found, seen, subDirs, files);
                }
            found.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return found;
        }

        private static void Walk(string dir, int depth, List<Entry> found, HashSet<string> seen,
                                 Func<string, IEnumerable<string>> subDirs, Func<string, IEnumerable<string>> files)
        {
            string pmx = PickPmx(files(dir));
            if (pmx != null)
            {
                string name = LeafName(dir);
                if (seen.Add(name)) found.Add(new Entry { Name = name, Dir = dir, PmxPath = pmx });
                return;   // a model folder is a leaf — its textures/ Toon/ … are not models
            }
            if (depth >= MaxDepth) return;
            foreach (var sub in subDirs(dir)) Walk(sub, depth + 1, found, seen, subDirs, files);
        }

        /// <summary>The .pmx a folder should load: the Japanese-named one (its bone names are what <see cref="MmdBoneMap"/>
        /// drives), else the alphabetically first — deterministic regardless of directory-enumeration order. Null when the
        /// folder holds no .pmx.</summary>
        public static string PickPmx(IEnumerable<string> files)
        {
            string jp = null, best = null;
            if (files != null)
                foreach (var f in files)
                {
                    if (string.IsNullOrEmpty(f)) continue;
                    if (!f.EndsWith(".pmx", StringComparison.OrdinalIgnoreCase)) continue;
                    string up = f.ToUpperInvariant();
                    if (up.Contains("-JP") || up.Contains("_JP"))
                    {
                        if (jp == null || string.CompareOrdinal(f, jp) < 0) jp = f;
                    }
                    if (best == null || string.CompareOrdinal(f, best) < 0) best = f;
                }
            return jp ?? best;
        }

        /// <summary>The last path segment, tolerating a trailing separator ("a/b/" → "b").</summary>
        public static string LeafName(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return "";
            string d = dir.Replace('\\', '/').TrimEnd('/');
            int i = d.LastIndexOf('/');
            string leaf = i < 0 ? d : d.Substring(i + 1);
            return leaf.Length == 0 ? d : leaf;
        }

        /// <summary>Index of the model to start on: the one whose name matches <paramref name="want"/> (case-insensitive,
        /// substring — so <c>-mmdmodel miku</c> finds "IkaHatunemiku2025"), else 0. -1 when nothing is installed.</summary>
        public static int IndexOf(List<Entry> models, string want)
        {
            if (models == null || models.Count == 0) return -1;
            if (!string.IsNullOrEmpty(want))
            {
                for (int i = 0; i < models.Count; i++)
                    if (string.Equals(models[i].Name, want, StringComparison.OrdinalIgnoreCase)) return i;
                for (int i = 0; i < models.Count; i++)
                    if (models[i].Name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0) return i;
            }
            return 0;
        }
    }
}
