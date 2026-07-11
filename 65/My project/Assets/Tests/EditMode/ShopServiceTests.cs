using NUnit.Framework;
using Sdo.Shop;

namespace Sdo.Tests
{
    public class ShopServiceTests
    {
        private static ShopItem Clothes(int id, int cat, ItemPriceCurrency cur, int price,
                                        int minLevel = 0, int sex = 2, int durationDays = -1)
            => new ShopItem
            {
                Id = id, Category = cat, PriceCategoryRaw = (int)cur, Price = price,
                MinLevel = minLevel, SexRaw = sex, DurationDays = durationDays, Quantity = -1, Name = "t" + id,
            };

        private static ShopItem Consumable(int id, ItemPriceCurrency cur, int price, int qty)
            => new ShopItem
            {
                Id = id, Category = ItemCategory.MainConsumables, PriceCategoryRaw = (int)cur, Price = price,
                DurationDays = -1, Quantity = qty, SexRaw = 2, Name = "c" + id,
            };

        // ---- Buy ----

        [Test]
        public void Buy_Success_DeductsWallet_AndOwns()
        {
            var wd = new Wardrobe(); wd.Wallet.Coins = 1000;
            var item = Clothes(6, ItemCategory.BottomFemale, ItemPriceCurrency.Coins, 200);
            Assert.AreEqual(BuyResult.Ok, ShopService.Buy(wd, item, 0));
            Assert.AreEqual(800, wd.Wallet.Coins);
            Assert.IsTrue(wd.Owns(6));
            Assert.IsFalse(wd.IsEquipped(6));   // buying does not auto-equip
        }

        [Test]
        public void Buy_RoutesToCorrectWallet()
        {
            var wd = new Wardrobe(); wd.Wallet.Points = 500; wd.Wallet.Coins = 500; wd.Wallet.Bonus = 500;
            ShopService.Buy(wd, Clothes(1, ItemCategory.TopFemale, ItemPriceCurrency.Points, 100), 0);
            ShopService.Buy(wd, Clothes(2, ItemCategory.TopFemale, ItemPriceCurrency.Coins, 100), 0);
            ShopService.Buy(wd, Clothes(3, ItemCategory.TopFemale, ItemPriceCurrency.Bonus, 100), 0);
            Assert.AreEqual(400, wd.Wallet.Points);
            Assert.AreEqual(400, wd.Wallet.Coins);
            Assert.AreEqual(400, wd.Wallet.Bonus);
        }

        [Test]
        public void Buy_NotEnoughMoney_LeavesWalletAndOwnershipUntouched()
        {
            var wd = new Wardrobe(); wd.Wallet.Coins = 100;
            var item = Clothes(6, ItemCategory.BottomFemale, ItemPriceCurrency.Coins, 200);
            Assert.AreEqual(BuyResult.NotEnoughMoney, ShopService.Buy(wd, item, 0));
            Assert.AreEqual(100, wd.Wallet.Coins);
            Assert.IsFalse(wd.Owns(6));
        }

        [Test]
        public void Buy_AlreadyOwnedClothing_ChargesOnce()
        {
            var wd = new Wardrobe(); wd.Wallet.Coins = 1000;
            var item = Clothes(6, ItemCategory.BottomFemale, ItemPriceCurrency.Coins, 200);
            Assert.AreEqual(BuyResult.Ok, ShopService.Buy(wd, item, 0));
            Assert.AreEqual(BuyResult.AlreadyOwned, ShopService.Buy(wd, item, 0));
            Assert.AreEqual(800, wd.Wallet.Coins);   // only charged the first time
        }

        [Test]
        public void Buy_Consumable_StacksQuantity_AndChargesEachTime()
        {
            var wd = new Wardrobe(); wd.Wallet.Points = 1000;
            var potion = Consumable(21001, ItemPriceCurrency.Points, 100, qty: 5);
            Assert.AreEqual(BuyResult.Ok, ShopService.Buy(wd, potion, 0));
            Assert.AreEqual(BuyResult.Ok, ShopService.Buy(wd, potion, 0));
            Assert.AreEqual(10, wd.GetOwned(21001).Quantity);   // stacked 5 + 5
            Assert.AreEqual(800, wd.Wallet.Points);             // charged twice
        }

        [Test]
        public void Buy_NoRoom_WhenSlotBucketFull()
        {
            var wd = new Wardrobe { ClothSlotCount = 0 }; wd.Wallet.Coins = 1000;
            Assert.AreEqual(BuyResult.NoRoom, ShopService.Buy(wd, Clothes(6, ItemCategory.TopFemale, ItemPriceCurrency.Coins, 10), 0));
        }

        // ---- ComputeExpire ----

