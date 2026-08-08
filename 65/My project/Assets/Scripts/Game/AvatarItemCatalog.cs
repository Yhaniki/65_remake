using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using Sdo.Shop;
using Sdo.Settings.Vfs;

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

        /// <summary>非衣服的 2D 商品 (道具/藥水/人物特效/寵物/寵物頭飾/寵物衣/寵物道具/禮包) —— 沒有 avatar mesh，
        /// 商品格畫 ITEM2D 圖示。官方 iteminfo 同樣有名字/價格/時效，所以走同一條 ShopItem 管線。</summary>
        public IReadOnlyList<ShopItem> Props { get; }

        private readonly Dictionary<(ItemSex, EquipSlot), List<ShopItem>> _groups;
        private readonly Dictionary<EquipSlot, List<ShopItem>> _propGroups;   // 2D 商品不分性別 (sex byte 皆 2/0，官方也不分頁)
        private readonly HashSet<string> _meshFiles;   // available MSH filenames across Root + dev Datas
        private readonly Dictionary<int, OutfitSet> _sets;   // 套装 setinfo: setId → component list (keyed by ShopItem.ModelId)

        private AvatarItemCatalog(List<ShopItem> clothing,
                                  Dictionary<(ItemSex, EquipSlot), List<ShopItem>> groups,
                                  HashSet<string> meshFiles, Dictionary<int, OutfitSet> sets,
                                  List<ShopItem> props, Dictionary<EquipSlot, List<ShopItem>> propGroups)
        {
            Clothing = clothing; _groups = groups; _meshFiles = meshFiles; _sets = sets;
            Props = props; _propGroups = propGroups;
        }

        /// <summary>一個 2D 商品分頁 (道具/藥水/特效/寵物…) 的商品，catalog 原序。</summary>
        public IReadOnlyList<ShopItem> PropGroup(EquipSlot slot)
            => _propGroups.TryGetValue(slot, out var l) ? l : (IReadOnlyList<ShopItem>)Array.Empty<ShopItem>();

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
                string hit = FindComponentMesh(c.ModelId, g, c.Token) ?? FindComponentMesh(c.ModelId, g == "MAN" ? "WOMAN" : "MAN", c.Token);
                if (hit != null) res.Add("AVATAR/" + hit);
            }
            return res;
        }

        /// <summary>
        /// 一套 (套装) 拆成「一件一件」的商品列 —— 官方買一套 = 那幾件各自進儲物櫃一格 (使用者:「不是一整個進去格子,
        /// 而是各個部分分開各自一個格子」),所以購買/入櫃/換穿走的都是這些單件,套装列本身不入櫃。
        ///
        /// 每個組件先照 <see cref="OutfitComponentMeshes"/> 同一條規則解出 mesh (含跨性別後備),再由檔名決定部位/性別:
        /// 目錄裡有名字的那一列優先 (玩家看到的是真正的商品名),否則合成一列 mesh-only (與商城逐部位分頁同一套 synth id
        /// → 重開遊戲仍解得回 <see cref="ById"/>)。解不出 mesh 或不是穿戴部位 (素體臉) 的組件略過。
        /// </summary>
        public List<ShopItem> OutfitComponentItems(ShopItem outfitItem)
        {
            var res = new List<ShopItem>();
            var set = SetForItem(outfitItem);
            if (set == null) return res;
            bool male = ItemTypes.GenderOf(outfitItem.Category, outfitItem.Name) == ItemSex.Male;
            string g = male ? "MAN" : "WOMAN";
            var seen = new HashSet<int>();
            foreach (var c in set.Components)
            {
                string hit = FindComponentMesh(c.ModelId, g, c.Token) ?? FindComponentMesh(c.ModelId, male ? "WOMAN" : "MAN", c.Token);
                if (hit == null) continue;
                var sex = hit.IndexOf("_MAN_", StringComparison.OrdinalIgnoreCase) >= 0 ? ItemSex.Male : ItemSex.Female;
                var slot = SlotFromMeshFileName(hit);
                if (slot == EquipSlot.None) continue;
                var it = NamedGarment(sex, slot, c.ModelId) ?? SynthGarment(sex, slot, c.ModelId);
                if (it != null && seen.Add(it.Id)) res.Add(it);
            }
            return res;
        }

        /// <summary>Pure: the worn slot an AVATAR mesh FILENAME names (<c>023425_WOMAN_COAT.MSH</c> → 上衣). 素體臉
        /// (<c>_FACE</c> without <c>_HUAN</c>) is body geometry, not a shop item → None.</summary>
        public static EquipSlot SlotFromMeshFileName(string file)
        {
            if (string.IsNullOrEmpty(file)) return EquipSlot.None;
            string u = file.ToUpperInvariant();
            if (u.Contains("_FACE_HUAN")) return EquipSlot.Expression;   // 表情 (先於 _FACE 判,否則會被吃掉)
            if (u.Contains("_CHIBANG")) return EquipSlot.Wings;
            if (u.Contains("_LINGDANG")) return EquipSlot.Necklace;
            if (u.Contains("_ONE")) return EquipSlot.OnePiece;
            if (u.Contains("_COAT")) return EquipSlot.Top;
            if (u.Contains("_PANT")) return EquipSlot.Bottom;
            if (u.Contains("_HAIR")) return EquipSlot.Hair;
            if (u.Contains("_SHOES")) return EquipSlot.Shoes;
            if (u.Contains("_HAND")) return EquipSlot.Gloves;
            if (u.Contains("_GLASS")) return EquipSlot.Glasses;
            return EquipSlot.None;
        }

        // (性別, 部位, modelId) → 目錄裡有名字的那一列。同一件常有租7/租30/永久多列 → 取目錄第一列 (商城也是這樣列)。
        private Dictionary<(ItemSex, EquipSlot, int), ShopItem> _garmentByModel;
        private ShopItem NamedGarment(ItemSex sex, EquipSlot slot, int modelId)
        {
            if (_garmentByModel == null)
            {
                _garmentByModel = new Dictionary<(ItemSex, EquipSlot, int), ShopItem>();
                foreach (var it in Clothing)
                {
                    if (it.EquipSlot == EquipSlot.Outfit || it.EquipSlot == EquipSlot.None) continue;
                    var key = (ItemTypes.GenderOf(it.Category, it.Name), it.EquipSlot, it.ModelId);
                    if (!_garmentByModel.ContainsKey(key)) _garmentByModel[key] = it;
                }
            }
            return _garmentByModel.TryGetValue((sex, slot, modelId), out var v) ? v : null;
        }

        // 沒有名字的組件 → 合成一列 (序號當名字、100M、永久),並登記進 _synthById 讓 ById 查得到。
        private ShopItem SynthGarment(ItemSex sex, EquipSlot slot, int modelId)
        {
            int cat = CategoryForSynthSlot(sex, slot);
            if (cat == 0) return null;
            int id = SynthId(slot, sex, modelId);
            if (_synthById.TryGetValue(id, out var s)) return s;
            var syn = NewSynth(id, modelId, modelId.ToString("D6"), cat);
            _synthById[id] = syn;
            return syn;
        }

        private string FindComponentMesh(int modelId, string g, string preferredToken = null)
        {
            string id = modelId.ToString("D6");
            // 該組件已知部位 → 直接用它的 token(修:020366 同時有 _COAT 與 _PANT,裙子要拿 PANT 不是先命中的 COAT)。
            if (!string.IsNullOrEmpty(preferredToken))
            {
                string pf = id + "_" + g + "_" + preferredToken + ".MSH";
                if (_meshFiles.Contains(pf)) return pf;
            }
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
            if (rel == null || !_meshFiles.Contains(rel.Substring(rel.LastIndexOf('/') + 1))) return false;
            // 有 mesh 但布料貼圖完全解不到的殘骸 (如 000004:主布料 submesh 的材質名是 GBK 亂碼「未标题-1副本_.dds」=
            // Photoshop 預設檔名的垃圾佔位,磁碟上也沒有自身的 COAT 貼圖) → 只會畫成一坨 fallback 純色 (使用者回報:
            // 服裝 000004 無法顯示)。視為不可渲染而隱藏,正如它已隱藏「沒 mesh」的道具。只檢查帶布料貼圖的 mesh 衣物槽
            // (上衣/下裝/髮型/鞋子/連身);配件/眼鏡/表情維持原本「有 mesh 即可」的規則。
            if (IsClothTextureSlot(it.EquipSlot) && !GarmentClothResolves(it)) return false;
            return true;
        }

        /// <summary>The mesh clothing slots whose garment carries its OWN cloth texture (上衣/下裝/髮型/鞋子/連身). A
        /// mesh in one of these that resolves to NO cloth texture renders as a flat fallback colour, so it is treated as
        /// non-renderable and hidden. Accessories/眼鏡/表情 are excluded (they keep the mesh-only rule).</summary>
        private static bool IsClothTextureSlot(EquipSlot s)
            => s == EquipSlot.Top || s == EquipSlot.Bottom || s == EquipSlot.Hair || s == EquipSlot.Shoes || s == EquipSlot.OnePiece;

        /// <summary>The slots one outfit design ships as interchangeable geometry: 上衣 / 下裝 / 連身. A single modelId's
        /// COAT, PANT and ONE meshes are the SAME design — the coat half of 快乐舞会 002247's 連身裙 is
        /// 002247_WOMAN_COAT.MSH; 野战迷彩中裤 001278's companion 001278_WOMAN_COAT.MSH merely re-uses 性感嘻哈 001277's
        /// coat texture. So a modelId named in ONE of these must not be re-synthesised as a phantom row in ANOTHER.</summary>
        private static bool IsBodyOutfitSlot(EquipSlot s)
            => s == EquipSlot.Top || s == EquipSlot.Bottom || s == EquipSlot.OnePiece;

        /// <summary>Pure: should a mesh-only garment row be synthesised for this (sex, slot, modelId)? NO when the modelId
        /// is already a NAMED garment worn in a body-outfit slot (上衣/下裝/連身) for the same sex — its mesh in a DIFFERENT
        /// body-outfit slot is that same outfit's companion piece, so listing it would show a visual duplicate (使用者回報:
        /// 合成上衣「001278」==「性感嘻哈」、「002247」==「快乐舞会」顯示同一件衣服). 髮型/鞋子/翅膀/表情 keep the plain
        /// "any mesh on disk" rule — they don't share a modelId across a 上↔下 split.</summary>
        public static bool ShouldSynthGarment(ItemSex sex, EquipSlot slot, int modelId, ISet<(ItemSex, int)> namedBodyOutfitIds)
            => !(IsBodyOutfitSlot(slot) && namedBodyOutfitIds != null && namedBodyOutfitIds.Contains((sex, modelId)));

        /// <summary>
        /// Pure: 這個 <c>_SHOES</c> mesh 其實是「下半身 companion 幾何」而不是鞋子嗎？判準 = 它自己的
        /// <c>{id}_{G}_SHOES.dds</c> 與同 id 同性別的 <c>{id}_{G}_{PANT|COAT|ONE}.dds</c> **位元組完全相同**
        /// —— 美術只是把下裝那張貼圖複製一份改名，沒有畫任何鞋子。
        ///
        /// 使用者回報：商城鞋子分頁的「003541」「003542」兩張卡是空的(沒顯示/透明)。這兩個
        /// <c>*_WOMAN_SHOES.MSH</c> 根本不是鞋，是具名下裝「熱舞短褲」(modelId 3541/3542) 的 companion 幾何：
        /// 同一條牛仔短褲+腰帶(186 verts vs PANT 的 140)，材質旗標/UV 都正常，只是長在**臀部**。鞋子卡片的官方取景
        /// 框對準腳踝(<c>ShopScreen.FrameFor(Shoes)</c> pos.y=-60 / scale 6.5)，這塊幾何整個落在鏡頭外 → 卡片全空。
        /// 官方 iteminfo 沒登錄它們(官方商城從來沒這兩張卡)，是我們的 mesh-only 合成把它們當鞋上架的。
        ///
        /// 全語料實測(<c>H:\65_remake_clean\DATA\AVATAR</c>，4170 個 <c>_SHOES.MSH</c>)：只有 7 個鞋子的 id 同時有
        /// 同性別的 PANT/COAT/ONE 貼圖，其中**只有這 2 個**貼圖是位元組相同的；027643(有同 id PANT mesh)、024141、
        /// 024411、015281、026400 的鞋貼圖都是自己畫的真鞋，不受影響。所以這條規則不需要黑名單，也不會誤殺。
        /// </summary>
        public static bool ShoesTextureIsBottomCopy(byte[] shoesDds, byte[] bottomDds)
        {
            if (shoesDds == null || bottomDds == null || shoesDds.Length == 0) return false;
            if (shoesDds.Length != bottomDds.Length) return false;
            for (int i = 0; i < shoesDds.Length; i++) if (shoesDds[i] != bottomDds[i]) return false;
            return true;
        }

        // 與鞋子貼圖比對的「下半身/連身」貼圖 token，依 companion 幾何常見的出處排序。
        private static readonly string[] BottomCompanionTokens = { "PANT", "COAT", "ONE" };
        // "{id6}_{G}" → 這個 SHOES mesh 是不是下裝 companion。整個 session 只算一次(shipped data 的固定性質)。
        private static readonly Dictionary<string, bool> _bottomCompanionShoes = new Dictionary<string, bool>();

        /// <summary>Disk side of <see cref="ShoesTextureIsBottomCopy"/>：這個 (modelId, 性別) 的 <c>_SHOES</c> mesh 是不是
        /// 下裝 companion。先用已建好的貼圖家族集合(<see cref="TexFamilies"/>)當免費的預篩，只有「鞋+下裝貼圖都在」的
        /// 極少數(全語料 7 個)才真的讀檔比對位元組。</summary>
        public static bool IsBottomCompanionShoes(int modelId, string g)
        {
            string stem = modelId.ToString("D6") + "_" + g;
            if (_bottomCompanionShoes.TryGetValue(stem, out var cached)) return cached;
            bool companion = false;
            var fams = TexFamilies();
            if (fams.Contains((stem + "_SHOES").ToUpperInvariant()))
                foreach (var tok in BottomCompanionTokens)
                {
                    if (!fams.Contains((stem + "_" + tok).ToUpperInvariant())) continue;
                    if (DdsBytesEqual(stem + "_SHOES.DDS", stem + "_" + tok + ".DDS")) { companion = true; break; }
                }
            _bottomCompanionShoes[stem] = companion;
            return companion;
        }

        // 兩個 AVATAR 貼圖檔名(同一目錄下)的內容是否相同。找第一個「兩檔都在」的 avatar 目錄比對；讀不到當作不同
        // (寧可讓那件上架,也不要因為 IO 失敗而默默少一件鞋)。
        private static bool DdsBytesEqual(string aName, string bName)
        {
            foreach (var dir in AvatarDirs())
            {
                string a = Path.Combine(dir, aName), b = Path.Combine(dir, bName);
                if (!VfsFile.Exists(a) || !VfsFile.Exists(b)) continue;
                try { return ShoesTextureIsBottomCopy(VfsFile.ReadAllBytes(a), VfsFile.ReadAllBytes(b)); }
                catch { return false; }
            }
            return false;
        }

        private static readonly Dictionary<string, bool> _clothResolveCache = new Dictionary<string, bool>();

        /// <summary>True if this garment's mesh has a resolvable CLOTH texture on disk. Fast path: if any texture of the
        /// item's own family (<c>{id}_{G}_{TOKEN}*</c> .dds/.an) exists, the garment's own-id/variant texture is present
        /// → it renders (no mesh read). Otherwise read the mesh's material names and apply the builder's actual
        /// resolution (<see cref="ClothTextureResolvable"/>): a corrupt row like 000004 (junk material name + no own
        /// texture) fails → hidden. Only the ~0.1% of garments with no own-family texture ever pay the mesh read, and
        /// the result is cached (a stable property of the shipped data).</summary>
        public bool GarmentClothResolves(ShopItem it)
        {
            var rel = it?.MshRelPath;
            if (rel == null) return true;
            if (_clothResolveCache.TryGetValue(rel, out var cached)) return cached;
            bool r = ComputeGarmentClothResolves(rel);
            _clothResolveCache[rel] = r;
            return r;
        }

        private static bool ComputeGarmentClothResolves(string rel)
        {
            string fam = Path.GetFileNameWithoutExtension(rel);              // e.g. "000004_MAN_COAT"
            if (TexFamilies().Contains(fam.ToUpperInvariant())) return true;   // own texture family present → renders
            string path = SdoAvatarBuilder.ResolveAvatarFile(rel);
            if (!VfsFile.Exists(path)) return false;
            string dir = Path.GetDirectoryName(path);
            List<string> names;
            try { names = MshLoader.ReadMaterialNames(VfsFile.ReadAllBytes(path)); }
            catch { return false; }
            return ClothTextureResolvable(names,
                nm => SdoAvatarBuilder.FindDdsPath(dir, nm) != null,
                anName => !string.IsNullOrEmpty(anName) && VfsFile.Exists(Path.Combine(dir, anName + ".an")));
        }

        /// <summary>Pure: does a garment mesh have a resolvable CLOTH texture? True if ANY of its submesh material names
        /// (from <see cref="MshLoader.ReadMaterialNames"/>) is a real garment texture — a non-<c>Basic</c> name that
        /// resolves to a DDS (<paramref name="ddsResolves"/>) OR a <c>_TexAnimEx(NAME)</c> placeholder whose frame list
        /// <c>NAME.an</c> exists (<paramref name="anExists"/>). Shared <c>Basic</c> skin-base names (exposed arms/neck)
        /// and blank/placeholder names don't count. A garment whose ONLY materials are skin-base or unresolvable junk
        /// (使用者:000004「未标题-1副本」) returns false. The own-id fallback the builder also tries is exactly what the
        /// caller's family fast path already covers, so it isn't repeated here.</summary>
        public static bool ClothTextureResolvable(IEnumerable<string> matNames, Func<string, bool> ddsResolves, Func<string, bool> anExists)
        {
            if (matNames == null) return false;
            foreach (var nm in matNames)
            {
                if (string.IsNullOrEmpty(nm)) continue;
                if (nm.IndexOf("Basic", StringComparison.OrdinalIgnoreCase) >= 0) continue;   // 膚色底材 (手臂/脖子),非布料
                if (ddsResolves != null && ddsResolves(nm)) return true;
                if (TexAnimEx.TryParse(nm, out var spec) && anExists != null && anExists(spec.Name)) return true;
            }
            return false;
        }

        // Garment texture "families" present on disk, as UPPERCASE "{id}_{G}_{TOKEN}" keys derived from every AVATAR
        // *.dds / *.an filename (garment tokens only). A family hit means the item's own or a variant/frame texture
        // exists → it renders, so GarmentClothResolves can skip the mesh read. Built once, cached for the session.
        private static HashSet<string> _texFamilies;
        private static readonly System.Text.RegularExpressions.Regex _famRx =
            new System.Text.RegularExpressions.Regex(@"^(\d{6})_(MAN|WOMAN)_(COAT|PANT|HAIR|SHOES|ONE)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        private static HashSet<string> TexFamilies()
        {
            if (_texFamilies != null) return _texFamilies;
            var set = new HashSet<string>();
            foreach (var dir in AvatarDirs())
            {
                try
                {
                    if (!VfsFile.DirectoryExists(dir)) continue;
                    foreach (var pat in new[] { "*.dds", "*.an" })
                        foreach (var f in VfsFile.GetFiles(dir, pat))
                        {
                            var m = _famRx.Match(Path.GetFileNameWithoutExtension(f));
                            if (m.Success) set.Add((m.Groups[1].Value + "_" + m.Groups[2].Value + "_" + m.Groups[3].Value).ToUpperInvariant());
                        }
                }
                catch { }
            }
            _texFamilies = set;
            return set;
        }

        /// <summary>Items of a gender + worn slot (e.g. female hair), in catalog order.</summary>
        public IReadOnlyList<ShopItem> Group(ItemSex sex, EquipSlot slot)
            => _groups.TryGetValue((sex, slot), out var l) ? l : (IReadOnlyList<ShopItem>)Array.Empty<ShopItem>();

        private static int CategoryFor(ItemSex sex, EquipSlot slot)
        {
            bool m = sex == ItemSex.Male;
            switch (slot)
            {
                case EquipSlot.Hair:       return m ? ItemCategory.HairMale   : ItemCategory.HairFemale;    // 髮型 mesh = _HAIR
                case EquipSlot.Top:        return m ? ItemCategory.TopMale    : ItemCategory.TopFemale;     // 上衣 mesh = _COAT
                case EquipSlot.Bottom:     return m ? ItemCategory.BottomMale : ItemCategory.BottomFemale;  // 下裝 mesh = _PANT
                case EquipSlot.Shoes:      return m ? ItemCategory.ShoesMale  : ItemCategory.ShoesFemale;   // 鞋子 mesh = _SHOES
                case EquipSlot.Wings:      return m ? ItemCategory.WingsMale  : ItemCategory.WingsFemale;
                case EquipSlot.Expression: return m ? ItemCategory.FaceMale   : ItemCategory.FaceFemale;    // 表情 mesh = _FACE_HUAN
                default: return 0;
            }
        }

        // Synthesised mesh-only rows get an Id in a high range that can't collide with a real iteminfo Id, so ById can
        // resolve an equipped synth item back to its ShopItem (→ its mesh) and it actually wears (user: 無名翅膀穿不起來).
        // Synth Id layout: SynthIdBase + mult*SynthSlotStride + modelId.
        //   • mult 0        = 附件 (翅膀/表情/项链) — bare, gender-agnostic modelId; kept exactly so ids already saved
        //                     in profiles keep resolving (SynthesizeAccessory probes MAN then WOMAN).
        //   • mult ≥ 2      = a specific 衣物 slot+gender (髮型/上衣/下裝/鞋子): mult = slotCode(1..4)*2 + genderBit
        //                     (0=男/1=女). Baking slot AND gender in disambiguates the ~16 modelIds shared by two slots
        //                     (e.g. 001277 女 both COAT & PANT) and the ~22 shared by both genders → a saved synth
        //                     garment reloads into the RIGHT slot/gender, not whichever mesh is probed first.
        public const int SynthIdBase = 90_000_000;
        private const int SynthSlotStride = 1_000_000;   // modelIds are 6-digit (< 1e6), so mult never overlaps modelId
        private readonly Dictionary<int, ShopItem> _synthById = new Dictionary<int, ShopItem>();
        private readonly Dictionary<(ItemSex, EquipSlot), List<ShopItem>> _meshModelCache = new Dictionary<(ItemSex, EquipSlot), List<ShopItem>>();

        // (sex, modelId) of every NAMED garment worn in a body-outfit slot (上衣/下裝/連身). A modelId here must not be
        // synthesised as a mesh-only row in a DIFFERENT body-outfit slot (see ShouldSynthGarment). Built once, cached.
        private HashSet<(ItemSex, int)> _namedBodyOutfitIds;
        private HashSet<(ItemSex, int)> NamedBodyOutfitIds()
        {
            if (_namedBodyOutfitIds != null) return _namedBodyOutfitIds;
            var s = new HashSet<(ItemSex, int)>();
            foreach (var it in Clothing)
                if (IsBodyOutfitSlot(it.EquipSlot))
                    s.Add((ItemTypes.GenderOf(it.Category, it.Name), it.ModelId));   // 與 groups/AllMeshModels 同一套性別判定
            _namedBodyOutfitIds = s;
            return s;
        }

        // Synth-Id slot code (1..7) for the 衣物 slots that synthesise mesh-only rows; 0 = 附件 (bare id, legacy).
        // 5..7 (手套/眼鏡/連身) 只有「套装拆件」用得到 (那些部位的商城分頁不合成 mesh-only 列),是純新增的
        // 編碼區段 (舊存檔只會出現 0..4),所以不影響任何既有 id 的解回。
        private static int SynthSlotCode(EquipSlot slot)
        {
            switch (slot)
            {
                case EquipSlot.Hair:     return 1;
                case EquipSlot.Top:      return 2;
                case EquipSlot.Bottom:   return 3;
                case EquipSlot.Shoes:    return 4;
                case EquipSlot.Gloves:   return 5;
                case EquipSlot.Glasses:  return 6;
                case EquipSlot.OnePiece: return 7;
                default:                 return 0;   // 翅膀/表情/项链 → bare modelId
            }
        }

        // Inverse of SynthSlotCode: the 衣物 slot a synth code names (None for code 0 = 附件 / an unused code).
        private static EquipSlot SlotFromSynthCode(int code)
        {
            switch (code)
            {
                case 1: return EquipSlot.Hair;
                case 2: return EquipSlot.Top;
                case 3: return EquipSlot.Bottom;
                case 4: return EquipSlot.Shoes;
                case 5: return EquipSlot.Gloves;
                case 6: return EquipSlot.Glasses;
                case 7: return EquipSlot.OnePiece;
                default: return EquipSlot.None;
            }
        }

        /// <summary>Category for a SYNTH row's slot — <see cref="CategoryFor"/> plus the three slots only 套装 拆件 ever
        /// synthesises (手套/眼鏡/連身). Deliberately separate: <see cref="CategoryFor"/> also drives
        /// <see cref="AllMeshModels"/>, and widening THAT would put every mesh-only glove/眼鏡/連身 on the shop shelf.</summary>
        private static int CategoryForSynthSlot(ItemSex sex, EquipSlot slot)
        {
            bool m = sex == ItemSex.Male;
            switch (slot)
            {
                case EquipSlot.Gloves:   return m ? ItemCategory.GlovesMale   : ItemCategory.GlovesFemale;    // _HAND
                case EquipSlot.Glasses:  return m ? ItemCategory.GlassesMale  : ItemCategory.GlassesFemale;   // _GLASS
                case EquipSlot.OnePiece: return m ? ItemCategory.OnePieceMale : ItemCategory.OnePieceFemale;  // _ONE
                case EquipSlot.Necklace: return m ? ItemCategory.NecklaceMale : ItemCategory.NecklaceFemale;  // _LINGDANG
                default: return CategoryFor(sex, slot);
            }
        }

        // 素體內建 default body parts/clothes (預設臉 900007、預設 髮/上衣/下著/鞋 900001..900020) 佔 900xxx 一段 →
        // 不上架 (user: 9xxxxx系列預設衣服的不要上架)。但 999xxx 是 patch Datas 後補的活動/特殊服裝 (999901..999949,mesh-only,
        // 借 035855..035858 / 023546 家族貼圖),要上架 (user: 9999xx 系列沒出現 → 上架)。磁碟上 9xxxxx 就只有這兩段
        // (900xxx 預設 20 個 / 999xxx patch 49 個),中間無檔 → 排除 900xxx、放行 999xxx。
        public const int DefaultModelIdBase = 900_000;   // 素體預設起點 (預設只佔 900xxx)
        public const int PatchModelIdBase   = 999_000;   // 999xxx = 後補活動/特殊服裝 → 允許上架

        /// <summary>True if a 6-digit model serial is a real buyable shop model: positive, and either below the 素體
        /// 預設 band (&lt; 900000) or in the 999xxx patch band (≥ 999000). The 900xxx 素體 defaults stay off the shelf.</summary>
        public static bool IsShopModelId(int modelId)
            => modelId > 0 && (modelId < DefaultModelIdBase || modelId >= PatchModelIdBase);

        // 官方 CN iteminfo 快取殘留的伺服器端測試列 —「Bug Item」(id 5002133, modelId 2133, cat 109 女项链):名字是當年
        // 伺服器 push 進快取的除錯佔位,不是真商品 (使用者:把叫 Bug Item 的項鍊刪掉)。上架前整列剔除;项链不是合成 slot
        // (CategoryFor→0),modelId 2133 不會被 AllMeshModels 用序號名反手撿回。cat 14000 的 Test Pack 1/2 等測試列非服裝,
        // 本就進不了目錄,不必列。
        private static readonly HashSet<int> PlaceholderItemIds = new HashSet<int> { 5002133 };

        /// <summary>True for a known server-side placeholder row (test data left in the official iteminfo cache,
        /// e.g. 「Bug Item」 5002133) — dropped from the catalog entirely.</summary>
        public static bool IsPlaceholderItem(int id) => PlaceholderItemIds.Contains(id);

        // 使用者要求:M 幣 (Coins / priceCategory 1) 定價超過 5000 的衣服一律壓到 5000;其他幣別 (G=Points / H=Bonus) 不動。
        public const int MaxCoinPrice = 5000;

        /// <summary>Pure: cap a price at <see cref="MaxCoinPrice"/> when it is charged in M 幣 (Coins); a price in any
        /// other wallet (G/H) is returned unchanged (user: 超過 5000M 的衣服都改成 5000).</summary>
        public static int CapCoinPrice(int price, int priceCategoryRaw)
            => (priceCategoryRaw == (int)ItemPriceCurrency.Coins && price > MaxCoinPrice) ? MaxCoinPrice : price;

        /// <summary>Encode a synth row's Id from its slot + gender + 6-digit model serial (see the SynthIdBase layout
        /// note). 附件 slots collapse to the legacy bare <c>SynthIdBase + modelId</c>; 衣物 slots bake slot+gender in.</summary>
        public static int SynthId(EquipSlot slot, ItemSex sex, int modelId)
        {
            int code = SynthSlotCode(slot);
            int mult = code == 0 ? 0 : code * 2 + (sex == ItemSex.Male ? 0 : 1);
            return SynthIdBase + mult * SynthSlotStride + modelId;
        }

        // The mesh-backed accessory slots a synth id can belong to: the AVATAR/*.MSH filename token + its per-gender
        // category. Used to rebuild a synth ShopItem straight from its bare model id (see SynthesizeAccessory).
        private struct SynthSlot { public string Token; public int CatMale, CatFemale; }
        private static readonly SynthSlot[] SynthAccessorySlots =
        {
            new SynthSlot { Token = "CHIBANG",   CatMale = ItemCategory.WingsMale,    CatFemale = ItemCategory.WingsFemale },     // 翅膀
            new SynthSlot { Token = "FACE_HUAN", CatMale = ItemCategory.FaceMale,     CatFemale = ItemCategory.FaceFemale },      // 表情
            new SynthSlot { Token = "LINGDANG",  CatMale = ItemCategory.NecklaceMale, CatFemale = ItemCategory.NecklaceFemale },  // 项链
        };

        /// <summary>Pure: rebuild a synthesised mesh-only accessory <see cref="ShopItem"/> from its bare synth id
        /// (<paramref name="id"/> ≥ <see cref="SynthIdBase"/>) by probing <paramref name="meshExists"/> (an AVATAR
        /// filename predicate) for a wing/表情/项链 mesh of that model id. Deliberately does NOT apply the shop's
        /// expression-quality gate: an item the player already OWNS/WEARS must always resolve back to its mesh. Returns
        /// null if no accessory mesh matches. Prefers the MAN mesh when a model id exists for both genders (rare).</summary>
        public static ShopItem SynthesizeAccessory(int id, Func<string, bool> meshExists)
        {
            if (id < SynthIdBase || meshExists == null) return null;
            int modelId = id - SynthIdBase;
            if (modelId <= 0) return null;
            string id6 = modelId.ToString("D6");
            foreach (var s in SynthAccessorySlots)
            {
                if (meshExists(id6 + "_MAN_" + s.Token + ".MSH"))   return NewSynth(id, modelId, id6, s.CatMale);
                if (meshExists(id6 + "_WOMAN_" + s.Token + ".MSH")) return NewSynth(id, modelId, id6, s.CatFemale);
            }
            return null;
        }

        /// <summary>Pure: rebuild a synthesised mesh-only <see cref="ShopItem"/> from its synth id — the general entry
        /// point used by <see cref="ById"/>. Decodes the slot (and, for 衣物, the gender) from the id (see the SynthIdBase
        /// layout note): a 附件 id (mult 0) is resolved by <see cref="SynthesizeAccessory"/> (probes 翅膀/表情/项链 tokens,
        /// MAN then WOMAN); a 衣物 id (mult ≥ 2) resolves straight to its known slot+gender mesh, so a modelId shared
        /// across slots or genders never resolves into the wrong one. Returns null if that mesh isn't on disk
        /// (<paramref name="meshExists"/>).</summary>
        public static ShopItem SynthesizeSynthItem(int id, Func<string, bool> meshExists)
        {
            if (id < SynthIdBase || meshExists == null) return null;
            int rel = id - SynthIdBase;
            int mult = rel / SynthSlotStride;
            if (mult == 0) return SynthesizeAccessory(id, meshExists);   // legacy bare-id 附件 (翅膀/表情/项链)
            var slot = SlotFromSynthCode(mult / 2);                      // mult = code*2 + genderBit
            if (slot == EquipSlot.None) return null;
            int modelId = rel % SynthSlotStride;
            if (modelId <= 0) return null;
            var sex = (mult % 2) == 0 ? ItemSex.Male : ItemSex.Female;
            int cat = CategoryForSynthSlot(sex, slot);
            string token = ItemTypes.MshSlotSuffix(cat);
            if (token == null) return null;
            string id6 = modelId.ToString("D6");
            string mesh = id6 + "_" + (sex == ItemSex.Male ? "MAN" : "WOMAN") + "_" + token + ".MSH";
            return meshExists(mesh) ? NewSynth(id, modelId, id6, cat) : null;
        }

        private static ShopItem NewSynth(int id, int modelId, string id6, int category) => new ShopItem
        {
            Id = id, Name = SynthName(category, modelId), Price = 100, PriceCategoryRaw = 1, ModelId = modelId, Category = category,
            MinLevel = 1, DurationDays = -1, Quantity = -1, SexRaw = ItemTypes.SexFromCategory(category) == ItemSex.Male ? 1 : 0,
        };

        /// <summary>Every model for a mesh-backed slot (翅膀/表情/髮型/上衣/下裝/鞋子 — see <see cref="CategoryFor"/>):
        /// the named iteminfo rows PLUS every extra model that ONLY exists as a mesh on disk — synthesised as a row named
        /// by its 6-digit serial, permanent, 100M (user: 無名的也加上去/用序號當名字) — so the shop can browse the full set.
        /// iteminfo lists only a fraction of the meshes shipped (e.g. ~100 of ~1000 wings, and likewise for hair/tops/
        /// bottoms/shoes); the user wants them all. Named rows first (keep their names/prices), then the mesh-only extras
        /// sorted by modelId. Cached + registered in <see cref="ById"/> so a tried-on synth row wears + persists.</summary>
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
            bool isShoes = slot == EquipSlot.Shoes;
            var have = new HashSet<int>();
            foreach (var it in named) have.Add(it.ModelId);
            var extra = new List<ShopItem>();
            foreach (var f in _meshFiles)
            {
                if (!f.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                int us = f.IndexOf('_');
                if (us <= 0 || !int.TryParse(f.Substring(0, us), out var modelId)) continue;
                if (!IsShopModelId(modelId)) continue;                        // 9xxxxx 素體預設衣服不上架 (user)
                if (!have.Add(modelId)) continue;
                if (!ShouldSynthGarment(sex, slot, modelId, NamedBodyOutfitIds())) continue;   // 跨部位同一套 → 不重複合成 (使用者:001278/002247)
                if (isExpr && !IsGoodExpressionModel(modelId, g)) continue;   // 濾掉會渲染成 空白/破圖/假臉 的異常表情 (user)
                if (isShoes && IsBottomCompanionShoes(modelId, g)) continue;  // 不是鞋:下裝 companion 幾何 → 卡片全空 (使用者:003541/003542)
                var syn = new ShopItem
                {
                    Id = SynthId(slot, sex, modelId), Name = SynthName(cat, modelId), Price = 100, PriceCategoryRaw = 1,   // 1=Coins→M 幣 (100M；user:無名 翅膀/髮型/上衣/下裝/鞋子 改100M/序號當名字); 名字優先用 TW 繁體, 無則 6 位序號
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
                if (!VfsFile.Exists(mshPath)) continue;
                // ① 必須有自己的最白膚色臉貼圖 (ForceLightExpressionFace 要用的那張)，否則整臉空白
                if (!VfsFile.Exists(Path.Combine(dir, id6 + "_" + g + "_FACE_HUAN0.DDS")) &&
                    !VfsFile.Exists(Path.Combine(dir, id6 + "_" + g + "_FACE_HUAN.DDS"))) return false;
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
                var s = System.Text.Encoding.ASCII.GetString(VfsFile.ReadAllBytes(mshPath)).ToLowerInvariant();
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
            if (id >= SynthIdBase)
            {
                // A synth 翅膀/表情/项链 OR a synth 髮型/上衣/下裝/鞋子. It only landed in _synthById if the shop browsed
                // that slot this session; an OWNED/EQUIPPED synth row reloaded from profile.json is looked up long before
                // that, so rebuild it from its mesh on demand (and cache). Without this, ById→null makes the 儲物櫃 hide
                // owned synth items AND a wardrobe save re-resolves equippedParts without them → they vanish until the
                // shop tab is reopened.
                if (_synthById.TryGetValue(id, out var syn)) return syn;
                syn = SynthesizeSynthItem(id, f => _meshFiles.Contains(f));
                if (syn != null) _synthById[id] = syn;
                return syn;
            }
            if (_byId == null)
            {
                _byId = new Dictionary<int, ShopItem>(Clothing.Count + Props.Count);
                foreach (var it in Clothing) _byId[it.Id] = it;
                foreach (var it in Props) _byId[it.Id] = it;   // 買下的 2D 道具重載存檔時也要查得到 (背包/數量)
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
                if (path != null) items = IteminfoReader.Parse(VfsFile.ReadAllBytes(path), TryGetGbk());
                else Debug.LogWarning("[shop] iteminfo.dat not found — catalog is empty");
            }
            catch (Exception e) { Debug.LogWarning("[shop] iteminfo load failed: " + e.Message); }

            // Overlay the build-time UTF-8 name sidecar. iteminfo.dat names are GBK/CP936; a standalone build has no
            // such codepage, so tools/package_build.ps1 bakes shop_names.tsv (id→UTF-8 name) and we apply it here —
            // the player then needs no encoding at all. No-op in the editor (GBK works there) or when the file is absent.
            ApplyNameSidecar(items);

            // Overlay the Traditional-Chinese (TW 櫻式搖滾) names on top: fills in a real name for many otherwise-
            // unnamed mesh-only rows AND replaces the CN Simplified name with the official Traditional one where the TW
            // client has it. Loaded into the static _twNames map here so it's ready before any synth-row naming below
            // (see SynthName), and applied last so a TW name wins over both the GBK parse and shop_names.tsv.
            LoadTwNames();
            ApplyTwNames(items);

            var sets = new Dictionary<int, OutfitSet>();
            try
            {
                var sp = ResolveSetinfoPath();
                if (sp != null) sets = SetinfoReader.Parse(VfsFile.ReadAllBytes(sp), TryGetGbk());
            }
            catch (Exception e) { Debug.LogWarning("[shop] setinfo load failed: " + e.Message); }

            // 同一件衣服常常上架兩次:陸版中文列 + 線上版英文重上架 (同 cat+modelId → 同 mesh/貼圖 = 看起來一模一樣,
            // 如 24976「金姬兰 女装」與「Flower Lace Dress」)。使用者:「把這種視覺完全一樣的衣服只留一組,留中文的」。
            var hidden = DuplicateListingIds(items);

            var clothing = new List<ShopItem>();
            var groups = new Dictionary<(ItemSex, EquipSlot), List<ShopItem>>();
            var props = new List<ShopItem>();
            var propGroups = new Dictionary<EquipSlot, List<ShopItem>>();
            foreach (var it in items)
            {
                var slot = it.EquipSlot;
                if (slot == EquipSlot.None) continue;
                if (ItemTypes.IsProp(slot))
                {
                    // 非衣服的 2D 商品 (道具/藥水/特效/寵物/禮包) —— 以前這裡直接 continue 丟掉，所以 道具店/伙伴店/礼包店
                    // 全是空的。現在全收進 props (ById 查得到);分頁清單 propGroups 稍後去重 (中英重複) 再建。
                    props.Add(it);
                    continue;
                }
                if (IsPlaceholderItem(it.Id)) continue;              // 伺服器測試佔位列 (「Bug Item」) 不上架
                clothing.Add(it);   // 重複列仍留在 Clothing → ById 查得到 (已擁有/已穿的英文 id 不會失效)
                if (hidden.Contains(it.Id)) continue;                // …但不進商城分頁清單
                var key = (ItemTypes.GenderOf(it.Category, it.Name), slot);   // GenderOf 修 cat203 套装 (sex byte 皆0,靠名字)
                if (!groups.TryGetValue(key, out var l)) groups[key] = l = new List<ShopItem>();
                l.Add(it);
            }
            if (hidden.Count > 0) Debug.Log($"[shop] {hidden.Count} 件英文重複上架列隱藏 (同 model 已有中文列)");
            // 非衣服 2D 商品:同 modelId+同 SKU 常有「中文列 + 英文重上架列」兩筆 (奇妙冰激凌 / Ice Cream,model 1120005)。
            // 使用者:「道具店/礼包店只拿中文的」→ 每個 SKU 有中文名列就藏掉英文/無中文列後才建分頁清單 (props 仍全保留供 ById)。
            var propHidden = PropDuplicateListingIds(props);
            int propNoArt = 0;
            foreach (var it in props)
            {
                if (propHidden.Contains(it.Id)) continue;
                // 無 2D 圖示 (DRESS/FindByPrefix) 又無 3D 禮盒 mesh (DAOJU) 的道具 = 官方沒美術的佔位/測試列 (Test Pack、
                // VIP PERMANENT、laim…,modelId 超出官方區間、原價 2000000) → 不上架,免得礼包店/道具店整頁空卡。
                if (DressCatalog.IconPath(it.ModelId) == null && DressCatalog.MeshRel(it.ModelId) == null) { propNoArt++; continue; }
                var pslot = it.EquipSlot;
                if (!propGroups.TryGetValue(pslot, out var pl)) propGroups[pslot] = pl = new List<ShopItem>();
                pl.Add(it);
            }
            if (propHidden.Count > 0) Debug.Log($"[shop] {propHidden.Count} 筆非衣服商品英文重複列隱藏 (同 SKU 已有中文列)");
            if (propNoArt > 0) Debug.Log($"[shop] {propNoArt} 筆道具無美術 (無 2D 圖也無 3D mesh) → 不上架");
            // 離線無 setinfo → 依「系列基底名」把同名多件衣物合成套裝,放進 套装 分頁(使用者要求:兔乖乖/璀璨繁星…)。
            int synth = BuildSyntheticSets(clothing, groups, sets);
            // 台版官方套装 (古惑仔/卡卡西/逍遙英雄/聖誕老公公…): 名字來自台版 iteminfo 的 Outfit 列、組件來自台版 setinfo,
            // 由 tools/build_shop_names_tw.py 併成 shop_sets_tw.tsv。CN 與 TW 的 setId 各自重編號、意義不同 → 當「新套装」
            // 加入 (重映射 id 避免撞 CN),不覆蓋。渲染由 shop 的 IsRenderable 過濾 (組件 mesh 齊的才顯示)。
            int twSets = AddTwSets(clothing, groups, sets);
            // 使用者:M 幣 (Coins) 定價 > 5000 的衣服壓到 5000 (G/H 幣不動)。clothing 與 groups 共用同一批 ShopItem 物件,
            // 改這裡的 Price → 顯示/購買/買齊/套装 全部吃到。合成 mes-only 列 (AllMeshModels, 100M) 本就 < 5000,不受影響。
            int capped = 0;
            foreach (var it in clothing) { int p = CapCoinPrice(it.Price, it.PriceCategoryRaw); if (p != it.Price) { it.Price = p; capped++; } }
            // 非衣服的 2D 商品 (道具/藥水/特效/寵物/禮包) 一樣壓 M 幣定價上限 —— 它們早在上面就分流進 props,沒被上面那圈
            // 掃到 (使用者回報「超過5000的沒套用5000M」= 禮包/特效卡那些 2000000M 沒被壓)。共用同一批 ShopItem 物件 → 改到底。
            foreach (var it in props) { int p = CapCoinPrice(it.Price, it.PriceCategoryRaw); if (p != it.Price) { it.Price = p; capped++; } }
            Debug.Log($"[shop] catalog: {clothing.Count} clothing items, {groups.Count} groups, {meshFiles.Count} meshes, {sets.Count} sets (+{synth} 合成 +{twSets} 台版, {capped} 件 M-幣 定價壓到 {MaxCoinPrice}), "
                      + $"{props.Count} 2D 商品 ({propGroups.Count} 頁)");
            return new AvatarItemCatalog(clothing, groups, meshFiles, sets, props, propGroups);
        }

        /// <summary>
        /// Pure: the ids of rows the shop should NOT list because they are a re-listing of a garment that is already
        /// there under a Chinese name. The same (Category, ModelId) means the same mesh + textures = literally the same
        /// clothes on screen; the client data ships thousands of them because the international/online service
        /// re-listed the CN catalogue with English names (7,474 such rows over 6,997 models, all priced 2,000,000):
        /// 24976 is 「金姬兰 女装」 AND "Flower Lace Dress"; 12657 is 「浪漫水粉 长靴」 AND "Kawaii Pink Boots".
        /// 使用者:「把這種視覺完全一樣的衣服只留一組,留中文的」.
        ///
        /// Rule: inside one (Category, ModelId) group, if ANY row has a CJK name, every row whose name has NO CJK
        /// character (English re-listings, and the numeric placeholder names we synthesise for unnamed rows) is hidden.
        /// Groups that are English-only (7,794 of them — items that never existed in the CN catalogue) keep every row,
        /// and duration tiers (7天/30天/永久, all Chinese) are untouched: they are genuinely different offers.
        /// Hidden rows stay in <see cref="Clothing"/> so <see cref="ById"/> still resolves an owned/equipped English id.
        /// </summary>
        /// <summary>
        /// Mascot COSTUME sets the user asked to pull from the shop (使用者:「panda suit f, ultraman top, chick suit f
        /// 這系列的衣服都拿掉」). They are one contiguous family in the online catalogue — a full-body animal/hero suit
        /// plus its matching hair, gloves and shoes — and they are listed here by MODEL id so every duration tier and
        /// both genders go at once:
        ///   963-966 Ultraman (hair / top / gloves / shoes), 967-973 Panda (hair M+F, suit M+F, gloves M+F),
        ///   979-984 Chicky (Yellow Chicky hair M+F, suit M+F, shoes M+F), 15435/15436 Chicky hair.
        /// Hidden from the shop listing only — the rows stay in <see cref="Clothing"/> so an already-owned id resolves.
        /// </summary>
        public static readonly HashSet<int> RemovedSeriesModelIds = new HashSet<int>
        {
            963, 964, 965, 966,                       // Ultraman F
            967, 968, 969, 971, 972, 973,             // Panda M/F
            979, 980, 981, 982, 983, 984,             // Chicky M/F
            15435, 15436,                             // Chicky hair M/F
        };

        public static HashSet<int> DuplicateListingIds(IEnumerable<ShopItem> items)
        {
            var hidden = new HashSet<int>();
            if (items == null) return hidden;
            foreach (var it in items)
                if (it != null && RemovedSeriesModelIds.Contains(it.ModelId)) hidden.Add(it.Id);
            var byModel = new Dictionary<(int, int), List<ShopItem>>();
            foreach (var it in items)
            {
                if (it == null) continue;
                var key = (it.Category, it.ModelId);
                if (!byModel.TryGetValue(key, out var l)) byModel[key] = l = new List<ShopItem>();
                l.Add(it);
            }
            foreach (var kv in byModel)
            {
                var rows = kv.Value;
                if (rows.Count < 2) continue;
                bool anyChinese = false;
                foreach (var r in rows) if (HasCjk(r.Name)) { anyChinese = true; break; }
                if (anyChinese)
                    foreach (var r in rows) if (!HasCjk(r.Name)) hidden.Add(r.Id);   // English/numeric twin of a Chinese row
                // …and collapse rows that survived but are indistinguishable to the player: same worn model, same
                // rental period, same displayed name. That happens when the Traditional-name sidecar (keyed by
                // category+modelId, so it renames the English re-listing too) makes both rows read identically, and for
                // the 128 CN rows the data itself duplicates. Keep the lowest id — the original CN listing.
                var bestByLabel = new Dictionary<(int, string), ShopItem>();
                foreach (var r in rows)
                {
                    if (hidden.Contains(r.Id)) continue;
                    var label = (r.DurationDays, r.Name ?? "");
                    if (!bestByLabel.TryGetValue(label, out var keep)) { bestByLabel[label] = r; continue; }
                    if (r.Id < keep.Id) { hidden.Add(keep.Id); bestByLabel[label] = r; }
                    else hidden.Add(r.Id);
                }
            }
            return hidden;
        }

        /// <summary>Pure: ids of NON-衣服 2D 商品 rows to hide from the shop pages because they are an English/無中文
        /// re-listing of a Chinese row for the SAME SKU. Unlike <see cref="DuplicateListingIds"/> (keyed by
        /// Category+ModelId, which would fold the ×1/×50/×100 SKUs of one item together since they share a name), this
        /// keys by SKU MINUS price (ModelId+Quantity+Duration+幣別) so distinct SKUs (×1/×50) survive while the CN/EN
        /// twins of one SKU — which carry DIFFERENT prices (奇妙冰激凌 500M vs the Ice Cream re-listing's 2,000,000) —
        /// still group and collapse to the Chinese-named row. Hidden rows stay in <see cref="Props"/> so ById resolves.</summary>
        public static HashSet<int> PropDuplicateListingIds(IEnumerable<ShopItem> props)
        {
            var hidden = new HashSet<int>();
            if (props == null) return hidden;
            // SKU 鍵**不含價格**:中文原版與英文重上架版價格常不同 (奇妙冰激凌 500M vs Ice Cream 原價 2000000),
            // 含價格會把它們當成兩個 SKU 而漏掉去重。同 modelId+數量+時效+幣別 就視為同一件的中/英兩版。
            var bySku = new Dictionary<(int, int, int, int), List<ShopItem>>();
            foreach (var it in props)
            {
                if (it == null) continue;
                var key = (it.ModelId, it.Quantity, it.DurationDays, it.PriceCategoryRaw);
                if (!bySku.TryGetValue(key, out var l)) bySku[key] = l = new List<ShopItem>();
                l.Add(it);
            }
            foreach (var kv in bySku)
            {
                var rows = kv.Value;
                if (rows.Count < 2) continue;
                bool anyCjk = false;
                foreach (var r in rows) if (HasCjk(r.Name)) { anyCjk = true; break; }
                if (!anyCjk) continue;   // 同 SKU 全無中文 → 沒有中文可留就整組保留 (別誤刪只有英文名的商品)
                foreach (var r in rows) if (!HasCjk(r.Name)) hidden.Add(r.Id);   // 有中文列 → 藏英文/無中文重複列
            }
            return hidden;
        }

        /// <summary>True when the name contains a CJK ideograph — i.e. it is the Chinese listing (Simplified from the CN
        /// iteminfo or Traditional from the TW sidecar) rather than an English re-listing / numeric placeholder.</summary>
        public static bool HasCjk(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var c in name)
                if ((c >= '一' && c <= '鿿') || (c >= '㐀' && c <= '䶿')) return true;
            return false;
        }

        private const int SynthSetIdBase = 80_000_000;   // 8xxxxxxx:高於 6 位 modelId、低於 SynthIdBase(9xxxxxxx),不撞

        /// <summary>Offline has no setinfo.dat, so synthesise 套装 from item NAMES: group garments whose name shares a
        /// "series" base (name minus its trailing 部位/性別 word — 「兔乖乖 女帽」→「兔乖乖」) by (base,gender); any group
        /// covering ≥2 distinct worn slots becomes one outfit set (one item per slot) shown as a dressed mannequin in the
        /// 套装 tab. Reuses the existing OutfitSet/OutfitComponentMeshes render path. Returns the number of sets made.</summary>
        private static int BuildSyntheticSets(List<ShopItem> clothing,
            Dictionary<(ItemSex, EquipSlot), List<ShopItem>> groups, Dictionary<int, OutfitSet> sets)
        {
            var byName = new Dictionary<(string, ItemSex), Dictionary<EquipSlot, ShopItem>>();
            foreach (var it in clothing)
            {
                var slot = it.EquipSlot;
                if (slot == EquipSlot.Outfit || slot == EquipSlot.Expression || slot == EquipSlot.None) continue;   // 表情非穿搭
                string bn = SeriesBaseName(it.Name);
                if (bn == null) continue;
                var key = (bn, ItemTypes.GenderOf(it.Category, it.Name));
                if (!byName.TryGetValue(key, out var bySlot)) byName[key] = bySlot = new Dictionary<EquipSlot, ShopItem>();
                if (!bySlot.ContainsKey(slot)) bySlot[slot] = it;   // 一個部位第一件
            }
            int made = 0, nextId = SynthSetIdBase;
            foreach (var kv in byName)
            {
                if (kv.Value.Count < 2) continue;   // 至少 2 個不同部位才算一套
                if (!kv.Value.ContainsKey(EquipSlot.Top) && !kv.Value.ContainsKey(EquipSlot.OnePiece)) continue;   // 套裝至少要有上衣(使用者要求),沒上衣的丟掉
                var (bn, gender) = kv.Key;
                int setId = nextId++;
                var set = new OutfitSet { SetId = setId };
                int price = 0;
                foreach (var comp in kv.Value.Values)
                {
                    set.Components.Add(new OutfitComponent { ModelId = comp.ModelId, Flag = -1, Token = ItemTypes.MshSlotSuffix(comp.Category) });
                    if (comp.Price > 0) price += comp.Price;
                }
                sets[setId] = set;
                var outfit = new ShopItem
                {
                    Id = setId, Name = bn, ModelId = setId,
                    Category = gender == ItemSex.Male ? ItemCategory.OutfitMale : ItemCategory.OutfitFemale,
                    Price = price > 0 ? price : 100, PriceCategoryRaw = 1, MinLevel = 1, DurationDays = -1, Quantity = -1,
                };
                clothing.Add(outfit);
                var okey = (gender, EquipSlot.Outfit);
                if (!groups.TryGetValue(okey, out var l)) groups[okey] = l = new List<ShopItem>();
                l.Add(outfit);
                made++;
            }
            return made;
        }

        // 台版套装 id base:把 TW setId 重映射成 TwSetIdBase+setId,避開 CN setId(≤ 850004)、合成套装(SynthSetIdBase 80M
        // 起小幅遞增)、synth 道具(SynthIdBase 90M) 與 6 位 modelId。落在 [85M, 85.85M],高於合成套装、低於 90M,
        // 故 ById 對 < 90M 的 id 仍走 _byId(clothing 內)找得到 → 套装穿得起來/存得住。
        private const int TwSetIdBase = 85_000_000;

        /// <summary>Register the台版 official outfit sets from <c>shop_sets_tw.tsv</c> as extra 套装 rows. Each set's name
        /// (from the TW iteminfo Outfit row) + component model ids (from the TW setinfo) are self-contained in the
        /// sidecar; we add an <see cref="OutfitSet"/> + a linked outfit <see cref="ShopItem"/> under a remapped id (see
        /// <see cref="TwSetIdBase"/>) so it never collides with a CN setId (CN/TW renumber sets independently). Skips a
        /// def whose exact component signature already exists (no duplicate of a CN/synthetic set) or whose remapped id
        /// is taken. Renderability is left to the shop's <see cref="IsRenderable"/> filter, exactly like the CN sets.
        /// Returns the number of sets added.</summary>
        private static int AddTwSets(List<ShopItem> clothing,
            Dictionary<(ItemSex, EquipSlot), List<ShopItem>> groups, Dictionary<int, OutfitSet> sets)
        {
            var path = ResolveDataFile(ShopSetTwSidecar.FileName);
            if (path == null) return 0;
            List<TwSetDef> defs;
            try { defs = ShopSetTwSidecar.Parse(VfsFile.ReadAllText(path, Encoding.UTF8)); }
            catch (Exception e) { Debug.LogWarning("[shop] " + ShopSetTwSidecar.FileName + " read failed: " + e.Message); return 0; }
            if (defs.Count == 0) return 0;

            // Dedup only against VISIBLE outfits (CN outfit rows + synthetic sets already in the 套装 groups) — not every
            // entry in `sets` (a CN setinfo record with no naming iteminfo row isn't shown, so it must not hide a TW set).
            var seen = new HashSet<string>();
            foreach (var it in clothing)
                if (it.EquipSlot == EquipSlot.Outfit && sets.TryGetValue(it.ModelId, out var os))
                    seen.Add(SetSignature(os.Components));

            int made = 0;
            foreach (var d in defs)
            {
                if (d.Components == null || d.Components.Length == 0 || string.IsNullOrEmpty(d.Name)) continue;
                int id = TwSetIdBase + d.SetId;
                if (sets.ContainsKey(id)) continue;

                var set = new OutfitSet { SetId = id };
                foreach (var mid in d.Components) set.Components.Add(new OutfitComponent { ModelId = mid, Flag = -1 });   // Token null → probe by token order
                if (!seen.Add(SetSignature(set.Components))) continue;   // identical to an existing CN/synthetic set

                sets[id] = set;
                var gender = d.Male ? ItemSex.Male : ItemSex.Female;
                var outfit = new ShopItem
                {
                    Id = id, Name = d.Name, ModelId = id,
                    Category = d.Male ? ItemCategory.OutfitMale : ItemCategory.OutfitFemale,
                    Price = 100, PriceCategoryRaw = 1, MinLevel = 1, DurationDays = -1, Quantity = -1,
                };
                clothing.Add(outfit);
                var okey = (gender, EquipSlot.Outfit);
                if (!groups.TryGetValue(okey, out var l)) groups[okey] = l = new List<ShopItem>();
                l.Add(outfit);
                made++;
            }
            return made;
        }

        // A set's identity for dedup: its component modelIds sorted + comma-joined (order-independent).
        private static string SetSignature(List<OutfitComponent> comps)
        {
            var ids = new List<int>(comps.Count);
            foreach (var c in comps) ids.Add(c.ModelId);
            ids.Sort();
            return string.Join(",", ids);
        }

        // 商品名 → 系列基底名:去掉尾端一個(空白分隔的)描述部位/性別的詞。尾詞需含 男/女 或部位關鍵字才去,否則回 null(不合成)。
        internal static string SeriesBaseName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            int sp = name.LastIndexOf(' ');
            if (sp <= 0 || sp + 1 >= name.Length) return null;
            const string slotChars = "男女帽发髮装裝衣裤褲鞋鞋镜鏡链鏈膀巴饰飾情连連身套";
            foreach (char c in name.Substring(sp + 1)) if (slotChars.IndexOf(c) >= 0) return name.Substring(0, sp);
            return null;
        }

        // setinfo.dat sits beside iteminfo.dat (online 閉撰敃氪 pack). Same folders as ResolveIteminfoPath.
        private static string ResolveSetinfoPath()
        {
            var cands = new[]
            {
                Path.Combine(SdoExtracted.Root, "setinfo.dat"),
                Path.Combine(SdoExtracted.Root, "AVATAR", "setinfo.dat"),
            };
            foreach (var c in cands) if (VfsFile.Exists(c)) return c;
            try
            {
                var assets = Directory.GetParent(SdoExtracted.Root)?.Parent?.FullName;
                if (assets != null && VfsFile.DirectoryExists(assets))
                    foreach (var sub in VfsFile.GetDirectories(assets))
                    {
                        var f = Path.Combine(sub, "setinfo.dat");
                        if (VfsFile.Exists(f)) return f;
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
                try { if (VfsFile.DirectoryExists(dir)) foreach (var f in VfsFile.GetFiles(dir, "*.MSH")) set.Add(Path.GetFileName(f)); }
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
            foreach (var c in cands) if (VfsFile.Exists(c)) return c;
            try
            {
                var assets = Directory.GetParent(SdoExtracted.Root)?.Parent?.FullName;   // .../assets
                if (assets != null && VfsFile.DirectoryExists(assets))
                    foreach (var sub in VfsFile.GetDirectories(assets))
                    {
                        var f = Path.Combine(sub, "iteminfo.dat");
                        if (VfsFile.Exists(f)) return f;
                    }
            }
            catch { }
            return null;
        }

        // Overlay UTF-8 item names from shop_names.tsv (baked by tools/package_build.ps1) over the GBK-parsed names.
        // This is the primary fix for the standalone build's missing CP936 codepage; the GBK path below is the fallback.
        private static void ApplyNameSidecar(List<ShopItem> items)
        {
            if (items == null || items.Count == 0) return;
            var path = ResolveDataFile(ShopNameSidecar.FileName);
            if (path == null) return;
            Dictionary<int, string> map;
            try { map = ShopNameSidecar.Parse(VfsFile.ReadAllText(path, Encoding.UTF8)); }
            catch (Exception e) { Debug.LogWarning("[shop] " + ShopNameSidecar.FileName + " read failed: " + e.Message); return; }
            if (map.Count == 0) return;
            int n = 0;
            foreach (var it in items)
                if (map.TryGetValue(it.Id, out var nm) && !string.IsNullOrEmpty(nm) && it.Name != nm) { it.Name = nm; n++; }
            Debug.Log($"[shop] applied {n} UTF-8 name overrides from {ShopNameSidecar.FileName}");
        }

        // ---- Traditional-Chinese (TW 櫻式搖滾) name overlay ----
        // The TW client names far more of the on-disk meshes than the CN iteminfo.dat does, in Traditional Chinese.
        // tools/build_shop_names_tw.py bakes those names into shop_names_tw.tsv (category+modelId → UTF-8 name). We
        // hold the parsed map statically so BOTH the real-row overlay (ApplyTwNames) and the synth-row namer (SynthName,
        // reachable from the static Synthesize* rebuild path used by ById) can consult it. Keyed by (category, modelId)
        // — NOT item id — because ids differ between clients and synth mesh-only rows have no iteminfo id at all.
        private static Dictionary<long, string> _twNames;

        // Load (or clear) the TW name map for the current data root. Re-read every catalog Load() (cheap; the data root
        // can change between loads/tests), mirroring ApplyNameSidecar's un-cached read. Absent file → _twNames = null.
        private static void LoadTwNames()
        {
            _twNames = null;
            var path = ResolveDataFile(ShopNameTwSidecar.FileName);
            if (path == null) return;
            try { _twNames = ShopNameTwSidecar.Parse(VfsFile.ReadAllText(path, Encoding.UTF8)); }
            catch (Exception e) { Debug.LogWarning("[shop] " + ShopNameTwSidecar.FileName + " read failed: " + e.Message); }
        }

        // Overlay TW Traditional names over the (GBK-parsed / shop_names.tsv) names of real iteminfo rows, by (cat,modelId).
        private static void ApplyTwNames(List<ShopItem> items)
        {
            if (items == null || _twNames == null || _twNames.Count == 0) return;
            int n = 0;
            foreach (var it in items)
                if (_twNames.TryGetValue(ShopNameTwSidecar.Key(it.Category, it.ModelId), out var nm)
                    && !string.IsNullOrEmpty(nm) && it.Name != nm) { it.Name = nm; n++; }
            if (n > 0) Debug.Log($"[shop] applied {n} 繁體 name overrides from {ShopNameTwSidecar.FileName}");
        }

        // The display name for a synthesised mesh-only row: the TW Traditional name for this (category, modelId) if we
        // have one, else the bare 6-digit model serial (the historical fallback). Used by NewSynth + AllMeshModels so
        // every unnamed mesh row shows a real name where the TW client provides one.
        internal static string SynthName(int category, int modelId)
        {
            if (_twNames != null && _twNames.TryGetValue(ShopNameTwSidecar.Key(category, modelId), out var nm)
                && !string.IsNullOrEmpty(nm)) return nm;
            return modelId.ToString("D6");
        }

        // Resolve a data file by name using the same search order as ResolveIteminfoPath: the runtime data root and its
        // AVATAR subdir (built player), then any sibling assets subfolder (dev repo). Returns null when not found.
        private static string ResolveDataFile(string fileName)
        {
            var cands = new[]
            {
                Path.Combine(SdoExtracted.Root, fileName),
                Path.Combine(SdoExtracted.Root, "AVATAR", fileName),
            };
            foreach (var c in cands) if (VfsFile.Exists(c)) return c;
            try
            {
                var assets = Directory.GetParent(SdoExtracted.Root)?.Parent?.FullName;
                if (assets != null && VfsFile.DirectoryExists(assets))
                    foreach (var sub in VfsFile.GetDirectories(assets))
                    {
                        var f = Path.Combine(sub, fileName);
                        if (VfsFile.Exists(f)) return f;
                    }
            }
            catch { }
            return null;
        }

        // GBK/CP936 decoder for the Simplified-Chinese names. In a standalone build the codepage is only present when
        // Assets/link.xml preserves I18N.CJK (otherwise the stripper drops it and GetEncoding throws). If it truly is
        // unavailable the reader falls back to byte-preserving Latin1 — names show as mojibake but the catalog works.
        // Cached so both call sites (items + sets) share one lookup and warn at most once.
        private static Encoding _gbk;
        private static bool _gbkResolved;
        private static Encoding TryGetGbk()
        {
            if (_gbkResolved) return _gbk;
            _gbkResolved = true;
            foreach (var id in new object[] { 936, "GBK", "gb2312" })
            {
                try
                {
                    _gbk = (id is int cp) ? Encoding.GetEncoding(cp) : Encoding.GetEncoding((string)id);
                    if (_gbk != null) return _gbk;
                }
                catch { }
            }
            Debug.LogWarning("[shop] GBK/CP936 codepage unavailable in this runtime — item names will render as " +
                             "mojibake. The standalone build must include I18N.CJK (see Assets/link.xml).");
            return null;
        }
    }
}
