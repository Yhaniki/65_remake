using System.Collections;
using NUnit.Framework;
using Sdo.Game;
using UnityEngine;
using UnityEngine.TestTools;

namespace Sdo.Tests
{
    public class HeadMarkerDepthTest
    {
        private const int Layer = 4;

        [Test]
        public void WorldPlane_Preserves_Projection_And_Design_Pixel_Size()
        {
            var go = new GameObject("HeadMarkerPlaneCam");
            var cam = go.AddComponent<Camera>();
            cam.enabled = false;
            cam.orthographic = false;
            cam.fieldOfView = 45f;
            cam.aspect = 4f / 3f;
            cam.nearClipPlane = 5f;
            cam.transform.position = new Vector3(15f, 30f, -120f);
            cam.transform.rotation = Quaternion.Euler(8f, -12f, 0f);
            try
            {
                Vector3 anchor = cam.transform.position + cam.transform.forward * 400f
                               + cam.transform.right * 35f + cam.transform.up * 20f;
                var plane = HeadMarker.SolveWorldPlane(cam, anchor, 0f);
                Assert.IsTrue(plane.Valid);

                Vector3 before = cam.WorldToViewportPoint(anchor);
                Vector3 after = cam.WorldToViewportPoint(plane.Position);
                Assert.AreEqual(before.x, after.x, 1e-5f);
                Assert.AreEqual(before.y, after.y, 1e-5f);

                Vector3 oneGlyphUp = plane.Position + cam.transform.up * (22f * plane.Scale);
                float projectedDesignPx = (cam.WorldToViewportPoint(oneGlyphUp).y - after.y) * SdoLayout.Height;
                Assert.AreEqual(22f, projectedDesignPx, 0.02f);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Existing_Hud_Name_Can_Be_Promoted_To_The_Scene_Depth_Layer()
        {
            var markerGo = new GameObject("PromotedHeadMarker");
            GameObject nameRoot = null;
            try
            {
                var marker = markerGo.AddComponent<HeadMarker>();
                marker.Init(null, "PROMOTE");
                Assert.IsFalse(marker.DepthTestedWorld);

                marker.EnableDepthTestedWorld(Layer);
                Assert.IsTrue(marker.DepthTestedWorld);
                Assert.AreEqual(Layer, markerGo.layer);
                marker.SetName("PROMOTED"); // force tracked-text cells to rebuild after the promotion

                nameRoot = marker.NameRootForTest;
                Assert.IsNotNull(nameRoot);
                var renderers = nameRoot.GetComponentsInChildren<MeshRenderer>();
                Assert.Greater(renderers.Length, 0);
                foreach (var renderer in renderers)
                {
                    Assert.AreEqual(Layer, renderer.gameObject.layer);
                    Assert.IsNotNull(renderer.sharedMaterial);
                    Assert.AreEqual(TextStyles.DepthTextShaderName, renderer.sharedMaterial.shader.name);
                    // 本機名牌是先建 HUD 版再 promote 的 —— HUD 那組負畫序若沒跟著換掉,搬進場景相機後
                    // 會排在透明批之前,被後面的噴水池水花整片蓋掉(見 HeadMarker.WorldNameOrder)。
                    Assert.Greater(renderer.sortingOrder, 0,
                        "promote 之後仍留著 HUD 的負 sortingOrder,透明場景物會蓋掉名牌");
                }
                Assert.AreEqual(HeadMarker.WorldNameOrder, MaxOrder(renderers),
                    "字面應該拿 WorldNameOrder(黑邊是它 −1)");
            }
            finally
            {
                if (nameRoot != null) Object.DestroyImmediate(nameRoot);
                Object.DestroyImmediate(markerGo);
            }
        }

        [UnityTest]
        public IEnumerator Destroying_Marker_Cleans_Its_Independent_Name_Root()
        {
            var markerGo = new GameObject("OwnedHeadMarker");
            GameObject nameRoot = null;
            try
            {
                var marker = markerGo.AddComponent<HeadMarker>();
                marker.Init(null, "OWNED");
                nameRoot = marker.NameRootForTest;
                Assert.IsNotNull(nameRoot);

                Object.Destroy(markerGo);
                yield return null;
                yield return null;

                Assert.IsTrue(nameRoot == null,
                    "destroying HeadMarker left its separately-rooted Label3D orphaned in the scene");
            }
            finally
            {
                if (nameRoot != null) Object.DestroyImmediate(nameRoot);
                if (markerGo != null) Object.DestroyImmediate(markerGo);
            }
        }

        [UnityTest]
        public IEnumerator Depth_Shader_Preserves_The_Source_Sprite_Color()
        {
            GameObject camGo = null, spriteGo = null;
            RenderTexture rt = null;
            Texture2D texture = null;
            Sprite sprite = null;
            Material material = null;
            try
            {
                camGo = new GameObject("DepthTextColorCam");
                var cam = camGo.AddComponent<Camera>();
                cam.enabled = false;
                cam.orthographic = true;
                cam.orthographicSize = 2f;
                cam.cullingMask = 1 << Layer;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.transform.position = new Vector3(0f, 0f, -10f);

                rt = new RenderTexture(64, 64, 24, RenderTextureFormat.ARGB32);
                rt.Create();
                cam.targetTexture = rt;

                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                texture.filterMode = FilterMode.Point;
                texture.SetPixels32(new[]
                {
                    new Color32(210, 35, 20, 255), new Color32(210, 35, 20, 255),
                    new Color32(210, 35, 20, 255), new Color32(210, 35, 20, 255)
                });
                texture.Apply();
                sprite = Sprite.Create(texture, new Rect(0f, 0f, 2f, 2f), Vector2.one * 0.5f, 1f);

                var shader = Shader.Find(TextStyles.DepthTextShaderName);
                Assert.IsNotNull(shader);
                material = new Material(shader);
                spriteGo = new GameObject("ColoredDepthSprite");
                spriteGo.layer = Layer;
                var renderer = spriteGo.AddComponent<SpriteRenderer>();
                renderer.sprite = sprite;
                renderer.sharedMaterial = material;
                renderer.color = Color.white;

                yield return null;

                var pixels = Render(cam, rt);
                Color32 center = pixels[(rt.height / 2) * rt.width + rt.width / 2];
                Assert.Greater(center.r, 150, "the source sprite's red channel was lost");
                Assert.Less(center.g, 80, "the depth shader replaced the sprite RGB with white");
                Assert.Less(center.b, 80, "the depth shader replaced the sprite RGB with white");
            }
            finally
            {
                if (spriteGo != null) Object.DestroyImmediate(spriteGo);
                if (material != null) Object.DestroyImmediate(material);
                if (sprite != null) Object.DestroyImmediate(sprite);
                if (texture != null) Object.DestroyImmediate(texture);
                if (camGo != null) Object.DestroyImmediate(camGo);
                if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); }
            }
        }

