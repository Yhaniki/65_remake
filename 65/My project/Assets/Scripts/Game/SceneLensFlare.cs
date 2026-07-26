using System.IO;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// SCN0004 海灘 太陽的鏡頭光斑鏈(LensFlare，官方 ctor 0x418730 / 可見性 0x418880 / 繪製 0x418990)。
    /// Ghidra 完全沒有把後兩支反編譯出來，常數與流程是逐指令反組譯的。
    ///
    /// 官方流程(逐項照抄):
    ///   壽命    建立時記下時間戳(+0xac)、上限 +0xa8 = 10000 ms。**全 exe 只寫過那一次時間戳**，
    ///           所以太陽光斑只在進場後 10 秒內存在，之後永遠不再畫 —— 這是原版行為，不是漏看。
    ///   夾角    v1 = normalize(sun − eye)、v2 = normalize(target − eye)；ang = acos(dot)×180/π
    ///           可見 ⇔ ang > 0 且 ang < 40。(嚴格大於 0：太陽正對畫面正中央時反而不畫，原版怪癖。)
    ///   投影    sx = (ndc.x + 1) × 400、sy = 300 − ndc.y × 300   ← 寫死 800×600 的半寬高
    ///           出界(sx 或 sy 超出 backbuffer 寬/高，且是**無號**比較所以負數也被擋)即整組不畫。
    ///   強度    dx = sx − cx；dy = (sy − cy)/cy × cx；r = √(dx²+dy²)/(cx×√2)；I = (1.25 − r)×50
    ///           ≤0 不畫、>1 夾成 1。★ 因為上一步已保證落在畫面內，r ≤ 1 → I ≥ 12.5 → **恆為 1**。
    ///           三個常數在實機上是死碼；照抄但別期待它會做出「離中心越遠越暗」的漸變(原版沒有)。
    ///   展開    k = 1；若 dist &lt; w/8 則 k = 2 − dist/(w/8)。太陽越靠近畫面正中央，鬼影拉得越開。
    ///   位置    pos_i = sun + (screenCentre − sun) × (k × t_i × 0.8)
    ///   四頂點  pos ± size(**size 是半邊長**，螢幕上實際邊長 = 2×size，固定像素、不隨距離縮放)
    ///   取樣    u 恆 0..1(整張寬)、v = V_i .. V_i + 0.125 → atlas 是 8 列(128×1024)，只有前 5 列有內容
    ///   混色    ONE/ONE 純加法，diffuse 的 alpha 不進顏色(見 Sdo/LensFlare)
    ///
    /// 畫在場景合成 quad(SceneBackdrop，z=90，±400×±300)的前面(z=89)，相機在 z=−100 —— 等同官方
    /// 「3D 之後、2D HUD 之前」的位置(HUD 在另一台相機，之後才畫)。
    /// </summary>
    public sealed class SceneLensFlare : MonoBehaviour
    {
        /// <summary>一顆光斑。v = atlas 的列起點；t = 沿「太陽→畫面中心」軸的位置；size = 半邊長。</summary>
        public readonly struct Element
        {
            public readonly float V, T, Size;
            public readonly Color32 Color;
            public Element(float v, uint argb, float t, float size)
            { V = v; T = t; Size = size; Color = new Color32((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24)); }
        }

        /// <summary>官方元素表 —— VA 0x00542c98 起、stride 0x10、**19 筆**(不是 18)。
        /// 欄位順序是 {v(float), ARGB(u32), t(float), size(float)}，v 在最前面 —— 讀錯順序前 17 筆看起來
        /// 也「合理」，直到最後一筆撞上表尾常數 50.0/1.25 才會露餡。三個獨立證據交叉確認 19 筆:
        /// 配置引數 count=0x13、頂點迴圈 cmp ebx,0x130(=19×0x10)、DrawPrimitive 迴圈 cmp esi,0x4c(=19×4)。</summary>
        public static readonly Element[] Elements =
        {
            new Element(0.375f, 0x80ffcf6fu, -0.25f,   8.0f),
            new Element(0.0f,   0xc0ffa0c0u,  0.00f, 120.0f),
            new Element(0.125f, 0xa0f06080u,  0.00f,  40.0f),
            new Element(0.25f,  0xa0e03040u,  0.00f, 100.0f),
            new Element(0.375f, 0x68f0a090u,  0.27f,  11.5f),
            new Element(0.5f,   0x781030d0u,  0.45f,  31.5f),
            new Element(0.5f,   0x704060e0u,  0.60f,   7.5f),
            new Element(0.5f,   0x8070a0d0u,  0.75f,  15.5f),
            new Element(0.25f,  0x60f04040u,  0.85f,   4.5f),
            new Element(0.0f,   0x80f0c0f0u,  1.05f,   6.5f),
            new Element(0.375f, 0x60f0c0f0u,  1.30f,   6.5f),
            new Element(0.5f,   0x60f0c0f0u,  1.40f,  11.5f),
            new Element(0.0f,   0x60706000u,  1.45f,   9.5f),
            new Element(0.5f,   0x6070a0f0u,  1.70f,  21.5f),
            new Element(0.375f, 0x6080c0f0u,  1.77f,  44.5f),
            new Element(0.25f,  0x68106000u,  1.81f,  13.5f),
            new Element(0.375f, 0x70e06000u,  2.10f,  33.5f),
            new Element(0.125f, 0x80f06030u,  2.40f, 130.5f),
            new Element(0.25f,  0x8070a0f0u,  2.80f,  19.5f),
        };

        public const float DesignW = 800f, DesignH = 600f;   // 官方寫死的 800×600
        public const float RowV = 0.125f;                     // atlas 8 列
        public const float AxisScale = 0.8f;                  // [0x542c94]
        public const float MaxAngleDeg = 40f;                 // [0x542c84]
        public const float LifetimeMs = 10000f;               // [+0xa8]
        /// <summary>官方太陽世界座標 (33, 175, −3)。</summary>
        public static readonly Vector3 SunPos = new Vector3(33f, 175f, -3f);

        private Camera _stageCam;
        private Mesh _mesh;
        private Vector3[] _verts;
        private Color32[] _cols;
        private MeshRenderer _mr;
        private float _bornTime;
        private bool _expired;

        /// <summary>顯示壽命(秒)。官方是 10 秒後永遠消失;設 &lt;= 0 代表常駐(非官方)。</summary>
        public float LifetimeSec = LifetimeMs / 1000f;

        public void Init(Camera stageCam, Texture atlas, int layer)
        {
            _stageCam = stageCam;
            _bornTime = Time.time;
            gameObject.layer = layer;

            int n = Elements.Length;
            _verts = new Vector3[n * 4];
            _cols = new Color32[n * 4];
            var uv = new Vector2[n * 4];
            var tris = new int[n * 6];
            for (int i = 0; i < n; i++)
            {
                // ★ V 軸要翻。官方是 D3D9 慣例:V=0 在**影像頂端**、V 增加往下走;Unity 的 V=0 在貼圖
                // **底端**、V 增加往上走。直接照抄 V 會把元素對到鏡射後的列 —— 而 LENSFLARE.BMP 的內容
                // 只在「由上往下」的第 0..4 列(第 5..7 列整片全黑)，所以照抄的結果是好幾顆光斑取到全黑、
                // 完全不出現，其餘的也取到錯的圖形(官方的亮環變成我們的柔和實心盤)。
                // quad 上緣取該列的上緣 → Unity V = 1 − V;下緣 → 1 − (V + 0.125)。
                float v0 = 1f - Elements[i].V, v1 = 1f - (Elements[i].V + RowV);
                uv[i * 4 + 0] = new Vector2(0f, v0); uv[i * 4 + 1] = new Vector2(1f, v0);   // 上排
                uv[i * 4 + 2] = new Vector2(0f, v1); uv[i * 4 + 3] = new Vector2(1f, v1);   // 下排
                int b = i * 4, o = i * 6;
                tris[o] = b; tris[o + 1] = b + 2; tris[o + 2] = b + 1;
                tris[o + 3] = b + 1; tris[o + 4] = b + 2; tris[o + 5] = b + 3;
            }
            _mesh = new Mesh { name = "LensFlare" };
            _mesh.MarkDynamic();
            _mesh.vertices = _verts; _mesh.uv = uv; _mesh.colors32 = _cols; _mesh.triangles = tris;
            _mesh.bounds = new Bounds(Vector3.zero, new Vector3(DesignW * 4f, DesignH * 4f, 1f));
            gameObject.AddComponent<MeshFilter>().mesh = _mesh;
            var sh = Shader.Find("Sdo/LensFlare");
            _mr = gameObject.AddComponent<MeshRenderer>();
            _mr.sharedMaterial = new Material(sh != null ? sh : Shader.Find("Unlit/Texture")) { mainTexture = atlas };
            _mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _mr.receiveShadows = false;
            _mr.enabled = false;
        }

        /// <summary>診斷用:每 N 秒印一次可見性判定的每一步(為什麼沒畫出來)。0 = 不印。</summary>
        public float DiagEverySec = 0f;
        private float _lastDiag = -999f;

        private void Diag()
        {
            if (DiagEverySec <= 0f || Time.time - _lastDiag < DiagEverySec) return;
            _lastDiag = Time.time;
            var cam = _stageCam;
            if (cam == null) { Debug.Log("[flare.diag] 沒有相機"); return; }
            float age = Time.time - _bornTime;
            float ang = AngleDeg(cam.transform.position, cam.transform.forward, SunPos);
            var ndc4 = cam.projectionMatrix * cam.worldToCameraMatrix * new Vector4(SunPos.x, SunPos.y, SunPos.z, 1f);
            string scr = ndc4.w > 0f ? ToScreen(new Vector2(ndc4.x / ndc4.w, ndc4.y / ndc4.w)).ToString("F1") : "(相機背後)";
            Debug.Log($"[flare.diag] age={age:F1}s/{LifetimeSec}s  eye={cam.transform.position.ToString("F0")} " +
                      $"fwd={cam.transform.forward.ToString("F2")}  ang={ang:F1}° (需 0<ang<40)  w={ndc4.w:F1}  screen={scr}  " +
                      $"drawn={(_mr != null && _mr.enabled)}");
        }

        private void LateUpdate()
        {
            Diag();
            if (_mr == null) return;
            if (LifetimeSec > 0f && Time.time - _bornTime > LifetimeSec)
            {
                if (!_expired) { _expired = true; _mr.enabled = false; }
                return;   // 官方:壽命一到就永遠不再畫
            }
            _mr.enabled = TryBuild();
        }

        private bool TryBuild()
        {
            var cam = _stageCam;
            if (cam == null || !cam.isActiveAndEnabled) return false;
            if (!IsWithinAngle(cam.transform.position, cam.transform.forward, SunPos)) return false;

            var ndc = cam.projectionMatrix * cam.worldToCameraMatrix * new Vector4(SunPos.x, SunPos.y, SunPos.z, 1f);
            if (ndc.w <= 0f) return false;                       // 在相機背後
            var sun = ToScreen(new Vector2(ndc.x / ndc.w, ndc.y / ndc.w));
            if (sun.x < 0f || sun.x > DesignW || sun.y < 0f || sun.y > DesignH) return false;   // 官方的出界判定

            float cx = DesignW * 0.5f, cy = DesignH * 0.5f;
            float dist = AxisDistance(sun, cx, cy);
            if (Intensity(dist, cx) <= 0f) return false;
            float k = SpreadK(dist, DesignW);

            for (int i = 0; i < Elements.Length; i++)
            {
                var e = Elements[i];
                Vector2 p = ElementScreenPos(sun, new Vector2(cx, cy), k, e.T);
                // 螢幕座標(y 向下) → SceneBackdrop 的 quad 空間(±400 × ±300，y 向上)，z=89 疊在場景之前
                float x0 = p.x - e.Size - cx, x1 = p.x + e.Size - cx;
                float y0 = cy - (p.y - e.Size), y1 = cy - (p.y + e.Size);
                int b = i * 4;
                _verts[b + 0] = new Vector3(x0, y0, 89f);
                _verts[b + 1] = new Vector3(x1, y0, 89f);
                _verts[b + 2] = new Vector3(x0, y1, 89f);
                _verts[b + 3] = new Vector3(x1, y1, 89f);
                for (int c = 0; c < 4; c++) _cols[b + c] = e.Color;
            }
            _mesh.vertices = _verts;
            _mesh.colors32 = _cols;
            return true;
        }

        // ── 以下是純函式，給測試直接驗官方公式 ───────────────────────────────────────────

        /// <summary>NDC → 官方螢幕座標(寫死 800×600 的半寬高，y 向下)。</summary>
        public static Vector2 ToScreen(Vector2 ndc)
            => new Vector2((ndc.x + 1f) * (DesignW * 0.5f), DesignH * 0.5f - ndc.y * (DesignH * 0.5f));

        /// <summary>相機朝向與「相機→太陽」的夾角(度)，官方可見條件是 0 &lt; ang &lt; 40(兩端皆不含)。</summary>
        public static float AngleDeg(Vector3 eye, Vector3 forward, Vector3 sun)
        {
            var v1 = (sun - eye).normalized;
            var v2 = forward.normalized;
            return Mathf.Acos(Mathf.Clamp(Vector3.Dot(v1, v2), -1f, 1f)) * Mathf.Rad2Deg;
        }

        public static bool IsWithinAngle(Vector3 eye, Vector3 forward, Vector3 sun)
        {
            float a = AngleDeg(eye, forward, sun);
            return a > 0f && a < MaxAngleDeg;
        }

        /// <summary>官方的「等比修正距離」：縱向先換算到橫向尺度再取長度。</summary>
        public static float AxisDistance(Vector2 sun, float cx, float cy)
        {
            float dx = sun.x - cx;
            float dy = (sun.y - cy) / cy * cx;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>I = (1.25 − dist/(cx·√2)) × 50，≤0 不畫、&gt;1 夾成 1。
        /// 因為出界判定已保證 dist ≤ cx·√2，實機上恆為 1 —— 照抄，但別指望它做出漸變。</summary>
        public static float Intensity(float dist, float cx)
        {
            float r = dist / (cx * 1.4142135381698608f);
            float i = (1.25f - r) * 50f;
            return i <= 0f ? 0f : Mathf.Min(1f, i);
        }

        /// <summary>鬼影展開係數：太陽離畫面中心 &lt; w/8 時才 &gt; 1(最大 2)。w/8 是整數截斷。</summary>
        public static float SpreadK(float dist, float w)
        {
            float q = dist / (int)(w / 8f);
            return q < 1f ? 2f - q : 1f;
        }

        /// <summary>pos = sun + (centre − sun) × (k × t × 0.8)。</summary>
        public static Vector2 ElementScreenPos(Vector2 sun, Vector2 centre, float k, float t)
        {
            float f = k * t * AxisScale;
            return new Vector2(sun.x + (centre.x - sun.x) * f, sun.y + (centre.y - sun.y) * f);
        }

        /// <summary>載入官方的 lensflare.bmp(128×1024、24bpp 未壓縮)。
        /// ★ 不能用 Texture2D.LoadImage —— 它只吃 PNG/JPG，餵 BMP 會靜默失敗回 false。自己解:
        /// BITMAPFILEHEADER(14) + BITMAPINFOHEADER(40)，pixel data 由 bfOffBits 指到,BGR 順序、
        /// 每列 4-byte 對齊、且 BMP 是 **bottom-up** 存放(biHeight 為正時第一列是影像最下面一列),
        /// 所以要翻正 —— 翻錯的話 v 分列會整個上下顛倒,19 顆光斑全部取到錯的 atlas 列。</summary>
        public static Texture2D LoadAtlas(string sceneDir)
        {
            var p = Path.Combine(sceneDir, "LENSFLARE.BMP");
            if (!File.Exists(p)) { Debug.LogWarning("[flare] 檔案不存在: " + p); return null; }
            var tex = DecodeBmp24(File.ReadAllBytes(p));
            if (tex == null) { Debug.LogWarning("[flare] BMP 解碼失敗(不是 24bpp 未壓縮?): " + p); return null; }
            tex.name = "LENSFLARE";
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            return tex;
        }

        /// <summary>24bpp / 32bpp 未壓縮 BMP → Texture2D(已翻正)。純函式，測試可直接餵合成 buffer。</summary>
        public static Texture2D DecodeBmp24(byte[] d)
        {
            if (d == null || d.Length < 54 || d[0] != 'B' || d[1] != 'M') return null;
            int off = System.BitConverter.ToInt32(d, 10);
            int hdr = System.BitConverter.ToInt32(d, 14);
            if (hdr < 40) return null;
            int w = System.BitConverter.ToInt32(d, 18);
            int h = System.BitConverter.ToInt32(d, 22);
            int bpp = System.BitConverter.ToInt16(d, 28);
            int compression = System.BitConverter.ToInt32(d, 30);
            if (w <= 0 || bpp != 24 && bpp != 32 || compression != 0) return null;
            bool bottomUp = h > 0;                 // biHeight 為負 = top-down
            h = Mathf.Abs(h);
            if (w > 4096 || h > 4096) return null;
            int bytesPP = bpp / 8;
            int stride = (w * bytesPP + 3) & ~3;   // 每列 4-byte 對齊
            if (off < 0 || (long)off + (long)stride * h > d.Length) return null;

            var px = new Color32[w * h];
            for (int row = 0; row < h; row++)
            {
                int src = off + row * stride;
                // Unity 的 Color32[] 與 bottom-up BMP 一樣,index 0 那一列都在影像最下面 —— 所以正的
                // biHeight 直接一對一;負的 biHeight(top-down)才要翻。翻錯的話 19 顆光斑會整組取到
                // 錯的 atlas 列。
                int dstRow = bottomUp ? row : (h - 1 - row);
                for (int x = 0; x < w; x++)
                {
                    int s = src + x * bytesPP;
                    px[dstRow * w + x] = new Color32(d[s + 2], d[s + 1], d[s], 255);   // BGR → RGB
                }
            }
            // ★ linear: true —— 專案跑在 Linear 色彩空間(m_ActiveColorSpace = 1),而官方是 D3D9、
            // gamma-unaware:它直接把位元組值相加。若把這張圖當成 sRGB,取樣時會先做 sRGB→linear
            // 轉換(200/255 → 0.57),疊在明亮的天空上就明顯比官方暗 —— 這正是「unity 做的沒有發光」。
            // 標成 linear 資料後,取樣拿到的就是原始值/255,加法的數值行為貼近官方。
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            tex.SetPixels32(px);
            tex.Apply(false, false);
            return tex;
        }
    }
}
