using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using Sdo.Game;

namespace Sdo.UI
{
    /// <summary>
    /// DEAD-FILE PROBE. Runs ONLY when the env var / EditorPref <c>SDO_PROBE</c> is set (wired from
    /// <see cref="FrontendApp"/>'s boot, which suppresses both the front-end UI and gameplay in probe mode).
    ///
    /// Purpose: reproduce — WITHOUT playing — the exact <c>File</c> reads the game makes across the WHOLE catalog
    /// (every song, every scene, every reachable note/effect skin, every camera, every motion the game can pick,
    /// the referenced UI art, and the full costume tree). An OS-level trace (Process Monitor, filtered to
    /// dance.exe reads under DATA\) records the paths it opens; diffing that used-set against a full listing of
    /// DATA\ yields the files no code path touches. See tools/run_probe_trace.ps1 + tools/prune_dead_data.ps1.
    ///
    /// It writes NOTHING itself and creates NO Unity graphics objects: for coverage it only needs each read to
    /// FIRE, so leaf files are "touched" (open + read a header) and only DPS/CDT are byte-parsed to discover the
    /// files THEY reference. That makes it OOM-proof, fast, and -nographics-safe.
    ///
    /// DELIBERATE granularity (safety-first — err toward KEEPING):
    ///   • AVATAR / 3DEFT / 3DNOTES / SE / LOADING — swept WHOLE (kept). The user keeps all 38k costumes; 3DEFT
    ///     is tiny and would otherwise need EFT-emitter index parsing. These folders are not prune targets.
    ///   • MOTION / AUMOTION / DANCE / CAMERA / EFFECT / NOTEIMAGE / MUSIC — driven by the game's ACTUAL selection
    ///     logic (catalogs, DPS contents, the CDT map×count selector, the hardcoded note-skin/motion tables), so
    ///     unreachable files fall out as dead. These are where the reclaim comes from.
    ///   • UI — only the folders a screen actually resolves are swept; unreferenced UI folders fall out.
    ///
    /// Sources for the hardcoded reachable sets are cited inline (recovered by a multi-agent read of the loaders).
    /// </summary>
    public sealed class UsedAssetsProbe : MonoBehaviour
    {
        private int _touched, _missing;
        private float _t0;
        private StreamWriter _log;          // self-record of touched DATA-relative paths (ground-truth used-set)
        private string _logPath;

        /// <summary>Spawn the probe (called by FrontendApp.Boot when SDO_PROBE is set). If the env value is a
        /// directory it overrides <see cref="SdoExtracted.Root"/> so the trace maps onto that exact DATA tree.</summary>
        public static bool LaunchIfRequested()
        {
            var v = ScreenGameplay.DevVar("SDO_PROBE");
            if (string.IsNullOrEmpty(v) || v == "0") return false;
            try { if ((v.Contains("/") || v.Contains("\\")) && Directory.Exists(v)) SdoExtracted.Root = v; } catch { }
            var go = new GameObject("UsedAssetsProbe");
            DontDestroyOnLoad(go);
            go.AddComponent<UsedAssetsProbe>();
            return true;
        }

        private void Start() { StartCoroutine(Run()); }

        private IEnumerator Run()
        {
            _t0 = Time.realtimeSinceStartup;
            OpenLog();
            Note($"probe START  Root={SdoExtracted.Root}  log={_logPath ?? "(none)"}");

            // Order is irrelevant to coverage (procmon unions everything); grouped for readable progress.
            yield return Section("keep-whole trees", ProbeKeepWholeTrees());
            yield return Section("UI art",          ProbeUi());
            yield return Section("note/effect skins", ProbeNoteAndEffectSkins());
            yield return Section("scenes+mapobjs",  ProbeScenes());
            yield return Section("cameras",         ProbeCameras());
            yield return Section("motion constants", ProbeMotionConstants());
            yield return Section("songs (gn/ogg/dps + dance clips)", ProbeSongs());

            Note($"probe DONE   touched={_touched} missing={_missing}  {Time.realtimeSinceStartup - _t0:F1}s");
            yield return null;
            Quit();
        }

