using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using Sdo.Shop;

namespace Sdo.Game
{
    /// <summary>
    /// The buyable avatar catalog for the 商城: item names + prices decoded from the client's <c>iteminfo.dat</c>
    /// (see <see cref="IteminfoReader"/>), filtered to clothing whose body-part mesh is actually present on disk
    /// (renderable / try-on-able), grouped by gender + slot for the shop UI. Loaded once and cached. The catalog still
    /// HOLDS non-renderable rows (so <see cref="ById"/> resolves owned/equipped ids), but the shop window filters them
    /// out via <see cref="IsRenderable"/> — items with no extracted model are hidden, not listed.
    /// </summary>
    public sealed class AvatarItemCatalog
    {
        private static AvatarItemCatalog _instance;
        /// <summary>Lazily loaded, cached catalog.</summary>
        public static AvatarItemCatalog Instance => _instance ?? (_instance = Load());

        /// <summary>Every clothing (worn) item in the catalog, renderable or not.</summary>
        public IReadOnlyList<ShopItem> Clothing { get; }
        public int Count => Clothing.Count;

        private readonly Dictionary<(ItemSex, EquipSlot), List<ShopItem>> _groups;
        private readonly HashSet<string> _meshFiles;   // available MSH filenames across Root + dev Datas
        private readonly Dictionary<int, OutfitSet> _sets;   // 套装 setinfo: setId → component list (keyed by ShopItem.ModelId)

        private AvatarItemCatalog(List<ShopItem> clothing,
                                  Dictionary<(ItemSex, EquipSlot), List<ShopItem>> groups,
                                  HashSet<string> meshFiles, Dictionary<int, OutfitSet> sets)
        {
            Clothing = clothing; _groups = groups; _meshFiles = meshFiles; _sets = sets;
        }

        // 套装 mesh slot tokens (component 檔名內嵌的部位) — 用 modelId+性別+token 直接 O(1) 命中 _meshFiles。
        private static readonly string[] OutfitSlotTokens =
            { "COAT", "PANT", "HAIR", "SHOES", "HAND", "ONE", "GLASS", "CHIBANG", "LINGDANG", "FACE_HUAN", "FACE" };

        /// <summary>The 套装 (outfit set) an outfit row links to (by ModelId == setId), or null.</summary>
        public OutfitSet SetForItem(ShopItem it)
            => (it != null && _sets != null && _sets.TryGetValue(it.ModelId, out var s)) ? s : null;

        /// <summary>Resolve an outfit's component garments to AVATAR-relative .msh paths (prefer the outfit's gender,
        /// fall back to the other for cross-gender shared components). Empty if the set/meshes are absent.</summary>
        public List<string> OutfitComponentMeshes(ShopItem outfitItem)
        {
            var res = new List<string>();
            var set = SetForItem(outfitItem);
            if (set == null) return res;
            var g = ItemTypes.GenderOf(outfitItem.Category, outfitItem.Name) == ItemSex.Male ? "MAN" : "WOMAN";
            foreach (var c in set.Components)
            {
                string hit = FindComponentMesh(c.ModelId, g) ?? FindComponentMesh(c.ModelId, g == "MAN" ? "WOMAN" : "MAN");
                if (hit != null) res.Add("AVATAR/" + hit);
            }
            return res;
        }

        private string FindComponentMesh(int modelId, string g)
        {
            string id = modelId.ToString("D6");
            foreach (var tok in OutfitSlotTokens)
            {
                string f = id + "_" + g + "_" + tok + ".MSH";
                if (_meshFiles.Contains(f)) return f;
            }
            return null;
        }

        /// <summary>True if this item can be shown in 3D: a single garment whose mesh is on disk, OR an outfit whose
        /// set has at least one resolvable component mesh.</summary>
        public bool IsRenderable(ShopItem it)
        {
            if (it == null) return false;
            if (it.EquipSlot == EquipSlot.Outfit) return OutfitComponentMeshes(it).Count > 0;
            var rel = it.MshRelPath;
            return rel != null && _meshFiles.Contains(rel.Substring(rel.LastIndexOf('/') + 1));
        }

        /// <summary>Items of a gender + worn slot (e.g. female hair), in catalog order.</summary>
        public IReadOnlyList<ShopItem> Group(ItemSex sex, EquipSlot slot)
            => _groups.TryGetValue((sex, slot), out var l) ? l : (IReadOnlyList<ShopItem>)Array.Empty<ShopItem>();

