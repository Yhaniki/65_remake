using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using Sdo.Osu;
using Sdo.Ruleset;

namespace Sdo.Game
{
    // Visual effects: click flash, burst FX, scene EFT spawn, HUD tick dispatch
    public partial class ScreenGameplay
    {
        private const float BurstSecPerFrame = 0.03f;     // ~30ms/frame -> 12 frames ≈ 0.36s (quick in-game flash)

        // Official offline note-skin mapping (note_00498bd0.c switch on skin index 0x68 = NoteType+1).
        //   EFFECT folder (game_eft_{N}.dge → EFT_<suffix>): the per-skin HIT BURST. For the {2,5,11,12,13,14} family the
        //   combo/judge come from a SHARED PUBLICEFT (so they don't change per skin — faithful); {8,9,10,PET} are self-contained.
        // NOTE: index 6 is the custom "EFT_3" skin (room label hiteft3) = EFT_7's JZ0x burst + NOTEIMAGE_5 board + EFT_5 combo.
        internal static readonly string[] NoteTypeEftSuffix = { "2", "5", "8", "9", "10", "11", "7", "12", "13", "14", "PET" };
        //   BOARD folder (note_image{N}.dgn → NOTEIMAGE_<suffix>): coarser — several note-types share one board skin.
        internal static readonly string[] NoteTypeBoardSuffix = { "6", "6", "8", "9", "10", "6", "5", "5", "5", "11", "PET" };
        //   COMBO/JUDGE source (game_eft_{N}.dge line refs): {2,5}→PUBLICEFT, {12,13,14}→PUBLICEFT2, {8,9,10,pet}→own
        //   folder, 11→EFT_5, custom EFT_3 (idx6)→EFT_5. Shared folders use Dsp_* names; self-contained use flat COMBO/00../PERFECT.
        internal static readonly string[] NoteTypeComboSource = { "PUBLICEFT", "PUBLICEFT", "EFT_8", "EFT_9", "EFT_10", "EFT_5", "EFT_5", "PUBLICEFT2", "PUBLICEFT2", "PUBLICEFT2", "EFT_PET" };
        internal int _eftNoteType = -1;                    // -1 = stock init; 0..9 = F4 test override

        /// <summary>Switch the WHOLE note skin for the F4 test: board (NOTEIMAGE falling notes + receptors) + hit burst +
        /// combo/judge, faithful to the offline note_image/game_eft split. Board + burst always change; combo/judge change
        /// only for skins whose EFT folder ships them (2/5/8/9/10/PET) — the 11/12/13/14 family shares PUBLICEFT, stays put.</summary>
        internal void SetNoteType(int noteType)
        {
            _hit3dMode = false; _note3dMode = false;                        // picking a 2D skin leaves the 3D hit + coloured-note mode
            for (int l = 0; l < Keys; l++) if (_hit3dLive[l] != null) { Destroy(_hit3dLive[l].gameObject); _hit3dLive[l] = null; }   // don't leak a looping hold burst
            int t = (noteType >= 0 && noteType < NoteTypeEftSuffix.Length) ? noteType : 0;
            SetNoteBoardSkin(NoteTypeBoardSuffix[t]);                        // falling notes + receptors + hold (live reload)
            LoadHitEffects(t);                                              // per-skin hit burst (sets _eftNoteType)
            LoadComboJudgeArt(t);                                          // combo digits + word + judgement words (shared or own folder)
        }

        /// <summary>F4 note-skin selector index → skin. Indices 0..N-1 are the 2D sprite skins (NoteTypeEftSuffix);
        /// index N is the "3D" skin (hiteft3D), whose hit effect is the real AU_HIT.EFT 3DEFT via <see cref="SpawnHit3d"/>.</summary>
        internal void SelectSkin(int idx)
        {
            if (idx >= NoteTypeEftSuffix.Length) EnableHit3dSkin();
            else SetNoteType(idx);
        }

        // Apply the room's win2 "note" selection (roomNoteType = GameSession.NoteType) at boot so gameplay uses the SAME
        // skin the player picked in the room. The room's NoteEftArt order (hiteft2..pet, then hiteft3D) matches the
        // SelectSkin index space 1:1 (0..9 = 2D skins, 10 = 3D). -2 = unset (F4/standalone → keep stock); -1 = 隨機.
        private void ApplyRoomNoteSkin()
        {
            if (roomNoteType == -2) return;
            int total = NoteTypeEftSuffix.Length + 1;                 // 10 2D skins + the 3D skin = 11 (== room NoteEftArt count)
            int idx = roomNoteType >= 0 ? Mathf.Clamp(roomNoteType, 0, total - 1)
                                        : UnityEngine.Random.Range(0, total);   // 隨機
            SelectSkin(idx);
        }

        /// <summary>Select the "3D" note skin (hiteft3D): (1) colour the falling notes by BEAT quantization — magenta
        /// (on-beat) / blue (off-8th) / green (16ths) up-arrows from 3DNOTES\, rotated per lane; (2) fire the HIT.EFT
        /// note-arrow burst at the receptor. Faithful to the official mode, which is a flat board + tilted camera (no
        /// curved geometry), so the remake reproduces it by colouring the 2D note sprites rather than porting 3D meshes.</summary>
        internal void EnableHit3dSkin()
        {
            _hit3dMode = true; _note3dMode = true;
            LoadNote3dFamilies(); LoadBoard3dSkin();
            LoadComboJudgeArt(0);   // the official 3D mode uses the SHARED standard combo/judge (PUBLICEFT) — set it
                                    // EXPLICITLY (index 0), not inherited from whatever 2D skin was selected before.
        }

