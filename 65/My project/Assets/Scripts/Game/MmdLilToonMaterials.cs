using UnityEngine;
using UnityEngine.Rendering;

namespace Sdo.Game
{
    /// <summary>
    /// PMX 材質 → <b>lilToon</b> 材質的翻譯。這是 MMD 身體的第二種著色後端（config.ini <c>[Mmd] mmdLilToon</c>），
    /// 與原本的 <c>Sdo/MmdModel</c> 並存：那一份是 MMD 固定管線的忠實移植（unlit、ramp 直接貼、鉛筆描邊），
    /// 這一份把同一批 PMX 材質餵給 lilToon（MIT，Assets/lilToon），換到「原神那一類」的卡通著色
    /// —— 兩段式 cel 陰影、邊緣光、有光照的描邊。
    ///
    /// <b>能對過去的資料就這些</b>，而且要先講清楚差在哪：原神那種畫面有一半在貼圖裡（每個角色專屬的
    /// ILM/lightmap：高光強度、陰影 ramp 索引、AO、描邊寬度全分通道存著，加上臉部 SDF 陰影圖）。
    /// PMX 沒有那些通道，所以這裡拿得到的只有 base 貼圖、diffuse 顏色、sphere（matcap）、toon ramp、
    /// edge 顏色/寬度。翻譯的策略是：
    ///   • toon ramp 只剩「暗端顏色」有意義 → 取樣它當 lilToon 的 <c>_ShadowColor</c>（保住模型的配色傾向），
    ///     階數改由 lilToon 的 border/blur 決定（那才是 cel 的形狀）。ramp 的中間段丟掉不可惜：MMD 的 ramp
    ///     本來就多半是兩段。
    ///   • sphere map 直接就是 matcap（同一個概念）：乘算 .sph → Multiply，加算 .spa → Add。
    ///   • edge 顏色/寬度 → lilToon 的 outline（它的描邊吃光照、會跟著暗部變色，比原本的純黑硬邊自然）。
    ///
    /// <b>lilToon 吃光照</b>（原本那份是 unlit）。場上沒有平行光時 lilToon 會掉到 <c>_LightMinLimit</c>，
    /// 整隻變暗 —— 所以 <see cref="MmdKeyLight"/> 負責補一顆，而且這裡把 min limit 開得比 lilToon 預設
    /// (0.05) 高很多：舞台上角色會轉一整圈，背光那半圈不能是全黑。
    /// </summary>
    public static class MmdLilToon
    {
        // ---- shader 名稱。lilToon 把「不透明/裁切/透明 × 有無描邊」拆成各自的 shader（描邊是多一個 pass，
        //      不是一個開關），所以選 shader 就等於選了這兩件事。主 shader 只有不透明那一支叫 "lilToon"，
        //      其餘都在 Hidden/ 底下（inspector 不列，Shader.Find 找得到）。
        public const string ShaderOpaque = "lilToon";
        public const string ShaderOpaqueOutline = "Hidden/lilToonOutline";
        public const string ShaderCutout = "Hidden/lilToonCutout";
        public const string ShaderCutoutOutline = "Hidden/lilToonCutoutOutline";
        public const string ShaderTransparent = "Hidden/lilToonTransparent";
        public const string ShaderTransparentOutline = "Hidden/lilToonTransparentOutline";

        // lilToon 的 matcap 混合模式（_MatCapBlendMode）：0 Normal / 1 Add / 2 Screen / 3 Multiply。
        public const int MatCapNormal = 0, MatCapAdd = 1, MatCapScreen = 2, MatCapMultiply = 3;

        /// <summary>PMX 的 sphere mode（1=乘算 .sph，2=加算 .spa）→ lilToon 的 matcap 混合模式。</summary>
        public static int MatCapBlendMode(int pmxSphereMode)
            => pmxSphereMode == 1 ? MatCapMultiply : MatCapAdd;

