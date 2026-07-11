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
                if (!w.Owns(kv.Value)) continue;   // 只存「擁有且穿著」的：商城試穿(未買)不落地，買了才會存成真的穿搭
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
        /// wardrobe first so re-opening a screen starts from the saved state (not stale in-memory shop try-on). Also heals
        /// a stale <see cref="UserProfile.equippedParts"/> cache (see <see cref="HealEquippedParts"/>).</summary>
        public static void Load(GameSession s)
        {
            if (s == null) return;
            s.Wardrobe.Reset();
            var p = ProfileManager.Active;
            ApplyProfileToWardrobe(p, s.Wardrobe);
            HealEquippedParts(p, s.Gender == 1 ? ItemSex.Male : ItemSex.Female);
        }

        /// <summary>Re-derive <see cref="UserProfile.equippedParts"/> (the room/gender-select/gameplay avatar's mesh-path
        /// cache) from the id-based <see cref="UserProfile.equippedItems"/> and persist if it changed. Fixes profiles saved
        /// while a synth accessory (翅膀/表情/项链) couldn't resolve: the wardrobe rebuilds from equippedItems (so it shows
        /// the accessory) but the room/gender-select read the stale equippedParts (so they don't) — user bug: 儲物櫃有、
        /// room/選性別沒有. Only adopts a re-derivation that doesn't LOSE parts (guards against a transiently-unresolvable
        /// id blanking the avatar); leaves legacy (no equippedItems) profiles untouched. Non-fatal on any error.</summary>
        public static void HealEquippedParts(UserProfile p, ItemSex sex)
        {
            if (p == null || p.equippedItems == null || p.equippedItems.Length == 0) return;
            try
            {
                var reDerived = ResolveEquippedParts(p, sex, id => AvatarItemCatalog.Instance.ById(id));
                var before = p.equippedParts ?? new string[0];
                if (reDerived.Length >= before.Length && !SamePaths(before, reDerived))
                {
                    p.equippedParts = reDerived;
                    ProfileManager.Save();
                }
            }
            catch (Exception e) { UnityEngine.Debug.LogWarning("[wardrobe] heal equippedParts failed (non-fatal): " + e.Message); }
        }

        /// <summary>Ordered worn mesh parts re-derived from a profile's id-based <see cref="UserProfile.equippedItems"/> via
        /// the catalog (<paramref name="byId"/>), so synth 翅膀/表情/项链 are included — the authoritative render list the
        /// room / gender-select / gameplay avatar should use. Falls back to the profile's existing cache/legacy parts when
        /// there is nothing id-based to resolve (never blanks the avatar). Pure (byId/meshExists injectable) → unit-tested.</summary>
        /// <summary>Gender-int overload (0=female / 1=male) for callers that don't reference <see cref="ItemSex"/>.</summary>
        public static string[] ResolveEquippedParts(UserProfile p, int gender, Func<int, ShopItem> byId, Func<string, bool> meshExists = null)
            => ResolveEquippedParts(p, gender == 1 ? ItemSex.Male : ItemSex.Female, byId, meshExists);

        public static string[] ResolveEquippedParts(UserProfile p, ItemSex sex, Func<int, ShopItem> byId, Func<string, bool> meshExists = null)
        {
            if (p == null) return new string[0];
            var eq = p.equippedItems;
            if (eq == null || eq.Length == 0 || byId == null) return p.EquippedAvatarParts();
            var items = new List<ShopItem>();
            foreach (var e in eq)
            {
                if (e == null || e.id == 0) continue;
                var it = byId(e.id);
                if (it != null) items.Add(it);
            }
            if (items.Count == 0) return p.EquippedAvatarParts();   // couldn't resolve any → keep existing (don't blank)
            var parts = Sdo.Game.AvatarOutfit.ResolveParts(sex, items, meshExists);
            return parts != null && parts.Count > 0 ? parts.ToArray() : p.EquippedAvatarParts();
        }

        private static bool SamePaths(string[] a, string[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase)) return false;
            return true;
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
