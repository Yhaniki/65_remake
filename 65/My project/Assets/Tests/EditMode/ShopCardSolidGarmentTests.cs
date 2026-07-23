using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 使用者回報 (001766 "Skirt Suit" 女連身): 「這衣服領口後面應該是有衣服的，但是框裡面變成透明透過後面」—— 商城格子
    /// 裡衣服的領口破了一個洞,直接看到卡片背景。
    ///
    /// 根因不是 alpha 也不是幾何缺料,是「單面 + 藏身體」:卡片會 <c>HideSkinSubmeshes</c> 只留布料,所以領口該露出的是
    /// 衣服自己的「另一面」(後片內側)。那面是 BACK FACE,單面 shader (Sdo/UnlitAvatarSheer 是 Cull Back) 直接剔掉 → 洞。
    /// 改成官方 .msh 旗標判透明之前,這種實心去背布料被貼圖直方圖判成 Cutout,卡片用的是雙面的 Sdo/UnlitDoubleSided,洞
    /// 才是補起來的;旗標一接上它改判 Blend,就掉進卡片「紗質材質不要動」的例外裡,洞就露出來了。
    ///
    /// 修法 = 把那個例外收窄成「只放過真紗」(<see cref="SdoAvatarBuilder.IsSheerFabric"/>);實心去背布料照舊換雙面
    /// cutout。這裡從兩端釘住:
    ///   • 分類:哪些布料算真紗 (真實 DDS 的 translucent 值),
    ///   • 結果:走完整張卡片管線渲染出來,衣服輪廓「裡面」不可以有透空的洞。
    /// 真紗的對照組 (024976 蕾絲裙) 必須維持不被換掉,否則 clip+α→1 會把蕾絲壓成實心 (使用者:「格子裡面的透明度沒改」)。
    /// </summary>
    public class ShopCardSolidGarmentTests
    {
        private const int RtW = 192, RtH = 192;

        // ── 分類:真紗 vs 實心去背布料 (值 = 真實 DDS 的 DdsLoader.TranslucentFraction) ────────────────────────────
        public struct FabricCase
        {
            public string Msh; public string Dds; public bool Sheer; public string Why;
            public override string ToString() => Msh + ":" + Dds;
        }

        private static readonly FabricCase[] Fabrics =
        {
            // 這次回報的衣服 — 官方旗標 2 (blend),但貼圖 93~95% 完全不透明,alpha 只是拿來去背輪廓 → 不是紗。
            new FabricCase { Msh = "001766_WOMAN_ONE.MSH", Dds = "001766_woman_coat.dds", Sheer = false, Why = "Skirt Suit 外套:translucent≈0.008,實心布 → 卡片要雙面才補得回領口" },
            new FabricCase { Msh = "001766_WOMAN_ONE.MSH", Dds = "001766_woman_pant.dds", Sheer = false, Why = "Skirt Suit 裙:translucent≈0.008,同上" },
            // 真紗對照組 — 這些換成 cutout 就會失去半透明。
            new FabricCase { Msh = "024976_WOMAN_ONE.MSH", Dds = "024976_woman_one_a.dds", Sheer = true, Why = "金姬兰蕾絲外層:translucent≈0.348" },
            new FabricCase { Msh = "024976_WOMAN_ONE.MSH", Dds = "024976_woman_one_b.dds", Sheer = true, Why = "金姬兰蕾絲外層背面:translucent≈0.274" },
            new FabricCase { Msh = "037000_WOMAN_COAT.MSH", Dds = "037000_woman_coat.dds", Sheer = true, Why = "牛奶裝整件紗:translucent≈0.317" },
            new FabricCase { Msh = "002178_WOMAN_ONE.MSH", Dds = "002178_woman_pant_.dds", Sheer = true, Why = "京族新娘装外層紗褲:translucent≈0.325" },
            // 同一件的上衣卻是實心 — 證明這個判斷是逐材質的,不是逐件。
            new FabricCase { Msh = "002178_WOMAN_ONE.MSH", Dds = "002178_woman_coat.dds", Sheer = false, Why = "京族新娘装上衣:translucent≈0.005,97% 貼圖不透明" },
        };

        private static IEnumerable<FabricCase> AllFabrics() => Fabrics;

        private static string AvatarDir()
        {
            var probe = SdoAvatarBuilder.ResolveAvatarFile("AVATAR/" + Fabrics[0].Msh);
            if (string.IsNullOrEmpty(probe) || !File.Exists(probe)) return null;
            return Path.GetDirectoryName(probe);
        }

        [Test, TestCaseSource(nameof(AllFabrics))]
        public void RealTexture_SheerClassification(FabricCase c)
        {
            var dir = AvatarDir();
            if (dir == null) Assert.Ignore("AVATAR data root not found — needs the game data (data_root.txt)");

            string ddsPath = SdoAvatarBuilder.FindDdsPath(dir, c.Dds);
            Assert.IsNotNull(ddsPath, $"{c.Dds}: texture not found in {dir} ({c.Why})");
            var st = DdsLoader.Analyze(File.ReadAllBytes(ddsPath));
            Assert.AreEqual(c.Sheer, SdoAvatarBuilder.IsSheerFabric(st.Translucent),
                $"{c.Dds} 的「是不是真紗」判斷跑掉了 — {c.Why}\n" +
                $"translucent={st.Translucent:F3}, bar={SdoAvatarBuilder.SheerTranslucentBar:F2}\n" +
                "判成紗 → 卡片保留單面 blend(實心布料的領口會透);判成實心 → 卡片換雙面 cutout(真紗會被壓成實心)。");
        }

        [Test]
        public void SheerBar_MatchesGarmentAlphaModeBar()
        {
            // 同一個度量、同一條線:卡片的紗/實心分流不可以跟 GarmentAlphaMode 的紗判定漂開,否則同一塊布在兩邊被
            // 歸成不同類。這裡直接用 GarmentAlphaMode 的 fallback 路徑反推它認的線。
            Assert.AreEqual(DdsAlphaMode.Blend,
                SdoAvatarBuilder.GarmentAlphaMode(DdsAlphaMode.Cutout, 0.05f, SdoAvatarBuilder.SheerTranslucentBar, true),
                "剛好踩線的 translucent 在 GarmentAlphaMode 要算紗");
            Assert.IsTrue(SdoAvatarBuilder.IsSheerFabric(SdoAvatarBuilder.SheerTranslucentBar), "踩線 = 紗 (>=)");
            Assert.IsFalse(SdoAvatarBuilder.IsSheerFabric(SdoAvatarBuilder.SheerTranslucentBar - 0.001f));
        }

        // ── 結果:整張卡片管線渲染,實心布料要填滿自己的輪廓(不可以看穿) ────────────────────────────────────────
        public struct CardCase
        {
            public string Msh; public float MinSilhouetteFill; public string Why;
            public override string ToString() => Msh;
        }

        private static readonly CardCase[] CardCases =
        {
            // 量的是「卡片實際畫出來的像素 / 這件衣服的輪廓」。單面 blend 布料在卡片上會少掉所有「背面朝向鏡頭」的
            // 地方(領口內裡、裙內側,甚至整件——見下),比例就掉下來。實測:修好前 0.00、修好後 0.99。
            new CardCase { Msh = "001766_WOMAN_ONE.MSH", MinSilhouetteFill = 0.90f, Why = "Skirt Suit:使用者回報「領口後面應該是有衣服的,但框裡面變成透明透過後面」(實測 0.99)" },
            // 對照組:另一件官方旗標判 blend 的實心上衣。門檻低一些是因為它 19% 貼圖是真洞(流蘇縫隙),輪廓量法把那些
            // 縫隙也算進輪廓 → 就算完全正確也填不到 100%。修好前同樣是 0.00。
            new CardCase { Msh = "003136_WOMAN_COAT.MSH", MinSilhouetteFill = 0.72f, Why = "紅色不羈牛仔:旗標 1 透明 + translucent 0.153<0.21 → Cutout(裁掉 19% 白底、留紅流蘇實心)。白底本非布料 → 只填 ~0.78,比舊 Blend 的 0.87 低是對的" },
        };

        private static IEnumerable<CardCase> AllCardCases() => CardCases;

        [Test, TestCaseSource(nameof(AllCardCases))]
        public void ShopCard_SolidGarmentFillsItsOwnSilhouette(CardCase c)
        {
            if (AvatarDir() == null) Assert.Ignore("AVATAR data root not found — needs the game data (data_root.txt)");

            var shopType = typeof(Sdo.UI.Screens.ShopScreen);
            var applyCutout = shopType.GetMethod("ApplyCardCutoutShader", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var hideSkin = shopType.GetMethod("HideSkinSubmeshes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(applyCutout, "ShopScreen.ApplyCardCutoutShader not found — card pipeline was renamed, update this test");
            Assert.IsNotNull(hideSkin, "ShopScreen.HideSkinSubmeshes not found — card pipeline was renamed, update this test");

            GameObject root = null;
            try
            {
                root = new GameObject("CardFillProbe");
                // 商城卡片真正走的那支 overload (ShopScreen.BuildCardPreview): 假人骨架 + bind pose + SdoAvatarBuilder.LoadParts。
                var av = SdoRoomAvatar.Build(root, 0, portraitOpaque: false, parts: new[] { "AVATAR/" + c.Msh },
                                             hrcRel: null, bindPoseNoIdle: true);
                Assert.IsNotNull(av, c.Msh + ": avatar build failed");
                av.enabled = false;

                applyCutout.Invoke(null, new object[] { root });   // 卡片:非紗材質換雙面 cutout
                hideSkin.Invoke(null, new object[] { root });      // 卡片:藏膚色 range,只留布料 → 領口沒有身體可以擋

                var bounds = MergedBounds(root);
                Assert.Greater(bounds.size.magnitude, 0.01f, c.Msh + ": no renderer bounds after card post-processing");

                int card = DrawnPixels(root, bounds);             // 卡片真正畫出來的
                ForceSilhouette(root);                            // 同樣的幾何,雙面 + 不裁 + 全不透明
                int silhouette = DrawnPixels(root, bounds);       // = 這件衣服的輪廓

                Assert.Greater(silhouette, RtW * RtH / 100, c.Msh + ": 輪廓太小,量不出東西 (先看 GarmentRendersVisibleTests)");
                float fill = card / (float)silhouette;
                Debug.Log($"[card-fill-probe] {c.Msh}: card={card} silhouette={silhouette} fill={fill:P2}");
                Assert.GreaterOrEqual(fill, c.MinSilhouetteFill,
                    $"{c.Msh} 的商城卡片只填了自己輪廓的 {fill:P2} — {c.Why}\n" +
                    "少掉的就是「背面朝向鏡頭」的地方(領口內裡、裙內側;繞序反過來的 mesh 甚至是整件)。卡片藏了身體,\n" +
                    "單面布料把那些面剔掉就直接看穿到卡片背景;實心去背布料要走雙面 cutout\n" +
                    "(ShopScreen.ApplyCardCutoutShader / SdoAvatarBuilder.IsSheerFabric)。");
            }
            finally
            {
                if (root != null) Object.DestroyImmediate(root);
            }
        }

        /// <summary>The other half of the fix: REAL sheer must survive the card untouched. 024976 金姬兰's lace shell is
        /// what forced the "leave sheer alone" exception in the first place — swapping it to the cutout shader clips the
        /// weave and forces α→1, i.e. solid black lace (使用者:「格子裡面的透明度沒改」). So after the card pipeline runs,
        /// at least one of its materials must STILL be the sheer shader.</summary>
        [Test]
        public void ShopCard_RealSheerKeepsItsBlendShader()
        {
            if (AvatarDir() == null) Assert.Ignore("AVATAR data root not found — needs the game data (data_root.txt)");

            var applyCutout = typeof(Sdo.UI.Screens.ShopScreen).GetMethod("ApplyCardCutoutShader",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(applyCutout, "ShopScreen.ApplyCardCutoutShader not found — card pipeline was renamed, update this test");

            GameObject root = null;
            try
            {
                root = new GameObject("SheerKeptProbe");
                var av = SdoRoomAvatar.Build(root, 0, portraitOpaque: false, parts: new[] { "AVATAR/024976_WOMAN_ONE.MSH" },
                                             hrcRel: null, bindPoseNoIdle: true);
                Assert.IsNotNull(av, "024976_WOMAN_ONE.MSH: avatar build failed");
                av.enabled = false;

                int sheerBefore = CountSheer(root);
                Assert.Greater(sheerBefore, 0, "024976 蕾絲裙沒有任何紗質材質 — 分類壞了 (見 GarmentAlphaRealDataTests)");
                applyCutout.Invoke(null, new object[] { root });
                Assert.Greater(CountSheer(root), 0,
                    "商城卡片把蕾絲裙的紗質材質也換成 cutout 了 — clip + α→1 會把蕾絲壓成實心黑。" +
                    "IsSheerFabric 的門檻或 ApplyCardCutoutShader 的例外收得太緊。");
            }
            finally { if (root != null) Object.DestroyImmediate(root); }
        }

        private static int CountSheer(GameObject root)
        {
            int n = 0;
            foreach (var mr in root.GetComponentsInChildren<MeshRenderer>())
                foreach (var m in mr.sharedMaterials)
                    if (m != null && m.shader != null && m.shader.name == "Sdo/UnlitAvatarSheer") n++;
            return n;
        }

        /// <summary>Render <paramref name="root"/> with a card-like orthographic camera into an alpha-cleared RT and
        /// count the pixels that actually got drawn (alpha above the same bar the coverage probe uses).</summary>
        private static int DrawnPixels(GameObject root, Bounds bounds)
        {
            RenderTexture rt = null; Camera cam = null; Texture2D shot = null;
            try
            {
                rt = new RenderTexture(RtW, RtH, 16, RenderTextureFormat.ARGB32);
                var camGo = new GameObject("CardFillProbeCam");
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

                int lit = 0;
                foreach (var p in shot.GetPixels32()) if (p.a > 25) lit++;
                return lit;
            }
            finally
            {
                if (cam != null) { cam.targetTexture = null; Object.DestroyImmediate(cam.gameObject); }
                if (shot != null) Object.DestroyImmediate(shot);
                if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); }
            }
        }

        /// <summary>Repaint every material as two-sided, un-clipped and fully opaque — the garment's pure SILHOUETTE,
        /// i.e. every pixel its geometry covers from this camera. The card is compared against it.</summary>
        private static void ForceSilhouette(GameObject root)
        {
            var solid = Shader.Find("Sdo/UnlitDoubleSided");
            Assert.IsNotNull(solid, "Sdo/UnlitDoubleSided missing — needed to measure the silhouette");
            foreach (var mr in root.GetComponentsInChildren<MeshRenderer>())
                foreach (var m in mr.sharedMaterials)
                    if (m != null) { m.shader = solid; m.SetFloat("_Cutoff", 0f); }
        }

        private static Bounds MergedBounds(GameObject root)
        {
            var rs = root.GetComponentsInChildren<Renderer>();
            var b = new Bounds(root.transform.position, Vector3.zero);
            bool any = false;
            foreach (var r in rs)
            {
                if (!r.enabled) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return any ? b : new Bounds(root.transform.position, Vector3.zero);
        }
    }
}
