using System;
using System.Collections.Generic;
using Sdo.Osu;
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
