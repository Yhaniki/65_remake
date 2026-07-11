using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Shop;
using Sdo.Settings;
using Sdo.UI.Core;

namespace Sdo.Tests
{
    /// <summary>
    /// The persistence bridge <see cref="WardrobeStore"/> (商城/儲物櫃 ↔ profile.json). Only the PURE conversion
    /// functions are exercised here — no ProfileManager / catalog / Unity I/O — so the round-trip contract (buy money
    /// records, owned items survive, worn outfit survives) is verified deterministically.
    /// </summary>
    public class WardrobeStoreTests
    {
        // A minimal catalog stub: id → ShopItem, and a mesh resolver that "finds" everything so ResolveParts keeps items.
        private static ShopItem Item(int id, int modelId, int category)
            => new ShopItem { Id = id, ModelId = modelId, Category = category, DurationDays = -1, Quantity = -1 };

        [Test]
        public void ApplyProfile_FreshProfile_GrantsStarterAllowanceOnce()
        {
            var p = new UserProfile();            // wallet.seeded == false
            var w = new Wardrobe();
            WardrobeStore.ApplyProfileToWardrobe(p, w);

            Assert.AreEqual(WardrobeStore.SeedCoins, w.Wallet.Coins);
            Assert.AreEqual(WardrobeStore.SeedPoints, w.Wallet.Points);
            Assert.AreEqual(WardrobeStore.SeedBonus, w.Wallet.Bonus);
        }

        [Test]
        public void ApplyProfile_SeededWallet_UsesStoredBalancesExactly()
        {
            var p = new UserProfile { wallet = new WalletSave { coins = 42, points = 7, bonus = 3, seeded = true } };
            var w = new Wardrobe();
            WardrobeStore.ApplyProfileToWardrobe(p, w);

            Assert.AreEqual(42, w.Wallet.Coins);
            Assert.AreEqual(7, w.Wallet.Points);
            Assert.AreEqual(3, w.Wallet.Bonus);
        }

        [Test]
        public void SpendToZeroThenSave_DoesNotReGrantStarterOnReload()
        {
            // Buy something down to 0, persist, reload → the seeded latch keeps 0 (no re-grant).
            var p = new UserProfile();
            var w = new Wardrobe();
            WardrobeStore.ApplyProfileToWardrobe(p, w);      // starter grant
            w.Wallet.Coins = 0; w.Wallet.Points = 0; w.Wallet.Bonus = 0;
            WardrobeStore.WriteWalletOwned(w, p);            // save (marks seeded)

            var w2 = new Wardrobe();
            WardrobeStore.ApplyProfileToWardrobe(p, w2);     // reload
            Assert.IsTrue(p.wallet.seeded);
            Assert.AreEqual(0, w2.Wallet.Coins);
            Assert.AreEqual(0, w2.Wallet.Points);
            Assert.AreEqual(0, w2.Wallet.Bonus);
        }

        [Test]
        public void OwnedItems_RoundTripThroughProfile()
        {
            var w = new Wardrobe();
            w.AddOwned(new OwnedItem { ItemId = 1001, Slot = ItemSlotType.Clothes, ExpireUnix = -1, Quantity = 1 });
            w.AddOwned(new OwnedItem { ItemId = 2002, Slot = ItemSlotType.Items, ExpireUnix = 123456, Quantity = 5 });

            var p = new UserProfile();
            WardrobeStore.WriteWalletOwned(w, p);

            var w2 = new Wardrobe();
            WardrobeStore.ApplyProfileToWardrobe(p, w2);

            Assert.IsTrue(w2.Owns(1001));
            Assert.IsTrue(w2.Owns(2002));
            var a = w2.GetOwned(1001);
            var b = w2.GetOwned(2002);
            Assert.AreEqual(ItemSlotType.Clothes, a.Slot);
            Assert.AreEqual(-1, a.ExpireUnix);
            Assert.AreEqual(ItemSlotType.Items, b.Slot);
            Assert.AreEqual(123456, b.ExpireUnix);
            Assert.AreEqual(5, b.Quantity);
        }

