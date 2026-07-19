// gn_note_export — 把每首 k.gn 的「音符時間 + type-10 音樂起點 marker」導成 JSON。
//
// 為什麼要有這支：measure_song_offsets.py 要把「譜面說第幾拍該有打點」跟「音檔實際的瞬態在哪」對起來，
// 而音符時間只有 C# 的 Sdo.Osu.GnChart 解得出來（Python 那邊只解表頭）。與其在 Python 重寫一份 .gn 解析器
// ——那樣量到的偏差可能只是兩個 parser 的分歧，整個量測就白做了—— 不如直接編譯**遊戲實際在用的那份原始碼**。
// Sdo.Osu 是 noEngineReferences（純邏輯），所以可以脫離 Unity 直接編成 console app。
//
//   dotnet run --project tools/gn_note_export -- <MUSIC 目錄> <gn_keytable.json> <輸出.json> [每首最多幾顆音符]
//
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Sdo.Osu;

namespace GnNoteExport
{
    internal static class Program
    {
        private const int DefaultMaxNotes = 400;   // 前 400 顆 ≈ 前 1~2 分鐘，足夠做互相關又不會讓 JSON 爆掉

        private static int Main(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("用法: gn_note_export <MUSIC 目錄> <gn_keytable.json> <輸出.json> [每首最多音符數]");
                return 2;
            }
            string musicDir = args[0], keyTablePath = args[1], outPath = args[2];
            int maxNotes = args.Length > 3 && int.TryParse(args[3], out int m) ? m : DefaultMaxNotes;

            uint[] allSeeds = LoadSeeds(keyTablePath, out var seedByGn);
            Console.Error.WriteLine($"keytable: {seedByGn.Count} 筆, {allSeeds.Length} 個相異 seed");

            // 只掃 k.gn（鍵盤譜）—— t 是毯子譜，共用同一個音檔，量一次就夠。
            var files = Directory.GetFiles(musicDir, "*.gn")
                                 .Where(p => Path.GetFileNameWithoutExtension(p).EndsWith("k", StringComparison.OrdinalIgnoreCase))
                                 .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                                 .ToArray();
            Console.Error.WriteLine($"k.gn: {files.Length} 個");

            var songs = new List<object>();
            int ok = 0, empty = 0;
            foreach (var path in files)
            {
                string gn = Path.GetFileName(path);
                string stem = Path.GetFileNameWithoutExtension(gn);
                stem = stem.Substring(0, stem.Length - 1).ToLowerInvariant();   // 去掉 k

                byte[] raw;
                try { raw = File.ReadAllBytes(path); }
                catch (Exception e) { Console.Error.WriteLine($"[skip] {gn}: {e.Message}"); continue; }

                uint[] seeds = seedByGn.TryGetValue(gn.ToLowerInvariant(), out uint s) && s != 0
                    ? new[] { s }.Concat(allSeeds.Where(x => x != s)).ToArray()   // 自己的 seed 先試，其餘當後備
                    : allSeeds;

                // 三個難度各解一次：音樂起點 marker 三者相同，但音符數不同 → 取音符最多的那個難度來對拍
                // （打點越密，互相關的峰越銳利）。
                OsuBeatmap best = null;
                int bestDiff = -1;
                for (int d = 0; d < 3; d++)
                {
                    OsuBeatmap map;
                    try { map = GnChart.Load(raw, d, seeds); }
                    catch { continue; }
                    if (map == null || map.HitObjects.Count == 0) continue;
                    if (best == null || map.HitObjects.Count > best.HitObjects.Count) { best = map; bestDiff = d; }
                }
                if (best == null) { empty++; continue; }

                // StartTimeMs 是 int（.gn 的音符時間本來就量化到整數毫秒）。
                var times = best.HitObjects
                                .Select(h => h.StartTimeMs)
                                .Distinct()                       // 同一 row 的多軌音符只算一個打點
                                .OrderBy(t => t)
                                .Take(maxNotes)
                                .ToArray();

                songs.Add(new
                {
                    gn = stem,                                    // 詞幹 sdomNNNN（= song_name_overrides 的 key，也是 .ogg 名）
                    diff = bestDiff,
                    bpm = Math.Round(best.Bpm, 4),
                    musicStartMs = Math.Round(best.MusicStartOffsetMs, 2),   // type-10：音符領先音樂的無聲數拍
                    totalNotes = best.HitObjects.Count,
                    noteMs = times,
                });
                ok++;
                if (ok % 250 == 0) Console.Error.WriteLine($"  … {ok} 首");
            }

            var doc = new
            {
                schema = "gn-note-export/1",
                note = "音符時間(ms, 譜面時間軸) + type-10 音樂起點。音檔上的時間 = noteMs - musicStartMs。",
                count = songs.Count,
                songs,
            };
            var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions
            {
                WriteIndented = false,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            File.WriteAllText(outPath, json, new UTF8Encoding(false));
            Console.Error.WriteLine($"完成: {ok} 首寫入 {outPath}（解不出音符 {empty} 首）");
            return 0;
        }

        // gn_keytable.json：{"songs":[{"gn":"sdom0001k.gn","enc":"sdom","seed":123,…}]}。
        // ddrm/plain 沒有 seed（GnChart 自己認得），所以「全部相異 seed」當後備清單就夠。
        private static uint[] LoadSeeds(string path, out Dictionary<string, uint> byGn)
        {
            byGn = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            var distinct = new List<uint>();
            var seen = new HashSet<uint>();

            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (!doc.RootElement.TryGetProperty("songs", out var arr)) return Array.Empty<uint>();
            foreach (var e in arr.EnumerateArray())
            {
                if (!e.TryGetProperty("gn", out var gnEl)) continue;
                string gn = gnEl.GetString() ?? "";
                if (gn.Length == 0) continue;
                if (e.TryGetProperty("seed", out var seedEl) && seedEl.TryGetInt64(out long v) && v != 0)
                {
                    uint u = unchecked((uint)v);
                    byGn[gn] = u;
                    if (seen.Add(u)) distinct.Add(u);
                }
            }
            return distinct.ToArray();
        }
    }
}
