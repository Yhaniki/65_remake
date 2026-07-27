using NUnit.Framework;
using Sdo.Game;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>
    /// 房間頭貼相機的「跟頭」濾波。守的是使用者回報的那組互相拉扯的需求:
    ///   「飛行朝前飛,身體會大幅前傾 → 頭很貼近相機變得很大 → 相機應該跟頭維持一定距離」
    ///   「但是要有一點 filter,身體小幅晃動的時候不應該一直跟著晃」
    /// 所以不變量就兩條:**小晃動時追蹤點一格都不能動**,**大位移時距離要追回來**;外加一條讓兩者能共存的
    /// 「大位移上疊小晃動時,追蹤點會停下來(不會被晃動一路帶著抖)」。
    ///
    /// 座標是模型單位(臉高實測 10.45(女)/10.76(男)),死區 0.3 個臉高 ≈ 3.14。
    /// </summary>
    public class HeadPortraitAimFilterTests
    {
        private const float FaceH = 10.45f;
        private static float DeadZone => HeadPortraitAimFilter.DefaultDeadZoneFaces * FaceH;   // ≈ 3.14
        private const float Smooth = HeadPortraitAimFilter.DefaultSmoothSec;
        private const float Dt = 1f / 60f;

        /// <summary>FLY_* 滑翔的前傾把頭相對 root 挪走的量級(往前 -Z、往下 -Y)。</summary>
        private static readonly Vector3 Lean = new Vector3(0f, -4f, -16f);

        /// <summary>待機/走路的頭部擺動(遠小於死區)。</summary>
        private static Vector3 Sway(int frame)
        {
            float p = frame * Dt * 2f * Mathf.PI * 0.8f;   // 0.8 Hz
            return new Vector3(Mathf.Sin(p) * 1.0f, Mathf.Sin(p * 2f) * 0.6f, Mathf.Cos(p) * 1.5f);
        }

        private static Vector3 Run(Vector3 start, System.Func<int, Vector3> raw, int frames,
                                   float deadZone = -1f, float smooth = Smooth, float dt = Dt, float creep = 0f)
        {
            if (deadZone < 0f) deadZone = DeadZone;
            var cur = start;
            for (int i = 0; i < frames; i++) cur = HeadPortraitAimFilter.Step(cur, raw(i), deadZone, smooth, creep, dt);
            return cur;
        }

        [Test]
        public void SmallSway_NeverMovesTheCamera()   // ← 「身體小幅晃動不應該一直跟著晃」
        {
            var end = Run(Vector3.zero, Sway, 600);   // 10 秒
            Assert.AreEqual(Vector3.zero, end, "死區內的擺動讓相機動了 —— 頭貼會跟著人一起晃");
        }

        [Test]
        public void ForwardLean_IsFollowed_SoTheHeadKeepsItsDistance()   // ← 飛行前傾,頭不該爆大
        {
            var end = Run(Vector3.zero, _ => Lean, 120);          // 2 秒
            float residual = (Lean - end).magnitude;              // = 相機到頭「被吃掉」的距離
            Assert.LessOrEqual(residual, DeadZone + 0.01f, "前傾追不回來 → 頭仍然貼到相機上");
            Assert.Greater(end.magnitude, Lean.magnitude * 0.7f, "追得太少");
        }

        [Test]
        public void LeanPlusSway_ComesToRest_InsteadOfChasingEveryWobble()
        {
            // 前傾 + 一直在晃:追蹤點會停在晃動包絡的邊緣,之後幾乎不再移動 —— 晃動整段在框內演出。
            var settled = Run(Vector3.zero, i => Lean + Sway(i), 600);          // 10 秒定位
            float travel = Travel(settled, i => Lean + Sway(i), 600, 300);      // 之後 5 秒總共走了多少
            float chased = Travel(Run(Vector3.zero, i => Lean + Sway(i), 600, deadZone: 0f),
                                  i => Lean + Sway(i), 600, 300, deadZone: 0f); // 同樣 5 秒,沒有死區會走多少

            Assert.Less(travel, 0.05f, "定位之後仍然在追晃動 → 頭貼會抖(死區沒吃掉擺動)");
            Assert.Greater(chased, 20f * travel, "對照組不成立 —— 沒有死區時本來就該一路跟著晃");
            Assert.LessOrEqual((Lean - settled).magnitude, DeadZone + 0.01f, "前傾沒追回來");
        }

        /// <summary>從 <paramref name="start"/> 開始跑 <paramref name="frames"/> 幀,累計追蹤點總共移動的距離。</summary>
        private static float Travel(Vector3 start, System.Func<int, Vector3> raw, int firstFrame, int frames,
                                    float deadZone = -1f, float creep = 0f)
        {
            if (deadZone < 0f) deadZone = DeadZone;
            var cur = start; float sum = 0f;
            for (int i = firstFrame; i < firstFrame + frames; i++)
            {
                var next = HeadPortraitAimFilter.Step(cur, raw(i), deadZone, Smooth, creep, Dt);
                sum += (next - cur).magnitude;
                cur = next;
            }
            return sum;
        }

        // ---- 慢爬:把死區留下的殘餘也吃掉 ---------------------------------------------------------------------------
        // 光靠死區,追蹤點永遠落後一個死區 —— 實測 FLY_NV 的前傾是「一直偏同一邊」的 11 個模型單位,落後 3.07 就是
        // 相機到頭的距離短 3.07(約 12%),頭還是會明顯變大。慢爬(τ=1.5s)只有持續偏同一邊才爬得動,來回擺動因為
        // 對稱又被長時間常數壓扁,幾乎不動(真 clip 實測:滑翔殘餘 3.07→0.17,待機相機自己只晃 ±0.29)。

        [Test]
        public void Creep_ErasesTheDeadZoneLag_OnASustainedLean()
        {
            float creep = HeadPortraitAimFilter.DefaultCreepSec;
            var noCreep = Run(Vector3.zero, _ => Lean, 600);
            var withCreep = Run(Vector3.zero, _ => Lean, 600, creep: creep);
            Assert.AreEqual(DeadZone, (Lean - noCreep).magnitude, 0.01f, "沒有慢爬時本來就該永遠落後一個死區");
            Assert.Less((Lean - withCreep).magnitude, 0.1f, "慢爬沒把持續偏移吃掉 → 飛行時頭還是比較大");
        }

        [Test]
        public void Creep_BarelyReactsToSway()   // ← 慢爬不能把「不跟著晃」賠掉
        {
            float creep = HeadPortraitAimFilter.DefaultCreepSec;
            float lo = float.MaxValue, hi = float.MinValue;
            var cur = Run(Vector3.zero, Sway, 600, creep: creep);   // 先跑 10 秒進穩態
            for (int i = 600; i < 900; i++)
            {
                cur = HeadPortraitAimFilter.Step(cur, Sway(i), DeadZone, Smooth, creep, Dt);
                lo = Mathf.Min(lo, cur.z); hi = Mathf.Max(hi, cur.z);
            }
            float swayAmp = 1.5f;                                   // Sway() 的 z 振幅
            Assert.Less(hi - lo, 0.3f * swayAmp, "慢爬把擺動也追進去了 → 頭貼會晃");
        }

        [Test]
        public void ReturningToRest_SettlesInsideTheDeadZone_WithoutOscillating()
        {
            var settled = Run(Vector3.zero, _ => Lean, 240);
            var back = Run(settled, _ => Vector3.zero, 240);      // 落地回正
            Assert.LessOrEqual(back.magnitude, DeadZone + 1e-3f, "回正後追蹤點沒有收回死區內");
            var again = Run(back, _ => Vector3.zero, 240);
            Assert.LessOrEqual((again - back).magnitude, 1e-4f, "停在死區邊緣後還在動 → 遲滯沒生效");
        }

        [Test]
        public void CrossingTheDeadZoneEdge_IsContinuous_NoJump()
        {
            // 剛好超出死區一點點 → 只能動一點點(不能一跨線就彈到目標)。
            var raw = new Vector3(0f, 0f, DeadZone + 0.05f);
            var moved = HeadPortraitAimFilter.Step(Vector3.zero, raw, DeadZone, Smooth, 0f, Dt);
            Assert.Less(moved.magnitude, 0.05f, "跨出死區時跳了一下");
            Assert.Greater(moved.magnitude, 0f, "超出死區卻不追");
        }

        [Test]
        public void IsFrameRateIndependent()
        {
            var at60 = Run(Vector3.zero, _ => Lean, 60, dt: 1f / 60f);      // 1 秒
            var at144 = Run(Vector3.zero, _ => Lean, 144, dt: 1f / 144f);   // 同樣 1 秒
            Assert.AreEqual(at60.z, at144.z, 0.05f, "不同 frame rate 追的量不一樣");
        }

        [Test]
        public void OnlyFollowsTheExcess_SoTheCameraAlwaysLagsByTheDeadZone()
        {
            var end = Run(Vector3.zero, _ => Lean, 600, smooth: 0f);        // 不平滑 → 一步到位
            Assert.AreEqual(DeadZone, (Lean - end).magnitude, 1e-3f, "沒有停在死區邊緣");
        }

        [Test]
        public void DegenerateInputs_DoNotMoveOrExplode()
        {
            var cur = new Vector3(1f, 2f, 3f);
            var raw = new Vector3(9f, 9f, 9f);
            Assert.AreEqual(cur, HeadPortraitAimFilter.Step(cur, raw, DeadZone, Smooth, 0f, 0f), "dt=0 不該動");
            Assert.AreEqual(cur, HeadPortraitAimFilter.Step(cur, raw, DeadZone, Smooth, 0f, -1f), "dt<0 不該動");
            Assert.AreEqual(cur, HeadPortraitAimFilter.Step(cur, raw, DeadZone, Smooth, 0f, float.NaN), "dt=NaN 不該動");
            Assert.AreEqual(cur, HeadPortraitAimFilter.Step(cur, cur, DeadZone, Smooth, 0f, Dt), "目標就是自己 → 不該動");
            // 死區關掉 = 純平滑跟隨(呼叫端要「完全跟頭」時用),仍然收斂到目標。
            var noDead = Run(Vector3.zero, _ => raw, 600, deadZone: 0f);
            Assert.AreEqual(raw.x, noDead.x, 1e-2f);
            Assert.AreEqual(raw.z, noDead.z, 1e-2f);
        }
    }
}
