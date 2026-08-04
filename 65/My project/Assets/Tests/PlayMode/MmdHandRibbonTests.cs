using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 手部光條(<see cref="HandRibbon"/>)要從**畫面上那隻手**長出來,而不是從驅動它的 SDO 骨架長出來。
    ///
    /// MMD 顯示開著時這兩隻手不在同一個地方:retarget 只把 MMD 的骨頭**指向**跟 SDO 一樣的方向,骨頭多長是模型
    /// 自己的事。實測初音(等高縮放到 SDO 骨架後)肩→手腕鏈只有 SDO 的 77%(14.65 vs 18.98,差 4.33 ≈ 身高的 8%),
    /// 所以掛在 SDO 手骨上的光條會在 MMD 手掌外面浮一截 ——「手的光沒接好,有一段隔空」。
    /// </summary>
    public class MmdHandRibbonTests
    {
        // ---- 覆寫本身(不需要模型/遊戲資料,到哪都跑得動) ----------------------------------------------------

        /// <summary>有覆寫就聽覆寫的;覆寫收回去就回到自己的錨點。而且**換來源那一幀要斷開** —— 兩隻手差著一截,
        /// 舊節點接新節點會拉出一條穿過空氣的光帶。
        ///
        /// 掌寬故意設成錨點的一半(初音的掌寬只有 SDO 的 52%),用來釘住「換身體只換位置、不換粗細」:
        /// 光條的寬度是照真實掌寬算的,不補正的話同一個玩家換個模型粗細就變一次。</summary>
        [UnityTest]
        public IEnumerator Source_OverridesTheAnchors_KeepsTheAnchorWidth_AndSwitchingSourceBreaksTheBand()
        {
            var host = new GameObject("HandRibbonHost");
            var anchorH = Anchor(host, new Vector3(0f, 1f, 0f));
            var anchorF = Anchor(host, new Vector3(0.4f, 1f, 0f));     // SDO 掌寬 0.4 → 帶寬 0.8
            var mmdH = Anchor(host, new Vector3(10f, 1f, 0f));
            var mmdF = Anchor(host, new Vector3(10.2f, 1f, 0f));       // MMD 掌寬 0.2(一半)

            var rib = new GameObject("HandRibbon").AddComponent<HandRibbon>();
            rib.hand = anchorH; rib.finger = anchorF;
            bool useMmd = true;
            rib.Source = (out Transform h, out Transform f) =>
            {
                h = useMmd ? mmdH : null; f = useMmd ? mmdF : null;
                return useMmd;
            };

            for (int i = 0; i < 4; i++) yield return null;
            var mesh = rib.GetComponent<MeshFilter>().sharedMesh;
            Assert.Greater(mesh.vertexCount, 1, "光條沒有累積出節點");
            AssertXRange(mesh, 9.75f, 10.65f, "光條沒有跟著覆寫(MMD)的手骨");
            Assert.AreEqual(0.8f, Width(mesh), 0.02f,
                            "換到掌寬只有一半的手之後光條跟著變細 —— 覆寫該換的是位置,不是粗細");

            // 覆寫收回去(＝沒選模型 / MMD 身體被丟掉)→ 回到 SDO 錨點,而且不能跟舊節點連成一條
            useMmd = false;
            yield return null;
            Assert.Less(rib.GetComponent<MeshFilter>().sharedMesh.vertexCount, 2,
                        "換手的那一幀沒有斷開 —— 舊節點會跟新的連成一條橫跨兩隻手的光帶");
            for (int i = 0; i < 4; i++) yield return null;
            mesh = rib.GetComponent<MeshFilter>().sharedMesh;
            AssertXRange(mesh, -0.1f, 0.9f, "光條沒有回到 SDO 錨點");
            Assert.AreEqual(0.8f, Width(mesh), 0.02f, "回到 SDO 錨點之後寬度變了");

            // 關掉補正 = 忠實掌寬:小手就是細帶子(留著這條路,才知道補正是可切換的政策不是寫死的)
            rib.matchAnchorWidth = false;
            useMmd = true;
            for (int i = 0; i < 4; i++) yield return null;
            Assert.AreEqual(0.4f, Width(rib.GetComponent<MeshFilter>().sharedMesh), 0.02f,
                            "matchAnchorWidth=false 應該退回忠實掌寬(帶寬 = 2×掌寬)");

            Object.Destroy(host); Object.Destroy(rib.gameObject);
            yield return null;
        }

        // ---- 真模型:MMD 的手跟 SDO 的手差多遠,以及光條掛對了沒 ------------------------------------------

        [UnityTest]
        public IEnumerator MmdBody_HandBonesResolve_AndAreNowhereNearTheSdoOnes()
        {
            if (string.IsNullOrEmpty(MmdAvatarSwap.ModelPath)) Assert.Ignore("MMD model not installed (DATA/MODEL/… or assets/MODEL/…)");
            if (!System.IO.File.Exists(System.IO.Path.Combine(SdoExtracted.Root, "AVATAR", "FEMALE.HRC")))
                Assert.Ignore("SDO game data not available");
            LogAssert.ignoreFailingMessages = true;

            MmdAvatarSwap.SetEnabled(true);
            var host = new GameObject("HandRibbonPreviewHost");
            var preview = host.AddComponent<GenderPreview3D>();
            preview.Build(gender: 0);
            for (int i = 0; i < 6; i++) yield return null;

            var driver = System.Array.Find(host.GetComponentsInChildren<SdoAvatar>(true), a => a.gameObject.activeInHierarchy);
            Assert.IsNotNull(driver, "沒有可用的預覽舞者");
            var mmd = MmdAvatarSwap.ActiveFor(driver);
            Assert.IsNotNull(mmd, "預覽沒有換成 MMD 身體");

            var smr = mmd.GetComponentInChildren<SkinnedMeshRenderer>(true);
            float body = smr.bounds.size.y;
            Assert.Greater(body, 0.1f);

            foreach (bool left in new[] { true, false })
            {
                Assert.IsTrue(mmd.TryHandBones(left, out var mh, out var mf),
                              $"MMD 身體找不到{(left ? "左" : "右")}手的骨(手腕 + 指根)");
                Assert.AreNotSame(mh, mf, "手腕跟指根解析成同一根骨 → 掌寬 0,光條會退化成一條線");

                // 這兩根骨的世界座標,就是「畫面上的手」在哪。SDO 骨架的同名骨在別的地方 —— 差距就是使用者看到的那段空隙。
                string bone = left ? "Bip01_L_Hand" : "Bip01_R_Hand";
                int hi = driver.BoneIndex(bone);
                Assert.GreaterOrEqual(hi, 0);
                Vector3 sdo = driver.transform.TransformPoint((Vector3)driver.BoneAnimWorld(hi).GetColumn(3));
                float gap = Vector3.Distance(sdo, mh.position);
                Debug.Log($"[handribbon-test] {bone}: SDO {sdo} vs MMD {mh.position} → 差 {gap:F3} (身高 {body:F3} 的 {gap / body:P1})");
                Assert.Greater(gap, 0.02f * body,
                               $"{bone} 的 SDO 骨與 MMD 骨幾乎重合({gap / body:P1} 身高)—— 這個測試就失去意義了," +
                               "請確認 retarget 沒有改成連骨長一起對齊");

                // 而且 MMD 的手要真的在**身體上**(在 SkinnedMeshRenderer 的包圍盒裡):證明光條掛過去之後是接著人的。
                Assert.IsTrue(smr.bounds.Contains(mh.position), $"{(left ? "左" : "右")}手骨落在 MMD 身體的包圍盒外");

                // 掌寬補正的分母:兩具身體的掌寬都要量得到,補正比例才有定義(初音實測約 SDO 的一半)。
                int fi = driver.BoneIndex(left ? "Bip01_L_Finger0" : "Bip01_R_Finger0");
                Assert.GreaterOrEqual(fi, 0);
                Vector3 sdoFinger = driver.transform.TransformPoint((Vector3)driver.BoneAnimWorld(fi).GetColumn(3));
                float sdoPalm = Vector3.Distance(sdo, sdoFinger), mmdPalm = Vector3.Distance(mh.position, mf.position);
                Debug.Log($"[handribbon-test] {bone} 掌寬: SDO {sdoPalm:F3} vs MMD {mmdPalm:F3} → 補正 ×{sdoPalm / Mathf.Max(mmdPalm, 1e-4f):F2}");
                Assert.Greater(sdoPalm, 1e-3f, "SDO 掌寬量不到 → 補正沒有基準");
                Assert.Greater(mmdPalm, 1e-3f, "MMD 掌寬量不到 → 補正會除以 0(手腕與指根解析成同一點)");
            }

            // 而生產程式碼掛上去的來源,解析出來的就是這兩根骨(ScreenGameplay.CreateHandTrail 用的同一個工廠)。
            var src = MmdAvatarSwap.HandSourceFor(driver, left: true);
            Assert.IsTrue(src(out var wiredHand, out var wiredFinger));
            Assert.IsTrue(mmd.TryHandBones(true, out var expectHand, out var expectFinger));
            Assert.AreSame(expectHand, wiredHand); Assert.AreSame(expectFinger, wiredFinger);

            // 關掉 MMD 顯示 → 來源交還給 SDO 錨點(回傳 false,光條用自己的 hand/finger)
            MmdAvatarSwap.SetEnabled(false);
            yield return null;
            Assert.IsFalse(src(out _, out _), "MMD 顯示關掉之後,手部光條還是被綁在 MMD 骨上");

            Object.Destroy(host);
            yield return null;
        }

        [TearDown]
        public void Restore() => MmdAvatarSwap.SetEnabled(false);

        private static Transform Anchor(GameObject host, Vector3 pos)
        {
            var t = new GameObject("a").transform;
            t.SetParent(host.transform, false);
            t.position = pos;
            return t;
        }

        /// <summary>帶子的寬度(這些測試的兩緣都排在 X 上)。</summary>
        private static float Width(Mesh mesh)
        {
            var v = mesh.vertices;
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var p in v) { lo = Mathf.Min(lo, p.x); hi = Mathf.Max(hi, p.x); }
            return v.Length == 0 ? 0f : hi - lo;
        }

        private static void AssertXRange(Mesh mesh, float lo, float hi, string msg)
        {
            var v = mesh.vertices;
            Assert.Greater(v.Length, 1, msg + "(沒有節點)");
            foreach (var p in v) Assert.IsTrue(p.x >= lo && p.x <= hi, $"{msg}:頂點 x={p.x:F2} 不在 [{lo}, {hi}]");
        }
    }
}
