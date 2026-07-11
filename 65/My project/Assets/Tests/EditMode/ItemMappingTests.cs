using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Shop;
using Sdo.Game;

namespace Sdo.Tests
{
    public class ItemMappingTests
    {
        private static ShopItem Item(int model, int cat)
            => new ShopItem { Id = 1, ModelId = model, Category = cat, Quantity = -1, Name = "x" };

        [Test]
        public void MshRelPath_FemaleShoes_ZeroPaddedModelId()
        {
            // real example: [14657] 长靴, modelId 12657, cat 105 (female shoes) -> 012657_WOMAN_SHOES.MSH
            Assert.AreEqual("AVATAR/012657_WOMAN_SHOES.MSH", Item(12657, ItemCategory.ShoesFemale).MshRelPath);
        }

        [Test]
        public void MshRelPath_MaleCoat()
        {
            Assert.AreEqual("AVATAR/012807_MAN_COAT.MSH", Item(12807, ItemCategory.TopMale).MshRelPath);
        }

        [Test]
        public void MshRelPath_SlotSuffixes()
        {
            Assert.AreEqual("AVATAR/000100_WOMAN_HAIR.MSH", Item(100, ItemCategory.HairFemale).MshRelPath);
            Assert.AreEqual("AVATAR/000100_WOMAN_PANT.MSH", Item(100, ItemCategory.BottomFemale).MshRelPath);
            Assert.AreEqual("AVATAR/000100_WOMAN_HAND.MSH", Item(100, ItemCategory.GlovesFemale).MshRelPath);   // gloves -> HAND
            // cat 6/106 = 商城「表情」(expression) → mesh _FACE_HUAN (實測 292 件皆此命名,非底層預設臉 _FACE;
            // 底層預設臉走 AvatarOutfit enum 定值 900007_WOMAN_FACE,不經 category)。
            Assert.AreEqual("AVATAR/000100_WOMAN_FACE_HUAN.MSH", Item(100, ItemCategory.FaceFemale).MshRelPath);
            Assert.AreEqual("AVATAR/000100_WOMAN_LINGDANG.MSH", Item(100, ItemCategory.NecklaceFemale).MshRelPath);   // cat 9/109 = 项链
            // cat 8/108 = 商城「翅膀」(饰品店 wing 頁) → mesh _CHIBANG (實測 109 件 cat8/108 全對到 NNNNNN_性別_CHIBANG.MSH)
            Assert.AreEqual("AVATAR/000100_WOMAN_CHIBANG.MSH", Item(100, ItemCategory.WingsFemale).MshRelPath);
            Assert.AreEqual("AVATAR/000100_MAN_CHIBANG.MSH", Item(100, ItemCategory.WingsMale).MshRelPath);
            Assert.AreEqual("AVATAR/000100_WOMAN_GLASS.MSH", Item(100, ItemCategory.GlassesFemale).MshRelPath);
            Assert.AreEqual("AVATAR/000100_WOMAN_ONE.MSH", Item(100, ItemCategory.OnePieceFemale).MshRelPath);
        }

        [Test]
        public void MshRelPath_NonWornCategories_AreNull()
        {
            Assert.IsNull(Item(100, ItemCategory.MainConsumables).MshRelPath);
            Assert.IsNull(Item(100, ItemCategory.AvatarEffects).MshRelPath);
            Assert.IsNull(Item(100, ItemCategory.OutfitFemale).MshRelPath);   // full outfit = multiple parts, no single mesh
        }

