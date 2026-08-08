using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Game;
using Sdo.Shop;

namespace Sdo.Tests
{
    /// <summary>Guards <see cref="AvatarItemCatalog.ShouldSynthGarment"/> — the phantom-duplicate filter for the 商城
    /// mesh-only synth rows. A modelId named in one body-outfit slot (上衣/下裝/連身) ships companion COAT/PANT/ONE
    /// meshes for the SAME design; synthesising a mesh-only row for the OTHER slot listed a visual duplicate
    /// (使用者回報: 合成上衣「001278」==「性感嘻哈」001277、「002247」==「快乐舞会」2247 顯示同一件衣服). Pure logic — the
    /// set of named body-outfit (sex, modelId) is injected, so no Unity/disk is needed.</summary>
    public class AvatarItemCatalogSynthTests
    {
        // 快乐舞会 = 連身裙 (cat 150, model 2247); 野战迷彩中裤 = 下裝 (cat 103, model 1278). Both female-named body outfits.
        private static ISet<(ItemSex, int)> Named =>
            new HashSet<(ItemSex, int)> { (ItemSex.Female, 2247), (ItemSex.Female, 1278), (ItemSex.Female, 1277) };

        [Test]
        public void NamedOnePiece_SuppressesSynthTop_SameModelId()
        {
            // 快乐舞会 is a named 連身裙 (2247). Its 002247_WOMAN_COAT.MSH must NOT become a phantom 上衣 "002247".
            Assert.IsFalse(AvatarItemCatalog.ShouldSynthGarment(ItemSex.Female, EquipSlot.Top, 2247, Named));
        }

        [Test]
        public void NamedBottom_SuppressesSynthTop_CompanionMesh()
        {
            // 野战迷彩中裤 is a named 下裝 (1278). Its companion 001278_WOMAN_COAT.MSH (which re-uses 001277 的 coat 貼圖)
            // must NOT become a phantom 上衣 "001278" duplicating 性感嘻哈.
            Assert.IsFalse(AvatarItemCatalog.ShouldSynthGarment(ItemSex.Female, EquipSlot.Top, 1278, Named));
        }

        [Test]
        public void NamedTop_SuppressesSynthBottom_ReverseCompanion()
        {
            // 性感嘻哈 is a named 上衣 (1277). Its companion 001277_WOMAN_PANT.MSH (re-uses 001278 的 pant 貼圖) must NOT
            // become a phantom 下裝 "001277".
            Assert.IsFalse(AvatarItemCatalog.ShouldSynthGarment(ItemSex.Female, EquipSlot.Bottom, 1277, Named));
        }

        [Test]
        public void UnnamedMeshModel_IsSynthesised()
        {
            // A modelId that no named row claims stays a browsable mesh-only row (無名的也上架/序號當名字).
            Assert.IsTrue(AvatarItemCatalog.ShouldSynthGarment(ItemSex.Female, EquipSlot.Top, 9999, Named));
        }

        [Test]
        public void OtherGender_IsNotSuppressed()
        {
            // The named set is female; a MALE synth of the same numeric modelId is a different mesh family → allowed.
            Assert.IsTrue(AvatarItemCatalog.ShouldSynthGarment(ItemSex.Male, EquipSlot.Top, 2247, Named));
        }

        [Test]
        public void NonBodyOutfitSlot_IsNeverSuppressed()
        {
            // 髮型/鞋子/翅膀/表情 don't share a modelId across a 上↔下 split, so the plain "any mesh" rule stands even when
            // the id happens to be a named body outfit.
            Assert.IsTrue(AvatarItemCatalog.ShouldSynthGarment(ItemSex.Female, EquipSlot.Shoes, 2247, Named));
            Assert.IsTrue(AvatarItemCatalog.ShouldSynthGarment(ItemSex.Female, EquipSlot.Hair, 2247, Named));
            Assert.IsTrue(AvatarItemCatalog.ShouldSynthGarment(ItemSex.Female, EquipSlot.Wings, 2247, Named));
        }

        [Test]
        public void NullNamedSet_AllowsSynth()
        {
            Assert.IsTrue(AvatarItemCatalog.ShouldSynthGarment(ItemSex.Female, EquipSlot.Top, 2247, null));
        }

        // ---- 「假鞋子」: 下裝 companion 幾何被合成成鞋子 (使用者: 003541/003542 卡片空白) ----

        [Test]
        public void ShoesTextureIsBottomCopy_IdenticalBytes_IsCompanion()
        {
            // 003541/003542 的 {id}_WOMAN_SHOES.DDS 與 {id}_WOMAN_PANT.DDS 位元組完全相同 = 沒畫鞋、只複製了下裝貼圖。
            var pant = new byte[] { 0x44, 0x44, 0x53, 0x20, 1, 2, 3, 4 };
            var shoes = (byte[])pant.Clone();
            Assert.IsTrue(AvatarItemCatalog.ShoesTextureIsBottomCopy(shoes, pant));
        }

