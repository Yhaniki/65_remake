using NUnit.Framework;
using Sdo.Game;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>
    /// 腳踝要**真的**落在目標上 —— 這是 IK 的全部意義,所以每個測試都直接量 |膝→目標| 有沒有等於小腿長。
    /// (aim 只複製方向不複製骨長,初音的腿比 SDO 女角短 9%,腳才會踩不到地。)
    /// </summary>
    public class MmdFootIkTests
    {
        private const float A = 11f, B = 15f;                 // 大腿 / 小腿
        private static readonly Vector3 Hip = new Vector3(1f, 30f, 0f);
        private static readonly Vector3 KneeForward = new Vector3(1f, 18f, 4f);   // 膝蓋朝 +Z(人往前跪)

        private static void AssertReaches(Vector3 hip, Vector3 target, Vector3 kneePos, Vector3 thighDir, float a, float b)
        {
            Assert.That(thighDir.magnitude, Is.EqualTo(1f).Within(1e-4f), "大腿方向要正規化");
            Assert.That(Vector3.Distance(hip, kneePos), Is.EqualTo(a).Within(1e-3f), "大腿長不能被改變");
            Assert.That(Vector3.Distance(kneePos, target), Is.EqualTo(b).Within(1e-3f), "小腿要正好接到目標");
        }

        [Test]
        public void Reachable_Target_Is_Hit_Exactly()
        {
            var target = new Vector3(1f, 8f, 3f);             // 距離 ~22.2,小於 a+b=26
            Assert.IsTrue(MmdFootIk.Solve(Hip, target, KneeForward, A, B, out var dir, out var knee));
            AssertReaches(Hip, target, knee, dir, A, B);
        }

        [Test]
        public void The_Knee_Bends_To_The_Side_The_Motion_Already_Chose()
        {
            // 🔴 IK 不可以自己決定膝蓋往哪彎 —— 那是動作的一部分。同一個目標,hint 在前就往前彎,hint 在後就往後。
            var target = new Vector3(1f, 8f, 0f);
            MmdFootIk.Solve(Hip, target, KneeForward, A, B, out _, out var kneeFwd);
            MmdFootIk.Solve(Hip, target, new Vector3(1f, 18f, -4f), A, B, out _, out var kneeBack);
            Assert.Greater(kneeFwd.z, Hip.z, "hint 在 +Z → 膝蓋要在 +Z 側");
            Assert.Less(kneeBack.z, Hip.z, "hint 在 -Z → 膝蓋要在 -Z 側");
            AssertReaches(Hip, target, kneeFwd, (kneeFwd - Hip).normalized, A, B);
            AssertReaches(Hip, target, kneeBack, (kneeBack - Hip).normalized, A, B);
        }

        [Test]
        public void Out_Of_Reach_Straightens_Toward_The_Target_Instead_Of_Giving_Up()
        {
            // 腿比 SDO 短,W_005663 上約 15% 的幀構不到。這時要伸直朝目標(連續),不能突然彈回 aim 的姿勢。
            var target = Hip + new Vector3(0f, -40f, 0f);     // 遠超過 a+b
            Assert.IsTrue(MmdFootIk.Solve(Hip, target, KneeForward, A, B, out var dir, out var knee));
            Assert.That(Vector3.Distance(dir, Vector3.down), Is.LessThan(1e-4f));
            Assert.That(Vector3.Distance(knee, Hip + Vector3.down * A), Is.LessThan(1e-3f));
        }

        [Test]
        public void Right_At_Full_Stretch_Does_Not_Blow_Up()
        {
            var target = Hip + Vector3.down * (A + B);
            Assert.IsTrue(MmdFootIk.Solve(Hip, target, KneeForward, A, B, out var dir, out var knee));
            AssertReaches(Hip, target, knee, dir, A, B);
        }

        [Test]
        public void A_Target_Sitting_On_The_Hip_Is_Refused_Rather_Than_Guessed()
        {
            Assert.IsFalse(MmdFootIk.Solve(Hip, Hip, KneeForward, A, B, out _, out _));
        }

        [Test]
        public void Folded_Past_The_Limit_Is_Refused()
        {
            // |a-b| = 4:目標比這還近就折不出來,維持 aim 的結果比硬解一個亂七八糟的姿勢好。
            Assert.IsFalse(MmdFootIk.Solve(Hip, Hip + Vector3.down * 2f, KneeForward, A, B, out _, out _));
        }

        [Test]
        public void Zero_Length_Bones_Are_Refused()
        {
            Assert.IsFalse(MmdFootIk.Solve(Hip, Hip + Vector3.down * 10f, KneeForward, 0f, B, out _, out _));
            Assert.IsFalse(MmdFootIk.Solve(Hip, Hip + Vector3.down * 10f, KneeForward, A, 0f, out _, out _));
        }

        [Test]
        public void A_Knee_Hint_On_The_Leg_Axis_Still_Produces_A_Valid_Solution()
        {
            // 腿正好打直時 hint 跟目標方向共線,叉積退化 —— 不能因此吐 NaN 或拒解。
            var target = Hip + Vector3.down * 20f;
            Assert.IsTrue(MmdFootIk.Solve(Hip, target, Hip + Vector3.down * 5f, A, B, out var dir, out var knee));
            AssertReaches(Hip, target, knee, dir, A, B);
            Assert.IsFalse(float.IsNaN(dir.x) || float.IsNaN(dir.y) || float.IsNaN(dir.z));
        }

        [Test]
        public void Equal_Bone_Lengths_Bend_Symmetrically()
        {
            var target = Hip + new Vector3(0f, -20f, 0f);
            Assert.IsTrue(MmdFootIk.Solve(Hip, target, KneeForward, 13f, 13f, out var dir, out var knee));
            AssertReaches(Hip, target, knee, dir, 13f, 13f);
            Assert.That(knee.y, Is.EqualTo(Hip.y - 10f).Within(1e-3f), "等長時膝蓋落在中點高度");
        }
    }
}
