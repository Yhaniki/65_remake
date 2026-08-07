using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Sdo.Settings;

namespace Sdo.UI.Util
{
    /// <summary>
    /// 全域畫面亮度（開場設定面板「顯示」分頁的 <c>opt_brightness</c> 滑桿）。做法是在所有東西畫完之後蓋一張
    /// 全螢幕 quad，把畫面**乘上**亮度倍率（<c>Sdo/UiScreenGain</c>）：
    ///   • 亮度 &lt; 1：<c>Blend DstColor Zero</c> → 畫面 × b；
    ///   • 亮度 &gt; 1：<c>Blend DstColor One</c> → 畫面 × (1 + gain)，gain = b−1（LDR 下 gain 上限 1 ⇒ 最亮 ×2）。
    /// 專案是 Linear color space ⇒ 混合在 linear 光量空間做，所以「0.5× ＝ 一半的光」是**精準**的，
    /// 而且暗的地方保持暗 —— 不像加白光那種做法會把純黑抬成灰、整個畫面泛白。
    ///
    /// 因為是 ScreenSpaceOverlay canvas，3D 舞台、場景、UI、對話框**全部一起變**（＝最接近顯示器亮度的行為）。
    /// 疊放順序（sortingOrder）刻意夾在中間：
    ///   <see cref="AspectController"/> 的 pillarbox 黑邊條 = short.MaxValue（最上；黑邊不歸亮度管）
    ///   → <see cref="ScreenTransition"/> 的轉場黑幕 = short.MaxValue−1（**要在亮度之上**，否則黑幕會被乘成半透明）
    ///   → 亮度 = <see cref="SortOrder"/>。
    ///
    /// 值每幀從 <see cref="DisplaySettingsManager"/> 的工作副本讀（不自己存一份），所以設定面板的滑桿一拖就即時看到，
    /// 不必按保存或重開；存檔照舊走 config.ini 的 <c>[Option] opt_brightness</c>。亮度＝1 時整張 quad 關掉（零成本）。
    /// </summary>
    public sealed class BrightnessOverlay : MonoBehaviour
    {
        /// <summary>亮度 overlay 的 shader（<c>Assets/Shaders/UiScreenGain.shader</c>）。**必須列在
        /// <c>BuildScript.RequiredShaders</c>**，否則打包版會被剝掉（見 ShaderInclusionTests）。</summary>
        public const string GainShader = "Sdo/UiScreenGain";

        private const int SortOrder = short.MaxValue - 3;

        private static readonly int GainId = Shader.PropertyToID("_Gain");
        private static readonly int SrcBlendId = Shader.PropertyToID("_SrcBlend");
        private static readonly int DstBlendId = Shader.PropertyToID("_DstBlend");

        private static BrightnessOverlay _inst;
        private Image _quad;
        private Material _mat;      // null＝shader 被剝掉 → 退回黑幕（只做得到變暗，見 Apply）

        /// <summary>建立（或取用既有的）亮度 overlay。<c>FrontendApp.Start</c> 開機時叫一次就好 ——
        /// canvas 是 DontDestroyOnLoad，之後進遊戲/換場景都還在。</summary>
        public static void Ensure()
        {
            if (_inst != null) return;
            var canvas = UIKit.CreateCanvas("BrightnessOverlay", new Vector2(800f, 600f), SortOrder);
            DontDestroyOnLoad(canvas.gameObject);
            _inst = canvas.gameObject.AddComponent<BrightnessOverlay>();
            _inst.Build((RectTransform)canvas.transform);
        }

        private void Build(RectTransform root)
        {
            _quad = UIKit.AddImage(root, "Gain", Color.white, raycast: false);
            UIKit.Stretch(_quad.rectTransform);     // 蓋滿整個視窗（黑邊條在更上層，不會被乘到）
            _quad.enabled = false;

            // ⚠ 這裡刻意寫成**字面字串**（而不是傳 GainShader）：ShaderInclusionTests 是掃 Shader.Find("Sdo/…") 的
            //   字面來確認它有沒有列進 BuildScript.RequiredShaders，傳常數的話那道防剝除的保護就掃不到。
            var sh = Shader.Find("Sdo/UiScreenGain");
            if (sh != null)
            {
                _mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                _mat.SetFloat(SrcBlendId, (float)BlendMode.DstColor);
                _quad.material = _mat;
            }
            else
            {
                // 漏了 RequiredShaders → 至少「變暗」還能用一般 alpha-blend 黑幕做（刻度會偏亮，變亮則做不到）。
                Debug.LogWarning($"[Brightness] shader \"{GainShader}\" not found — 只剩黑幕變暗（刻度會偏亮，也無法調亮）");
            }
            Apply(CurrentBrightness());
        }

        // 每幀跟著工作副本走：設定面板的滑桿直接改那份值，這裡不需要任何通知/註冊。
        private void LateUpdate() => Apply(CurrentBrightness());

        /// <summary>現在設定裡的亮度（夾進合法範圍；設定還沒載入 → 1＝原樣）。</summary>
        public static float CurrentBrightness()
        {
            var d = DisplaySettingsManager.Settings?.display;
            float b = d?.brightness ?? DisplaySettings.DefaultBrightness;
            if (b <= 0f) b = DisplaySettings.DefaultBrightness;
            return Mathf.Clamp(b, DisplaySettings.MinBrightness, DisplaySettings.MaxBrightness);
        }

        private void Apply(float b)
        {
            if (_quad == null) return;
            if (IsIdentity(b)) { _quad.enabled = false; return; }
            _quad.enabled = true;

            if (_mat == null)
            {
                // fallback：只做得到變暗（黑幕）。變亮就當作原樣。
                _quad.enabled = b < 1f;
                _quad.color = new Color(0f, 0f, 0f, Mathf.Clamp01(1f - b));
                return;
            }
            _mat.SetFloat(GainId, GainFor(b));
            _mat.SetFloat(DstBlendId, (float)(IsBoost(b) ? BlendMode.One : BlendMode.Zero));
        }

        /// <summary>要餵給 shader 的 <c>_Gain</c>：變暗＝倍率本身（×b），變亮＝要「多加幾倍」（×(1+gain)）。純函式。</summary>
        public static float GainFor(float brightness) => IsBoost(brightness) ? brightness - 1f : Mathf.Max(0f, brightness);

        /// <summary>這個亮度是要「調亮」嗎（決定 DstBlend = One 還是 Zero）。純函式。</summary>
        public static bool IsBoost(float brightness) => brightness > 1f;

        /// <summary>畫面原樣（1×）→ 整張 quad 不用畫。純函式。</summary>
        public static bool IsIdentity(float brightness) => Mathf.Approximately(brightness, 1f);
    }
}
