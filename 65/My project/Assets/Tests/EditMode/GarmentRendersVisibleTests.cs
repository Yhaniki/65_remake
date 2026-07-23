using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// END-TO-END pixel guard: a garment the official data marks TRANSPARENT must still SHOW UP. Classification tests
    /// (<see cref="GarmentAlphaRealDataTests"/>) prove we read the official flag correctly; they cannot catch the next
    /// failure mode — the blend RENDER path making the garment invisible (使用者回報:「大部分有透明度的衣服都壞了」,
    /// 商城格子整片空白 + 身上看不到上衣). So this builds the real avatar through the real card pipeline
    /// (<see cref="SdoRoomAvatar.Build"/>), renders it with a card-like orthographic camera into a transparent RT, and
    /// asserts the garment actually covers pixels.
    ///
    /// Runs in EditMode (Camera.Render works without play mode). Skips itself when the game data isn't present.
    /// </summary>
    public class GarmentRendersVisibleTests
    {
        private const int RtW = 128, RtH = 128;

        /// <summary>A garment mesh + how much of the frame it must cover, plus what makes it interesting.</summary>
        public struct Case
        {
            public string Msh; public float MinCoverage; public string Why;
            public override string ToString() => Msh;
        }

        private static readonly Case[] Cases =
        {
            // Official blend (bit1) garments — the class the user reported as broken.
            new Case { Msh = "037888_WOMAN_ONE.MSH",  MinCoverage = 0.02f, Why = "眉画犹思:官方 blend,72% 貼圖不透明 → 必須看得見" },
            new Case { Msh = "024976_WOMAN_ONE.MSH",  MinCoverage = 0.02f, Why = "Flower Lace:blend 蕾絲外層 + opaque 內襯" },
            new Case { Msh = "037000_WOMAN_COAT.MSH", MinCoverage = 0.01f, Why = "牛奶裝:整件紗,最淡的一件也要有可見像素" },
            new Case { Msh = "001934_WOMAN_ONE.MSH",  MinCoverage = 0.02f, Why = "Purple Dress 1:blend 上衣 + opaque 兩層裙" },
            // Official opaque (flag 0/1) — the control group; if these ever vanish the failure is elsewhere.
            new Case { Msh = "003136_WOMAN_COAT.MSH", MinCoverage = 0.01f, Why = "紅色不羈牛仔:官方實心上衣" },
        };

        private static IEnumerable<Case> AllCases() => Cases;

        private static string AvatarDir()
        {
            var probe = SdoAvatarBuilder.ResolveAvatarFile("AVATAR/" + Cases[0].Msh);
            if (string.IsNullOrEmpty(probe) || !File.Exists(probe)) return null;
            return Path.GetDirectoryName(probe);
        }

        [Test, TestCaseSource(nameof(AllCases))]
        public void OfficiallyTransparentGarment_StillRendersPixels(Case c)
        {
            if (AvatarDir() == null) Assert.Ignore("AVATAR data root not found — needs the game data (data_root.txt)");

            GameObject root = null; RenderTexture rt = null; Camera cam = null; Texture2D shot = null;
            try
            {
                root = new GameObject("GarmentProbe");
                var av = SdoRoomAvatar.Build(root, 0, portraitOpaque: false, male: false,
                                             equippedParts: new[] { "AVATAR/" + c.Msh });
                Assert.IsNotNull(av, c.Msh + ": avatar build failed");
                av.enabled = false;

                // Frame whatever geometry loaded (the garment alone), like the shop card's ortho camera.
                var bounds = MergedBounds(root);
                Assert.Greater(bounds.size.magnitude, 0.01f, c.Msh + ": no renderer bounds — nothing was built");

                rt = new RenderTexture(RtW, RtH, 16, RenderTextureFormat.ARGB32);
                var camGo = new GameObject("GarmentProbeCam");
                cam = camGo.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = Mathf.Max(bounds.extents.x, bounds.extents.y) * 1.2f + 0.001f;
                cam.transform.position = bounds.center + new Vector3(0f, 0f, -Mathf.Max(10f, bounds.size.z * 4f));
                cam.transform.LookAt(bounds.center);
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = Mathf.Max(1000f, bounds.size.magnitude * 20f);
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);   // transparent, exactly like the card RT
                cam.targetTexture = rt;
                cam.Render();

                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                shot = new Texture2D(RtW, RtH, TextureFormat.RGBA32, false);
                shot.ReadPixels(new Rect(0, 0, RtW, RtH), 0, 0);
                shot.Apply();
                RenderTexture.active = prev;

                int lit = 0, strong = 0;
                var px = shot.GetPixels32();
                foreach (var p in px) { if (p.a > 25) lit++; if (p.a > 200) strong++; }
                float coverage = lit / (float)px.Length;
                Debug.Log($"[garment-probe] {c.Msh}: coverage={coverage:P2} strong(a>0.78)={strong / (float)px.Length:P2}");

                Assert.GreaterOrEqual(coverage, c.MinCoverage,
                    $"{c.Msh} 幾乎沒畫出任何東西 (coverage {coverage:P2} < {c.MinCoverage:P2}) — {c.Why}\n" +
                    "官方標成透明的衣服在我們的 blend 路徑上變隱形 = 使用者回報的「有透明度的衣服都壞了」。");
            }
            finally
            {
                if (cam != null) { cam.targetTexture = null; Object.DestroyImmediate(cam.gameObject); }
                if (shot != null) Object.DestroyImmediate(shot);
                if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); }
                if (root != null) Object.DestroyImmediate(root);
            }
        }

        /// <summary>The SHOP CARD pipeline end-to-end: the same build, then the card's own post-processing (force the
        /// cutout shader on everything except sheer, hide the skin ranges) before rendering into the transparent RT.
        /// This is the configuration the user saw empty (使用者截圖:上裝分頁一整排空格子). Post-processing is invoked on
        /// the REAL private methods by reflection so the test can't drift from the shipping code.</summary>
        [Test, TestCaseSource(nameof(AllCases))]
        public void OfficiallyTransparentGarment_StillRendersInShopCard(Case c)
        {
            if (AvatarDir() == null) Assert.Ignore("AVATAR data root not found");

            var shopType = typeof(Sdo.UI.Screens.ShopScreen);
            var applyCutout = shopType.GetMethod("ApplyCardCutoutShader", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var hideSkin = shopType.GetMethod("HideSkinSubmeshes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(applyCutout, "ShopScreen.ApplyCardCutoutShader not found — card pipeline was renamed, update this test");
            Assert.IsNotNull(hideSkin, "ShopScreen.HideSkinSubmeshes not found — card pipeline was renamed, update this test");

            GameObject root = null; RenderTexture rt = null; Camera cam = null; Texture2D shot = null;
            try
            {
                root = new GameObject("GarmentCardProbe");
                var av = SdoRoomAvatar.Build(root, 0, portraitOpaque: false, male: false,
                                             equippedParts: new[] { "AVATAR/" + c.Msh });
                Assert.IsNotNull(av, c.Msh + ": avatar build failed");
                av.enabled = false;

                applyCutout.Invoke(null, new object[] { root });      // 卡片:非紗材質一律 cutout
                hideSkin.Invoke(null, new object[] { root });         // 卡片:藏膚色 range,只留布料

                var bounds = MergedBounds(root);
                Assert.Greater(bounds.size.magnitude, 0.01f, c.Msh + ": no renderer bounds after card post-processing");

                rt = new RenderTexture(RtW, RtH, 16, RenderTextureFormat.ARGB32);
                var camGo = new GameObject("GarmentCardProbeCam");
                cam = camGo.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = Mathf.Max(bounds.extents.x, bounds.extents.y) * 1.2f + 0.001f;
                cam.transform.position = bounds.center + new Vector3(0f, 0f, -Mathf.Max(10f, bounds.size.z * 4f));
                cam.transform.LookAt(bounds.center);
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = Mathf.Max(1000f, bounds.size.magnitude * 20f);
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                cam.targetTexture = rt;
                cam.Render();

                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                shot = new Texture2D(RtW, RtH, TextureFormat.RGBA32, false);
                shot.ReadPixels(new Rect(0, 0, RtW, RtH), 0, 0);
                shot.Apply();
                RenderTexture.active = prev;

                int lit = 0, strong = 0;
                var px = shot.GetPixels32();
                foreach (var p in px) { if (p.a > 25) lit++; if (p.a > 200) strong++; }
                float coverage = lit / (float)px.Length;
                Debug.Log($"[garment-card-probe] {c.Msh}: coverage={coverage:P2} strong(a>0.78)={strong / (float)px.Length:P2}");

                Assert.GreaterOrEqual(coverage, c.MinCoverage,
                    $"{c.Msh} 在商城卡片管線下幾乎沒畫出東西 (coverage {coverage:P2}) — {c.Why}\n" +
                    "= 使用者截圖裡那一整排空格子。");
            }
            finally
            {
                if (cam != null) { cam.targetTexture = null; Object.DestroyImmediate(cam.gameObject); }
                if (shot != null) Object.DestroyImmediate(shot);
                if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); }
                if (root != null) Object.DestroyImmediate(root);
            }
        }

        /// <summary>Bisect WHERE a blend garment disappears: render the same built garment through progressively more
        /// of the real shop-card RT/camera configuration and report coverage for each step. The user's cards are empty
        /// while the plain probe above renders fine, so one of these knobs is the culprit.</summary>
        [Test]
        public void Bisect_CardRenderTargetConfiguration()
        {
            if (AvatarDir() == null) Assert.Ignore("AVATAR data root not found");

            foreach (var msh in new[] { "037888_WOMAN_ONE.MSH", "024976_WOMAN_ONE.MSH", "003136_WOMAN_COAT.MSH" })
            {
                var root = new GameObject("BisectProbe");
                var av = SdoRoomAvatar.Build(root, 0, portraitOpaque: false, male: false, equippedParts: new[] { "AVATAR/" + msh });
                if (av == null) { Object.DestroyImmediate(root); Debug.Log("[bisect] " + msh + ": build failed"); continue; }
                av.enabled = false;
                var b = MergedBounds(root);

                Debug.Log($"[bisect] {msh} plainRT={Coverage(root, b, 0, false):P2}  msaa2={Coverage(root, b, 2, false):P2}  " +
                          $"msaa2+cardClip={Coverage(root, b, 2, true):P2}");
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>Render the FULL dressed avatar (body + garment, the room/gameplay configuration) over an OPAQUE
        /// backdrop and save a PNG, so a "body is see-through" report can be looked at instead of guessed. Writes to
        /// the repo root as garment-probe-&lt;mesh&gt;.png. Diagnostic only — never fails.</summary>
        [Test]
        public void Snapshot_DressedAvatar()
        {
            if (AvatarDir() == null) Assert.Ignore("AVATAR data root not found");

            // The outfit the user is looking at: default WOMAN body with the 金姬兰 one-piece replacing coat+pant.
            var outfits = new (string label, string[] parts)[]
            {
                ("lace-dress", new[] { "AVATAR/900007_WOMAN_FACE.MSH", "AVATAR/900017_WOMAN_HAIR.MSH",
                                       "AVATAR/024976_WOMAN_ONE.MSH", "AVATAR/900020_WOMAN_SHOES.MSH",
                                       "AVATAR/900011_WOMAN_HAND.MSH" }),
                // solid cut-out cloth with a LOW NECKLINE — the case that must not read see-through at the back
                ("skirt-suit", new[] { "AVATAR/900007_WOMAN_FACE.MSH", "AVATAR/900017_WOMAN_HAIR.MSH",
                                       "AVATAR/001766_WOMAN_ONE.MSH", "AVATAR/900020_WOMAN_SHOES.MSH",
                                       "AVATAR/900011_WOMAN_HAND.MSH" }),
                // 使用者:「黑色魅力娃娃裙子中間不該有膚色穿透出來」
                ("doll-dress", new[] { "AVATAR/900007_WOMAN_FACE.MSH", "AVATAR/900017_WOMAN_HAIR.MSH",
                                       "AVATAR/003834_WOMAN_ONE.MSH", "AVATAR/900020_WOMAN_SHOES.MSH",
                                       "AVATAR/900011_WOMAN_HAND.MSH" }),
                // 使用者:「閃爍貼圖動畫沒去背」— 換幀幀是 TGA 的 SHINE 系列
                ("shine-purple", new[] { "AVATAR/900007_WOMAN_FACE.MSH", "AVATAR/900017_WOMAN_HAIR.MSH",
                                         "AVATAR/017246_WOMAN_ONE.MSH", "AVATAR/900020_WOMAN_SHOES.MSH",
                                         "AVATAR/900011_WOMAN_HAND.MSH" }),
                // 使用者:「京族新娘 透明度有明顯漸層」— 紗料 DXT3 4-bit alpha 階梯,AlphaSmooth 抹平
                ("jingzu-bride", new[] { "AVATAR/900007_WOMAN_FACE.MSH", "AVATAR/900017_WOMAN_HAIR.MSH",
                                         "AVATAR/002178_WOMAN_ONE.MSH", "AVATAR/900020_WOMAN_SHOES.MSH",
                                         "AVATAR/900011_WOMAN_HAND.MSH" }),
                // 使用者:「黑色魅力娃娃 裙子中間層有一段透明,那段貼圖不應該透明」— 去背荷葉裙,Cutout 才對
                ("doll-black", new[] { "AVATAR/900007_WOMAN_FACE.MSH", "AVATAR/900017_WOMAN_HAIR.MSH",
                                       "AVATAR/003834_WOMAN_ONE.MSH", "AVATAR/900020_WOMAN_SHOES.MSH",
                                       "AVATAR/900011_WOMAN_HAND.MSH" }),
                // 使用者:「fly pink butterfly 邊緣透明度沒做出來」— DXT1 黑底發光翅膀 → 加成
                ("butterfly-wing", new[] { "AVATAR/900007_WOMAN_FACE.MSH", "AVATAR/900017_WOMAN_HAIR.MSH",
                                           "AVATAR/900018_WOMAN_COAT.MSH", "AVATAR/900019_WOMAN_PANT.MSH",
                                           "AVATAR/900020_WOMAN_SHOES.MSH", "AVATAR/900011_WOMAN_HAND.MSH",
                                           "AVATAR/008448_WOMAN_CHIBANG.MSH" }),
                ("default-body", SdoRoomAvatar.WomanParts),
            };
            foreach (var (label, parts) in outfits)
            {
                var root = new GameObject("SnapProbe_" + label);
                var av = SdoRoomAvatar.Build(root, 0, portraitOpaque: false, male: false, equippedParts: parts);
                if (av == null) { Object.DestroyImmediate(root); Debug.Log("[snap] " + label + ": build failed"); continue; }
                av.enabled = false;
                int sheer = 0, opaque = 0;
                foreach (var mr in root.GetComponentsInChildren<MeshRenderer>())
                    foreach (var m in mr.sharedMaterials)
                        if (m != null)
                        {
                            bool isSheer = m.shader != null && m.shader.name == "Sdo/UnlitAvatarSheer";
                            if (isSheer) sheer++; else opaque++;
                            var tex = m.mainTexture as Texture2D;
                            Debug.Log($"[snap-mat] {label} {mr.name}: shader={m.shader?.name} queue={m.renderQueue} " +
                                      $"tex={(tex == null ? "NULL" : tex.width + "x" + tex.height)} " +
                                      $"prepass={(m.HasProperty("_PrepassZWrite") ? m.GetFloat("_PrepassZWrite").ToString("0.#") : "-")} " +
                                      $"density={(m.HasProperty("_Density") ? m.GetFloat("_Density").ToString("0.#") : "-")} " +
                                      $"enabled={mr.enabled} tris={(mr.GetComponent<MeshFilter>()?.sharedMesh?.triangles.Length ?? 0) / 3}");
                        }
                Debug.Log($"[snap] {label}: {sheer} blended materials, {opaque} others");
                SaveShot(root, "H:/65_remake/garment-probe-" + label + ".png");
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>Controlled bisect for "the blended garment draws nothing": same built dress, rendered three ways —
        /// as-is, with the depth prepass forced back ON, and with every sheer material swapped to the plain opaque
        /// shader. Whichever variant shows the dress tells us if the loss is geometry, depth, or the blend itself.</summary>
        [Test]
        public void Bisect_WhyBlendedDressVanishes()
        {
            if (AvatarDir() == null) Assert.Ignore("AVATAR data root not found");
            var parts = new[] { "AVATAR/024976_WOMAN_ONE.MSH" };

            foreach (var mode in new[] { "asis", "prepass-on", "opaque-shader", "alpha-one" })
            {
                var root = new GameObject("BisectDress_" + mode);
                var av = SdoRoomAvatar.Build(root, 0, portraitOpaque: false, male: false, equippedParts: parts);
                if (av == null) { Object.DestroyImmediate(root); continue; }
                av.enabled = false;
                var opaque = Shader.Find("Unlit/Texture");
                foreach (var mr in root.GetComponentsInChildren<MeshRenderer>())
                {
                    var mats = mr.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var m = mats[i];
                        if (m == null || m.shader == null || m.shader.name != "Sdo/UnlitAvatarSheer") continue;
                        if (mode == "prepass-on") m.SetFloat("_PrepassZWrite", 1f);
                        else if (mode == "opaque-shader") m.shader = opaque;
                        else if (mode == "alpha-one") m.SetFloat("_Density", 4f);
                    }
                    mr.sharedMaterials = mats;
                }
                SaveShot(root, "H:/65_remake/garment-bisect-" + mode + ".png");
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>使用者:「金姬兰 腳上破一個洞,官方沒有」. Render the dressed avatar as-is and with the sheer
        /// materials forced TWO-SIDED (Cull Off): if the hole fills in, it is back-face culling showing through a slit
        /// / the garment's inside; if it stays, the geometry or a material there is genuinely missing.</summary>
        [Test]
        public void Bisect_SkirtHole_CullingOrGeometry()
        {
            if (AvatarDir() == null) Assert.Ignore("AVATAR data root not found");
            var parts = new[] { "AVATAR/900007_WOMAN_FACE.MSH", "AVATAR/900017_WOMAN_HAIR.MSH",
                                "AVATAR/024976_WOMAN_ONE.MSH", "AVATAR/900020_WOMAN_SHOES.MSH",
                                "AVATAR/900011_WOMAN_HAND.MSH" };
            foreach (var mode in new[] { "asis", "cull-off", "no-blend-layers" })
            {
                var root = new GameObject("HoleProbe_" + mode);
                var av = SdoRoomAvatar.Build(root, 0, portraitOpaque: false, male: false, equippedParts: parts);
                if (av == null) { Object.DestroyImmediate(root); continue; }
                av.enabled = false;
                foreach (var mr in root.GetComponentsInChildren<MeshRenderer>())
                    foreach (var m in mr.sharedMaterials)
                    {
                        if (m == null || m.shader == null || m.shader.name != "Sdo/UnlitAvatarSheer") continue;
                        if (mode == "cull-off") m.SetInt("_Cull", 0);          // needs the shader to expose it; harmless otherwise
                        else if (mode == "no-blend-layers") m.SetFloat("_Density", 4f);
                    }
                SaveShot(root, "H:/65_remake/garment-hole-" + mode + ".png");
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>使用者:「背面可以看到前面,內褲位置前面可以看到後面」. Compare the sheer render states that a SINGLE
        /// pass can offer (URP draws only one untagged pass, so a depth prepass is not available) — front AND back view
        /// for each, since the artefact only shows from behind.</summary>
        [Test]
        public void Bisect_SheerCullZWriteStates()
        {
            if (AvatarDir() == null) Assert.Ignore("AVATAR data root not found");
            var parts = new[] { "AVATAR/900007_WOMAN_FACE.MSH", "AVATAR/900017_WOMAN_HAIR.MSH",
                                "AVATAR/024976_WOMAN_ONE.MSH", "AVATAR/900020_WOMAN_SHOES.MSH",
                                "AVATAR/900011_WOMAN_HAND.MSH" };
            var states = new (string label, float cull, float zwrite)[]
            {
                ("cullOff-zwOff", 0f, 0f),
                ("cullOff-zwOn",  0f, 1f),
                ("cullBack-zwOn", 2f, 1f),
                ("cullBack-zwOff", 2f, 0f),   // the original state: single-sided, layers accumulate
            };
            foreach (var (label, cull, zw) in states)
                foreach (bool back in new[] { false, true })
                {
                    var root = new GameObject("StateProbe_" + label);
                    var av = SdoRoomAvatar.Build(root, 0, portraitOpaque: false, male: false, equippedParts: parts);
                    if (av == null) { Object.DestroyImmediate(root); continue; }
                    av.enabled = false;
                    if (back) root.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
                    foreach (var mr in root.GetComponentsInChildren<MeshRenderer>())
                        foreach (var m in mr.sharedMaterials)
                        {
                            if (m == null || m.shader == null || m.shader.name != "Sdo/UnlitAvatarSheer") continue;
                            m.SetFloat(SdoAvatarBuilder.SheerCullProp, cull);
                            m.SetFloat(SdoAvatarBuilder.SheerZWriteProp, zw);
                        }
                    SaveShot(root, $"H:/65_remake/garment-state-{label}-{(back ? "back" : "front")}.png");
                    Object.DestroyImmediate(root);
                }
        }

        /// <summary>Render 金姬兰 through the EXACT shop-preview overload (SdoRoomAvatar.Build with hrcRel/bodyWeight,
        /// bindPoseNoIdle:false) that the shop's left preview uses — NOT the RenderMode overload the other probes use.
        /// The body-skin filler was originally added only to the RenderMode overload, so the shop path never got it
        /// (使用者:「金姬兰背後還是沒改善」). Front + back, so the upper-back fill is visible.</summary>
        [Test]
        public void Snapshot_ShopPreviewPath_LaceDressBack()
        {
            if (AvatarDir() == null) Assert.Ignore("AVATAR data root not found");
            var parts = new[] { "AVATAR/900007_WOMAN_FACE.MSH", "AVATAR/900017_WOMAN_HAIR.MSH",
                                "AVATAR/024976_WOMAN_ONE.MSH", "AVATAR/900020_WOMAN_SHOES.MSH",
                                "AVATAR/900011_WOMAN_HAND.MSH" };
            foreach (bool back in new[] { false, true })
            {
                var root = new GameObject("ShopPathProbe");
                // the SHOP overload: portraitOpaque:false, parts, hrcRel=FEMALE.HRC, bindPoseNoIdle:false (left preview)
                var av = SdoRoomAvatar.Build(root, 0, false, parts, "AVATAR/FEMALE.HRC", bindPoseNoIdle: false, bodyWeight: 1f);
                if (av == null) { Object.DestroyImmediate(root); continue; }
                av.enabled = false;
                if (back) root.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
                SaveShot(root, "H:/65_remake/shoppath-lace-" + (back ? "back" : "front") + ".png");
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>DIAGNOSTIC (使用者:「先把衣服本身弄成全透明,就能清楚看到背後肩部破洞」): build 金姬兰 and HIDE all the
        /// lace cloth renderers, keeping ONLY the dress mesh's own SKIN (W_Basic) geometry. Renders front + back over an
        /// opaque backdrop so exactly which body geometry the dress ships — and where the hole is — is visible.</summary>
        [Test]
        public void Diagnostic_LaceDress_SkinGeometryOnly()
        {
            if (AvatarDir() == null) Assert.Ignore("AVATAR data root not found");
            var parts = new[] { "AVATAR/024976_WOMAN_ONE.MSH" };   // dress ONLY (no default face/hair/hand)
            SdoAvatarBuilder.LogHoleFill = true;
            foreach (var mode in new[] { "normal", "skinonly", "skinonly-2sided" })
                foreach (bool back in new[] { false, true })
                {
                    var root = new GameObject("DiagProbe");
                    var av = SdoRoomAvatar.Build(root, 0, false, parts, "AVATAR/FEMALE.HRC", bindPoseNoIdle: false);
                    if (av == null) { Object.DestroyImmediate(root); continue; }
                    av.enabled = false;
                    foreach (var mr in root.GetComponentsInChildren<MeshRenderer>())
                    {
                        bool anySkin = false;
                        foreach (var m in mr.sharedMaterials)
                            if (m != null && SdoAvatarBuilder.IsSkinMaterialName(m.name)) anySkin = true;
                        if (mode != "normal" && !anySkin) mr.enabled = false;             // hide cloth (skin-only modes)
                        if (mode == "skinonly-2sided" && anySkin)                          // force skin two-sided
                            foreach (var m in mr.sharedMaterials) if (m != null && m.HasProperty("_Cull")) m.SetInt("_Cull", 0);
                    }
                    SaveShotFromAngle(root, "H:/65_remake/diag-024976-" + mode + "-" + (back ? "back" : "front") + ".png", back);
                    Object.DestroyImmediate(root);
                }
            SdoAvatarBuilder.LogHoleFill = false;
        }

        /// <summary>SaveShot but with the camera placed FRONT (back=false) or BEHIND (back=true) the avatar — a real
        /// back view (moving the camera, not rotating the root, so there's no ambiguity).</summary>
        private static void SaveShotFromAngle(GameObject root, string path, bool back)
        {
            var b = MergedBounds(root);
            const int W = 320, H = 640;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            var camGo = new GameObject("DiagCam");
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = b.extents.y * 1.15f + 0.001f;
            float dist = Mathf.Max(50f, b.size.magnitude * 4f);
            cam.transform.position = b.center + new Vector3(0f, 0f, back ? dist : -dist);
            cam.transform.LookAt(b.center);
            cam.nearClipPlane = 0.01f; cam.farClipPlane = 10000f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.15f, 0.55f, 0.15f, 1f);
            cam.targetTexture = rt;
            cam.Render();
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var shot = new Texture2D(W, H, TextureFormat.RGBA32, false);
            shot.ReadPixels(new Rect(0, 0, W, H), 0, 0); shot.Apply();
            RenderTexture.active = prev;
            System.IO.File.WriteAllBytes(path, shot.EncodeToPNG());
            cam.targetTexture = null;
            Object.DestroyImmediate(shot); Object.DestroyImmediate(camGo);
            rt.Release(); Object.DestroyImmediate(rt);
        }

        private static void SaveShot(GameObject root, string path)
        {
            var b = MergedBounds(root);
            const int W = 320, H = 640;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            var camGo = new GameObject("SnapCam");
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = b.extents.y * 1.15f + 0.001f;
            cam.transform.position = b.center + new Vector3(0f, 0f, -Mathf.Max(50f, b.size.z * 6f));
            cam.transform.LookAt(b.center);
            cam.nearClipPlane = 0.01f; cam.farClipPlane = 10000f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.15f, 0.55f, 0.15f, 1f);   // OPAQUE green: anything green = see-through
            cam.targetTexture = rt;
            cam.Render();
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var shot = new Texture2D(W, H, TextureFormat.RGBA32, false);
            shot.ReadPixels(new Rect(0, 0, W, H), 0, 0); shot.Apply();
            RenderTexture.active = prev;
            System.IO.File.WriteAllBytes(path, shot.EncodeToPNG());
            Debug.Log("[snap] wrote " + path);
            cam.targetTexture = null;
            Object.DestroyImmediate(shot); Object.DestroyImmediate(camGo);
            rt.Release(); Object.DestroyImmediate(rt);
        }

        /// <summary>Dump what the built garment ACTUALLY got: per material, the shader that survived the card
        /// post-processing, its render queue and the alpha the sheer path would output. If the sheer shader isn't
        /// found in this environment the probe would silently exercise a different path than the game — which would
        /// make every coverage number here meaningless.</summary>
        [Test]
        public void Dump_BuiltGarmentMaterials()
        {
            if (AvatarDir() == null) Assert.Ignore("AVATAR data root not found");
            Debug.Log("[dump] Shader.Find(Sdo/UnlitAvatarSheer) = " + (Shader.Find("Sdo/UnlitAvatarSheer") != null ? "FOUND" : "NULL") +
                      " | Sdo/UnlitDoubleSided = " + (Shader.Find("Sdo/UnlitDoubleSided") != null ? "FOUND" : "NULL") +
                      " | Unlit/Texture = " + (Shader.Find("Unlit/Texture") != null ? "FOUND" : "NULL"));

            var applyCutout = typeof(Sdo.UI.Screens.ShopScreen).GetMethod("ApplyCardCutoutShader",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            foreach (var msh in new[] { "037888_WOMAN_ONE.MSH", "024976_WOMAN_ONE.MSH", "003136_WOMAN_COAT.MSH" })
            {
                var root = new GameObject("DumpProbe");
                var av = SdoRoomAvatar.Build(root, 0, portraitOpaque: false, male: false, equippedParts: new[] { "AVATAR/" + msh });
                if (av == null) { Object.DestroyImmediate(root); continue; }
                av.enabled = false;
                foreach (var mr in root.GetComponentsInChildren<MeshRenderer>())
                    foreach (var m in mr.sharedMaterials)
                        if (m != null)
                            Debug.Log($"[dump] {msh} BEFORE-card: shader={m.shader?.name} queue={m.renderQueue} tex={(m.mainTexture != null ? m.mainTexture.name : "null")}");
                applyCutout?.Invoke(null, new object[] { root });
                foreach (var mr in root.GetComponentsInChildren<MeshRenderer>())
                    foreach (var m in mr.sharedMaterials)
                        if (m != null)
                            Debug.Log($"[dump] {msh} AFTER-card:  shader={m.shader?.name} queue={m.renderQueue}");
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>Render <paramref name="root"/> and return the fraction of pixels with alpha above the visible
        /// threshold. <paramref name="msaa"/> 0 = none (2 mirrors the shop card RT); <paramref name="cardClip"/> uses
        /// the shop card's near/far (5/1000) and eye distance (110) instead of bounds-derived ones.</summary>
        private static float Coverage(GameObject root, Bounds b, int msaa, bool cardClip)
        {
            var rt = new RenderTexture(RtW, RtH, 16, RenderTextureFormat.ARGB32);
            if (msaa > 0) { rt.antiAliasing = msaa; }
            var camGo = new GameObject("BisectCam");
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = Mathf.Max(b.extents.x, b.extents.y) * 1.2f + 0.001f;
            float eye = cardClip ? 110f : Mathf.Max(10f, b.size.z * 4f);
            cam.transform.position = b.center + new Vector3(0f, 0f, -eye);
            cam.transform.LookAt(b.center);
            cam.nearClipPlane = cardClip ? 5f : 0.01f;
            cam.farClipPlane = cardClip ? 1000f : Mathf.Max(1000f, b.size.magnitude * 20f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.targetTexture = rt;
            cam.Render();

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var shot = new Texture2D(RtW, RtH, TextureFormat.RGBA32, false);
            shot.ReadPixels(new Rect(0, 0, RtW, RtH), 0, 0);
            shot.Apply();
            RenderTexture.active = prev;

            int lit = 0; var px = shot.GetPixels32();
            foreach (var p in px) if (p.a > 25) lit++;
            float cov = lit / (float)px.Length;

            cam.targetTexture = null;
            Object.DestroyImmediate(shot); Object.DestroyImmediate(camGo);
            rt.Release(); Object.DestroyImmediate(rt);
            return cov;
        }

        private static Bounds MergedBounds(GameObject root)
        {
            var rs = root.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return new Bounds(Vector3.zero, Vector3.zero);
            var b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            return b;
        }
    }
}
