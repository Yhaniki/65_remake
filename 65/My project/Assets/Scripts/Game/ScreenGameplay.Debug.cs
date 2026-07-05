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
    // F4 in-game debug panel (OnGUI)
    public partial class ScreenGameplay
    {
        // standard room "速度" steps (matches RoomConfig.speedSteps defaults) — quick-select buttons for live tuning
        private static readonly float[] ScrollSpeedSteps = { 1.0f, 1.5f, 2.0f, 2.5f, 3.0f, 4.0f, 5.0f, 6.0f, 8.0f };

        // in-game debug tuning sliders (F4 toggles). Board alpha applies live; burst size/brightness apply to the
        // next bursts (taps fire continuously, so the effect shows within ~0.3s).
        private void OnGUI()
        {
            if (!_showDebugUI) return;
            float h = Mathf.Min(560f, Screen.height - 16f);
            GUILayout.BeginArea(new Rect(Screen.width - 280, 8, 270, h), GUI.skin.box);

            // --- TABS: pinned header (F4-hide + tab bar) is tiny; ALL controls live inside the scroll view per tab, so
            // each group (Play / Combo / Stage) gets the full panel height instead of fighting one growing slider list. ---
            GUILayout.Label("[F4 hide]   Debug");
            _dbgTab = GUILayout.Toolbar(_dbgTab, DbgTabs);

            // ── GLOBAL 遊戲內時間流速 (slow-mo / 暫停) — 每個分頁都在，方便觀察音符/特效。也可用鍵盤 [ ] \ = ──
            // (縮放 Time.timeScale：音符/舞者/特效全部一起變慢；音樂 AudioSource 不隨之變速，是純觀察工具。)
            {
                bool paused = Time.timeScale <= 0f;
                GUILayout.BeginHorizontal();
                GUILayout.Label($"時間流速: {(paused ? "⏸ 暫停" : _timeScale.ToString("0.00") + "×")}", GUILayout.Width(110));
                if (GUILayout.Button("1×", GUILayout.Width(32))) SetTimeScale(1f);
                if (GUILayout.Button(paused ? "▶" : "❚❚", GUILayout.Width(32))) { if (paused) SetTimeScale(_timeScale); else Time.timeScale = 0f; }
                GUILayout.EndHorizontal();
                float ts = GUILayout.HorizontalSlider(_timeScale, 0.05f, 2f);
                if (Mathf.Abs(ts - _timeScale) > 1e-3f) SetTimeScale(ts);
            }

            // slow-mo time control is mode-level (observation) — keep it reachable on every tab while observing.
            if (observeBurstMode)
            {
                bool paused = Time.timeScale <= 0f;
                GUILayout.Label("== OBSERVE ==  cam0, no dance/notes/music");
                GUILayout.Label($"Time: {(paused ? "PAUSED" : _timeScale.ToString("0.00") + "×")}   [ ] slow/fast, \\ pause, = reset");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("0.1×")) SetTimeScale(0.1f);
                if (GUILayout.Button("0.25×")) SetTimeScale(0.25f);
                if (GUILayout.Button("0.5×")) SetTimeScale(0.5f);
                if (GUILayout.Button("1×")) SetTimeScale(1f);
                if (GUILayout.Button(paused ? "▶" : "❚❚")) { if (paused) SetTimeScale(_timeScale); else Time.timeScale = 0f; }
                GUILayout.EndHorizontal();
                GUILayout.Space(4);
            }

            _dbgScroll = GUILayout.BeginScrollView(_dbgScroll);
            if (_dbgTab == 0)        // ===== PLAY: playtest + body shape =====
            {
                autoPlay = GUILayout.Toggle(autoPlay, autoPlay ? " Auto-play: ON" : " Auto-play: OFF — manual");
                GUILayout.Label("Manual keys  L/D/U/R = A S W D  or  Num 4 5 8 6");
                GUILayout.Label($"Force hit grade: {(forcedJudge < 0 ? "Real (timing)" : ForceJudgeLabels[forcedJudge + 1])}");
                forcedJudge = GUILayout.Toolbar(forcedJudge + 1, ForceJudgeLabels) - 1;   // 0=Real(-1), 1..4=Perfect..Miss
                GUILayout.Space(6);
                // ── note scroll speed (osu-style, fixed base tempo) ──
                GUILayout.Label($"Scroll 速度: {scrollSpeedMul:F1}×  → {ManiaScroll.BaseVelocityFor(scrollSpeedMul, referenceBpm):F0}px/s base");
                GUILayout.BeginHorizontal();
                for (int i = 0; i < ScrollSpeedSteps.Length; i++)
                    if (GUILayout.Button(ScrollSpeedSteps[i].ToString("0.#"))) { scrollSpeedMul = ScrollSpeedSteps[i]; BuildScroll(); }
                GUILayout.EndHorizontal();
                GUILayout.Label($"基準 BPM (固定，不隨歌曲BPM): {referenceBpm:F0}");
                float nb = GUILayout.HorizontalSlider(referenceBpm, 60f, 240f);
                if (Mathf.Abs(nb - referenceBpm) > 0.5f) { referenceBpm = nb; BuildScroll(); }
                bool cs = GUILayout.Toggle(constantScroll, constantScroll
                    ? " 固定速度 ON：osu Constant，全程不變速（忽略BPM/SV）"
                    : " 固定速度 OFF：osu 預設，內部仍隨 BPM變速/SV 變速");
                if (cs != constantScroll) { constantScroll = cs; BuildScroll(); }
                GUILayout.Space(6);
                // 體型 (fat/thin): preset buttons (faithful SDO body indices) + a fine B slider — re-shape the dancer LIVE.
                GUILayout.Label($"Body shape (thin..fat): B={_bodyShapeB:F3}  (1.00 = standard)");
                GUILayout.BeginHorizontal();
                for (int i = 0; i < BodyShapeLabels.Length; i++)
                    if (GUILayout.Button(BodyShapeLabels[i]))
                    { _bodyShapeB = SdoBodyShape.WeightFromIndex(i, maleBody); if (_avatar) _avatar.SetBodyShape(_bodyShapeB); }
                GUILayout.EndHorizontal();
                float newB = GUILayout.HorizontalSlider(_bodyShapeB, 0.7f, 1.4f);   // fine override (continuous)
                if (Mathf.Abs(newB - _bodyShapeB) > 1e-4f) { _bodyShapeB = newB; if (_avatar) _avatar.SetBodyShape(_bodyShapeB); }
            }
            else if (_dbgTab == 1)   // ===== COMBO: fire bursts + combo/mesh/trail tuning =====
            {
                // fire a specific combo burst on demand (tier 0..4 = 100..500COMBO).
                GUILayout.BeginHorizontal();
                GUILayout.Label("Fire combo:", GUILayout.Width(66));
                for (int t = 0; t < 5; t++) if (GUILayout.Button(((t + 1) * 100).ToString())) SpawnComboBurst(t);
                GUILayout.EndHorizontal();
                if (GUILayout.Button("Fire FINISHED (result firework)")) SpawnNamedEft("FINISHED", 5f);

                // ── 300 AEF_3_00 (blue flame) — pinned FIRST so it's the easiest group to find/tune ──
                GUILayout.Space(6);
                GUILayout.Label("══ 300 AEF_3_00 藍焰 ══");
                GUILayout.Label($"300 AEF_3_00 透明度 opacity: {EftEffect.Mesh300Alpha:F2} (低=透明/淡)");
                EftEffect.Mesh300Alpha = GUILayout.HorizontalSlider(EftEffect.Mesh300Alpha, 0.05f, 1f);
                GUILayout.Label($"300 AEF_3_00 亮度 intensity: {EftEffect.Mesh300Intensity:F1}× (1=raw/drowned)");
                EftEffect.Mesh300Intensity = GUILayout.HorizontalSlider(EftEffect.Mesh300Intensity, 1f, 8f);
                GUILayout.Label($"300 AEF_3_00 前後 Z: {EftEffect.Mesh300Z:F2} (− 往後不擋球 / + 往前)");
                EftEffect.Mesh300Z = GUILayout.HorizontalSlider(EftEffect.Mesh300Z, -2f, 2f);
                EftEffect.Mesh300Straight = GUILayout.Toggle(EftEffect.Mesh300Straight, " 300 AEF_3_00 拉直 straighten (de-lean)");
                GUILayout.Label($"300 AEF_3_00 寬度 width: {EftEffect.MeshWidthMatch:F2}× (vs ball)");
                EftEffect.MeshWidthMatch = GUILayout.HorizontalSlider(EftEffect.MeshWidthMatch, 0.1f, 1.2f);
                GUILayout.Label($"300 AEF_3_00 收縮 shrink: {EftEffect.MeshShrinkEnd:F2} end (低=更小更快)");
                EftEffect.MeshShrinkEnd = GUILayout.HorizontalSlider(EftEffect.MeshShrinkEnd, 0.05f, 1f);
                GUILayout.Label($"300 AEF_3_00 出生 W×{EftEffect.MeshStartW:F1} / H×{EftEffect.MeshStartH:F1} (→1 by end)");
                EftEffect.MeshStartW = GUILayout.HorizontalSlider(EftEffect.MeshStartW, 1f, 4f);
                EftEffect.MeshStartH = GUILayout.HorizontalSlider(EftEffect.MeshStartH, 1f, 8f);
                GUILayout.Label($"300 AEF_3_00 下降錨點 drop: {EftEffect.MeshDropFrac:F2} (0=貼球/跟上, 1=球底)");
                EftEffect.MeshDropFrac = GUILayout.HorizontalSlider(EftEffect.MeshDropFrac, 0f, 1.5f);

                // ── 200 AEF_3_00 (ground curtain) ──
                GUILayout.Space(6);
                GUILayout.Label("══ 200 AEF_3_00 地面 ══");
                GUILayout.Label($"200 AEF_3_00 亮度 intensity: {EftEffect.MeshIntensity:F1}× (1=raw/drowned)");
                EftEffect.MeshIntensity = GUILayout.HorizontalSlider(EftEffect.MeshIntensity, 1f, 8f);
                GUILayout.Label($"200 AEF_3_00 透明度 opacity: {EftEffect.MeshAlpha:F2}");
                EftEffect.MeshAlpha = GUILayout.HorizontalSlider(EftEffect.MeshAlpha, 0.2f, 1f);
                GUILayout.Label($"200 AEF_3_00 數量 count: {EftEffect.MeshMax200} (official ~5-6)");
                EftEffect.MeshMax200 = Mathf.RoundToInt(GUILayout.HorizontalSlider(EftEffect.MeshMax200, 1f, 15f));

                // ── burst / glow / exposure (200+300 common) ──
                GUILayout.Space(6);
                GUILayout.Label("══ Burst / glow ══");
                GUILayout.Label($"Combo-burst size: {comboBurstSize:F2}×  (press B to test)");
                comboBurstSize = GUILayout.HorizontalSlider(comboBurstSize, 0.3f, 3f);
                GUILayout.Label($"Combo-burst brightness: {comboBurstBright:F2}× (1.0=faithful)");
                comboBurstBright = GUILayout.HorizontalSlider(comboBurstBright, 0.2f, 2.5f);
                GUILayout.Label($"Outer-glow intensity: {comboGlow:F2}× (0=off/faithful)");
                comboGlow = GUILayout.HorizontalSlider(comboGlow, 0f, 3f);
                GUILayout.Label($"Outer-glow spread: {comboGlowSpread:F2}× bigger than particle");
                comboGlowSpread = GUILayout.HorizontalSlider(comboGlowSpread, 0f, 3f);
                GUILayout.Label($"Combo spawn exposure: {EftEffect.BallCoreIntensity:F1}× (200/300 white-hot at birth; 1=off)");
                EftEffect.BallCoreIntensity = GUILayout.HorizontalSlider(EftEffect.BallCoreIntensity, 1f, 10f);
                GUILayout.Label($"Combo exposure fade: {EftEffect.BallCoreExpoFrac:F2} of life (→real colour; lower=colour sooner)");
                EftEffect.BallCoreExpoFrac = GUILayout.HorizontalSlider(EftEffect.BallCoreExpoFrac, 0.05f, 0.8f);
                // combo TRAIL streaks (200/300's light flares = engine 0x20000 = a unit quad stretched by animScale.y, NOT a
                // swept band; length is the scaleY channel, so only the WIDTH is tunable here — 1× = faithful)
                GUILayout.Label($"Combo trail width: {EftEffect.TrailWidthMul:F2}×  (200/300 light streaks, 1=faithful)");
                EftEffect.TrailWidthMul = GUILayout.HorizontalSlider(EftEffect.TrailWidthMul, 0.2f, 3f);

                // ── 氣條 ShowTime gauge (POWER_*.EFT head glow) — see docs SDO_SHOWTIME round-4 ──
                GUILayout.Space(6);
                GUILayout.Label("══ 氣條 Gauge (POWER) ══");
                GUILayout.Label("隔離顯示 (診斷頭光有沒有畫出來):");
                EftEffect.PowerIsolate = GUILayout.Toolbar(EftEffect.PowerIsolate, new[] { "全部", "只ribbon", "只頭光" });
                EftEffect.PowerEngineSampler = GUILayout.Toggle(EftEffect.PowerEngineSampler,
                    " 引擎2點取樣 (ON=官方:光暈收斂可見/OFF=全曲線膨脹成霧看不到)");
                GUILayout.Label($"頭光白熱 head white-hot: {EftEffect.PowerHeadGlowBright:F2}× (naga00星+sparks爆白; 1=faithful-soft)");
                EftEffect.PowerHeadGlowBright = GUILayout.HorizontalSlider(EftEffect.PowerHeadGlowBright, 1f, 6f);
                GUILayout.Label($"頭光暈(大顆) halo: {EftEffect.PowerHaloBright:F2}× (aef_4_02 接在填充頭的大光暈=官方那顆)");
                EftEffect.PowerHaloBright = GUILayout.HorizontalSlider(EftEffect.PowerHaloBright, 0.5f, 8f);
                GUILayout.Label($"白熱核大小 white-core size: {EftEffect.PowerWhiteSize:F2}× (1=忠實; 穩定靠整數位置截斷=引擎正解, 不是pin)");
                EftEffect.PowerWhiteSize = GUILayout.HorizontalSlider(EftEffect.PowerWhiteSize, 1f, 4f);
                GUILayout.Label($"交叉帶亮度 cross-dim: {EftEffect.PowerCrossDim:F2} (slot3 斜向交叉電流帶亮度, 太亮會洗白)");
                EftEffect.PowerCrossDim = GUILayout.HorizontalSlider(EftEffect.PowerCrossDim, 0f, 1f);
                GUILayout.Label($"交叉帶角度 cross-angle: {EftEffect.PowerCrossAngle:F0}° (slot3 斜向角度; 0=與水平帶重疊, 越大越斜)");
                EftEffect.PowerCrossAngle = GUILayout.HorizontalSlider(EftEffect.PowerCrossAngle, 0f, 90f);
                GUILayout.Label($"集氣條電流速度 gauge speed: {energyStripSpeed:F2}× (crackle 快慢+密度; 1=官方節奏)");
                energyStripSpeed = GUILayout.HorizontalSlider(energyStripSpeed, 0.5f, 4f);
                foreach (var g in _gaugeStrip) { var e = g ? g.GetComponent<EftEffect>() : null; if (e) e.SpeedMul = energyStripSpeed; }   // live-apply to the running gauge
                GUILayout.Label($"爆發側電流速度 side speed: {showtimeBurstSideSpeed:F2}× (noteboard 左右 EDGE4 柱; 只窗口內有效)");
                showtimeBurstSideSpeed = GUILayout.HorizontalSlider(showtimeBurstSideSpeed, 0.5f, 4f);
                foreach (var g in _boardBurstGos) { var e = g ? g.GetComponent<EftEffect>() : null; if (e && e.Persistent) e.SpeedMul = showtimeBurstSideSpeed; }   // live-apply to the looping side columns (skip the one-shot centre BOOM)
                GUILayout.Label($"0位置左推 empty hide: {gaugeEmptyHideP:F0} wu (氣條=0 時把頭光藏到可視左緣外; 太小=0就露頭光, 太大=打好幾下才冒出來)");
                gaugeEmptyHideP = GUILayout.HorizontalSlider(gaugeEmptyHideP, 0f, 200f);
            }
            else if (_dbgTab == 2)    // ===== STAGE: board / hit-burst / HP / floor-ring / hand-trail =====
            {
                GUILayout.Label($"Opening intro: {openingIntroSec:F1}s {(_trackVisible ? "(shown)" : "(holding — camera only)")}");
                openingIntroSec = GUILayout.HorizontalSlider(openingIntroSec, 0f, 15f);   // board+HP+READY appear after this; tunable live during the hold
                GUILayout.Label($"相機開場切掉前: {camIntroSkipSec:F1}s (從第N秒的frame開始放；重進歌套用)");
                camIntroSkipSec = GUILayout.HorizontalSlider(camIntroSkipSec, 0f, 10f);
                GUILayout.Label($"Board opacity: {boardAlpha:F2}× (1=native, ~1.4=official, ~2.6=opaque)");
                boardAlpha = GUILayout.HorizontalSlider(boardAlpha, 0f, 2.6f);
                GUILayout.Label($"Board X nudge: {boardX:F0}px");
                boardX = GUILayout.HorizontalSlider(boardX, -40f, 40f);
                GUILayout.Label($"Burst size: {burstSize:F2}×");
                burstSize = GUILayout.HorizontalSlider(burstSize, 0.3f, 3f);
                GUILayout.Label($"Burst brightness: {burstBright:F2}×");
                burstBright = GUILayout.HorizontalSlider(burstBright, 0.3f, 3f);

                // ── NoteType skin (live test): switches the WHOLE skin — board (NOTEIMAGE) + hit burst + combo/judge. ──
                // The cycle runs stock → 0..N-1 (2D sprite skins) → "3D" (hiteft3D = the AU_HIT.EFT 3DEFT hit burst).
                int nN = NoteTypeEftSuffix.Length;   // 2D sprite skins
                int total = nN + 1;                  // + the "3D" skin at index nN
                int cur = _hit3dMode ? nN : _eftNoteType;   // -1 = stock
                GUILayout.Label(_hit3dMode ? $"Note skin: 3D 立體 (hiteft3D)：音符依節拍上色 洋紅/藍/綠 + {Hit3dEftNames[hit3dEftIdx]}.EFT 命中"
                    : _eftNoteType < 0 ? "Note skin: stock"
                    : $"Note skin: {_eftNoteType} → board NOTEIMAGE_{NoteTypeBoardSuffix[_eftNoteType]} / EFT_{NoteTypeEftSuffix[_eftNoteType]}");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("◄ prev")) SelectSkin(((cur < 0 ? 0 : cur) + total - 1) % total);
                if (GUILayout.Button("next ►")) SelectSkin(cur < 0 ? 0 : (cur + 1) % total);
                GUILayout.EndHorizontal();
                if (GUILayout.Button(_hit3dMode ? "Fire 3D hit (all lanes)" : "Fire hit-burst (all lanes)"))
                    for (int l = 0; l < Keys; l++) { if (_hit3dMode) SpawnHit3d(l); else if (_burstFrames != null) SpawnBurst(l, false); }
                if (_hit3dMode)   // 3D-hit tuning (only when the 3D skin is active)
                {
                    GUILayout.Label($"★ 整體等比例大小 master: {note3dMaster:F2}× (note+打擊區+long+閃光 一起縮放)");
                    { float m = GUILayout.HorizontalSlider(note3dMaster, 0.5f, 2f); if (Mathf.Abs(m - note3dMaster) > 1e-3f) { note3dMaster = m; PlaceReceptors(receptor3dScale); } }
                    note3dMesh = GUILayout.Toggle(note3dMesh, note3dMesh ? " ══ note = 真 3D mesh（貼齊2D位置）: ON ══" : " ══ note = 2D 彩色精靈: OFF ══");
                    if (note3dMesh && _highway != null && _highway.Ready)
                    {
                        var hw = _highway;
                        GUILayout.Label($"3D note 大小: {hw.noteSize:F2}× (太大就調小)");
                        hw.noteSize = GUILayout.HorizontalSlider(hw.noteSize, 0.2f, 1.5f);
                        GUILayout.Label($"3D note 輝光速度 fps: {hw.noteFrameFps:F0} (0=不閃, 太快就調小)");
                        hw.noteFrameFps = GUILayout.HorizontalSlider(hw.noteFrameFps, 0f, 30f);
                        GUILayout.BeginHorizontal();
                        GUILayout.Label($"箭頭翻面 flattenX: {hw.flattenX:F0}", GUILayout.Width(140));
                        if (GUILayout.Button("+90")) hw.flattenX = 90f;
                        if (GUILayout.Button("-90")) hw.flattenX = -90f;
                        GUILayout.EndHorizontal();
                        GUILayout.Label($"箭頭整體轉向 baseRot: {hw.baseRotZ:F0}° (方向不對就轉90的倍數)");
                        GUILayout.BeginHorizontal();
                        if (GUILayout.Button("0")) hw.baseRotZ = 0f;
                        if (GUILayout.Button("90")) hw.baseRotZ = 90f;
                        if (GUILayout.Button("180")) hw.baseRotZ = 180f;
                        if (GUILayout.Button("270")) hw.baseRotZ = 270f;
                        GUILayout.EndHorizontal();
                    }
                    note3dFlip180 = GUILayout.Toggle(note3dFlip180, note3dFlip180 ? " 音符箭頭上下翻轉: ON" : " 音符箭頭上下翻轉: OFF（若箭頭方向相反就打開）");
                    GUILayout.Label($"打擊區大小 receptor: {receptor3dScale:F2}× (太大就調小)");
                    { float rs = GUILayout.HorizontalSlider(receptor3dScale, 0.4f, 1.2f); if (Mathf.Abs(rs - receptor3dScale) > 1e-3f) { receptor3dScale = rs; PlaceReceptors(receptor3dScale); } }
                    GUILayout.Label($"打擊區按下放大: {receptorPressAmt:F2} (官方 JUDGELINE_2 會 pop)");
                    receptorPressAmt = GUILayout.HorizontalSlider(receptorPressAmt, 0f, 0.6f);
                    GUILayout.Label($"長按寬度 holdWidth: {note3dHoldWidth:F2}× (跟 note 大小配)");
                    note3dHoldWidth = GUILayout.HorizontalSlider(note3dHoldWidth, 0.2f, 1.2f);
                    GUILayout.Label($"長按頭部間隙 headGap: {note3dHoldHeadGap:F0}px (long 起點在 note 下方多少)");
                    note3dHoldHeadGap = GUILayout.HorizontalSlider(note3dHoldHeadGap, -20f, 80f);
                    GUILayout.Label($"尾蓋微調 capOffset: {note3dCapOffset:F0}px (跟 long 尾端銜接)");
                    note3dCapOffset = GUILayout.HorizontalSlider(note3dCapOffset, -60f, 60f);
                    GUILayout.Label("long 貼圖: 官方 LONG.MSH 映射 (U .2243-.7683, V 錨定尾端, 不透明)");
                    GUILayout.Label($"3D hit 特效: {Hit3dEftNames[hit3dEftIdx]}  (官方=HIT 箭頭)");
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("◄ eft")) hit3dEftIdx = (hit3dEftIdx + Hit3dEftNames.Length - 1) % Hit3dEftNames.Length;
                    if (GUILayout.Button("eft ►")) hit3dEftIdx = (hit3dEftIdx + 1) % Hit3dEftNames.Length;
                    GUILayout.EndHorizontal();
                    GUILayout.Label("顏色 tint:");
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("黃")) hit3dTint = new Color(1f, 0.95f, 0.55f);
                    if (GUILayout.Button("金")) hit3dTint = new Color(1f, 0.80f, 0.25f);
                    if (GUILayout.Button("白")) hit3dTint = Color.white;
                    if (GUILayout.Button("藍")) hit3dTint = new Color(0.4f, 0.7f, 1f);
                    if (GUILayout.Button("紫")) hit3dTint = new Color(0.8f, 0.4f, 1f);
                    if (GUILayout.Button("紅")) hit3dTint = new Color(1f, 0.4f, 0.3f);
                    GUILayout.EndHorizontal();
                    GUILayout.Label($"tint RGB: {hit3dTint.r:F2}/{hit3dTint.g:F2}/{hit3dTint.b:F2}");
                    hit3dTint.r = GUILayout.HorizontalSlider(hit3dTint.r, 0f, 1f);
                    hit3dTint.g = GUILayout.HorizontalSlider(hit3dTint.g, 0f, 1f);
                    hit3dTint.b = GUILayout.HorizontalSlider(hit3dTint.b, 0f, 1f);
                    GUILayout.Label($"3D hit 大小 scale: {hit3dScale:F0}px");
                    hit3dScale = GUILayout.HorizontalSlider(hit3dScale, 20f, 300f);
                    GUILayout.Label($"3D hit 亮度 bright: {hit3dBright:F2}×");
                    hit3dBright = GUILayout.HorizontalSlider(hit3dBright, 0.3f, 3f);
                    GUILayout.Label($"3D hit 上飄 motion: {hit3dMotion:F2} (1=原速, 0=定住)");
                    hit3dMotion = GUILayout.HorizontalSlider(hit3dMotion, 0f, 1f);
                }

                GUILayout.Label($"Click-flash brightness: {clickFlashBright:F2}× (hit=white per lane)");
                clickFlashBright = GUILayout.HorizontalSlider(clickFlashBright, 0f, 1.5f);
                GUILayout.Label($"Miss red-flash: {missFlashAlpha:F2}× white (1=match, soft glow, all 4 lanes)");
                missFlashAlpha = GUILayout.HorizontalSlider(missFlashAlpha, 0f, 1.5f);
                GUILayout.Label($"Keydown burst: {recKeydownStepSec*1000f:F0}ms/frame ({recKeydownStepSec*5f*1000f:F0}ms total, 5 frames)");
                recKeydownStepSec = GUILayout.HorizontalSlider(recKeydownStepSec, 0.005f, 0.1f);
                GUILayout.Label($"HP-glow brightness: {hpGlowBright:F2}× (~1=old dim, 2.5=official)");
                hpGlowBright = GUILayout.HorizontalSlider(hpGlowBright, 0.3f, 5f);
                GUILayout.Label($"HP-glow X offset: {hpGlowOffsetX:F0}px (− = left toward bar)");
                hpGlowOffsetX = GUILayout.HorizontalSlider(hpGlowOffsetX, -48f, 8f);
                GUILayout.Label($"Floor-ring spread (radius): {ringOuterRadius:F0}");
                ringOuterRadius = GUILayout.HorizontalSlider(ringOuterRadius, 4f, 60f);
                GUILayout.Label($"Floor-ring brightness: {ringBrightness:F2}× (0=off)");
                ringBrightness = GUILayout.HorizontalSlider(ringBrightness, 0f, 2f);
                GUILayout.Label($"Floor-ring spin: {ringSpinDeg:F0}°/s");
                ringSpinDeg = GUILayout.HorizontalSlider(ringSpinDeg, -120f, 120f);
                GUILayout.Label($"Hand-trail width: {handTrailWidth:F2}×");
                handTrailWidth = GUILayout.HorizontalSlider(handTrailWidth, 0.1f, 3f);
                GUILayout.Label($"Hand-trail time: {handTrailTime:F2}s");
                handTrailTime = GUILayout.HorizontalSlider(handTrailTime, 0.05f, 1.2f);
                foreach (var rib in _handTrails) if (rib) { rib.widthMul = handTrailWidth; rib.life = handTrailTime; }
            }
            else if (_dbgTab == 3)    // ===== EMOJI: head marker + rank/roster + head-emoji test =====
            {
                GUILayout.Label("══ 頭頂名牌 Head marker（螢幕空間，對官方圖微調）══");
                if (_headMarker == null) GUILayout.Label("(name marker 尚未就緒：需載入舞者 avatar)");
                else
                {
                    GUILayout.Label($"名字字級 font(px): {_headMarker.nameFontPx:F0}");
                    _headMarker.nameFontPx = GUILayout.HorizontalSlider(_headMarker.nameFontPx, 8f, 64f);
                    GUILayout.Label($"箭頭寬度 arrow(px): {_headMarker.arrowDesignW:F0}");
                    _headMarker.arrowDesignW = GUILayout.HorizontalSlider(_headMarker.arrowDesignW, 6f, 64f);
                    GUILayout.Label($"離頭距離 up(世界): {_headMarker.upWorld:F1}");
                    _headMarker.upWorld = GUILayout.HorizontalSlider(_headMarker.upWorld, 0f, 50f);
                    GUILayout.Label($"箭頭/名字間距 gap(px): {_headMarker.arrowGapPx:F0}");
                    _headMarker.arrowGapPx = GUILayout.HorizontalSlider(_headMarker.arrowGapPx, 0f, 30f);
                    GUILayout.Label($"箭頭換幀 frame: {_headMarker.frameMs:F0}ms");
                    _headMarker.frameMs = GUILayout.HorizontalSlider(_headMarker.frameMs, 50f, 600f);
                }

                GUILayout.Space(8);
                GUILayout.Label("══ 排名/清單 Rank & roster（即時微調）══");
                GUILayout.Label($"清單字級 list font(px): {rosterFontWorld:F0}");
                rosterFontWorld = GUILayout.HorizontalSlider(rosterFontWorld, 10f, 48f);
                GUILayout.Label($"清單起始 firstY: {rosterFirstY:F0}");
                rosterFirstY = GUILayout.HorizontalSlider(rosterFirstY, 40f, 300f);
                GUILayout.Label($"清單列距 rowStep: {rosterRowStep:F0}");
                rosterRowStep = GUILayout.HorizontalSlider(rosterRowStep, 12f, 44f);
                GUILayout.Label($"名次數字寬 rankW(px): {rankDigitW:F0}  間距 pitch: {rankPitch:F0}");
                rankDigitW = GUILayout.HorizontalSlider(rankDigitW, 12f, 48f);
                rankPitch = GUILayout.HorizontalSlider(rankPitch, 14f, 48f);
                GUILayout.Label($"名次中心X / Y: {rankCenterX:F0} / {rankY:F0}");
                rankCenterX = GUILayout.HorizontalSlider(rankCenterX, 280f, 520f);
                rankY = GUILayout.HorizontalSlider(rankY, 36f, 130f);
                GUILayout.Label($"旁觀標題X/Y: {lookerTitleX:F0}/{lookerTitleY:F0}  字級:{lookerFontWorld:F0}");
                lookerTitleX = GUILayout.HorizontalSlider(lookerTitleX, 560f, 780f);
                lookerTitleY = GUILayout.HorizontalSlider(lookerTitleY, 150f, 320f);
                lookerFontWorld = GUILayout.HorizontalSlider(lookerFontWorld, 8f, 32f);
                GUILayout.Label($"旁觀名 X/起始Y/列距: {lookerX:F0}/{lookerFirstY:F0}/{lookerRowStep:F0}");
                lookerX = GUILayout.HorizontalSlider(lookerX, 560f, 780f);
                lookerFirstY = GUILayout.HorizontalSlider(lookerFirstY, 160f, 380f);
                lookerRowStep = GUILayout.HorizontalSlider(lookerRowStep, 10f, 30f);
                if (GUILayout.Button("套用清單版面 / re-layout list")) RelayoutRoster();

                GUILayout.Space(8);
                GUILayout.Label("══ 表情測試 Head emoji ══");
                if (_emoji == null) GUILayout.Label("(emoji 尚未就緒：需載入舞者 avatar)");
                else
                {
                    GUILayout.Label("點擊直接觸發（取當下人物位置後凍結）：");
                    for (int i = 0; i < EmojiTestButtons.Length; i += 2)
                    {
                        GUILayout.BeginHorizontal();
                        if (GUILayout.Button(EmojiTestButtons[i].label)) ShowEmoji(EmojiTestButtons[i].kind);
                        if (i + 1 < EmojiTestButtons.Length && GUILayout.Button(EmojiTestButtons[i + 1].label)) ShowEmoji(EmojiTestButtons[i + 1].kind);
                        GUILayout.EndHorizontal();
                    }
                    if (GUILayout.Button("Stop / 清除")) _emoji.Stop();

                    GUILayout.Space(6);
                    GUILayout.Label("位置 XYZ（槽位 world 座標 + 世界偏移）— 即時套用：");
                    GUILayout.Label($"X: {_emoji.xOff:F1}");
                    _emoji.xOff = GUILayout.HorizontalSlider(_emoji.xOff, -60f, 60f);
                    GUILayout.Label($"Y: {_emoji.yOff:F1}");
                    _emoji.yOff = GUILayout.HorizontalSlider(_emoji.yOff, -20f, 80f);
                    GUILayout.Label($"Z: {_emoji.zOff:F1}");
                    _emoji.zOff = GUILayout.HorizontalSlider(_emoji.zOff, -60f, 60f);

                    GUILayout.Space(4);
                    GUILayout.Label($"大小 scale: {_emoji.worldScale:F2}");
                    _emoji.worldScale = GUILayout.HorizontalSlider(_emoji.worldScale, 0.05f, 1.5f);
                    GUILayout.Label($"每幀 frame: {_emoji.frameMs:F0}ms");
                    _emoji.frameMs = GUILayout.HorizontalSlider(_emoji.frameMs, 50f, 1000f);
                    GUILayout.Label($"跟隨平滑 follow: {_emoji.followLerp:F1} (大=瞬移, 小=慢慢移)");
                    _emoji.followLerp = GUILayout.HorizontalSlider(_emoji.followLerp, 1f, 30f);
                }
            }
            if (_dbgTab == 4)    // ===== RESULT: 結算面板微調 (名字 / 頭框 / 頭像 AvtShow) =====
            {
                freeMode = GUILayout.Toggle(freeMode, freeMode ? " 自由模式: ON（無排名/無G·經驗，死亡=GAME OVER）" : " 自由模式: OFF");
                GUILayout.Space(6);
                GUILayout.Label("══ 結算名字 & 頭框（進結算後即時套用）══");
                if (_result == null) GUILayout.Label("(尚未進結算畫面)");
                else
                {
                    GUILayout.Label($"名字 X: {_result.nickX:F0}");
                    _result.nickX = GUILayout.HorizontalSlider(_result.nickX, 60f, 320f);
                    GUILayout.Label($"名字 Y偏移: {_result.nickYOff:F0}");
                    _result.nickYOff = GUILayout.HorizontalSlider(_result.nickYOff, -10f, 30f);
                    GUILayout.Label($"名字 大小 size: {_result.nickSize:F0}");
                    _result.nickSize = GUILayout.HorizontalSlider(_result.nickSize, 10f, 36f);
                    GUILayout.Space(6);
                    GUILayout.Label($"頭框 X: {_result.headBoxX:F0}");
                    _result.headBoxX = GUILayout.HorizontalSlider(_result.headBoxX, 0f, 90f);
                    GUILayout.Label($"頭框 Y偏移: {_result.headBoxYOff:F0}");
                    _result.headBoxYOff = GUILayout.HorizontalSlider(_result.headBoxYOff, -10f, 50f);
                    GUILayout.Label($"頭框 正方形大小 size: {_result.headBoxSize:F0}");
                    _result.headBoxSize = GUILayout.HorizontalSlider(_result.headBoxSize, 20f, 96f);
                    GUILayout.Label($"頭像 上方溢出 overflowTop: {_result.headOverflowTop:F0}px（頭髮長出框上緣的高度；底邊固定不變形）");
                    _result.headOverflowTop = GUILayout.HorizontalSlider(_result.headOverflowTop, 0f, 48f);
                }
                GUILayout.Space(8);
                GUILayout.Label("══ 頭像 AvtShow（idle 人物）即時套用 ══");
                headAutoFrame = GUILayout.Toggle(headAutoFrame, headAutoFrame
                    ? " 自動取景: ON（量測髮頂自動算距離→永不切頂；用 zoom 微調）"
                    : " 自動取景: OFF（手動 dist/瞄準偏移）");
                if (headAutoFrame)
                {
                    GUILayout.Label($"自動 zoom: {headZoom:F2}（>1 拉遠=頭變小+上方留白更多；<1 放大）");
                    headZoom = GUILayout.HorizontalSlider(headZoom, 0.5f, 2f);
                    GUILayout.Label($"（自動算出的 dist≈{headPortraitDist:F0}）");
                }
                GUILayout.Label($"旋轉 yaw: {headAvatarYaw:F0}°（官方結算=0 正面）");
                headAvatarYaw = GUILayout.HorizontalSlider(headAvatarYaw, 0f, 360f);
                GUILayout.Label($"縮放 scale: {headAvatarScale:F2}");
                headAvatarScale = GUILayout.HorizontalSlider(headAvatarScale, 0.2f, 6f);
                GUILayout.Label($"相機 距離 dist: {headPortraitDist:F0}（小=放大；自動取景時無效）");
                headPortraitDist = GUILayout.HorizontalSlider(headPortraitDist, 5f, 120f);
                GUILayout.Label($"相機 FOV: {headPortraitFov:F0}（官方=45）");
                headPortraitFov = GUILayout.HorizontalSlider(headPortraitFov, 10f, 60f);
                GUILayout.Label($"相機 俯角 pitch: {headPitchDeg:F1}°（官方≈2.3 正面略俯）");
                headPitchDeg = GUILayout.HorizontalSlider(headPitchDeg, -10f, 15f);
                GUILayout.Space(4);
                GUILayout.Label($"瞄準偏移 X: {headAimOffset.x:F1}");
                headAimOffset.x = GUILayout.HorizontalSlider(headAimOffset.x, -40f, 40f);
                GUILayout.Label($"瞄準偏移 Y: {headAimOffset.y:F1}（+往上對到臉）");
                headAimOffset.y = GUILayout.HorizontalSlider(headAimOffset.y, -40f, 40f);
                GUILayout.Label($"瞄準偏移 Z: {headAimOffset.z:F1}");
                headAimOffset.z = GUILayout.HorizontalSlider(headAimOffset.z, -40f, 40f);
            }
            if (_dbgTab == 5)    // ===== BANNER: YOU WIN/LOSE 位置 / 大小 / 動畫時間 + 預覽/播放測試 =====
            {
                GUILayout.Label("══ YOU WIN/LOSE 橫幅動畫（進結算畫面後即時套用）══");
                GUILayout.Label("只調動畫『起始位置』；結束位置與大小固定(官方)。可先按 F5 跳到結算畫面。");
                if (_result == null) GUILayout.Label("(尚未進結算畫面)");
                else
                {
                    GUILayout.Label($"起始位置 X(中心): {_result.bannerStartX:F0}");
                    _result.bannerStartX = GUILayout.HorizontalSlider(_result.bannerStartX, -100f, 900f);
                    GUILayout.Label($"起始位置 Y(中心): {_result.bannerStartY:F0}");
                    _result.bannerStartY = GUILayout.HorizontalSlider(_result.bannerStartY, -100f, 400f);
                    GUILayout.Label($"起始大小 scale: {_result.bannerStartScale:F2}");
                    _result.bannerStartScale = GUILayout.HorizontalSlider(_result.bannerStartScale, 0.5f, 6f);
                    GUILayout.Label($"動畫時間 sec: {_result.bannerAnimSec:F2}");
                    _result.bannerAnimSec = GUILayout.HorizontalSlider(_result.bannerAnimSec, 0.05f, 2f);
                    GUILayout.Space(6);
                    GUILayout.Label("預覽『起始』（定格在起始點+畫面寬，拖上面 X/Y 即時看）：");
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("預覽起始 WIN")) _result.PreviewBanner(true, true);
                    if (GUILayout.Button("預覽起始 LOSE")) _result.PreviewBanner(false, true);
                    GUILayout.EndHorizontal();
                    GUILayout.Label("預覽『結束』（定格在固定結束點/大小）：");
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("預覽結束 WIN")) _result.PreviewBanner(true, false);
                    if (GUILayout.Button("預覽結束 LOSE")) _result.PreviewBanner(false, false);
                    GUILayout.EndHorizontal();
                    GUILayout.Label("播放動畫（起始→結束）：");
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("播放 WIN")) _result.PlayBannerTest(true);
                    if (GUILayout.Button("播放 LOSE")) _result.PlayBannerTest(false);
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.EndScrollView();

            GUILayout.EndArea();
        }
    }
}
