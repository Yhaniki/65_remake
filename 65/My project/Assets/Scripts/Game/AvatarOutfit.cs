using System;
using System.Collections.Generic;
using System.IO;
using Sdo.Shop;

namespace Sdo.Game
{
    /// <summary>
    /// Resolves a worn outfit — equipped 商城 items layered over the WOMAN defaults — into the ordered list of
    /// body-part <c>.msh</c> paths that <see cref="SdoAvatarBuilder"/> loads. This is the single seam the shop's
    /// 換裝 / try-on drives: pick items, resolve parts, rebuild the avatar. Pure logic (mesh existence is injectable),
    /// so the layering rules are unit-testable.
    /// </summary>
    public static class AvatarOutfit
    {
        /// <summary>Default WOMAN body parts keyed by the slot each occupies, so an equipped item replaces exactly one
        /// slot (the same starter costume the in-game dancer / lobby avatar use).</summary>
        public static readonly IReadOnlyDictionary<EquipSlot, string> WomanDefaults = new Dictionary<EquipSlot, string>
        {
            { EquipSlot.Face,   "AVATAR/900007_WOMAN_FACE.MSH" },
            { EquipSlot.Hair,   "AVATAR/900017_WOMAN_HAIR.MSH" },
            { EquipSlot.Top,    "AVATAR/900018_WOMAN_COAT.MSH" },
            { EquipSlot.Bottom, "AVATAR/900019_WOMAN_PANT.MSH" },
            { EquipSlot.Shoes,  "AVATAR/900020_WOMAN_SHOES.MSH" },
            { EquipSlot.Gloves, "AVATAR/900011_WOMAN_HAND.MSH" },
        };

        /// <summary>Default MAN body parts (starter male costume), keyed by slot — the male counterpart of
        /// <see cref="WomanDefaults"/> (verified present: 900001 FACE / 900002 HAIR / 900003 COAT / 900004 PANT /
        /// 900005 HAND / 900006 SHOES).</summary>
        public static readonly IReadOnlyDictionary<EquipSlot, string> ManDefaults = new Dictionary<EquipSlot, string>
        {
            { EquipSlot.Face,   "AVATAR/900001_MAN_FACE.MSH" },
            { EquipSlot.Hair,   "AVATAR/900002_MAN_HAIR.MSH" },
            { EquipSlot.Top,    "AVATAR/900003_MAN_COAT.MSH" },
            { EquipSlot.Bottom, "AVATAR/900004_MAN_PANT.MSH" },
            { EquipSlot.Shoes,  "AVATAR/900006_MAN_SHOES.MSH" },
            { EquipSlot.Gloves, "AVATAR/900005_MAN_HAND.MSH" },
        };

        public const string FemaleHrc = "AVATAR/FEMALE.HRC";
        public const string MaleHrc = "AVATAR/MALE.HRC";

        /// <summary>Skeleton for a gender — male clothing skins to MALE.HRC, female to FEMALE.HRC (never mix: a MAN
        /// mesh on the female skeleton, or vice versa, deforms wrong).</summary>
        public static string HrcFor(ItemSex sex) => sex == ItemSex.Male ? MaleHrc : FemaleHrc;

        /// <summary>Starter default parts for a gender.</summary>
        public static IReadOnlyDictionary<EquipSlot, string> DefaultsFor(ItemSex sex)
            => sex == ItemSex.Male ? ManDefaults : WomanDefaults;

        /// <summary>Marker prefix on a parts-list entry meaning "load this mesh but draw ONLY its skin (Basic) ranges" —
        /// the builders strip it and blank every cloth range of that part. See <see cref="WithBodySkinFiller"/>.</summary>
        public const string SkinOnlyPrefix = "skinonly:";

