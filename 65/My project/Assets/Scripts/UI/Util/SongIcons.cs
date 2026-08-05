using System.IO;
using UnityEngine;
using Sdo.Game;
using Sdo.Settings.Vfs;

namespace Sdo.UI.Util
{
    /// <summary>
    /// Best-effort song-icon loader. Reads Root/UI/MUSIC/ICONS under <see cref="SdoExtracted.Root"/> ONLY — no assets/
    /// scan (the resolved data root, e.g. the pruned clean pack via data_root.txt, is the single icon source).
    /// Returns null when unavailable; callers fall back to a placeholder.
    /// </summary>
    public static class SongIcons
    {
        private static string[] _dirs;

        private static string[] Dirs()
        {
            if (_dirs != null) return _dirs;
            var list = new System.Collections.Generic.List<string>();
            try
            {
                // Icons under the resolved data root ONLY — no assets/ scan (data_root.txt points this at the clean pack).
                //
                // 🔴 VfsFile 而不是 Directory.Exists —— UI/ 打包之後這個目錄在磁碟上**不存在**，
                //    原生檢查會回 false、整個 ICONS 來源被丟掉，症狀是「CD 圖全變預設圖」而且不報錯。
                var inData = Path.Combine(SdoExtracted.Root, "UI", "MUSIC", "ICONS");
                if (VfsFile.DirectoryExists(inData)) list.Add(inData);
            }
            catch { /* best effort */ }
            _dirs = list.ToArray();
            // 讀不到資產是 WARN，不是靜默 —— 沒有這行的話「CD 圖全變預設圖」在 log 裡完全看不出來。
            if (_dirs.Length == 0)
                Debug.LogWarning("[songicon] 找不到 ICONS 來源(" + Path.Combine(SdoExtracted.Root, "UI", "MUSIC", "ICONS")
                                 + ")→ 所有 CD 圖會退成預設圖");
            return _dirs;
        }

        public static Sprite Load(int fileId)
        {
            foreach (var dir in Dirs())
            {
                var s = SdoExtracted.LoadImage(dir, fileId + ".PNG") ?? SdoExtracted.LoadImage(dir, fileId + ".png");
                if (s != null) return s;
            }
            return null;
        }

        /// <summary>Load a named icon from the same ICONS folder (e.g. RANDOM.PNG / NONE.PNG). Null if unavailable.</summary>
        public static Sprite LoadNamed(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            foreach (var dir in Dirs())
            {
                var s = SdoExtracted.LoadImage(dir, fileName);
                if (s != null) return s;
            }
            return null;
        }
    }
}