        [Test]
        public void ComputeExpire_ByDuration()
        {
            const long now = 1_000_000L;
            Assert.AreEqual(-1, ShopService.ComputeExpire((int)ItemDuration.Permanent, now));
            Assert.AreEqual(1, ShopService.ComputeExpire((int)ItemDuration.Zero, now));
            Assert.AreEqual(now + 7 * 86400L, ShopService.ComputeExpire(7, now));
            Assert.AreEqual(now + 30 * 86400L, ShopService.ComputeExpire(30, now));
            Assert.AreEqual(-1, ShopService.ComputeExpire(-999, now));   // unknown negative → permanent
        }

        [Test]
        public void Buy_Rental_SetsFutureExpiry_Permanent_SetsMinusOne()
        {
            var wd = new Wardrobe(); wd.Wallet.Coins = 1000;
            ShopService.Buy(wd, Clothes(7, ItemCategory.BottomFemale, ItemPriceCurrency.Coins, 50, durationDays: 7), 1000);
            Assert.AreEqual(1000 + 7 * 86400L, wd.GetOwned(7).ExpireUnix);

            ShopService.Buy(wd, Clothes(8, ItemCategory.BottomFemale, ItemPriceCurrency.Coins, 200, durationDays: -1), 1000);
            Assert.AreEqual(-1, wd.GetOwned(8).ExpireUnix);
        }

        // ---- Equip ----

        [Test]
        public void Equip_Ok_OccupiesSlot()
        {
            var wd = new Wardrobe(); wd.Wallet.Coins = 1000;
            var hair = Clothes(101, ItemCategory.HairFemale, ItemPriceCurrency.Coins, 10, sex: (int)ItemSex.Female);
            ShopService.Buy(wd, hair, 0);
            Assert.AreEqual(EquipResult.Ok, ShopService.Equip(wd, hair, playerLevel: 1, ItemSex.Female, 0));
            Assert.AreEqual(101, wd.EquippedId(EquipSlot.Hair));
        }

        [Test]
        public void Equip_MinLevel_Blocks()
        {
            var wd = new Wardrobe(); wd.Wallet.Coins = 1000;
            var top = Clothes(102, ItemCategory.TopFemale, ItemPriceCurrency.Coins, 10, minLevel: 10);
            ShopService.Buy(wd, top, 0);
            Assert.AreEqual(EquipResult.MinLevel, ShopService.Equip(wd, top, playerLevel: 5, ItemSex.Both, 0));
            Assert.AreEqual(0, wd.EquippedId(EquipSlot.Top));
        }

        [Test]
        public void Equip_WrongGender_Blocks()
        {
            var wd = new Wardrobe(); wd.Wallet.Coins = 1000;
            var femaleTop = Clothes(102, ItemCategory.TopFemale, ItemPriceCurrency.Coins, 10, sex: (int)ItemSex.Female);
            ShopService.Buy(wd, femaleTop, 0);
            Assert.AreEqual(EquipResult.WrongGender, ShopService.Equip(wd, femaleTop, 1, ItemSex.Male, 0));
        }

        [Test]
        public void Equip_NotOwned_Blocks()
        {
            var wd = new Wardrobe();
            var hair = Clothes(101, ItemCategory.HairFemale, ItemPriceCurrency.Coins, 10);
            Assert.AreEqual(EquipResult.NotOwned, ShopService.Equip(wd, hair, 1, ItemSex.Both, 0));
        }

        [Test]
        public void Equip_Consumable_NotWearable()
        {
            var wd = new Wardrobe(); wd.Wallet.Points = 1000;
            var potion = Consumable(21001, ItemPriceCurrency.Points, 10, qty: 1);
            ShopService.Buy(wd, potion, 0);
            Assert.AreEqual(EquipResult.NotWearable, ShopService.Equip(wd, potion, 1, ItemSex.Both, 0));
        }

        [Test]
        public void Equip_ExpiredRental_Blocks()
        {
            var wd = new Wardrobe(); wd.Wallet.Coins = 1000;
            var rental = Clothes(103, ItemCategory.BottomFemale, ItemPriceCurrency.Coins, 50, durationDays: 7);
            ShopService.Buy(wd, rental, nowUnix: 0);   // expires at 7*86400
            Assert.AreEqual(EquipResult.Expired, ShopService.Equip(wd, rental, 1, ItemSex.Both, nowUnix: 8 * 86400L));
        }

        [Test]
        public void Unequip_ClearsSlot()
        {
            var wd = new Wardrobe(); wd.Wallet.Coins = 1000;
            var hair = Clothes(101, ItemCategory.HairFemale, ItemPriceCurrency.Coins, 10);
            ShopService.Buy(wd, hair, 0);
            ShopService.Equip(wd, hair, 1, ItemSex.Both, 0);
            ShopService.Unequip(wd, EquipSlot.Hair);
            Assert.AreEqual(0, wd.EquippedId(EquipSlot.Hair));
        }
    }
}
