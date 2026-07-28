using UnityEngine;

namespace Sdo.Game
{
    /// <summary>頭貼相機「跟著頭走」的濾波器 —— 純函式(只吃數字,不碰任何 Unity 物件)→ 可單元測試。
    ///
    /// 為什麼需要:房間頭貼的取景點是**凍結**在模型空間的一個定點(idle 第 0 幀量到的臉框中心),每幀只補 root 骨的
    /// 平移。姿勢本身把頭挪走時相機完全不知情 —— 穿飛行翅膀往前飛(FLY_NV / FLY_NAN 的前傾滑行)身體大幅前傾,頭就
    /// 朝相機衝過來,相機到頭的距離被吃掉 → 頭在框裡爆大(使用者回報)。
    ///
    /// 為什麼不能「每幀直接對準頭」:那正是當初改成凍結的原因 —— 待機/走路的小幅擺動會讓相機每幀跟著抖,看起來像人
    /// 在前後晃。要的是「大位移跟、小晃動不跟」,所以這個濾波器做三件事:
    ///   • 死區 (deadZone):目標離目前追蹤點在死區內 → 完全不動。擺動就在框內自然演出,相機是死的。
    ///   • 超出死區才追,而且只追「超出死區的那一段」(goal = raw − deadZone·dir)→ 進出死區沒有跳變(位置連續),
    ///     而且回正之後追蹤點會停在死區邊緣、不會被拉回原點來回抽動(遲滯)。
    ///   • 指數平滑 (smoothSec):追的過程再抹一層,前傾/回正是滑進滑出而不是彈過去。用 exp(−dt/τ) → 與 frame rate
    ///     無關(60/144 fps 追同一段位移的時間曲線一致)。
    /// </summary>
    public static class HeadPortraitAimFilter
    {
        /// <summary>死區半徑,以「臉高」為單位。0.3 個臉高 ≈ 3 個模型單位(臉高實測 10.45(女)/10.76(男)):
        /// 待機/走路的頭部擺動落在裡面 → 相機完全不動;飛行前傾把頭挪掉十幾個單位 → 遠遠超出 → 相機跟。</summary>
        public const float DefaultDeadZoneFaces = 0.3f;

        /// <summary>死區外快追的指數平滑時間常數(秒)。約 0.2 秒:飛行起飛/落回時鏡頭是滑的,又不會慢到明顯落後。</summary>
        public const float DefaultSmoothSec = 0.2f;

        /// <summary>慢爬的時間常數(秒)。只有死區擋不住的「持續往同一邊偏」才爬得動;來回擺動被它壓到看不見。</summary>
        public const float DefaultCreepSec = 1.5f;

        /// <summary>把追蹤點 <paramref name="current"/> 往目標 <paramref name="raw"/> 推進一幀。</summary>
        /// <param name="deadZone">死區半徑(與座標同單位);≤0 = 不設死區,純平滑跟隨。</param>
        /// <param name="smoothSec">死區外「快追」的指數平滑時間常數(秒);≤0 = 不平滑,直接貼到死區邊緣。</param>
        /// <param name="creepSec">慢爬的時間常數(秒);≤0 = 關掉(追蹤點就永遠落後死區那麼多)。</param>
        /// <param name="dt">這一幀的時間(秒);≤0 → 不推進。</param>
        public static Vector3 Step(Vector3 current, Vector3 raw, float deadZone, float smoothSec, float creepSec, float dt)
        {
            if (!(dt > 0f)) return current;                     // NaN / 暫停 / 同一幀重算 → 不動
            Vector3 err = raw - current;
            float m = err.magnitude;
            if (!(m > 0f)) return current;
            // (1) 快追:只追「超出死區」的部分。死區內 goal == current(完全不動),剛跨出死區時 goal 從 current
            //     連續長出來 → 進出死區沒有跳變。
            Vector3 goal = deadZone > 0f
                ? (m > deadZone ? raw - err * (deadZone / m) : current)
                : raw;
            if (goal != current)
                current += (goal - current) * Mathf.Clamp01(smoothSec > 1e-4f ? 1f - Mathf.Exp(-dt / smoothSec) : 1f);
            // (2) 慢爬:把死區留下的殘餘也吃掉。時間常數長到擺動被壓成幾乎不動(而且來回對稱、正負互相抵消),
            //     但「一直偏同一邊」的持續位移(飛行前傾)會累積 → 距離最後完全補回來,不是永遠落後一個死區。
            if (creepSec > 1e-4f)
                current += (raw - current) * Mathf.Clamp01(1f - Mathf.Exp(-dt / creepSec));
            return current;
        }
    }
}
