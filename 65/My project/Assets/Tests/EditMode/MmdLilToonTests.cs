using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// MMD 身體的第二種著色後端（lilToon）的翻譯規則。這裡測的是 PMX 那幾個欄位怎麼變成 lilToon 的屬性 ——
    /// 純函式，不需要模型也不需要跑起來。「畫出來好不好看」不在這裡（那要看實機），這裡守的是不會默默壞掉的部分：
    /// 選錯 shader（描邊/透明整組消失）、描邊寬度沒夾住（糊成一團黑）、陰影色從全黑 ramp 取出死黑。
    /// </summary>
    public class MmdLilToonTests
    {
        // ---------------------------------------------------------------- shader 選擇
        // lilToon 把「不透明/裁切/透明 × 有無描邊」拆成六支各自的 shader，選錯就是整組效果不見。

        [Test]
        public void OpaqueMaterialPicksTheOutlineShaderOnlyWhenThePmxAsksForAnEdge()
        {
            Assert.AreEqual(MmdLilToon.ShaderOpaque, MmdLilToon.ShaderNameFor(MmdMaterialRenderMode.Opaque, false));
            Assert.AreEqual(MmdLilToon.ShaderOpaqueOutline, MmdLilToon.ShaderNameFor(MmdMaterialRenderMode.Opaque, true));
        }

        [Test]
        public void CutoutAndBlendBothTakeTheClippingShaderFamily()
        {
            Assert.AreEqual(MmdLilToon.ShaderCutout, MmdLilToon.ShaderNameFor(MmdMaterialRenderMode.Cutout, false));
            Assert.AreEqual(MmdLilToon.ShaderCutoutOutline, MmdLilToon.ShaderNameFor(MmdMaterialRenderMode.Cutout, true));
            // 半透明也走 Cutout 那一支:MMD 的半透明是「混色 + 寫深度 + 丟掉 alpha=0」,而 lilToon 只有
            // Cutout(LIL_RENDER==1)那支會 clip。混色/深度是材質狀態,不是 shader 選的 → 拿它畫得出半透明,
            // 而且不會因為開了 ZWrite 就讓全透明的 texel 在深度緩衝裡留下看不見的牆。
            Assert.AreEqual(MmdLilToon.ShaderCutout, MmdLilToon.ShaderNameFor(MmdMaterialRenderMode.Blend, false));
            Assert.AreEqual(MmdLilToon.ShaderCutoutOutline, MmdLilToon.ShaderNameFor(MmdMaterialRenderMode.Blend, true));
        }

        [Test]
        public void HiddenMaterialsFallOnTheOpaqueFamily()
        {
            // Hidden 的材質根本不畫（mesh 那一段就被跳過），走哪支都行 —— 但不能丟例外或回 null。
            Assert.AreEqual(MmdLilToon.ShaderOpaque, MmdLilToon.ShaderNameFor(MmdMaterialRenderMode.Hidden, false));
        }

        // ---------------------------------------------------------------- sphere → matcap

        [Test]
        public void MultiplySphereBecomesAMultiplyMatCapAndAddSphereBecomesAdd()
        {
            // PMX: 1 = 乘算 .sph（金屬/皮膚的悶光）、2 = 加算 .spa（會發亮的高光）。搞反 = 眼睛整顆白掉。
            Assert.AreEqual(MmdLilToon.MatCapMultiply, MmdLilToon.MatCapBlendMode(1));
            Assert.AreEqual(MmdLilToon.MatCapAdd, MmdLilToon.MatCapBlendMode(2));
        }

        // ---------------------------------------------------------------- 描邊寬度

        [Test]
        public void NoEdgeSizeMeansNoOutline()
        {
            Assert.AreEqual(0f, MmdLilToon.OutlineWidth(0f));
            Assert.AreEqual(0f, MmdLilToon.OutlineWidth(-1f));
        }

        [Test]
        public void HugeEdgeSizeIsClampedSoTheModelDoesNotTurnIntoASilhouette()
        {
            // MMD 有些模型把 edge size 開到很大（在 MMD 自己的相機下才看得見），照抄過來會糊成一團黑。
            Assert.AreEqual(MmdLilToon.OutlineWidthMax, MmdLilToon.OutlineWidth(50f), 1e-6f);
        }

        [Test]
        public void TinyEdgeSizeStillDrawsAVisibleLine()
        {
            Assert.AreEqual(MmdLilToon.OutlineWidthMin, MmdLilToon.OutlineWidth(0.001f), 1e-6f);
        }

        [Test]
        public void TypicalEdgeSizeScalesLinearly()
        {
            Assert.AreEqual(1f * MmdLilToon.OutlineWidthPerEdgeSize, MmdLilToon.OutlineWidth(1f), 1e-6f);
            Assert.AreEqual(2f * MmdLilToon.OutlineWidthPerEdgeSize, MmdLilToon.OutlineWidth(2f), 1e-6f);
        }

        // ---------------------------------------------------------------- 陰影色（從 toon ramp 取暗端）

        [Test]
        public void NoRampFallsBackToTheDefaultShadowTint()
        {
            Assert.AreEqual(MmdLilToon.DefaultShadowColor, MmdLilToon.ShadowColorFromRamp(null));
        }

        [Test]
        public void ShadowColourComesFromTheBottomOfTheRampNotTheTop()
        {
            // ramp 是 V=0 暗 → V=1 亮。GetPixels32 第 0 列＝最底＝暗端。取到頂端 = 暗部跟亮部同色 = 沒有 cel。
            var ramp = Ramp(bottom: new Color32(120, 60, 60, 255), top: new Color32(255, 255, 255, 255));
            var c = MmdLilToon.ShadowColorFromRamp(ramp);
            Object.DestroyImmediate(ramp);

            Assert.Greater(c.r, c.g, "紅色調的暗端應該保住色相");
            Assert.Less(c.r, 0.95f, "取到的是亮端（整條 ramp 的頂），不是暗端");
        }

        [Test]
        public void AnAllBlackRampIsLiftedSoTheShadowSideStillShowsTheTexture()
        {
            // 不少模型的 ramp 底部就是純黑。直接拿會讓暗部整片死黑（材質完全看不見）。
            var ramp = Ramp(bottom: new Color32(0, 0, 0, 255), top: new Color32(255, 255, 255, 255));
            var c = MmdLilToon.ShadowColorFromRamp(ramp);
            Object.DestroyImmediate(ramp);

            Assert.Greater(Mathf.Max(c.r, Mathf.Max(c.g, c.b)), 0.2f);
        }

        [Test]
        public void BrightenKeepsTheHueAndOnlyLiftsWhatIsBelowTheFloor()
        {
            var lifted = MmdLilToon.Brighten(new Color(0.1f, 0.05f, 0.05f, 1f), 0.4f);
            Assert.AreEqual(0.4f, lifted.r, 1e-5f);
            Assert.AreEqual(0.5f, lifted.g / lifted.r, 1e-4f, "色相要保住（原本 g 是 r 的一半）");

            var untouched = new Color(0.9f, 0.8f, 0.7f, 1f);
            Assert.AreEqual(untouched, MmdLilToon.Brighten(untouched, 0.4f), "已經夠亮的顏色不該被動");
        }

        // ---------------------------------------------------------------- 混色/深度狀態

        [Test]
        public void BlendMaterialsWriteDepthClipAtZeroAndSitInTheTransparentQueue()
        {
            var mat = NewMaterial();
            MmdLilToon.ApplyRenderMode(mat, MmdMaterialRenderMode.Blend);
            // 整具身體是一個 SkinnedMeshRenderer,Unity 不對 submesh 做距離排序 → 不寫深度就變成
            // 「材質順序決定誰蓋誰」(雙馬尾蓋過袖子、髮影蓋過瀏海)。MMD 自己也是全程寫深度的。
            Assert.AreEqual(1f, mat.GetFloat("_ZWrite"));
            Assert.AreEqual(MmdMaterialClassifier.BlendClipCutoff, mat.GetFloat("_Cutoff"), 1e-6f,
                "寫深度就一定要丟掉 alpha=0 的 texel,否則它們會在深度緩衝裡留下看不見的牆");
            Assert.AreEqual((int)RenderQueue.Transparent, mat.renderQueue);
            Assert.AreEqual((float)BlendMode.SrcAlpha, mat.GetFloat("_SrcBlend"));
            // alpha 通道單獨配：alpha 也用 SrcAlpha 會把底下的 A=1 變成 a²+(1−a)，在頭貼/房間的 RT 上打出半透明的洞。
            Assert.AreEqual((float)BlendMode.One, mat.GetFloat("_SrcBlendAlpha"));
            Object.DestroyImmediate(mat);
        }

        [Test]
        public void CutoutMaterialsWriteDepthAndSitInTheAlphaTestQueue()
        {
            var mat = NewMaterial();
            MmdLilToon.ApplyRenderMode(mat, MmdMaterialRenderMode.Cutout);
            Assert.AreEqual(1f, mat.GetFloat("_ZWrite"));
            Assert.AreEqual((int)RenderQueue.AlphaTest, mat.renderQueue);
            Object.DestroyImmediate(mat);
        }

        [Test]
        public void OpaqueMaterialsAreFullyOpaqueInTheGeometryQueue()
        {
            var mat = NewMaterial();
            MmdLilToon.ApplyRenderMode(mat, MmdMaterialRenderMode.Opaque);
            Assert.AreEqual(1f, mat.GetFloat("_ZWrite"));
            Assert.AreEqual((int)RenderQueue.Geometry, mat.renderQueue);
            Assert.AreEqual((float)BlendMode.One, mat.GetFloat("_SrcBlend"));
            Assert.AreEqual((float)BlendMode.Zero, mat.GetFloat("_DstBlend"));
            Object.DestroyImmediate(mat);
        }

        // ---------------------------------------------------------------- 整份材質

        [Test]
        public void ConfigureWritesEverythingTheThreeDisplayTogglesLaterFlip()
        {
            var shader = Shader.Find(MmdLilToon.ShaderOpaqueOutline);
            if (shader == null) Assert.Ignore("lilToon 沒裝（Assets/lilToon）—— 這個測試只在裝了的時候有意義。");

            var mat = new Material(shader);
            var sphere = new Texture2D(2, 2);
            MmdLilToon.Configure(mat, Texture2D.whiteTexture, new Color(0.9f, 0.9f, 1f, 1f), doubleSided: true,
                                 MmdMaterialRenderMode.Opaque, sphere, sphereMode: 2, toonRamp: null,
                                 edgeColor: Color.black, edgeSize: 1f);

            Assert.AreEqual(1f, mat.GetFloat(MmdLilToon.ToonProperty), "cel 陰影要開著（設定面板的『卡通著色』才有東西可關）");
            Assert.AreEqual(1f, mat.GetFloat(MmdLilToon.SphereProperty), "有 sphere 貼圖就要開 matcap");
            Assert.AreEqual(MmdLilToon.MatCapAdd, mat.GetFloat("_MatCapBlendMode"), 1e-6f);
            Assert.Greater(mat.GetFloat(MmdLilToon.OutlineWidthProperty), 0f, "PMX 說要描邊，寬度就不能是 0");
            Assert.AreEqual(0f, mat.GetFloat("_Cull"), "雙面材質不能被剔除");
            Assert.GreaterOrEqual(mat.GetFloat("_LightMinLimit"), 0.2f,
                "亮度下限太低 → 角色轉到背光那半圈會整片黑（場上只有一顆固定方向的平行光）");

            Object.DestroyImmediate(mat);
            Object.DestroyImmediate(sphere);
        }

        [Test]
        public void ConfigureLeavesMatCapOffWhenThePmxMaterialHasNoSphereTexture()
        {
            var shader = Shader.Find(MmdLilToon.ShaderOpaque);
            if (shader == null) Assert.Ignore("lilToon 沒裝（Assets/lilToon）。");

            var mat = new Material(shader);
            MmdLilToon.Configure(mat, Texture2D.whiteTexture, Color.white, doubleSided: false,
                                 MmdMaterialRenderMode.Opaque, sphereTex: null, sphereMode: 0, toonRamp: null,
                                 edgeColor: Color.black, edgeSize: 0f);

            Assert.AreEqual(0f, mat.GetFloat(MmdLilToon.SphereProperty));
            Assert.AreEqual(0f, mat.GetFloat(MmdLilToon.OutlineWidthProperty));
            Object.DestroyImmediate(mat);
        }

        // ---------------------------------------------------------------- build 剝離防護

        [Test]
        public void EveryLilToonShaderTheRuntimeAsksForIsAlwaysIncludedInTheBuild()
        {
            // 與 ShaderInclusionTests 同一個陷阱：只在執行期 Shader.Find 取的 shader 會被 player build 剝掉。
            // 那個測試只掃 "Sdo/…" 字面，lilToon 的名字不長那樣，而且這裡是從常數組出來的 → 掃不到。
            string buildScript = Path.Combine(Application.dataPath, "Editor", "BuildScript.cs");
            Assert.IsTrue(File.Exists(buildScript), "BuildScript.cs not found at " + buildScript);
            string src = File.ReadAllText(buildScript);

            foreach (var name in new[]
            {
                MmdLilToon.ShaderOpaque, MmdLilToon.ShaderOpaqueOutline,
                MmdLilToon.ShaderCutout, MmdLilToon.ShaderCutoutOutline,
                MmdLilToon.ShaderTransparent, MmdLilToon.ShaderTransparentOutline,
            })
                Assert.IsTrue(src.Contains("\"" + name + "\""),
                    $"lilToon shader \"{name}\" 不在 BuildScript.RequiredShaders 裡 → 打包版會被剝掉，"
                    + "MmdAvatar 只好退回 Sdo/MmdModel（editor 看起來正常，只有 build 壞）。");
        }

        // ---------------------------------------------------------------- helpers

        private static Material NewMaterial()
        {
            // 狀態那幾個屬性哪支 shader 都有（Sdo/MmdModel 一定在，lilToon 可能沒裝）。
            var shader = Shader.Find(MmdLilToon.ShaderOpaque) ?? Shader.Find("Sdo/MmdModel") ?? Shader.Find("Unlit/Texture");
            return new Material(shader);
        }

        /// <summary>一張 1×16 的直向漸層，底＝暗端、頂＝亮端（＝MMD toon ramp 的排法）。</summary>
        private static Texture2D Ramp(Color32 bottom, Color32 top)
        {
            const int h = 16;
            var t = new Texture2D(1, h, TextureFormat.RGBA32, false);
            var px = new Color32[h];
            for (int y = 0; y < h; y++) px[y] = Color32.Lerp(bottom, top, y / (float)(h - 1));
            t.SetPixels32(px);
            t.Apply(false, false);
            return t;
        }
    }
}
