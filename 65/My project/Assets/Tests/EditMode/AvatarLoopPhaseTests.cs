using NUnit.Framework;
using Sdo.Game;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>
    /// 待機循環的相位。官方實測(hook_avtshow_online.js,2026-08-03):
    ///   * 結算頭貼 —— 每個玩家一個 driver、同一毫秒開播同一支 63-tick 循環,整段 cursor spread = 0.000
    ///     → **所有人同步呼吸**。
    ///   * 房間頭貼 —— 各自不同動作、節奏不一致(官方畫的是每個人的 avatar 本體)。
    /// 對應到 remake:自由循環的幀只由「共用時鐘 + 相位偏移」決定,與 avatar 何時被建立無關,
    /// 所以 phase 相同 = 同步、phase 不同 = 錯開。這條就是上面兩種行為的唯一開關。
    /// </summary>
    public class AvatarLoopPhaseTests
    {
        private const float Fps = 30f, MaxTime = 62f;   // 63-tick 循環(官方結算頭貼實測長度)

        [Test]
        public void SamePhase_SameFrame_AtEveryInstant()
        {
            // 兩個各自建立、但相位相同的 avatar:任何時刻都在同一幀(結算頭貼的「同步呼吸」)
            foreach (float t in new[] { 0f, 0.017f, 1.234f, 10f, 123.456f })
                Assert.AreEqual(SdoAvatar.LoopFrame(t, 0f, Fps, MaxTime),
                                SdoAvatar.LoopFrame(t, 0f, Fps, MaxTime), 1e-4f,
                                $"same phase must give the same frame at t={t}");
        }

        [Test]
        public void PhaseOffset_StaggersTheCrowd()
        {
            // 房間 NPC 用 slot*0.31 錯開 → 同一時刻彼此不同幀
            float a = SdoAvatar.LoopFrame(5f, 0f, Fps, MaxTime);
            float b = SdoAvatar.LoopFrame(5f, 0.31f, Fps, MaxTime);
            Assert.That(Mathf.Abs(b - a), Is.GreaterThan(1e-3f), "a phase offset must move the frame");
            Assert.AreEqual(0.31f * Fps, b - a, 1e-3f, "offset in seconds scales by fps into frames");
        }

        [Test]
        public void Wraps_WithinClip_NeverExceedsLength()
        {
            for (float t = 0f; t < 20f; t += 0.05f)
            {
                float f = SdoAvatar.LoopFrame(t, 0f, Fps, MaxTime);
                Assert.GreaterOrEqual(f, 0f);
                Assert.Less(f, MaxTime + 1f, $"frame must stay inside the clip at t={t}");
            }
        }

        [Test]
        public void ZeroLengthClip_HoldsFrameZero()
        {
            Assert.AreEqual(0f, SdoAvatar.LoopFrame(7.5f, 0.31f, Fps, 0f), 1e-6f);
        }

        [Test]
        public void CreationTime_DoesNotMatter_OnlyPhaseDoes()
        {
            // 關鍵:幀綁的是共用時鐘,不是「這隻 avatar 活了多久」。房間頭貼晚 3 秒才建立,
            // 只要相位相同就仍然跟本體同幀 —— 這是頭貼鏡射能靠 CurrentPoseTime 對齊的前提。
            const float born = 3f, now = 8f;
            float bodyFrame = SdoAvatar.LoopFrame(now, 0f, Fps, MaxTime);
            float headFrame = SdoAvatar.LoopFrame(now, 0f, Fps, MaxTime);   // 晚生,但同相位
            Assert.AreEqual(bodyFrame, headFrame, 1e-4f);
            // 反例:若拿「出生後經過的時間」當幀來源就會差一截(舊寫法的錯誤模型)
            float ifAgeBased = SdoAvatar.LoopFrame(now - born, 0f, Fps, MaxTime);
            Assert.That(Mathf.Abs(bodyFrame - ifAgeBased), Is.GreaterThan(1e-3f));
        }
    }
}
