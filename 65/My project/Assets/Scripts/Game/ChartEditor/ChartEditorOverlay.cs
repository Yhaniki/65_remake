using System;
using System.Collections.Generic;
using Sdo.Osu;
using Sdo.Ruleset;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 編輯器覆蓋層：音符板上的小節/拍格線，加上板子旁邊那條會跟著捲動的音樂波形。
    ///
    /// 關鍵是「時間 → Y」完全走 <see cref="ScreenGameplay.EditorYForTime"/>（＝音符自己用的那條 <c>YForTime</c>），
    /// 所以格線、波形、音符三者永遠在同一格上：變速（type-1）、掉落方向（向上/向下）、速度檔位改了都自動跟著，
    /// 這裡一行都不用改。每幀只重建「看得到的那段」（可視窗 ≈ 1~4 秒 → 幾十到幾百個四邊形），成本可以忽略。
    ///
    /// 一個 Mesh 畫完全部（頂點色），不用 SpriteRenderer：格線是任意粗細的長條、波形是每格一根對稱的刺，
    /// 用 sprite 拼會生出幾百個 GameObject。
    /// </summary>
    public sealed class ChartEditorOverlay : MonoBehaviour
    {
        public ScreenGameplay Game;
        public WaveformPeaks Peaks;              // null = 還在解碼（或沒有音檔）
        public double PeaksOffsetMs;             // 波形第 0 格對應的譜面時間 = 音樂起點的無聲數拍（type-10）

        public bool showGrid = true;
        public bool showWaveform = true;
        public int subdivisions = 4;             // 每拍細分：1=拍線 2=8分 4=16分 …

        /// <summary>判定線（完美時機的音符實際落點 = 受擊線 + judgeOffsetY）。</summary>
        public bool showJudgeLine;

        /// <summary>osu 式打擊誤差橫條（打拍測試用）。</summary>
        public bool showHitError;

        // 波形直條（design px）：貼在音符板右邊；面板置中時整條跟著右移，兩種版面都放得下。
        private const float StripGap = 12f;
        private const float StripWidth = 84f;

        private static readonly Color32 ColMeasure = new Color32(0x7a, 0x7a, 0x92, 0xff);
        private static readonly Color32 ColBeat = new Color32(0x44, 0x44, 0x58, 0xff);
        private static readonly Color32 ColSub = new Color32(0x2b, 0x2b, 0x36, 0xff);
        private static readonly Color32 ColStripBg = new Color32(0x14, 0x14, 0x1a, 0xff);
        private static readonly Color32 ColWavePeak = new Color32(0x4a, 0x3f, 0x0d, 0xff);   // 瞬態的淡暈（畫在 RMS 底下）
        private static readonly Color32 ColWaveLo = new Color32(0x8a, 0x77, 0x18, 0xff);
        private static readonly Color32 ColWaveHi = new Color32(0xf0, 0xcf, 0x3a, 0xff);
        private static readonly Color32 ColPlayhead = new Color32(0xe5, 0x39, 0x35, 0xff);
        private static readonly Color32 ColJudgeLine = new Color32(0x00, 0xe5, 0xff, 0xff);
        // 誤差條：判定窗色帶（暗）＋ tick/箭頭（亮）。配色沿用 osu 的分級色。
        private static readonly Color32 ColMissBand = new Color32(0x3a, 0x14, 0x14, 0xff);
        private static readonly Color32 ColBadBand = new Color32(0x4a, 0x3d, 0x0b, 0xff);
        private static readonly Color32 ColCoolBand = new Color32(0x24, 0x40, 0x0d, 0xff);
        private static readonly Color32 ColPerfectBand = new Color32(0x14, 0x3c, 0x4c, 0xff);
        private static readonly Color32 ColPerfect = new Color32(0x66, 0xcc, 0xff, 0xff);
        private static readonly Color32 ColCool = new Color32(0x88, 0xb3, 0x00, 0xff);
        private static readonly Color32 ColBad = new Color32(0xff, 0xcc, 0x22, 0xff);
        private static readonly Color32 ColMiss = new Color32(0xed, 0x11, 0x21, 0xff);
        private static readonly Color32 ColCentre = new Color32(0xff, 0xff, 0xff, 0xff);
        private static readonly Color32 ColArrow = new Color32(0xff, 0xff, 0xff, 0xe0);
        private static readonly Color32 ColEarly = new Color32(0x55, 0x99, 0xff, 0xff);
        private static readonly Color32 ColLate = new Color32(0xff, 0x88, 0x55, 0xff);

        private Mesh _mesh;
        private MeshRenderer _mr;
        private readonly List<Vector3> _verts = new List<Vector3>();
        private readonly List<Color32> _cols = new List<Color32>();
        private readonly List<int> _tris = new List<int>();
        private readonly List<BeatGrid.Line> _lines = new List<BeatGrid.Line>();
        private BeatGrid _grid;
        private OsuBeatmap _gridFor;

        /// <summary>可視窗掃描的步長/上限：慢速+低 BPM 時可視段可以到十幾秒，給足夠餘裕但仍有上限。</summary>
        private const double ScanStepMs = 40.0;
        private const int ScanCap = 900;         // 40ms × 900 = 36 秒

        private void Awake()
        {
            _mesh = new Mesh { name = "ChartEditorOverlay" };
            _mesh.MarkDynamic();
            gameObject.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _mr = gameObject.AddComponent<MeshRenderer>();
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, Color.white); tex.Apply();
            var mat = new Material(Shader.Find("Sprites/Default")) { mainTexture = tex };
            _mr.sharedMaterial = mat;
            _mr.sortingOrder = 2;                // 音符板(-30) 之上、音符(5) 之下
            _mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _mr.receiveShadows = false;
        }

        // LateUpdate：ScreenGameplay 的 Update 這一幀已經把 _nowMs 推進、音符也捲好了 → 這裡拿到的是同一個時間。
        private void LateUpdate()
        {
            _verts.Clear(); _cols.Clear(); _tris.Clear();
            if (Game != null && Game.EditorReady) Build();
            _mesh.Clear();
            if (_verts.Count > 0)
            {
                _mesh.SetVertices(_verts);
                _mesh.SetColors(_cols);
                _mesh.SetTriangles(_tris, 0);
            }
        }

        private void Build()
        {
            float top = Game.EditorClipTopY, bottom = Game.EditorClipBottomY;
            double now = Game.EditorNowMs;
            VisibleWindow(now, top, bottom, out double msLo, out double msHi);

            if (showGrid) BuildGrid(msLo, msHi, top, bottom);
            if (showWaveform) BuildWaveform(msLo, msHi, top, bottom);
            if (showJudgeLine) BuildJudgeLine();
            if (showHitError) BuildHitErrorBar();
        }

        // 判定線：完美時機的音符實際會落在 judgeLineY + judgeOffsetY（受擊線圖本身不動）。偏移 0 時就疊在受擊線上。
        private void BuildJudgeLine()
        {
            float y = Game.EditorJudgeLineY + Game.judgeOffsetY;
            float x0 = Game.EditorTrackLeftPx - 10f, x1 = Game.EditorTrackRightPx + 10f;
            Quad(x0, y - 1f, x1, y + 1f, ColJudgeLine);
            // 偏移不為 0 時，畫一條虛線把「受擊線」和「判定線」連起來，看得出差多少
            if (Mathf.Abs(Game.judgeOffsetY) >= 1f)
            {
                float ry = Game.EditorJudgeLineY;
                float lo = Mathf.Min(ry, y), hi = Mathf.Max(ry, y);
                for (float t = lo; t < hi; t += 6f)
                    Quad(x0 - 6f, t, x0 - 2f, Mathf.Min(t + 3f, hi), ColJudgeLine);
            }
        }

        // 可視時間窗：從現在往前/往後走，直到 Y 掉出音符板的可見帶為止。YForTime 對時間是單調的
        // （距離 = 捲動速度的積分，恆正），所以往外走一定會出界 —— 不會漏掉中間的段。
        private void VisibleWindow(double now, float top, float bottom, out double msLo, out double msHi)
        {
            const float Pad = 48f;
            msLo = now; msHi = now;
            for (int k = 1; k <= ScanCap; k++)
            {
                double t = now + k * ScanStepMs;
                msHi = t;
                float y = Game.EditorYForTime(t);
                if (y < top - Pad || y > bottom + Pad) break;
            }
            for (int k = 1; k <= ScanCap; k++)
            {
                double t = now - k * ScanStepMs;
                if (t < -4000.0) { msLo = t; break; }
                msLo = t;
                float y = Game.EditorYForTime(t);
                if (y < top - Pad || y > bottom + Pad) break;
            }
        }

        private void BuildGrid(double msLo, double msHi, float top, float bottom)
        {
            var map = Game.EditorMap;
            if (map == null) return;
            if (_grid == null || !ReferenceEquals(_gridFor, map)) { _grid = BeatGrid.From(map); _gridFor = map; }

            _grid.LinesInWindow(msLo, msHi, subdivisions, _lines);
            float x0 = Game.EditorTrackLeftPx - 2f, x1 = Game.EditorTrackRightPx + 2f;
            foreach (var ln in _lines)
            {
                float y = Game.EditorYForTime(ln.Ms);
                if (y < top || y > bottom) continue;
                float h; Color32 c;
                switch (ln.Kind)
                {
                    case BeatGrid.LineKind.Measure: h = 2.0f; c = ColMeasure; break;
                    case BeatGrid.LineKind.Beat: h = 1.5f; c = ColBeat; break;
                    default: h = 1.0f; c = ColSub; break;
                }
                Quad(x0, y - h * 0.5f, x1, y + h * 0.5f, c);
            }
        }

        private void BuildWaveform(double msLo, double msHi, float top, float bottom)
        {
            float sx0 = Game.EditorTrackRightPx + StripGap;
            float sx1 = Mathf.Min(sx0 + StripWidth, SdoLayout.Width - 4f);
            if (sx1 - sx0 < 8f) return;
            float cx = (sx0 + sx1) * 0.5f, halfMax = (sx1 - sx0) * 0.5f - 1f;

            Quad(sx0, top, sx1, bottom, ColStripBg);                        // 條的底（靜音處也看得到條在哪）
            Quad(cx - 0.5f, top, cx + 0.5f, bottom, ColSub);                // 中線

            var peaks = Peaks;
            if (peaks != null && peaks.Count > 0)
            {
                double bucket = peaks.BucketMs;
                // 對齊到 bucket 邊界（用譜面時間算，波形的第 0 格 = 音樂起點 = PeaksOffsetMs）
                double firstIdx = Math.Floor((msLo - PeaksOffsetMs) / bucket);
                double t = PeaksOffsetMs + firstIdx * bucket;
                int guard = 0;
                while (t <= msHi && guard++ < 8192)
                {
                    double tStart = t;
                    t += bucket;
                    float rms = peaks.RmsAtMs(tStart - PeaksOffsetMs);
                    float peak = peaks.PeakAtMs(tStart - PeaksOffsetMs);
                    if (peak <= 0.001f) continue;

                    float ya = Game.EditorYForTime(tStart), yb = Game.EditorYForTime(t);
                    float ylo = Mathf.Max(Mathf.Min(ya, yb), top), yhi = Mathf.Min(Mathf.Max(ya, yb), bottom);
                    if (yhi <= ylo) continue;

                    // 峰值（瞬態）＝外面那圈淡暈；RMS（音量包絡）＝裡面那根實體 —— 只畫峰值的話整條會是實心柱。
                    float hp = Mathf.Max(1f, peak * halfMax);
                    Quad(cx - hp, ylo, cx + hp, yhi, ColWavePeak);
                    float hr = rms * halfMax;
                    if (hr >= 0.5f)
                        Quad(cx - hr, ylo, cx + hr, yhi, Color32.Lerp(ColWaveLo, ColWaveHi, Mathf.Clamp01(rms * 1.4f)));
                }
            }

            float py = Game.EditorJudgeLineY;   // 播放頭（＝受擊線）：波形上的紅線就是「現在播到哪」
            Quad(sx0, py - 1f, sx1, py + 1f, ColPlayhead);
        }

        // ---------- osu 式打擊誤差橫條（BarHitErrorMeter） ----------
        //
        // 橫條左右兩端 = ±最大判定窗（Miss 邊界），中央 = 剛好準時。每一擊畫一根 tick（依判定上色），
        // 5 秒內淡出；下方的箭頭是誤差的指數移動平均（EMA，同 osu：avg = avg*0.9 + delta*0.1），
        // 它偏哪邊就代表你整體偏早/偏晚 —— 那就是要拿去修 global offset 的量。
        private struct Tick { public double Delta; public Judgment J; public float Born; }

        private readonly List<Tick> _ticks = new List<Tick>();
        private double _ema;
        private bool _emaPrimed;

        private const float BarW = 300f;         // 橫條寬（設計 px）
        private const float BarH = 12f;          // 判定窗色帶的高
        private const float TickH = 22f;         // tick 的高
        private const float TickLifeSec = 5f;    // tick 活多久（同 osu）

        /// <summary>打到一下就餵進來（deltaMs 為 NaN = miss，不畫 tick 也不進 EMA）。</summary>
        public void AddHit(double deltaMs, Judgment j)
        {
            if (double.IsNaN(deltaMs)) return;
            _ticks.Add(new Tick { Delta = deltaMs, J = j, Born = Time.realtimeSinceStartup });
            _ema = _emaPrimed ? _ema * 0.9 + deltaMs * 0.1 : deltaMs;   // osu 的 floatingAverage
            _emaPrimed = true;
            if (_ticks.Count > 120) _ticks.RemoveRange(0, _ticks.Count - 120);
        }

        public void ClearHits() { _ticks.Clear(); _ema = 0.0; _emaPrimed = false; }

        /// <summary>誤差的指數移動平均（毫秒）；還沒打過回 0。</summary>
        public double AverageError => _emaPrimed ? _ema : 0.0;

        private static Color32 JudgeColor(Judgment j)
        {
            switch (j)
            {
                case Judgment.Perfect: return ColPerfect;
                case Judgment.Cool: return ColCool;
                case Judgment.Bad: return ColBad;
                default: return ColMiss;
            }
        }

        private void BuildHitErrorBar()
        {
            var w = Game.EditorWindows;
            if (w == null || w.MissBoundary <= 0.0) return;

            // 放在軌道「右側」、與受擊線同高：音符不會從它身上穿過去（軌道裡任何位置都在音符的行進路線上），
            // 又剛好在眼睛盯著受擊線時的餘光範圍內 —— 這正是 osu 把它擺在遊玩區旁邊的理由。
            float cx = Game.EditorTrackRightPx + 30f + BarW * 0.5f;
            float cy = Game.EditorJudgeLineY;
            float half = BarW * 0.5f;
            float msToPx = half / (float)w.MissBoundary;   // 兩端 = ±Miss 邊界

            // 判定窗色帶：由寬到窄疊上去（大的先畫，小的蓋在上面）＝ 中央往外的同心帶，同 osu
            Band(cx, cy, (float)w.MissBoundary * msToPx, ColMissBand);
            Band(cx, cy, (float)w.Bad * msToPx, ColBadBand);
            Band(cx, cy, (float)w.Cool * msToPx, ColCoolBand);
            Band(cx, cy, (float)w.Perfect * msToPx, ColPerfectBand);
            Quad(cx - 1f, cy - BarH, cx + 1f, cy + BarH, ColCentre);   // 中央線 = 剛好準時

            // 每一擊的 tick（越舊越淡）
            float nowRt = Time.realtimeSinceStartup;
            for (int i = _ticks.Count - 1; i >= 0; i--)
            {
                float age = nowRt - _ticks[i].Born;
                if (age > TickLifeSec) { _ticks.RemoveAt(i); continue; }
                float a = Mathf.Clamp01(1f - age / TickLifeSec);
                float x = cx + Mathf.Clamp((float)_ticks[i].Delta * msToPx, -half, half);
                var c = JudgeColor(_ticks[i].J);
                Quad(x - 1.5f, cy - TickH * 0.5f, x + 1.5f, cy + TickH * 0.5f,
                     new Color32(c.r, c.g, c.b, (byte)(220 * a)));
            }

            // EMA 箭頭（偏哪邊 = 整體偏早/偏晚）
            if (_emaPrimed)
            {
                float ax = cx + Mathf.Clamp((float)_ema * msToPx, -half, half);
                float ay = cy + TickH * 0.5f + 3f;
                Quad(ax - 5f, ay, ax + 5f, ay + 2.5f, ColArrow);
                Quad(ax - 3f, ay + 2.5f, ax + 3f, ay + 5f, ColArrow);
                Quad(ax - 1f, ay + 5f, ax + 1f, ay + 7f, ColArrow);
            }

            // 兩端標示：左 = 太早、右 = 太晚（畫成小方塊，IMGUI 那邊有文字說明）
            Quad(cx - half - 12f, cy - 3f, cx - half - 4f, cy + 3f, ColEarly);
            Quad(cx + half + 4f, cy - 3f, cx + half + 12f, cy + 3f, ColLate);
        }

        private void Band(float cx, float cy, float halfPx, Color32 c)
            => Quad(cx - halfPx, cy - BarH * 0.5f, cx + halfPx, cy + BarH * 0.5f, c);

        // design px（左上原點）→ world；四邊形一律 (x0,y0)=左上、(x1,y1)=右下
        private void Quad(float x0, float y0, float x1, float y1, Color32 c)
        {
            int i = _verts.Count;
            _verts.Add(SdoLayout.ToWorld(x0, y0, 0f));
            _verts.Add(SdoLayout.ToWorld(x1, y0, 0f));
            _verts.Add(SdoLayout.ToWorld(x1, y1, 0f));
            _verts.Add(SdoLayout.ToWorld(x0, y1, 0f));
            _cols.Add(c); _cols.Add(c); _cols.Add(c); _cols.Add(c);
            _tris.Add(i); _tris.Add(i + 1); _tris.Add(i + 2);
            _tris.Add(i); _tris.Add(i + 2); _tris.Add(i + 3);
        }
    }
}