        private static int CategoryFor(ItemSex sex, EquipSlot slot)
        {
            bool m = sex == ItemSex.Male;
            switch (slot)
            {
                case EquipSlot.Wings:      return m ? ItemCategory.WingsMale : ItemCategory.WingsFemale;
                case EquipSlot.Expression: return m ? ItemCategory.FaceMale  : ItemCategory.FaceFemale;   // 表情 mesh = _FACE_HUAN
                default: return 0;
            }
        }

        // Synthesised mesh-only rows get an Id in a high range that can't collide with a real iteminfo Id, so ById can
        // resolve an equipped synth item back to its ShopItem (→ its mesh) and it actually wears (user: 無名翅膀穿不起來).
        private const int SynthIdBase = 90_000_000;
        private readonly Dictionary<int, ShopItem> _synthById = new Dictionary<int, ShopItem>();
        private readonly Dictionary<(ItemSex, EquipSlot), List<ShopItem>> _meshModelCache = new Dictionary<(ItemSex, EquipSlot), List<ShopItem>>();

        /// <summary>Every model for a mesh-backed slot (e.g. Wings/CHIBANG): the named iteminfo rows PLUS every extra
        /// model that ONLY exists as a mesh on disk — synthesised as a row named by its 6-digit serial, permanent, 100M
        /// (user: 無名翅膀改100M/用序號當名字) — so the shop can browse the full set. iteminfo lists only ~100 of the
        /// ~1000 wing meshes shipped; 翅膀 wants them all (user #3). Named rows first (keep their names/prices), then the
        /// mesh-only extras sorted by modelId. Cached + registered in <see cref="ById"/> so a tried-on synth wing wears.</summary>
        public IReadOnlyList<ShopItem> AllMeshModels(ItemSex sex, EquipSlot slot)
        {
            if (_meshModelCache.TryGetValue((sex, slot), out var cached)) return cached;
            var named = new List<ShopItem>(Group(sex, slot));
            int cat = CategoryFor(sex, slot);
            string token = cat == 0 ? null : ItemTypes.MshSlotSuffix(cat);
            if (token == null) { _meshModelCache[(sex, slot)] = named; return named; }
            string g = sex == ItemSex.Male ? "MAN" : "WOMAN";
            string suffix = "_" + g + "_" + token + ".MSH";
            bool isExpr = slot == EquipSlot.Expression;
            var have = new HashSet<int>();
            foreach (var it in named) have.Add(it.ModelId);
            var extra = new List<ShopItem>();
            foreach (var f in _meshFiles)
            {
                if (!f.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                int us = f.IndexOf('_');
                if (us <= 0 || !int.TryParse(f.Substring(0, us), out var modelId)) continue;
                if (!have.Add(modelId)) continue;
                if (isExpr && !IsGoodExpressionModel(modelId, g)) continue;   // 濾掉會渲染成 空白/破圖/假臉 的異常表情 (user)
                var syn = new ShopItem
                {
                    Id = SynthIdBase + modelId, Name = modelId.ToString("D6"), Price = 100, PriceCategoryRaw = 1,   // 1=Coins→M 幣 (100M；user:無名翅膀改100M)
                    ModelId = modelId, Category = cat, MinLevel = 1, DurationDays = -1, Quantity = -1,
                    SexRaw = sex == ItemSex.Male ? 1 : 0,
                };
                extra.Add(syn);
                _synthById[syn.Id] = syn;   // 讓 ById(該Id) 找得到 → 試穿時 RebuildAvatar 解得出 mesh → 穿得起來
            }
            extra.Sort((a, b) => a.ModelId.CompareTo(b.ModelId));
            named.AddRange(extra);
            _meshModelCache[(sex, slot)] = named;
            return named;
        }

        // 已知會渲染異常、但檔案層面看起來正常(有貼圖、mesh 引自己)的表情 → 顯式排除 (user 回報但 heuristic 抓不到)：
        // 015352 = 重複 mesh(FACE_HUAN_/EXPRESSION)+混 DXT1/DXT3;036682 = 512² 貼圖 + 烘死的裝飾。
        private static readonly HashSet<int> BadExpressionModels = new HashSet<int> { 15352, 36682 };

        // 合成(無名)表情是否能正常當一張臉顯示。濾掉三種官方資料異常 (user: 空白/破圖/顯示錯的東西)：
        //   ① 沒有自己的 HUAN 膚色貼圖 → 一團空白(如 012883);
        //   ② mesh 引到「別人 id / 別性別」的臉貼圖，或引到 _Basic_ 衣服貼圖 → 臉的一部分破圖/貼錯(如 017676→017675_man、028496→W_Basic_Coat2);
        //   ③ 顯式黑名單 (015352/036682)。
        private bool IsGoodExpressionModel(int modelId, string g)
        {
            if (BadExpressionModels.Contains(modelId)) return false;
            string id6 = modelId.ToString("D6");
            string mshName = id6 + "_" + g + "_FACE_HUAN.MSH";
            foreach (var dir in AvatarDirs())
            {
                string mshPath = Path.Combine(dir, mshName);
                if (!File.Exists(mshPath)) continue;
                // ① 必須有自己的最白膚色臉貼圖 (ForceLightExpressionFace 要用的那張)，否則整臉空白
                if (!File.Exists(Path.Combine(dir, id6 + "_" + g + "_FACE_HUAN0.DDS")) &&
                    !File.Exists(Path.Combine(dir, id6 + "_" + g + "_FACE_HUAN.DDS"))) return false;
                // ② mesh 內嵌的貼圖名不能引到別的 id / _Basic_ 衣服貼圖
                return !ExpressionMeshRefsForeign(mshPath, id6);
            }
            return false;   // 磁碟找不到 mesh
        }

        // 掃 MSH 內嵌 ASCII 貼圖名：出現 "_basic_"(衣服貼圖) 或 "NNNNNN_(man|woman)_face_huan" 且 NNNNNN≠本身 id → 引到外來貼圖。
        private static bool ExpressionMeshRefsForeign(string mshPath, string id6)
        {
            try
            {
                var s = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(mshPath)).ToLowerInvariant();
                if (s.Contains("_basic_")) return true;
                foreach (System.Text.RegularExpressions.Match m in
                         System.Text.RegularExpressions.Regex.Matches(s, @"(\d{6})_(?:man|woman)_face_huan"))
                    if (m.Groups[1].Value != id6) return true;
            }
            catch { }
            return false;
        }

