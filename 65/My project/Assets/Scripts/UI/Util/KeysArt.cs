using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Sdo.Game;

namespace Sdo.UI.Util
{
    /// <summary>
    /// Loads the per-key letter/number glyphs the original OPTION 鍵盤 tab paints on each key cap. These are
    /// individual PNGs in <c>UI/LOBBYDLG/KEYS</c> (A.PNG..Z.PNG, 0.PNG..9.PNG, arrows, nav keys), each 27×36
    /// with the blue-fill + white-outline baked into the art. There is NO letter art in OPTIONDLG.PNG itself —
    /// its key.an/key1.an chips are BLANK; the glyph is composited on top. The offline EXE loads these by a
    /// hardcoded path (<c>"Datas\UI\lobbyDlg\Keys\&lt;x&gt;.png"</c>, FUN_00461170 @0x461170) keyed on the bound
    /// key's DirectInput scan code, then blits it onto the key Label. We reproduce that by mapping a Unity
    /// <see cref="KeyCode"/> name to its glyph file.
    ///
    /// Folder resolution reads DATA/UI/LOBBYDLG/KEYS under <see cref="SdoExtracted.Root"/> ONLY — no assets/ scan.
    /// <see cref="Glyph"/> returns null for a key with no glyph
    /// (Shift/Ctrl/Enter…) so callers can fall back to text.
    /// </summary>
    public static class KeysArt
    {
        private static string _dir;
        private static readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>();

        /// <summary>Resolved LOBBYDLG/KEYS folder (lazy). Settable for tests (clears the sprite cache).</summary>
        public static string Dir
        {
            get { return _dir ?? (_dir = ResolveDir()); }
            set { _dir = value; _cache.Clear(); }
        }

        private static string ResolveDir()
        {
            try
            {
                // Use the resolved data root ONLY — no assets/ scan (data_root.txt points this at the clean pack).
                return Path.Combine(SdoExtracted.Root, "UI", "LOBBYDLG", "KEYS");
            }
            catch { return Path.Combine(SdoExtracted.Root, "UI", "LOBBYDLG", "KEYS"); }
        }

        /// <summary>Glyph sprite for a KeyCode enum name (e.g. "A", "Keypad4", "LeftArrow"); null if the key has
        /// no glyph in the KEYS folder (caller draws a text fallback). Cached; full-image sprite (27×36).</summary>
        public static Sprite Glyph(string keyName)
        {
            var file = FileFor(keyName);
            if (file == null) return null;
            if (_cache.TryGetValue(file, out var s) && s != null) return s;
            var tex = SdoExtracted.LoadTextureRaw(Dir, file + ".PNG");
            if (tex == null) { _cache[file] = null; return null; }
            s = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
            _cache[file] = s;
            return s;
        }

        /// <summary>Pure KeyCode-name → glyph filename (no extension), matching the on-disk KEYS set exactly.
        /// Null = no glyph for that key. Unit-testable.</summary>
        public static string FileFor(string keyName)
        {
            if (string.IsNullOrEmpty(keyName)) return null;
            if (keyName.Length == 1 && keyName[0] >= 'A' && keyName[0] <= 'Z') return keyName;   // A..Z
            if (keyName.StartsWith("Alpha") && keyName.Length == 6) return keyName.Substring(5);  // Alpha0..9 -> 0..9
            if (keyName.StartsWith("Keypad") && keyName.Length == 7) return keyName.Substring(6); // Keypad0..9 -> 0..9
            switch (keyName)
            {
                case "Space": return "SPACE";
                case "Comma": return "COMMA";
                case "Period": return "PERIOD";
                case "Slash": return "SLASH";
                case "Semicolon": return "SEM";
                case "Quote": return "APO";
                case "LeftBracket": return "LBRACKET";
                case "RightBracket": return "RBRACKET";
                case "UpArrow": return "UP";
                case "DownArrow": return "DOWN";
                case "LeftArrow": return "LEFT";
                case "RightArrow": return "RIGHT";
                case "Home": return "HOME";
                case "End": return "END";
                case "PageUp": return "PAGEU";
                case "PageDown": return "PAGED";
                case "Insert": return "INSERT";
                case "Delete": return "DELETE";
            }
            return null;
        }
    }
}