        /// <summary>True when <paramref name="rel"/> carries the <see cref="SkinOnlyPrefix"/>; <paramref name="bare"/>
        /// gets the plain mesh path either way.</summary>
        public static bool IsSkinOnly(string rel, out string bare)
        {
            if (!string.IsNullOrEmpty(rel) && rel.StartsWith(SkinOnlyPrefix, StringComparison.Ordinal))
            { bare = rel.Substring(SkinOnlyPrefix.Length); return true; }
            bare = rel; return false;
        }

        /// <summary>
        /// The BODY's own skin geometry lives inside the COAT and PANT meshes — the torso/arms in the coat, the hips and
        /// legs in the pants. A ONE-PIECE dress replaces the Top slot AND drops the Bottom, so when the dress's cloth
        /// doesn't cover a spot (a skirt slit, a low back) there is simply nothing behind it and the scene shows
        /// straight through (使用者 on 金姬兰:「腳上破一個洞」「背後還是破一塊」). The retail client always has that
        /// body underneath.
        ///
        /// So: whenever a one-piece occupies the Top slot with no separate Bottom, append the gender's DEFAULT pants
        /// (and coat, if a one-piece somehow left the Top empty) tagged <see cref="SkinOnlyPrefix"/> — the builders then
        /// draw only their W/M_Basic skin ranges, never the starter clothes, so the dress keeps its own look while the
        /// body behind it is solid again. Pure and order-preserving; the filler is appended last so it draws after the
        /// existing parts (it is opaque, so queue order is irrelevant).
        /// </summary>
        public static List<string> WithBodySkinFiller(IEnumerable<string> parts, ItemSex sex)
        {
            var res = new List<string>();
            bool hasOnePiece = false, hasBottom = false, hasTop = false;
            if (parts != null)
                foreach (var rel in parts)
                {
                    if (string.IsNullOrEmpty(rel)) continue;
                    res.Add(rel);
                    IsSkinOnly(rel, out var bare);
                    var u = bare.ToUpperInvariant();
                    if (u.Contains("_ONE")) hasOnePiece = true;
                    else if (u.Contains("_PANT")) hasBottom = true;
                    else if (u.Contains("_COAT")) hasTop = true;
                }
            if (!hasOnePiece || hasBottom) return res;          // separate top+bottom already carry the body
            // The break the user actually sees on a halter / backless one-piece is the UPPER BACK near the shoulders
            // (使用者:「金姬兰一直都是上背部靠近肩膀後面破掉，不能只補上面嗎」) — where a low-cut back leaves the body
            // uncovered and the scene shows through. Fill the BARE TORSO (shoulders→waist). Measured: the body torso is
            // ~2.8 core radius vs the dress bodice's ~3.4-4.9, so it sits INSIDE the dress; only the low back / halter
            // opening (where there IS no cloth) shows it. Legs are NOT filled: a one-piece's SKIRT part covers the legs,
            // and the starter/legs fillers poked through slits (reverted, see git history).
            res.Add(SkinOnlyPrefix + BareTorsoFiller(sex));
            _ = hasTop;
            return res;
        }

        /// <summary>
        /// A bare legs + hips mesh used to back a one-piece dress. Chosen by measuring the whole corpus: these are the
        /// garment meshes whose materials are ALL shared skin bases (no cloth at all — 291 of them for women), ranked by
        /// hip radius so the filler always sits INSIDE the dress. 000112_WOMAN_PANT covers Y 8..38 (legs through hips)
        /// at hip radius 4.30, against 5.15 for 024976 金姬兰's skirt in the same band, with only 101 vertices.
        /// The male counterpart 004102_MAN_PANT is the equivalent (bare legs through the waist, Y 8..40).
        /// </summary>
        public static string BareLegsFiller(ItemSex sex)
            => sex == ItemSex.Male ? "AVATAR/004102_MAN_PANT.MSH" : "AVATAR/000112_WOMAN_PANT.MSH";