        /// <summary>
        /// 這個材質要用哪一支 lilToon shader。<paramref name="hasEdge"/> 是「這個 PMX 材質有沒有勾描邊」，
        /// 不是設定裡的描邊開關 —— 開關是把 <c>_OutlineWidth</c> 歸零（見 <see cref="OutlineWidthProperty"/>），
        /// 不換 shader，否則每按一次開關就要重建整批材質。
        /// </summary>
        public static string ShaderNameFor(MmdMaterialRenderMode mode, bool hasEdge)
        {
            switch (mode)
            {
                // 🔴 半透明也走 **Cutout** 那一支,不是 Transparent 那一支 —— 因為 MMD 的半透明是
                // 「alpha 混色 + 寫深度 + 丟掉 alpha=0」(見 MmdMaterialClassifier.Apply),而 lilToon 只有
                // LIL_RENDER==1(Cutout)那一支會 clip;Transparent 那一支不 clip,一旦開 ZWrite,全透明的
                // texel 就會在深度緩衝裡留下看不見的牆。混色/深度/queue 都是材質上的 _SrcBlend/_ZWrite/
                // renderQueue 決定的(見 ApplyRenderMode),所以拿 Cutout 那支 shader 一樣畫得出半透明,
                // 差別只在它多了那個 clip —— 而那正是我們要的。裁切線由 _Cutoff 區分:
                // Cutout=0.5,Blend=BlendClipCutoff(≈0)。
                case MmdMaterialRenderMode.Cutout:
                case MmdMaterialRenderMode.Blend:  return hasEdge ? ShaderCutoutOutline : ShaderCutout;
                default:                           return hasEdge ? ShaderOpaqueOutline : ShaderOpaque;
            }
        }

        // ---- 設定面板那三個開關在 lilToon 這邊改的是哪個屬性（MmdModel 那邊是 _SphereMode/_UseToon/_EdgeSize）。
        public const string SphereProperty = "_UseMatCap";
        public const string ToonProperty = "_UseShadow";
        public const string OutlineWidthProperty = "_OutlineWidth";

        // ---- 描邊寬度。PMX 的 edge size 是 MMD 單位下的粗細（多數模型 0.5~2），lilToon 的 _OutlineWidth
        //      是 0~1 的自家刻度（預設 0.08 已經偏粗）。線性對過去再夾住上限：MMD 有些模型把 edge size 開到
        //      很大是為了在 MMD 自己的相機下看得見，照抄過來會糊成一團黑。
        public const float OutlineWidthPerEdgeSize = 0.02f;
        public const float OutlineWidthMax = 0.12f;
        public const float OutlineWidthMin = 0.004f;

        /// <summary>PMX edge size → lilToon <c>_OutlineWidth</c>。edge size ≤0 = 這個材質不描邊 → 0。</summary>
        public static float OutlineWidth(float pmxEdgeSize)
        {
            if (pmxEdgeSize <= 0f) return 0f;
            return Mathf.Clamp(pmxEdgeSize * OutlineWidthPerEdgeSize, OutlineWidthMin, OutlineWidthMax);
        }

        // ---- 陰影。lilToon 的 cel 是「border（分界位置）+ blur（分界銳利度）+ 兩層陰影顏色」，
        //      不是一張 ramp。這幾個值是「原神那一類」的取向：分界偏亮側、非常銳利、第二層再壓一點。
        public const float ShadowBorder = 0.55f;
        public const float ShadowBlur = 0.06f;
        public const float Shadow2ndBorder = 0.28f;
        public const float Shadow2ndBlur = 0.08f;
        /// <summary>沒有 toon ramp 可取樣時的陰影底色（偏冷的淡紫灰，lilToon 自己的預設也是這個取向）。</summary>
        public static readonly Color DefaultShadowColor = new Color(0.78f, 0.72f, 0.82f, 1f);

        /// <summary>
        /// 從 MMD 的 toon ramp 取「暗端」當陰影色。ramp 是一張直的漸層（V=0 暗 → V=1 亮），所以取樣底部那幾列。
        /// 讀不到（不可讀的貼圖 / 沒有 ramp）就回 <see cref="DefaultShadowColor"/>。
        ///
        /// 取「一小段的平均」而不是單一個 texel：不少 ramp 最底下那一列是純黑的收邊，直接拿會把整個暗部壓死。
        /// </summary>
        public static Color ShadowColorFromRamp(Texture2D ramp)
        {
            if (ramp == null) return DefaultShadowColor;
            Color32[] px;
            try { px = ramp.GetPixels32(); }
            catch { return DefaultShadowColor; }   // 不可讀（壓縮格式 / readable 沒開）
            if (px == null || px.Length == 0) return DefaultShadowColor;

            int w = Mathf.Max(1, ramp.width), h = Mathf.Max(1, ramp.height);
            // GetPixels32 是 V 向上（第 0 列 = 最底 = 暗端）。取最底 1/8（至少一列）的平均。
            int rows = Mathf.Max(1, h / 8);
            double r = 0, g = 0, b = 0; int n = 0;
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (i >= px.Length) break;
                    r += px[i].r; g += px[i].g; b += px[i].b; n++;
                }
            if (n == 0) return DefaultShadowColor;
            var c = new Color((float)(r / n / 255.0), (float)(g / n / 255.0), (float)(b / n / 255.0), 1f);
            // 全黑的 ramp 底（有些模型就是這樣畫的）會讓暗部整片死黑 → 提到一個還看得見材質的下限。
            return Brighten(c, 0.35f);
        }