        [Test]
        public void SlotType_And_EquipSlot_Mapping()
        {
            Assert.AreEqual(ItemSlotType.Clothes, Item(1, ItemCategory.TopFemale).SlotType);
            Assert.AreEqual(ItemSlotType.Items, Item(1, ItemCategory.MainConsumables).SlotType);
            Assert.AreEqual(EquipSlot.Shoes, Item(1, ItemCategory.ShoesMale).EquipSlot);
            Assert.AreEqual(EquipSlot.Consumable, Item(1, ItemCategory.MainConsumables).EquipSlot);
            Assert.AreEqual(EquipSlot.Expression, Item(1, ItemCategory.FaceFemale).EquipSlot);   // 6/106 = 表情
            Assert.AreEqual(EquipSlot.Necklace, Item(1, ItemCategory.NecklaceMale).EquipSlot);   // 9/109 = 项链
            Assert.AreEqual(EquipSlot.Wings, Item(1, ItemCategory.WingsMale).EquipSlot);         // 8 = 翅膀 (男)
            Assert.AreEqual(EquipSlot.Wings, Item(1, ItemCategory.WingsFemale).EquipSlot);       // 108 = 翅膀 (女)
            Assert.AreEqual(ItemSlotType.Clothes, Item(1, ItemCategory.WingsFemale).SlotType);   // 翅膀是可穿戴衣物 slot,非消耗品
            Assert.AreEqual(EquipSlot.Outfit, Item(1, ItemCategory.OutfitFemale).EquipSlot);     // 200 = 套装
            Assert.AreEqual(EquipSlot.Outfit, Item(1, ItemCategory.OutfitMixed).EquipSlot);      // 203 = 混性別套装
        }

        [Test]
        public void GenderOf_Outfit_FromNameForMixedCategory()
        {
            // cat 200/201 resolve by category; cat 203 (sex byte unreliable) must read 男/女 from the name.
            Assert.AreEqual(ItemSex.Female, ItemTypes.GenderOf(ItemCategory.OutfitFemale, "倾城佳雪 女装"));
            Assert.AreEqual(ItemSex.Male, ItemTypes.GenderOf(ItemCategory.OutfitMale, "暗夜骑斗士 男装"));
            Assert.AreEqual(ItemSex.Male, ItemTypes.GenderOf(ItemCategory.OutfitMixed, "浮世清欢男套装"));
            Assert.AreEqual(ItemSex.Female, ItemTypes.GenderOf(ItemCategory.OutfitMixed, "某某女套装"));
        }

        [Test]
        public void GenderFolder_FromCategoryBlock()
        {
            Assert.AreEqual("MAN", Item(1, ItemCategory.TopMale).GenderFolder);
            Assert.AreEqual("WOMAN", Item(1, ItemCategory.TopFemale).GenderFolder);
        }

        // ---- synthesised mesh-only accessory rebuild (AvatarItemCatalog.ById fix) ----
        // A synth wing/表情/项链 owned+equipped in profile.json must rebuild from its mesh on reload even when the shop
        // never browsed that slot this session — otherwise the 儲物櫃 hides it and a save drops it from equippedParts
        // (user bug: 重開遊戲翅膀/表情進儲物櫃就消失，去商城買一個才恢復).

        private static System.Func<string, bool> MeshSet(params string[] names)
        {
            var set = new HashSet<string>(names);
            return set.Contains;
        }

        [Test]
        public void SynthesizeAccessory_MaleWing_RebuildsFromMesh()
        {
            int id = AvatarItemCatalog.SynthIdBase + 37931;                 // the real equipped wing id from profile 00000001
            var it = AvatarItemCatalog.SynthesizeAccessory(id, MeshSet("037931_MAN_CHIBANG.MSH"));
            Assert.IsNotNull(it);
            Assert.AreEqual(id, it.Id);
            Assert.AreEqual(37931, it.ModelId);
            Assert.AreEqual(EquipSlot.Wings, it.EquipSlot);
            Assert.AreEqual(ItemSex.Male, it.Sex);
            Assert.AreEqual("AVATAR/037931_MAN_CHIBANG.MSH", it.MshRelPath);
        }

        [Test]
        public void SynthesizeAccessory_MaleExpression_RebuildsFromMesh()
        {
            int id = AvatarItemCatalog.SynthIdBase + 37953;                 // the real equipped 表情 id from profile 00000001
            var it = AvatarItemCatalog.SynthesizeAccessory(id, MeshSet("037953_MAN_FACE_HUAN.MSH"));
            Assert.IsNotNull(it);
            Assert.AreEqual(EquipSlot.Expression, it.EquipSlot);
            Assert.AreEqual("AVATAR/037953_MAN_FACE_HUAN.MSH", it.MshRelPath);
        }

