using NUnit.Framework;
using Sdo.Game;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>
    /// 頭貼取景。核心不變量:**只由頭骨決定** —— 相機的距離/瞄準點只跟頭骨位置、avatar scale、zoom 有關,
    /// 完全不看臉、頭髮、或任何配件的幾何(使用者:「不該算臉或頭髮,就是對頭的骨骼的位置就好」)。
    ///
    /// 這條規則就是「穿 Ribbon Star M(037939 翅膀)結算大頭貼跑掉」的修法:舊版量 renderer bounds 的髮頂算距離,
    /// 翅膀頂比髮頂高一大截(實測 h 12.61 → 31.50)就把相機甩遠 2.5 倍。函式簽名裡根本沒有 mesh,是結構性的保證。
    /// </summary>
    public class HeadBoneFramingTests
    {
        private static readonly Vector3 Bone = new Vector3(5000f, 55.12f, 5000f);   // 男生頭骨 rest(實測)
        private static readonly Vector3 Aim = new Vector3(-2f, HeadBoneFraming.AimUpModel, 0f);

        [Test]
        public void Calibration_ReproducesTheVerifiedHeadCloseUp()
        {
            // 校準值取自「翅膀不算進去」時那個正確構圖(scale 1.05):dist 25.17、瞄準點在頭骨上方 4.64。
            HeadBoneFraming.Compute(Bone, 1.05f, 1f, Aim, out var target, out float dist);
            Assert.AreEqual(25.17f, dist, 0.05f);
            Assert.AreEqual(Bone.y + 4.64f, target.y, 0.05f);
            Assert.AreEqual(Bone.x - 2.1f, target.x, 0.05f);   // X 把臉擺正
        }

        [Test]
        public void Framing_ScalesWithTheAvatar_SoTheHeadStaysTheSameSizeInFrame()
        {
            HeadBoneFraming.Compute(Bone, 1f, 1f, Aim, out var t1, out float d1);
            HeadBoneFraming.Compute(Bone, 2f, 1f, Aim, out var t2, out float d2);
            Assert.AreEqual(2f * d1, d2, 1e-3f);                       // 距離跟著 scale 等比 → 畫面上的頭一樣大
            Assert.AreEqual(2f * (t1.y - Bone.y), t2.y - Bone.y, 1e-3f);
        }

        [Test]
        public void Zoom_IsTheOnlyLever_AndOnlyMovesTheCameraBack()
        {
            HeadBoneFraming.Compute(Bone, 1.05f, 1f, Aim, out var t1, out float d1);
            HeadBoneFraming.Compute(Bone, 1.05f, 1.5f, Aim, out var t2, out float d2);
            Assert.AreEqual(1.5f * d1, d2, 1e-3f);
            Assert.AreEqual(t1, t2);            // 只改距離,構圖中心不動
        }

        [Test]
        public void DegenerateInputs_DoNotProduceADegenerateCamera()
        {
            HeadBoneFraming.Compute(Bone, 0f, 0f, Aim, out _, out float dist);
            Assert.Greater(dist, 0f);           // scale/zoom 被夾住 → 相機不會塌在瞄準點上
        }

        [Test]
        public void TheResultPortraitUsesTheseConstants()   // 結算頭貼的預設值就是這組(F4 才會改)
        {
            var go = new GameObject("t");
            try
            {
                var s = go.AddComponent<ScreenGameplay>();
                Assert.AreEqual(HeadBoneFraming.DistModel, s.headPortraitDist, 1e-4f);
                Assert.AreEqual(HeadBoneFraming.AimUpModel, s.headAimOffset.y, 1e-4f);
                Assert.AreEqual(1f, s.headZoom, 1e-4f);
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
