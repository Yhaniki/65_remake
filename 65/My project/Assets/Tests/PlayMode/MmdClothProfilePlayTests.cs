using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// End-to-end check of the per-model physics file: build a real MMD dancer off the real .pmx, drop a
    /// <c>physics.ini</c> into the model's folder, rebuild — the cloth must come up with the FILE's values; delete it and
    /// the cloth must fall back to the values converted from the .pmx.
    ///
    /// The unit tests (<see cref="MmdClothProfileTests"/>) cover the parsing/override rules; this one covers the wiring
    /// nobody else does: model folder → <see cref="MmdClothProfile.Load"/> → <see cref="MmdMagicaCloth"/> → the rig.
    /// Runs against the installed model + SDO data; skips itself if either is missing. Restores the model folder to
    /// exactly the state it found it in.
    /// </summary>
    public class MmdClothProfilePlayTests
    {
        private string _dir;                 // the selected model's folder
        private string _backup;              // the user's own physics.ini, if they had one

        [SetUp]
        public void RequireModelAndData()
        {
            if (string.IsNullOrEmpty(MmdAvatarSwap.ModelPath)) Assert.Ignore("MMD model not installed (DATA/MODEL/…)");
            if (!File.Exists(Path.Combine(SdoExtracted.Root, "AVATAR", "FEMALE.HRC"))) Assert.Ignore("SDO game data not available");
            _dir = Path.GetDirectoryName(MmdAvatarSwap.ModelPath);
            string path = MmdClothProfile.PathFor(_dir);
            _backup = File.Exists(path) ? File.ReadAllText(path) : null;
            if (_backup != null) File.Delete(path);   // start from "no file" = pure conversion
        }

        [TearDown]
        public void Restore()
        {
            MmdAvatarSwap.SetEnabled(false);
            string path = MmdClothProfile.PathFor(_dir);
            if (_backup != null) File.WriteAllText(path, _backup);
            else if (File.Exists(path)) File.Delete(path);
        }

        [UnityTest]
        public IEnumerator NoFile_ConvertsFromThePmx_ThenTheFileOverridesIt_ThenDeletingItReverts()
        {
            LogAssert.ignoreFailingMessages = true;

            var host = new GameObject("ClothProfileHost");
            var preview = host.AddComponent<GenderPreview3D>();
            preview.Build(gender: 0);
            MmdAvatarSwap.SetEnabled(true);
            for (int i = 0; i < 5; i++) yield return null;

            var driver = System.Array.Find(host.GetComponentsInChildren<SdoAvatar>(true), a => a.gameObject.activeInHierarchy);
            var mmd = MmdAvatarSwap.ActiveFor(driver);
            Assert.IsNotNull(mmd, "the preview dancer did not swap to the MMD body");
            var cloth = mmd.Cloth;
            if (cloth == null) Assert.Ignore("Magica Cloth 2 not installed — the cloth path can't be exercised");

            // ---- 1) no physics.ini → everything comes from the model's own rigid bodies/joints ----
            Assert.IsNull(cloth.ProfilePath, "a physics.ini leaked in from somewhere");
            var converted = FindPart(cloth, MmdClothPartId.Hair);
            Assert.Greater(converted.AngleStiffness, 0f, "the converted hair stiffness is empty");
            Assert.AreEqual(1f, cloth.CurrentColliderMul, 1e-4f);

            // ---- 2) drop a physics.ini in the model folder and rebuild → the FILE wins, key by key ----
            File.WriteAllText(MmdClothProfile.PathFor(_dir),
                "[global]\ncolliderRadiusMul = 1.5\n\n[hair]\nangleStiffness = 0.123\ngravityMul = 0.25\n");
            MmdAvatarSwap.Rebuild();
            for (int i = 0; i < 5; i++) yield return null;

            var mmd2 = MmdAvatarSwap.ActiveFor(driver);
            Assert.IsNotNull(mmd2, "the dancer lost its MMD body across the rebuild");
            var cloth2 = mmd2.Cloth;
            Assert.IsNotNull(cloth2);
            Assert.IsNotNull(cloth2.ProfilePath, "the rebuilt rig did not pick up the model's physics.ini");

            var tuned = FindPart(cloth2, MmdClothPartId.Hair);
            Assert.AreEqual(0.123f, tuned.AngleStiffness, 1e-3f, "the file's angleStiffness did not reach the cloth");
            Assert.AreEqual(0.25f, tuned.GravityMul, 1e-3f, "the file's gravityMul did not reach the cloth");
            Assert.AreEqual(1.5f, cloth2.CurrentColliderMul, 1e-3f, "the file's colliderRadiusMul did not reach the colliders");
            // a key the file never mentioned keeps the converted value
            Assert.AreEqual(converted.DepthInertia, tuned.DepthInertia, 1e-4f, "an un-mentioned key was clobbered instead of kept");

            // ---- 3) delete it → straight back to the converted values ----
            Assert.IsTrue(MmdAvatarSwap.DeleteProfile());
            for (int i = 0; i < 5; i++) yield return null;

            var cloth3 = MmdAvatarSwap.ActiveFor(driver).Cloth;
            Assert.IsNull(cloth3.ProfilePath, "still running a physics.ini after it was deleted");
            var back = FindPart(cloth3, MmdClothPartId.Hair);
            Assert.AreEqual(converted.AngleStiffness, back.AngleStiffness, 1e-4f);
            Assert.AreEqual(converted.GravityMul, back.GravityMul, 1e-4f);

            Object.Destroy(host);
            yield return null;
        }

        [UnityTest]
        public IEnumerator SaveProfile_WritesWhatTheDancerIsRunning_AndItReloadsTheSame()
        {
            LogAssert.ignoreFailingMessages = true;

            var host = new GameObject("ClothSaveHost");
            var preview = host.AddComponent<GenderPreview3D>();
            preview.Build(gender: 0);
            MmdAvatarSwap.SetEnabled(true);
            for (int i = 0; i < 5; i++) yield return null;

            var driver = System.Array.Find(host.GetComponentsInChildren<SdoAvatar>(true), a => a.gameObject.activeInHierarchy);
            var cloth = MmdAvatarSwap.ActiveFor(driver)?.Cloth;
            if (cloth == null) Assert.Ignore("Magica Cloth 2 not installed — the cloth path can't be exercised");

            var before = FindPart(cloth, MmdClothPartId.Hair);
            string path = MmdAvatarSwap.SaveProfile();
            Assert.IsNotNull(path, "physics.ini could not be written into the model folder");
            Assert.IsTrue(File.Exists(path));

            // rebuilding off the file we just wrote must reproduce the same rig (a save that changes the look is a bug)
            MmdAvatarSwap.Rebuild();
            for (int i = 0; i < 5; i++) yield return null;

            var after = MmdAvatarSwap.ActiveFor(driver).Cloth;
            Assert.IsNotNull(after.ProfilePath);
            var p = FindPart(after, MmdClothPartId.Hair);
            Assert.AreEqual(before.AngleStiffness, p.AngleStiffness, 1e-3f);
            Assert.AreEqual(before.GravityMul, p.GravityMul, 1e-3f);
            Assert.AreEqual(before.DampingTip, p.DampingTip, 1e-3f);
            Assert.AreEqual(before.AngleLimitDeg, p.AngleLimitDeg, 1e-2f);
            Assert.AreEqual(before.Connection == MmdClothConnection.Auto ? MmdClothConnection.Line : before.Connection,
                            p.Connection == MmdClothConnection.Auto ? MmdClothConnection.Line : p.Connection);

            Object.Destroy(host);
            yield return null;
        }

        private static MmdClothPart FindPart(MmdMagicaCloth cloth, MmdClothPartId id)
        {
            foreach (var kv in cloth.CurrentParts) if (kv.Key == id) return kv.Value;
            Assert.Fail("the model has no '" + id + "' cloth part");
            return default;
        }
    }
}