        [Test]
        public void SynthesizeAccessory_FemaleNecklace_RebuildsFromMesh()
        {
            int id = AvatarItemCatalog.SynthIdBase + 100;
            var it = AvatarItemCatalog.SynthesizeAccessory(id, MeshSet("000100_WOMAN_LINGDANG.MSH"));
            Assert.IsNotNull(it);
            Assert.AreEqual(EquipSlot.Necklace, it.EquipSlot);
            Assert.AreEqual(ItemSex.Female, it.Sex);
        }

        [Test]
        public void SynthesizeAccessory_NoMatchingMesh_ReturnsNull()
        {
            int id = AvatarItemCatalog.SynthIdBase + 37931;
            Assert.IsNull(AvatarItemCatalog.SynthesizeAccessory(id, MeshSet()));                    // nothing on disk
            Assert.IsNull(AvatarItemCatalog.SynthesizeAccessory(id, MeshSet("037931_MAN_COAT.MSH"))); // a top is not an accessory
        }

        [Test]
        public void SynthesizeAccessory_NonSynthId_ReturnsNull()
        {
            // a real iteminfo id (< SynthIdBase) is never a synth accessory, even if a same-numbered mesh exists
            Assert.IsNull(AvatarItemCatalog.SynthesizeAccessory(337891, MeshSet("337891_MAN_CHIBANG.MSH")));
        }

        // ---- synthesised mesh-only CLOTHING rows (髮型/上衣/下裝/鞋子 加進商城,序號當名字,100M) ----
        // Same idea as the accessory synth above but for the 4 body-garment slots. The synth Id bakes slot+gender in
        // (see AvatarItemCatalog.SynthId) so a modelId that exists in two slots — or for both genders — reloads into the
        // RIGHT one instead of whichever mesh is probed first (there ARE such collisions in the shipped meshes).

        [Test]
        public void SynthId_ClothingRoundTrips_ToRightSlotGenderAndMesh()
        {
            int id = AvatarItemCatalog.SynthId(EquipSlot.Hair, ItemSex.Female, 100);
            var it = AvatarItemCatalog.SynthesizeSynthItem(id, MeshSet("000100_WOMAN_HAIR.MSH"));
            Assert.IsNotNull(it);
            Assert.AreEqual(id, it.Id);
            Assert.AreEqual(100, it.ModelId);
            Assert.AreEqual("000100", it.Name);                                  // 序號當名字
            Assert.AreEqual(100, it.Price);                                      // 100 …
            Assert.AreEqual(ItemPriceCurrency.Coins, it.Currency);               // … M 幣
            Assert.AreEqual(EquipSlot.Hair, it.EquipSlot);
            Assert.AreEqual(ItemSex.Female, it.Sex);
            Assert.AreEqual("AVATAR/000100_WOMAN_HAIR.MSH", it.MshRelPath);
        }

        [Test]
        public void SynthId_SameModelInTwoSlots_ResolvesEachToItsOwnSlot()
        {
            // real collision: modelId 001277 女 exists as BOTH a COAT and a PANT mesh.
            int topId = AvatarItemCatalog.SynthId(EquipSlot.Top, ItemSex.Female, 1277);
            int botId = AvatarItemCatalog.SynthId(EquipSlot.Bottom, ItemSex.Female, 1277);
            Assert.AreNotEqual(topId, botId, "a shared modelId must get distinct synth ids per slot");

            var both = MeshSet("001277_WOMAN_COAT.MSH", "001277_WOMAN_PANT.MSH");   // both on disk → must not cross-resolve
            var top = AvatarItemCatalog.SynthesizeSynthItem(topId, both);
            var bot = AvatarItemCatalog.SynthesizeSynthItem(botId, both);
            Assert.AreEqual(EquipSlot.Top, top.EquipSlot);
            Assert.AreEqual("AVATAR/001277_WOMAN_COAT.MSH", top.MshRelPath);
            Assert.AreEqual(EquipSlot.Bottom, bot.EquipSlot);
            Assert.AreEqual("AVATAR/001277_WOMAN_PANT.MSH", bot.MshRelPath);
        }