        [UnityTest]
        public IEnumerator World_Name_Is_Occluded_By_Geometry_In_The_Scene_Depth_Buffer()
        {
            GameObject camGo = null, markerGo = null, nameRoot = null, blocker = null;
            RenderTexture rt = null;
            try
            {
                camGo = new GameObject("HeadMarkerDepthCam");
                var cam = camGo.AddComponent<Camera>();
                cam.enabled = false;
                cam.orthographic = false;
                cam.fieldOfView = 45f;
                cam.aspect = 4f / 3f;
                cam.nearClipPlane = 5f;
                cam.farClipPlane = 200f;
                cam.cullingMask = 1 << Layer;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

                rt = new RenderTexture(800, 600, 24, RenderTextureFormat.ARGB32);
                rt.Create();
                cam.targetTexture = rt;

                markerGo = new GameObject("DepthHeadMarker");
                var marker = markerGo.AddComponent<HeadMarker>();
                marker.upWorld = 0f;
                marker.nameFontPx = 60f;
                marker.AnchorGetter = () => new Vector3(0f, 0f, 100f);
                marker.CamGetter = () => cam;
                marker.Init(null, "DEPTH", depthTestedWorld: true, worldLayer: Layer);

                yield return null;
                yield return null;

                nameRoot = marker.NameRootForTest;
                Assert.IsNotNull(nameRoot);
                var renderers = nameRoot.GetComponentsInChildren<MeshRenderer>();
                Assert.Greater(renderers.Length, 0);
                foreach (var renderer in renderers)
                {
                    Assert.AreEqual(Layer, renderer.gameObject.layer);
                    Assert.IsNotNull(renderer.sharedMaterial);
                    Assert.AreEqual(TextStyles.DepthTextShaderName, renderer.sharedMaterial.shader.name);
                }

                int before = CountCream(Render(cam, rt));
                Assert.Greater(before, 300, "depth-tested name did not render");

                blocker = GameObject.CreatePrimitive(PrimitiveType.Quad);
                blocker.layer = Layer;
                blocker.transform.position = new Vector3(0f, 0f, 50f);
                blocker.transform.rotation = cam.transform.rotation;
                blocker.transform.localScale = Vector3.one * 100f;
                blocker.GetComponent<MeshRenderer>().sharedMaterial =
                    new Material(Shader.Find("Unlit/Color")) { color = new Color32(20, 100, 20, 255) };
                yield return null;

                int after = CountCream(Render(cam, rt));
                Assert.Less(after, before * 0.02f,
                    "the name still drew through opaque geometry, so it is not sharing SceneCam depth");
            }
            finally
            {
                if (blocker != null)
                {
                    var renderer = blocker.GetComponent<MeshRenderer>();
                    if (renderer != null && renderer.sharedMaterial != null)
                        Object.DestroyImmediate(renderer.sharedMaterial);
                    Object.DestroyImmediate(blocker);
                }
                if (nameRoot != null) Object.DestroyImmediate(nameRoot);
                if (markerGo != null) Object.DestroyImmediate(markerGo);
                if (camGo != null) Object.DestroyImmediate(camGo);
                if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); }
            }
        }

        /// <summary>
        /// 場景裡的透明物(SCN0025 春天噴水池的水就是這種:官方旗標透明批 → 一般 alpha blend、
        /// sortingOrder 0)不准蓋掉名牌 —— 就算它在名牌**後面**。
        ///
        /// 名牌沿用 HUD 那組負 sortingOrder 時,它會排在整個透明批之前畫,後畫的水直接整片糊上去
        /// (名牌不寫深度,「比較近」救不了它)。使用者回報:人站在噴水池前面,名牌卻被後面的水擋住。
        /// </summary>
        [UnityTest]
        public IEnumerator World_Name_Is_Not_Painted_Over_By_Transparent_Scene_Geometry_Behind_It()
        {
            GameObject camGo = null, markerGo = null, nameRoot = null, water = null;
            RenderTexture rt = null;
            Texture2D waterTex = null;
            Material waterMat = null;
            try
            {
                camGo = new GameObject("HeadMarkerWaterCam");
                var cam = camGo.AddComponent<Camera>();
                cam.enabled = false;
                cam.orthographic = false;
                cam.fieldOfView = 45f;
                cam.aspect = 4f / 3f;
                cam.nearClipPlane = 5f;
                cam.farClipPlane = 400f;
                cam.cullingMask = 1 << Layer;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

                rt = new RenderTexture(800, 600, 24, RenderTextureFormat.ARGB32);
                rt.Create();
                cam.targetTexture = rt;

                markerGo = new GameObject("WaterHeadMarker");
                var marker = markerGo.AddComponent<HeadMarker>();
                marker.upWorld = 0f;
                marker.nameFontPx = 60f;
                marker.AnchorGetter = () => new Vector3(0f, 0f, 100f);
                marker.CamGetter = () => cam;
                marker.Init(null, "DEPTH", depthTestedWorld: true, worldLayer: Layer);

                yield return null;
                yield return null;

                nameRoot = marker.NameRootForTest;
                Assert.IsNotNull(nameRoot);
                int before = CountCream(Render(cam, rt));
                Assert.Greater(before, 300, "depth-tested name did not render");

                // 噴水池的水:名牌 BEHIND 20 個單位、整片蓋住畫面、Queue Transparent / ZWrite Off、
                // sortingOrder 0(場景 mapobj 就是這個預設值)。
                waterTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                waterTex.SetPixels32(new[]
                {
                    new Color32(30, 120, 200, 230), new Color32(30, 120, 200, 230),
                    new Color32(30, 120, 200, 230), new Color32(30, 120, 200, 230)
                });
                waterTex.Apply();
                waterMat = new Material(Shader.Find("Unlit/Transparent")) { mainTexture = waterTex };
                water = GameObject.CreatePrimitive(PrimitiveType.Quad);
                water.layer = Layer;
                water.transform.position = new Vector3(0f, 0f, 120f);   // 名牌(z=100)之後
                water.transform.rotation = cam.transform.rotation;
                var waterRenderer = water.GetComponent<MeshRenderer>();
                waterRenderer.sharedMaterial = waterMat;
                waterRenderer.sortingOrder = 0;
                water.transform.localScale = Vector3.one * 200f;
                yield return null;

                int after = CountCream(Render(cam, rt));
                Assert.Greater(after, before * 0.8f,
                    "後面的透明場景物(噴水池的水)把名牌蓋掉了 —— 名牌的 sortingOrder 又排到透明批之前了");
            }
            finally
            {
                if (water != null) Object.DestroyImmediate(water);
                if (waterMat != null) Object.DestroyImmediate(waterMat);
                if (waterTex != null) Object.DestroyImmediate(waterTex);
                if (nameRoot != null) Object.DestroyImmediate(nameRoot);
                if (markerGo != null) Object.DestroyImmediate(markerGo);
                if (camGo != null) Object.DestroyImmediate(camGo);
                if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); }
            }
        }

        private static Color32[] Render(Camera cam, RenderTexture rt)
        {
            cam.Render();
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0f, 0f, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;
            var pixels = tex.GetPixels32();
            Object.DestroyImmediate(tex);
            return pixels;
        }

        private static int MaxOrder(MeshRenderer[] renderers)
        {
            int max = int.MinValue;
            foreach (var r in renderers) if (r.sortingOrder > max) max = r.sortingOrder;
            return max;
        }

        private static int CountCream(Color32[] pixels)
        {
            int count = 0;
            foreach (var pixel in pixels)
                if (pixel.r > 170 && pixel.g > 170 && pixel.b > 120) count++;
            return count;
        }
    }
}