        [Test]
        public void ShoesTextureIsBottomCopy_RealShoeTexture_IsNot()
        {
            // 對照組 (027643 女鞋同 id 也有 PANT，但鞋貼圖是自己畫的)：內容不同 → 照常上架。
            var pant = new byte[] { 0x44, 0x44, 0x53, 0x20, 1, 2, 3, 4 };
            var shoes = new byte[] { 0x44, 0x44, 0x53, 0x20, 1, 2, 3, 9 };
            Assert.IsFalse(AvatarItemCatalog.ShoesTextureIsBottomCopy(shoes, pant));
            Assert.IsFalse(AvatarItemCatalog.ShoesTextureIsBottomCopy(shoes, new byte[] { 0x44 }));   // 長度不同 → 先擋掉
        }

        [Test]
        public void ShoesTextureIsBottomCopy_MissingOrEmpty_IsNot()
        {
            // 讀不到貼圖不能被當成 companion，否則 IO 失敗會默默讓整批鞋子從商城消失。
            Assert.IsFalse(AvatarItemCatalog.ShoesTextureIsBottomCopy(null, new byte[] { 1 }));
            Assert.IsFalse(AvatarItemCatalog.ShoesTextureIsBottomCopy(new byte[] { 1 }, null));
            Assert.IsFalse(AvatarItemCatalog.ShoesTextureIsBottomCopy(new byte[0], new byte[0]));
        }

        // ---- 套装拆件用到的 mesh 檔名 → 部位 / synth id 來回 ----

        [Test]
        public void SlotFromMeshFileName_MapsEveryWornToken()
        {
            Assert.AreEqual(EquipSlot.Top,        AvatarItemCatalog.SlotFromMeshFileName("023425_WOMAN_COAT.MSH"));
            Assert.AreEqual(EquipSlot.Bottom,     AvatarItemCatalog.SlotFromMeshFileName("023426_WOMAN_PANT.MSH"));
            Assert.AreEqual(EquipSlot.Hair,       AvatarItemCatalog.SlotFromMeshFileName("023424_WOMAN_HAIR.MSH"));
            Assert.AreEqual(EquipSlot.Shoes,      AvatarItemCatalog.SlotFromMeshFileName("023427_MAN_SHOES.MSH"));
            Assert.AreEqual(EquipSlot.Gloves,     AvatarItemCatalog.SlotFromMeshFileName("023428_MAN_HAND.MSH"));
            Assert.AreEqual(EquipSlot.Glasses,    AvatarItemCatalog.SlotFromMeshFileName("000673_WOMAN_GLASS.MSH"));
            Assert.AreEqual(EquipSlot.OnePiece,   AvatarItemCatalog.SlotFromMeshFileName("002247_WOMAN_ONE.MSH"));
            Assert.AreEqual(EquipSlot.Wings,      AvatarItemCatalog.SlotFromMeshFileName("023108_WOMAN_CHIBANG.MSH"));
            Assert.AreEqual(EquipSlot.Necklace,   AvatarItemCatalog.SlotFromMeshFileName("023109_WOMAN_LINGDANG.MSH"));
            Assert.AreEqual(EquipSlot.Expression, AvatarItemCatalog.SlotFromMeshFileName("023110_WOMAN_FACE_HUAN.MSH"));
            // 素體臉是身體幾何,不是商品 → 不會被拆成一件衣服。
            Assert.AreEqual(EquipSlot.None,       AvatarItemCatalog.SlotFromMeshFileName("900007_WOMAN_FACE.MSH"));
        }

        [Test]
        public void SynthId_RoundTrips_ForTheOutfitOnlySlots()
        {
            // 手套/眼鏡/連身 只有「套装拆件」會合成 → 這三個 synth id 必須解得回同一個部位+性別+mesh,
            // 否則買了整套、重開遊戲後那幾件就從儲物櫃消失。
            var slots = new[] { EquipSlot.Gloves, EquipSlot.Glasses, EquipSlot.OnePiece };
            var tokens = new[] { "HAND", "GLASS", "ONE" };
            for (int i = 0; i < slots.Length; i++)
                foreach (var sex in new[] { ItemSex.Male, ItemSex.Female })
                {
                    string mesh = "023425_" + (sex == ItemSex.Male ? "MAN" : "WOMAN") + "_" + tokens[i] + ".MSH";
                    int id = AvatarItemCatalog.SynthId(slots[i], sex, 23425);
                    var it = AvatarItemCatalog.SynthesizeSynthItem(id, f => f == mesh);
                    Assert.IsNotNull(it, mesh + " 解不回商品列");
                    Assert.AreEqual(slots[i], it.EquipSlot, mesh);
                    Assert.AreEqual(23425, it.ModelId, mesh);
                    Assert.AreEqual("AVATAR/" + mesh, it.MshRelPath, mesh);
                }
        }

        [Test]
        public void SynthId_NewSlotCodes_DoNotCollideWithTheOldOnes()
        {
            var ids = new HashSet<int>();
            var slots = new[] { EquipSlot.Hair, EquipSlot.Top, EquipSlot.Bottom, EquipSlot.Shoes,
                                EquipSlot.Gloves, EquipSlot.Glasses, EquipSlot.OnePiece };
            foreach (var slot in slots)
                foreach (var sex in new[] { ItemSex.Male, ItemSex.Female })
                    Assert.IsTrue(ids.Add(AvatarItemCatalog.SynthId(slot, sex, 23425)), slot + "/" + sex + " id 撞號");
            Assert.IsTrue(ids.Add(AvatarItemCatalog.SynthId(EquipSlot.Wings, ItemSex.Female, 23425)));   // 附件仍是 bare id
        }
    }
}
