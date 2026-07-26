using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// SCN0001 新天地 兩面霓虹招牌的逐字閃爍(「LA MAISON」8 字、「SN❄WFLAKE」9 格)。
    ///
    /// 官方把每個字做成 SCENE.MSH 裡獨立的一個 2 三角形 range,並在執行期另外載一張同名但少一條底線的
    /// DDS;閃爍就是把兩張貼圖在材質槽上「對調」。控制器 Effect_Tick_004a63e0 每幀跑,狀態機是:
    ///   mode 0 → 用全域 PRNG 的 bit1 抽 1 或 2(各 50%)，抽的那一幀就 return
    ///   mode 1「整面閃」 全亮 700ms → 全暗 700ms → 結束(停在全暗)
    ///   mode 2「逐字掃」 全暗起手;每 500ms 亮一個字(表序 0→N-1)，亮滿後再每 500ms 從最後一個往回熄，
    ///                    熄完再多一拍收尾。一輪 = (2N+1) × 500ms
    ///   跑完 → 回 mode 0 重抽。兩面招牌各自獨立抽、獨立計時,所以永遠不同步。
    ///
    /// ★ 亮暗方向很容易做反:直覺會以為帶底線的 `X_.dds` 是熄滅版 —— 實測 17/17 相反,
    ///   **`X_.dds` 才是發光的那張**,而且它同時就是 SCENE.MSH 的材質名(mesh 開場掛的就是它)。
    ///   官方 Entry 建構子把 state 初始化成 1 = 亮,所以「暗」才是被切換出來的例外狀態。
    ///   (L_.DDS 的 alpha 加權亮度 17.66,L.DDS 只有 2.12 —— 差 8 倍。)
    ///
    /// 官方切到「暗」時除了換貼圖,還把渲染狀態從「alpha blend + 關閉 alpha test」改成
    /// 「關閉混色 + alpha test GREATER 160」,所以暗版只剩最亮的核心、外暈整個被裁掉。只換貼圖不換狀態,
    /// 暗版的外暈殘影會糊在招牌上。這裡用兩份 material(亮 = SceneLoader 原本建的;暗 = 複製一份改成
    /// cutout + _Cutoff 160/255)互換來復刻。
    /// </summary>
    public sealed class SceneNeonSign : MonoBehaviour
    {
        /// <summary>官方的兩個模式。</summary>
        public enum Mode { Blink = 1, Wipe = 2 }

        public const float BlinkMs = 700f;   // 0x2bc
        public const float WipeMs = 500f;    // 0x1f4

        private sealed class Sign
        {
            public MeshRenderer Renderer;
            public int[] SubmeshIndex;      // 每個字在 renderer.sharedMaterials 裡的位置(表序)
            public Material[] Lit, Dark;
            public Mode Mode;
            public float T0;
            public int Step = -1;
            public bool Running;
        }

        private readonly List<Sign> _signs = new List<Sign>();
        private System.Random _rng;

        /// <summary>登記一面招牌。<paramref name="submeshIndex"/> 必須照表序(= 招牌上的閱讀順序),
        /// 逐字掃就是照這個順序跑。</summary>
        public void AddSign(MeshRenderer mr, int[] submeshIndex, Material[] lit, Material[] dark)
        {
            if (mr == null || submeshIndex == null || submeshIndex.Length == 0) return;
            _signs.Add(new Sign { Renderer = mr, SubmeshIndex = submeshIndex, Lit = lit, Dark = dark });
        }

        public int SignCount => _signs.Count;

        /// <summary>固定亂數種子 — 只給測試用,讓抽模式可重現。</summary>
        public void SeedForTest(int seed) { _rng = new System.Random(seed); }

        private void Start()
        {
            if (_rng == null) _rng = new System.Random();
            foreach (var s in _signs) StartRound(s);
        }

        private void Update()
        {
            float now = Time.time;
            foreach (var s in _signs)
            {
                if (!s.Running) { StartRound(s); continue; }
                float ms = s.Mode == Mode.Blink ? BlinkMs : WipeMs;
                int step = (int)((now - s.T0) * 1000f / ms);
                if (step == s.Step) continue;
                s.Step = step;
                int n = s.SubmeshIndex.Length;
                if (step >= StepCount(s.Mode, n)) { s.Running = false; continue; }
                Apply(s, step);
            }
        }

        private void StartRound(Sign s)
        {
            // 官方:mode = ((rnd >> 1) & 1) + 1 —— 取的是 bit1,不是 bit0、也不是取模。
            s.Mode = (((_rng.Next() >> 1) & 1) + 1) == 1 ? Mode.Blink : Mode.Wipe;
            s.T0 = Time.time;
            s.Step = 0;
            s.Running = true;
            Apply(s, 0);
        }

        private void Apply(Sign s, int step)
        {
            var mats = s.Renderer.sharedMaterials;
            int n = s.SubmeshIndex.Length;
            for (int i = 0; i < n; i++)
            {
                bool lit = IsLit(s.Mode, step, i, n);
                int idx = s.SubmeshIndex[i];
                if (idx >= 0 && idx < mats.Length) mats[idx] = lit ? s.Lit[i] : s.Dark[i];
            }
            s.Renderer.sharedMaterials = mats;
        }

        /// <summary>一輪有幾拍。Blink:亮一拍、暗一拍。Wipe:亮滿 N 拍 + 熄滅 N 拍 + 收尾 1 拍。</summary>
        public static int StepCount(Mode mode, int n) => mode == Mode.Blink ? 2 : 2 * n + 1;

        /// <summary>第 <paramref name="step"/> 拍時,表序第 <paramref name="i"/> 個字亮不亮(純函式)。
        /// Wipe 的語意逐字照抄官方:phase0 每拍 ++idx(到 N 轉 phase1)、phase1 每拍 --idx(到 0 轉 phase2)、
        /// phase2 收尾;每拍畫的是「前 idx 個亮、其餘暗」。</summary>
        public static bool IsLit(Mode mode, int step, int i, int n)
        {
            if (mode == Mode.Blink) return step <= 0;          // 第 0 拍全亮、第 1 拍全暗
            int idx = step <= n ? step : Mathf.Max(0, 2 * n - step);
            return i < idx;
        }
    }
}