        /// <summary>
        /// The torso half of the same filler, picked the same way: of the 306 WOMAN_COAT meshes whose materials are ALL
        /// shared skin bases, 002436_WOMAN_COAT is the one that spans the whole torso (Y 38..52, waist to neck) at a
        /// hip-tight radius of 2.83 with 50 vertices — far narrower than any bodice, so it never pokes through. It
        /// continues exactly where <see cref="BareLegsFiller"/> ends (male: 000048_MAN_COAT, Y 40..52).
        /// </summary>
        public static string BareTorsoFiller(ItemSex sex)
            => sex == ItemSex.Male ? "AVATAR/000048_MAN_COAT.MSH" : "AVATAR/002436_WOMAN_COAT.MSH";

        // Stable draw order (Glasses/Necklace/Wings additive — no default; Expression replaces the Face slot, see below).
        private static readonly EquipSlot[] Order =
            { EquipSlot.Face, EquipSlot.Hair, EquipSlot.Top, EquipSlot.Bottom, EquipSlot.Shoes, EquipSlot.Gloves, EquipSlot.Glasses, EquipSlot.Necklace, EquipSlot.Wings };

        /// <summary>
        /// Build the WOMAN parts list wearing <paramref name="equipped"/> over the defaults. A worn item replaces its
        /// slot's mesh (Hair→Hair, Top→Coat, …); a OnePiece replaces Top and drops the separate Bottom; Glasses is
        /// additive. Items whose mesh isn't on disk are skipped (the default stays) — <paramref name="meshExists"/>
        /// defaults to the real Root+Datas resolver.
        /// </summary>
        /// <summary>Female-default overload (back-compat for the female-only callers).</summary>
        public static List<string> ResolveParts(IEnumerable<ShopItem> equipped, Func<string, bool> meshExists = null)
            => ResolveParts(ItemSex.Female, equipped, meshExists);

        /// <summary>As above but for either gender: layers <paramref name="equipped"/> over that gender's defaults.
        /// Equipped items should already be of matching gender (male-category meshes only skin to MALE.HRC).</summary>
        public static List<string> ResolveParts(ItemSex sex, IEnumerable<ShopItem> equipped, Func<string, bool> meshExists = null)
        {
            meshExists = meshExists ?? DefaultMeshExists;
            var slots = new Dictionary<EquipSlot, string>(DefaultsFor(sex));
            if (equipped != null)
                foreach (var it in equipped)
                {
                    if (it == null) continue;
                    var rel = it.MshRelPath;
                    if (rel == null || !meshExists(rel)) continue;
                    switch (it.EquipSlot)
                    {
                        case EquipSlot.OnePiece: slots[EquipSlot.Top] = rel; slots.Remove(EquipSlot.Bottom); break;
                        case EquipSlot.Expression: slots[EquipSlot.Face] = rel; break;   // 表情 = 換臉 (取代預設臉,避免雙臉 z-fight)
                        case EquipSlot.None: break;
                        default: slots[it.EquipSlot] = rel; break;                       // Necklace 等飾品 = 各自 slot (Order 已含)
                    }
                }
            var list = new List<string>(Order.Length);
            foreach (var s in Order)
                if (slots.TryGetValue(s, out var p) && !string.IsNullOrEmpty(p)) list.Add(p);
            return list;
        }

        private static bool DefaultMeshExists(string rel) => File.Exists(SdoAvatarBuilder.ResolveAvatarFile(rel));

        // ---- 試穿疊加 (商城 ComposeParts；由 ShopScreen 搬來成純邏輯) ----

        private static readonly EquipSlot[] ComposeOrder =
            { EquipSlot.Face, EquipSlot.Hair, EquipSlot.Top, EquipSlot.Bottom, EquipSlot.Shoes, EquipSlot.Gloves };