        private Dictionary<int, ShopItem> _byId;
        /// <summary>Look up a clothing item by id (e.g. to resolve the currently-equipped ids into meshes). Null if unknown.
        /// Falls back to the synthesised mesh-only rows (<see cref="AllMeshModels"/>) so a tried-on unnamed wing resolves.</summary>
        public ShopItem ById(int id)
        {
            if (id >= SynthIdBase) return _synthById.TryGetValue(id, out var syn) ? syn : null;
            if (_byId == null)
            {
                _byId = new Dictionary<int, ShopItem>(Clothing.Count);
                foreach (var it in Clothing) _byId[it.Id] = it;
            }
            return _byId.TryGetValue(id, out var v) ? v : null;
        }

        /// <summary>Log the first <paramref name="max"/> catalog items (name / gender+slot / price+currency /
        /// try-on-able) — a quick way to confirm the iteminfo.dat pipeline + GBK Chinese-name decoding work in the
        /// current Unity runtime (driven by the Tools/Shop/Dump Catalog editor menu).</summary>
        public void DumpToLog(int max = 12)
        {
            Debug.Log($"[shop] catalog: {Count} clothing items, {_meshFiles.Count} meshes on disk");
            int n = 0;
            foreach (var it in Clothing)
            {
                Debug.Log($"[shop]   [{it.Id}] {it.Name} | {ItemTypes.SexFromCategory(it.Category)} {it.EquipSlot} | {it.Price} {it.Currency} | try-on={IsRenderable(it)}");
                if (++n >= max) break;
            }
        }