        /// <summary>把顏色的最大分量拉到至少 <paramref name="floorValue"/>（保持色相）。</summary>
        public static Color Brighten(Color c, float floorValue)
        {
            float m = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            if (m >= floorValue) return c;
            if (m <= 0.0001f) return new Color(floorValue, floorValue, floorValue, c.a);
            float k = floorValue / m;
            return new Color(c.r * k, c.g * k, c.b * k, c.a);
        }

        // ---- 光照。lilToon 沒有平行光時亮度會掉到 _LightMinLimit；舞台上角色轉一整圈，背光那半圈不能全黑，
        //      所以 min 開得比 lilToon 預設(0.05)高很多。max=1 是不讓場景燈把角色打爆。
        public const float LightMinLimit = 0.4f;
        public const float LightMaxLimit = 1f;

        /// <summary>
        /// 把一個 PMX 材質的資料寫進一份已經是 lilToon shader 的材質。
        /// <paramref name="renderMode"/> 決定混色/深度狀態，<paramref name="sphereMode"/> 是 PMX 的 sphere 模式
        /// （0=無），<paramref name="edgeSize"/> ≤0 = 這個材質不描邊。
        /// </summary>
        public static void Configure(
            Material mat,
            Texture baseTex,
            Color diffuse,
            bool doubleSided,
            MmdMaterialRenderMode renderMode,
            Texture sphereTex,
            int sphereMode,
            Texture2D toonRamp,
            Color edgeColor,
            float edgeSize)
        {
            if (mat == null) return;

            mat.SetTexture("_MainTex", baseTex);
            mat.SetColor("_Color", diffuse);
            mat.SetFloat("_Cull", doubleSided ? 0f : 2f);       // Off : Back（同 UnityEngine.Rendering.CullMode）
            mat.SetFloat("_Cutoff", 0.5f);
            ApplyRenderMode(mat, renderMode);

            // 光照下限/上限：見 LightMinLimit。
            mat.SetFloat("_LightMinLimit", LightMinLimit);
            mat.SetFloat("_LightMaxLimit", LightMaxLimit);
            mat.SetFloat("_AsUnlit", 0f);

            // cel 陰影
            mat.SetFloat("_UseShadow", 1f);
            var shadow = ShadowColorFromRamp(toonRamp);
            mat.SetColor("_ShadowColor", shadow);
            mat.SetColor("_Shadow2ndColor", Color.Lerp(shadow, Color.black, 0.22f));
            mat.SetFloat("_ShadowBorder", ShadowBorder);
            mat.SetFloat("_ShadowBlur", ShadowBlur);
            mat.SetFloat("_Shadow2ndBorder", Shadow2ndBorder);
            mat.SetFloat("_Shadow2ndBlur", Shadow2ndBlur);
            mat.SetFloat("_ShadowStrength", 1f);

            // 邊緣光（原神那類最好認的一味）。lilToon 預設值本來就接近，只把它打開並讓它在暗部收掉。
            mat.SetFloat("_UseRim", 1f);
            mat.SetColor("_RimColor", new Color(1f, 0.97f, 0.92f, 0.55f));
            mat.SetFloat("_RimBorder", 0.62f);
            mat.SetFloat("_RimBlur", 0.35f);
            mat.SetFloat("_RimFresnelPower", 4.5f);
            mat.SetFloat("_RimShadowMask", 1f);

            // sphere map → matcap
            bool hasSphere = sphereTex != null && (sphereMode == 1 || sphereMode == 2);
            mat.SetFloat("_UseMatCap", hasSphere ? 1f : 0f);
            if (hasSphere)
            {
                mat.SetTexture("_MatCapTex", sphereTex);
                mat.SetFloat("_MatCapBlendMode", MatCapBlendMode(sphereMode));
                mat.SetFloat("_MatCapBlend", 1f);
                // MMD 的 sphere 是「照相機空間法線直接查表」，不跟著頭轉 → 關掉 lilToon 的 Z 旋轉抵銷才對得上。
                mat.SetFloat("_MatCapZRotCancel", 0f);
            }

            // 描邊
            mat.SetColor("_OutlineColor", edgeColor);
            mat.SetFloat(OutlineWidthProperty, OutlineWidth(edgeSize));
            mat.SetFloat("_OutlineFixWidth", 1f);        // 螢幕固定寬：模型被縮放到 SDO 身高，object-space 寬度會跟著跑
            mat.SetFloat("_OutlineEnableLighting", 1f);  // 描邊吃光照 → 暗部的線跟著沉下去（純黑硬邊是 MMD 那一份的事）
            mat.SetFloat("_OutlineZWrite", renderMode == MmdMaterialRenderMode.Blend ? 0f : 1f);
        }

