using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Game;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>
    /// 腳為什麼會在地上抖、會滑 —— 這個檔案釘死兩條答案(見 <see cref="MmdRetargetPlan"/> 的規則①②)。
    /// 兩條都是拿 W_005663(初音)離線重跑 retarget 量出來的,不是「看起來比較好」。
    /// </summary>
    public class MmdRetargetPlanTests
    {
        // ---------------------------------------------------------------- 迷你骨架(只留跟腳有關的)
        // SDO:Bip01 跟 Pelvis 疊在同一點(零長度 root),這正是 MMD 那邊沒有的性質。
        // 脊椎要留到 Spine1:Aim 走的是語意骨鏈(上半身→上半身2),鏈上少一節就沒得瞄。
        private static readonly string[] HrcNames =
        {
            "Bip01", "Bip01_Pelvis", "Bip01_Spine", "Bip01_Spine1", "Bip01_Neck", "Bip01_Head",
            "Bip01_L_Thigh", "Bip01_L_Calf", "Bip01_L_Foot", "Bip01_L_Toe0",
        };
        private static readonly int[] HrcParent = { -1, 0, 1, 2, 3, 4, 1, 6, 7, 8 };
        private static readonly Vector3[] HrcBind =
        {
            new Vector3(0f, 30f, 0f),      // Bip01        ─┐ 同一點
            new Vector3(0f, 30f, 0f),      // Pelvis       ─┘
            new Vector3(0f, 34f, 0f),      // Spine
            new Vector3(0f, 39f, 0f),      // Spine1
            new Vector3(0f, 45f, 0f),      // Neck
            new Vector3(0f, 48f, 0f),      // Head
            new Vector3(3f, 30f, 0f),      // L_Thigh
            new Vector3(3f, 17f, 0f),      // L_Calf
            new Vector3(3f, 4f, 0f),       // L_Foot
            new Vector3(3f, 0f, 3f),       // L_Toe0
        };

        // MMD:センター→グルーブ→腰→下半身 之間是有長度的,root 一轉整條腿就被甩走。
        private static readonly string[] MmdNames =
        {
            "全ての親", "センター", "グルーブ", "腰", "下半身", "上半身", "上半身2", "首", "頭",
            "左足", "左ひざ", "左足首", "左つま先",
        };
        private static readonly Vector3[] MmdPos =
        {
            new Vector3(0f, 0f, 0f),       // 全ての親
            new Vector3(0f, 8f, 0f),       // センター
            new Vector3(0f, 8f, 0f),       // グルーブ
            new Vector3(0f, 11f, 0f),      // 腰      ← 跟 センター 差 3,SDO 那邊對應的差是 0
            new Vector3(0f, 11.5f, 0f),    // 下半身
            new Vector3(0f, 12f, 0f),      // 上半身
            new Vector3(0f, 14f, 0f),      // 上半身2
            new Vector3(0f, 16f, 0f),      // 首
            new Vector3(0f, 17f, 0f),      // 頭
            new Vector3(1f, 11f, 0f),      // 左足
            new Vector3(1f, 6f, 0f),       // 左ひざ
            new Vector3(1f, 1.5f, 0f),     // 左足首
            new Vector3(1f, 0.3f, 1.2f),   // 左つま先
        };

        private static MmdBoneDrive[] Plan(string[] names = null, Vector3[] pos = null) =>
            MmdRetargetPlan.Build(names ?? MmdNames, pos ?? MmdPos, HrcNames, HrcParent, HrcBind);

        private static MmdBoneDrive Of(MmdBoneDrive[] plan, string bone, string[] names = null)
        {
            var n = names ?? MmdNames;
            for (int i = 0; i < n.Length; i++) if (n[i] == bone) return plan[i];
            Assert.Fail("骨頭不在測試骨架裡:" + bone);
            return default;
        }

        // ---------------------------------------------------------------- 規則① センター 只吃平移
        [Test]
        public void Center_Takes_Translation_Only_When_The_Pelvis_Is_Driven()
        {
            // 🔴 回歸點:センター 若拿 SDO Bip01 的世界旋轉,MMD 的 センター→腰 那 3 個單位的骨長會被那個
            //    旋轉甩出去,整個下半身連同雙腳跟著掃弧 —— 動作裡根本沒有那段位移。
            Assert.AreEqual(MmdDriveMode.FollowParent, Of(Plan(), "センター").Mode);
        }

        [Test]
        public void Center_Keeps_The_World_Delta_When_The_Pelvis_Is_Not_Mapped()
        {
            // 沒有 下半身 的模型,root 是唯一還在提供身體朝向的東西 → 不能連它也拿掉。
            var names = (string[])MmdNames.Clone();
            names[4] = "下半身_未對應";
            Assert.AreEqual(MmdDriveMode.WorldDelta, Of(Plan(names), "センター", names).Mode);
        }

        [Test]
        public void Dropping_The_Root_Spin_Does_Not_Cost_Any_Facing()
        {
            // 之所以敢拿掉:身體朝向是由 下半身/上半身 的 Aim 提供的,而 Aim 是絕對朝向、跟父骨無關。
            var p = Plan();
            Assert.AreEqual(MmdDriveMode.Aim, Of(p, "下半身").Mode);
            Assert.AreEqual(MmdDriveMode.Aim, Of(p, "上半身").Mode);
            Assert.AreEqual(MmdDriveMode.WorldDelta, Of(p, "頭").Mode, "頭仍要絕對朝向才會保持抬頭");
        }

        // ---------------------------------------------------------------- 規則② 足首 用世界差量
        [Test]
        public void The_Ankle_Copies_The_Sdo_Rotation_From_Rest_Not_The_Calf()
        {
            // 🔴 回歸點:足首 若落回 FollowParent(跟小腿硬轉)腳底永遠擺不平 —— 實測腳掌旋轉平均偏 16.5°、
            //    最多 46.1°。兩具骨架的 rest 都是站直腳底貼地,所以照抄「相對 rest 的旋轉」才是對的(0.0°)。
            Assert.AreEqual(MmdDriveMode.WorldDelta, Of(Plan(), "左足首").Mode);
        }

        [Test]
        public void The_Ankle_Does_Not_Aim_At_The_Toe_Even_If_The_Model_Has_One()
        {
            // 🔴 曾經改錯的方向:瞄 つま先 看起來對,但 MMD 的 つま先 是腳趾關節、SDO 的 Toe0 在地板上,
            //    兩邊 ankle→toe 差 8° —— 瞄過去等於把那 8° 烘進每一幀(實測殘留 9.5°)。
            Assert.AreEqual(MmdDriveMode.WorldDelta, Of(Plan(), "左足首").Mode);
            Assert.AreEqual(MmdDriveMode.Unmapped, Of(Plan(), "左つま先").Mode, "つま先 不該有對應");
        }

        [Test]
        public void A_Model_Without_A_Toe_Bone_Still_Builds()
        {
            var names = new List<string>(MmdNames); var pos = new List<Vector3>(MmdPos);
            int i = names.IndexOf("左つま先"); names.RemoveAt(i); pos.RemoveAt(i);
            Assert.AreEqual(MmdDriveMode.WorldDelta, Of(Plan(names.ToArray(), pos.ToArray()), "左足首", names.ToArray()).Mode);
        }

        // ---------------------------------------------------------------- 真實 FEMALE.HRC 有分支,不能靠 child 檔案順序猜 Aim
        [Test]
        public void BranchedHrc_UsesCanonicalContinuation_NotTheFirstMappedSibling()
        {
            // 真實順序:Spine 先列兩條腿才列 Spine1；Neck 先列兩邊肩膀才列 Head。
            // 舊的「第一個 mapped child」因此會讓 上半身→右腿、首→右肩。
            string[] hrcNames =
            {
                "Bip01", "Bip01_Pelvis", "Bip01_Spine",
                "Bip01_R_Thigh", "Bip01_L_Thigh", "Bip01_Spine1",
                "Bip01_Neck", "Bip01_R_Clavicle", "Bip01_L_Clavicle", "Bip01_Head",
                "Bip01_L_Hand", "Bip01_L_Finger0", "Bip01_L_Finger1",
            };
            int[] hrcParent = { -1, 0, 1, 2, 2, 2, 5, 6, 6, 6, 0, 10, 10 };
            Vector3[] hrcBind =
            {
                new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 0f), new Vector3(0f, 4f, 0f),
                new Vector3(-3f, 0f, 0f), new Vector3(3f, 0f, 0f), new Vector3(0f, 8f, 0f),
                new Vector3(0f, 12f, 0f), new Vector3(-2f, 12f, 0f), new Vector3(2f, 12f, 0f),
                new Vector3(0f, 14f, 0f), new Vector3(5f, 10f, 0f), new Vector3(6f, 10f, 0f),
                new Vector3(6f, 10.2f, 0f),
            };
            string[] mmdNames =
            {
                "センター", "下半身", "上半身", "上半身2", "首", "頭",
                "右足", "左足", "右肩", "左肩", "左手首", "左親指０", "左人指１",
            };
            Vector3[] mmdPos =
            {
                new Vector3(0f, 1f, 0f), new Vector3(0f, 2f, 0f), new Vector3(0f, 4f, 0f),
                new Vector3(0f, 8f, 0f), new Vector3(0f, 12f, 0f), new Vector3(0f, 14f, 0f),
                new Vector3(-3f, 0f, 0f), new Vector3(3f, 0f, 0f), new Vector3(-2f, 12f, 0f),
                new Vector3(2f, 12f, 0f), new Vector3(5f, 10f, 0f), new Vector3(6f, 10f, 0f),
                new Vector3(6f, 10.2f, 0f),
            };

            var plan = MmdRetargetPlan.Build(mmdNames, mmdPos, hrcNames, hrcParent, hrcBind);
            int spine = System.Array.IndexOf(mmdNames, "上半身");
            int neck = System.Array.IndexOf(mmdNames, "首");
            int head = System.Array.IndexOf(mmdNames, "頭");
            int wrist = System.Array.IndexOf(mmdNames, "左手首");

            Assert.AreEqual(MmdDriveMode.Aim, plan[spine].Mode);
            Assert.AreEqual("Bip01_Spine1", hrcNames[plan[spine].AimChildHrc], "Spine 不可瞄 sibling 大腿");
            Assert.AreEqual(MmdDriveMode.Aim, plan[neck].Mode);
            Assert.AreEqual("Bip01_Head", hrcNames[plan[neck].AimChildHrc], "Neck 不可瞄 sibling 肩膀");
            Assert.AreEqual(MmdDriveMode.WorldDelta, plan[head].Mode, "頭保持絕對朝向");
            Assert.AreEqual(-1, plan[head].AimChildHrc);
            Assert.AreEqual(MmdDriveMode.FollowParent, plan[wrist].Mode, "手腕不可任選一根手指當 Aim");
            Assert.AreEqual(-1, plan[wrist].AimChildHrc);
        }

        // ---------------------------------------------------------------- 其它不能被上面兩條改掉的事
        [Test]
        public void The_Leg_Chain_Aims_All_The_Way_Down()
        {
            var p = Plan();
            Assert.AreEqual(MmdDriveMode.Aim, Of(p, "左足").Mode);
            Assert.AreEqual("Bip01_L_Calf", HrcNames[Of(p, "左足").AimChildHrc]);
            Assert.AreEqual(MmdDriveMode.Aim, Of(p, "左ひざ").Mode);
            Assert.AreEqual("Bip01_L_Foot", HrcNames[Of(p, "左ひざ").AimChildHrc]);
        }

        [Test]
        public void Aim_Rest_Direction_Is_The_Mmd_Bone_To_Child_Direction()
        {
            var knee = Of(Plan(), "左ひざ");
            Vector3 expect = (MmdPos[System.Array.IndexOf(MmdNames, "左足首")]
                            - MmdPos[System.Array.IndexOf(MmdNames, "左ひざ")]).normalized;
            Assert.That(Vector3.Distance(knee.AimRestDir, expect), Is.LessThan(1e-5f));
        }

        [Test]
        public void Unmapped_In_Between_Bones_Are_Left_Alone()
        {
            // グルーブ/腰 沒有對應 —— 它們跟著父骨,不該被硬塞一個驅動來源。
            var p = Plan();
            Assert.AreEqual(MmdDriveMode.Unmapped, Of(p, "グルーブ").Mode);
            Assert.AreEqual(MmdDriveMode.Unmapped, Of(p, "腰").Mode);
            Assert.AreEqual(-1, Of(p, "腰").Hrc);
        }

        [Test]
        public void An_Empty_Or_Broken_Rig_Returns_An_Empty_Plan_Instead_Of_Throwing()
        {
            Assert.AreEqual(0, MmdRetargetPlan.Build(new string[0], new Vector3[0], HrcNames, HrcParent, HrcBind).Length);
            var p = MmdRetargetPlan.Build(MmdNames, MmdPos, new string[0], new int[0], new Vector3[0]);
            Assert.AreEqual(MmdNames.Length, p.Length);
            foreach (var d in p) Assert.AreEqual(-1, d.Hrc);
        }
    }
}
