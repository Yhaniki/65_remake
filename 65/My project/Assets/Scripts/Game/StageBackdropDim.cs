using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 舞台**背景**亮度（遊玩畫面；1 = 原樣、0 = 全黑只剩人物）。做法是把背景每一份材質的 <c>_Color</c> tint
    /// 乘暗 —— 專案裡的場景/道具 shader（Sdo/SceneVertex*、Sdo/UnlitInstanced*、Sdo/UnlitOverlay、
    /// Sdo/UnlitAdditiveOverlay…）全都是 <c>tex × _Color</c>，所以乘 tint 就等於乘亮度。
    ///
    /// **為什麼不是疊一層暗幕、也不是拆兩台相機**：飄的彩帶、雪、粒子、火焰這些半透明背景是 ZWrite Off（不寫深度），
    /// 它們能蓋在人物前面純粹是因為 render queue 排在人物之後。一旦把背景與人物拆給兩台相機（背景先畫、人物後畫），
    /// 那些不寫深度的東西就會被後畫的人物蓋掉 —— 原本在人物前面的彩帶會跑到人物後面。改乘材質 tint 之後
    /// **繪製順序、混合、深度全都沒動**，只有顏色變暗，所以前後關係與原本一模一樣。
    ///
    /// 誰算背景＝物件在 <c>StageBackdrop</c> layer 上（ScreenGameplay.BackdropLayer）：SCENE.MSH、mapobj 道具、
    /// 場景的人、燈、招牌、場景火焰/鬼火/常駐 EFT。人物（舞者、手部光條、combo burst、星環）在 SceneLayer，碰都不碰。
    /// </summary>
    public sealed class StageBackdropDim
    {
        // 材質原色要留著，不然反覆乘會越乘越暗（也回不去 1×）。
        private readonly List<Entry> _mats = new List<Entry>();
        private readonly List<EftEffect> _efts = new List<EftEffect>();   // EFT 每幀自己寫材質色 → 只能走它的 Dim 欄位
        private float _applied = 1f;

        private struct Entry
        {
            public Material Mat;
            public Color Base;      // Collect 當下的 _Color
            public bool FadeAlpha;  // 連 alpha 一起淡出（見 ShouldFadeAlpha）
        }

        /// <summary>目前套用的值。</summary>
        public float Current => _applied;

        /// <summary>掃出 <paramref name="layer"/> 上所有背景 renderer 的材質（與 EFT）。場景蓋好之後叫一次；
        /// 之後有延遲生成的背景 EFT 再叫一次即可（重複掃到的會被跳過）。</summary>
        public void Collect(int layer)
        {
#pragma warning disable 0618
            foreach (var r in Object.FindObjectsOfType<Renderer>(includeInactive: true))
#pragma warning restore 0618
            {
                if (r == null || r.gameObject.layer != layer) continue;
                // EFT 的粒子材質每幀被 SetCol 重寫 → 寫進材質沒有用，改記住這顆 EFT，用它的 Dim。
                var eft = r.GetComponentInParent<EftEffect>();
                if (eft != null) { if (!_efts.Contains(eft)) _efts.Add(eft); continue; }
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || !m.HasProperty(ColorId)) continue;
                    if (Contains(m)) continue;
                    _mats.Add(new Entry { Mat = m, Base = m.color, FadeAlpha = ShouldFadeAlpha(m) });
                }
            }
            _applied = 1f;   // 新收進來的材質還沒被乘過 → 讓下一次 Apply 一定寫得下去
        }

        /// <summary>套用亮度（0..1）。值沒變就不做事，所以每幀呼叫是便宜的。</summary>
        public void Apply(float dim)
        {
            dim = Mathf.Clamp01(dim);
            if (Mathf.Approximately(dim, _applied)) return;
            _applied = dim;
            for (int i = 0; i < _mats.Count; i++)
            {
                var e = _mats[i];
                if (e.Mat == null) continue;
                var b = e.Base;
                e.Mat.color = new Color(b.r * dim, b.g * dim, b.b * dim, e.FadeAlpha ? b.a * dim : b.a);
            }
            for (int i = 0; i < _efts.Count; i++) if (_efts[i] != null) _efts[i].Dim = dim;
        }

        /// <summary>這份材質的 alpha 要不要跟著淡出。純函式（<paramref name="shaderName"/> 便於單測）。
        ///
        /// alpha-blend 的背景（彩帶/紗/去背貼片）光把 rgb 乘暗還不夠 —— 全黑時它會變成一塊**黑色半透明**，
        /// 蓋在人物前面就把人物也壓暗了。這類要連 alpha 一起收到 0，才是真的「消失」。
        /// 反過來，cutout（alpha-test）與不透明材質**絕不能**動 alpha：clip 吃的就是 alpha，乘下去邊緣會被啃掉、
        /// 甚至整片被裁光。additive 只看 rgb，rgb 乘到 0 就已經不加光了，alpha 不必動。</summary>
        public static bool ShouldFadeAlpha(string shaderName)
        {
            switch (shaderName)
            {
                case "Sdo/UnlitInstancedAlpha":
                case "Sdo/UnlitInstancedAlphaCullBack":
                case "Sdo/SceneVertexAlpha":
                case "Sdo/UnlitOverlay":
                case "Unlit/Transparent":       // NewMapobjMat 找不到自訂 shader 時的內建退路
                    return true;
                default:
                    return false;               // cutout / opaque / additive
            }
        }

        private static bool ShouldFadeAlpha(Material m) => m.shader != null && ShouldFadeAlpha(m.shader.name);

        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private bool Contains(Material m)
        {
            for (int i = 0; i < _mats.Count; i++) if (_mats[i].Mat == m) return true;
            return false;
        }
    }
}