        /// <summary>混色/深度狀態。lilToon 的透明變體同樣靠材質上的 _SrcBlend/_ZWrite 決定，所以要寫進去。</summary>
        public static void ApplyRenderMode(Material mat, MmdMaterialRenderMode mode)
        {
            if (mat == null) return;
            switch (mode)
            {
                case MmdMaterialRenderMode.Cutout:
                    mat.SetOverrideTag("RenderType", "TransparentCutout");
                    mat.SetFloat("_SrcBlend", (float)BlendMode.One);
                    mat.SetFloat("_DstBlend", (float)BlendMode.Zero);
                    mat.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
                    mat.SetFloat("_DstBlendAlpha", (float)BlendMode.Zero);
                    mat.SetFloat("_ZWrite", 1f);
                    mat.renderQueue = (int)RenderQueue.AlphaTest;
                    break;

                case MmdMaterialRenderMode.Blend:
                    // 半透明也寫深度 + 只丟 alpha=0 —— 理由與 MmdMaterialClassifier.Apply 那份一字不差
                    // （整具身體是一個 SkinnedMeshRenderer，不寫深度就變成「材質順序決定誰蓋誰」）。
                    mat.SetOverrideTag("RenderType", "Transparent");
                    mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                    mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                    mat.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
                    mat.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
                    mat.SetFloat("_ZWrite", 1f);
                    mat.SetFloat("_Cutoff", MmdMaterialClassifier.BlendClipCutoff);
                    mat.renderQueue = (int)RenderQueue.Transparent;
                    break;

                default:
                    mat.SetOverrideTag("RenderType", "Opaque");
                    mat.SetFloat("_SrcBlend", (float)BlendMode.One);
                    mat.SetFloat("_DstBlend", (float)BlendMode.Zero);
                    mat.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
                    mat.SetFloat("_DstBlendAlpha", (float)BlendMode.Zero);
                    mat.SetFloat("_ZWrite", 1f);
                    mat.renderQueue = (int)RenderQueue.Geometry;
                    break;
            }
        }
    }

    /// <summary>
    /// lilToon 後端需要一顆平行光才有明暗（原本的 <c>Sdo/MmdModel</c> 是 unlit，所以整個專案本來一顆燈都沒有）。
    /// 這裡補一顆常駐的：方向固定在世界空間（跟著相機的話 cel 陰影會永遠貼在同一個位置，看起來就沒有立體感），
    /// 強度壓在 1 以下，暗面靠 <see cref="MmdLilToon.LightMinLimit"/> 撐住。
    ///
    /// 對現有畫面是零影響 —— 專案裡其它東西全是 unlit shader，不吃光。
    /// </summary>
    public static class MmdKeyLight
    {
        private static Light _light;

        /// <summary>從角色正面偏左上打下來（MMD/原神慣用的 key light 位置）。</summary>
        public static readonly Vector3 EulerAngles = new Vector3(38f, -152f, 0f);
        public const float Intensity = 0.85f;

        /// <summary>場上還沒有這顆燈就建一顆。已經有就什麼都不做。</summary>
        public static void Ensure()
        {
            if (_light != null) return;
            var go = new GameObject("MmdKeyLight");
            Object.DontDestroyOnLoad(go);
            go.transform.eulerAngles = EulerAngles;
            _light = go.AddComponent<Light>();
            _light.type = LightType.Directional;
            _light.color = new Color(1f, 0.98f, 0.94f, 1f);
            _light.intensity = Intensity;
            _light.shadows = LightShadows.None;   // 影子由 lilToon 的 cel 陰影負責，不需要真的陰影貼圖
        }
    }
}
