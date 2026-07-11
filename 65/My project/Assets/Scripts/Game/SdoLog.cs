using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Lightweight runtime log file for the built player (and the editor). Auto-installs BEFORE the first scene
    /// loads and mirrors EVERY UnityEngine Debug.Log/Warning/Error (and uncaught exceptions) to a plain-text file,
    /// so when the dance freezes or an asset is missing the player can hand over ONE file instead of hunting for
    /// Unity's buried Player.log. Warnings/errors are always written; plain info lines only when <see cref="Verbose"/>
    /// (Debug.Log fires every frame in places, so info is opt-in to keep the file small).
    ///
    /// Location (printed on the first line and to the console): &lt;exeDir&gt;/log.txt when the exe folder is
    /// writable — the most discoverable spot, right beside dance.exe and DATA/ — else
    /// Application.persistentDataPath/log.txt (always writable). The previous run is kept as log.txt.prev.
    ///
    /// This class touches NO Unity API from the log callback (which can arrive on a worker thread): the file path is
    /// resolved once on the main thread at install; the callback only does DateTime + a locked File.AppendAllText.
    /// </summary>
    public static class SdoLog
    {
        private static string _path;
        private static readonly object _gate = new object();
        private static bool _installed;

        /// <summary>Also mirror plain Debug.Log (info) lines, not just warnings/errors. Off by default (chatty).</summary>
        public static bool Verbose = false;

        /// <summary>Absolute path of the active log file (null until installed).</summary>
        public static string Path => _path;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            if (_installed) return;
            _installed = true;
            _path = ResolvePath();
            try
            {
                if (File.Exists(_path))   // keep the previous session's log as .prev
                {
                    var prev = _path + ".prev";
                    try { if (File.Exists(prev)) File.Delete(prev); File.Move(_path, prev); } catch { }
                }
                File.WriteAllText(_path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  unity={Application.unityVersion}  data={SafeRoot()}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
            catch { /* even file creation failed (read-only dir) — carry on console-only */ }
            // Threaded variant catches errors raised off the main thread too; the callback is thread-safe (lock + no Unity API).
            Application.logMessageReceivedThreaded += OnLog;
            Debug.Log("[sdolog] writing to " + _path);
        }

        private static string SafeRoot() { try { return SdoExtracted.Root; } catch { return "?"; } }

        private static string ResolvePath()
        {
            try
            {
                // Built player: Application.dataPath == <exe>/<product>_Data, so its parent is the exe folder.
                var exeDir = Directory.GetParent(Application.dataPath)?.FullName;
                if (!string.IsNullOrEmpty(exeDir))
                {
                    var p = System.IO.Path.Combine(exeDir, "log.txt");
                    try { File.AppendAllText(p, ""); return p; } catch { /* not writable (Program Files) — fall through */ }
                }
            }
            catch { }
            try { return System.IO.Path.Combine(Application.persistentDataPath, "log.txt"); }
            catch { return "log.txt"; }
        }

        private static void OnLog(string msg, string stack, LogType type)
        {
            if (type == LogType.Log && !Verbose) return;   // skip chatty info unless asked
            bool wantStack = type == LogType.Error || type == LogType.Exception || type == LogType.Assert;
            Write(Tag(type), msg, wantStack ? stack : null);
        }

        private static string Tag(LogType t)
        {
            switch (t)
            {
                case LogType.Warning: return "WARN";
                case LogType.Error: return "ERR ";
                case LogType.Exception: return "EXC ";
                case LogType.Assert: return "ASRT";
                default: return "INFO";
            }
        }

        /// <summary>Record a missing / empty / corrupt asset straight to the file (always written; no console spam).</summary>
        public static void MissingAsset(string kind, string path, string why = null)
            => Write("MISS", kind + " " + path + (string.IsNullOrEmpty(why) ? "" : " — " + why), null);

        /// <summary>Write a line straight to the log file (bypasses the Unity console).</summary>
        public static void Note(string tag, string msg) => Write(tag, msg, null);

        private static void Write(string tag, string msg, string stack)
        {
            var path = _path;
            if (path == null) return;
            try
            {
                var sb = new StringBuilder(msg == null ? 32 : msg.Length + 24);
                sb.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append("  ").Append(tag).Append("  ").Append(msg).Append('\n');
                if (!string.IsNullOrEmpty(stack)) sb.Append(stack).Append('\n');
                lock (_gate) File.AppendAllText(path, sb.ToString());
            }
            catch { /* never let logging throw into the game loop */ }
        }
    }
}
