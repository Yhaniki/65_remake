using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 幀時間統計 —— 每隔一段時間印一行「平均/最差」。
    ///
    /// 這不是暫時的除錯碼:多舞者(M8)的驗收依據就是這幾個數字,而且之後每次動到蒙皮/材質/角色數
    /// 都應該再量一次。所以做成一個可以重複用的小東西(打歌畫面與房間各一份),而不是各自抄一段。
    ///
    /// 🔴 用 <see cref="Time.unscaledDeltaTime"/>。遊戲流速(F5/F6 改 <c>Time.timeScale</c>)會讓
    /// scaled 的 deltaTime 變小 —— 拿它來量會把「歌放慢了」讀成「效能變好了」。
    ///
    /// 「最差幀」比平均重要:舞蹈是節奏遊戲,單獨一個 60ms 的尖峰就是一次看得見的頓,
    /// 而它會被平均值完全藏起來。
    /// </summary>
    public sealed class FrameStats
    {
        private readonly string _tag;
        private readonly float _periodSec;
        private float _accum;
        private int _frames;
        private float _worst;
        private float _nextLogRt = -1f;

        public FrameStats(string tag, float periodSec = 2f)
        {
            _tag = tag;
            _periodSec = periodSec > 0.1f ? periodSec : 2f;
            Uncap();
        }

        /// <summary>
        /// 量測期間解掉垂直同步與幀率上限。
        ///
        /// 🔴 不解掉的話**量不到任何東西**:vsync 會把每一幀都補到 16.68ms,不管實際算了多久 ——
        /// 第一次量測就是這樣,1 隻與 16 隻角色都是「平均 16.68ms / 60 FPS」,完全看不出差別。
        /// 那個數字唯一的資訊量是「有沒有掉幀」(有掉的話平均會 &gt; 16.68);要知道**還剩多少餘裕**
        /// 就必須讓它跑滿。
        ///
        /// 只在量測模式(有設 SDO_DANCERS / SDO_ROOMAVATARS)才會走到這裡,所以不影響正常遊玩。
        /// </summary>
        private static void Uncap()
        {
            if (_uncapped) return;
            _uncapped = true;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            Debug.Log("[perf] 量測模式:已關閉 vSync 與幀率上限(否則每幀都被補到 16.68ms,量不出成本)");
        }

        private static bool _uncapped;

        /// <summary>每幀呼叫。<paramref name="subjects"/> = 這一刻場上有幾隻角色(印出來當對照)。</summary>
        public void Tick(int subjects)
        {
            float dt = Time.unscaledDeltaTime;
            _accum += dt;
            _frames++;
            if (dt > _worst) _worst = dt;

            float rt = Time.realtimeSinceStartup;
            if (_nextLogRt < 0f) { _nextLogRt = rt + _periodSec; return; }   // 第一個週期不印(還在載入/暖機)
            if (rt < _nextLogRt || _frames <= 0) return;

            float avgMs = _accum / _frames * 1000f;
            Debug.Log(string.Format("[perf] {0} 角色={1} 幀數={2} 平均={3:F2}ms ({4:F0} FPS) 最差={5:F2}ms ({6:F0} FPS)",
                                    _tag, subjects, _frames, avgMs,
                                    avgMs > 0.001f ? 1000f / avgMs : 0f,
                                    _worst * 1000f, _worst > 0.000001f ? 1f / _worst : 0f));
            _accum = 0f; _frames = 0; _worst = 0f;
            _nextLogRt = rt + _periodSec;
        }
    }
}
