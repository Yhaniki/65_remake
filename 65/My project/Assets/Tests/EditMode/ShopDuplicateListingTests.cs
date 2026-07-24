using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Game;
using Sdo.Shop;

namespace Sdo.Tests
{
    /// <summary>
    /// Guards 使用者「把這種視覺完全一樣的衣服只留一組,留中文的」. The shipped iteminfo lists thousands of garments twice:
    /// the CN row (e.g. 「金姬兰 女装」, item 124976) and an English re-listing of the SAME (Category, ModelId) from the
    /// international service ("Flower Lace Dress", item 1224976, priced 2,000,000). Same category+model = same mesh and
    /// textures, so the shop showed two identical-looking cards.
    /// <see cref="AvatarItemCatalog.DuplicateListingIds"/> hides the English twin and keeps the Chinese one.
    /// </summary>
    public class ShopDuplicateListingTests
    {
        private static ShopItem Row(int id, int model, int cat, string name)
            => new ShopItem { Id = id, ModelId = model, Category = cat, Name = name };

        [Test]
        public void EnglishRelisting_IsHidden_ChineseKept()
        {
            var items = new List<ShopItem>
            {
                // the shipped rows: three Chinese duration tiers (7天/30天/永久) + the English re-listing (永久).
                new ShopItem { Id = 124976,  ModelId = 24976, Category = 150, Name = "金姬兰 女装",       DurationDays = 7 },
                new ShopItem { Id = 224976,  ModelId = 24976, Category = 150, Name = "金姬兰 女装",       DurationDays = 30 },
                new ShopItem { Id = 324976,  ModelId = 24976, Category = 150, Name = "金姬兰 女装",       DurationDays = -1 },
                new ShopItem { Id = 1224976, ModelId = 24976, Category = 150, Name = "Flower Lace Dress", DurationDays = -1 },
            };
            var hidden = AvatarItemCatalog.DuplicateListingIds(items);
            Assert.IsTrue(hidden.Contains(1224976), "the English twin of 金姬兰 女装 must be hidden");
            Assert.IsFalse(hidden.Contains(124976));
            Assert.IsFalse(hidden.Contains(224976));
            Assert.IsFalse(hidden.Contains(324976), "duration tiers are different offers — all Chinese rows stay");
        }

        [Test]
        public void EnglishOnlyDesign_StaysListed()
        {
            // 7,794 models exist ONLY in the international catalogue — hiding those would delete them from the shop.
            var items = new List<ShopItem>
            {
                Row(3010001, 10001, 101, "SHINE Beige Jacket"),
                Row(3010002, 10002, 101, "SHINE Pearl Dress"),
            };
            Assert.IsEmpty(AvatarItemCatalog.DuplicateListingIds(items));
        }

        [Test]
        public void SameModelDifferentSlot_IsNotADuplicate()
        {
            // A design's COAT and PANT share a modelId; they are different garments, so the key includes Category.
            var items = new List<ShopItem>
            {
                Row(1001, 1278, 102, "野战迷彩 女装"),
                Row(1002, 1278, 103, "Camo Shorts"),
            };
            Assert.IsEmpty(AvatarItemCatalog.DuplicateListingIds(items),
                "different Category = different worn slot = not the same visual item");
        }

        [Test]
        public void NumericPlaceholderName_IsHiddenWhenAChineseRowExists()
        {
            // Unnamed rows render as their 6-digit id (037880). If the same design also has a real Chinese name, the
            // numeric card is the same clothes with a worse label.
            var items = new List<ShopItem>
            {
                Row(500, 37880, 102, "雪域妖姬 上装"),
                Row(501, 37880, 102, "037880"),
            };
            var hidden = AvatarItemCatalog.DuplicateListingIds(items);
            Assert.IsTrue(hidden.Contains(501));
            Assert.IsFalse(hidden.Contains(500));
        }