        // hiteft3D hit burst: play the selected 3D hit EFT (default HIT.EFT = the official note-ARROW flash) at the struck
        // lane's receptor. Rendered in the SAME orthographic play field / layer as the sprite bursts (design-space world
        // coords via SdoLayout), with SortingOrder over the board/notes and a GOLD Tint (the note-arrow texture is white;
        // the official 3D hit is yellow via a play-time tint). follow=null → pinned at the receptor for its life.
        // Spawn a 3D hit EFT at the lane's receptor, oriented per-lane like the arrow. loop=true (HIT_LONG) makes it a
        // self-sustaining continuous burst (negative-life emitters re-init forever) that the host tears down on release.
        private EftEffect SpawnHit3dEft(int lane, string name, bool loop)
        {
            var file = LoadNamedEft(name);
            if (file == null) return null;
            var go = new GameObject("Hit3d" + lane);
            go.transform.position = SdoLayout.ToWorld(LaneLeftX[lane] + LaneCx0, judgeLineY, hit3dZ);
            // OFFICIAL: the hit burst is oriented per-lane like the arrow (FUN_004bcba0 rotZ = {90,180,0,-90}° for lanes
            // 0=left 1=down 2=up 3=right) — NOT always-up. The burst's note-arrow world-quads inherit this host rotation.
            go.transform.rotation = Quaternion.Euler(0f, 0f, Note3dRot[lane] + (note3dFlip180 ? 180f : 0f));
            var eff = go.AddComponent<EftEffect>();
            eff.SortingOrder = 6;                                            // over the board (-30) / notes (5), like the 2D burst
            eff.MotionScale = hit3dMotion;                                   // damp any rise so it stays near the receptor
            eff.Tint = hit3dTint;                                            // multiplies the white file diffuse → the on-screen colour
            // FaithfulAlpha + brightness 1×: the official engine adds tex×diffuse×(ch1/255) LINEARLY — the soft look IS
            // the EFT's authored envelope. The old Legacy-Additive path (2× shader × 1.5 bright, ch1→SrcAlpha) clipped it.
            eff.FaithfulAlpha = true;
            eff.Loop = loop;                                                // HIT_LONG: continuous self-sustain for the whole hold
            eff.Init(file, hit3dScale * note3dMaster, null, ResolveEftTex, _addMat, 0, hit3dBright, 0f, 0.6f, ResolveEftMesh);
            return eff;
        }

        // TAP / hold-head flash — the F4-selected hit EFT (default HIT). OFFICIAL: one effect slot per lane, RESET on
        // every hit (FUN_004c33d0 → FUN_004bcae0) so a new hit restarts the burst instead of stacking brightness.
        private void SpawnHit3d(int lane)
        {
            if (lane < 0 || lane >= Keys) return;
            if (_hit3dLive[lane] != null) { Destroy(_hit3dLive[lane].gameObject); _hit3dLive[lane] = null; }
            _hit3dLive[lane] = SpawnHit3dEft(lane, Hit3dEftNames[Mathf.Clamp(hit3dEftIdx, 0, Hit3dEftNames.Length - 1)], false);
        }

        // HOLD HEAD accepted → replace the lane slot with the CONTINUOUS looping HIT_LONG (official 0x54). It self-
        // sustains via its negative-life emitters for the whole hold; StopHit3dLong tears it down on release.
        private void SpawnHit3dLong(int lane)
        {
            if (!_hit3dMode || lane < 0 || lane >= Keys) return;
            if (_hit3dLive[lane] != null) { Destroy(_hit3dLive[lane].gameObject); _hit3dLive[lane] = null; }
            _hit3dLive[lane] = SpawnHit3dEft(lane, "HIT_LONG", true);
        }

        // HOLD RELEASE / complete → tear down the looping HIT_LONG and play the one-shot HIT_SUO terminator (official
        // StopHoldEffect → ClearSlot → 0x55). Not stored: it auto-destroys when spent.
        private void StopHit3dLong(int lane)
        {
            if (!_hit3dMode || lane < 0 || lane >= Keys) return;
            if (_hit3dLive[lane] != null) { Destroy(_hit3dLive[lane].gameObject); _hit3dLive[lane] = null; }
            SpawnHit3dEft(lane, "HIT_SUO", false);
        }

        // Load (and cache) any 3DEFT/&lt;name&gt;.EFT by name. Shares the SpawnNamedEft cache so a file is parsed once.
        private static EftFile LoadNamedEft(string name)
        {
            if (_namedEftCache.TryGetValue(name, out var file)) return file;
            var path = Path.Combine(SdoExtracted.Root, "3DEFT", name + ".EFT");
            if (!File.Exists(path)) { Debug.LogWarning("[hit3d] missing " + path); return null; }
            file = EftFile.Load(File.ReadAllBytes(path));
            _namedEftCache[name] = file;
            return file;
        }

        /// <summary>(Re)load the per-skin hit-burst frames. Family skins (2/5/11/12/13/14) store one non-directional set
        /// EFT_HIT0..11 (.PNG in 11-14, .BMP in 2/5) used on every lane. Self-contained skins (8/9/10/PET) ship DIRECTIONAL
        /// frames — jz*_rl for the left/right lanes (0/3) and jz*_ud for the up/down lanes (1/2) — so a paw stays one colour
        /// per lane instead of cycling rl→ud. Keeps the current burst if the folder yields nothing.</summary>
        internal void LoadHitEffects(int noteType)
        {
            int t = (noteType >= 0 && noteType < NoteTypeEftSuffix.Length) ? noteType : 0;
            _eftNoteType = t;
            string dir = SdoExtracted.EftDir2(NoteTypeEftSuffix[t]);
            var bf = new List<Sprite>();                                  // family: EFT_HIT0..11 (non-directional)
            for (int i = 0; i < 12; i++)
            {
                var s = SdoExtracted.LoadImage(dir, "EFT_HIT" + i + ".PNG") ?? SdoExtracted.LoadImage(dir, "EFT_HIT" + i + ".BMP");
                if (s != null) bf.Add(s);
            }
            if (bf.Count > 0) { _burstFrames = bf.ToArray(); _burstFramesUD = null; return; }
            var rl = LoadJzFrames(dir, "rl");                             // self-contained: directional jz*_rl / jz*_ud
            if (rl != null) { _burstFrames = rl; _burstFramesUD = LoadJzFrames(dir, "ud"); return; }   // _ud may be null → rl used for all lanes
            // non-directional UPPERCASE JZ frames (EFT_7 = the custom "EFT_3" skin): JZ00.PNG..JZ0N.PNG, same on every lane
            var jz = new List<Sprite>();
            for (int i = 0; i < 32; i++) { var s = SdoExtracted.LoadImage(dir, "JZ" + i.ToString("00") + ".PNG"); if (s == null) break; jz.Add(s); }
            if (jz.Count > 0) { _burstFrames = jz.ToArray(); _burstFramesUD = null; }
        }

