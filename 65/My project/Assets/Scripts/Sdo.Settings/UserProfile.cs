using System;
using System.Collections.Generic;

namespace Sdo.Settings
{
    [Serializable]
    public class AvatarOutfit
    {
        public string face = "";
        public string hair = "";
        public string coat = "";
        public string pant = "";
        public string shoes = "";
        public string hand = "";

        public static AvatarOutfit FromParts(string[] parts)
        {
            var o = new AvatarOutfit();
            o.FillMissing(parts);
            return o;
        }

        public void FillMissing(string[] parts)
        {
            if (parts == null) return;
            if (parts.Length > 0 && string.IsNullOrEmpty(face)) face = parts[0];
            if (parts.Length > 1 && string.IsNullOrEmpty(hair)) hair = parts[1];
            if (parts.Length > 2 && string.IsNullOrEmpty(coat)) coat = parts[2];
            if (parts.Length > 3 && string.IsNullOrEmpty(pant)) pant = parts[3];
            if (parts.Length > 4 && string.IsNullOrEmpty(shoes)) shoes = parts[4];
            if (parts.Length > 5 && string.IsNullOrEmpty(hand)) hand = parts[5];
            Clean();
        }

        public string[] ToParts()
        {
            Clean();
            return new[] { face, hair, coat, pant, shoes, hand };
        }

        public bool HasGenderMismatch(int gender)
        {
            var parts = ToParts();
            for (int i = 0; i < parts.Length; i++)
            {
                string u = (parts[i] ?? "").ToUpperInvariant();
                if (gender == 1 && u.Contains("_WOMAN_")) return true;
                if (gender != 1 && u.Contains("_MAN_")) return true;
            }
            return false;
        }

        private void Clean()
        {
            face = UserProfile.NormalizeClothPath(face);
            hair = UserProfile.NormalizeClothPath(hair);
            coat = UserProfile.NormalizeClothPath(coat);
            pant = UserProfile.NormalizeClothPath(pant);
            shoes = UserProfile.NormalizeClothPath(shoes);
            hand = UserProfile.NormalizeClothPath(hand);
        }
    }

    /// <summary>Persisted three-balance wallet (M=coins / G=points / H=bonus). <see cref="seeded"/> distinguishes a
    /// brand-new profile (give the starter allowance once) from one whose balances have legitimately hit 0 — without it,
    /// spending down to 0 would re-trigger the starter grant on the next launch. Mirrors <c>Sdo.Shop.Wallet</c>; the
    /// bridge (WardrobeStore) copies between the two.</summary>
    [Serializable]
    public class WalletSave
    {
        public int coins;
        public int points;
        public int bonus;
        public bool seeded;   // false = never granted the starter allowance yet
    }

    /// <summary>One owned 商城 item, keyed by its shop item id (mirrors <c>Sdo.Shop.OwnedItem</c>): what, when it lapses,
    /// how many, and which inventory bucket (200=clothes / 400=consumables). This is the id-based inventory the 儲物櫃
    /// (wardrobe) lists — the parallel path-based <see cref="UserProfile.ownedClothes"/> stays for the legacy avatar
    /// loaders.</summary>
    [Serializable]
    public class OwnedItemSave
    {
        public int id;
        public long expire = -1;   // -1 = permanent; else Unix-seconds expiry
        public int qty = 1;
        public int slot;           // (int)Sdo.Shop.ItemSlotType (200 clothes / 400 items)
    }

    /// <summary>What is worn in one body slot: <see cref="slot"/> = (int)Sdo.Shop.EquipSlot, <see cref="id"/> = shop
    /// item id. Slot is stored as a raw int so <see cref="Sdo.Settings"/> stays a leaf assembly (no Sdo.Shop ref).</summary>
    [Serializable]
    public class EquipSave
    {
        public int slot;
        public int id;
    }

    [Serializable]
    public class UserProfile
    {
        public string id = "00000000";
        public string name = "玩家001";
        public int gender = 0;
        public int avatarId = 0;
        // 體型 (胖瘦): 每個角色自己的身材參數 (faithful SDO body index 0..4 → SdoBodyShape.WeightFromIndex; 0=瘦 1=標準 2..4=胖)。
        // 房間/遊戲的本機角色 avatar 讀這個值決定骨骼橫截面縮放;服裝預覽(商店/儲物櫃)則一律用標準身材(index 1),不受此值影響。
        public int bodyShapeIndex = 0;
        public string[] ownedClothes = new string[0];
        public AvatarOutfit equippedClothes = new AvatarOutfit();
        public string createdAt = "";
        public string lastPlayedAt = "";

