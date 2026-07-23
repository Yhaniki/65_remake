using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Shared builder for an SDO avatar's visible body parts: loads each .msh, creates a MeshFilter+MeshRenderer per
    /// submesh with the right material (garment DDS / shared skin DDS, hair two-sided, or the opaque portrait shader),
    /// and registers the skinned submesh on the <see cref="SdoAvatar"/>. Consolidates three near-identical copies of
    /// this loop — the in-stage dancer (ScreenGameplay.TryLoadAvatar), the head-portrait avatar
    /// (ScreenGameplay.BuildIdleHeadAvatar) and the lobby room avatar (SdoRoomAvatar.Build) — so a costume / 換裝
    /// change lives in ONE place. The caller owns the <see cref="SdoAvatar"/> itself (Setup / body-shape / motion /
    /// DPS) and any post-processing (bounds framing, cameras, hand trails). This is the single seam the 商城 (shop)
    /// equipment flow drives: swap the <c>parts</c> list and rebuild.
    /// </summary>
    public static class SdoAvatarBuilder
    {
        /// <summary>How the part materials are shaded.</summary>
        public enum SkinStyle
        {
            /// <summary>In-stage dancer / lobby avatar: Unlit/Texture, hair two-sided, and the COAT/PANT 2-material
            /// split (cloth range → garment DDS, skin range → shared W_Basic skin DDS).</summary>
            Gameplay,
            /// <summary>Head portrait / isolated: Sdo/PortraitOpaque, a single texture per submesh (clean opaque
            /// head for the portrait RT).</summary>
            Portrait,
        }

        /// <summary>Outcome of a build: how many .msh parts loaded and their merged model-space bounds.</summary>
        public struct Result { public int Parts; public Bounds Bounds; public bool Any; }

        /// <summary>The default WOMAN costume (6 body-part .msh) — the in-game dancer, the head portrait and the lobby
        /// avatar all start from this identical set. The 商城 equipment flow will replace this with a resolved,
        /// per-slot outfit; kept here as the single canonical default meanwhile.</summary>
        public static readonly string[] DefaultWomanParts =
        {
            "AVATAR/900007_WOMAN_FACE.MSH",
            "AVATAR/900017_WOMAN_HAIR.MSH",
            "AVATAR/900018_WOMAN_COAT.MSH",
            "AVATAR/900019_WOMAN_PANT.MSH",
            "AVATAR/900020_WOMAN_SHOES.MSH",
            "AVATAR/900011_WOMAN_HAND.MSH",
        };

        /// <summary>
        /// Load <paramref name="parts"/> (Extracted-relative .msh paths) under <paramref name="parent"/>, building a
        /// renderer per submesh and registering each skinned submesh on <paramref name="avatar"/> (may be null → static
        /// meshes only, no skinning). <paramref name="namePrefix"/> is prepended to child GameObject names (e.g. "h_"
        /// for the head portrait). Returns the merged model-space bounds + loaded part count.
        /// </summary>
        public static Result LoadParts(GameObject parent, SdoAvatar avatar, IEnumerable<string> parts,
                                       SkinStyle style, string namePrefix = "")
        {
            var res = new Result();
            int transparentOrder = 0;   // 本次 build 的透明衣物材質數 → 依載入(檔案 submesh)順序給固定 renderQueue
            var bodyShader = Shader.Find("Unlit/Texture");
            var hairShader = Shader.Find("Sdo/UnlitDoubleSided") ?? bodyShader;
            var glassShader = Shader.Find("Sdo/UnlitAvatarAlpha") ?? hairShader;   // 眼鏡 DXT3 有真 alpha → 鏡片半透(見眼睛),去背 a=0 隱形
            var sheerShader = Shader.Find("Sdo/UnlitAvatarSheer") ?? glassShader;  // 真紗質/蕾絲布料 → alpha-blend + 密度提升(比玻璃/翅膀多一層密度,見 shader)
            var portraitShader = Shader.Find("Sdo/PortraitOpaque") ?? bodyShader;
            var fallbackShader = Shader.Find("Unlit/Color");

            foreach (var rel0 in parts)
            {
                // 「只留皮膚」件:穿連身裙時補回身體幾何 (見 AvatarOutfit.WithBodySkinFiller) —— 載入這顆 mesh,但把
                // 非皮膚(布料)range 的三角形清空,只留 W/M_Basic 皮膚,身體後面才不會透空。
                bool skinOnly = AvatarOutfit.IsSkinOnly(rel0, out var rel);
                var path = ResolveAvatarFile(rel);
                var mshBytes = AvatarAssetCache.Read(path);   // cached +背景預讀 (商城捲動時整包 msh/dds 已在 RAM)
                if (mshBytes == null) { Debug.LogWarning("[avatar] missing " + rel); continue; }
                var r = MshLoader.Load(mshBytes);
                if (r == null || r.Submeshes.Count == 0) { Debug.LogWarning("[avatar] parse fail " + rel); continue; }
                var dir = Path.GetDirectoryName(path);
                string relU = rel.ToUpperInvariant();
                bool hair = relU.Contains("HAIR");
                // 眼鏡(GLASS) 與 翅膀(CHIBANG) 都是 DXT3 帶真 alpha 的半透明配件 → alpha-blend (鏡片透出眼睛;翅膀是發光羽翼、去背+半透)。
                bool alphaAccessory = relU.Contains("GLASS") || relU.Contains("CHIBANG");
                // Portrait: one opaque shader for everything. Gameplay: hair renders TWO-SIDED (single-sided Cull Back
                // hides inward strands → see-through gaps); glasses/wings ALPHA-BLEND (translucent); body parts stay
                // single-sided opaque (closed solids, less overdraw).
                var texShader = style == SkinStyle.Portrait ? portraitShader
                              : alphaAccessory ? glassShader
                              : hair ? hairShader
                              : bodyShader;
                string stem = Path.GetFileNameWithoutExtension(rel);
                // 炫 hair (model band [40000,49999] = 炫红/炫紫/炫白/炫黄) → its texture V scrolls at 2.0/s, sweeping a
                // bright band through the hair ("不斷變色"). Not for the Portrait head thumbnail; the in-world dancer/lobby
                // is where it shows. See SpecialMotionItems.IsUvScrollHair for the decompile + Frida trail.
                int? partModelId = SpecialMotionItems.ModelIdFromMeshPath(rel);
                bool uvScroll = style != SkinStyle.Portrait
                                && partModelId.HasValue && SpecialMotionItems.IsUvScrollHair(partModelId.Value);
                var sheerMats = new List<Material>();   // 這個部位的透明材質;≥2 片要關 prepass (見 SheerPrepassEnabled)
                int si = 0;
                foreach (var sub in r.Submeshes)   // each submesh = its own texture + skin (COAT/PANT have 2)
                {
                    var go = new GameObject(namePrefix + stem + "_" + si++);
                    go.transform.SetParent(parent.transform, false);
                    if (skinOnly) ShrinkFillerMesh(sub);   // 墊底件往內縮,才不會從衣服裡透出來
                    // 這件自帶的軀幹皮膚 (W_Basic_Coat*/Pants*) 若背後上部有真破洞 (024976 金姬兰:低背蕾絲後面
                    // 露出場景),用皮膚色把它補起來。只補「單材質皮膚 submesh」的背向內部孔,不碰領口/腰口/袖口。
                    else if (sub.Mesh.subMeshCount == 1 && IsSkinMaterialName(sub.Dds))
                        FillBackFacingSkinHoles(sub);
                    go.AddComponent<MeshFilter>().mesh = sub.Mesh;
                    var mr = go.AddComponent<MeshRenderer>();

                    // 2-material skin submeshes (COAT/PANT): cloth range → garment DDS, skin range → shared W_Basic DDS.
                    // Only meaningful for the full-body Gameplay style; the portrait head never shows them (keep single).
                    if (style != SkinStyle.Portrait && sub.Ranges != null && sub.Ranges.Count > 1
                        && sub.Mesh.subMeshCount == sub.Ranges.Count)
                    {
                        var mats = new Material[sub.Ranges.Count];
                        for (int s = 0; s < sub.Ranges.Count; s++)
                        {
                            int a = sub.Ranges[s].Attrib;
                            string nm = (sub.DdsNames != null && a >= 0 && a < sub.DdsNames.Length && !string.IsNullOrEmpty(sub.DdsNames[a])) ? sub.DdsNames[a] : sub.Dds;
                            nm = PreferOwnIdTexture(dir, rel, nm);   // 共用模板 mesh:內嵌的是模板 id → 換成道具自己 id 的改色貼圖 (070030 女鞋 reuse 012989 mesh)
                            var mf = MatFlagAt(sub, a);             // 官方逐材質透明旗標 (msh +0x194) — 這條 range 用的那筆材質
                            var t = ResolveDds(dir, nm, out var am, IsBodyGarment(rel), mf, out float tl);
                            // _texanimex(...) 佔位符不能退回 mesh 自己的 base atlas——那是 256 master 卡,靜態貼上會蓋掉換幀動畫(使用者看到的黃卡)
                            if (t == null && !TexAnimEx.TryParse(nm, out _)) t = ResolveDds(dir, MeshSelfDds(rel), out am, IsBodyGarment(rel), mf, out tl);   // 引到外來貼圖id → 退回 mesh 自己的 id
                            if (t == null && !string.IsNullOrEmpty(nm) && !TexAnimEx.TryParse(nm, out _)) Debug.LogWarning($"[avtex] item='{LogLabel}' {rel}: material '{nm}' unresolved → fallback colour {PartColor(rel)}");   // texanim 由下方 TryBuildTexAnim 渲染,非未解析
                            var sh = AlphaShaderFor(texShader, am, bodyShader, sheerShader, hairShader);   // 真孔洞→cutout不透明 / 真紗質→sheer alpha-blend(密度提升)
                            // 記下材質名 = DDS 名 (如 W_Basic_Coat2)，讓上層 (商城衣物縮圖) 能認出「膚色 range」把它藏掉。
                            // 「補身體」件的布料 range → 畫成單色皮膚 (見 AvatarOutfit.WithBodySkinFiller):連身裙開衩
                            // 或低背處後面才有身體擋著,而不是直接看穿到場景;又不會露出預設短褲的紅色。
                            if (skinOnly && !IsSkinMaterialName(nm)) { mats[s] = NewBodyFillMaterial(bodyShader); continue; }
                            mats[s] = t != null ? new Material(sh) { mainTexture = t, name = nm ?? "" }
                                                : (TryBuildTexAnim(go, dir, nm, texShader)
                                                   ?? new Material(fallbackShader) { color = PartColor(rel), name = nm ?? "" });
                            // 透明衣物 renderer 間的前後改固定順序 (見 TransparentGarmentQueue;距離排序逐幀翻轉=褲子閃爍)
                            if (IsBodyGarment(rel) && mats[s].renderQueue >= 3000) mats[s].renderQueue = TransparentGarmentQueue(transparentOrder++);
                            // 真紗 vs 實心去背布料 — 卡片縮圖靠它決定要不要換成雙面 cutout (見 IsSheerFabric)
                            if (mats[s].shader == sheerShader) { ApplySheerMaterialState(mats[s], tl); sheerMats.Add(mats[s]); }
                        }
                        mr.sharedMaterials = mats;
                    }
                    else
                    {
                        var dds = PreferOwnIdTexture(dir, rel, sub.Dds);   // 共用模板 mesh:換成道具自己 id 的改色貼圖
                        uint? mf1 = (sub.MatFlags != null && sub.MatFlags.Length > 0) ? sub.DdsFlags : (uint?)null;   // 官方旗標:單材質挑到的那筆
                        var tex = ResolveDds(dir, dds, out var am, IsBodyGarment(rel), mf1, out float tl1);
                        // texanim 佔位符不退回 base atlas(256 master 卡),讓下面走 TryBuildTexAnim 換幀動畫
                        if (tex == null && !TexAnimEx.TryParse(dds, out _)) tex = ResolveDds(dir, MeshSelfDds(rel), out am, IsBodyGarment(rel), mf1, out tl1);   // 引到外來貼圖id → 退回 mesh 自己的 id
                        if (tex == null && !string.IsNullOrEmpty(dds) && !TexAnimEx.TryParse(dds, out _)) Debug.LogWarning($"[avtex] item='{LogLabel}' {rel}: material '{dds}' unresolved → fallback colour {PartColor(rel)}");   // texanim 由下方 TryBuildTexAnim 渲染,非未解析
                        var sh = AlphaShaderFor(texShader, am, bodyShader, sheerShader, hairShader);   // 真孔洞→cutout不透明 / 真紗質→sheer(glassShader 是 Cull Off+無深度,037000 牛奶裝從背面看得到胸前字)
                        if (skinOnly && !IsSkinMaterialName(dds)) mr.sharedMaterial = NewBodyFillMaterial(bodyShader);
                        else
                        mr.sharedMaterial = tex != null ? new Material(sh) { mainTexture = tex, name = dds ?? "" }
                                                        : (TryBuildTexAnim(go, dir, dds, texShader)   // 翅膀/貼花 _TexAnimEx 動塗 → 換幀動畫
                                                           ?? new Material(fallbackShader) { color = PartColor(rel), name = dds ?? "" });
                        // 透明衣物 renderer 間的前後改固定順序 (見 TransparentGarmentQueue;距離排序逐幀翻轉=褲子閃爍)
                        if (IsBodyGarment(rel) && mr.sharedMaterial.renderQueue >= 3000) mr.sharedMaterial.renderQueue = TransparentGarmentQueue(transparentOrder++);
                        // 真紗 vs 實心去背布料 — 卡片縮圖靠它決定要不要換成雙面 cutout (見 IsSheerFabric)
                        if (mr.sharedMaterial.shader == sheerShader) { ApplySheerMaterialState(mr.sharedMaterial, tl1); sheerMats.Add(mr.sharedMaterial); }
                    }

                    if (uvScroll) AttachUvScroll(go, mr);   // 炫 hair → per-frame V texture scroll

                    if (avatar != null && sub.BindVerts != null && sub.BoneHrc != null)
                        avatar.AddPart(sub.Mesh, sub.BindVerts, sub.BoneHrc, sub.BoneWt, sub.MshInvBindByHrc);

                    var mb = sub.Mesh.bounds;
                    if (!res.Any) { res.Bounds = mb; res.Any = true; } else res.Bounds.Encapsulate(mb);
                }

                if (!SheerPrepassEnabled(sheerMats.Count))
                    foreach (var m in sheerMats) m.SetFloat(SheerPrepassProp, 0f);
                // 依這個部位的透明層數補密度 (寫深度只留最近的一層;見 SheerDensityFor)
                foreach (var m in sheerMats) m.SetFloat(SheerDensityProp, SheerDensityFor(sheerMats.Count));
                res.Parts++;
            }
            return res;
        }

        // 炫 hair UV scroll: attach AvatarUvScroll to scroll the textured material(s) V each frame (2.0 units/s, REPEAT
        // wrap). The existing hair shader (UnlitDoubleSided) uses TRANSFORM_TEX, so mainTextureOffset works as-is — no
        // shader swap. Skips skin ranges / fallback-colour materials (no mainTexture) so only the hair texture scrolls.
        private static void AttachUvScroll(GameObject go, Renderer mr)
        {
            var mats = mr.sharedMaterials;
            bool any = false;
            for (int i = 0; i < mats.Length; i++)
                if (mats[i] != null && mats[i].mainTexture != null) { any = true; break; }
            if (!any) return;
            go.AddComponent<AvatarUvScroll>().Init(mats, SpecialMotionItems.UvScrollUnitsPerSec);
        }

        // 翅膀(CHIBANG)的「動塗」：官方把發光羽翼做成 model-embedded 換幀貼圖。MSH 的材質名不是真檔名,而是佔位符
        // "_TexAnimEx(NAME)interval_..."(如 _texanimex(002090_woman_chibang)150_1.dds)。真正的貼圖是同資料夾 "<NAME>.an"
        // 列出的一串 DDS 幀(002090_woman_chibang_1/_2/_3.dds),依 interval(ms)輪播。ResolveDds 找不到佔位符 → 原本 fallback
        // 成一坨米色(user 看到的「flat tan 翅膀」)。這裡解出幀序列、先貼第 0 幀、再掛 MapobjTexAnimator 逐幀輪播。與場景道具
        // 共用同一套 TexAnimEx/MapobjTexAnimator(render/008 TexAnimEx_parse 的忠實移植)。回傳材質;非動塗/無幀 → 回 null 讓
        // 呼叫端走 fallback 色。
        internal static Material TryBuildTexAnim(GameObject go, string dir, string placeholder, Shader shader)
        {
            if (string.IsNullOrEmpty(dir) || !TexAnimEx.TryParse(placeholder, out var spec)) return null;
            string anPath = Path.Combine(dir, spec.Name + ".an");
            var anText = AvatarAssetCache.ReadText(anPath);
            if (anText == null) return null;
            var frameNames = TexAnimEx.ParseAn(anText);
            if (frameNames.Length == 0) return null;
            var frames = new List<Texture>(frameNames.Length);
            foreach (var fn in frameNames) { var t = ResolveAnimFrame(dir, fn); if (t != null) frames.Add(t); }
            if (frames.Count == 0) return null;
            // 布料(呼叫端傳不透明 Unlit/Texture)的換幀若帶真 alpha,不能沿用不透明 shader——透明底會畫成實心白板
            // (070025 領口愛心卡引 012989_* 幀:DXT3、99% 全透明)。依第一幀 alpha 分佈換 cutout/blend,與一般貼圖的
            // AlphaShaderFor 同語意。只動布料呼叫端:翅膀/髮/portrait 傳入的 alpha shader 是每條路徑調校過的,不覆蓋;
            // 無 alpha 幀(012983 衣身、場景閃燈)也維持原 shader。
            if (shader != null && shader.name == "Unlit/Texture")
            {
                try
                {
                    var am = AnimFrameAlphaMode(dir, frameNames[0]);
                    if (am == DdsAlphaMode.Cutout) shader = Shader.Find("Sdo/UnlitDoubleSided") ?? shader;
                    else if (am == DdsAlphaMode.Blend) shader = Shader.Find("Sdo/UnlitAvatarAlpha") ?? shader;
                }
                catch { }
            }
            // A wing whose frames are DXT1 GLOW on a BLACK background carry NO alpha, so straight blend paints the black as
            // a rectangle / a muddy 1-bit edge (使用者 FLY Pink Butterfly 008448:「邊緣去背沒修好」). It must be DE-BACKED:
            // derive an alpha from brightness (LoadDxt1BlackKeyed) so the black keys to transparent and the wing stays
            // SOLID, then draw it alpha-blend. (Additive was WRONG — it made "整個變半透明"; a de-back keeps the wing opaque
            // and only cuts the background.) Only DXT1-dark-glow frame-sets trigger it; DXT3/5 wings keep their alpha path.
            try
            {
                string p0 = FindDdsPath(dir, frameNames[0]);
                if (p0 != null && p0.ToLowerInvariant().EndsWith(".dds"))
                {
                    var fb = AvatarAssetCache.Read(p0);
                    if (fb != null && DdsLoader.LooksLikeDarkGlowDxt1(fb))
                    {
                        var keyed = new List<Texture>(frameNames.Length);
                        foreach (var fn in frameNames)
                        {
                            string fp = FindDdsPath(dir, fn);
                            var kb = fp != null ? AvatarAssetCache.Read(fp) : null;
                            var kt = kb != null ? DdsLoader.LoadDxt1BlackKeyed(kb) : null;
                            if (kt != null) keyed.Add(kt);
                        }
                        if (keyed.Count == frames.Count)   // every frame re-decoded → swap to the de-backed set
                        {
                            frames = keyed;
                            var blend = Shader.Find("Sdo/UnlitAvatarAlpha");
                            if (blend != null) shader = blend;
                        }
                    }
                }
            }
            catch { }
            var mat = new Material(shader) { mainTexture = frames[0], name = placeholder ?? "" };
            go.AddComponent<MapobjTexAnimator>().Init(new[] { mat }, frames.ToArray(), spec.IntervalMs > 0f ? spec.IntervalMs : 150f);
            return mat;
        }

        /// <summary>
        /// The alpha class of an animated-frame texture, whether it ships as .dds OR .tga. The frame-set shader used to
        /// be chosen from the .dds path only, so a CLOTH garment whose frames are TGA kept its opaque shader and painted
        /// the frames' transparent background as solid white rectangles — the whole SHINE Sexy Demon / Purple Lace
        /// family animates from 32-bit TGA frames (使用者:「看起來都是閃爍貼圖動畫沒去背」, 上衣/褲子都有).
        /// Returns Opaque when neither file resolves, which keeps the caller's shader untouched.
        /// </summary>
        public static DdsAlphaMode AnimFrameAlphaMode(string dir, string frameName)
        {
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(frameName)) return DdsAlphaMode.Opaque;
            // NOTE: FindDdsPath matches by NAME, so for a ".tga" frame it happily returns the TGA itself — decoding
            // that as DDS silently reported Opaque and left the frames on the opaque shader (the white blocks).
            string hit = FindDdsPath(dir, frameName);
            if (hit != null)
            {
                var b = AvatarAssetCache.Read(hit);
                if (b != null)
                    return hit.ToLowerInvariant().EndsWith(".tga")
                        ? DdsLoader.GetTgaAlphaMode(b)
                        : DdsLoader.GetSceneAlphaMode(b);
            }
            string stem = Path.GetFileNameWithoutExtension(frameName.Replace('\\', '/'));
            foreach (var cand in new[] { frameName, stem + ".tga", stem + ".TGA" })
            {
                if (string.IsNullOrEmpty(cand) || !cand.ToLowerInvariant().EndsWith(".tga")) continue;
                string p = Path.Combine(dir, Path.GetFileName(cand));
                if (!File.Exists(p)) continue;
                var b = AvatarAssetCache.Read(p);
                if (b != null) return DdsLoader.GetTgaAlphaMode(b);
            }
            return DdsAlphaMode.Opaque;
        }

        // Resolve one texanim frame name (from a .an list) to a texture. A frame may be a .dds (DXT, e.g. SCN0016
        // buildings / 002090 wings) OR a .tga (SDO ships many wing glow frames as 32-bit TGA, e.g. 花雨飞翼 023921 —
        // 023921_woman_chibang2_/3_.tga exist ONLY as TGA). Honour the listed extension, then fall back to the other
        // (frame 1 of some wings ships as BOTH .dds and .tga).
        private static Texture2D ResolveAnimFrame(string dir, string frameName)
        {
            if (string.IsNullOrEmpty(frameName)) return null;
            string ext = Path.GetExtension(frameName).ToLowerInvariant();
            if (ext == ".tga")
                return LoadTgaFile(dir, frameName) ?? ResolveDds(dir, Path.ChangeExtension(frameName, ".dds"));
            return ResolveDds(dir, frameName) ?? LoadTgaFile(dir, Path.ChangeExtension(frameName, ".tga"));
        }

        // Find a .tga by name (exact, then case-insensitive stem match — mirrors ResolveDds) and decode it.
        private static Texture2D LoadTgaFile(string dir, string name)
        {
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return null;
            string fn = Path.GetFileName(name.Replace('\\', '/'));
            string hit = null;
            string direct = Path.Combine(dir, fn);
            if (File.Exists(direct)) hit = direct;
            // 舊碼在此 Directory.GetFiles(dir,"*.*") 逐檔比對 —— AVATAR 資料夾有 67,000 個檔,一次未命中就是整包列舉。
            // 改走與 DDS 同款的一次性索引 (TgaIndex),之後每次查詢 O(1)。
            else if (TgaIndex(dir).TryGetValue(Path.GetFileNameWithoutExtension(fn).ToLowerInvariant(), out var f2)) hit = f2;
            if (hit == null) return null;
            try { var b = AvatarAssetCache.Read(hit); return b != null ? DdsLoader.LoadTga(b) : null; } catch { return null; }
        }

        /// <summary>Resolve an Extracted-relative avatar file (e.g. "AVATAR/012657_WOMAN_SHOES.MSH") to an absolute
        /// path. Prefers the runtime data root; falls back to the dev full-catalog staging (&lt;repo&gt;/assets/Datas)
        /// so the whole 商城 catalog is try-on-able in the editor even though only the starter models are under
        /// Extracted. Returns the root path (even if absent) when neither has it, so callers still log a miss.</summary>
        public static string ResolveAvatarFile(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return rel;
            lock (_resolveLock) if (_resolved.TryGetValue(rel, out var memo)) return memo;
            var p = Path.Combine(SdoExtracted.Root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(p))
            {
                var alt = DevDatasPath(rel);
                if (alt != null && File.Exists(alt)) p = alt;
            }
            lock (_resolveLock) _resolved[rel] = p;
            return p;
        }

        // rel → absolute, memoised: the 商城 asks for the same paths on every scroll step (visible cards + prefetch
        // look-ahead) and each miss costs 1-2 File.Exists probes in a 67k-file folder. The data set never changes at
        // runtime, so one answer per rel is enough. Locked — the prefetch worker resolves paths too.
        private static readonly Dictionary<string, string> _resolved = new Dictionary<string, string>();
        private static readonly object _resolveLock = new object();

        // Map an AVATAR-relative path to the dev full-catalog staging <repo>/assets/Datas/<rel>. Root in the editor is
        // <repo>/assets/sdox_offline/Extracted, so its grandparent is <repo>/assets. Null if that can't be derived.
        private static string DevDatasPath(string rel)
        {
            try
            {
                var assets = Directory.GetParent(SdoExtracted.Root)?.Parent?.FullName;   // .../assets
                return assets == null ? null : Path.Combine(assets, "Datas", rel.Replace('/', Path.DirectorySeparatorChar));
            }
            catch { return null; }
        }

        // Resolve an avatar DDS by material name within its folder: exact filename first, then a case-insensitive stem
        // match; decoded via DdsLoader with alpha-edge bleed for cut-out textures (mirrors ScreenGameplay.ResolveDds so
        // the dancer looks identical whichever path built it).
        /// <summary>Pick the material shader for a plain garment (texShader == bodyShader) by its texture's alpha class.
        /// Only the plain-garment path is eligible; hair/glasses/wings/portrait keep their own shader.
        ///   • <see cref="DdsAlphaMode.Cutout"/> — a SOLID body with REAL holes (e.g. the 眉画犹思 連身裙 037888:
        ///     16.5% alpha-0 lace holes + 72.5% solid) → <paramref name="cutoutShader"/> (alpha-TEST, a→1, ZWrite On):
        ///     the dress body stays fully opaque and the holes clip to reveal skin. Using alpha-BLEND here made the whole
        ///     solid dress see-through (the reported bug); the plain opaque shader instead painted the holes solid black.
        ///   • <see cref="DdsAlphaMode.Blend"/> — a genuinely SHEER fabric (lace/mesh/organza, e.g. Flower Lace Dress
        ///     024976) → <paramref name="garmentBlendShader"/> (Sdo/UnlitAvatarSheer: blend + density boost, ZWrite Off,
        ///     Queue=Transparent): its a=0 texels contribute nothing (skin shows) and the veil reads as dense translucent
        ///     fabric, no z-fight with the skin. Distinct from the glasses/wings Sdo/UnlitAvatarAlpha (no density boost).
        ///   • <see cref="DdsAlphaMode.Opaque"/> → the opaque <paramref name="bodyShader"/> unchanged.</summary>
        private static Shader AlphaShaderFor(Shader texShader, DdsAlphaMode am, Shader bodyShader, Shader garmentBlendShader, Shader cutoutShader)
        {
            if (texShader != bodyShader)
            {
                // 髮 mesh (hairShader = cutoutShader = Sdo/UnlitDoubleSided) 的柔性半透明層 —— 帽子的紗/veil (am=Blend) ——
                // 用 cutout 會被裁成透明線框 (兔乖乖 女帽)。真髮絲是硬鏤空 (Cutout) → 保留髮 shader;只有 veil 這種
                // Blend 層改實心 (可見,不再線框)。
                if (texShader == cutoutShader && am == DdsAlphaMode.Blend) return bodyShader;
                return texShader;
            }
            switch (am)
            {
                case DdsAlphaMode.Cutout: return cutoutShader;
                case DdsAlphaMode.Blend:  return garmentBlendShader;
                default:                  return bodyShader;   // Opaque
            }
        }

        public static Texture2D ResolveDds(string dir, string ddsName) => ResolveDds(dir, ddsName, out _);

        /// <summary>As <see cref="ResolveDds(string,string)"/> but also reports the texture's distribution-based alpha
        /// class (<see cref="DdsLoader.GetSceneAlphaMode"/>) so the caller can pick an alpha-blend material for a garment
        /// whose texture真的去背 (e.g. the 刺青/tattoo tops that are mostly transparent). Opaque tops report Opaque.</summary>
        public static Texture2D ResolveDds(string dir, string ddsName, out DdsAlphaMode sceneAlpha) => ResolveDds(dir, ddsName, out sceneAlpha, false);

        /// <summary>As above, but when <paramref name="bodyGarment"/> (coat/pant/one) a texture that comes back MOSTLY
        /// transparent is treated as OPAQUE — its alpha channel is broken (RGB fine, alpha exported all-0), which the
        /// cutout/blend path would otherwise render as a see-through wireframe (璀璨繁星 男褲/裙裝). Accessories
        /// (wings/glasses) legitimately are cut-outs, so they pass bodyGarment=false and keep their alpha.</summary>
        public static Texture2D ResolveDds(string dir, string ddsName, out DdsAlphaMode sceneAlpha, bool bodyGarment)
            => ResolveDds(dir, ddsName, out sceneAlpha, bodyGarment, null);

        /// <summary>As above, plus the material's OFFICIAL flags dword from the .msh (<see cref="MshLoader.SubMesh.MatFlags"/>).
        /// When supplied for a body garment the flag DECIDES the alpha mode (<see cref="OfficialAlphaMode"/>) — that is what
        /// the real engine does — and the texture histogram is only consulted for the RGB bleed decision. Pass null (or a
        /// non-garment) to keep the legacy histogram classification.</summary>
        public static Texture2D ResolveDds(string dir, string ddsName, out DdsAlphaMode sceneAlpha, bool bodyGarment, uint? matFlags)
            => ResolveDds(dir, ddsName, out sceneAlpha, bodyGarment, matFlags, out _);

        /// <summary>As above, and also reports the texture's genuinely-translucent texel fraction
        /// (<see cref="DdsLoader.AlphaStats.Translucent"/>) so the caller can tell REAL SHEER FABRIC from solid cloth
        /// that merely carries a silhouette alpha — see <see cref="IsSheerFabric"/>. 0 when the texture doesn't load.</summary>
        public static Texture2D ResolveDds(string dir, string ddsName, out DdsAlphaMode sceneAlpha, bool bodyGarment, uint? matFlags,
                                           out float translucent)
        {
            sceneAlpha = DdsAlphaMode.Opaque;
            translucent = 0f;
            string hit = FindDdsPath(dir, ddsName);
            if (hit == null) return null;
            try
            {
                var bytes = AvatarAssetCache.Read(hit);
                if (bytes == null) return null;
                // ONE alpha walk for all five answers (was HasAlpha + glow + scene + hard + translucent = 4 full
                // re-scans of the same texels per texture — pure repeat work on every 商城 card build).
                var st = DdsLoader.Analyze(bytes);
                sceneAlpha = st.Scene;   // distribution-based (≥3% 真洞才 Cutout) → 不會被雜訊誤判
                translucent = st.Translucent;
                if (bodyGarment && matFlags.HasValue)
                    sceneAlpha = OfficialAlphaMode(matFlags.Value, st.HasAlpha, st.Translucent);   // 旗標決定透不透明,直方圖決定紗/去背
                else if (bodyGarment && sceneAlpha != DdsAlphaMode.Opaque)
                    sceneAlpha = GarmentAlphaMode(sceneAlpha, st.HardTransp, st.Translucent, true);
                // 真紗料(alpha-BLEND)的 DXT3 只有 4-bit(16 階)alpha,平滑的紗漸層會被量化成一圈圈階梯(年輪) —— 使用者:
                // 「京族新娘 透明度有明顯漸層,跟 SCN0022 一樣」。用同一支 SmoothAlpha 低通把階梯抹成連續漸層(只動 alpha,
                // 保留孔洞細節)。只對紗料做:不透明/去背布料沒有這種漸層,平滑反而糊掉邊。
                var smooth = sceneAlpha == DdsAlphaMode.Blend ? DdsLoader.AlphaSmooth.PreserveDetail : DdsLoader.AlphaSmooth.None;
                return DdsLoader.Load(bytes, st.HasAlpha && !st.AdditiveGlow, smooth);
            }
            catch { return null; }
        }

        /// <summary>
        /// THE OFFICIAL RULE, pure. Reverse-engineered from the retail client (H:\sdo_cn\sdo.bin.c): a garment's
        /// transparency is NOT derived from its texture at all — the artist marks it per-material in the .msh, and the
        /// engine simply obeys. Material record +0x194 holds a small flags word (whole corpus: only 0/1/2/0x11/0x12)
        /// and the engine's ONE test on it is <see cref="MshLoader.MatFlagTransparentMask"/> (<c>flags &amp; 0x3f</c>):
        ///   • non-zero → the material is collected into the DEFERRED pass, drawn after the opaque body with
        ///     ALPHABLENDENABLE=TRUE + ALPHAOP=MODULATE (texture α × TFACTOR α) → straight alpha BLEND. ZWrite is never
        ///     disabled and cull stays single-sided, which is why the official client shows no see-through-body /
        ///     flicker artefacts;
        ///   • zero → the material draws in the opaque batch, so its alpha channel is IGNORED ENTIRELY. There is NO
        ///     alpha test anywhere in the engine (D3DRS_ALPHATESTENABLE is only ever cleared at device reset) — an
        ///     official garment is never a "cutout".
        /// Corpus evidence that the mask (not one bit) is the transparency signal: of the cloth textures used by
        /// flag-0 materials only 0.3% carry real alpha, versus 99.0% for flag-1 and 75.9% for flag-2. Treating flag 1
        /// as opaque made the 024976 lace dress paint its near-black texture solid over the liner (使用者:「比官方的黑」).
        /// <paramref name="textureHasAlpha"/> only short-circuits the pointless case of blending a texture with no alpha
        /// channel (DXT1 / all-255): the result is identical to opaque, and Opaque keeps it out of the transparent queue.
        ///
        /// This replaces the histogram guessing (<see cref="GarmentAlphaMode"/>) that produced the repeated
        /// "fix one garment, break another" seesaw — corpus-wide the two agreed on barely a third of cloth textures.
        /// Notable consequences, all verified against the flags in the shipped meshes: the Flower Lace dress renders an
        /// OPAQUE liner (024976 …ONE1_/2_, flag clear) under a BLENDED lace shell (…ONE_A/_B, flag set) — that layering,
        /// not a density hack, is where the official "denser than ours" look comes from; 京族新娘装 002178's outer skirt
        /// is NOT blended officially (so the official client never had the flicker we fought); and 紅色不羈牛仔 003136 is
        /// not blended either, i.e. officially a plain solid top.
        /// </summary>
        public static DdsAlphaMode OfficialAlphaMode(uint matFlags, bool textureHasAlpha)
            => OfficialAlphaMode(matFlags, textureHasAlpha, 1f);   // unknown texel mix → assume sheer (old behaviour)

        /// <summary>
        /// As above, but split the transparent batch into CUTOUT vs BLEND by the texture's genuinely-translucent texel
        /// fraction. The official flag decides ONLY opaque-vs-transparent; WITHIN the transparent batch the texture's
        /// own alpha distribution decides the KIND:
        ///   • a real SHEER fabric (lace / mesh / organza) has many mid-alpha texels (<paramref name="translucent"/> ≥
        ///     <see cref="SheerTranslucentBar"/>) → BLEND, so the skin shows through (金姬兰 lace 0.27-0.35, 京族 紗裙 0.325);
        ///   • a SILHOUETTE CUT-OUT garment (a solid dress whose alpha is ~all 0 or 255, its mid-alphas being only the
        ///     anti-aliased edge of the shape) has a LOW fraction → CUTOUT: clip the fully-transparent texels, draw the
        ///     rest OPAQUE. Blending it instead let the AA edges of each ruffle tier read ~45% transparent — a see-through
        ///     band the fabric should never have (使用者 on 黑色魅力娃娃 003834:「裙子的中間層有一段透明，那段貼圖不
        ///     應該透明」; measured translucent ≈ 0.005-0.016, i.e. a cut-out, not a sheer).
        /// This is NOT the old histogram guessing (which also decided opaque-vs-transparent and caused the seesaw): the
        /// flag still gates transparency; the histogram only picks how a KNOWN-transparent material is drawn.
        /// </summary>
        public static DdsAlphaMode OfficialAlphaMode(uint matFlags, bool textureHasAlpha, float translucent)
        {
            if ((matFlags & MshLoader.MatFlagTransparentMask) == 0 || !textureHasAlpha) return DdsAlphaMode.Opaque;
            return translucent >= SheerTranslucentBar ? DdsAlphaMode.Blend : DdsAlphaMode.Cutout;
        }

        /// <summary>The official flags for material <paramref name="index"/> of <paramref name="sub"/>, or null when the
        /// mesh carried no flag table (older/short material list) — callers then fall back to the histogram path.</summary>
        public static uint? MatFlagAt(MshLoader.SubMesh sub, int index)
            => (sub?.MatFlags != null && index >= 0 && index < sub.MatFlags.Length) ? sub.MatFlags[index] : (uint?)null;

        /// <summary>Pure body-garment (coat/pant/one/shoes) alpha-mode decision — the File-reading <see cref="ResolveDds"/>
        /// delegates the classification here so it can be unit-tested without a texture on disk. Inputs are the texture's
        /// distribution-based <paramref name="scene"/> class plus its hard-transparent (a≤8) and genuinely-translucent
        /// (mid-alpha) fractions. Rules, in order:
        ///   • a real SHEER fabric (lace/mesh/organza) — many mid-alpha texels AND not mostly-holes — stays alpha-BLEND so
        ///     the skin shows through (Flower Lace Dress 024976_WOMAN_ONE: translucent≈0.27-0.35, hardTransp≈0.07-0.11).
        ///     This is checked FIRST because <see cref="DdsLoader.GetSceneAlphaMode"/> would otherwise call such lace
        ///     Cutout (hard clip + a→1 = solid black sleeves, the "透明度沒做出來" report) and the opaque-force below would
        ///     flatten a soft one entirely. The mid-alpha bar sits at 0.21 — the midpoint of the two verified anchor
        ///     sets — because a SOLID garment whose edges are big soft FRINGES scores mid-alpha from the edge gradient
        ///     alone (紅色不羈牛仔 003136_WOMAN_COAT_: feather fringes, translucent≈0.153 yet 73% of visible texels fully
        ///     opaque; the old 0.15 bar classed it sheer → the whole top rendered transparent). Verified sheer positives
        ///     all measure ≥0.274 (024976 A/B 0.348/0.274, 牛奶裝 037000 0.317), fringe/hem look-alikes ≤0.153;
        ///   • a broken/spurious all-0 alpha channel (&gt;70% holes) OR a soft-shaded SOLID garment (scene=Blend, few
        ///     midtones) → OPAQUE, else it renders as a see-through wireframe / whole-dress transparency (璀璨繁星 男褲);
        ///   • otherwise keep the scene class — a SOLID dress with hard lace-hem holes stays Cutout (眉画犹思 037888:
        ///     translucent≈0.09, so it is NOT mistaken for sheer fabric).
        /// Accessories (wings/glasses/hair) never reach here (bodyGarment=false) and keep their own alpha untouched.</summary>
        public static DdsAlphaMode GarmentAlphaMode(DdsAlphaMode scene, float hardTransp, float translucent, bool bodyGarment)
        {
            if (!bodyGarment || scene == DdsAlphaMode.Opaque) return scene;
            if (hardTransp <= 0.7f && translucent >= SheerTranslucentBar) return DdsAlphaMode.Blend;
            if (scene == DdsAlphaMode.Blend || hardTransp > 0.7f) return DdsAlphaMode.Opaque;
            return scene;
        }

        /// <summary>Shop-set label (item name, or 6-digit id when unnamed) that the <c>[avtex]</c> fallback warnings
        /// print, so the log says WHICH shop item failed (使用者:「不然我不知道哪個 log 對到哪個衣服」). Set before a
        /// card/preview build; harmless when null.</summary>
        public static string LogLabel;

        /// <summary>True for a body garment slot (coat/pant/one-piece/shoes) whose texture alpha is classified by
        /// <see cref="GarmentAlphaMode"/> — broken alpha → opaque, genuine sheer fabric → blend — as opposed to
        /// accessories (wings/glasses/hair) that legitimately use alpha cut-outs. Public so the room/gender/wardrobe
        /// avatar loop (<see cref="SdoRoomAvatar"/>) shares the SAME predicate and doesn't drift from the shop builder.</summary>
        /// <summary>True when a material name is one of the shared SKIN bases (W_Basic_* / M_Basic_*) — the bare
        /// arms/legs/torso baked into a garment mesh. Corpus-checked: every skin texture contains "Basic" and no cloth
        /// texture does. Used by the <see cref="AvatarOutfit.SkinOnlyPrefix"/> filler parts, which draw ONLY these.</summary>
        /// <summary>Flat body tone used by the <see cref="AvatarOutfit.SkinOnlyPrefix"/> filler for the parts of the
        /// default body mesh that are CLOTH (the starter shorts share one texture with the legs, so cloth and skin
        /// cannot be separated by material). Painting them plain skin means a one-piece's slit shows a body behind it
        /// instead of the scene — and never the starter shorts' red.</summary>
        public static readonly Color BodyFillSkinColor = new Color(0.94f, 0.80f, 0.71f);

        /// <summary>
        /// Close BACK-FACING geometry holes in a garment's own SKIN mesh (the W_Basic body baked into the garment). A
        /// backless / halter one-piece (024976 金姬兰) ships its body skin with an actual GAP cut out of the upper-centre
        /// BACK — the lace over it is see-through, so the gap shows the scene straight through (使用者:「背後肩部破洞」).
        /// There is no separate nude base body in the data, but the hole is a real boundary loop in THIS mesh, so we fan-
        /// fill it with skin: the added triangles sit ON the back surface (coplanar with the surrounding skin), so they
        /// can NEVER poke through the front the way a separate filler mesh did.
        ///
        /// Only loops that are (a) BACK-facing (mean z &gt; 0) and (b) INTERIOR — not touching the mesh's Y extremes (the
        /// neck / waist openings) nor its X extremes (the arm-holes) — are filled, so the arms/neck/waist stay open.
        /// Pure geometry on the Unity mesh (subMeshCount must be 1 = a single-material skin submesh). No-op otherwise.
        /// </summary>
        /// <summary>Diagnostic toggle: when true, <see cref="FillBackFacingSkinHoles"/> logs every boundary loop it sees
        /// and whether it filled or which gate rejected it. Off in normal builds to keep the test log clean.</summary>
        public static bool LogHoleFill = false;

        public static void FillBackFacingSkinHoles(MshLoader.SubMesh sub)
        {
            var mesh = sub?.Mesh;
            if (mesh == null || mesh.subMeshCount != 1) return;
            var verts = mesh.vertices;
            var tris = mesh.triangles;
            if (verts.Length == 0 || tris.Length < 3) return;

            // These skin meshes DUPLICATE vertices along UV seams (see 零蒙皮接縫頂點): one physical edge is shared by
            // two triangles that reference DIFFERENT-indexed-but-SAME-POSITION vertices, so a naive "directed edge whose
            // reverse index isn't present" test flags every seam as a false hole AND leaves the real hole's boundary
            // walk broken (it never closes → silently dropped, which is why 024976's 肩部破洞 survived the first pass).
            // Weld by POSITION first: edges are counted in canonical (position) space, so seam twins cancel and only a
            // genuine geometry hole stays unbalanced.
            var canon = new int[verts.Length];
            var canonPos = new List<int>();                         // canonical id → representative original vertex
            var posToCanon = new Dictionary<long, int>();
            // multiplier must exceed twice the quantised coordinate range (|coord|·512 ≲ 30000) so distinct
            // positions never share a key — 40961 was too small (y·512≈26000 overflowed the x bucket → false welds).
            long PosKey(Vector3 v) => ((long)Mathf.RoundToInt(v.x * 512f) * 1000003L + Mathf.RoundToInt(v.y * 512f)) * 1000003L + Mathf.RoundToInt(v.z * 512f);
            for (int i = 0; i < verts.Length; i++)
            {
                long pk = PosKey(verts[i]);
                if (!posToCanon.TryGetValue(pk, out int ci)) { ci = canonPos.Count; posToCanon[pk] = ci; canonPos.Add(i); }
                canon[i] = ci;
            }

            // net directed-edge count in canonical space; net(u,v) = uses(u→v) − uses(v→u). A closed hole leaves net>0
            // on the way round; interior + seam edges net to 0.
            var edgeNet = new Dictionary<long, int>();
            long EKey(int a, int b) => ((long)a << 32) | (uint)b;
            for (int t = 0; t < tris.Length; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = canon[tris[t + e]], b = canon[tris[t + (e + 1) % 3]];
                    if (a == b) continue;
                    edgeNet[EKey(a, b)] = edgeNet.TryGetValue(EKey(a, b), out var c) ? c + 1 : 1;
                }
            var outAdj = new Dictionary<int, List<int>>();          // canonical from → to's still to be walked
            foreach (var kv in edgeNet)
            {
                int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
                int net = kv.Value - (edgeNet.TryGetValue(EKey(b, a), out var r) ? r : 0);
                for (int k = 0; k < net; k++)
                {
                    if (!outAdj.TryGetValue(a, out var lst)) { lst = new List<int>(); outAdj[a] = lst; }
                    lst.Add(b);
                }
            }
            if (outAdj.Count == 0) return;

            Bounds mb = mesh.bounds;
            float yLo = mb.min.y + mb.size.y * 0.06f, yHi = mb.max.y - mb.size.y * 0.06f;
            float xIn = mb.size.x * 0.44f;   // |x| below this = interior (not an arm-hole at the sides)

            // Skinning must stay in lockstep: every added vertex needs a bind position and a 4-bone palette entry,
            // else AddPart's Work buffer / PrepareGpuMesh.boneWeights mismatch mesh.vertexCount. The bind copy is what
            // the skeleton actually deforms; the rendered `verts` are already this same bind pose at load time.
            var bind = sub.BindVerts; var bhrc = sub.BoneHrc; var bwt = sub.BoneWt;
            bool hasSkin = bind != null && bind.Length == verts.Length
                           && bhrc != null && bhrc.Length == verts.Length * 4
                           && bwt != null && bwt.Length == verts.Length * 4;

            var addVerts = new List<Vector3>(); var addUV = new List<Vector2>(); var addTris = new List<int>();
            var addBind = new List<Vector3>(); var addHrc = new List<int>(); var addWt = new List<float>();
            var uvs = mesh.uv; bool hasUv = uvs != null && uvs.Length == verts.Length;
            foreach (var startNode in new List<int>(outAdj.Keys))
            {
                while (outAdj.TryGetValue(startNode, out var slist) && slist.Count > 0)
                {
                    // walk one closed loop starting at startNode, consuming edges as we go
                    var loopC = new List<int>();
                    int cur = startNode; int guard = 0; bool closed = false;
                    while (guard++ < 8192)
                    {
                        loopC.Add(cur);
                        if (!outAdj.TryGetValue(cur, out var lst) || lst.Count == 0) break;
                        int nxt = lst[lst.Count - 1]; lst.RemoveAt(lst.Count - 1);   // consume this boundary edge
                        if (nxt == startNode) { closed = true; break; }
                        cur = nxt;
                    }
                    if (!closed || loopC.Count < 3) continue;

                    // map canonical loop → representative original vertices (share the same position)
                    var loop = new List<int>(loopC.Count);
                    foreach (var ci in loopC) loop.Add(canonPos[ci]);

                    Vector3 c = Vector3.zero; float minY = float.MaxValue, maxY = float.MinValue, maxAbsX = 0f, meanZ = 0f;
                    foreach (var vi in loop)
                    {
                        var v = verts[vi]; c += v; meanZ += v.z;
                        minY = Mathf.Min(minY, v.y); maxY = Mathf.Max(maxY, v.y); maxAbsX = Mathf.Max(maxAbsX, Mathf.Abs(v.x));
                    }
                    c /= loop.Count; meanZ /= loop.Count;
                    // fill ONLY a back-facing interior hole: on the back, not reaching neck/waist/arm-holes.
                    string reject = meanZ <= 0f ? "front(meanZ<=0)"
                                  : (minY < yLo || maxY > yHi) ? "neck/waist(Y-extreme)"
                                  : maxAbsX > xIn ? "arm-hole(X-extreme)" : null;
                    if (LogHoleFill)
                        Debug.Log($"[holefill] '{sub.Dds}' loop n={loop.Count} c=({c.x:0.#},{c.y:0.#},{c.z:0.#}) " +
                                  $"meanZ={meanZ:0.##} y=[{minY:0.#}..{maxY:0.#}] gate=[{yLo:0.#}..{yHi:0.#}] |x|max={maxAbsX:0.#}/{xIn:0.#} " +
                                  $"→ {(reject ?? "FILL")}");
                    if (reject != null) continue;

                    int centre = verts.Length + addVerts.Count;
                    addVerts.Add(c); if (hasUv) addUV.Add(new Vector2(0.5f, 0.5f));
                    if (hasSkin)
                    {
                        Vector3 cb = Vector3.zero; foreach (var vi in loop) cb += bind[vi]; cb /= loop.Count;
                        addBind.Add(cb);
                        int src = loop[0] * 4;   // borrow the palette of a boundary vertex (all on the back torso → same spine bones)
                        for (int k = 0; k < 4; k++) { addHrc.Add(bhrc[src + k]); addWt.Add(bwt[src + k]); }
                    }
                    for (int i = 0; i < loop.Count; i++)
                    {
                        int a = loop[i], b = loop[(i + 1) % loop.Count];
                        addTris.Add(a); addTris.Add(b); addTris.Add(centre);         // one winding
                        addTris.Add(b); addTris.Add(a); addTris.Add(centre);         // + reverse (double-sided cap)
                    }
                }
            }
            if (addVerts.Count == 0) return;

            var nv = new List<Vector3>(verts); nv.AddRange(addVerts);
            var nt = new List<int>(tris); nt.AddRange(addTris);
            mesh.vertices = nv.ToArray();
            if (hasUv) { var nu = new List<Vector2>(uvs); nu.AddRange(addUV); mesh.uv = nu.ToArray(); }
            mesh.triangles = nt.ToArray();
            mesh.RecalculateBounds();

            if (hasSkin && addBind.Count == addVerts.Count)
            {
                var nb = new List<Vector3>(bind); nb.AddRange(addBind); sub.BindVerts = nb.ToArray();
                var nh = new List<int>(bhrc); nh.AddRange(addHrc); sub.BoneHrc = nh.ToArray();
                var nw = new List<float>(bwt); nw.AddRange(addWt); sub.BoneWt = nw.ToArray();
            }
        }

        private static Texture2D _bodyFillTex;
        /// <summary>1×1 skin swatch for the filler. It is a TEXTURE (not a plain Unlit/Color) because every downstream
        /// pass — the shop card's shader swap, the wardrobe thumbnail, the texanim probe — reads
        /// <c>material.mainTexture</c> and Unity logs an error for a material without <c>_MainTex</c>.</summary>
        public static Texture2D BodyFillTexture
        {
            get
            {
                if (_bodyFillTex == null)
                {
                    _bodyFillTex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { name = "bodyfill" };
                    _bodyFillTex.SetPixel(0, 0, BodyFillSkinColor);
                    _bodyFillTex.Apply(false, true);
                }
                return _bodyFillTex;
            }
        }

        /// <summary>A flat skin-tone material for the body filler's cloth ranges (see <see cref="BodyFillSkinColor"/>).</summary>
        public static Material NewBodyFillMaterial(Shader opaqueTextured)
            => new Material(opaqueTextured) { mainTexture = BodyFillTexture, name = "bodyfill" };

        /// <summary>How much a body-filler mesh is pulled toward the body axis before it is drawn. The filler is a real
        /// garment's bare-legs geometry (<see cref="AvatarOutfit.BareLegsFiller"/>), so although its hip is narrower
        /// than a dress's WIDEST point it still poked skin through where the dress hugs tighter than average
        /// (024976 金姬兰's front skirt). Shrinking X/Z tucks it inside every garment; Y is untouched so it still
        /// reaches from the knees to the waist.</summary>
        public const float BodyFillShrink = 0.82f;

        /// <summary>Pure: pull <paramref name="v"/> toward the vertical body axis by <see cref="BodyFillShrink"/>.</summary>
        /// <summary>Apply <see cref="ShrinkToBodyAxis"/> to a filler submesh — both the rendered vertices AND the bind
        /// positions the skinning uses, so the shrunken shape follows the skeleton exactly like the original.</summary>
        internal static void ShrinkFillerMesh(MshLoader.SubMesh sub)
        {
            if (sub?.Mesh == null) return;
            var v = sub.Mesh.vertices;
            for (int i = 0; i < v.Length; i++) v[i] = ShrinkToBodyAxis(v[i]);
            sub.Mesh.vertices = v;
            sub.Mesh.RecalculateBounds();
            if (sub.BindVerts != null)
                for (int i = 0; i < sub.BindVerts.Length; i++) sub.BindVerts[i] = ShrinkToBodyAxis(sub.BindVerts[i]);
        }

        public static Vector3 ShrinkToBodyAxis(Vector3 v)
            => new Vector3(v.x * BodyFillShrink, v.y, v.z * BodyFillShrink);

        public static bool IsSkinMaterialName(string materialName)
            => !string.IsNullOrEmpty(materialName)
               && materialName.IndexOf("Basic", System.StringComparison.OrdinalIgnoreCase) >= 0;

        public static bool IsBodyGarment(string rel)
        { string u = (rel ?? "").ToUpperInvariant(); return u.Contains("COAT") || u.Contains("PANT") || u.Contains("_ONE") || u.Contains("SHOES"); }

        /// <summary>Pure: deterministic render queue for the <paramref name="order"/>-th TRANSPARENT body-garment
        /// material of one avatar build. Unity sorts same-queue transparent renderers per frame by bounds distance;
        /// two overlapping sheer garment renderers (002178 艳红 京族新娘装: 白褲 sub2 與外層紗裙 sub3 共用同一張半透明
        /// pant_ 貼圖,各自是一個 renderer) swap that order as the skinned bounds move with the idle motion, so the
        /// pants flipped every few frames between "painted over the skirt" and "depth-clipped behind it" (使用者:
        /// 「褲子會一直閃爍」). Distinct queue values in LOAD order — file submesh order, which is the official D3D9
        /// draw order — pin the composite; clamped so deep part lists stay inside the transparent band (&lt; 4000).</summary>
        public static int TransparentGarmentQueue(int order) => 3000 + (order < 0 ? 0 : order > 400 ? 400 : order);

        /// <summary>Shader property that turns the sheer depth prepass on (1) / off (0) — see the shader header.</summary>
        public const string SheerPrepassProp = "_PrepassZWrite";

        /// <summary>Pure: does a garment part that produced <paramref name="sheerMaterialCount"/> blended materials keep
        /// the depth PREPASS? The retail client draws its transparent list SORTED back-to-front with ZWrite on, so its
        /// stacked sheer layers accumulate AND no piece shows its own far side. We draw in a PINNED (non-depth) order to
        /// stop Unity's per-frame distance sort from flickering, so we approximate that with this switch:
        ///   • ONE blended material in the part → keep the prepass: nearest-surface-only is what stops 037000 牛奶裝's
        ///     far-side sleeve compositing over the chest;
        ///   • TWO OR MORE (024976 金姬兰: lace shell + liner; 003136's four fringe materials) → drop it, or whichever
        ///     piece happens to draw first depth-rejects the rest, leaving a single thin layer
        ///     (使用者:「金姬兰 又太透明,官方沒有這麼透明」 / 002178「變成下半身透明」).</summary>
        public static bool SheerPrepassEnabled(int sheerMaterialCount) => sheerMaterialCount < 2;

        /// <summary>Shader property flagging a blended material as REAL SHEER FABRIC (1) vs solid cloth (0) — see
        /// <see cref="IsSheerFabric"/> and the shader header. Set on every material the builders route to the sheer
        /// shader.</summary>
        public const string SheerFabricProp = "_SheerFabric";

        /// <summary>Mid-alpha bar separating a real translucent WEAVE from solid cloth whose alpha only cuts a
        /// silhouette. Verified anchors: sheer ≥0.274 (024976 lace 0.348/0.274, 037000 牛奶裝 0.317, 002178 紗褲 0.325),
        /// solid ≤0.164 (001766 Skirt Suit 0.008, 002178 上衣 0.005, 003136 流蘇 0.153, 001934 上衣 0.164).</summary>
        public const float SheerTranslucentBar = 0.21f;

        /// <summary>Pure: is a material the official flags routed to alpha-BLEND a real SHEER WEAVE, or SOLID cloth
        /// whose alpha channel only cuts its silhouette? Both blend identically in-game, but they must be drawn
        /// DIFFERENTLY on a shop/wardrobe card, where the body is hidden: single-sided solid cloth then leaves the
        /// neckline a see-through HOLE instead of showing the garment's own far side (使用者 on 001766 Skirt Suit:
        /// 「領口後面應該是有衣服的，但框裡面變成透明透過後面」) — before the official-flag switch those garments
        /// classified as Cutout and the card drew them with the TWO-SIDED shader, which is what filled the neckline.
        /// Real sheer must NOT swap to that shader: clipping + α→1 turns a lace dress into solid black sleeves
        /// (使用者:「格子裡面的透明度沒改」). Same measure and bar as <see cref="GarmentAlphaMode"/>'s sheer test.</summary>
        public static bool IsSheerFabric(float translucent) => translucent >= SheerTranslucentBar;

        /// <summary>Shader state properties the blend garment shader exposes per material (see its header).</summary>
        public const string SheerCullProp = "_CullMode", SheerZWriteProp = "_ZWriteMode";

        /// <summary>
        /// Tag ONE material the official flags routed to the blend shader, and give it the render state its FABRIC
        /// needs. This is the single seam both avatar pipelines use, so they cannot drift.
        ///   • real sheer weave (<see cref="IsSheerFabric"/>) → ZWrite OFF + Cull Back: the garment's stacked lace
        ///     layers accumulate, which is where the official density comes from (024976 金姬兰 ships 18 of them).
        ///   • solid cut-out cloth (alpha only carves the silhouette) → ZWrite ON + Cull OFF: it behaves like the
        ///     opaque cloth it visually is, so a low neckline can't read as see-through
        ///     (使用者:「低領口衣服背面會變成透明穿透」) and the two-sided draw fills the neckline with the garment's
        ///     own inside instead of a hole.
        /// This per-material state is the ONLY sanctioned way to fix self-see-through — adding a second shader pass
        /// makes every blended garment vanish (see the shader header and GarmentFlickerFixTests).
        /// </summary>
        /// <summary>Shader property holding the fabric density power.</summary>
        public const string SheerDensityProp = "_Density";

        /// <summary>
        /// Pure: the <c>_Density</c> a part's blended materials should use, given how many of them that part has.
        /// Depth-writing means only the surface nearest the camera survives, so a garment that officially STACKS many
        /// blended layers loses them and reads far too sheer — 024976 金姬兰 ships 18 blended materials and needed the
        /// "fabric over itself twice" compensation (使用者:「又太透明,官方沒有這麼透明」). A garment with only a layer
        /// or two (001294 紫魅 露背晚礼: one solid coat + one chiffon panel) never had that stack, so the same
        /// compensation just makes it muddy (使用者:「灰色那塊是不夠透明」). Compensate only where layers were lost.
        /// </summary>
        public static float SheerDensityFor(int sheerMaterialCount) => sheerMaterialCount >= 3 ? 2f : 1f;

        public static void ApplySheerMaterialState(Material m, float translucent)
        {
            if (m == null) return;
            bool sheer = IsSheerFabric(translucent);
            m.SetFloat(SheerFabricProp, sheer ? 1f : 0f);
            // BOTH classes draw two-sided. Single-sided leaves a HOLE wherever the garment's front panel doesn't cover
            // and only its far side faces the camera — a skirt slit then showed the scene straight through
            // (使用者:「金姬兰 腳上破一個洞,官方沒有」), and a low neckline showed the same on solid cloth. Drawing the
            // inside surface fills both with the garment's own far side, which is also what the client shows.
            m.SetFloat(SheerCullProp, 0f);
            // BOTH classes also WRITE DEPTH. Leaving ZWrite off for sheer let every layer of the garment composite,
            // which reads as "you can see the other side through it" — from behind you saw the front of the dress, and
            // at the hip (where the mesh has no body geometry under the cloth) you saw straight through
            // (使用者:「背面可以看到前面,而且內褲位置前面可以看到後面」). With depth on, the surface nearest the camera
            // wins, exactly like the opaque body. The density that accumulation used to provide is now supplied by
            // _Density in the shader (raise it if a lace garment reads too sheer).
            //
            // NOTE: the textbook fix — a ColorMask-0 depth PREPASS — is impossible here: this project renders with URP,
            // and URP draws only ONE untagged pass per material, so a 2-pass shader silently draws just the invisible
            // prepass and every blended garment disappears. See the shader header.
            m.SetFloat(SheerZWriteProp, 1f);
        }

        /// <summary>The mesh's OWN texture derived from its filename: 'AVATAR/023441_WOMAN_ONE.MSH' → '023441_WOMAN_ONE.dds'.
        /// Some meshes embed a FOREIGN texture id in their material name (祕密花園 023441_WOMAN_ONE 引 'sh1226_woman_one.dds',
        /// 不存在) — when that fails to resolve, the mesh's own id texture is the correct fallback.</summary>
        private static string MeshSelfDds(string rel)
        { string stem = Path.GetFileNameWithoutExtension((rel ?? "").Replace('\\', '/')); return string.IsNullOrEmpty(stem) ? null : stem + ".dds"; }

        /// <summary>The leading 6-digit item id of a name ("070030_woman_shoes.dds" → "070030"), or null when it isn't a
        /// "NNNNNN_…" filename (shared skin bases like "W_Basic_Pants2.dds", or a "sh1226_…" alias, have no leading id).</summary>
        private static string LeadingSixDigitId(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length < 7) return null;
            for (int i = 0; i < 6; i++) if (name[i] < '0' || name[i] > '9') return null;
            return name[6] == '_' ? name.Substring(0, 6) : null;
        }

        /// <summary>Pure: rewrite <paramref name="materialName"/> so its leading 6-digit TEMPLATE id becomes
        /// <paramref name="ownId"/>; null when there's nothing to swap (no leading 6-digit id, or it already equals ownId).
        /// Handles a plain "NNNNNN_woman_shoes.dds" AND a "_texanimex(NNNNNN_woman_shoes)100_1.dds" placeholder (the id
        /// inside the parens is swapped, the interval kept). Unit-testable; the caller confirms the swapped-id file exists
        /// before using the result. This is the "shared-template mesh" case: 070030 女鞋 reuses 012989's geometry, so its
        /// material names embed 012989's id, but it ships its OWN recolour textures under 070030_*.</summary>
        public static string SwapLeadingId(string materialName, string ownId)
        {
            if (string.IsNullOrEmpty(materialName) || string.IsNullOrEmpty(ownId)) return null;
            bool isAnim = TexAnimEx.TryParse(materialName, out var spec);
            string inner = isAnim ? spec.Name : materialName;          // "012989_woman_shoes" | "012989_woman_shoes.dds"
            string embId = LeadingSixDigitId(inner);
            if (embId == null || embId == ownId) return null;
            string swappedInner = ownId + inner.Substring(embId.Length);
            if (!isAnim) return swappedInner;                          // "070030_woman_shoes.dds"
            int open = materialName.IndexOf('('), close = open >= 0 ? materialName.IndexOf(')', open + 1) : -1;
            if (open < 0 || close < 0) return null;
            return materialName.Substring(0, open + 1) + swappedInner + materialName.Substring(close);   // "_texanimex(070030_woman_shoes)100_1.dds"
        }

        /// <summary>Prefer the mesh's OWN-id recolour texture over a shared-template mesh's embedded template-id material
        /// name — but ONLY when the own-id file is actually present, else the material is returned unchanged (so a shared
        /// skin base, or a template whose recolour wasn't shipped, keeps working). Needs the folder to probe for the file,
        /// so it wraps the pure <see cref="SwapLeadingId"/> with an existence check (.an for a texanim placeholder, else
        /// the .dds itself). Fixes 070030 女鞋 rendering the template's pink shoe + a static 256 master-card decal.
        /// Public so the room/gender-select loop (<see cref="SdoRoomAvatar"/>) can share the SAME id-swap — else that
        /// self-contained loop renders shared-template recolours (070025 男上衣) in the TEMPLATE's colour (使用者回報:
        /// 商城看是黃色,房間/選男女看變粉紅).</summary>
        public static string PreferOwnIdTexture(string dir, string rel, string materialName)
        {
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(materialName)) return materialName;
            string ownId = LeadingSixDigitId(Path.GetFileName((rel ?? "").Replace('\\', '/')));
            if (ownId == null) return materialName;
            string swapped = SwapLeadingId(materialName, ownId);
            if (swapped == null) return materialName;
            bool isAnim = TexAnimEx.TryParse(swapped, out var spec);
            bool exists = isAnim ? File.Exists(Path.Combine(dir, spec.Name + ".an"))   // 換幀清單存在才換 → 走 own-id 動畫
                                 : FindDdsPath(dir, swapped) != null;                  // own-id 圖集存在才換
            return exists ? swapped : materialName;
        }

        // Cache: dir -> (normalised dds stem -> file path). The AVATAR folder holds ~40k files; the old per-call
        // Directory.GetFiles + linear stem scan was O(files) EVERY resolve. Built once per dir; also powers the fuzzy match.
        // Lock-guarded: the 商城 background prefetch (AvatarAssetCache) resolves texture paths off the main thread, so
        // both the lookup and the one-time build must be serialised (a torn Dictionary would throw mid-scroll).
        private static readonly Dictionary<string, Dictionary<string, string>> _ddsByNorm = new Dictionary<string, Dictionary<string, string>>();
        private static readonly object _indexLock = new object();
        private static Dictionary<string, string> DdsIndex(string dir)
        {
            lock (_indexLock)
            {
                if (_ddsByNorm.TryGetValue(dir, out var m)) return m;
                m = new Dictionary<string, string>();
                try { foreach (var f in Directory.GetFiles(dir, "*.dds")) { var k = NormStem(Path.GetFileNameWithoutExtension(f)); if (!m.ContainsKey(k)) m[k] = f; } }
                catch { }
                _ddsByNorm[dir] = m;
                return m;
            }
        }

        // Same one-time index for .tga frames (翅膀 wings ship many glow frames as TGA only).
        private static readonly Dictionary<string, Dictionary<string, string>> _tgaByStem = new Dictionary<string, Dictionary<string, string>>();
        private static Dictionary<string, string> TgaIndex(string dir)
        {
            lock (_indexLock)
            {
                if (_tgaByStem.TryGetValue(dir, out var m)) return m;
                m = new Dictionary<string, string>();
                try { foreach (var f in Directory.GetFiles(dir, "*.tga")) { var k = Path.GetFileNameWithoutExtension(f).ToLowerInvariant(); if (!m.ContainsKey(k)) m[k] = f; } }
                catch { }
                _tgaByStem[dir] = m;
                return m;
            }
        }

        /// <summary>
        /// Fuzzy DDS lookup for avatar meshes whose embedded material name doesn't match the on-disk file 1:1 —
        /// artist-authored variants that the strict exact/stem match misses, dropping the part to a flat fallback
        /// colour (white faces, brown hair, blue coats):
        ///   • a spurious separator before a digit — <c>..._face_huan_1</c> vs on-disk <c>..._face_huan1</c>
        ///   • the recurring <c>haun</c>↔<c>huan</c> transposition — <c>..._face_haun0</c> vs <c>..._face_huan0</c>
        ///   • an extra numeric suffix on a shared base — <c>M_Basic_face01</c> vs on-disk <c>M_Basic_face</c>
        /// Normalise (lowercase, haun→huan, drop separators) and match; then, as a last resort, strip a trailing digit
        /// run to hit the shared base. ONLY reached after exact + stem match fail, so items that already resolve are untouched.
        /// </summary>
        /// <summary>On-disk .dds path for a material/frame name (exact filename first, then fuzzy stem), no decode.
        /// Shared by ResolveDds and the texanim frame alpha probe. Null when nothing matches.</summary>
        public static string FindDdsPath(string dir, string ddsName)
        {
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(ddsName)) return null;
            string name = Path.GetFileName(ddsName.Replace('\\', '/'));
            string direct = Path.Combine(dir, name);
            if (File.Exists(direct)) return direct;
            return FuzzyFindDds(dir, Path.GetFileNameWithoutExtension(name));
        }

        public static string FuzzyFindDds(string dir, string stem)
        {
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem)) return null;
            var idx = DdsIndex(dir);
            string norm = NormStem(stem);
            if (idx.TryGetValue(norm, out var f1)) return f1;
            string baseStem = System.Text.RegularExpressions.Regex.Replace(norm, "[0-9]+$", "");
            if (baseStem.Length > 0 && baseStem != norm && idx.TryGetValue(baseStem, out var f2)) return f2;
            return null;
        }

        private static string NormStem(string s)
            => (s ?? "").ToLowerInvariant().Replace("haun", "huan").Replace("_", "").Replace("-", "").Replace(" ", "");

        /// <summary>Flat fallback colour when a part's DDS can't be resolved (keeps the silhouette readable).</summary>
        public static Color PartColor(string rel)
        {
            string u = rel.ToUpperInvariant();
            if (u.Contains("HAIR")) return new Color(0.30f, 0.20f, 0.16f);
            if (u.Contains("FACE") || u.Contains("HAND")) return new Color(0.96f, 0.82f, 0.72f);
            if (u.Contains("COAT")) return new Color(0.42f, 0.62f, 0.92f);
            if (u.Contains("PANT")) return new Color(0.86f, 0.86f, 0.92f);
            if (u.Contains("SHOES")) return new Color(0.22f, 0.20f, 0.26f);
            return new Color(0.80f, 0.75f, 0.70f);
        }
    }
}
