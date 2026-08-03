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
        /// <summary>累計知名度(名声)—— 大廳右下角顯示成 <c>LV 2 (15)</c> 的那個數字(格式/等級換算見 <see cref="FameLevel"/>)。
        /// 放在這一區是因為它**由購物驅動**:唯一會加它的是商城的購買(ShopScreen 的 DoBuy / DoBuyAll,每件依價格換算),
        /// 🔴 快速充值不算 —— 那是免費送錢,能算的話一鍵就刷滿等級。舊存檔沒有這個 key,JsonUtility 會給預設值 0
        /// (= LV 1),所以不需要任何 migration。</summary>
        public int fame;

        // ---- 個人資料(家族/等級)的 per-user 覆寫 ----
        // **外層的** DATA/PROFILE/profile.json 是所有角色共用的 Default(見 ProfileDefaults);這三個欄位讓
        // 「這個角色」可以有自己的家族/等級。解析一律走 ProfileFields —— 不要直接讀這裡,也不要直接讀 ProfileDefaults。
        public string familyName = "";
        public string familyEmblem = "";
        public string playerLevel = "";

        /// <summary>目前等級內累積的經驗值(滿級後固定 0)。每局結算加上 <c>Sdo.Ruleset.Reward.Experience</c>,
        /// 跨過門檻就把 <see cref="playerLevel"/> 往上推(曲線見 <see cref="PlayerLevel"/>,落地在
        /// <see cref="ProfileManager.AddExperience"/>)。經驗值**只有角色自己有**,外層那份 Default 沒有這欄。</summary>
        public int exp;

        /// <summary>
        /// 這個角色有沒有自己的 [Profile] 設定?
        ///
        /// 🔴 這個 latch 是必要的,不能用「欄位留空 = 沿用 Default」代替:現行約定是
        /// **familyName 留空 = 不顯示家族**、**playerLevel 留空 = 不顯示等級**,而 JsonUtility 對 string
        /// 一律給 ""(分不出「這個 key 不存在」與「使用者刻意清空」)。少了這個旗標,
        /// 「我就是不想顯示家族」下次開機就會被 config.ini 的 Default 蓋回來。
        ///
        /// false(舊檔預設)= 三個欄位全部吃 config.ini;一旦這個角色自己設過,就整組吃 profile.json 的值。
        /// 與 <see cref="WalletSave.seeded"/> 是同一種手法。
        /// </summary>
        public bool hasProfileOverrides;

        // ---- 個人資料視窗裡「自己填」的四格(官方的 city_edit / QQ_edit / constellation_edit / age_edit)----
        //
        // 純粹是給別人看的自我介紹欄位:server 不知道、也不驗證(這套連線根本沒有帳號持久化),就是存在
        // **自己這台機器**的 profile.json 裡,下次開遊戲還在。看別人的資料時這四格一律空白 —— 我們拿不到
        // 對方填了什麼,座位快照只帶得到 Id / 名字 / 等級 / 家族。
        //
        // 長度限制照官方 EditBox 的 limittext:城市 12、即時通 12(digitcase=只收數字)、星座 6、年齡 2(只數字)。
        // 年齡存字串而不是 int:官方那格就是「沒填 = 空白」,存 0 的話會變成畫面上多一個沒人填過的 0。
        public string city = "";
        public string imAccount = "";
        public string constellation = "";
        public string age = "";

        /// <summary>累計遊玩統計(個人資料頁的命中率/勝率就是它算的)。見 <see cref="PlayStats"/>。</summary>
        public PlayStats stats = new PlayStats();

        /// <summary>本機好友清單。server 沒有帳號持久化 → 好友是這台機器記得的。見 <see cref="FriendEntry"/>。</summary>
        public FriendEntry[] friends = new FriendEntry[0];

        /// <summary>本機黑名單(官方「設置阻止」)。與 <see cref="friends"/> 同一種資料、同一個理由住在本機,
        /// 語意是「我這台機器不顯示他說的話」——見 <see cref="BlockList"/>。</summary>
        public FriendEntry[] blocked = new FriendEntry[0];

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
            if (fame < 0) fame = 0;   // 知名度只會往上加,負值必是壞檔 → 當 0(= LV 1)
            if (stats == null) stats = new PlayStats(); else stats.Sanitize();
            friends = SanitizeFriends(friends);
            blocked = SanitizeFriends(blocked);   // 同一種清單(名字為鍵、去重、丟殘骸),見 BlockList
            // 家族/等級：只去頭尾空白（前後空白會讓「留空＝刻意不顯示」的判定失真，見 hasProfileOverrides）。
            familyName = (familyName ?? "").Trim();
            familyEmblem = (familyEmblem ?? "").Trim();
            playerLevel = PlayerLevel.CleanText(playerLevel);
            if (exp < 0) exp = 0;   // 經驗只會往上加，負值必是壞檔
            EnsureWardrobe();
            return this;
        }

        /// <summary>好友/黑名單清單防呆:丟掉沒有名字的殘骸、依名字去重(同一個人被加兩次只留先加的那筆)。
        /// 鍵是名字而不是 id —— 理由見 <see cref="FriendList"/>。</summary>
        private static FriendEntry[] SanitizeFriends(FriendEntry[] src)
        {
            if (src == null) return new FriendEntry[0];
            var outList = new List<FriendEntry>(src.Length);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < src.Length; i++)
            {
                var f = src[i];
                if (f == null) continue;
                string name = (f.name ?? "").Trim();
                if (name.Length == 0) continue;
                if (!seen.Add(name)) continue;
                f.name = name;
                f.id = (f.id ?? "").Trim();
                outList.Add(f);
            }
            return outList.ToArray();
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
