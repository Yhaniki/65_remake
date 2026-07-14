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
            var bodyShader = Shader.Find("Unlit/Texture");
            var hairShader = Shader.Find("Sdo/UnlitDoubleSided") ?? bodyShader;
            var glassShader = Shader.Find("Sdo/UnlitAvatarAlpha") ?? hairShader;   // 眼鏡 DXT3 有真 alpha → 鏡片半透(見眼睛),去背 a=0 隱形
            var portraitShader = Shader.Find("Sdo/PortraitOpaque") ?? bodyShader;
            var fallbackShader = Shader.Find("Unlit/Color");

            foreach (var rel in parts)
            {
                var path = ResolveAvatarFile(rel);
                if (!File.Exists(path)) { Debug.LogWarning("[avatar] missing " + rel); continue; }
                var r = MshLoader.Load(File.ReadAllBytes(path));
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
                int si = 0;
                foreach (var sub in r.Submeshes)   // each submesh = its own texture + skin (COAT/PANT have 2)
                {
                    var go = new GameObject(namePrefix + stem + "_" + si++);
                    go.transform.SetParent(parent.transform, false);
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
                            var t = ResolveDds(dir, nm, out var am, IsBodyGarment(rel));
                            if (t == null) t = ResolveDds(dir, MeshSelfDds(rel), out am, IsBodyGarment(rel));   // 引到外來貼圖id → 退回 mesh 自己的 id
                            if (t == null && !string.IsNullOrEmpty(nm)) Debug.LogWarning($"[avtex] item='{LogLabel}' {rel}: material '{nm}' unresolved → fallback colour {PartColor(rel)}");
                            var sh = AlphaShaderFor(texShader, am, bodyShader, glassShader, hairShader);   // 真孔洞→cutout不透明 / 全軟→alpha-blend
                            // 記下材質名 = DDS 名 (如 W_Basic_Coat2)，讓上層 (商城衣物縮圖) 能認出「膚色 range」把它藏掉。
                            mats[s] = t != null ? new Material(sh) { mainTexture = t, name = nm ?? "" }
                                                : (TryBuildTexAnim(go, dir, nm, texShader)
                                                   ?? new Material(fallbackShader) { color = PartColor(rel), name = nm ?? "" });
                        }
                        mr.sharedMaterials = mats;
                    }
                    else
                    {
                        var tex = ResolveDds(dir, sub.Dds, out var am, IsBodyGarment(rel));
                        if (tex == null) tex = ResolveDds(dir, MeshSelfDds(rel), out am, IsBodyGarment(rel));   // 引到外來貼圖id → 退回 mesh 自己的 id
                        if (tex == null && !string.IsNullOrEmpty(sub.Dds)) Debug.LogWarning($"[avtex] item='{LogLabel}' {rel}: material '{sub.Dds}' unresolved → fallback colour {PartColor(rel)}");
                        var sh = AlphaShaderFor(texShader, am, bodyShader, glassShader, hairShader);   // 真孔洞→cutout不透明 / 全軟→alpha-blend
                        mr.sharedMaterial = tex != null ? new Material(sh) { mainTexture = tex, name = sub.Dds ?? "" }
                                                        : (TryBuildTexAnim(go, dir, sub.Dds, texShader)   // 翅膀 _TexAnimEx 動塗 → 換幀動畫
                                                           ?? new Material(fallbackShader) { color = PartColor(rel), name = sub.Dds ?? "" });
                    }

                    if (avatar != null && sub.BindVerts != null && sub.BoneHrc != null)
                        avatar.AddPart(sub.Mesh, sub.BindVerts, sub.BoneHrc, sub.BoneWt, sub.MshInvBindByHrc);

                    var mb = sub.Mesh.bounds;
                    if (!res.Any) { res.Bounds = mb; res.Any = true; } else res.Bounds.Encapsulate(mb);
                }
                res.Parts++;
            }
            return res;
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
            if (!File.Exists(anPath)) return null;
            var frameNames = TexAnimEx.ParseAn(File.ReadAllText(anPath));
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
                    string p0 = FindDdsPath(dir, frameNames[0]);   // 只認 .dds 幀;TGA 幀=翅膀,不會走進這個 if
                    if (p0 != null)
                    {
                        var am = DdsLoader.GetSceneAlphaMode(File.ReadAllBytes(p0));
                        if (am == DdsAlphaMode.Cutout) shader = Shader.Find("Sdo/UnlitDoubleSided") ?? shader;
                        else if (am == DdsAlphaMode.Blend) shader = Shader.Find("Sdo/UnlitAvatarAlpha") ?? shader;
                    }
                }
                catch { }
            }
            var mat = new Material(shader) { mainTexture = frames[0], name = placeholder ?? "" };
            go.AddComponent<MapobjTexAnimator>().Init(new[] { mat }, frames.ToArray(), spec.IntervalMs > 0f ? spec.IntervalMs : 150f);
            return mat;
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
            else
            {
                string stem = Path.GetFileNameWithoutExtension(fn).ToLowerInvariant();
                foreach (var f in Directory.GetFiles(dir, "*.*"))
                    if (Path.GetExtension(f).ToLowerInvariant() == ".tga" && Path.GetFileNameWithoutExtension(f).ToLowerInvariant() == stem) { hit = f; break; }
            }
            if (hit == null) return null;
            try { return DdsLoader.LoadTga(File.ReadAllBytes(hit)); } catch { return null; }
        }

        /// <summary>Resolve an Extracted-relative avatar file (e.g. "AVATAR/012657_WOMAN_SHOES.MSH") to an absolute
        /// path. Prefers the runtime data root; falls back to the dev full-catalog staging (&lt;repo&gt;/assets/Datas)
        /// so the whole 商城 catalog is try-on-able in the editor even though only the starter models are under
        /// Extracted. Returns the root path (even if absent) when neither has it, so callers still log a miss.</summary>
        public static string ResolveAvatarFile(string rel)
        {
            var p = Path.Combine(SdoExtracted.Root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(p)) return p;
            var alt = DevDatasPath(rel);
            return (alt != null && File.Exists(alt)) ? alt : p;
        }

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
        ///   • <see cref="DdsAlphaMode.Blend"/> — a mostly-soft gradient (glass, additive glows, a 去背/tattoo decal like
        ///     至尊王者无敌 000558) → <paramref name="glassShader"/> (blend, ZWrite Off, Queue=Transparent): its a=0
        ///     texels contribute nothing (skin shows) and the soft body reads as translucent, no z-fight with the skin.
        ///   • <see cref="DdsAlphaMode.Opaque"/> → the opaque <paramref name="bodyShader"/> unchanged.</summary>
        private static Shader AlphaShaderFor(Shader texShader, DdsAlphaMode am, Shader bodyShader, Shader glassShader, Shader cutoutShader)
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
                case DdsAlphaMode.Blend:  return glassShader;
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
        {
            sceneAlpha = DdsAlphaMode.Opaque;
            string hit = FindDdsPath(dir, ddsName);
            if (hit == null) return null;
            try
            {
                var bytes = File.ReadAllBytes(hit);
                bool hasAlpha = DdsLoader.HasAlpha(bytes);
                bool additiveGlow = hasAlpha && DdsLoader.LooksLikeAdditiveGlow(bytes);
                sceneAlpha = DdsLoader.GetSceneAlphaMode(bytes);   // distribution-based (≥3% 真洞才 Cutout) → 不會被雜訊誤判
                // 布料(COAT/PANT/ONE/SHOES)的 alpha 壞掉(全透明 >70% = 匯出壞/atlas 留白;柔性 Blend = 光影漸層非透明)→ 當實心,
                // 否則畫成透明線框/袖子穿透。但 Cutout(<70% 硬鏤空,如裙擺蕾絲/去背刺青)是「真透明」設計 → 保留不動。
                if (bodyGarment && sceneAlpha != DdsAlphaMode.Opaque
                    && (sceneAlpha == DdsAlphaMode.Blend || DdsLoader.HardTransparentFraction(bytes) > 0.7f))
                    sceneAlpha = DdsAlphaMode.Opaque;
                return DdsLoader.Load(bytes, bleedAlphaEdges: hasAlpha && !additiveGlow);
            }
            catch { return null; }
        }

        /// <summary>Shop-set label (item name, or 6-digit id when unnamed) that the <c>[avtex]</c> fallback warnings
        /// print, so the log says WHICH shop item failed (使用者:「不然我不知道哪個 log 對到哪個衣服」). Set before a
        /// card/preview build; harmless when null.</summary>
        public static string LogLabel;

        /// <summary>True for a body garment slot (coat/pant/one-piece) whose texture must stay opaque when its alpha is
        /// broken — as opposed to accessories (wings/glasses/hair) that legitimately use alpha cut-outs.</summary>
        private static bool IsBodyGarment(string rel)
        { string u = (rel ?? "").ToUpperInvariant(); return u.Contains("COAT") || u.Contains("PANT") || u.Contains("_ONE") || u.Contains("SHOES"); }

        /// <summary>The mesh's OWN texture derived from its filename: 'AVATAR/023441_WOMAN_ONE.MSH' → '023441_WOMAN_ONE.dds'.
        /// Some meshes embed a FOREIGN texture id in their material name (祕密花園 023441_WOMAN_ONE 引 'sh1226_woman_one.dds',
        /// 不存在) — when that fails to resolve, the mesh's own id texture is the correct fallback.</summary>
        private static string MeshSelfDds(string rel)
        { string stem = Path.GetFileNameWithoutExtension((rel ?? "").Replace('\\', '/')); return string.IsNullOrEmpty(stem) ? null : stem + ".dds"; }

        // Cache: dir -> (normalised dds stem -> file path). The AVATAR folder holds ~40k files; the old per-call
        // Directory.GetFiles + linear stem scan was O(files) EVERY resolve. Built once per dir; also powers the fuzzy match.
        private static readonly Dictionary<string, Dictionary<string, string>> _ddsByNorm = new Dictionary<string, Dictionary<string, string>>();
        private static Dictionary<string, string> DdsIndex(string dir)
        {
            if (_ddsByNorm.TryGetValue(dir, out var m)) return m;
            m = new Dictionary<string, string>();
            try { foreach (var f in Directory.GetFiles(dir, "*.dds")) { var k = NormStem(Path.GetFileNameWithoutExtension(f)); if (!m.ContainsKey(k)) m[k] = f; } }
            catch { }
            _ddsByNorm[dir] = m;
            return m;
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
