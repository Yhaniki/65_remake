using System.Collections.Generic;
using System.IO;
using Sdo.Game;

namespace Sdo.UI.Catalog
{
    public sealed class NoteSkinInfo
    {
        public string Id;       // folder name under Extracted/NOTEIMAGE, e.g. "NOTEIMAGE_5"
        public string NameZh;
        public NoteSkinInfo(string id, string name) { Id = id; NameZh = name; }
    }

    /// <summary>
    /// The 4-key noteskins shipped under Extracted/NOTEIMAGE. The known set is fixed engine art;
    /// we filter to those whose folder actually exists on disk so the picker never offers a missing skin.
    /// </summary>
    public static class NoteSkinCatalog
    {
        public const string DefaultId = "NOTEIMAGE_5";

        private static readonly NoteSkinInfo[] Candidates =
        {
            new NoteSkinInfo("NOTEIMAGE_5", "經典 5"),
            new NoteSkinInfo("NOTEIMAGE_6", "霓虹 6"),
            new NoteSkinInfo("NOTEIMAGE_8", "炫光 8"),
            new NoteSkinInfo("NOTEIMAGE_9", "幾何 9"),
            new NoteSkinInfo("NOTEIMAGE_10", "晶鑽 10"),
            new NoteSkinInfo("NOTEIMAGE_11", "極簡 11"),
            new NoteSkinInfo("JAM_NOTEIMAGE", "Jam 風格"),
        };

        private static List<NoteSkinInfo> _available;

        public static IReadOnlyList<NoteSkinInfo> Available
        {
            get
            {
                if (_available != null) return _available;
                _available = new List<NoteSkinInfo>();
                string baseDir = Path.Combine(SdoExtracted.Root, "NOTEIMAGE");
                foreach (var c in Candidates)
                {
                    // Include if the folder exists; if the asset root is unavailable, fall back to listing all.
                    if (!Directory.Exists(baseDir) || Directory.Exists(Path.Combine(baseDir, c.Id)))
                        _available.Add(c);
                }
                if (_available.Count == 0) _available.Add(Candidates[0]);
                return _available;
            }
        }

        public static NoteSkinInfo Get(string id)
        {
            foreach (var s in Available) if (s.Id == id) return s;
            return Available[0];
        }
    }
}