        private IEnumerator Section(string name, IEnumerator body)
        {
            int before = _touched;
            float t = Time.realtimeSinceStartup;
            Note($"  [{name}] ...");
            // guarded so one subsystem's failure never aborts the whole sweep
            while (true)
            {
                object cur; bool moved;
                try { moved = body.MoveNext(); cur = body.Current; }
                catch (Exception e) { Note($"  [{name}] EXC {e.Message}"); break; }
                if (!moved) break;
                yield return cur;
            }
            Note($"  [{name}] +{_touched - before} touched  ({Time.realtimeSinceStartup - t:F1}s)");
        }

        // ---------------------------------------------------------------- touch primitives

        // The probe self-records every SUCCESSFUL open to a plain-text file — this IS the ground-truth used-set
        // (the probe is the single choke point that opens every file, so its own list is complete). Procmon is the
        // independent OS-level cross-check (tools/run_probe_trace.ps1). Path: env SDO_PROBE_LOG, else <exeDir>/used_files.txt.
        private void OpenLog()
        {
            try
            {
                _logPath = ScreenGameplay.DevVar("SDO_PROBE_LOG");
                if (string.IsNullOrEmpty(_logPath))
                {
                    var exeDir = Directory.GetParent(Application.dataPath)?.FullName ?? ".";
                    _logPath = Path.Combine(exeDir, "used_files.txt");
                }
                _log = new StreamWriter(_logPath, false, new System.Text.UTF8Encoding(false));
            }
            catch { _log = null; _logPath = null; }
        }

