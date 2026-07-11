using System;
using System.Collections.Generic;
using Sdo.Shop;
using Sdo.Settings;
using Sdo.Game;

namespace Sdo.UI.Core
{
    /// <summary>
    /// The seam that makes the 商城/儲物櫃 persistent. The runtime shop model (<see cref="Sdo.Shop.Wardrobe"/> — owned
    /// item ids, equipped-by-slot, wallet) lives on <see cref="GameSession"/> and is otherwise session-only; this class
    /// copies it to/from the on-disk <see cref="UserProfile"/> (DATA/PROFILE/&lt;id&gt;/profile.json, via
    /// <see cref="ProfileManager"/>). So a purchase, a wallet change, or an outfit change survives an app restart.
    ///
    /// Layering of responsibilities keeps the SHOP and the WARDROBE from stepping on each other:
    ///   • the SHOP only ever commits owned+wallet (a buy / recharge) — try-on is preview-only and must NOT persist;
    ///   • the WARDROBE commits the whole outfit (owned+wallet+equipped+resolved parts).
    /// The conversion functions are pure (no Unity, no I/O) so they are unit-tested; only <see cref="Load"/>/
    /// <see cref="SaveOwnedWallet"/>/<see cref="SaveAll"/> touch <see cref="ProfileManager"/> and the catalog.
    /// </summary>
    public static class WardrobeStore
    {
        // Starter allowance granted ONCE to a fresh profile (moved here from GameSession.SeedRoomDefaults so it is
        // seeded through the wallet.seeded latch, not re-granted every launch). M=Coins / G=Points / H=Bonus.
        public const int SeedCoins = 100000, SeedPoints = 100000, SeedBonus = 1000;

        // ---------------- pure conversion (no Unity / no I/O) — unit-tested ----------------

        /// <summary>Populate <paramref name="w"/> from the persisted <paramref name="p"/>: wallet (or the one-time starter
        /// grant if this profile was never seeded), owned item ids, and the worn outfit. Clears nothing it doesn't set,
        /// so call it on a fresh <see cref="Wardrobe"/> (or after <see cref="Reset"/>).</summary>
        public static void ApplyProfileToWardrobe(UserProfile p, Wardrobe w)
        {
            if (p == null || w == null) return;
            p.Sanitize();

            w.ClothSlotCount = p.clothSlots;   // 服飾欄容量 (預設 3；服饰栏扩充 累加)

            if (p.wallet != null && p.wallet.seeded)
            {
                w.Wallet.Coins = p.wallet.coins;
                w.Wallet.Points = p.wallet.points;
                w.Wallet.Bonus = p.wallet.bonus;
            }
            else
            {
                w.Wallet.Coins = SeedCoins; w.Wallet.Points = SeedPoints; w.Wallet.Bonus = SeedBonus;
            }

            if (p.ownedItems != null)
                foreach (var o in p.ownedItems)
                {
                    if (o == null || o.id == 0) continue;
                    w.AddOwned(new OwnedItem
                    {
                        ItemId = o.id,
                        Slot = (ItemSlotType)o.slot,
                        ExpireUnix = o.expire,
                        Quantity = o.qty <= 0 ? 1 : o.qty,
                    });
                }

            if (p.equippedItems != null)
                foreach (var e in p.equippedItems)
                {
                    if (e == null || e.id == 0) continue;
                    var slot = (EquipSlot)e.slot;
                    if (slot != EquipSlot.None) w.SetEquipped(slot, e.id);
                }
        }

        /// <summary>Write the wallet + owned inventory of <paramref name="w"/> into <paramref name="p"/> (leaves the worn
        /// outfit fields untouched). Marks the wallet seeded so the starter grant never re-fires. Used by the SHOP after a
        /// buy / recharge — it must not touch equipped state (shop try-on is preview-only).</summary>
        public static void WriteWalletOwned(Wardrobe w, UserProfile p)
        {
            if (w == null || p == null) return;
            p.wallet = new WalletSave { coins = w.Wallet.Coins, points = w.Wallet.Points, bonus = w.Wallet.Bonus, seeded = true };
            p.clothSlots = w.ClothSlotCount;   // 服飾欄容量 (服饰栏扩充 累加後落地)

            var owned = new List<OwnedItemSave>(w.Owned.Count);
            foreach (var kv in w.Owned)
            {
                var o = kv.Value;
                if (o == null) continue;
                owned.Add(new OwnedItemSave { id = o.ItemId, expire = o.ExpireUnix, qty = o.Quantity, slot = (int)o.Slot });
            }
            p.ownedItems = owned.ToArray();
        }

        /// <summary>Write the worn outfit of <paramref name="w"/> into <paramref name="p"/>: the equipped slot→id map AND
        /// the resolved ordered mesh part paths (<see cref="UserProfile.equippedParts"/>, incl. accessories/wings) that the
        /// room/gameplay avatar renders. <paramref name="byId"/> resolves an equipped id to its <see cref="ShopItem"/>
        /// (unresolvable ids are dropped from the parts list but kept in the id map). Used by the WARDROBE.</summary>
        public static void WriteEquipped(Wardrobe w, UserProfile p, ItemSex sex, Func<int, ShopItem> byId, Func<string, bool> meshExists = null)
        {
            if (w == null || p == null) return;

            var eq = new List<EquipSave>();
            var items = new List<ShopItem>();
            foreach (var kv in w.Equipped)
            {
                if (kv.Value == 0) continue;
                eq.Add(new EquipSave { slot = (int)kv.Key, id = kv.Value });
                var it = byId != null ? byId(kv.Value) : null;
                if (it != null) items.Add(it);
            }
            p.equippedItems = eq.ToArray();
            var parts = Sdo.Game.AvatarOutfit.ResolveParts(sex, items, meshExists);   // 明確用 Sdo.Game.AvatarOutfit (與 Sdo.Settings.AvatarOutfit 同名，避免歧義)
            p.equippedParts = parts != null ? parts.ToArray() : new string[0];
        }

        // ---------------- I/O wrappers (Unity / ProfileManager / catalog) ----------------

        /// <summary>Load the active profile's wallet + inventory + outfit into <paramref name="s"/>'s wardrobe. Resets the
        /// wardrobe first so re-opening a screen starts from the saved state (not stale in-memory shop try-on).</summary>
        public static void Load(GameSession s)
        {
            if (s == null) return;
            s.Wardrobe.Reset();
            ApplyProfileToWardrobe(ProfileManager.Active, s.Wardrobe);
        }

        /// <summary>Persist a purchase / recharge: wallet + owned only (NOT the worn outfit). Cheap — no catalog load.</summary>
        public static void SaveOwnedWallet(GameSession s)
        {
            var p = ProfileManager.Active;
            if (s == null || p == null) return;
            WriteWalletOwned(s.Wardrobe, p);
            ProfileManager.Save();
        }

        /// <summary>Persist everything incl. the worn outfit (resolving equipped ids through the catalog). Used when the
        /// wardrobe changes 穿搭 / deletes an item / closes.</summary>
        public static void SaveAll(GameSession s)
        {
            var p = ProfileManager.Active;
            if (s == null || p == null) return;
            WriteWalletOwned(s.Wardrobe, p);
            WriteEquipped(s.Wardrobe, p, s.Gender == 1 ? ItemSex.Male : ItemSex.Female, id => AvatarItemCatalog.Instance.ById(id));
            ProfileManager.Save();
        }
    }
}
