using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Sdo.Settings
{
    /// <summary>
    /// **單首歌的 offset**（毫秒）——StepMania 的 song offset、osu 的 beatmap offset。
    ///
    /// 跟 <see cref="RoomConfig.globalOffsetMs"/>（全域，補這台機器的整條延遲）是兩回事：這個是補**那一首譜**
    /// 本身跟音檔對不齊（很常見：譜是別人打的、音檔換過版本）。兩者相加才是實際套在譜面時鐘上的量。
    ///
    /// 正 = 音符相對音樂**往後**（判定與畫面都延後）。譜面聽起來「音符比音樂早」就調正的。
    ///
    /// .gn 是唯讀的（而且還加密），改不進去 → 存成執行檔同層的 <c>song_offsets.ini</c>（跟 config.ini 同一層，
    /// 純文字可手改）。key = .gn 檔名（小寫）。
    /// </summary>
    public static class SongOffsets
    {
        public const string FileName = "song_offsets.ini";

        /// <summary>單次調整的步進（秒）——沿用 StepMania 編輯器：F11/F12 一次 0.02 秒，按住 Alt 微調 0.001 秒。</summary>
        public const double StepSec = 0.02;
        public const double FineStepSec = 0.001;

        /// <summary>合理範圍（毫秒）。超過這個量就不是 offset 沒對好，是譜/音檔根本配錯了。</summary>
        public const float MinMs = -2000f, MaxMs = 2000f;

        private static readonly Dictionary<string, float> _byGn = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static bool _loaded;

        public static string FilePath
        {
            get
            {
                string dir;
                try { dir = Directory.GetParent(Application.dataPath).FullName; }
                catch { dir = Application.dataPath; }
                return Path.Combine(dir, FileName);
            }
        }

        private static string Key(string gnPathOrName)
            => string.IsNullOrEmpty(gnPathOrName) ? "" : Path.GetFileName(gnPathOrName).ToLowerInvariant();

        /// <summary>該首歌的 offset（毫秒）；沒設過 = 0。</summary>
        public static float Get(string gnPathOrName)
        {
            EnsureLoaded();
            var k = Key(gnPathOrName);
            return k.Length > 0 && _byGn.TryGetValue(k, out var v) ? v : 0f;
        }

        /// <summary>設定並立刻寫檔（值為 0 就把該筆移除，檔案不會越長越髒）。</summary>
        public static void Set(string gnPathOrName, float ms)
        {
            EnsureLoaded();
            var k = Key(gnPathOrName);
            if (k.Length == 0) return;
            ms = Mathf.Clamp(ms, MinMs, MaxMs);
            if (Mathf.Abs(ms) < 0.0005f) _byGn.Remove(k);
            else _byGn[k] = ms;
            Save();
        }

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var raw in File.ReadAllLines(FilePath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line[0] == '[') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    if (float.TryParse(line.Substring(eq + 1).Trim(),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float v))
                        _byGn[k] = Mathf.Clamp(v, MinMs, MaxMs);
                }
            }
            catch (Exception e) { Debug.LogWarning("[SongOffsets] 讀取失敗: " + e.Message); }
        }

        public static void Save()
        {
            try { File.WriteAllText(FilePath, Serialize()); }
            catch (Exception e) { Debug.LogWarning("[SongOffsets] 寫入失敗: " + e.Message); }
        }

        /// <summary>純函式：輸出 ini 文字（測試用）。</summary>
        public static string Serialize()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("# 單首歌的 offset（毫秒）。正 = 音符相對音樂往後（音符聽起來比音樂早就調正的）。\n");
            sb.Append("# 在譜面編輯器裡用 F11 / F12 調（一次 20ms，按住 Alt 是 1ms），會自動寫回這裡。\n");
            sb.Append("# 這是「那一首譜跟音檔沒對齊」的補償；整台機器的延遲請調 config.ini 的 globalOffsetMs。\n");
            sb.Append("[SongOffsetMs]\n");
            var keys = new List<string>(_byGn.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (var k in keys)
                sb.Append(k).Append('=')
                  .Append(_byGn[k].ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            return sb.ToString();
        }

        /// <summary>測試用：清掉記憶體中的表（不動檔案）。</summary>
        public static void ResetForTests()
        {
            _byGn.Clear();
            _loaded = true;   // 不要再去讀檔（測試環境沒有那個檔）
        }

        /// <summary>測試用：不寫檔的設定。</summary>
        public static void SetInMemory(string gnPathOrName, float ms)
        {
            EnsureLoaded();
            var k = Key(gnPathOrName);
            if (k.Length == 0) return;
            ms = Mathf.Clamp(ms, MinMs, MaxMs);
            if (Mathf.Abs(ms) < 0.0005f) _byGn.Remove(k); else _byGn[k] = ms;
        }
    }
}