        // Record a successful open as a lower-case, backslash, DATA-relative path (matches tools/prune_dead_data.ps1).
        private void LogHit(string abs)
        {
            if (_log == null) return;
            try
            {
                var root = SdoExtracted.Root;
                var rel = abs;
                if (abs.Length > root.Length && abs.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    rel = abs.Substring(root.Length).TrimStart('\\', '/');
                _log.WriteLine(rel.Replace('/', '\\').ToLowerInvariant());
            }
            catch { }
        }

        // Open + read a 64-byte header: fires a CreateFile + ReadFile SUCCESS that procmon records, without the
        // cost of reading whole (multi-MB) oggs/dds. FileShare.ReadWrite avoids any transient lock. Never throws.
        private void Touch(string abs)
        {
            try
            {
                if (string.IsNullOrEmpty(abs) || !File.Exists(abs)) { _missing++; return; }
                using (var fs = new FileStream(abs, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var b = new byte[64];
                    fs.Read(b, 0, b.Length);
                }
                _touched++;
                LogHit(abs);
            }
            catch { }
        }

        // DATA-relative touch. rel uses '/'; normalised to the OS separator.
        private void TouchRel(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return;
            Touch(Path.Combine(SdoExtracted.Root, rel.Replace('/', Path.DirectorySeparatorChar)));
        }

        // Touch every file under a DATA-relative folder (recursive). Missing folder = no-op.
        // skipExts (lower-case, with dot) lets a sweep exclude extensions the loaders never open — e.g. the UI sweep
        // skips .dds (UI 2D art is .an/.png/.bmp only; SdoExtracted can't decode DDS) so the redundant source atlases
        // aren't kept. Without this, a whole-folder sweep over-keeps DDS/junk siblings.
        private IEnumerator TouchDirRel(string rel, bool recurse = true, HashSet<string> skipExts = null)
        {
            var abs = Path.Combine(SdoExtracted.Root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(abs)) yield break;
            IEnumerator<string> it = null;
            try { it = EnumFiles(abs, recurse).GetEnumerator(); } catch { yield break; }
            int i = 0;
            while (true)
            {
                bool has; string f = null;
                try { has = it.MoveNext(); if (has) f = it.Current; }
                catch { break; }
                if (!has) break;
                if (skipExts == null || !skipExts.Contains(Path.GetExtension(f).ToLowerInvariant())) Touch(f);
                if ((++i & 1023) == 0) yield return null;   // breathe every 1024 files
            }
        }

        private static IEnumerable<string> EnumFiles(string dir, bool recurse)
            => Directory.EnumerateFiles(dir, "*", recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);

        private byte[] ReadAll(string rel)
        {
            try
            {
                var abs = Path.Combine(SdoExtracted.Root, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(abs)) { _missing++; return null; }
                var b = File.ReadAllBytes(abs);
                _touched++;
                LogHit(abs);
                return b;
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------- keep-whole trees

        private IEnumerator ProbeKeepWholeTrees()
        {
            // Not prune targets — swept whole so nothing in them is ever flagged dead.
            yield return TouchDirRel("AVATAR");     // full costume mesh+texture catalog (user keeps all 38k)
            yield return TouchDirRel("3DEFT");      // particle .EFT + GENERIC/XMESH (avoids EFT-emitter index parsing)
            yield return TouchDirRel("3DNOTES");    // 3D note highway meshes/textures
            yield return TouchDirRel("SE");         // already name-referenced-pruned by build_clean_data
            yield return TouchDirRel("LOADING");    // gameplay loading screens
            yield return TouchDirRel("PROFILE");    // per-user writable (also on the prune keep-list)
            yield return TouchDirRel("BGM");        // lobby bgm's ship home at the DATA root (dev trees keep UI/BGM — see UiDirs)
        }

        // ---------------------------------------------------------------- UI art

        // Only the UI folders a screen actually resolves (per the *Art.cs resolvers). Everything else under UI/
        // (BG, EMBLEM, INVITEDLG, ITEMDLG, ITEM2D_PACK*, LOGINDLG, MATCHITEMS, REPLAY, TIPS, LOBBYDLG\* except
        // KEYS, MUSIC\* except ICONS, STATIS\* except STATISTIC) is left untouched → dead.
        private static readonly string[] UiDirs =
        {
            "UI/GAMEPLAY",          // HUD + PKSCORE + PLAYSHOWTIME (kept whole; a few TEAM/FREE dupes ride along)
            "UI/ARROW",             // head-marker arrow frames
            "UI/PLAYINGEXP",        // in-play emoji cut-ins
            "UI/MUSIC/ICONS",       // song-select cover icons
            "UI/STATIS/STATISTIC",  // result screen (the ONLY STATIS subtree the active ResultScreen reads)
            "UI/ROOMDLG", "UI/ROOM",
            "UI/OPTIONDLG", "UI/SHOP", "UI/MYHOUSEDLG",
            "UI/PLAYERINFORMATIONDLG",   // 房間右鍵點人的個人資訊/戰績面板 (PlayerInfoArt) — 線上限定資料夾
            "UI/BUBBLE2", "UI/EXPRESSIONS",
            "UI/LOBBYDLG/KEYS",     // keyboard glyphs (the only LOBBYDLG subtree read)
            "UI/LOBBYSEL",          // gender-select art
            "UI/BGM",               // front-end random background music
        };

        // Extensions the UI 2D loaders never open — original DDS atlases (converted to PNG at extraction) + tool/junk.
        private static readonly HashSet<string> UiSkipExts =
            new HashSet<string> { ".dds", ".dds_old", ".png_old", ".rar", ".exe", ".db", ".bat", ".bak" };

        private IEnumerator ProbeUi()
        {
            foreach (var d in UiDirs) yield return TouchDirRel(d, recurse: true, skipExts: UiSkipExts);
        }

        // ---------------------------------------------------------------- note boards + effect skins

        // Reachable EFFECT skin folders. From ScreenGameplay.Effects.cs note-skin tables:
        //   NoteTypeEftSuffix {2,5,8,9,10,11,7,12,13,14,PET} + boot EftDir(2)/EftDir(13) + ShowTime;
        //   combo sources PUBLICEFT / PUBLICEFT2 / EFT_5/8/9/10/PET;
        //   GAMEOVER suffixes reachable from NoteTypeBoardSuffix {5,6,8,9,10,11,PET} → {2,5,8,9,10} (+ base).
        // Unlisted EFFECT folders (EFT_1, O2JAM_EFT_*, GAMEOVER0/1/3, EFT_DRUM/MOVEUP, *.DGE, loose art) → dead.
        private static readonly string[] EffectSkinDirs =
        {
            "EFFECT/EFT_2","EFFECT/EFT_5","EFFECT/EFT_7","EFFECT/EFT_8","EFFECT/EFT_9","EFFECT/EFT_10",
            "EFFECT/EFT_11","EFFECT/EFT_12","EFFECT/EFT_13","EFFECT/EFT_14","EFFECT/EFT_PET","EFFECT/EFT_SHOWTIME",
            "EFFECT/PUBLICEFT","EFFECT/PUBLICEFT2",
            "EFFECT/GAMEOVER","EFFECT/GAMEOVER2","EFFECT/GAMEOVER5","EFFECT/GAMEOVER8","EFFECT/GAMEOVER9","EFFECT/GAMEOVER10",
        };

        // Reachable NOTEIMAGE board skins. NoteTypeBoardSuffix {6,6,8,9,10,6,5,5,5,11,PET} + ShowTime swap.
        // Unlisted (JAM_NOTEIMAGE, SIX_*, DRUM_*, MOVEUP_1, *.DGN, ITEMS, notes_board2..7) → dead.
        private static readonly string[] NoteImageDirs =
        {
            "NOTEIMAGE/NOTEIMAGE_5","NOTEIMAGE/NOTEIMAGE_6","NOTEIMAGE/NOTEIMAGE_8","NOTEIMAGE/NOTEIMAGE_9",
            "NOTEIMAGE/NOTEIMAGE_10","NOTEIMAGE/NOTEIMAGE_11","NOTEIMAGE/NOTEIMAGE_PET","NOTEIMAGE/NOTEIMAGE_SHOWTIME",
        };

        private IEnumerator ProbeNoteAndEffectSkins()
        {
            foreach (var d in EffectSkinDirs) yield return TouchDirRel(d);
            foreach (var d in NoteImageDirs) yield return TouchDirRel(d);
            // shared board files at NOTEIMAGE root (BuildBoard / LoadArt)
            TouchRel("NOTEIMAGE/notes_board1.png");
            for (int i = 1; i <= 4; i++) TouchRel("NOTEIMAGE/notes_board_click" + i + ".png");
            yield break;
        }

        // ---------------------------------------------------------------- scenes + mapobjs

        private IEnumerator ProbeScenes()
        {
            var sceneRoot = Path.Combine(SdoExtracted.Root, "SCENE");
            if (!Directory.Exists(sceneRoot)) yield break;
            string[] folders;
            try { folders = Directory.GetDirectories(sceneRoot); } catch { yield break; }

            foreach (var full in folders)
            {
                var F = Path.GetFileName(full);
                if (string.Equals(F, "MAPOBJ", StringComparison.OrdinalIgnoreCase)) continue;   // shared prop tree (driven below)

                // (a) the stage folder itself (SCENE.MSH + its .dds) — every scene folder is reachable, keep whole
                yield return TouchDirRel("SCENE/" + F);

                // (b) mapobj props for this scene (catalog-driven → only referenced MAPOBJ subfolders are kept)
                foreach (var g in SceneMapobjCatalog.ForFolder(F))
                    yield return TouchDirRel("SCENE/MAPOBJ/" + g.Folder);

                // (c) scene NPCs ("場景的人") — SCN0017 only. AVATAR side is kept whole already; the MOTION clips are
                //     precise-driven, so touch them explicitly.
                foreach (var a in SceneAvatarCatalog.ForFolder(F))
                {
                    if (!string.IsNullOrEmpty(a.Mot)) TouchMotBothGenders(a.Mot);   // MOTION/*_CHANGJING.MOT
                }

                // (d) persistent background EFTs (3DEFT is kept whole already, but touch by name for completeness)
                foreach (var e in SceneEftCatalog.ForFolder(F))
                    if (!string.IsNullOrEmpty(e.Eft)) TouchRel("3DEFT/" + e.Eft + ".EFT");
            }
        }

        // ---------------------------------------------------------------- cameras (DATA/CAMERA)

        // Reachable CDT set = the map×count selector's whole table (SoloCdt ∪ GroupCdt) + the {1,3,6} fallbacks
        // (ScreenGameplay.SelectCdtPath / SoloCdt / GroupCdt). Each reachable CDT is parsed and every CV it names is
        // touched. Any CAMERA/*.CDT or *.CV neither in a reachable CDT nor a fallback → dead.
        private static readonly string[] CdtStems =
        {
            "Garage_1","sea_1","Christmas_","playground_","sky_1","egypt_1","palace_1","huache_1",
            "fifa_1","ocean_1","Ghosthill_1","street_1","railway_1","houseboat_1",
            "Garage","sea","Christmas","playground","sky","egypt","palace","huache",
            "fifa","ocean","Ghosthill","street","railway","houseboat",
            "1","3","6",
        };

        private IEnumerator ProbeCameras()
        {
            foreach (var stem in CdtStems)
            {
                var rel = "CAMERA/" + stem + ".CDT";
                var bytes = ReadAll(rel);
                if (bytes == null) continue;
                CdtLoader cdt = null;
                try { cdt = CdtLoader.Load(bytes); } catch { }
                if (cdt?.Shots != null)
                    foreach (var s in cdt.Shots)
                        if (!string.IsNullOrEmpty(s.CvRelPath)) TouchRel("CAMERA/" + s.CvRelPath);
                yield return null;
            }
            TouchRel("CAMERA/1/CAM0000/000.CV");   // CvCameraPitchUp fixed shot
        }

        // ---------------------------------------------------------------- motion constants (non-DPS picks)

        // Every hardcoded motion the game can pick outside a song's DPS. Recovered from ScreenGameplay (dancer
        // idle/dance/win/lose), SdoRoomAvatar + RoomLayout (room idle/walk/seat/spectator), RoomChatCommand (cat 24
        // chat actions), GenderPreview3D + WardrobeScreen (previews), SpecialMotionItems (fly), SceneAvatarCatalog
        // (_CHANGJING NPCs). Both-gender siblings are also touched (male play remaps W→M).
        private static readonly string[] MotionConstants =
        {
            // gameplay dancer
            "WDANCE0002.MOT","MDANCE0002.MOT","WREST0072.MOT","MREST0082.MOT",
            "WWIN0002.MOT","MWIN0001.MOT","WLOST0003.MOT","MREST0004.MOT",
            // scene NPCs (SCN0017)
            "WDANCE0001_CHANGJING.MOT","MDANCE0001_CHANGJING.MOT","MDANCE0002_CHANGJING.MOT",
            // room chat actions (category 24, 8 per gender)
            "WREST0058.MOT","WREST0059.MOT","WREST0073.MOT","WREST0061.MOT","WREST0062.MOT","WREST0063.MOT","WREST0064.MOT","WREST0074.MOT",
            "MREST0070.MOT","MREST0071.MOT","MREST0083.MOT","MREST0073.MOT","MREST0074.MOT","MREST0075.MOT","MREST0076.MOT","MREST0084.MOT",
            // room seat idle + lobby idle/walk
            "WREST0056.MOT","MREST0067.MOT","WWALK0001.MOT","MWALK0001.MOT",
            // gender-select + wardrobe previews
            "MREST0002_01.MOT","MREST0002_02.MOT","WREST0011.MOT","WREST0013.MOT","WREST0016.MOT",
            // flying-wing special idle/glide
            "FLYSTAY_NV.MOT","FLYSTAY_NAN.MOT","FLY_NV.MOT","FLY_NAN.MOT",
        };

        private IEnumerator ProbeMotionConstants()
        {
            foreach (var m in MotionConstants) TouchMotBothGenders(m);
            // spectator waiting clips WWAITING001..012 / MWAITING001..012 (RoomLayout; 12 exist, 10 consumed)
            for (int i = 1; i <= 12; i++)
            {
                TouchMotBothGenders("WWAITING" + i.ToString("000") + ".MOT");
                TouchMotBothGenders("MWAITING" + i.ToString("000") + ".MOT");
            }
            yield break;
        }

        // Touch a motion under BOTH AUMOTION and MOTION (ResolveMot tries AUMOTION first), for BOTH genders
        // (male play remaps a leading W→M and vice-versa). Over-touching motions is safe (keeps a few extra).
        private void TouchMotBothGenders(string name)
        {
            TouchMot(name);
            var flip = GenderFlip(name);
            if (flip != null) TouchMot(flip);
        }

        private void TouchMot(string name)
        {
            TouchRel("AUMOTION/" + name);
            TouchRel("MOTION/" + name);
        }

        private static string GenderFlip(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            char c = name[0];
            if (c == 'W') return "M" + name.Substring(1);
            if (c == 'w') return "m" + name.Substring(1);
            if (c == 'M') return "W" + name.Substring(1);
            if (c == 'm') return "w" + name.Substring(1);
            return null;
        }

        // ---------------------------------------------------------------- songs: gn + ogg + preview + per-song DPS + dance clips

        // Match a *.mot filename token anywhere in a DPS's bytes (version-agnostic — DpsLoader parses PAS00003 only,
        // but 35 legacy PAS00002 charts also carry clip names we must keep). Bytes are treated as latin-1 text.
        private static readonly Regex MotToken = new Regex(@"[0-9A-Za-z_]+\.mot", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private IEnumerator ProbeSongs()
        {
            // reachable DANCE = { <SongFileId>.DPS } ∪ the 22 breakdance charts
            var breaking = new List<string>();
            for (int n = 1; n <= 6; n++) breaking.Add("BREAKING_E_" + n + ".DPS");
            for (int n = 1; n <= 8; n++) breaking.Add("BREAKING_N_" + n + ".DPS");
            for (int n = 1; n <= 8; n++) breaking.Add("BREAKING_H_" + n + ".DPS");
            foreach (var b in breaking) ScanDpsForMots("DANCE/" + b);

            int i = 0;
            var seenFileId = new HashSet<int>();
            // CURATED list only: the browse UI shows / plays only the 'k' variant of each sdomNNNNk/t.gn pair
            // (SongListModel.Curate). The 't' charts + short tutorial gn (sdomN.gn) are NEVER loaded as files — their
            // catalog ENTRIES survive for title/artist lookups + font warmup, but the gn/ogg FILES are dead. Iterating
            // SongCatalog.All here (the old bug) over-kept ~185 MB of t.gn + tutorial oggs.
            var songs = Sdo.UI.Catalog.SongListModel.FromCatalog().All;
            foreach (var e in songs)
            {
                if (e == null) continue;
                // chart (k and t are separate catalog entries → both touched by iterating All)
                if (!string.IsNullOrEmpty(e.gn)) TouchRel("MUSIC/" + e.gn);
                // main audio: sdomNNNN.ogg (k/t share one). External songs (no sdom token) skip — not in DATA/MUSIC.
                var m = Regex.Match(e.gn ?? "", @"sdom\d+");
                if (m.Success) TouchRel("MUSIC/" + m.Value + ".ogg");
                // per-song choreography + its referenced dance clips (once per fileId; k/t share it)
                if (seenFileId.Add(e.fileId))
                {
                    ScanDpsForMots("DANCE/" + e.fileId + ".DPS");
                    TouchRel("MUSIC/exper/" + e.fileId + ".ogg");   // song-select preview
                }
                if ((++i & 255) == 0) { Note($"    songs {i}/{songs.Count}"); yield return null; }
            }
        }

        // Touch the DPS file, then touch every AUMOTION/ + MOTION/ clip it names (both genders).
        private void ScanDpsForMots(string dpsRel)
        {
            var bytes = ReadAll(dpsRel);
            if (bytes == null) return;
            string text;
            try { text = System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes); }
            catch { text = System.Text.Encoding.ASCII.GetString(bytes); }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match mt in MotToken.Matches(text))
                if (seen.Add(mt.Value)) TouchMotBothGenders(mt.Value);
        }

        // ---------------------------------------------------------------- exit / logging

        private void Note(string msg)
        {
            Debug.Log("[probe] " + msg);   // stdout in batchmode / Player.log
            try { SdoLog.Note("PROBE", msg); } catch { }
        }

        private void Quit()
        {
            try { if (_log != null) { _log.Flush(); _log.Dispose(); _log = null; } } catch { }
            try { SdoLog.Note("PROBE", "quit  used_files=" + _logPath); } catch { }
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit(0);
#endif
        }
    }
}
