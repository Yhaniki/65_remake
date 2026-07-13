using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// End-to-end check that the MMD display swap (F7 / <c>-mmd</c>) reaches the avatars that are NOT the stage dancer:
    /// the room 頭貼 and the 男/女 select preview (the 結算 left headshot is the same RoomHeadPortrait-style rig, driven by
    /// <see cref="MmdAvatar.TryHeadBoundsRest"/>, which is asserted here too). These three render a PRIVATE avatar into a
    /// RenderTexture through a layer-culled camera, so the two ways this can silently break are (a) the MMD rig lands on
    /// the wrong layer and the portrait camera renders an empty texture, and (b) the portrait camera keeps framing the
    /// hidden SDO head and zooms out to a whole body, because an MMD model has no FACE/HAIR renderers to measure.
    ///
    /// Runs against the REAL Miku .pmx and the REAL SDO data (both resolve from the repo in the editor); skips itself if
    /// either is absent.
    /// </summary>
    public class MmdPortraitSwapTests
    {
        [SetUp]
        public void RequireModelAndData()
        {
            if (string.IsNullOrEmpty(MmdDebug.ModelPath)) Assert.Ignore("MMD model not installed (assets/IkaHatunemiku2025)");
            if (!System.IO.File.Exists(System.IO.Path.Combine(SdoExtracted.Root, "AVATAR", "FEMALE.HRC")))
                Assert.Ignore("SDO game data not available");
        }

        [TearDown]
        public void Restore() => MmdDebug.SetEnabled(false);

        [UnityTest]
        public IEnumerator RoomHeadPortrait_SwapsToMmd_AndTheCamFramesTheHeadNotTheWholeBody()
        {
            LogAssert.ignoreFailingMessages = true;
            MmdDebug.SetEnabled(true);

            var host = new GameObject("HeadPortraitHost");
            var portrait = host.AddComponent<RoomHeadPortrait>();
            Assert.IsTrue(portrait.Init(male: false), "portrait avatar failed to load");
            for (int i = 0; i < 5; i++) yield return null;   // let the rig build + the first CPU skin land

            var driver = host.GetComponentInChildren<SdoAvatar>(true);
            var mmd = MmdDebug.ActiveFor(driver);
            Assert.IsNotNull(mmd, "the 頭貼 avatar did not swap to the MMD body");

            // (a) layer: the portrait cam culls to `portrait.layer` — a rig left on the default layer renders nothing.
            var smr = mmd.GetComponentInChildren<SkinnedMeshRenderer>(true);
            Assert.IsNotNull(smr);
            Assert.IsTrue(smr.enabled, "MMD mesh not shown");
            Assert.AreEqual(portrait.layer, smr.gameObject.layer, "MMD mesh is not on the head-portrait layer");
            foreach (var mr in driver.GetComponentsInChildren<MeshRenderer>(true))
                Assert.IsFalse(mr.enabled, "the SDO body is still drawn behind the MMD one");

            // (a2) no cloth solver on a portrait rig — the sway is invisible at 192×152 and the solver is the costliest
            // part of a build. The hair rides the head in its styled rest pose instead.
            Assert.IsFalse(mmd.HasCloth, "the 頭貼 rig built a hair/skirt sim it will never visibly use");

            // (b) framing: the head box must be HEAD-sized. Miku's twintails hang off head-child bones down to the waist,
            // so a naive "head bone subtree" AABB would be most of the body — the portrait would show a full figure.
            Assert.IsTrue(mmd.TryHeadBounds(out var head), "no MMD head bounds");
            float body = smr.bounds.size.y;
            Assert.Greater(body, 0.1f);
            Assert.Less(head.size.y, 0.45f * body, $"head box {head.size.y:F1} is body-sized (body {body:F1}) — twintails not clipped");
            Assert.Greater(head.size.y, 0.08f * body, "head box collapsed to nothing");
            // the head sits in the TOP of the body (a portrait aimed at the middle would show the chest)
            Assert.Greater(head.center.y, smr.bounds.center.y + 0.2f * body, "head box is not in the upper body");

            // the fixed-cam variant (結算 headshot) must agree with the live one — same head, just the rest pose.
            Assert.IsTrue(mmd.TryHeadBoundsRest(out var rest), "no MMD rest head bounds");
            Assert.Less(Vector3.Distance(rest.center, head.center), 0.5f * head.size.y, "rest vs live head disagree");

            // (c) the camera actually SEES it: the head portrait RT must come back non-empty.
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                var cam = host.GetComponentInChildren<Camera>();
                Assert.IsNotNull(cam);
                cam.Render();
                Assert.Greater(OpaqueFraction((RenderTexture)portrait.Texture), 0.05f,
                    "the 頭貼 RenderTexture is (near) empty — the portrait cam is not seeing the MMD rig");
            }

            Object.Destroy(host);
            yield return null;
        }

        [UnityTest]
        public IEnumerator GenderPreview_SwapsBothGenders_IncludingTheOneThatWasParkedInactive()
        {
            LogAssert.ignoreFailingMessages = true;
            MmdDebug.SetEnabled(false);   // start on the SDO bodies …

            var host = new GameObject("GenderPreviewHost");
            var preview = host.AddComponent<GenderPreview3D>();
            preview.Build(gender: 0);     // … so the unselected (male) dancer is registered while INACTIVE
            for (int i = 0; i < 3; i++) yield return null;

            var avatars = host.GetComponentsInChildren<SdoAvatar>(true);
            Assert.AreEqual(2, avatars.Length, "expected a female and a male preview dancer");
            var shown = System.Array.Find(avatars, a => a.gameObject.activeInHierarchy);
            var parked = System.Array.Find(avatars, a => !a.gameObject.activeInHierarchy);
            Assert.IsNotNull(shown); Assert.IsNotNull(parked);

            MmdDebug.SetEnabled(true);
            for (int i = 0; i < 5; i++) yield return null;
            Assert.IsNotNull(MmdDebug.ActiveFor(shown), "the displayed preview did not swap to MMD");
            Assert.IsNull(MmdDebug.ActiveFor(parked), "the parked preview cannot build a rig while inactive");

            // switching gender shows the parked dancer → MmdDebug must build its rig on the next frame (deferred build)
            preview.SetGender(1);
            for (int i = 0; i < 5; i++) yield return null;
            Assert.IsNotNull(MmdDebug.ActiveFor(parked), "the other gender never swapped to MMD after being shown");
            var parkedSmr = MmdDebug.ActiveFor(parked).GetComponentInChildren<SkinnedMeshRenderer>(true);
            Assert.AreEqual(GenderPreview3D.PreviewLayer, parkedSmr.gameObject.layer);

            // Every rig of the same model shares ONE mesh + ONE material set (they only own their bones). Rebuilding the
            // 172k-vert mesh per rig is the thing this guards against — with 6 rigs alive that is 6× the mesh and 6× the
            // per-texture alpha scan. Shareable because MMD bindposes are rig-independent (see MmdAvatar.Shared).
            // the full-body preview DOES keep its hair/skirt physics (only the head portraits drop it)
            Assert.IsTrue(MmdDebug.ActiveFor(shown).HasCloth, "the full-body gender preview lost its cloth sim");

            var shownSmr = MmdDebug.ActiveFor(shown).GetComponentInChildren<SkinnedMeshRenderer>(true);
            Assert.AreSame(shownSmr.sharedMesh, parkedSmr.sharedMesh, "each rig rebuilt the mesh instead of sharing it");
            Assert.AreSame(shownSmr.sharedMaterials[0], parkedSmr.sharedMaterials[0], "each rig rebuilt the materials");
            Assert.Greater(shownSmr.sharedMesh.vertexCount, 1000);
            // …and they still pose INDEPENDENTLY off that one mesh (different genders → different idle clips → different pose)
            Assert.AreNotSame(shownSmr.bones, parkedSmr.bones);

            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                host.GetComponentInChildren<Camera>().Render();
                Assert.Greater(OpaqueFraction((RenderTexture)preview.PreviewTexture), 0.02f,
                    "the gender-preview RenderTexture is (near) empty — the preview cam is not seeing the MMD rig");
            }

            Object.Destroy(host);
            yield return null;
        }

        // Fraction of the RT that is not fully transparent — these RTs are alpha-cleared, so "the model is in frame".
        private static float OpaqueFraction(RenderTexture rt)
        {
            if (rt == null) return 0f;
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply(false);
            RenderTexture.active = prev;

            var px = tex.GetPixels32();
            int n = 0;
            foreach (var p in px) if (p.a > 25) n++;
            Object.Destroy(tex);
            return px.Length == 0 ? 0f : (float)n / px.Length;
        }
    }
}
