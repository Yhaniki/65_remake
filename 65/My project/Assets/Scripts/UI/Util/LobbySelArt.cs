using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Sdo.Game;

namespace Sdo.UI.Util
{
    /// <summary>
    /// Loads the original standalone LOBBYSEL (DDRLOBBYSEL "MainScreen") art — the opening 男/女 select screen. Unlike
    /// ROOMDLG (which prefers the online 閉撰敃氪 atlas), LOBBYSEL is standalone-only, so it resolves straight against
    /// <see cref="SdoExtracted.Root"/>/UI/LOBBYSEL (dev → assets/sdox_offline/Extracted/UI/LOBBYSEL; built → DATA/UI/
    /// LOBBYSEL, which package_build fills as part of the base Extracted mirror). Each .an here is a plain single-PNG
    /// reference (e.g. LobbySel130a.an → LobbySel130a.png), so <see cref="SdoExtracted.LoadAn1"/> (PNG + Y-flip + cache)
    /// loads it directly — no DDS decode. Returns null for a missing asset; callers guard (UIKit.AddSprite tolerates null).
    /// </summary>
    public static class LobbySelArt
    {
        public const string FolderName = "LOBBYSEL";

        private static string _dir;
        private static readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>();
        private static readonly Dictionary<string, Sprite[]> _framesCache = new Dictionary<string, Sprite[]>();

        /// <summary>Resolved LOBBYSEL art folder (lazy). Settable for tests (clears the cache).</summary>
        public static string Dir
        {
            get { return _dir ?? (_dir = Path.Combine(SdoExtracted.Root, "UI", FolderName)); }
            set { _dir = value; _cache.Clear(); _framesCache.Clear(); }
        }

        /// <summary>First frame of a LOBBYSEL .an as a sprite (cached); null if missing. Name may include or omit ".an".</summary>
        public static Sprite An(string anName)
        {
            if (string.IsNullOrEmpty(anName)) return null;
            if (_cache.TryGetValue(anName, out var s) && s != null) return s;
            s = SdoExtracted.LoadAn1(Dir, anName);
            _cache[anName] = s;
            return s;
        }

        /// <summary>ALL frames of a LOBBYSEL .an as sprites (cached), for small looping UI animations like twt.an.</summary>
        public static Sprite[] AnFrames(string anName)
        {
            if (string.IsNullOrEmpty(anName)) return new Sprite[0];
            if (_framesCache.TryGetValue(anName, out var s) && s != null) return s;
            s = SdoExtracted.LoadAn(Dir, anName);
            _framesCache[anName] = s;
            return s;
        }
    }
}