        [Test]
        public void Equipped_RoundTripsAndResolvesParts()
        {
            // Female top (cat 102 → EquipSlot.Top → COAT), modelId 12345 → AVATAR/012345_WOMAN_COAT.MSH.
            var top = Item(id: 5001, modelId: 12345, category: ItemCategory.TopFemale);
            var byId = new Dictionary<int, ShopItem> { { top.Id, top } };

            var w = new Wardrobe();
            w.AddOwned(new OwnedItem { ItemId = top.Id, Slot = ItemSlotType.Clothes, ExpireUnix = -1, Quantity = 1 });   // 只存「擁有且穿著」→ 要先擁有
            w.SetEquipped(EquipSlot.Top, top.Id);

            var p = new UserProfile();
            WardrobeStore.WriteEquipped(w, p, ItemSex.Female, id => byId.TryGetValue(id, out var it) ? it : null,
                                        meshExists: _ => true);

            // slot→id map persisted
            Assert.AreEqual(1, p.equippedItems.Length);
            Assert.AreEqual((int)EquipSlot.Top, p.equippedItems[0].slot);
            Assert.AreEqual(top.Id, p.equippedItems[0].id);

            // resolved worn parts include the garment mesh (layered over the WOMAN defaults)
            CollectionAssert.Contains(p.equippedParts, "AVATAR/012345_WOMAN_COAT.MSH");

            // EquippedAvatarParts prefers the full resolved list
            var worn = p.EquippedAvatarParts();
            CollectionAssert.Contains(worn, "AVATAR/012345_WOMAN_COAT.MSH");

            // and the equipped id restores on reload
            var w2 = new Wardrobe();
            WardrobeStore.ApplyProfileToWardrobe(p, w2);
            Assert.AreEqual(top.Id, w2.EquippedId(EquipSlot.Top));
        }

        [Test]
        public void EquippedAvatarParts_EmptyParts_FallsBackToLegacySixSlot()
        {
            // Old profile with no equippedParts but a legacy equippedClothes → still renders that costume.
            var p = new UserProfile { gender = 0 };
            p.Sanitize();   // fills equippedClothes from WOMAN defaults
            Assert.AreEqual(0, p.equippedParts.Length);
            var worn = p.EquippedAvatarParts();
            Assert.Greater(worn.Length, 0);
            CollectionAssert.Contains(worn, "AVATAR/900018_WOMAN_COAT.MSH");   // WOMAN default coat
        }

        // ResolveEquippedParts re-derives the room/gender-select render list from the id-based equippedItems, so an
        // accessory the wardrobe shows (via ById) is ALSO worn by the room/gender-select even when the persisted
        // equippedParts cache is stale (user bug: 儲物櫃有翅膀/表情、room 和選性別沒有).
        [Test]
        public void ResolveEquippedParts_IncludesEquippedAccessory_EvenWhenCacheIsStale()
        {
            var top  = new ShopItem { Id = 5001,     ModelId = 22222, Category = ItemCategory.TopFemale,   DurationDays = -1, Quantity = -1 };
            var wing = new ShopItem { Id = 90012345, ModelId = 12345, Category = ItemCategory.WingsFemale, DurationDays = -1, Quantity = -1 };
            var byId = new Dictionary<int, ShopItem> { { top.Id, top }, { wing.Id, wing } };
            var p = new UserProfile
            {
                gender = 0,
                equippedItems = new[]
                {
                    new EquipSave { slot = (int)EquipSlot.Top,   id = top.Id },
                    new EquipSave { slot = (int)EquipSlot.Wings, id = wing.Id },
                },
                equippedParts = new[] { "AVATAR/900007_WOMAN_FACE.MSH" },   // stale cache: missing the wing (+ top)
            };

            var parts = WardrobeStore.ResolveEquippedParts(p, ItemSex.Female,
                id => byId.TryGetValue(id, out var it) ? it : null, meshExists: _ => true);

            CollectionAssert.Contains(parts, "AVATAR/012345_WOMAN_CHIBANG.MSH");   // the equipped wing IS in the render list
            CollectionAssert.Contains(parts, "AVATAR/022222_WOMAN_COAT.MSH");      // and the equipped top
        }

        [Test]
        public void ResolveEquippedParts_NoEquippedItems_FallsBackToProfileParts()
        {
            var p = new UserProfile { gender = 0 };
            p.Sanitize();   // legacy equippedClothes, empty equippedItems
            var parts = WardrobeStore.ResolveEquippedParts(p, ItemSex.Female, id => null, meshExists: _ => true);
            CollectionAssert.Contains(parts, "AVATAR/900018_WOMAN_COAT.MSH");   // legacy default coat (fallback, not blank)
        }
    }
}
