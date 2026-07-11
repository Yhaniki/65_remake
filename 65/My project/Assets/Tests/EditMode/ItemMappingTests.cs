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
    }
}
