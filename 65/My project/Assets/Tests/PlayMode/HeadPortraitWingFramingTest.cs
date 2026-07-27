using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 真實資產回歸:穿「Ribbon Star M」(iteminfo 37939 / cat 8 翅膀 = AVATAR/037939_MAN_CHIBANG.MSH)之後,
    /// 結算左側大頭貼的相機被甩掉 —— 舊版拿 renderer bounds 的髮頂算距離,翅膀比頭高就把相機推遠。
    ///
    /// 現在取景只對頭骨,所以這裡驗證的是:同一副骨架下,穿不穿翅膀,**頭骨位置與算出來的相機取景完全一樣**;
    /// 同時量出翅膀確實高過髮頂多少(執行期 renderer.bounds 是唯一可信的量法 —— MSH raw 頂點在 scaled bone-local
    /// 綁定下會騙人),留下「為什麼不能量幾何」的實據。
    /// Run: -runTests -testPlatform PlayMode -testFilter Sdo.Tests.HeadPortraitWingFramingTest
    /// </summary>
    public class HeadPortraitWingFramingTest
    {
        private const string WingRel = "AVATAR/037939_MAN_CHIBANG.MSH";   // Ribbon Star M
        private static readonly Vector3 Aim = new Vector3(-2f, HeadBoneFraming.AimUpModel, 0f);

        [UnityTest]
        public IEnumerator RibbonStarWing_DoesNotMoveTheHeadPortraitCamera()
        {
            string wingPath = SdoAvatarBuilder.ResolveAvatarFile(WingRel);
            if (string.IsNullOrEmpty(wingPath) || !File.Exists(wingPath))
                Assert.Ignore("DATA 沒有 " + WingRel + " — 跳過真實資產驗證");

            var bare = new List<string>(SdoRoomAvatar.DefaultParts(true));
            var winged = new List<string>(bare) { WingRel };
            var goA = new GameObject("bare");
            var goB = new GameObject("winged");
            try
            {
                var avA = SdoRoomAvatar.Build(goA, 11, portraitOpaque: true, male: true, equippedParts: bare.ToArray(), bodyIndex: 0);
                var avB = SdoRoomAvatar.Build(goB, 11, portraitOpaque: true, male: true, equippedParts: winged.ToArray(), bodyIndex: 0);
                Assert.IsNotNull(avA); Assert.IsNotNull(avB);
                for (int i = 0; i < 8; i++) yield return null;   // 等 CPU skinning 把 bounds 算好

                // 取景基準:頭骨的 rest 位置 —— 穿什麼都一樣(同一副骨架)
                Vector3 boneA = avA.BoneModelPos("Bip01_Head"), boneB = avB.BoneModelPos("Bip01_Head");
                Assert.AreNotEqual(Vector3.zero, boneA, "抓不到頭骨");
                Assert.AreEqual(boneA.x, boneB.x, 1e-4f);
                Assert.AreEqual(boneA.y, boneB.y, 1e-4f);
                Assert.AreEqual(boneA.z, boneB.z, 1e-4f);

                HeadBoneFraming.Compute(goA.transform.TransformPoint(boneA), 1.05f, 1f, Aim, out var tA, out float dA);
                HeadBoneFraming.Compute(goB.transform.TransformPoint(boneB), 1.05f, 1f, Aim, out var tB, out float dB);
                Assert.AreEqual(dA, dB, 1e-4f);          // 相機距離不變 → 頭一樣大
                Assert.AreEqual(tA.y, tB.y, 1e-4f);      // 瞄準點不變 → 頭在框裡的位置也一樣

                // 為什麼不能量幾何:翅膀頂比髮頂高一大截(舊版就是拿這個當「髮頂」)
                float wingTop = float.NegativeInfinity, headTop = float.NegativeInfinity;
                foreach (var r in avB.GetComponentsInChildren<Renderer>())
                {
                    if (r == null) continue;
                    var b = r.bounds;
                    if (b.size.sqrMagnitude < 1e-6f) continue;
                    string n = r.gameObject.name.ToUpperInvariant();
                    if (n.Contains("CHIBANG")) wingTop = Mathf.Max(wingTop, b.max.y);
                    if (n.Contains("FACE") || n.Contains("HAIR")) headTop = Mathf.Max(headTop, b.max.y);
                }
                float boneY = goB.transform.TransformPoint(boneB).y;
                Debug.Log(string.Format("[wing-frame] headBoneY={0:F2} hairTop={1:F2} wingTop={2:F2} → 舊版 dist {3:F1} vs 現在 {4:F1}",
                                        boneY, headTop, wingTop, 1.9f * (wingTop - boneY), dB));
                Assert.Greater(wingTop, headTop, "這件翅膀沒有高過頭 → 換一件會壓到頭貼的來測");
                Assert.Less(dB, 1.9f * (wingTop - boneY), "取景又被翅膀推遠了");
            }
            finally { Object.DestroyImmediate(goA); Object.DestroyImmediate(goB); }
        }
    }
}
