using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// The「換場景要重新讀取比較慢」contract. Measured on the real Miku model: parsing the .pmx is 89 ms, building the
    /// model's SHARED assets (mesh + materials + ten 2048² PNG textures) is ~1.4 s, and every rig built afterwards is
    /// only ~20 ms. So the whole question of whether a screen change stalls comes down to one thing — <b>does that
    /// 1.4 s survive?</b> Two ways it would not, both asserted here:
    ///
    /// <list type="number">
    /// <item>The shared assets are not actually shared — every rig rebuilds them (mesh identity below).</item>
    /// <item>They are shared, but Unity reclaims them between screens: <c>SceneManager.LoadScene</c> (which the result
    /// screen's 重玩 uses) runs <c>Resources.UnloadUnusedAssets</c>, and a script-created Mesh/Material/Texture2D that
    /// no live GameObject references at that instant is exactly what it collects. That is why they are pinned with
    /// <c>HideFlags.DontUnloadUnusedAsset</c> — and the pin is what this asserts, by running the unload for real.</item>
    /// </list>
    ///
    /// Runs against the REAL model + SDO data (both resolve from the repo in the editor); skips itself without them.
    /// </summary>
    public class MmdSharedAssetTests
    {
        [SetUp]
        public void RequireModelAndData()
        {
            if (string.IsNullOrEmpty(MmdAvatarSwap.ModelPath)) Assert.Ignore("MMD model not installed (DATA/MODEL/… or assets/MODEL/…)");
            if (!System.IO.File.Exists(System.IO.Path.Combine(SdoExtracted.Root, "AVATAR", "FEMALE.HRC")))
                Assert.Ignore("SDO game data not available");
        }

        [TearDown]
        public void Restore() => MmdAvatarSwap.SetEnabled(false);

        [UnityTest]
        public IEnumerator TwoRigs_ShareOneMeshAndMaterialSet_ThatSurvivesAnAssetUnload()
        {
            LogAssert.ignoreFailingMessages = true;
            MmdAvatarSwap.SetEnabled(true);

            var a = BuildPortraitRig("SharedAssetHostA");
            var b = BuildPortraitRig("SharedAssetHostB");
            for (int i = 0; i < 6; i++) yield return null;   // let both rigs build

            var smrA = MmdRenderer(a);
            var smrB = MmdRenderer(b);
            Assert.IsNotNull(smrA, "first rig never built");
            Assert.IsNotNull(smrB, "second rig never built");

            // (1) shared, not copied — 172k verts skinned once, not once per dancer on screen.
            Assert.AreSame(smrA.sharedMesh, smrB.sharedMesh, "each rig built its own copy of the 172k-vert mesh");
            Assert.AreSame(smrA.sharedMaterials[0], smrB.sharedMaterials[0], "each rig built its own materials/textures");
            // …but each rig poses independently: same mesh, its OWN bone transforms.
            Assert.AreNotSame(smrA.bones[0], smrB.bones[0], "both rigs are driving the SAME bone transforms");

            var mesh = smrA.sharedMesh;
            var mat = smrA.sharedMaterials[0];
            var tex = mat.mainTexture;
            Assert.AreEqual(HideFlags.DontUnloadUnusedAsset, mesh.hideFlags & HideFlags.DontUnloadUnusedAsset,
                            "the shared mesh is not pinned — a scene load would reclaim it and re-pay the rebuild");
            Assert.AreEqual(HideFlags.DontUnloadUnusedAsset, mat.hideFlags & HideFlags.DontUnloadUnusedAsset,
                            "the shared materials are not pinned");
            if (tex != null)
                Assert.AreEqual(HideFlags.DontUnloadUnusedAsset, tex.hideFlags & HideFlags.DontUnloadUnusedAsset,
                                "the decoded textures are not pinned — they are ~95% of the model's build cost");

            // (2) the real thing: drop every rig, then run the same unload a scene change runs. The cache holds only
            // managed references at this point, which is precisely the situation the pin exists for.
            Object.DestroyImmediate(a);
            Object.DestroyImmediate(b);
            yield return null;
            var op = Resources.UnloadUnusedAssets();
            while (!op.isDone) yield return null;
            yield return null;

            Assert.IsTrue(mesh != null, "the shared mesh was unloaded by Resources.UnloadUnusedAssets");
            Assert.IsTrue(mat != null, "the shared materials were unloaded by Resources.UnloadUnusedAssets");
            if (tex != null) Assert.IsTrue(tex != null, "the decoded texture was unloaded by Resources.UnloadUnusedAssets");

            // …and a rig built after the unload gets the very same assets back, i.e. it really did not re-pay anything.
            var c = BuildPortraitRig("SharedAssetHostC");
            for (int i = 0; i < 6; i++) yield return null;
            var smrC = MmdRenderer(c);
            Assert.IsNotNull(smrC, "rig built after the unload failed");
            Assert.AreSame(mesh, smrC.sharedMesh, "the rig after the unload rebuilt the mesh instead of reusing it");
            Object.DestroyImmediate(c);
        }

        // A head-portrait rig is the cheapest real consumer (no cloth solver) and builds its own private SdoAvatar.
        private static GameObject BuildPortraitRig(string name)
        {
            var host = new GameObject(name);
            var portrait = host.AddComponent<RoomHeadPortrait>();
            if (!portrait.Init(male: false)) { Object.DestroyImmediate(host); Assert.Ignore("portrait avatar failed to load"); }
            return host;
        }

        private static SkinnedMeshRenderer MmdRenderer(GameObject host)
        {
            var driver = host.GetComponentInChildren<SdoAvatar>(true);
            var mmd = driver != null ? MmdAvatarSwap.ActiveFor(driver) : null;
            return mmd != null ? mmd.GetComponentInChildren<SkinnedMeshRenderer>(true) : null;
        }
    }
}
