using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// End-to-end check that the MMD display swap (config.ini <c>mmdEnabled</c> / <c>-mmd</c>) reaches the avatars that are NOT the stage dancer:
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
            if (string.IsNullOrEmpty(MmdAvatarSwap.ModelPath)) Assert.Ignore("MMD model not installed (assets/IkaHatunemiku2025)");
            if (!System.IO.File.Exists(System.IO.Path.Combine(SdoExtracted.Root, "AVATAR", "FEMALE.HRC")))
                Assert.Ignore("SDO game data not available");
        }

        [TearDown]
        public void Restore() => MmdAvatarSwap.SetEnabled(false);

        [UnityTest]
        public IEnumerator RoomHeadPortrait_SwapsToMmd_AndTheCamFramesTheHeadNotTheWholeBody()
        {
            LogAssert.ignoreFailingMessages = true;
            MmdAvatarSwap.SetEnabled(true);

            var host = new GameObject("HeadPortraitHost");
            var portrait = host.AddComponent<RoomHeadPortrait>();
            Assert.IsTrue(portrait.Init(male: false), "portrait avatar failed to load");
            for (int i = 0; i < 5; i++) yield return null;   // let the rig build + the first CPU skin land

            var driver = host.GetComponentInChildren<SdoAvatar>(true);
            var mmd = MmdAvatarSwap.ActiveFor(driver);
            Assert.IsNotNull(mmd, "the 頭貼 avatar did not swap to the MMD body");

            // (a) layer: the portrait cam culls to `portrait.layer` — a rig left on the default layer renders nothing.
            var smr = mmd.GetComponentInChildren<SkinnedMeshRenderer>(true);
            Assert.IsNotNull(smr);
            Assert.IsTrue(smr.enabled, "MMD mesh not shown");
            Assert.AreEqual(portrait.layer, smr.gameObject.layer, "MMD mesh is not on the head-portrait layer");
            foreach (var mr in driver.GetComponentsInChildren<MeshRenderer>(true))
                Assert.IsFalse(mr.enabled, "the SDO body is still drawn behind the MMD one");

            // (a2) 房間那顆頭貼**預設不建**布料:它整場都活著,而布料是 rig 最貴的一段。頭髮就維持造型姿勢
            // 跟著頭骨走。要擺動的那幾格(結算列)自己把 clothSim 打開 —— 見下面那個測試。
            Assert.IsFalse(mmd.HasCloth, "the 頭貼 rig built a hair/skirt sim it was not asked for");

            // (b) framing: the head box must be HEAD-sized. Miku's twintails hang off head-child bones down to the waist,
            // so a naive "head bone subtree" AABB would be most of the body — the portrait would show a full figure.
            Assert.IsTrue(mmd.TryHeadBounds(out var head), "no MMD head bounds");
            float body = smr.bounds.size.y;
            Assert.Greater(body, 0.1f);
            Assert.Less(head.size.y, 0.45f * body, $"head box {head.size.y:F1} is body-sized (body {body:F1}) — twintails not clipped");
            Assert.Greater(head.size.y, 0.08f * body, "head box collapsed to nothing");
            // the head sits in the TOP of the body (a portrait aimed at the middle would show the chest)
            Assert.Greater(head.center.y, smr.bounds.center.y + 0.2f * body, "head box is not in the upper body");

            // The cam LOCKS ONTO the head: it re-aims every frame, so the head holds the middle of the slot while the
            // body walks and sways under it. And because the box is the rest-measured head SIZE (not the posed AABB, whose
            // extents swell as the head turns), the framing distance must not breathe either.
            var cam = host.GetComponentInChildren<Camera>();
            float xMin = 9f, xMax = -9f, dMin = float.MaxValue, dMax = float.MinValue;
            for (int i = 0; i < 40; i++)
            {
                yield return null;
                Assert.IsTrue(mmd.TryHeadBounds(out var h));
                var vp = cam.WorldToViewportPoint(h.center);
                xMin = Mathf.Min(xMin, vp.x); xMax = Mathf.Max(xMax, vp.x);
                float d = Vector3.Distance(cam.transform.position, h.center);
                dMin = Mathf.Min(dMin, d); dMax = Mathf.Max(dMax, d);
            }
            Assert.AreEqual(0.5f, xMin, 0.02f, "the head drifted off-centre — the cam is not locked onto it");
            Assert.AreEqual(0.5f, xMax, 0.02f, "the head drifted off-centre — the cam is not locked onto it");
            Assert.Less(dMax - dMin, 0.02f * dMax, "the framing distance is breathing — the head box is not a fixed size");

            // (c) the camera actually SEES it: the head portrait RT must come back non-empty.
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                Assert.IsNotNull(cam);
                cam.Render();
                Assert.Greater(OpaqueFraction((RenderTexture)portrait.Texture), 0.05f,
                    "the 頭貼 RenderTexture is (near) empty — the portrait cam is not seeing the MMD rig");
            }

            Object.Destroy(host);
            yield return null;
        }

        /// <summary>
        /// 結算列那幾格頭貼(<c>ScreenGameplay.AttachResultHeadPortraits</c> / <c>BuildIdleHeadAvatar</c>)是
        /// <c>clothSim = true</c> 建的,MMD 身體上的頭髮/裙子才會擺動。
        ///
        /// 沒有布料時症狀不是「少了一點細節」而是**頭髮完全不動**:<see cref="MmdAvatar"/> 的 retarget 會整組
        /// 跳過 physics 骨(那些骨本來就是留給布料解算的),於是它們停在 rest 姿勢剛性地跟著頭骨走
        /// (使用者回報:「遠端玩家大頭貼換 MMD 的沒有擺動」)。
        /// </summary>
        [UnityTest]
        public IEnumerator ResultRowPortrait_WithClothSim_BuildsTheHairSim()
        {
            LogAssert.ignoreFailingMessages = true;
            MmdAvatarSwap.SetEnabled(true);

            var host = new GameObject("ResultHeadPortraitHost");
            var portrait = host.AddComponent<RoomHeadPortrait>();
            // 結算列的設定(見 AttachResultHeadPortraits):只對頭骨取景、地面 clip、192×216、**要布料**
            portrait.clothSim = true;
            portrait.boneFraming = true;
            portrait.groundClipsOnly = true;
            portrait.rtWidth = 192; portrait.rtHeight = 216;
            Assert.IsTrue(portrait.Init(male: false), "portrait avatar failed to load");
            for (int i = 0; i < 5; i++) yield return null;

            var driver = host.GetComponentInChildren<SdoAvatar>(true);
            var mmd = MmdAvatarSwap.ActiveFor(driver);
            Assert.IsNotNull(mmd, "the 結算頭貼 avatar did not swap to the MMD body");
            Assert.IsTrue(mmd.HasCloth, "結算頭貼 built no hair sim — the MMD hair can only ride the head rigidly");

            Object.Destroy(host);
            yield return null;
        }

        [UnityTest]
        public IEnumerator GenderPreview_SwapsBothGenders_IncludingTheOneThatWasParkedInactive()
        {
            LogAssert.ignoreFailingMessages = true;
            MmdAvatarSwap.SetEnabled(false);   // start on the SDO bodies …

            var host = new GameObject("GenderPreviewHost");
            var preview = host.AddComponent<GenderPreview3D>();
            preview.Build(gender: 0);     // … so the unselected (male) dancer is registered while INACTIVE
            for (int i = 0; i < 3; i++) yield return null;

            var avatars = host.GetComponentsInChildren<SdoAvatar>(true);
            Assert.AreEqual(2, avatars.Length, "expected a female and a male preview dancer");
            var shown = System.Array.Find(avatars, a => a.gameObject.activeInHierarchy);
            var parked = System.Array.Find(avatars, a => !a.gameObject.activeInHierarchy);
            Assert.IsNotNull(shown); Assert.IsNotNull(parked);

            MmdAvatarSwap.SetEnabled(true);
            for (int i = 0; i < 5; i++) yield return null;
            Assert.IsNotNull(MmdAvatarSwap.ActiveFor(shown), "the displayed preview did not swap to MMD");
            Assert.IsNull(MmdAvatarSwap.ActiveFor(parked), "the parked preview cannot build a rig while inactive");

            // switching gender shows the parked dancer → MmdAvatarSwap must build its rig on the next frame (deferred build)
            preview.SetGender(1);
            for (int i = 0; i < 5; i++) yield return null;
            Assert.IsNotNull(MmdAvatarSwap.ActiveFor(parked), "the other gender never swapped to MMD after being shown");
            var parkedSmr = MmdAvatarSwap.ActiveFor(parked).GetComponentInChildren<SkinnedMeshRenderer>(true);
            Assert.AreEqual(GenderPreview3D.PreviewLayer, parkedSmr.gameObject.layer);

            // Every rig of the same model shares ONE mesh + ONE material set (they only own their bones). Rebuilding the
            // 172k-vert mesh per rig is the thing this guards against — with 6 rigs alive that is 6× the mesh and 6× the
            // per-texture alpha scan. Shareable because MMD bindposes are rig-independent (see MmdAvatar.Shared).
            // the full-body preview DOES keep its hair/skirt physics (only the head portraits drop it)
            Assert.IsTrue(MmdAvatarSwap.ActiveFor(shown).HasCloth, "the full-body gender preview lost its cloth sim");

            var shownSmr = MmdAvatarSwap.ActiveFor(shown).GetComponentInChildren<SkinnedMeshRenderer>(true);
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
