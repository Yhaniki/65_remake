using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Sdo.Osu;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// The dance of an external (user <c>Songs/</c>) song. osu!/StepMania charts ship no choreography, so the dancer
    /// used to fall back to one looping clip; instead one is GENERATED ONCE, exactly like the CD disc:
    /// <see cref="RandomDps"/> plans it, the .dps is written into the song's own folder, and its name is recorded in
    /// that folder's <see cref="SongSidecar"/> (<c>sdoinfo.dat</c>) — the file the disc is already recorded in. Every
    /// later play reads the sidecar and loads the file; nothing is regenerated. (Delete the <c>#DPS</c> line to get a
    /// fresh one.)
    ///
    /// The dance is DETERMINISTIC: the RNG seed is the SHA-256 of the song's DIFFICULTY CHARTS and nothing else —
    /// not the folder's name, not the file names, not the audio. So the same charts always dance the same way: on
    /// another machine, after the sidecar is deleted, after the library is moved or renamed, and — the case this rule
    /// exists for — on both sides of 缺歌傳檔.
    ///
    /// 🔴 A song that reached this machine through 缺歌傳檔 lands in
    /// <c>ADDON/SONG/connect/&lt;歌名 - 作者 [packId 前 8 碼]&gt;/</c> — the sender's own folder name is never on the wire
    /// (see <c>NetSongFetcher.ConnectFolderName</c>), and the .dps itself is not transferred either
    /// (<c>SongPackFilter</c> drops <c>dance*.dps</c>; the receiver regenerates it). Seeding off the folder name gave
    /// the two sides two different seeds → the RNG diverged on its very first draw → 同一首歌、同一場,兩邊的舞者跳完全
    /// 不同的舞。The charts, on the other hand, cross byte-for-byte: they are the files whose SHA-256 the manifest
    /// actually carries (<see cref="SongPackId.NeedsContentHash"/>) and that the receiver re-verifies file by file.
    ///
    /// The charts are also exactly what the PLAN is measured from (span + BPM, see <see cref="DanceInputs"/>), so
    /// seed and inputs are one and the same source: everything that can change the dance is in the seed, and nothing
    /// that can't (audio, artwork, folder layout) is. An edited chart is a different dance — correctly so.
    ///
    /// 🔴 The fingerprint must not depend on the ORDER of the slots. Which chart is 簡單/普通/困難 is decided per
    /// machine by <c>RoomConfig.difficultyCalc</c> (minacalc vs osu — see <c>SongCatalog.Entry.SortSlotsByDisplayLevel</c>),
    /// so two players can hold the same three charts in different slots. The hashes are therefore sorted, i.e. taken
    /// as a SET — which is also how <see cref="DanceInputs.UnionSeconds"/> reads them.
    ///
    /// It is also the SAME dance whichever difficulty is played: everything the plan depends on — the seed, the length
    /// and the beat grid — is per-SONG (see <see cref="DanceInputs"/>), never per-chart. The dance still starts on the
    /// played difficulty's own first note (ScreenGameplay._danceStartSec), so the dancer holds its standby idle through
    /// the intro and starts moving on the first note; only WHAT it dances is shared.
    /// </summary>
    public static class ExternalDps
    {
        /// <summary>Shortest span worth choreographing (s).</summary>
        private const double MinDanceSeconds = 4.0;

        /// <summary>
        /// Absolute path of the song's .dps — the recorded one, else one generated (and recorded) now, else "" (the
        /// caller then keeps the fallback dance clip).
        /// </summary>
        /// <param name="map">The loaded chart — only the FALLBACK source of length/tempo, for a song whose charts can't
        /// be measured or whose BPM is unknown (see <see cref="DanceInputs"/>); planning off it directly is what used
        /// to make the dance depend on the difficulty played first.</param>
        /// <param name="songBpm">The catalog's per-song BPM (&lt;= 0 = unknown → the chart's own).</param>
        /// <param name="chartFormat">Sdo.Osu.SongFormat of this song's charts (1=osu, 2=sm, 3=.gn 包, 4=.mc).</param>
        /// <param name="chartSeed">.gn pack key, for a packed song's charts.</param>
        /// <param name="chartPaths">EVERY difficulty's chart file (empty slots may be "" / null) — their note windows,
        /// unioned, are the dance's length.</param>
        /// <param name="chartIndices">Per-slot .sm block / .gn difficulty index, parallel to <paramref name="chartPaths"/>.</param>
        /// <param name="packId">The folder's cross-machine identity (<c>SongCatalog.Entry.packId</c>) — the FALLBACK
        /// seed, for the song whose charts can't be read at all; "" → the folder name (see <see cref="SeedFor"/>).</param>
        public static string EnsureFor(string folderPath, string songKey, string packId, OsuBeatmap map, double songBpm,
                                       int chartFormat, long chartSeed,
                                       IReadOnlyList<string> chartPaths, IReadOnlyList<int> chartIndices)
        {
            if (string.IsNullOrEmpty(folderPath) || map == null || map.HitObjects.Count == 0) return "";
            songKey = songKey ?? "";

            string text = SongSidecar.ReadText(folderPath);   // current name, else the legacy sdo.header
            var recorded = SongSidecar.Find(SongSidecar.Parse(text), songKey);
            if (recorded != null && !string.IsNullOrEmpty(recorded.Dps) && recorded.DpsVersion >= SongSidecar.DpsGenerator)
            {
                string had = Path.Combine(folderPath, recorded.Dps);
                if (File.Exists(had)) return had;            // generated by an earlier run → reuse, never rebuild
            }
            // …but a dance an OLDER generator built is rebuilt once: a fix to the choreography has to reach the songs
            // the player has already danced (the seed is unchanged, so the new dance is still this song's own).

            // Length and tempo come from the SONG, not from the chart in hand — otherwise the same song choreographs
            // differently depending on which difficulty reached this first (see DanceInputs). Every difficulty is
            // re-read here, as written: this runs once per song, and gameplay's copy of the chart has already been
            // through lead-in/collapse by now.
            var windows = ExternalChartIO.Windows(chartFormat, chartPaths, chartIndices, chartSeed);
            var inputs = DanceInputs.For(DanceInputs.UnionSeconds(windows), songBpm,
                                         (map.LastNoteMs - map.FirstNoteMs) / 1000.0, map.Bpm);
            if (inputs.Seconds < MinDanceSeconds) return "";
            if (DpsMotionLibrary.Pool.Count == 0) return "";  // no motions in the data tree → nothing to plan with

            // One identity, hashed once: the charts are read here (SHA-256), and asking for it twice would read them twice.
            string identity = IdentityOf(chartPaths, chartIndices, folderPath, songKey, packId);
            uint seed = RandomDps.Fnv(identity);

            var req = new RandomDpsRequest
            {
                Bpm = inputs.Bpm,
                DanceSeconds = inputs.Seconds,
                Pool = DpsMotionLibrary.Pool,
                Intros = DpsMotionLibrary.Intros,
                Groups = DpsMotionLibrary.Groups,
                FrameCount = DpsMotionLibrary.Frames,
                Seed = seed,
                ChartName = "ext_" + seed.ToString("x8") + ".gn",
            };

            byte[] dps = RandomDps.Build(req);
            if (dps.Length == 0) { SdoLog.Note("dps", "nothing planned for " + folderPath); return ""; }

            string file = SongSidecar.DpsFileName(songKey);
            string path = Path.Combine(folderPath, file);
            try
            {
                File.WriteAllBytes(path, dps);
                SongSidecar.WriteText(folderPath, SongSidecar.SetDps(text, songKey, file));
            }
            catch (System.Exception ex)
            {
                SdoLog.Note("dps", "could not write " + path + ": " + ex.Message);
                return "";                                   // read-only folder → dancer keeps the fallback clip
            }
            Debug.Log($"[dps] generated {file} for {Path.GetFileName(folderPath)}: {inputs.Seconds:F1}s @ {req.Bpm:F1} bpm, " +
                      $"seed {req.Seed:x8} (from {SeedSource(identity)}), {windows.Count} difficult(y/ies) measured" +
                      $"{(inputs.PerSong ? "" : " — FELL BACK to this chart's own span/bpm")}");
            return path;
        }

        /// <summary>The RNG seed of this song's dance — its charts' content (see the class remarks). Public for tests.</summary>
        public static uint SeedFor(IReadOnlyList<string> chartPaths, IReadOnlyList<int> chartIndices,
                                   string folderPath, string songKey, string packId)
            => RandomDps.Fnv(IdentityOf(chartPaths, chartIndices, folderPath, songKey, packId));

        // What "the same song" MEANS, in order of preference:
        //   • its CHARTS' content — the rule. Nothing outside the charts takes part, so the identity survives every
        //     renaming the transfer (or the player) does to the folder around them.
        //   • packId — the fallback when not one chart could be hashed (deleted mid-play, unreadable, a song whose
        //     charts live inside a .gn pack this build can't open). Still cross-machine, just coarser: it also moves
        //     when the artwork or the audio does.
        //   • the folder's NAME — the last resort (no packId either: an unscannable folder, a pre-v10 scan cache).
        // The folder's full PATH never takes part at any level: a library moved to another drive must not re-choreograph.
        private static string IdentityOf(IReadOnlyList<string> chartPaths, IReadOnlyList<int> chartIndices,
                                         string folderPath, string songKey, string packId)
        {
            string charts = ChartsFingerprint(chartPaths, chartIndices);
            if (charts.Length > 0) return charts;

            string key = (songKey ?? "").ToLowerInvariant();
            if (!string.IsNullOrEmpty(packId)) return packId.ToLowerInvariant() + "|" + key;

            string leaf = "";
            try { leaf = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); }
            catch { leaf = folderPath ?? ""; }
            return (leaf ?? "").ToLowerInvariant() + "|" + key;
        }

        /// <summary>Which of the three identities the seed ended up on — the log line's way of saying "this song will
        /// NOT match the same song on another machine" when it had to fall back.</summary>
        private static string SeedSource(string identity)
        {
            if (identity.StartsWith("charts|", StringComparison.Ordinal)) return "charts";
            if (identity.StartsWith(SongPackId.Prefix, StringComparison.Ordinal)) return "packId — CHARTS UNREADABLE";
            return "folder name — no charts, no packId";
        }

        /// <summary>
        /// The song's charts as a SET of "&lt;file sha256&gt;:&lt;index&gt;" — sorted (slot order is a local display
        /// setting, see the class remarks) and de-duplicated (a .sm / .gn holds every difficulty in ONE file, so the
        /// same path arrives three times with three indices — the index is what tells them apart).
        /// "" = not one chart could be read → the caller falls back.
        /// </summary>
        private static string ChartsFingerprint(IReadOnlyList<string> chartPaths, IReadOnlyList<int> chartIndices)
        {
            if (chartPaths == null) return "";
            var parts = new List<string>(3);
            var hashed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // one read per FILE, not per slot
            for (int i = 0; i < chartPaths.Count; i++)
            {
                string path = chartPaths[i];
                if (string.IsNullOrEmpty(path)) continue;

                string sha;
                if (!hashed.TryGetValue(path, out sha)) hashed[path] = sha = SongPackId.HashFile(path) ?? "";
                if (sha.Length == 0) continue;   // unreadable → this slot simply isn't part of the identity

                int index = chartIndices != null && i < chartIndices.Count ? chartIndices[i] : 0;
                string part = sha + ":" + index.ToString(CultureInfo.InvariantCulture);
                if (!parts.Contains(part)) parts.Add(part);
            }
            if (parts.Count == 0) return "";
            parts.Sort(StringComparer.Ordinal);
            return "charts|" + string.Join(";", parts.ToArray());
        }
    }
}