        // Load a directional hit-frame set jz00_<dir>.png, jz01_<dir>.png … until the first gap. null if none.
        private static Sprite[] LoadJzFrames(string dir, string dirSuffix)
        {
            var list = new List<Sprite>();
            for (int i = 0; i < 32; i++)
            {
                var s = SdoExtracted.LoadImage(dir, "jz" + i.ToString("00") + "_" + dirSuffix + ".png");
                if (s == null) break;
                list.Add(s);
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        /// <summary>Reload combo digits + combo word + judgement words for a NoteType from the source its .dge points at:
        /// PUBLICEFT/PUBLICEFT2 (shared, Dsp_* names) for the family skins, or the self-contained EFT_8/9/10/PET (flat
        /// names). Missing files are skipped (current art kept). _comboWord caches its sprite once, so it is reassigned
        /// explicitly; _judgeSprites/_comboDigitSprites are read fresh every frame.</summary>
        private void LoadComboJudgeArt(int noteType)
        {
            int t = (noteType >= 0 && noteType < NoteTypeComboSource.Length) ? noteType : 0;
            string src = NoteTypeComboSource[t];
            string dir = Path.Combine(SdoExtracted.Root, "EFFECT", src);
            bool shared = src.StartsWith("PUBLICEFT");   // shared families use Dsp_* names; self-contained use flat names
            // bleed: true → kill the bilinear white halo on the glyph edges (the source PNGs store a transparent-white matte)
            AssignSprite(ref _judgeSprites[0], SdoExtracted.LoadImage(dir, shared ? "Dsp_1_Perfect.png" : "PERFECT.PNG", bleed: true));
            AssignSprite(ref _judgeSprites[1], SdoExtracted.LoadImage(dir, shared ? "Dsp_2_Cool.png" : "COOL.PNG", bleed: true));
            AssignSprite(ref _judgeSprites[2], SdoExtracted.LoadImage(dir, shared ? "Dsp_3_Bad.png" : "BAD.PNG", bleed: true));
            AssignSprite(ref _judgeSprites[3], SdoExtracted.LoadImage(dir, shared ? "Dsp_4_Miss.png" : "MISS.PNG", bleed: true));
            for (int i = 0; i < 10; i++)
                AssignSprite(ref _comboDigitSprites[i], SdoExtracted.LoadImage(dir, shared ? ("Dsp_Num" + i + ".png") : ("0" + i + ".PNG"), bleed: true));
            var cw = SdoExtracted.LoadImage(dir, shared ? "Dsp_0_Combo.png" : "COMBO.PNG", bleed: true);
            if (cw != null && _comboWord != null) _comboWord.sprite = cw;
            // READY/GO opening animation source: PUBLICEFT skins (2/5) use EFT_5's opening (per request — PUBLICEFT stores
            // its own only under Dsp_A_* names, not the flat READY00 this loads); self-contained skins use their combo
            // folder; else fall back to the skin's own hit-burst folder (EFT_<suffix>); else keep the LoadArt default.
            string readyDir = src == "PUBLICEFT" ? SdoExtracted.EftDir2("5") : dir;
            if (!TryLoadReadyGo(readyDir)) TryLoadReadyGo(SdoExtracted.EftDir2(NoteTypeEftSuffix[t]));
        }

        // Load READY00..09 / GO01..06 from a folder into _readyFrames/_goFrames. Returns false (leaving them untouched)
        // if the folder has no READY frames, so callers can fall through to another source.
        private bool TryLoadReadyGo(string dir)
        {
            var rf = new List<Sprite>(); for (int i = 0; i < 10; i++) { var s = SdoExtracted.LoadImage(dir, "READY0" + i + ".PNG", bleed: true); if (s != null) rf.Add(s); }
            if (rf.Count == 0) return false;
            _readyFrames = rf.ToArray();
            var gf = new List<Sprite>(); for (int i = 1; i <= 6; i++) { var s = SdoExtracted.LoadImage(dir, "GO0" + i + ".PNG", bleed: true); if (s != null) gf.Add(s); }
            if (gf.Count > 0) _goFrames = gf.ToArray();
            return true;
        }

        private static void AssignSprite(ref Sprite dst, Sprite s) { if (s != null) dst = s; }

        private void UpdateFx()
        {
            for (int i = _fx.Count - 1; i >= 0; i--)
            {
                var fx = _fx[i];
                var frames = fx.Frames;   // each burst animates ITS OWN (directional) frame set, captured at spawn
                if (frames == null || frames.Length == 0) { if (fx.IsHold) _holdBurst[fx.Lane] = null; DestroyBurst(fx); _fx.RemoveAt(i); continue; }
                int step = (int)((Time.time - fx.Start) / BurstSecPerFrame);
                if (step >= frames.Length)
                {
                    // HOLD: finished a round, still held -> loop (wait for the full animation before the next round).
                    // TAP (or released hold): one-shot, ends here.
                    if (fx.IsHold && _holding[fx.Lane] != null) { fx.Start = Time.time; step = 0; }
                    else { if (fx.IsHold) _holdBurst[fx.Lane] = null; DestroyBurst(fx); _fx.RemoveAt(i); continue; }
                }
                var spr = frames[step];
                fx.Sr.sprite = spr; if (fx.Sr2) fx.Sr2.sprite = spr;
            }
        }

        private void DestroyBurst(BurstFx fx) { if (fx.Sr) Destroy(fx.Sr.gameObject); if (fx.Mat) _matPool.Push(fx.Mat); }

        // Tear down every in-flight gameplay burst / hold / click-flash. Needed when the result is entered MID-song
        // (F5, or HP-out while holding): a hold burst loops forever while its lane stays "held", so without this it
        // freezes on screen behind the result panel.
        private void ClearGameplayFx()
        {
            for (int i = _fx.Count - 1; i >= 0; i--) DestroyBurst(_fx[i]);
            _fx.Clear();
            for (int lane = 0; lane < Keys; lane++)
            {
                _holdBurst[lane] = null; _holding[lane] = null;
                if (_clickFlashSr[lane]) _clickFlashSr[lane].enabled = false; _clickFlashStart[lane] = -1f;
            }
            _missFlashStart = -1f;
            if (_missOverlay) _missOverlay.enabled = false;
        }

        private sealed class BurstFx { public SpriteRenderer Sr, Sr2; public Material Mat; public int Lane; public float Start; public bool IsHold; public Sprite[] Frames; }

        // ---------- lane click flash (decompiled NoteBoard_DrawClickFlash_00498bd0) ----------

        private float _missFlashStart = -1f;            // miss red flash one-shot (drives the track-wide _missOverlay)
        public float missFlashAlpha = 1.0f;             // miss red glow strength ×clickFlashBright (1 = exactly the white click flash's
                                                        // brightness — it now uses the same glow sprite, tiled across the 4 lanes; lower to taste)

        // (re)start the lane's click strip at frame 0 (full alpha). Called ONCE on a key-press / head-hit.
        private void TriggerClickFlash(int lane)
        {
            if (lane < 0 || lane >= Keys || _clickFlashSr[lane] == null) return;
            _clickFlashStart[lane] = Time.time;
        }

        // fire the all-lane RED flash (called on a Miss); re-arms on each miss.
        private void TriggerMissFlash() { _missFlashStart = Time.time; }

        // ONE-SHOT per press: the 3-frame click-flash cycle (255→130→0) plays once then hides. Holding does NOT re-pulse —
        // the official plays it once per key-DOWN (time-gated), not continuously while struck. A MISS runs the SAME 3-frame
        // cycle but on the track-wide _missOverlay, tinted RED — covers all four lanes (the per-lane strips read too faint
        // over the board's dark edge lanes, so only the middle two showed).
        private void UpdateClickFlash()
        {
            int missStep = -1;                          // ≥0 = miss frame this tick → red wash over all lanes
            if (_missFlashStart >= 0f)
            {
                int s = (int)((Time.time - _missFlashStart) / Mathf.Max(1e-4f, clickFlashStepSec));
                if (s >= ClickFlashAlpha.Length - 1) _missFlashStart = -1f;   // last frame is 0-alpha → done
                else missStep = s;
            }
            if (_missOverlay)
            {
                bool on = missStep >= 0;
                if (_missOverlay.enabled != on) _missOverlay.enabled = on;
                if (on) _missOverlay.color = new Color(1f, 0f, 0f, ClickFlashAlpha[missStep] * clickFlashBright * missFlashAlpha);   // tracks the white flash's brightness
            }
            for (int lane = 0; lane < Keys; lane++)
            {
                var sr = _clickFlashSr[lane]; if (sr == null) continue;
                if (_clickFlashStart[lane] < 0f) { if (sr.enabled) sr.enabled = false; continue; }
                int step = (int)((Time.time - _clickFlashStart[lane]) / Mathf.Max(1e-4f, clickFlashStepSec));
                if (step >= ClickFlashAlpha.Length - 1) { _clickFlashStart[lane] = -1f; sr.enabled = false; continue; }  // one-shot ends on the 0-alpha frame
                float a = ClickFlashAlpha[step] * clickFlashBright;   // 0.8 = ~80% brightness
                sr.enabled = a > 0f;
                if (sr.enabled) sr.color = new Color(1f, 1f, 1f, a);
            }
        }

        // ---------- HUD update ----------

        private void UpdateHud()
        {
            // keydown receptor burst: play *_judgeline2..6 once over recKeydownStepSec/frame, then snap back to idle (1)
            for (int c = 0; c < Keys; c++)
            {
                if (_receptors[c] == null) continue;
                Sprite spr = _recIdle[c];
                if (_recDownStart[c] >= 0f && _recDownFrames[c] != null)
                {
                    int f = (int)((Time.time - _recDownStart[c]) / Mathf.Max(1e-4f, recKeydownStepSec));
                    if (f >= _recDownFrames[c].Length) _recDownStart[c] = -1f;   // burst done → idle frame 1
                    else spr = _recDownFrames[c][f];
                }
                _receptors[c].sprite = spr;
                // 3D skin: the JUDGELINE receptor is one up-arrow rotated per lane (like the notes); reset to identity in 2D.
                _receptors[c].transform.localRotation = _note3dMode
                    ? Quaternion.Euler(0f, 0f, Note3dRot[c] + (note3dFlip180 ? 180f : 0f))
                    : Quaternion.identity;
                // 3D skin press-pulse: scale the receptor up on keydown (reproduces the official JUDGELINE_2.MOT pop).
                if (_recBaseScale[c].sqrMagnitude > 1e-6f)
                {
                    float pulse = 1f;
                    if (_note3dMode && _recDownStart[c] >= 0f)
                    {
                        float a = (Time.time - _recDownStart[c]) / Mathf.Max(1e-4f, receptorPressSec);
                        if (a < 1f) pulse = 1f + receptorPressAmt * Mathf.Sin(Mathf.PI * a);
                    }
                    _receptors[c].transform.localScale = _recBaseScale[c] * pulse;
                }
            }

            float age = Time.time - _judgeWordAt;
            if (_judgeWord.sprite != null && age < 0.5f)
            {
                _judgeWord.color = new Color(1, 1, 1, Mathf.Clamp01(1f - age / 0.5f));
                float pop = (1f + Mathf.Clamp01(1f - age * 6f) * 1.0f) * 0.8f; // 2.0->1.0 ×0.8 (decompiled)
                PlaceAspect(_judgeWord, JudgeWordCenter.x, JudgeWordCenter.y, _judgeWord.sprite.bounds.size.x, -2);
                _judgeWord.transform.localScale *= pop;
            }
            else _judgeWord.color = new Color(1, 1, 1, 0);

            UpdateComboDigits(); UpdateScoreDigits(); UpdateHpBar();
            if (showtimeMode) UpdateEnergyBar();   // (UpdateBanner ticks in Update so it survives post-song)

            if (_timeText)
            {
                // official format: "[left]  [total]". Left = "--:--" until the music starts (during the READY/GO
                // opening _clockStart is still in the future), then a countdown of the remaining time. Right = total.
                int tot = (int)Math.Round(_totalMs / 1000.0);
                double el = Time.timeAsDouble - _clockStart;
                string left = el < 0 ? "- : -" : $"{(int)Math.Max(0, tot - el) / 60} : {(int)Math.Max(0, tot - el) % 60:00}";
                _timeText.text = FullWidth($"{left}    {tot / 60} : {tot % 60:00}");
            }
            // combo milestone (50/100/150…) -> celebration burst + voice
            int milestone = (_score.Combo / 50) * 50;
            if (milestone >= 50 && milestone != _lastMilestone) { _lastMilestone = milestone; SpawnMilestone(milestone); }

            string camLbl = (use3dCamera && _camReady) ? (_camMode < 0 ? "  F2:AUTO" : $"  F2:CAM{_camMode}") : "";
            if (_info) _info.text = (_failed ? "FAILED " : "") + $"P{_score.PerfectCount} C{_score.CoolCount} B{_score.BadCount} M{_score.MissCount}  x{_score.Combo}{camLbl}";
        }

        private void SpawnMilestone(int combo)
        {
            string v = null;   // combo voices (decompiled): 100→0008 200→0007 300→0006 400→0005 500→0004
            switch (combo) { case 100: v = "VOICE_0008"; break; case 200: v = "VOICE_0007"; break; case 300: v = "VOICE_0006"; break; case 400: v = "VOICE_0005"; break; case 500: v = "VOICE_0004"; break; }
            if (v != null) PlaySe(v);
            // floor combo burst at every 100 (orig 100combo.eft..500combo.eft = 3DEft idx 0x13..0x17,
            // spawned at the dancer's pelvis-on-floor by Dancer_SpawnDirEffect_004a7680, uniform scale 30/20/30/30/35;
            // tiers clamp to 500. NB: the original fires these from authored chart events, not combo%100 — we
            // approximate with the milestone so the effect still plays at 100/200/...).
            if (combo % 100 == 0) SpawnComboBurst(Mathf.Clamp(combo / 100 - 1, 0, 4));
        }

        public float comboBurstSize = 1f, comboBurstBright = 1f;   // F4-tunable
        public float comboGlow = 0f, comboGlowSpread = 0.6f;        // outer-glow halo: intensity (0=off, faithful) + spread
        // decompiled per-tier uniform effect scale (DAT_0054f228, Dancer_SpawnDirEffect_004a7680): 100..500 = 30/20/30/30/35
        private static readonly float[] ComboTierScale = { 30f, 20f, 30f, 30f, 35f };
        private static readonly Dictionary<int, EftFile> _eftCache = new Dictionary<int, EftFile>();
        // Faithful: load the actual <tier>COMBO.EFT and run it through the EftEffect interpreter (real emitters,
        // EMIT counts, start-delays, per-channel curves, ring geometry, additive) — no hand-tuned sprites.
        private void SpawnComboBurst(int tier)
        {
            tier = Mathf.Clamp(tier, 0, 4);
            int effId = 0x13 + tier;
            if (!_eftCache.TryGetValue(effId, out var file))
            {
                var path = Path.Combine(SdoExtracted.Root, "3DEFT", ((tier + 1) * 100) + "COMBO.EFT");
                if (!File.Exists(path)) { Debug.LogWarning("[combo] missing " + path); return; }
                file = EftFile.Load(File.ReadAllBytes(path));
                _eftCache[effId] = file;
            }
            var go = new GameObject("ComboBurst" + ((tier + 1) * 100));
            // pelvis projected to floor (Dancer_SpawnDirEffect: Bip01_Pelvis x/z, y≈0.1); the yuanpan ground ring
            // already tracks the pelvis-on-floor, so reuse its transform as the follow anchor.
            go.transform.position = _ringTr != null ? _ringTr.position : new Vector3(_avatarChest.x, 0.6f, _avatarChest.z);
            // Render on the STAGE layer/camera so the burst shares the scene depth → it rises FROM THE GROUND and is
            // occluded by the dancer's body (a 2D composite drew it flat in front of everything). The white-hot core
            // is enlarged in this depth-correct linear-additive path via EftEffect.BallCoreIntensity (F4 slider).
            int layer = use3dCamera ? SceneLayer : 0;
            float effScale = ComboTierScale[tier] * comboBurstSize;   // .eft uniform spawn scale (× F4 size tuning)
            go.AddComponent<EftEffect>().Init(file, effScale, _ringTr, ResolveEftTex, _addMat, layer, comboBurstBright, comboGlow, comboGlowSpread, ResolveEftMesh);
            if (use3dCamera) SetLayerRecursive(go, SceneLayer);
        }

        private static readonly Dictionary<string, EftFile> _namedEftCache = new Dictionary<string, EftFile>();
        public void SpawnNamedEftForTest(string name, float baseScale) => SpawnNamedEft(name, baseScale);
        // Spawn any <name>.EFT (e.g. FINISHED = the end-of-song result burst: a tex103 root that bursts 18+18+30
        // billboards with posSpread 90-122° = a near-spherical firework. effScale≈5: base 0.25 × anim 5.26 × 5 = the
        // captured worldScale 6.57). Same interpreter/anchor as combos; baseScale × the F4 size slider.
        // Persistent per-scene background EFTs (decompiled from the StageScene controllers): the SCN0008 ground magic
        // circle (結界), christmas/snow scenes' snow, the sea aurora+bubbles, carnival glows, etc. Spawned once on
        // scene load and looped for the whole song. Native SDO coords on the stage layer (same as the mapobjs).
        // SCN0003 sweeping spotlights (light_left/light_right): the original oscillates all effect GOs
        // ±10° on Z at 0.5°/tick every 50ms (FUN_004b2310, StageScene_UpdateOscPlanes). This coroutine
        // replicates that sweep. The three bands are spawned at t=0/2000/4000ms so their 15s animation
        // cycles (Life0=300 ticks) are staggered — different brightness/color per band at any moment.
        List<GameObject> _oscLightGos;
        bool _oscStarted;
        System.Collections.IEnumerator OscLightZCo()
        {
            float angle = 0f, vel = 0.5f;
            var wait = new WaitForSeconds(0.05f);
            while (true)
            {
                if (angle > 10f)  { angle = 10f;  vel = -0.5f; }
                if (angle < -10f) { angle = -10f; vel =  0.5f; }
                var q = Quaternion.Euler(0f, 0f, angle);
                foreach (var go in _oscLightGos) if (go != null) go.transform.rotation = q;
                angle += vel;
                yield return wait;
            }
        }

        void RegisterSceneEft(GameObject go, string eft)
        {
            if (go == null) return;
            if (eft == "light_left" || eft == "light_right")
            {
                if (_oscLightGos == null) _oscLightGos = new List<GameObject>();
                _oscLightGos.Add(go);
                if (!_oscStarted) { _oscStarted = true; StartCoroutine(OscLightZCo()); }
            }
        }

        System.Collections.IEnumerator SpawnDelayedEftCo(IReadOnlyList<SceneEftPlacement> entries)
        {
            float elapsedSec = 0f;
            foreach (var e in entries)
            {
                if (e.SpawnDelay <= 0) continue;
                float wantSec = e.SpawnDelay * 0.001f;
                if (wantSec > elapsedSec)
                {
                    yield return new WaitForSeconds(wantSec - elapsedSec);
                    elapsedSec = wantSec;
                }
                RegisterSceneEft(SpawnSceneEft(e.Eft, new Vector3(e.X, e.Y, e.Z), new Vector3(e.Ex, e.Ey, e.Ez), e.Scale), e.Eft);
            }
        }

        private void SpawnSceneEffects()
        {
            var placements = SceneEftCatalog.ForFolder(SceneFolder());
            bool hasDelayed = false;
            foreach (var e in placements)
            {
                if (e.SpawnDelay > 0) { hasDelayed = true; continue; }
                RegisterSceneEft(SpawnSceneEft(e.Eft, new Vector3(e.X, e.Y, e.Z), new Vector3(e.Ex, e.Ey, e.Ez), e.Scale), e.Eft);
            }
            if (hasDelayed) StartCoroutine(SpawnDelayedEftCo(placements));
        }

        private void AttachSceneEftsToMapobj(string baseName, SdoAvatar avatar, Transform owner)
        {
            if (avatar == null || owner == null) return;
            var entries = SceneAttachedEftCatalog.ForMapobj(SceneFolder(), baseName);
            if (entries.Count == 0) return;

            avatar.PoseFrame(0f);
            foreach (var e in entries)
            {
                int bone = avatar.BoneIndex(e.Bone);
                if (bone < 0)
                {
                    Debug.LogWarning($"[eft] attached scene eft {e.Eft}: bone {e.Bone} missing on {baseName}");
                    continue;
                }

                var anchorGo = new GameObject($"{baseName}_{e.Bone}_{e.Eft}_anchor");
                anchorGo.transform.SetParent(owner, false);
                Transform follow = anchorGo.transform;
                if (e.Offset != Vector3.zero)
                {
                    var offsetGo = new GameObject("offset");
                    offsetGo.transform.SetParent(anchorGo.transform, false);
                    offsetGo.transform.localPosition = e.Offset;
                    follow = offsetGo.transform;
                }

                avatar.AddAnchor(bone, anchorGo.transform);
                anchorGo.transform.position = owner.TransformPoint(avatar.BoneModelPos(e.Bone));
                var go = SpawnSceneEft(e.Eft, follow.position, e.EulerDeg, e.Scale, follow);
                RegisterSceneEft(go, e.Eft);
                if (use3dCamera) SetLayerRecursive(anchorGo, SceneLayer);
                Debug.Log($"[eft] attached scene eft {e.Eft} -> {baseName}/{e.Bone} offset {e.Offset} scale {e.Scale}");
            }
        }

        private GameObject SpawnSceneEft(string name, Vector3 pos, Vector3 euler, float scale, Transform follow = null)
        {
            if (!_namedEftCache.TryGetValue(name, out var file))
            {
                var path = Path.Combine(SdoExtracted.Root, "3DEFT", name + ".EFT");
                if (!File.Exists(path)) { Debug.LogWarning("[eft] scene eft missing " + path); return null; }
                file = EftFile.Load(File.ReadAllBytes(path));
                _namedEftCache[name] = file;
            }
            var go = new GameObject("SceneEft_" + name);
            go.transform.position = pos;
            go.transform.rotation = Quaternion.Euler(euler);
            int layer = use3dCamera ? SceneLayer : 0;
            var eff = go.AddComponent<EftEffect>();
            eff.Persistent = true;   // never auto-destroy; loops for the whole song
            eff.EffectName = name;
            eff.Init(file, scale, follow, ResolveEftTex, _addMat, layer, comboBurstBright, comboGlow, comboGlowSpread, ResolveEftMesh);
            if (use3dCamera) SetLayerRecursive(go, SceneLayer);
            Debug.Log($"[eft] scene eft {name} @ {pos} euler {euler} scale {scale}");
            return go;
        }

        private void SpawnNamedEft(string name, float baseScale)
        {
            if (!_namedEftCache.TryGetValue(name, out var file))
            {
                var path = Path.Combine(SdoExtracted.Root, "3DEFT", name + ".EFT");
                if (!File.Exists(path)) { Debug.LogWarning("[eft] missing " + path); return; }
                file = EftFile.Load(File.ReadAllBytes(path));
                _namedEftCache[name] = file;
            }
            var go = new GameObject("Eft_" + name);
            go.transform.position = _ringTr != null ? _ringTr.position : new Vector3(_avatarChest.x, 0.6f, _avatarChest.z);
            int layer = use3dCamera ? SceneLayer : 0;
            float effScale = baseScale * comboBurstSize;
            go.AddComponent<EftEffect>().Init(file, effScale, _ringTr, ResolveEftTex, _addMat, layer, comboBurstBright, comboGlow, comboGlowSpread, ResolveEftMesh);
            if (use3dCamera) SetLayerRecursive(go, SceneLayer);
        }

        // EFT generic texture list (Extracted/3DEFT/GENERIC/LIST.TXT): pipe index → relative path → Texture2D.
        // sRGB import (linear=false): GPU 硬體 decode sRGB→linear on sample，在 linear-space Unity 裡與
        // D3D9 raw-byte pipeline 的 monitor-gamma 顯示結果一致（round-trip: 0.1→0.01 linear→0.1 顯示）。
        private static string[] _eftTexList;
        private static readonly Dictionary<int, Texture2D> _eftTexCache = new Dictionary<int, Texture2D>();
        private static Texture2D ResolveEftTex(int idx)
        {
            if (_eftTexCache.TryGetValue(idx, out var cached)) return cached;
            if (_eftTexList == null)
            {
                var list = new List<string>();
                var lp = Path.Combine(SdoExtracted.Root, "3DEFT", "GENERIC", "LIST.TXT");
                if (File.Exists(lp))
                    foreach (var raw in File.ReadAllLines(lp))
                    {
                        int bar = raw.IndexOf('|'); if (bar < 0) continue;
                        if (!int.TryParse(raw.Substring(0, bar).Trim(), out int li)) continue;
                        var toks = raw.Substring(bar + 1).Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        while (list.Count <= li) list.Add(null);
                        list[li] = (toks.Length > 0 && toks[0] != "NULL") ? toks[0] : null;
                    }
                _eftTexList = list.ToArray();
            }
            Texture2D tex = null;
            if (idx >= 0 && idx < _eftTexList.Length && _eftTexList[idx] != null)
            {
                var rel = _eftTexList[idx].Replace("generic\\", "").Replace("generic/", "").Replace('\\', '/');
                var dir = Path.Combine(SdoExtracted.Root, "3DEFT", "GENERIC", Path.GetDirectoryName(rel) ?? "");
                var name = Path.GetFileName(rel).ToUpperInvariant();
                tex = SdoExtracted.LoadTextureRaw(dir, name + ".png") ?? SdoExtracted.LoadTextureRaw(dir, name + ".BMP");
            }
            _eftTexCache[idx] = tex;
            return tex;
        }

        // EFT 3D-mesh list (Extracted/3DEFT/XMESH/LIST.TXT): the SECOND texture pipeline the texture-only port missed.
        // The engine (Effect_LoadTextureLists_004be540 first loop) increments an index per parsed line and loads
        // <path>.msh/.hrc via Avatar_LoadHrc into AvatarScene; a particle's word[6] indexes it. Here: sequential
        // line index → .MSH parsed by LoadEffectMesh (FVF-0x112 effect-mesh path) → Unity mesh + its DDS texture.
        // idx 32 = adol_x\aef03_00 = the 200/300COMBO slot0 blue mesh (textured AEF_3_00.DDS); 100/101 = column_00/01.
        private static string[] _eftMeshList;
        private static readonly Dictionary<int, EftMeshData> _eftMeshCache = new Dictionary<int, EftMeshData>();
        private static EftMeshData ResolveEftMesh(int idx)
        {
            if (_eftMeshCache.TryGetValue(idx, out var cached)) return cached;
            if (_eftMeshList == null)
            {
                var list = new List<string>();
                var lp = Path.Combine(SdoExtracted.Root, "3DEFT", "XMESH", "LIST.TXT");
                if (File.Exists(lp))
                    foreach (var raw in File.ReadAllLines(lp))
                    {
                        if (string.IsNullOrWhiteSpace(raw)) continue;   // engine: sscanf==0 on a blank line ⇒ no index bump
                        int bar = raw.IndexOf('|');
                        var body = (bar >= 0 ? raw.Substring(bar + 1) : raw).Trim();
                        var toks = body.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        string path = toks.Length > 0 ? toks[0] : null;   // path token (2nd sscanf field)
                        if (path != null && path.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant() == "xmesh") path = null;
                        list.Add(path);   // sequential index = position over non-blank lines (the engine's counter)
                    }
                _eftMeshList = list.ToArray();
            }
            EftMeshData md = null;
            if (idx >= 0 && idx < _eftMeshList.Length && _eftMeshList[idx] != null)
            {
                var rel = _eftMeshList[idx].Replace("xmesh\\", "").Replace("xmesh/", "").Replace('/', '\\');
                var xdir = Path.Combine(SdoExtracted.Root, "3DEFT", "XMESH");
                // the extraction keeps meshes both under their subfolder (adol_x\AEF03_00.MSH) AND flattened in XMESH\;
                // try the listed subpath first, then the bare basename.
                var mshPath = Path.Combine(xdir, rel + ".MSH");
                if (!File.Exists(mshPath)) mshPath = Path.Combine(xdir, Path.GetFileName(rel).ToUpperInvariant() + ".MSH");
                if (File.Exists(mshPath))
                {
                    try { md = LoadEffectMesh(File.ReadAllBytes(mshPath), xdir, idx, rel); }
                    catch (Exception e) { Debug.LogWarning("[eft-mesh] load error idx " + idx + ": " + e.Message); }
                }
                if (md == null) Debug.LogWarning("[eft-mesh] missing/failed mesh idx " + idx + " (" + rel + ")");
            }
            _eftMeshCache[idx] = md;
            return md;
        }

        // Parse an SDO effect-mesh (.MSH, FVF 0x112 = pos+normal+uv, stride 32) into a Unity mesh + its texture.
        // This variant differs from SCENE.MSH (its post-vertex material block is laid out differently, so SceneLoader
        // mis-reads numMat), but the geometry header is identical: magic "Mesh00000030", submeshCount, then
        // [fvf, idxBytes, opt, indices, vertBytes, stride, verts]. We parse block 0 (aef03_00 = 15 verts / 16 tris)
        // and texture it with the mesh's own DDS — preferring the distinctively-coloured "aef_3*" material (the blue
        // the user identified) over the generic image/aef_0_* parts. Verbatim verts (D3D9 & Unity share LH).
        private static EftMeshData LoadEffectMesh(byte[] d, string xdir, int idx, string rel)
        {
            if (d == null || d.Length < 32 || System.Text.Encoding.ASCII.GetString(d, 0, 4) != "Mesh") return null;
            // MULTI-SUBMESH effect mesh (e.g. SCN0008 delta_line = aka/ao/ki, the 3 colour lines): parse ALL submeshes
            // via MshLoader and keep each submesh's own material, so every colour renders (the single-block path below
            // — kept for the combo aef03_00 whose material name needs the aef_3 heuristic — would show only 1 colour).
            int submeshCount0 = (int)(uint)(d[12] | (d[13] << 8) | (d[14] << 16) | (d[15] << 24));
            if (submeshCount0 > 1)
            {
                var r = MshLoader.Load(d);
                if (r == null || r.Submeshes.Count == 0) return null;
                string meshDir = Path.Combine(xdir, Path.GetDirectoryName(rel.Replace('\\', '/')) ?? "");
                int total = 0; foreach (var s in r.Submeshes) total += s.Mesh.vertexCount;
                var vv = new Vector3[total]; var uu = new Vector2[total];
                var subTris = new int[r.Submeshes.Count][]; var subTex2 = new Texture2D[r.Submeshes.Count];
                int vb = 0;
                for (int si = 0; si < r.Submeshes.Count; si++)
                {
                    var sm = r.Submeshes[si]; var sv = sm.Mesh.vertices; var su = sm.Mesh.uv;
                    System.Array.Copy(sv, 0, vv, vb, sv.Length);
                    if (su != null && su.Length == sv.Length) System.Array.Copy(su, 0, uu, vb, su.Length);
                    var stt = sm.Mesh.triangles; var ot = new int[stt.Length];
                    for (int t = 0; t < stt.Length; t++) ot[t] = stt[t] + vb;
                    subTris[si] = ot;
                    subTex2[si] = !string.IsNullOrEmpty(sm.Dds) ? (LoadXmeshDds(meshDir, sm.Dds) ?? LoadXmeshDds(xdir, sm.Dds)) : null;
                    vb += sv.Length;
                }
                var mm = new Mesh { name = "eft-mesh-" + idx };
                if (total > 65535) mm.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mm.vertices = vv; mm.uv = uu; mm.subMeshCount = r.Submeshes.Count;
                for (int si = 0; si < r.Submeshes.Count; si++) mm.SetTriangles(subTris[si], si);
                mm.RecalculateBounds();
                Debug.Log($"[eft-mesh] idx {idx} '{rel}': MULTI {r.Submeshes.Count} sub, tex=[{string.Join(",", System.Array.ConvertAll(subTex2, x => x != null ? "ok" : "null"))}]");
                var md2 = new EftMeshData { Mesh = mm, SubTex = subTex2 };

                // .MOT-DRIVEN RIGID PROP (SCN0008 delta_line, xmesh 172): if a sibling .HRC + .MOT exist and the
                // submeshes are weightless, expose per-submesh meshes + skeleton so EftEffect animates each colour bar
                // on its OWN bone (DELTA_LINE.MOT extends them via scale.Y). Same rigid-no-weights logic as the mapobj
                // path (~1184-1196); same dual path resolution as the .MSH (listed subpath, then flattened basename).
                var hrcPath = Path.Combine(xdir, rel + ".HRC");
                if (!File.Exists(hrcPath)) hrcPath = Path.Combine(xdir, Path.GetFileName(rel).ToUpperInvariant() + ".HRC");
                var motPath = Path.Combine(xdir, rel + ".MOT");
                if (!File.Exists(motPath)) motPath = Path.Combine(xdir, Path.GetFileName(rel).ToUpperInvariant() + ".MOT");
                if (File.Exists(hrcPath) && File.Exists(motPath))
                {
                    HrcLoader hrc = null; MotLoader mot = null;
                    try { hrc = HrcLoader.Load(File.ReadAllBytes(hrcPath)); } catch (Exception e) { Debug.LogWarning("[eft-mesh] hrc load " + hrcPath + ": " + e.Message); }
                    try { mot = MotLoader.Load(File.ReadAllBytes(motPath)); } catch (Exception e) { Debug.LogWarning("[eft-mesh] mot load " + motPath + ": " + e.Message); }
                    bool rigidNoWeights = hrc != null && hrc.BindWorld != null;
                    if (rigidNoWeights) foreach (var sub in r.Submeshes) if (sub.BoneHrc != null) { rigidNoWeights = false; break; }
                    if (hrc != null && mot != null && rigidNoWeights)
                    {
                        int[] leaves = HrcLeafBones(hrc);
                        var subMeshes = new Mesh[r.Submeshes.Count];
                        var subBone = new int[r.Submeshes.Count];
                        for (int si = 0; si < r.Submeshes.Count; si++)
                        {
                            subMeshes[si] = r.Submeshes[si].Mesh;   // verts in bone-local space (NOT baked) — followed by its bone
                            // EFT particle mesh ≠ scene geometry: it has NO baked vertex lighting (its per-vertex DIFFUSE is
                            // 0x00000000 = black). MshLoader read that into colors32, and the additive particle shader
                            // (Legacy Particles/Additive) MULTIPLIES vertex colour → ×0 = the bars vanished. Drop colours so
                            // they fall back to white (the colour comes from the emitter channels via _TintColor, not vertices).
                            subMeshes[si].colors32 = null;
                            subBone[si] = EftSubmeshBone(r.Submeshes[si].Dds, hrc, leaves, si);
                        }
                        md2.Hrc = hrc; md2.Mot = mot; md2.SubmeshMeshes = subMeshes; md2.SubmeshTex = subTex2; md2.SubmeshBone = subBone;
                        Debug.Log($"[eft-mesh] idx {idx} '{rel}': MOT-rigid {r.Submeshes.Count} sub, bones=[{string.Join(",", subBone)}] motMaxTime={mot.MaxTime}");
                    }
                }
                return md2;
            }
            int p = 12;
            uint U() { uint v = (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24)); p += 4; return v; }
            float F(int o) => BitConverter.ToSingle(d, o);
            U();                                  // submeshCount (block 0 only)
            U();                                  // fvf (0x112)
            int idxBytes = (int)U(); U();         // index bytes, options
            int idxCount = idxBytes / 2;
            if (idxCount <= 0 || p + idxBytes > d.Length) return null;
            var tris = new int[idxCount];
            for (int i = 0; i < idxCount; i++) { tris[i] = (ushort)(d[p] | (d[p + 1] << 8)); p += 2; }
            int vertBytes = (int)U(); int stride = (int)U();
            if (stride < 16 || vertBytes <= 0 || p + vertBytes > d.Length) return null;
            int vcount = vertBytes / stride, uvOff = stride - 8;
            var verts = new Vector3[vcount]; var uvs = new Vector2[vcount];
            for (int i = 0; i < vcount; i++)
            {
                int b = p + i * stride;
                verts[i] = new Vector3(F(b), F(b + 4), F(b + 8));      // verbatim (D3D9 & Unity both LH)
                uvs[i] = new Vector2(F(b + uvOff), F(b + uvOff + 4));  // V not flipped (same as scene/avatar)
            }
            var mesh = new Mesh { name = "eft-mesh-" + idx };
            mesh.vertices = verts; mesh.uv = uvs; mesh.triangles = tris; mesh.RecalculateBounds();

            // pick the texture: scan the file's embedded .dds names, prefer an "aef_3*" (the coloured part), else the
            // first non-"image" material, else the first. Then resolve the file in XMESH\ (NTFS case-insensitive).
            var names = new List<string>();
            for (int o = 0; o + 4 < d.Length; o++)
                if (d[o] == '.' && d[o + 1] == 'd' && d[o + 2] == 'd' && d[o + 3] == 's')
                {
                    int s = o; while (s > 0 && d[s - 1] > 32 && d[s - 1] < 127) s--;
                    names.Add(System.Text.Encoding.ASCII.GetString(d, s, o - s + 4));
                }
            string pick = null;
            foreach (var n in names) if (n.ToLowerInvariant().Contains("aef_3")) { pick = n; break; }
            if (pick == null) foreach (var n in names) if (!n.ToLowerInvariant().Contains("image")) { pick = n; break; }
            if (pick == null && names.Count > 0) pick = names[0];
            Texture2D tex = pick != null ? LoadXmeshDds(xdir, pick) : null;
            Debug.Log($"[eft-mesh] idx {idx} '{rel}': {vcount}v/{idxCount / 3}t, dds=[{string.Join(",", names)}] picked '{pick}' tex={(tex != null)}");
            return new EftMeshData { Mesh = mesh, SubTex = new[] { tex } };
        }

        // Map an effect submesh to the HRC bone it rides, by matching the submesh's DDS COLOUR token to the bone name
        // (SCN0008 delta_line: aka_line→Frame*_aka_*, ao_line→*_ao_*, ki_line→*_ki_*). Falls back to the leaf bones in
        // order when no colour matches, so a generic multi-part rigid prop still animates.
        private static int EftSubmeshBone(string dds, HrcLoader hrc, int[] leaves, int si)
        {
            if (hrc != null && hrc.Names != null && !string.IsNullOrEmpty(dds))
            {
                string low = dds.ToLowerInvariant();
                foreach (var tok in new[] { "aka", "ao", "ki" })   // distinct substrings (aka/ao/ki) — colour of the bar
                    if (low.Contains(tok))
                        for (int b = 0; b < hrc.Names.Length; b++)
                            if (hrc.Names[b] != null && hrc.Names[b].ToLowerInvariant().Contains(tok)) return b;
            }
            return leaves != null && leaves.Length > 0 ? leaves[System.Math.Min(si, leaves.Length - 1)] : -1;
        }

        // Resolve a DDS referenced inside a .MSH: the stored name may carry leading binary/junk bytes (e.g. "LBaef_3_00.dds"),
        // so try progressively-trimmed suffixes until one names a real file under XMESH\ (case-insensitive on Windows).
        private static Texture2D LoadXmeshDds(string xdir, string rawName)
        {
            for (int s = 0; s < rawName.Length; s++)
            {
                var cand = Path.GetFileName(rawName.Substring(s));
                if (cand.Length < 5) continue;
                var fp = Path.Combine(xdir, cand);
                if (File.Exists(fp)) { try { return DdsLoader.Load(File.ReadAllBytes(fp)); } catch { return null; } }
            }
            return null;
        }

    }
}
