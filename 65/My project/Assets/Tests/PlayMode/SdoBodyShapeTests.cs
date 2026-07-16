using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>Pure-logic tests for the faithful SDO body-shape (體型) port (see SdoBodyShape).</summary>
    public class SdoBodyShapeTests
    {
        [Test]
        public void StandardWeight_IsNoOp_ForEveryGroup()
        {
            Assert.AreEqual(Vector3.one, SdoBodyShape.ScaleFor("Bip01_Pelvis", 1f));   // big group
            Assert.AreEqual(Vector3.one, SdoBodyShape.ScaleFor("Bip01_Spine1", 1f));   // small group
            Assert.AreEqual(Vector3.one, SdoBodyShape.ScaleFor("Bip01_Head", 1f));     // head
        }

        [Test]
        public void Thin_ShrinksCrossSection_KeepsLengthAxis()
        {
            var s = SdoBodyShape.ScaleFor("Bip01_Pelvis", 0.889f);
            Assert.AreEqual(1f, s.x, 1e-5f);              // X = bone length axis is preserved
            Assert.Less(s.y, 1f);                         // cross-section shrinks (thinner)
            Assert.AreEqual(s.y, s.z, 1e-6f);             // both cross axes scale uniformly
        }

        [Test]
        public void Fat_GrowsCrossSection()
        {
            var s = SdoBodyShape.ScaleFor("Bip01_Pelvis", 1.2f);
            Assert.AreEqual(1f, s.x, 1e-5f);
            Assert.Greater(s.y, 1f);
        }

        [Test]
        public void GroupRates_MatchDecompiledFormula()
        {
            const float B = 1.5f;
            // BIG group (pelvis table): (B-1) * 1.9128205 + 1
            Assert.AreEqual((B - 1f) * 1.9128205f + 1f, SdoBodyShape.ScaleFor("Bip01_L_Thigh", B).y, 1e-5f);
            Assert.AreEqual((B - 1f) * 1.9128205f + 1f, SdoBodyShape.ScaleFor("Bip01_L_UpperArm", B).y, 1e-5f);
            // SMALL group (spine1 table): exactly B
            Assert.AreEqual(B, SdoBodyShape.ScaleFor("Bip01_L_Hand", B).y, 1e-5f);
            Assert.AreEqual(B, SdoBodyShape.ScaleFor("Bip01_Spine1", B).y, 1e-5f);
            // HEAD: (B-1) * 0.2725275 + 1, on the Y axis only (Z stays 1)
            var head = SdoBodyShape.ScaleFor("Bip01_Head", B);
            Assert.AreEqual((B - 1f) * 0.2725275f + 1f, head.y, 1e-5f);
            Assert.AreEqual(1f, head.z, 1e-6f);
        }

        [Test]
        public void UnaffectedBones_AreNeverScaled()
        {
            Assert.AreEqual(Vector3.one, SdoBodyShape.ScaleFor("Bip01_L_Finger0", 0.5f));
            Assert.AreEqual(Vector3.one, SdoBodyShape.ScaleFor("Bip01_L_Foot", 0.5f));
            Assert.AreEqual(Vector3.one, SdoBodyShape.ScaleFor("Bip01_L_Toe0", 0.5f));
            Assert.AreEqual(Vector3.one, SdoBodyShape.ScaleFor("Bip01", 0.5f));
        }

        [Test]
        public void WeightFromIndex_IsFaithfulToBuildCharacter()
        {
            // female (baseline 90): {80,90,100,110,120}
            Assert.AreEqual(80f / 90f, SdoBodyShape.WeightFromIndex(0, false), 1e-5f);  // thinnest
            Assert.AreEqual(1f, SdoBodyShape.WeightFromIndex(1, false), 1e-5f);          // standard
            Assert.AreEqual(120f / 90f, SdoBodyShape.WeightFromIndex(4, false), 1e-5f);  // fattest
            // male (baseline 110): {100,110,120,130,140}
            Assert.AreEqual(100f / 110f, SdoBodyShape.WeightFromIndex(0, true), 1e-5f);  // thinnest
            Assert.AreEqual(1f, SdoBodyShape.WeightFromIndex(1, true), 1e-5f);           // standard
        }

        [Test]
        public void WeightFromIndex_ClampsOutOfRange()
        {
            Assert.AreEqual(SdoBodyShape.WeightFromIndex(0, false), SdoBodyShape.WeightFromIndex(-3, false), 1e-6f);
            Assert.AreEqual(SdoBodyShape.WeightFromIndex(4, false), SdoBodyShape.WeightFromIndex(99, false), 1e-6f);
        }

        [Test]
        public void StandardIndex1_IsNormalBody_ForBothGenders_AndScalesNothing()
        {
            // 商店/儲物櫃的「卡片縮圖」用預設「正常身材」= index 1 (SdoRoomAvatar preview overload 的 bodyWeight 預設 1.0)。
            // index 1 對兩種性別都是 B=1.0，且對任何骨頭都是不縮放 (no-op)。(左側玩家假人另外跟隨玩家自己的體型。)
            Assert.AreEqual(1f, SdoBodyShape.WeightFromIndex(1, false), 1e-6f);
            Assert.AreEqual(1f, SdoBodyShape.WeightFromIndex(1, true), 1e-6f);
            float bStd = SdoBodyShape.WeightFromIndex(1, false);
            Assert.AreEqual(Vector3.one, SdoBodyShape.ScaleFor("Bip01_Pelvis", bStd));
            Assert.AreEqual(Vector3.one, SdoBodyShape.ScaleFor("Bip01_L_Hand", bStd));
            Assert.AreEqual(Vector3.one, SdoBodyShape.ScaleFor("Bip01_Head", bStd));
        }
    }
}