        [Test]
        public void TraditionalChineseName_CountsAsChinese()
        {
            // The TW sidecar overlays Traditional names (「金姬蘭 女裝」) — those are the keeper, not the English row.
            var items = new List<ShopItem>
            {
                Row(1, 24976, 150, "金姬蘭 女裝"),
                Row(2, 24976, 150, "Flower Lace Dress"),
            };
            var hidden = AvatarItemCatalog.DuplicateListingIds(items);
            Assert.IsTrue(hidden.Contains(2));
            Assert.IsFalse(hidden.Contains(1));
        }

        [Test]
        public void HasCjk_ClassifiesNamesCorrectly()
        {
            Assert.IsTrue(AvatarItemCatalog.HasCjk("金姬兰 女装"));
            Assert.IsTrue(AvatarItemCatalog.HasCjk("金姬蘭 女裝"));
            Assert.IsTrue(AvatarItemCatalog.HasCjk("充气奶牛锤（男）"));
            Assert.IsFalse(AvatarItemCatalog.HasCjk("Flower Lace Dress"));
            Assert.IsFalse(AvatarItemCatalog.HasCjk("037880"));
            Assert.IsFalse(AvatarItemCatalog.HasCjk(""));
            Assert.IsFalse(AvatarItemCatalog.HasCjk(null));
        }

        [Test]
        public void IdenticalLabelRows_CollapseToTheLowestId()
        {
            // The Traditional-name sidecar is keyed by (category, modelId), so it renames the English re-listing to the
            // same Chinese name — two cards that read EXACTLY the same. Same model + same rental period + same name =
            // one offer; keep the original (lowest) id.
            var items = new List<ShopItem>
            {
                new ShopItem { Id = 124976,  ModelId = 24976, Category = 150, Name = "金姬蘭 女裝", DurationDays = -1 },
                new ShopItem { Id = 1224976, ModelId = 24976, Category = 150, Name = "金姬蘭 女裝", DurationDays = -1 },
            };
            var hidden = AvatarItemCatalog.DuplicateListingIds(items);
            Assert.IsTrue(hidden.Contains(1224976));
            Assert.IsFalse(hidden.Contains(124976));
        }

        [Test]
        public void SameNameDifferentDuration_BothStay()
        {
            // 7天/30天/永久 are the same garment at different prices — genuinely different offers, all kept.
            var items = new List<ShopItem>
            {
                new ShopItem { Id = 124976, ModelId = 24976, Category = 150, Name = "金姬兰 女装", DurationDays = -1 },
                new ShopItem { Id = 224976, ModelId = 24976, Category = 150, Name = "金姬兰 女装", DurationDays = 30 },
                new ShopItem { Id = 324976, ModelId = 24976, Category = 150, Name = "金姬兰 女装", DurationDays = 7 },
            };
            Assert.IsEmpty(AvatarItemCatalog.DuplicateListingIds(items));
        }

        [Test]
        public void RemovedMascotSeries_IsHidden()
        {
            // 使用者:「panda suit f, ultraman top, chick suit f 這系列的衣服都拿掉」— the whole family goes, every
            // duration tier and both genders, and the matching hair/gloves/shoes with it.
            var items = new List<ShopItem>
            {
                Row(1200968, 968, 150, "Panda Suit F"),
                Row(1200972, 972, 50,  "Panda Suit M"),
                Row(1200983, 983, 150, "Chicky Suit F"),
                Row(1200964, 964, 150, "Ultraman Top F"),
                Row(3000963, 963, 101, "Ultraman Hair F"),
                Row(1400984, 984, 105, "Chicky Shoes F"),
                Row(1201766, 1766, 150, "Skirt Suit"),        // control: a normal garment stays
            };
            var hidden = AvatarItemCatalog.DuplicateListingIds(items);
            foreach (var id in new[] { 1200968, 1200972, 1200983, 1200964, 3000963, 1400984 })
                Assert.IsTrue(hidden.Contains(id), "mascot series item " + id + " must be pulled from the shop");
            Assert.IsFalse(hidden.Contains(1201766), "unrelated garments must not be caught by the series list");
        }

        [Test]
        public void SingleRowGroups_AreNeverHidden()
        {
            var items = new List<ShopItem> { Row(7, 999, 102, "Solo English Item") };
            Assert.IsEmpty(AvatarItemCatalog.DuplicateListingIds(items));
        }

