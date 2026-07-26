using System;
using System.IO;
using NUnit.Framework;
using Sdo.Game;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>
    /// Covers the mapobj transparency / texture-animation pure logic added for the "去背 + missing-texture"
    /// stage-asset pass: DDS alpha detection, the _TexAnimEx material/.an parser, and the new frame-swap entries.
    /// </summary>
    public class MapobjTransparencyTests
    {
        // Minimal valid DDS: 128-byte header ("DDS ", dims, fourCC@84) + one 4x4 block of payload.
        private static byte[] MakeDds(string fourcc, byte[] block, int w = 4, int h = 4)
        {
            var d = new byte[128 + block.Length];
            d[0] = (byte)'D'; d[1] = (byte)'D'; d[2] = (byte)'S'; d[3] = (byte)' ';
            BitConverter.GetBytes(h).CopyTo(d, 12);
            BitConverter.GetBytes(w).CopyTo(d, 16);
            System.Text.Encoding.ASCII.GetBytes(fourcc).CopyTo(d, 84);
            block.CopyTo(d, 128);
            return d;
        }

        private static byte[] MakeDxt3ColorBlock(byte alphaNibble, ushort color565)
        {
            var block = new byte[16];
            byte a = (byte)(alphaNibble | (alphaNibble << 4));
            for (int i = 0; i < 8; i++) block[i] = a;
            BitConverter.GetBytes(color565).CopyTo(block, 8);
            BitConverter.GetBytes((ushort)0).CopyTo(block, 10);
            return block;
        }

        [Test]
        public void HasAlpha_Dxt3_AllOpaque_IsFalse()
        {
            var block = new byte[16];
            for (int i = 0; i < 8; i++) block[i] = 0xFF;   // every 4-bit alpha nibble = 15 -> 255
            Assert.IsFalse(DdsLoader.HasAlpha(MakeDds("DXT3", block)));
        }

        [Test]
        public void HasAlpha_Dxt3_WithTransparentTexel_IsTrue()
        {
            var block = new byte[16];
            for (int i = 0; i < 8; i++) block[i] = 0xFF;
            block[0] = 0x00;   // one texel fully transparent
            Assert.IsTrue(DdsLoader.HasAlpha(MakeDds("DXT3", block)));
        }

        [Test]
        public void HasAlpha_Dxt5_OpaqueEndpoints_IsFalse()
        {
            var block = new byte[16]; block[0] = 255; block[1] = 255;   // a0=a1=255, no interpolation below 255
            Assert.IsFalse(DdsLoader.HasAlpha(MakeDds("DXT5", block)));
        }

        [Test]
        public void HasAlpha_Dxt5_SixCodePalette_IsTrue()
        {
            var block = new byte[16]; block[0] = 0; block[1] = 255;   // a0<a1 -> palette includes 0 -> transparent reachable
            Assert.IsTrue(DdsLoader.HasAlpha(MakeDds("DXT5", block)));
        }

        [Test]
        public void HasAlpha_Dxt1_IsAlwaysOpaque()
        {
            Assert.IsFalse(DdsLoader.HasAlpha(MakeDds("DXT1", new byte[8])));
        }

        [Test]
        public void GetAlphaMode_Dxt3_HardTransparent_IsCutout()
        {
            var block = new byte[16];
            for (int i = 0; i < 8; i++) block[i] = 0xFF;
            block[0] = 0xF0;   // one 0-alpha nibble, the rest opaque
            Assert.AreEqual(DdsAlphaMode.Cutout, DdsLoader.GetAlphaMode(MakeDds("DXT3", block)));
        }

        [Test]
        public void GetAlphaMode_Dxt3_SoftAlpha_IsBlend()
        {
            var block = new byte[16];
            for (int i = 0; i < 8; i++) block[i] = 0x99;   // alpha 153/255
            Assert.AreEqual(DdsAlphaMode.Blend, DdsLoader.GetAlphaMode(MakeDds("DXT3", block)));
        }

        [Test]
        public void GetAlphaMode_Dxt5_SelectedSoftAlpha_IsBlend()
        {
            var block = new byte[16];
            block[0] = 255; block[1] = 0;
            block[2] = 0x02;   // first 3-bit selector = code 2 -> interpolated alpha
            Assert.AreEqual(DdsAlphaMode.Blend, DdsLoader.GetAlphaMode(MakeDds("DXT5", block)));
        }

        // The mapobj depth-write fix (SCN0009 GUATAN 掛毯 穿模) routes an ANIMATED prop to the cutout (ZWrite On)
        // shader when its texture is GetSceneAlphaMode==Cutout. Pin the two decision points the gate relies on:
        [Test]
        public void GetSceneAlphaMode_Dxt3_MostlyOpaqueWithHoles_IsCutout()
        {
            // guantan.dds profile: ~91% opaque body + ~6% hard-transparent holes, almost no midtone → Cutout.
            var block = new byte[16];
            for (int i = 0; i < 8; i++) block[i] = 0xFF;   // all opaque
            block[0] = 0xF0;                               // one texel fully transparent (1/16 = 6.25% hole)
            Assert.AreEqual(DdsAlphaMode.Cutout, DdsLoader.GetSceneAlphaMode(MakeDds("DXT3", block)));
        }

        [Test]
        public void GetSceneAlphaMode_Dxt3_SoftGradient_IsBlend()
        {
            // A soft/glow cloth (uniform midtone, no hard holes) must stay Blend so the gate does NOT depth-write it.
            var block = new byte[16];
            for (int i = 0; i < 8; i++) block[i] = 0x99;   // alpha 153/255 everywhere → soft
            Assert.AreEqual(DdsAlphaMode.Blend, DdsLoader.GetSceneAlphaMode(MakeDds("DXT3", block)));
        }

        [Test]
        public void LooksLikeAdditiveGlow_Detects_Soft_Bright_Dxt3()
        {
            Assert.IsTrue(DdsLoader.LooksLikeAdditiveGlow(MakeDds("DXT3", MakeDxt3ColorBlock(9, 0xffff))));
        }

        [Test]
        public void LooksLikeAdditiveGlow_Detects_SoftRadialGradient_Dxt3()
        {
            // Radial-gradient glow sprite: transparent border (pixels 0-7 alpha=0) + soft centre (pixels 8-15 alpha≈136).
            // visibleRatio=0.5<0.95, softOfVisible=1.0, opaqueOfVisible=0, meanLum=132 → additive path fires.
            // Models GUANG1_.DDS (86.7% visible, 100% soft, 0% opaque, meanLum=81).
            var block = new byte[16];
            // pixels 0-7: alpha nibble=0 (bytes 0-3 stay zero)
            // pixels 8-15: alpha nibble=8 → alpha=(8*255+7)/15=136 (soft)
            block[4] = 0x88; block[5] = 0x88; block[6] = 0x88; block[7] = 0x88;
            BitConverter.GetBytes((ushort)0x8410).CopyTo(block, 8);  // medium gray, MaxLum≈132
            Assert.IsTrue(DdsLoader.LooksLikeAdditiveGlow(MakeDds("DXT3", block)));
        }

        [Test]
        public void LooksLikeAdditiveGlow_Rejects_Opaque_Or_Dim_Dxt3()
        {
            // All-opaque (alpha=255): Soft==0, returns false immediately.
            Assert.IsFalse(DdsLoader.LooksLikeAdditiveGlow(MakeDds("DXT3", MakeDxt3ColorBlock(15, 0xffff))));
            // Uniform soft alpha (all 16 pixels visible, visibleRatio=1.0): NOT < 0.95, does not hit the radial-gradient
            // path; meanLum≈66 < 180, so the bright-glow path also fails → false.
            Assert.IsFalse(DdsLoader.LooksLikeAdditiveGlow(MakeDds("DXT3", MakeDxt3ColorBlock(9, 0x4208))));
        }

        [Test]
        public void HasAlpha_NotDds_IsFalse()
        {
            Assert.IsFalse(DdsLoader.HasAlpha(null));
            Assert.IsFalse(DdsLoader.HasAlpha(new byte[16]));   // too short / no magic
        }

        // 5×5: opaque red core, white transparent matte around it (the SCN0026 car-billboard shape in miniature).
        private static Color32[] RedOnWhiteMatte(int w, int h)
        {
            var px = new Color32[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 0);   // white, fully transparent
            for (int y = 1; y < h - 1; y++)
                for (int x = 1; x < w - 1; x++)
                    px[y * w + x] = new Color32(220, 10, 10, 255);                        // opaque red core
            return px;
        }

        [Test]
        public void BleedAlphaEdges_Replaces_White_Matte_With_Prop_Colour()
        {
            const int w = 5, h = 5;
            var px = RedOnWhiteMatte(w, h);
            DdsLoader.BleedAlphaEdges(px, w, h);
            // a transparent matte texel adjacent to the red core is now reddish, not white — alpha untouched.
            var edge = px[0 * w + 1];   // top edge, directly above a core texel
            Assert.Less(edge.b, 120, "blue channel of the matte should drop (no longer white)");
            Assert.Greater(edge.r, edge.b, "matte takes on the red prop colour");
            Assert.AreEqual(0, edge.a, "alpha (the cut-out shape) is unchanged");
            // the opaque core is left exactly as authored.
            var core = px[2 * w + 2];
            Assert.AreEqual(220, core.r); Assert.AreEqual(10, core.g); Assert.AreEqual(255, core.a);
        }

        [Test]
        public void BleedAlphaEdges_FullyOpaque_IsNoOp()
        {
            var px = new Color32[9];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32((byte)(i * 10), 5, 5, 255);
            var copy = (Color32[])px.Clone();
            DdsLoader.BleedAlphaEdges(px, 3, 3);
            for (int i = 0; i < px.Length; i++)
            {
                Assert.AreEqual(copy[i].r, px[i].r); Assert.AreEqual(copy[i].g, px[i].g);
                Assert.AreEqual(copy[i].b, px[i].b); Assert.AreEqual(copy[i].a, px[i].a);
            }
        }

        [Test]
        public void TexAnimEx_Parses_Name_And_Interval()
        {
            Assert.IsTrue(TexAnimEx.TryParse("_texanimex(fangzi3)1300_0.dds", out var s));
            Assert.AreEqual("fangzi3", s.Name);
            Assert.AreEqual(1300f, s.IntervalMs, 1e-3f);

            Assert.IsTrue(TexAnimEx.TryParse("_TEXANIMEX(DENG1)1200_0_.dds", out var s2));
            Assert.AreEqual("DENG1", s2.Name);
            Assert.AreEqual(1200f, s2.IntervalMs, 1e-3f);

            Assert.IsTrue(TexAnimEx.TryParse("_texanimex(cs2)200_0_.dds", out var s3));
            Assert.AreEqual("cs2", s3.Name);
            Assert.AreEqual(200f, s3.IntervalMs, 1e-3f);

            // 翅膀/wings — 花雨飞翼 023921: NAME carries a trailing underscore, interval 200. The ".an" is <NAME>.an.
            Assert.IsTrue(TexAnimEx.TryParse("_texanimex(023921_woman_chibang1_)200_1.dds", out var w));
            Assert.AreEqual("023921_woman_chibang1_", w.Name);
            Assert.AreEqual(200f, w.IntervalMs, 1e-3f);
        }

        [Test]
        public void TexAnimEx_Rejects_Ordinary_Material_Names()
        {
            Assert.IsFalse(TexAnimEx.TryParse("guanggao.dds", out _));
            Assert.IsFalse(TexAnimEx.TryParse("", out _));
            Assert.IsFalse(TexAnimEx.TryParse(null, out _));
        }

        [Test]
        public void TexAnimEx_ParseAn_Splits_Dds_Lines()
        {
            var frames = TexAnimEx.ParseAn("fangzi3an.dds\r\nfangzi3liang.dds");
            Assert.AreEqual(2, frames.Length);
            Assert.AreEqual("fangzi3an.dds", frames[0]);
            Assert.AreEqual("fangzi3liang.dds", frames[1]);
            // tolerates trailing whitespace / blank lines / non-image noise
            var f2 = TexAnimEx.ParseAn("  a.dds \n\n  b.dds  \r\nnote\n");
            Assert.AreEqual(2, f2.Length);
            Assert.AreEqual("a.dds", f2[0]);
            Assert.AreEqual("b.dds", f2[1]);
        }

        [Test]
        public void TexAnimEx_ParseAn_Accepts_Tga_Frames()
        {
            // 翅膀/wings ship their glow frames as .tga (花雨飞翼 023921_WOMAN_CHIBANG1_.AN) — dropping these was why
            // the wings fell back to the flat beige colour. .tga/.bmp/.png must all be kept; non-image lines ignored.
            var frames = TexAnimEx.ParseAn("023921_woman_chibang1_.tga\r\n023921_woman_chibang2_.tga\r\n023921_woman_chibang3_.tga\r\n");
            Assert.AreEqual(3, frames.Length);
            Assert.AreEqual("023921_woman_chibang1_.tga", frames[0]);
            Assert.AreEqual("023921_woman_chibang3_.tga", frames[2]);

            var mixed = TexAnimEx.ParseAn("a.tga\nb.dds\nc.bmp\nd.png\nreadme\n");
            Assert.AreEqual(4, mixed.Length);
        }

        [Test]
        public void TexAnim_Christmas_Billboards_Are_Animated_Transparent()
        {
            // SCN0005's reindeer + ground decal: placeholder MSH materials -> must be driven by the frame sequence.
            var christmas = SceneMapobjTexAnimCatalog.Find("SCN0005", "CHRISTMAS");
            Assert.IsNotNull(christmas);
            Assert.AreEqual(4, christmas.Frames.Length);
            Assert.AreEqual("CHRISTMAS001.dds", christmas.Frames[0]);
            Assert.IsTrue(christmas.Transparent);

            Assert.IsNotNull(SceneMapobjCatalog.ForFolder("SCN0005"));   // sanity: the scene mounts these props
            Assert.IsNotNull(SceneMapobjTexAnimCatalog.Find("SCN0005", "MERRYCHRISTMAS"));
        }

        [Test]
        public void TexAnim_Boat_Screen_Is_Opaque_But_Water_Ripple_Transparent()
        {
            var screen = SceneMapobjTexAnimCatalog.Find("SCN0018", "BOAT_SCREEN");
            Assert.IsNotNull(screen);
            Assert.IsFalse(screen.Transparent, "the boat video screen is opaque");
            Assert.AreEqual(4, screen.Frames.Length);

            var shuimo = SceneMapobjTexAnimCatalog.Find("SCN0018", "SHUIMO");
            Assert.IsNotNull(shuimo);
            Assert.IsTrue(shuimo.Transparent, "the ink-water ripple is an alpha cut-out");
        }

        [Test]
        public void SaloonDeng_Marquee_Pattern_Is_Verbatim()
        {
            // SCN0021 ceiling light bars run a shared 198×12 on/off table (DAT_005518e8), NOT per-prop cyclers — so
            // they're driven by SaloonDengMarquee, not the tex-anim catalog. Verify the embedded table verbatim.
            Assert.IsNull(SceneMapobjTexAnimCatalog.Find("SCN0021", "DENG1"), "deng is the marquee's job, not the catalog");
            Assert.IsNotNull(SceneMapobjCatalog.ForFolder("SCN0021"));   // sanity: the scene still mounts the deng meshes

            var lit = SaloonDengPattern.Lit;
            Assert.AreEqual(198, SaloonDengPattern.Rows);
            Assert.AreEqual(12, SaloonDengPattern.Bars);
            Assert.AreEqual(198, lit.Length);
            Assert.AreEqual(100f, SaloonDengPattern.IntervalMs);

            // rows 0..11 = a single light sweeping bar0 -> bar11 (left -> right across the dome).
            for (int r = 0; r < 12; r++)
            {
                int onCount = 0, onBar = -1;
                for (int b = 0; b < 12; b++) if (lit[r][b]) { onCount++; onBar = b; }
                Assert.AreEqual(1, onCount, "row " + r + " lights exactly one bar");
                Assert.AreEqual(r, onBar, "the lit bar sweeps left->right");
            }
            // tail blackout (final rows all off) and total lit-cell count (576 ones in the EXE).
            for (int b = 0; b < 12; b++) Assert.IsFalse(lit[197][b], "row 197 is full blackout");
            int total = 0;
            foreach (var row in lit) foreach (var on in row) if (on) total++;
            Assert.AreEqual(576, total, "lit-cell count matches DAT_005518e8");
        }

        [Test]
        public void SceneFlame_Scn0022_Is_Three_Billboards_At_150ms()
        {
            // 坟墓 (SCN0022) 鬼火/蓝火: the official draws it as 3 camera-facing BillboardSet sprites (NOT the SHAN.MSH
            // mesh, which is loaded but never linked as a child) animated at 150 ms — StageScene_UpdateLightGroups_004b0b30
            // advances a 0x96=150ms timer, index=(i+1)%3, and writes the frame to all 3 billboards from one shared counter.
            var set = SceneFlameBillboardCatalog.ForFolder("SCN0022");
            Assert.IsNotNull(set);
            Assert.AreEqual(3, set.Frames.Length);
            Assert.AreEqual("FENMUOBJ_LANHUO_01_.TGA", set.Frames[0], "smooth 8-bit TGA (the DXT3 twin bands the glow)");
            Assert.AreEqual(150f, set.IntervalMs, 1e-3f);
            Assert.AreEqual(3, set.Billboards.Length);
            // decompiled billboard positions (030 case 0x16: param_1[0x56/57/58] +0x28/2c/30) + size 100 (0x42c80000).
            Assert.AreEqual(-163.43f, set.Billboards[0].X, 0.01f);
            Assert.AreEqual(40f, set.Billboards[0].Y, 0.01f);
            Assert.AreEqual(135.51f, set.Billboards[0].Z, 0.01f);
            Assert.AreEqual(182.96f, set.Billboards[2].X, 0.01f);
            foreach (var b in set.Billboards) Assert.AreEqual(100f, b.Size, 1e-3f);

            Assert.IsNotNull(SceneFlameBillboardCatalog.ForFolder("scn0022"), "folder key is case-insensitive");
            Assert.IsNull(SceneFlameBillboardCatalog.ForFolder("SCN0008"), "only 坟墓 has flame billboards");
        }

        [Test]
        public void SceneGhost_Scn0022_Flying_Ghosts_Are_Mot_Billboards()
        {
            // 坟墓 flying ghosts (gui/gui2): .mot-driven camera-facing billboards (the official Billboard_AddEntry's them),
            // with a GUI01↔GUI02 texture swing @125 ms. Rendering the flat .mot-baked mesh foreshortened/edge-on ("硬").
            var defs = SceneGhostBillboardCatalog.ForFolder("SCN0022");
            Assert.AreEqual(2, defs.Count);
            var gui = defs[0];
            Assert.AreEqual("LABA11.MOT", gui.Mot);
            Assert.AreEqual(2, gui.Frames.Length);
            Assert.AreEqual("FENMUOBJ_GUI_GUI01_.dds", gui.Frames[0]);
            Assert.AreEqual(125f, gui.IntervalMs, 1e-3f);
            Assert.Greater(gui.Size, 0f);
            Assert.AreEqual("SCENE/MAPOBJ/FENMU/GUI2", defs[1].Dir);

            Assert.AreEqual(2, SceneGhostBillboardCatalog.ForFolder("scn0022").Count);   // folder key case-insensitive
            Assert.AreEqual(0, SceneGhostBillboardCatalog.ForFolder("SCN0008").Count);   // only 坟墓 has ghost billboards
            // the ghost swing is NOT a mapobj-mesh tex-anim (the mesh is skipped) — it lives in the ghost catalog.
            Assert.IsNull(SceneMapobjTexAnimCatalog.Find("SCN0022", "LABA11"));
        }

        [Test]
        public void SceneUvScroll_Scn0022_Spotlights_Force_AlphaBlend()
        {
            // 射光 (sheguang1-3): the MSH loader makes these additive (LooksLikeAdditiveGlow false positive → hard edge,
            // no transparency variation, like SCN0015 窗光). Force two-sided standard alpha-blend so the beam reads soft.
            foreach (var key in new[] { "SHEGUANG", "SHEGUANG2", "SHEGUANG3" })
            {
                Assert.AreEqual(SceneMapobjUvScrollCatalog.RenderMode.AlphaBlendOverlay,
                    SceneMapobjUvScrollCatalog.FindRenderMode("SCN0022", key), key);
                Assert.AreEqual(0f, SceneMapobjUvScrollCatalog.Find("SCN0022", key).sqrMagnitude, 1e-6f, key + " does not UV-scroll");
            }
            // it must NOT be the additive path (that was the wrong fix) and must not leak to other scenes.
            Assert.AreEqual(SceneMapobjUvScrollCatalog.RenderMode.KeepMaterial,
                SceneMapobjUvScrollCatalog.FindRenderMode("SCN0022", "LABA11"));
        }

        [Test]
        public void SmoothAlpha_Debands_Gradient_But_Preserves_Sharp_Detail()
        {
            // DXT3's 4-bit alpha quantises a soft glow to ~9-16 levels → concentric "tree-ring" bands (年輪). SmoothAlpha
            // is a BILATERAL pass: it merges the small quantisation steps into a smooth fade WITHOUT erasing high-contrast
            // detail (the SCN0022 ghost's eye/mouth holes live in the alpha). Synthesise a 16-band gradient + a hard hole.
            const int w = 64, h = 32;
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int level = x * 16 / w;                                  // 16 bands, step 255/15 ≈ 17
                    px[y * w + x] = new Color32(200, 210, 255, (byte)(level * 255 / 15));
                }
            // punch a hard, high-contrast "eye" hole (alpha 0) inside a bright band (alpha ≈ 136) — the "face" detail.
            for (int y = 14; y < 18; y++)
                for (int x = 34; x < 40; x++)
                    px[y * w + x] = new Color32(200, 210, 255, 0);

            int before = DistinctAlpha(px);
            DdsLoader.SmoothAlpha(px, w, h);
            int after = DistinctAlpha(px);
            Assert.LessOrEqual(before, 17, "the synthetic gradient starts banded (≤16 levels + 0)");
            Assert.Greater(after, before * 2, "de-banding introduces many intermediate alpha levels → no visible rings");
            Assert.Less(px[16 * w + 37].a, 40, "the hard hole (the face) is PRESERVED, not blurred away");
            Assert.AreEqual(200, px[16 * w + 10].r, "RGB is untouched — only alpha is smoothed");
            Assert.AreEqual(255, px[16 * w + 10].b);
        }

        private static int DistinctAlpha(Color32[] px)
        {
            var seen = new bool[256]; int n = 0;
            foreach (var p in px) if (!seen[p.a]) { seen[p.a] = true; n++; }
            return n;
        }

        [Test]
        public void TexAnim_Lookup_Misses_Are_Null()
        {
            Assert.IsNull(SceneMapobjTexAnimCatalog.Find("SCN0005", "NOT_A_PROP"));
            Assert.IsNull(SceneMapobjTexAnimCatalog.Find("SCN9999", "CHRISTMAS"));
        }

        [Test]
        public void SceneEft_Scn0008_Is_The_Magic_Circle()
        {
            var fx = SceneEftCatalog.ForFolder("SCN0008");
            Assert.AreEqual(1, fx.Count);
            Assert.AreEqual("kikkai_3", fx[0].Eft);
            Assert.AreEqual(0f, fx[0].X); Assert.AreEqual(0f, fx[0].Y); Assert.AreEqual(0f, fx[0].Z);
            Assert.AreEqual(180f, fx[0].Ey, 1e-3f);   // Y-rotated 180° (decompiled 0x43340000)
            Assert.AreEqual(40f, fx[0].Scale, 1e-3f); // scale 40 (0x42200000)
        }

        [Test]
        public void SceneEft_Multi_And_Empty()
        {
            var scn0011 = SceneEftCatalog.ForFolder("SCN0011");
            Assert.AreEqual(8, scn0011.Count);   // 2 bgl + 2 gravcolor + 4 stagelightb
            for (int i = 4; i < scn0011.Count; i++)
            {
                Assert.AreEqual(0f, scn0011[i].Ex, 1e-3f);
                Assert.AreEqual(0f, scn0011[i].Ey, 1e-3f);
                Assert.AreEqual(0f, scn0011[i].Ez, 1e-3f);
            }
            Assert.AreEqual(5, SceneEftCatalog.ForFolder("SCN0014").Count);   // aurora + 4 bubbles
            Assert.AreEqual(0, SceneEftCatalog.ForFolder("SCN0009").Count);   // GUATAN scene has no bg EFT
            Assert.AreEqual(0, SceneEftCatalog.ForFolder(null).Count);
            // personal room + wedding hall share the same effect set
            Assert.AreEqual(3, SceneEftCatalog.ForFolder("SCN0037").Count);
            Assert.AreEqual(SceneEftCatalog.ForFolder("SCN0037").Count, SceneEftCatalog.ForFolder("SCN0038").Count);
        }

        [Test]
        public void SceneEft_Scn0015_Fire3_And_Booklights()
        {
            var fx = SceneEftCatalog.ForFolder("SCN0015");
            Assert.AreEqual(1, fx.Count);   // fire3; booklight is bone-attached to SHU1-4

            // fire3: from decompiled Effect_Play(0x35) + Effect_SetTransformAnimated coords
            var fire3 = fx[0];
            Assert.AreEqual("fire3", fire3.Eft);
            Assert.AreEqual(55.15f, fire3.X, 0.01f);
            Assert.AreEqual(339.83f, fire3.Y, 0.01f);
            Assert.AreEqual(1237.66f, fire3.Z, 0.01f);
            Assert.AreEqual(100f, fire3.Scale, 1e-3f);
        }

        [Test]
        public void SceneAttachedEft_Scn0015_Booklights_Follow_Official_Bones()
        {
            var map = new[]
            {
                ("15_SHU1", "Plane29"),
                ("15_SHU2", "Plane66"),
                ("15_SHU3", "Plane33"),
                ("15_SHU4", "Plane31"),
            };

            foreach (var (mapobj, bone) in map)
            {
                var attached = SceneAttachedEftCatalog.ForMapobj("SCN0015", mapobj);
                Assert.AreEqual(1, attached.Count);
                Assert.AreEqual("booklight", attached[0].Eft);
                Assert.AreEqual(bone, attached[0].Bone);
                Assert.AreEqual(0f, attached[0].Offset.x, 1e-3f);
                Assert.AreEqual(-5f, attached[0].Offset.y, 1e-3f);
                Assert.AreEqual(0f, attached[0].Offset.z, 1e-3f);
                Assert.AreEqual(20f, attached[0].Scale, 1e-3f);
            }
        }

        [Test]
        public void SceneEftRender_Scn0015_Profiles_Fire_And_Book_Glow()
        {
            var flame = SceneEftRenderCatalog.Find("fire3", 2, 84);
            Assert.Greater(flame.RgbMul, 1f);
            Assert.Greater(flame.AlphaMul, 1f);
            Assert.AreEqual(1f, flame.ScaleMul.x, 1e-3f);
            Assert.AreEqual(1f, flame.ScaleMul.y, 1e-3f);

            var halo = SceneEftRenderCatalog.Find("fire3", 4, 30);
            Assert.Less(halo.RgbMul, 1f);
            Assert.Less(halo.AlphaMul, 1f);
            Assert.Greater(halo.ScaleMul.x, 0.5f);
            Assert.Greater(halo.ScaleMul.y, 0.5f);

            // booklight slot2 = 書上的發光球：載體 baseSize 0.3 乘進來後球只有 ~14 世界單位（「發光太小」），
            // 所以 scaleMul 放大它（>2×）、rgbMul 壓暗顏色，alpha 維持原樣不動。
            var book = SceneEftRenderCatalog.Find("booklight", 2, 31);
            Assert.Less(book.RgbMul, 1f);
            Assert.AreEqual(1f, book.AlphaMul, 1e-3f);
            Assert.Greater(book.ScaleMul.x, 2f);
            Assert.AreEqual(1f, SceneEftRenderCatalog.Find("booklight", 0, 0).ScaleMul.x, 1e-3f);
        }

        /// <summary>SCENE/MAPOBJ under the real data root, or null when the game data isn't present.</summary>
        private static string MapobjDir()
        {
            try
            {
                var dir = Path.Combine(SdoExtracted.Root, Path.Combine("SCENE", "MAPOBJ"));
                return Directory.Exists(dir) ? dir : null;
            }
            catch { return null; }
        }

        private static uint FlagOf(System.Collections.Generic.List<(string Name, uint Flags)> table, string dds)
        {
            foreach (var m in table)
                if (string.Equals(Path.GetFileName(m.Name.Replace('\\', '/')), dds, StringComparison.OrdinalIgnoreCase))
                    return m.Flags;
            Assert.Fail("material '" + dds + "' not in the mesh's material table");
            return 0u;
        }

        [Test]
        public void RealData_Scn0014_Beam_And_Scn0025_Water_Are_Official_Transparent()
        {
            var root = MapobjDir();
            if (root == null) Assert.Ignore("SCENE/MAPOBJ data root not found — real-data row needs the game data (data_root.txt)");

            // 舞台中間的投影光:單材質,官方旗標 1 = 透明批。
            var guang = MshLoader.ReadMaterialTable(File.ReadAllBytes(Path.Combine(root, "14_HAIDI/GUANG/GUANG.MSH")));
            Assert.IsNotEmpty(guang);
            Assert.IsTrue(MshLoader.IsOfficialTransparent(FlagOf(guang, "touyingguang_.dds")));

            // 旋轉小電視:5 個材質,只有那道光是透明的 —— 所以 OfficialMaterialAlpha 必須逐材質套,
            // 不然螢幕/框架/投影機也會跟著變半透明。
            var tv = MshLoader.ReadMaterialTable(File.ReadAllBytes(Path.Combine(root, "14_HAIDI/TV/TV.MSH")));
            Assert.GreaterOrEqual(tv.Count, 5);
            Assert.IsTrue(MshLoader.IsOfficialTransparent(FlagOf(tv, "touyingguang_.dds")), "電視射出來的光");
            foreach (var opaque in new[] { "zhuanpan.dds", "gangjia.dds", "tv.dds", "touyingji_c.dds" })
                Assert.IsFalse(MshLoader.IsOfficialTransparent(FlagOf(tv, opaque)), opaque + " 官方是實心的");

            // 噴水池的水:旗標 0x11。
            var water = MshLoader.ReadMaterialTable(File.ReadAllBytes(Path.Combine(root, "CHUNTIAN/DONGHUA/CHUNTIANDONGHUA.MSH")));
            Assert.AreEqual(0x11u, FlagOf(water, "shui_c_.dds"));
        }

        [Test]
        public void RealData_Scn0014_Beam_Mesh_Unwraps_Around_Its_Axis_In_U()
        {
            var root = MapobjDir();
            if (root == null) Assert.Ignore("SCENE/MAPOBJ data root not found");

            // 光是 6 段圓錐:U 沿著繞軸方向鋪滿 0..1、V 只有兩排(頂圈/底圈)。這就是為什麼官方捲的是 U —— 捲 U
            // 才會讀成「光紋繞著光軸轉」。若哪天有人把 BeamSpinU 改成捲 V,這條會擋下來。
            var r = MshLoader.Load(File.ReadAllBytes(Path.Combine(root, "14_HAIDI/GUANG/GUANG.MSH")));
            Assert.IsNotNull(r);
            Assert.AreEqual(1, r.Submeshes.Count);
            var uv = r.Submeshes[0].Mesh.uv;
            Assert.AreEqual(14, uv.Length, "6 段圓錐 = 14 頂點 / 12 三角形");
            float uMin = 1f, uMax = 0f;
            var vs = new System.Collections.Generic.HashSet<int>();
            foreach (var t in uv)
            {
                uMin = Mathf.Min(uMin, t.x); uMax = Mathf.Max(uMax, t.x);
                vs.Add(Mathf.RoundToInt(t.y * 1000f));
            }
            Assert.Less(uMin, 0.05f, "U 從接近 0 開始");
            Assert.Greater(uMax, 0.95f, "U 鋪到接近 1(繞完一圈)");
            Assert.AreEqual(2, vs.Count, "V 只有兩排 → V 不是可捲的方向");
            foreach (var sub in r.Submeshes) UnityEngine.Object.DestroyImmediate(sub.Mesh);
        }

        [Test]
        public void RealData_Scn0025_Butterfly_Wing_Frames_Exist_On_Disk()
        {
            var root = MapobjDir();
            if (root == null) Assert.Ignore("SCENE/MAPOBJ data root not found");

            // catalog 的幀名要真的對得上磁碟(這正是「MSH 材質 01.dds 只是佔位第一幀」的那組)。
            var groups = new (string Folder, string Mesh)[]
            {
                ("CHUNTIAN/HUTEIDONGHUA", "HUDEICHUNTIANDONGHUA"),
                ("CHUNTIAN/HUDIEDONGHUA2", "HUDEICHUNTIANDONGHUA2"),
                ("CHUNTIAN/HUDIEDONGHUA3", "HUDEICHUNTIANDONGHUA3"),
                ("CHUNTIAN/HUDIEDONGHUA4", "HUDEICHUNTIANDONGHUA4"),
            };
            foreach (var (folder, mesh) in groups)
            {
                var anim = SceneMapobjTexAnimCatalog.Find("SCN0025", mesh);
                Assert.IsNotNull(anim, mesh);
                foreach (var f in anim.Frames)
                    Assert.IsTrue(File.Exists(Path.Combine(Path.Combine(root, folder), f)),
                        folder + "/" + f + " 不存在 → 換幀會退回佔位貼圖");
            }
        }

        [Test]
        public void MshLoader_IsOfficialTransparent_Matches_Engine_Mask()
        {
            // 引擎唯一的判定是 flags & 0x3f != 0(非 0 → 延後透明批)。全語料只有 0/1/2/0x11/0x12 五種值。
            Assert.IsFalse(MshLoader.IsOfficialTransparent(0u), "0 = 不透明批");
            Assert.IsTrue(MshLoader.IsOfficialTransparent(1u), "SCN0014 投影光 / SCN0016 地板光條");
            Assert.IsTrue(MshLoader.IsOfficialTransparent(2u));
            Assert.IsTrue(MshLoader.IsOfficialTransparent(0x11u), "SCN0025 噴水池的水");
            Assert.IsTrue(MshLoader.IsOfficialTransparent(0x12u));
            Assert.IsFalse(MshLoader.IsOfficialTransparent(0x40u), "遮罩外的高位元不算透明");
        }

        [Test]
        public void SceneUvScroll_Scn0014_ProjectorBeams_Defer_To_Official_Flags()
        {
            // 舞台中間的 GUANG 與旋轉電視射出的光都貼 TOUYINGGUANG_.DDS(亮 RGB + 幾乎全軟 alpha)→
            // LooksLikeAdditiveGlow 誤判成加法 → 疊出一塊死白(使用者:「光沒去背」)。官方旗標說它是一般透明批。
            foreach (var key in new[] { "GUANG", "TV" })
                Assert.AreEqual(SceneMapobjUvScrollCatalog.RenderMode.OfficialMaterialAlpha,
                    SceneMapobjUvScrollCatalog.FindRenderMode("SCN0014", key), key);
            // 不能外溢到同場景其他道具/別的場景。
            Assert.AreEqual(SceneMapobjUvScrollCatalog.RenderMode.KeepMaterial,
                SceneMapobjUvScrollCatalog.FindRenderMode("SCN0014", "SEA_SCREEN"));
            Assert.AreEqual(SceneMapobjUvScrollCatalog.RenderMode.KeepMaterial,
                SceneMapobjUvScrollCatalog.FindRenderMode("SCN0020", "TV1"), "只有海底那台電視");
        }

        [Test]
        public void SceneUvScroll_Scn0014_Beam_Spins_In_U_And_Coral_Splits_Into_Two_Rates()
        {
            // FUN_004b0330 每 50 ms 寫 7 個貼圖座標:3× 珊瑚樹 V=−t、3× 珊瑚枝 V=−2t、第 7 個(唯一寫 U、4× 速率)
            // = 投影光。光的 mesh 是 6 段圓錐展開(U 0.018‥0.976、V 只有兩排),所以捲 U = 光紋繞著光軸自轉,
            // 疊在 .mot 帶著整支道具繞場公轉之上(它的 .mot 只有一根動畫骨,不可能另外自轉)。
            var beam = SceneMapobjUvScrollCatalog.Find("SCN0014", "GUANG");
            Assert.AreEqual(0.32f, beam.x, 1e-6f, "U += 0.016 per 50 ms = 4× 珊瑚速率");
            Assert.AreEqual(0f, beam.y, 1e-6f, "光只捲 U(官方 V 明確寫 0)");

            foreach (var tree in new[] { "SHANHU-BAI", "SHANHU-HONG", "SHANHU-LV" })
            {
                var s = SceneMapobjUvScrollCatalog.Find("SCN0014", tree);
                Assert.AreEqual(0f, s.x, 1e-6f, tree);
                Assert.AreEqual(-0.08f, s.y, 1e-6f, tree + " 是 1× 那組");
            }
            foreach (var branch in new[] { "SHANHUZHI-BAI", "SHANHUZHI-HONG", "SHANHUZHI-LV" })
            {
                var s = SceneMapobjUvScrollCatalog.Find("SCN0014", branch);
                Assert.AreEqual(0f, s.x, 1e-6f, branch);
                Assert.AreEqual(-0.16f, s.y, 1e-6f, branch + " 是 2× 那組");
            }
        }

        [Test]
        public void SceneUvScroll_Scn0025_Fountain_Flows_And_Defers_To_Official_Flags()
        {
            // FUN_004b0d20 最後一段:每 50 ms 設 U=0、V += _DAT_00589044(0.05)⇒ +1.0 UV/s(到 1.0 歸零)。
            var speed = SceneMapobjUvScrollCatalog.Find("SCN0025", "CHUNTIANDONGHUA");
            Assert.AreEqual(0f, speed.x, 1e-6f);
            Assert.AreEqual(1.0f, speed.y, 1e-6f);
            // 水的官方旗標是 0x11(透明批);啟發式把它當加法輝光 → 噴出來的水變成死白一片。
            Assert.AreEqual(SceneMapobjUvScrollCatalog.RenderMode.OfficialMaterialAlpha,
                SceneMapobjUvScrollCatalog.FindRenderMode("SCN0025", "CHUNTIANDONGHUA"));
            Assert.IsFalse(SceneMapobjUvScrollCatalog.UsesAdditiveOverlay("SCN0025", "CHUNTIANDONGHUA"));
            // 同場景的蝴蝶/草不捲、不改材質(蝴蝶走換幀)。
            Assert.AreEqual(0f, SceneMapobjUvScrollCatalog.Find("SCN0025", "HUDEICHUNTIANDONGHUA").sqrMagnitude, 1e-6f);
            Assert.AreEqual(SceneMapobjUvScrollCatalog.RenderMode.KeepMaterial,
                SceneMapobjUvScrollCatalog.FindRenderMode("SCN0025", "CAOCHUNTIANDONGHUA"));
        }

        [Test]
        public void TexAnim_Scn0025_Butterflies_Flap_At_Official_Intervals()
        {
            // 四群蝴蝶各自一組 4 幀翅膀(Scene_LoadBackground case 0x19 載 CHUNTIAN_HUDEI<N>0..3),
            // FUN_004b0d20 用各自的計時器推進:0x32/0x28/0x3c/0x1e ms、index = (i+1) & 3。
            var expect = new (string Mesh, string Prefix, float Ms)[]
            {
                ("HUDEICHUNTIANDONGHUA",  "CHUNTIAN_HUDEI1", 50f),
                ("HUDEICHUNTIANDONGHUA2", "CHUNTIAN_HUDEI2", 40f),
                ("HUDEICHUNTIANDONGHUA3", "CHUNTIAN_HUDEI3", 60f),
                ("HUDEICHUNTIANDONGHUA4", "CHUNTIAN_HUDEI4", 30f),
            };
            foreach (var (mesh, prefix, ms) in expect)
            {
                var anim = SceneMapobjTexAnimCatalog.Find("SCN0025", mesh);
                Assert.IsNotNull(anim, mesh + " 少了翅膀換幀 → 蝴蝶飛但不拍翅膀");
                Assert.AreEqual(4, anim.Frames.Length, mesh);
                Assert.AreEqual(prefix + "0.dds", anim.Frames[0], mesh + " 幀從 0 起算");
                Assert.AreEqual(prefix + "3.dds", anim.Frames[3], mesh);
                Assert.AreEqual(ms, anim.IntervalMs, 1e-3f, mesh);
                Assert.IsTrue(anim.Transparent, mesh + " 是去背的蝴蝶剪影");
                Assert.IsFalse(anim.HoldLast, mesh + " 是循環拍翅膀,不是播一次");
            }
            // 水/草不是換幀(水靠 UV 捲動、草靠 .mot)。
            Assert.IsNull(SceneMapobjTexAnimCatalog.Find("SCN0025", "CHUNTIANDONGHUA"));
            Assert.IsNull(SceneMapobjTexAnimCatalog.Find("SCN0025", "CAOCHUNTIANDONGHUA"));
        }

        [Test]
        public void SceneUvScroll_Scn0015_WindowBeam_Uses_Mapobj_Target()
        {
            var speed = SceneMapobjUvScrollCatalog.Find("SCN0015", "15_UV");
            Assert.AreEqual(0f, speed.x, 1e-6f);
            Assert.AreEqual(0.06f, speed.y, 1e-6f);
            // D3D9 實測窗光是標準 alpha-blend（SRCALPHA/INVSRCALPHA），不是 additive：MSH loader 的
            // LooksLikeAdditiveGlow 會把 GUANG1_.DDS 誤判成輝光 → 用 ForceAlphaBlend 蓋掉它。
            Assert.AreEqual(SceneMapobjUvScrollCatalog.RenderMode.ForceAlphaBlend,
                SceneMapobjUvScrollCatalog.FindRenderMode("SCN0015", "15_UV"));
            Assert.IsFalse(SceneMapobjUvScrollCatalog.UsesAdditiveOverlay("SCN0015", "15_UV"));
            Assert.AreEqual(0f, SceneMapobjUvScrollCatalog.Find("SCN0015", "15_HUA").sqrMagnitude, 1e-6f);
            Assert.IsFalse(SceneMapobjUvScrollCatalog.UsesAdditiveOverlay("SCN0015", "15_HUA"));
        }
    }
}