        public static AvatarItemCatalog Load()
        {
            var meshFiles = BuildMeshFileSet();

            List<ShopItem> items = new List<ShopItem>();
            try
            {
                var path = ResolveIteminfoPath();
                if (path != null) items = IteminfoReader.Parse(File.ReadAllBytes(path), TryGetGbk());
                else Debug.LogWarning("[shop] iteminfo.dat not found — catalog is empty");
            }
            catch (Exception e) { Debug.LogWarning("[shop] iteminfo load failed: " + e.Message); }

            var sets = new Dictionary<int, OutfitSet>();
            try
            {
                var sp = ResolveSetinfoPath();
                if (sp != null) sets = SetinfoReader.Parse(File.ReadAllBytes(sp), TryGetGbk());
            }
            catch (Exception e) { Debug.LogWarning("[shop] setinfo load failed: " + e.Message); }

            var clothing = new List<ShopItem>();
            var groups = new Dictionary<(ItemSex, EquipSlot), List<ShopItem>>();
            foreach (var it in items)
            {
                if (it.SlotType != ItemSlotType.Clothes) continue;   // skip consumables / effects
                var slot = it.EquipSlot;
                if (slot == EquipSlot.None) continue;
                clothing.Add(it);
                var key = (ItemTypes.GenderOf(it.Category, it.Name), slot);   // GenderOf 修 cat203 套装 (sex byte 皆0,靠名字)
                if (!groups.TryGetValue(key, out var l)) groups[key] = l = new List<ShopItem>();
                l.Add(it);
            }
            Debug.Log($"[shop] catalog: {clothing.Count} clothing items, {groups.Count} groups, {meshFiles.Count} meshes, {sets.Count} sets");
            return new AvatarItemCatalog(clothing, groups, meshFiles, sets);
        }

        // setinfo.dat sits beside iteminfo.dat (online 閉撰敃氪 pack). Same folders as ResolveIteminfoPath.
        private static string ResolveSetinfoPath()
        {
            var cands = new[]
            {
                Path.Combine(SdoExtracted.Root, "setinfo.dat"),
                Path.Combine(SdoExtracted.Root, "AVATAR", "setinfo.dat"),
            };
            foreach (var c in cands) if (File.Exists(c)) return c;
            try
            {
                var assets = Directory.GetParent(SdoExtracted.Root)?.Parent?.FullName;
                if (assets != null && Directory.Exists(assets))
                    foreach (var sub in Directory.GetDirectories(assets))
                    {
                        var f = Path.Combine(sub, "setinfo.dat");
                        if (File.Exists(f)) return f;
                    }
            }
            catch { }
            return null;
        }

        private static HashSet<string> BuildMeshFileSet()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in AvatarDirs())
            {
                try { if (Directory.Exists(dir)) foreach (var f in Directory.GetFiles(dir, "*.MSH")) set.Add(Path.GetFileName(f)); }
                catch { }
            }
            return set;
        }

        // Where avatar body-part meshes live: the runtime data root first, then the dev full-catalog staging.
        private static IEnumerable<string> AvatarDirs()
        {
            yield return Path.Combine(SdoExtracted.Root, "AVATAR");
            string assets = null;
            try { assets = Directory.GetParent(SdoExtracted.Root)?.Parent?.FullName; } catch { }
            if (assets != null) yield return Path.Combine(assets, "Datas", "AVATAR");
        }

        // iteminfo.dat: beside/under the data root in a build, or in a sibling assets folder in the dev repo.
        private static string ResolveIteminfoPath()
        {
            var cands = new[]
            {
                Path.Combine(SdoExtracted.Root, "iteminfo.dat"),
                Path.Combine(SdoExtracted.Root, "AVATAR", "iteminfo.dat"),
            };
            foreach (var c in cands) if (File.Exists(c)) return c;
            try
            {
                var assets = Directory.GetParent(SdoExtracted.Root)?.Parent?.FullName;   // .../assets
                if (assets != null && Directory.Exists(assets))
                    foreach (var sub in Directory.GetDirectories(assets))
                    {
                        var f = Path.Combine(sub, "iteminfo.dat");
                        if (File.Exists(f)) return f;
                    }
            }
            catch { }
            return null;
        }

        // GBK/CP936 decoder for the Simplified-Chinese names. Returns null if the runtime lacks the codepage (the
        // reader then falls back to byte-preserving Latin1 — names show as mojibake but the catalog still works). A
        // build step can pre-convert to UTF-8 to remove this dependency.
        private static Encoding TryGetGbk()
        {
            try { return Encoding.GetEncoding(936); } catch { }
            try { return Encoding.GetEncoding("GB2312"); } catch { return null; }
        }
    }
}