        // ---- 商城/儲物櫃 持久化 (item-id 為鍵；由 WardrobeStore 在 Sdo.Shop.Wardrobe 之間橋接)。金幣也記在這裡 (wallet)。----
        public WalletSave wallet = new WalletSave();
        public int clothSlots = 9;   // 服飾欄容量：預設 1 頁=9 格(裝得下一整套穿搭)，按「服饰栏扩充」每次 +9，最多 1000（Wardrobe.ClothSlotCount）
        public OwnedItemSave[] ownedItems = new OwnedItemSave[0];   // 擁有的商城道具 (含衣物 id)
        public EquipSave[] equippedItems = new EquipSave[0];        // 目前穿的每個部位 → item id
        // 目前穿搭解析出的完整 mesh 部位清單 (含飾品/翅膀/表情，順序=AvatarOutfit.Order)。房間/遊戲 avatar 的權威來源；
        // 空 (舊檔) 時退回 6 部位的 equippedClothes。由 WardrobeStore 在存檔時用 Sdo.Game.AvatarOutfit.ResolveParts 算出。
        public string[] equippedParts = new string[0];

        public UserProfile() { }

        public UserProfile(string id, string name, int gender)
        {
            this.id = id;
            this.name = name;
            this.gender = gender;
        }

        public UserProfile Sanitize()
        {
            if (string.IsNullOrEmpty(id)) id = "00000000";
            if (string.IsNullOrEmpty(name)) name = "玩家001";
            gender = gender == 1 ? 1 : 0;
            if (avatarId < 0) avatarId = 0;
            if (bodyShapeIndex < 0) bodyShapeIndex = 0; else if (bodyShapeIndex > 4) bodyShapeIndex = 4;   // 體型 index 夾在 0..4
            if (wallet == null) wallet = new WalletSave();
            if (clothSlots < 9) clothSlots = 9; else if (clothSlots > 1000) clothSlots = 1000;   // 最少 1 頁(9)；舊檔存的 3 會自動補到 9
            if (ownedItems == null) ownedItems = new OwnedItemSave[0];
            if (equippedItems == null) equippedItems = new EquipSave[0];
            if (equippedParts == null) equippedParts = new string[0];
            EnsureWardrobe();
            return this;
        }

        /// <summary>The ordered mesh part paths the room/gameplay avatar wears. Prefers the full <see cref="equippedParts"/>
        /// list (includes accessories/wings/expression, written by WardrobeStore), falling back to the legacy 6-slot
        /// <see cref="equippedClothes"/> for profiles saved before the 儲物櫃 (or when nothing has been equipped yet).</summary>
        public string[] EquippedAvatarParts()
        {
            Sanitize();
            if (equippedParts != null && equippedParts.Length > 0) return Clone(equippedParts);
            return Clone(equippedClothes.ToParts());
        }

        private void EnsureWardrobe()
        {
            var defaults = DefaultClothesForGender(gender);
            if (equippedClothes == null || equippedClothes.HasGenderMismatch(gender))
                equippedClothes = AvatarOutfit.FromParts(defaults);
            else
                equippedClothes.FillMissing(defaults);

            var owned = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddClothes(owned, seen, ownedClothes);
            AddClothes(owned, seen, defaults);
            AddClothes(owned, seen, equippedClothes.ToParts());
            ownedClothes = owned.ToArray();
        }

        private static void AddClothes(List<string> dst, HashSet<string> seen, string[] src)
        {
            if (src == null) return;
            for (int i = 0; i < src.Length; i++)
            {
                string rel = NormalizeClothPath(src[i]);
                if (string.IsNullOrEmpty(rel)) continue;
                if (seen.Add(rel)) dst.Add(rel);
            }
        }

        internal static string NormalizeClothPath(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return "";
            rel = rel.Trim().Replace('\\', '/');
            if (rel.Length == 0) return "";
            if (rel.IndexOf('/') < 0) rel = "AVATAR/" + rel;
            if (!rel.EndsWith(".MSH", StringComparison.OrdinalIgnoreCase)) rel += ".MSH";
            return rel;
        }

        private static string[] Clone(string[] src)
        {
            if (src == null) return new string[0];
            var dst = new string[src.Length];
            Array.Copy(src, dst, src.Length);
            return dst;
        }

        public static string[] DefaultClothesForGender(int gender)
        {
            return gender == 1 ? new[]
            {
                "AVATAR/900001_MAN_FACE.MSH",
                "AVATAR/900002_MAN_HAIR.MSH",
                "AVATAR/900003_MAN_COAT.MSH",
                "AVATAR/900004_MAN_PANT.MSH",
                "AVATAR/900006_MAN_SHOES.MSH",
                "AVATAR/900005_MAN_HAND.MSH",
            } : new[]
            {
                "AVATAR/900007_WOMAN_FACE.MSH",
                "AVATAR/900017_WOMAN_HAIR.MSH",
                "AVATAR/900018_WOMAN_COAT.MSH",
                "AVATAR/900019_WOMAN_PANT.MSH",
                "AVATAR/900020_WOMAN_SHOES.MSH",
                "AVATAR/900011_WOMAN_HAND.MSH",
            };
        }
    }
}
