using System.IO;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Builds a default female (WOMAN) SDO avatar for the waiting room — the same skeleton + 6 body parts + standby
    /// idle the in-game dancer uses (ScreenGameplay.TryLoadAvatar), but standalone so the room can spawn one without
    /// going through the play screen. Reused for both the walkable local player (full-body, in-scene) and the isolated
    /// head-portrait avatar (parked off-stage, rendered head-only). Avatar motion/skinning is driven by SdoAvatar.
    /// </summary>
    public static class SdoRoomAvatar
    {
        // Default WOMAN costume — identical to ScreenGameplay's defaults so the lobby avatar matches the dancer.
        public static readonly string[] WomanParts =
        {
            "AVATAR/900007_WOMAN_FACE.MSH",
            "AVATAR/900017_WOMAN_HAIR.MSH",
            "AVATAR/900018_WOMAN_COAT.MSH",
            "AVATAR/900019_WOMAN_PANT.MSH",
            "AVATAR/900020_WOMAN_SHOES.MSH",
            "AVATAR/900011_WOMAN_HAND.MSH",
        };
        public const string FemaleHrc = "AVATAR/FEMALE.HRC";
        public const string IdleMot = "MOTION/WREST0056.MOT";   // LOBBY standby idle (motion cat 0) — NOT the in-game
                                                                 // arena idle WREST0072 (cat 0x15); the room holds standby
        public const string WalkMot = "MOTION/WWALK0001.MOT";   // free-walk clip (StateRoom walk category 6)

        // Default MAN costume — the 900001..900006 body set (mirrors the WOMAN 900007.. set part-for-part). Used by the
        // standalone gender-select preview (GenderSelectScreen) so the male toggle shows a real male dancer, not a
        // recoloured female. The decompiled rest table maps lobby standby cat 0 to MREST0067 for male, WREST0056
        // for female; free-walk has a male-skeleton MWALK0001 variant.
        public static readonly string[] ManParts =
        {
            "AVATAR/900001_MAN_FACE.MSH",
            "AVATAR/900002_MAN_HAIR.MSH",
            "AVATAR/900003_MAN_COAT.MSH",
            "AVATAR/900004_MAN_PANT.MSH",
            "AVATAR/900006_MAN_SHOES.MSH",
            "AVATAR/900005_MAN_HAND.MSH",
        };
        public const string MaleHrc = "AVATAR/MALE.HRC";
        public const string MaleIdleMot = "MOTION/MREST0067.MOT";   // LOBBY standby idle (male rest cat 0)
        public const string MaleWalkMot = "MOTION/MWALK0001.MOT";   // free-walk clip (male)

        public static string[] DefaultParts(bool male) => male ? ManParts : WomanParts;

        /// <summary>How the avatar's materials are set up for its render target.</summary>
        public enum RenderMode
        {
            /// <summary>Full body over an OPAQUE 3D scene (room/gameplay): Unlit/Texture + two-sided hair, with the
            /// 2-material COAT/PANT skin submeshes resolved per-range (so arms/legs aren't painted with cloth).</summary>
            Scene,
            /// <summary>Head-only over a TRANSPARENT portrait RT (result/room head): Sdo/PortraitOpaque (opaque + hair
            /// cutout for a clean silhouette) and a single material per submesh (the head never shows COAT/PANT skin).</summary>
            PortraitHead,
            /// <summary>Full body over a TRANSPARENT RT (gender-select preview): the PortraitOpaque opaque-cutout shader
            /// so hair gaps don't punch transparent holes / occlude the face on the alpha-cleared RT, BUT the 2-material
            /// COAT/PANT skin ranges are kept (it's a whole body, not just a head).</summary>
            PreviewBody,
        }

        /// <summary>
        /// Build the avatar onto <paramref name="parent"/>: load FEMALE.HRC, the 6 WOMAN parts and the idle clip, set
        /// the default (thin) body shape, arm the idle pose, and put everything on <paramref name="layer"/>. When
        /// <paramref name="portraitOpaque"/> the parts use the Sdo/PortraitOpaque shader (clean opaque head for the
        /// portrait RT); otherwise normal Unlit/Texture + two-sided hair, with the 2-material COAT/PANT skin submeshes
        /// resolved per-range (so arms/legs aren't painted with cloth). Returns the SdoAvatar, or null if the skeleton
        /// or every part failed to load.
        /// </summary>
        public static SdoAvatar Build(GameObject parent, int layer, bool portraitOpaque)
            => Build(parent, layer, portraitOpaque, male: false);

        /// <summary>Gendered overload: <paramref name="male"/> true loads MALE.HRC + the MAN body set + the male
        /// standby idle (and the male body-weight baseline); false is the default WOMAN build above. Everything else
        /// (shaders, 2-material skin ranges, layering) is identical.</summary>
        public static SdoAvatar Build(GameObject parent, int layer, bool portraitOpaque, bool male)
            => Build(parent, layer, portraitOpaque, male, null);

        /// <summary>Back-compat bool overload: <paramref name="portraitOpaque"/> true → <see cref="RenderMode.PortraitHead"/>,
        /// false → <see cref="RenderMode.Scene"/>. New callers should pass a <see cref="RenderMode"/> directly.</summary>
        public static SdoAvatar Build(GameObject parent, int layer, bool portraitOpaque, bool male, string[] equippedParts)
            => Build(parent, layer, portraitOpaque ? RenderMode.PortraitHead : RenderMode.Scene, male, equippedParts);

        public static SdoAvatar Build(GameObject parent, int layer, RenderMode mode, bool male = false, string[] equippedParts = null)
        {
            // PortraitHead + PreviewBody both composite over an alpha-cleared RT → use the opaque-cutout shader so hair
            // gaps stay transparent instead of writing depth/alpha holes over the face. Only the head portrait collapses
            // the COAT/PANT submeshes to a single material (it never shows them); the full-body preview keeps the ranges.
            bool useCutout = mode != RenderMode.Scene;
            bool singleMaterial = mode == RenderMode.PortraitHead;
            string hrcRel = male ? MaleHrc : FemaleHrc;
            var bodyParts = NormalizeParts(equippedParts, male);
            // 用 ResolveAvatarFile(Root + dev Datas 全量) 解析,不再只找 Root —— 商城買的衣物 mesh 常只在 Datas 全量目錄,
            // Root-only 會漏(→ 房間人變光頭)。與左側預覽/遊戲內舞者同一條解析路徑,穿搭在房間才一致。
            string hrcPath = SdoAvatarBuilder.ResolveAvatarFile(hrcRel);
            if (!File.Exists(hrcPath)) { Debug.LogWarning("[room-avatar] missing " + hrcPath); return null; }
            var hrc = HrcLoader.Load(File.ReadAllBytes(hrcPath));
            if (hrc == null) { Debug.LogWarning("[room-avatar] HRC parse fail"); return null; }

            var idle = LoadMot(male ? MaleIdleMot : IdleMot);
            var av = parent.AddComponent<SdoAvatar>();
            av.Setup(hrc, idle);
            av.SetBodyShape(SdoBodyShape.WeightFromIndex(0, male));   // default thin body (male/female baseline)
            av.RestMot = idle;
            av.BlendSec = 0.5f;   // 0.3s smoothstep crossfade on idle↔walk (and the mirrored head portrait) — no hard cut

            var bodyShader = Shader.Find("Unlit/Texture");
            var hairShader = Shader.Find("Sdo/UnlitDoubleSided") ?? bodyShader;
            var portraitShader = Shader.Find("Sdo/PortraitOpaque") ?? bodyShader;
            var fallback = Shader.Find("Unlit/Color");

            int parts = 0;
            foreach (var rel in bodyParts)
            {
                var path = SdoAvatarBuilder.ResolveAvatarFile(rel);   // Root + dev Datas 全量 (見上;修光頭)
                if (!File.Exists(path)) { Debug.LogWarning("[room-avatar] missing " + rel); continue; }
                var r = MshLoader.Load(File.ReadAllBytes(path));
                if (r == null || r.Submeshes.Count == 0) { Debug.LogWarning("[room-avatar] parse fail " + rel); continue; }
                var dir = Path.GetDirectoryName(path);
                bool hair = rel.ToUpperInvariant().Contains("HAIR");
                var sh = useCutout ? portraitShader : (hair ? hairShader : bodyShader);
                int si = 0;
                foreach (var sub in r.Submeshes)
                {
                    var go = new GameObject(Path.GetFileNameWithoutExtension(rel) + "_" + si++);
                    go.transform.SetParent(parent.transform, false);
                    go.AddComponent<MeshFilter>().mesh = sub.Mesh;
                    var mr = go.AddComponent<MeshRenderer>();

                    // 2-material skin submeshes (COAT/PANT): cloth range -> garment DDS, skin range -> shared W_Basic DDS.
                    // Only meaningful for the full-body avatar; the head portrait never shows them, so keep it single.
                    if (!singleMaterial && sub.Ranges != null && sub.Ranges.Count > 1 && sub.Mesh.subMeshCount == sub.Ranges.Count)
                    {
                        var mats = new Material[sub.Ranges.Count];
                        for (int s = 0; s < sub.Ranges.Count; s++)
                        {
                            int a = sub.Ranges[s].Attrib;
                            string nm = (sub.DdsNames != null && a >= 0 && a < sub.DdsNames.Length && !string.IsNullOrEmpty(sub.DdsNames[a])) ? sub.DdsNames[a] : sub.Dds;
                            var t = ResolveDds(dir, nm);
                            mats[s] = t != null ? new Material(sh) { mainTexture = t } : new Material(fallback) { color = PartColor(rel) };
                        }
                        mr.sharedMaterials = mats;
                    }
                    else
                    {
                        var tex = ResolveDds(dir, sub.Dds);
                        mr.sharedMaterial = tex != null ? new Material(sh) { mainTexture = tex } : new Material(fallback) { color = PartColor(rel) };
                    }

                    if (sub.BindVerts != null && sub.BoneHrc != null)
                        av.AddPart(sub.Mesh, sub.BindVerts, sub.BoneHrc, sub.BoneWt, sub.MshInvBindByHrc);
                }
                parts++;
            }
            if (parts == 0) { Debug.LogWarning("[room-avatar] no parts loaded"); Object.Destroy(av); return null; }

            av.PoseInitialIdle();           // arm the idle so the first frame isn't the bind/T-pose
            SetLayerRecursive(parent, layer);
            return av;
        }

        /// <summary>商城 shop preview overload: builds EXACTLY the given <paramref name="parts"/> (item-only card, or a
        /// full composed outfit) on the <paramref name="hrcRel"/> skeleton (the wshop/mshop mannequin for cards), via the
        /// shared <see cref="SdoAvatarBuilder"/>. When <paramref name="bindPoseNoIdle"/> the skeleton shows its BIND POSE
        /// with no motion (the official AvtShow display mode). Kept separate from the room/gender overloads above so each
        /// path preserves its own tested behaviour.</summary>
        public static SdoAvatar Build(GameObject parent, int layer, bool portraitOpaque, string[] parts, string hrcRel = null,
                                      bool bindPoseNoIdle = false)
        {
            string hrcPath = SdoAvatarBuilder.ResolveAvatarFile(hrcRel ?? FemaleHrc);   // MALE.HRC for male outfits
            if (!File.Exists(hrcPath)) { Debug.LogWarning("[room-avatar] missing " + hrcPath); return null; }
            var hrc = HrcLoader.Load(File.ReadAllBytes(hrcPath));
            if (hrc == null) { Debug.LogWarning("[room-avatar] HRC parse fail"); return null; }

            // bindPoseNoIdle (商城 shop preview): show the skeleton's BIND POSE with NO motion — exactly the official
            // AvtShow (AvtShow_LoadModelByName → AvatarHelper_Create(name,0), no .mot). The shop passes the wshop/mshop
            // MANNEQUIN hrc whose bind is arms-down + STRAIGHT legs. Animate=false → SdoAvatar.Pose uses hrc.LocalRest.
            var idle = bindPoseNoIdle ? null : LoadMot(IdleMot);
            var av = parent.AddComponent<SdoAvatar>();
            av.Setup(hrc, idle);
            av.SetBodyShape(SdoBodyShape.WeightFromIndex(0, false));   // default thin female (matches the dancer)
            if (bindPoseNoIdle) { av.Animate = false; }
            else { av.RestMot = idle; av.BlendSec = 0f; }   // no idle↔walk crossfade — start walking immediately

            // Load the body/garment parts via the shared builder (same loop the in-game dancer + head portrait use).
            var built = SdoAvatarBuilder.LoadParts(parent, av, parts ?? WomanParts,
                portraitOpaque ? SdoAvatarBuilder.SkinStyle.Portrait : SdoAvatarBuilder.SkinStyle.Gameplay);
            if (built.Parts == 0) { Debug.LogWarning("[room-avatar] no parts loaded"); Object.Destroy(av); return null; }

            if (bindPoseNoIdle) av.PoseFrame(0f);   // skin the bind pose now (retargets T-pose-authored garments onto the mannequin)
            else av.PoseInitialIdle();              // arm the idle so the first frame isn't the bind/T-pose
            SetLayerRecursive(parent, layer);
            return av;
        }

        private static string[] NormalizeParts(string[] parts, bool male)
        {
            var defaults = DefaultParts(male);
            var res = new string[defaults.Length];
            for (int i = 0; i < res.Length; i++)
            {
                string rel = parts != null && i < parts.Length ? parts[i] : null;
                res[i] = string.IsNullOrEmpty(rel) ? defaults[i] : NormalizeRel(rel);
            }
            return res;
        }

        private static string NormalizeRel(string rel)
        {
            rel = (rel ?? "").Trim().Replace('\\', '/');
            if (rel.Length == 0) return rel;
            if (rel.IndexOf('/') < 0) rel = "AVATAR/" + rel;
            if (!rel.EndsWith(".MSH", System.StringComparison.OrdinalIgnoreCase)) rel += ".MSH";
            return rel;
        }

        // Resolve an avatar DDS by name within its folder (mirror of ScreenGameplay.ResolveDds: exact name first, then a
        // case-insensitive stem match), decoded via DdsLoader.
        private static Texture2D ResolveDds(string dir, string ddsName)
        {
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
            try { return DdsLoader.Load(File.ReadAllBytes(hit)); }
            catch { return null; }
        }

        private static Color PartColor(string rel)
        {
            string u = rel.ToUpperInvariant();
            if (u.Contains("HAIR")) return new Color(0.30f, 0.20f, 0.12f);
            if (u.Contains("FACE") || u.Contains("HAND")) return new Color(0.95f, 0.80f, 0.70f);
            if (u.Contains("COAT")) return new Color(0.35f, 0.45f, 0.70f);
            if (u.Contains("PANT")) return new Color(0.70f, 0.25f, 0.30f);
            return new Color(0.6f, 0.6f, 0.65f);
        }

        public static MotLoader LoadMot(string rel)
        {
            try
            {
                var path = Path.Combine(SdoExtracted.Root, rel.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(path) ? MotLoader.Load(File.ReadAllBytes(path)) : null;
            }
            catch { return null; }
        }

        public static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform c in go.transform) SetLayerRecursive(c.gameObject, layer);
        }
    }
}