        [Test]
        public void SynthId_SameModelBothGenders_ResolvesEachToItsOwnGender()
        {
            // real collision: modelId 003547 exists as BOTH a MAN and a WOMAN hair mesh.
            int maleId = AvatarItemCatalog.SynthId(EquipSlot.Hair, ItemSex.Male, 3547);
            int femaleId = AvatarItemCatalog.SynthId(EquipSlot.Hair, ItemSex.Female, 3547);
            Assert.AreNotEqual(maleId, femaleId, "a dual-gender modelId must get distinct synth ids per gender");

            var both = MeshSet("003547_MAN_HAIR.MSH", "003547_WOMAN_HAIR.MSH");
            Assert.AreEqual("AVATAR/003547_MAN_HAIR.MSH",   AvatarItemCatalog.SynthesizeSynthItem(maleId, both).MshRelPath);
            Assert.AreEqual("AVATAR/003547_WOMAN_HAIR.MSH", AvatarItemCatalog.SynthesizeSynthItem(femaleId, both).MshRelPath);
        }

        [Test]
        public void SynthId_Accessory_StaysBareModelId_ForBackwardCompat()
        {
            // 附件 (翅膀/表情/项链) keep the legacy gender-agnostic bare encoding so ids ALREADY saved in profiles resolve.
            Assert.AreEqual(AvatarItemCatalog.SynthIdBase + 37931, AvatarItemCatalog.SynthId(EquipSlot.Wings, ItemSex.Male, 37931));
            Assert.AreEqual(AvatarItemCatalog.SynthIdBase + 37931, AvatarItemCatalog.SynthId(EquipSlot.Wings, ItemSex.Female, 37931));
            Assert.AreEqual(AvatarItemCatalog.SynthIdBase + 100,   AvatarItemCatalog.SynthId(EquipSlot.Necklace, ItemSex.Female, 100));
        }

        [Test]
        public void SynthesizeSynthItem_RoutesAccessoryIds_ToAccessoryResolution()
        {
            // the general entry point must still resolve a legacy bare accessory id via the accessory probe.
            int id = AvatarItemCatalog.SynthIdBase + 37931;
            var it = AvatarItemCatalog.SynthesizeSynthItem(id, MeshSet("037931_MAN_CHIBANG.MSH"));
            Assert.IsNotNull(it);
            Assert.AreEqual(EquipSlot.Wings, it.EquipSlot);
        }

        [Test]
        public void SynthesizeSynthItem_ClothingNoMeshOnDisk_ReturnsNull()
        {
            int id = AvatarItemCatalog.SynthId(EquipSlot.Shoes, ItemSex.Male, 500);
            Assert.IsNull(AvatarItemCatalog.SynthesizeSynthItem(id, MeshSet()));                         // nothing on disk
            Assert.IsNull(AvatarItemCatalog.SynthesizeSynthItem(id, MeshSet("000500_WOMAN_SHOES.MSH"))); // wrong gender only
        }

        [Test]
        public void SynthesizeSynthItem_NonSynthId_ReturnsNull()
        {
            Assert.IsNull(AvatarItemCatalog.SynthesizeSynthItem(337891, MeshSet("337891_MAN_HAIR.MSH")));
        }

        [Test]
        public void IsShopModelId_Excludes9xxxxxDefaultsAndNonPositive()
        {
            // 9xxxxx = 素體內建預設衣服/部位 (預設臉 900007、預設髮/上衣/下著/鞋 900002..900020) → 不上架 (user)
            Assert.IsFalse(AvatarItemCatalog.IsShopModelId(900007), "900007 default face is not a shop model");
            Assert.IsFalse(AvatarItemCatalog.IsShopModelId(900004), "900004 default pant is not a shop model");
            Assert.IsFalse(AvatarItemCatalog.IsShopModelId(AvatarItemCatalog.DefaultModelIdBase), "the 900000 boundary is excluded");
            Assert.IsFalse(AvatarItemCatalog.IsShopModelId(0), "0 is not a model");
            Assert.IsFalse(AvatarItemCatalog.IsShopModelId(-1), "negative is not a model");
            // real buyable serials stay in
            Assert.IsTrue(AvatarItemCatalog.IsShopModelId(12657), "012657 (a real 长靴) is a shop model");
            Assert.IsTrue(AvatarItemCatalog.IsShopModelId(1277), "001277 is a shop model");
            Assert.IsTrue(AvatarItemCatalog.IsShopModelId(899999), "899999 (just below the default range) is still a shop model");
        }
    }
}
