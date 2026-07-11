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
                            var t = ResolveDds(dir, nm, out var am);
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
                        var tex = ResolveDds(dir, sub.Dds, out var am);
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
        private static Material TryBuildTexAnim(GameObject go, string dir, string placeholder, Shader shader)
        {
            if (string.IsNullOrEmpty(dir) || !TexAnimEx.TryParse(placeholder, out var spec)) return null;
            string anPath = Path.Combine(dir, spec.Name + ".an");
            if (!File.Exists(anPath)) return null;
            var frameNames = TexAnimEx.ParseAn(File.ReadAllText(anPath));
            if (frameNames.Length == 0) return null;
            var frames = new List<Texture>(frameNames.Length);
            foreach (var fn in frameNames) { var t = ResolveAnimFrame(dir, fn); if (t != null) frames.Add(t); }
            if (frames.Count == 0) return null;
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
            if (texShader != bodyShader) return texShader;
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
        public static Texture2D ResolveDds(string dir, string ddsName, out DdsAlphaMode sceneAlpha)
        {
            sceneAlpha = DdsAlphaMode.Opaque;
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(ddsName)) return null;
            string name = Path.GetFileName(ddsName.Replace('\\', '/'));
            string direct = Path.Combine(dir, name);
            string hit = File.Exists(direct) ? direct : null;
            if (hit == null)
            {
                string stem = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
                foreach (var f in Directory.GetFiles(dir, "*.*"))
                    if (Path.GetExtension(f).ToLowerInvariant() == ".dds" && Path.GetFileNameWithoutExtension(f).ToLowerInvariant() == stem) { hit = f; break; }
            }
            if (hit == null) return null;
            try
            {
                var bytes = File.ReadAllBytes(hit);
                bool hasAlpha = DdsLoader.HasAlpha(bytes);
                bool additiveGlow = hasAlpha && DdsLoader.LooksLikeAdditiveGlow(bytes);
                sceneAlpha = DdsLoader.GetSceneAlphaMode(bytes);   // distribution-based (≥3% 真洞才 Cutout) → 不會被雜訊誤判
                return DdsLoader.Load(bytes, bleedAlphaEdges: hasAlpha && !additiveGlow);
            }
            catch { return null; }
        }

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