        /// <summary>
        /// 把 overrides(套裝組件 / 試穿單件)逐部位疊到 baseParts 上：連身取代上下著；眼鏡/項鍊/翅膀=附加(依 mesh
        /// token 去重)；其餘覆蓋該部位。沒被 overrides 覆蓋的部位保留 base(「套裝沒有的部位沿用現況」)。
        /// 連身裙佔 Top 槽並拔掉 Bottom——之後換回單件上/下裝時，被連身拔掉的另一半補回 <paramref name="sex"/> 的
        /// 預設件(人物的腿/上身皮膚幾何長在 PANT/COAT mesh 裡，少一件=那段身體整段消失、看起來「透明」)。
        /// </summary>
        public static string[] ComposeParts(ItemSex sex, IEnumerable<string> baseParts, IEnumerable<string> overrides)
        {
            var defaults = DefaultsFor(sex);
            var slots = new Dictionary<EquipSlot, string>();
            var additive = new Dictionary<string, string>();   // token(GLASS/LINGDANG/CHIBANG…) → mesh,去重(base 與 override 同類只留一)
            bool topIsOnePiece = false;
            void Apply(string rel)
            {
                if (string.IsNullOrEmpty(rel)) return;
                var s = SlotFromMeshToken(rel);
                if (s == EquipSlot.OnePiece) { slots[EquipSlot.Top] = rel; slots.Remove(EquipSlot.Bottom); topIsOnePiece = true; }
                else if (s == EquipSlot.Glasses || s == EquipSlot.Necklace || s == EquipSlot.None) additive[MeshToken(rel)] = rel;
                else
                {
                    if (topIsOnePiece && s == EquipSlot.Top && !slots.ContainsKey(EquipSlot.Bottom))
                        slots[EquipSlot.Bottom] = defaults[EquipSlot.Bottom];   // 連身→上衣:補回預設下裝
                    if (topIsOnePiece && s == EquipSlot.Bottom)
                        slots[EquipSlot.Top] = defaults[EquipSlot.Top];         // 連身→下裝:連身脫掉,補回預設上衣
                    if (s == EquipSlot.Top || s == EquipSlot.Bottom) topIsOnePiece = false;
                    slots[s] = rel;
                }
            }
            if (baseParts != null) foreach (var rel in baseParts) Apply(rel);
            if (overrides != null) foreach (var rel in overrides) Apply(rel);
            var list = new List<string>();
            foreach (var s in ComposeOrder)
                if (slots.TryGetValue(s, out var p) && !string.IsNullOrEmpty(p)) list.Add(p);
            list.AddRange(additive.Values);
            return list.ToArray();
        }

        /// <summary>mesh 檔名最後一段部位 token:'AVATAR/023424_WOMAN_HAIR.MSH' → 'HAIR' (附加類去重用)。</summary>
        public static string MeshToken(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return "";
            var n = rel; int dot = n.LastIndexOf('.'); if (dot > 0) n = n.Substring(0, dot);
            int us = n.LastIndexOf('_'); return (us >= 0 ? n.Substring(us + 1) : n).ToUpperInvariant();
        }

        /// <summary>從組件 mesh 檔名的部位 token 推 EquipSlot (CHIBANG 翅膀無對應 slot → None=附加)。</summary>
        public static EquipSlot SlotFromMeshToken(string rel)
        {
            string u = rel.ToUpperInvariant();
            if (u.Contains("_ONE")) return EquipSlot.OnePiece;
            if (u.Contains("_COAT")) return EquipSlot.Top;
            if (u.Contains("_PANT")) return EquipSlot.Bottom;
            if (u.Contains("_HAIR")) return EquipSlot.Hair;
            if (u.Contains("_SHOES")) return EquipSlot.Shoes;
            if (u.Contains("_HAND")) return EquipSlot.Gloves;
            if (u.Contains("_GLASS")) return EquipSlot.Glasses;
            if (u.Contains("_LINGDANG")) return EquipSlot.Necklace;
            if (u.Contains("_FACE")) return EquipSlot.Face;   // FACE / FACE_HUAN
            return EquipSlot.None;   // CHIBANG 翅膀等 → 附加
        }
    }
}
