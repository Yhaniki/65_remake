using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    // Guards the fix for 使用者回報「服裝 000004 無法顯示」: a 商城 garment whose mesh exists but whose CLOTH texture
    // can't be resolved (000004_MAN_COAT's main submesh names a GBK-junk「未标题-1副本_.dds」 and has no own COAT .dds)
    // only ever renders as a flat fallback colour, so AvatarItemCatalog.IsRenderable must hide it. Two pure pieces:
    //   • AvatarItemCatalog.ClothTextureResolvable — the render/hide decision from a mesh's material names.
    //   • MshLoader.ReadMaterialNames             — pulls those names out of a raw .msh without building a Unity mesh.
    public class ClothRenderabilityTests
    {
        private static System.Func<string, bool> Set(params string[] names)
        {
            var set = new HashSet<string>(names);
            return set.Contains;
        }
        private static readonly System.Func<string, bool> None = _ => false;

        // ---- ClothTextureResolvable (the pure render/hide decision) ----

        [Test]
        public void Broken000004_JunkNameAndSkinOnly_IsNotResolvable()
        {
            // 000004_MAN_COAT: cloth submesh = GBK junk "未标题-1副本_.dds" (resolves to nothing), skin submesh = M_Basic_Coat.
            var names = new[] { "未标题-1副本_.dds", "M_Basic_Coat.dds" };
            Assert.IsFalse(AvatarItemCatalog.ClothTextureResolvable(names, Set("M_Basic_Coat.dds"), None),
                "a garment whose only real material is unresolvable junk must be hidden");
        }

        [Test]
        public void NormalCoat_OwnClothTextureResolves()
        {
            var names = new[] { "000044_man_coat.dds", "M_Basic_Coat.dds" };
            Assert.IsTrue(AvatarItemCatalog.ClothTextureResolvable(names, Set("000044_man_coat.dds", "M_Basic_Coat.dds"), None));
        }

        [Test]
        public void CrossReferencedTexture_Resolves()
        {
            // many family-less garments legitimately name ANOTHER id's texture (020789_MAN_COAT → 020789_woman_coat.dds).
            var names = new[] { "020789_woman_coat.dds" };
            Assert.IsTrue(AvatarItemCatalog.ClothTextureResolvable(names, Set("020789_woman_coat.dds"), None));
        }

        [Test]
        public void TexAnimPlaceholder_ResolvesWhenAnFrameListExists()
        {
            // animated garments name a _TexAnimEx(...) placeholder; the real texture is the "<inner>.an" frame list.
            var names = new[] { "_texanimex(011109_man_coat)100_1.dds" };
            Assert.IsTrue(AvatarItemCatalog.ClothTextureResolvable(names, None, Set("011109_man_coat")),
                "a _TexAnimEx garment renders via its .an frame list");
            Assert.IsFalse(AvatarItemCatalog.ClothTextureResolvable(names, None, None),
                "…but not when the .an frame list is missing too");
        }

        [Test]
        public void SkinBaseOnly_IsNotResolvable()
        {
            Assert.IsFalse(AvatarItemCatalog.ClothTextureResolvable(new[] { "M_Basic_Coat.dds", "" }, Set("M_Basic_Coat.dds"), None),
                "a mesh with only the shared skin-base material has no garment texture");
        }

        [Test]
        public void EmptyOrNull_IsNotResolvable()
        {
            Assert.IsFalse(AvatarItemCatalog.ClothTextureResolvable(new string[0], Set("x.dds"), None));
            Assert.IsFalse(AvatarItemCatalog.ClothTextureResolvable(null, Set("x.dds"), None));
            Assert.IsFalse(AvatarItemCatalog.ClothTextureResolvable(new string[] { null, "" }, Set("x.dds"), None));
        }

        // ---- MshLoader.ReadMaterialNames (raw .msh → material names, no Unity mesh) ----

        // Build a minimal one-submesh .msh with the given material names, matching the layout ParseSubmesh reads:
        // "Mesh00000030" | u32 submeshCount | [ u32 fvf | u32 idxSize | u32 opt | idx bytes | u32 vertSize | u32 stride |
        //  vert bytes | u32 reserved[6] | u32 numMat | numMat × 408-byte material (name @ +68) ].
        private static byte[] BuildMsh(params string[] materialNames)
        {
            var b = new List<byte>();
            void U32(int v) { b.Add((byte)v); b.Add((byte)(v >> 8)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 24)); }
            b.AddRange(Encoding.ASCII.GetBytes("Mesh00000030"));
            U32(1);                       // submeshCount
            U32(0x1156);                  // fvf (stride-40 single-bone)
            U32(6);                       // idxSize (3 ushort indices)
            U32(101);                     // options
            b.AddRange(new byte[6]);      // indices (0,0,0 — value irrelevant to name reading)
            U32(120);                     // vertSize = stride*3
            U32(40);                      // stride
            b.AddRange(new byte[120]);    // vertex block
            for (int i = 0; i < 6; i++) U32(0);   // reserved[6]
            U32(materialNames.Length);    // numMat
            foreach (var nm in materialNames)
            {
                int start = b.Count;
                b.AddRange(new byte[408]);
                var raw = Encoding.ASCII.GetBytes(nm ?? "");
                for (int i = 0; i < raw.Length && i < 319; i++) b[start + 17 * 4 + i] = raw[i];   // name @ +68, NUL-terminated
            }
            return b.ToArray();
        }

        [Test]
        public void ReadMaterialNames_SingleMaterial()
        {
            CollectionAssert.AreEqual(new[] { "000044_man_coat.dds" },
                MshLoader.ReadMaterialNames(BuildMsh("000044_man_coat.dds")));
        }

        [Test]
        public void ReadMaterialNames_ClothPlusSkin()
        {
            CollectionAssert.AreEqual(new[] { "hello_coat.dds", "M_Basic_Coat.dds" },
                MshLoader.ReadMaterialNames(BuildMsh("hello_coat.dds", "M_Basic_Coat.dds")));
        }

        [Test]
        public void ReadMaterialNames_MalformedBuffer_ReturnsEmpty()
        {
            Assert.IsEmpty(MshLoader.ReadMaterialNames(null));
            Assert.IsEmpty(MshLoader.ReadMaterialNames(Encoding.ASCII.GetBytes("not a mesh at all")));
            Assert.IsEmpty(MshLoader.ReadMaterialNames(new byte[4]));
        }

        [Test]
        public void ReadMaterialNames_FeedsResolvable_EndToEnd()
        {
            // the two pieces compose: names read from a mesh feed the render/hide decision.
            var names = MshLoader.ReadMaterialNames(BuildMsh("000044_man_coat.dds", "M_Basic_Coat.dds"));
            Assert.IsTrue(AvatarItemCatalog.ClothTextureResolvable(names, Set("000044_man_coat.dds"), None));

            var broken = MshLoader.ReadMaterialNames(BuildMsh("未标题-1副本_.dds", "M_Basic_Coat.dds"));
            Assert.IsFalse(AvatarItemCatalog.ClothTextureResolvable(broken, Set("M_Basic_Coat.dds"), None));
        }
    }
}