        // ---- 非衣服 2D 商品 (道具/藥水/特效/禮包) 的去重:PropDuplicateListingIds ----
        // 使用者:「道具店/礼包店只拿中文的」。跟衣服不同,不能按 (Category,ModelId) 合併 —— 消耗品有「同名不同數量」的
        // SKU (小喇叭 ×1/×50/×100,名字都叫「小喇叭」),按 modelId 合併會把 SKU 也收成一筆。改按完整 SKU 去重。
        private static ShopItem Prop(int id, int model, int qty, int price, string name, int dur = -1, int cat = 21000)
            => new ShopItem { Id = id, ModelId = model, Category = cat, Quantity = qty, Price = price, PriceCategoryRaw = 1, DurationDays = dur, Name = name };

        [Test]
        public void PropEnglishRelisting_SameSku_IsHidden_ChineseKept()
        {
            // 奇妙冰激凌 (中,原價 500) 與 Ice Cream (英,重上架佔位價 2000000) 同 modelId/數量/時效/幣別 但**價格不同**
            // → 只留中文那筆 (log 實例 model 1120005)。價格不同也要視為同一件的中/英兩版 → SKU 鍵不含價格。
            var props = new List<ShopItem>
            {
                Prop(1, 1120005, 1, 500,     "奇妙冰激凌"),
                Prop(2, 1120005, 1, 2000000, "Ice Cream"),
            };
            var hidden = AvatarItemCatalog.PropDuplicateListingIds(props);
            Assert.IsTrue(hidden.Contains(2), "英文重上架列要藏 (即使價格與中文列不同)");
            Assert.IsFalse(hidden.Contains(1), "中文列要留");
        }

        [Test]
        public void PropDifferentQuantitySku_AllKept_EnglishTwinsHidden()
        {
            // 小喇叭 ×1 與 ×50 是不同 SKU (不同商品),都要保留;各自的英文重複列才藏 → 證明 SKU 不會被折疊掉。
            var props = new List<ShopItem>
            {
                Prop(1, 100000, 1,  5000,  "小喇叭"),
                Prop(2, 100000, 1,  5000,  "Small Horn"),
                Prop(3, 100000, 50, 5000,  "小喇叭"),
                Prop(4, 100000, 50, 5000,  "Small Horn"),
            };
            var hidden = AvatarItemCatalog.PropDuplicateListingIds(props);
            Assert.IsTrue(hidden.Contains(2), "×1 的英文列要藏");
            Assert.IsTrue(hidden.Contains(4), "×50 的英文列要藏");
            Assert.IsFalse(hidden.Contains(1), "×1 中文列保留");
            Assert.IsFalse(hidden.Contains(3), "×50 中文列保留 (SKU 不被折疊)");
        }

        [Test]
        public void PropEnglishOnly_SameSku_BothKept()
        {
            // 同 SKU 全無中文 → 沒有中文可留,整組保留 (只有英文名的商品不能被誤刪)。
            var props = new List<ShopItem>
            {
                Prop(5, 200010, 1, 100, "Sparkle Effect"),
                Prop(3, 200010, 1, 100, "Sparkle Effect"),
            };
            Assert.IsEmpty(AvatarItemCatalog.PropDuplicateListingIds(props));
        }

        [Test]
        public void PropEnglishOnly_DistinctSku_AllKept()
        {
            var props = new List<ShopItem>
            {
                Prop(1, 200010, 1, 100, "Sparkle Effect"),
                Prop(2, 200011, 1, 100, "Bubble Effect"),
            };
            Assert.IsEmpty(AvatarItemCatalog.PropDuplicateListingIds(props));
        }

        [Test]
        public void PropSingleRow_NotHidden()
        {
            var props = new List<ShopItem> { Prop(1, 600003, 1, 2000000, "婚庆大礼包") };
            Assert.IsEmpty(AvatarItemCatalog.PropDuplicateListingIds(props));
        }
    }
}
