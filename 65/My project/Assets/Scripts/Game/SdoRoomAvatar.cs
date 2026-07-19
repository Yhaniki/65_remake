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
        /// false → <see cref="RenderMode.Scene"/>. New callers should pass a <see cref="RenderMode"/> directly.
        /// <paramref name="bodyIndex"/> = 這個角色自己的體型 (胖瘦) index 0..4 (預設 0=瘦;由 UI 層從 profile.json 帶入)。</summary>
        public static SdoAvatar Build(GameObject parent, int layer, bool portraitOpaque, bool male, string[] equippedParts, int bodyIndex = 0)
            => Build(parent, layer, portraitOpaque ? RenderMode.PortraitHead : RenderMode.Scene, male, equippedParts, bodyIndex);

        public static SdoAvatar Build(GameObject parent, int layer, RenderMode mode, bool male = false, string[] equippedParts = null, int bodyIndex = 0)
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
            av.SetBodyShape(SdoBodyShape.WeightFromIndex(bodyIndex, male));   // 這個角色自己的體型 (胖瘦;預設 0=瘦,male/female baseline)
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
                // 髮/眼鏡/翅膀/項鍊都要雙面+alpha-cutout(去背),否則翅膀/眼鏡鏤空處變實心。其餘走 Unlit/Texture。
                string ru = rel.ToUpperInvariant();
                bool twoSidedAlpha = ru.Contains("HAIR") || ru.Contains("GLASS") || ru.Contains("CHIBANG") || ru.Contains("LINGDANG");
                // 布料(非 髮/眼鏡/翅膀/項鍊)一律 bodyShader (Unlit/Texture,UNITY_OPAQUE_ALPHA 逼 alpha=1) → 破 alpha 布料
                // 在透空 RT 也畫成實心,不被 portrait cutout 裁成透明線框(璀璨繁星 褲子 在男女選單/儲物櫃)。只有 twoSidedAlpha
                // (髮/眼鏡/翅膀/項鍊,鏤空去背)才需 cutout:RT 用 portrait、場景用 hair。
                var sh = twoSidedAlpha ? (useCutout ? portraitShader : hairShader) : bodyShader;
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
                            // 翅膀(CHIBANG)的發光羽翼常是 model-embedded 換幀貼圖:材質名是佔位符 "_TexAnimEx(NAME)…"，
                            // 找不到真檔 → 交給共用的 TryBuildTexAnim 解出 <NAME>.an 幀序列並掛動畫(與遊戲內舞者/商城同一套),
                            // 否則房間/選男女的翅膀會變一坨灰色(user 回報 8448 貼圖寫不出來)。仍解不出才退 fallback 色。
                            Material texAnim = t == null ? SdoAvatarBuilder.TryBuildTexAnim(go, dir, nm, sh) : null;
                            if (t == null && texAnim == null && !string.IsNullOrEmpty(nm)) Debug.LogWarning($"[avtex] item='{SdoAvatarBuilder.LogLabel}' {rel}: material '{nm}' unresolved → fallback colour {PartColor(rel)}");
                            mats[s] = t != null ? new Material(sh) { mainTexture = t } : (texAnim ?? new Material(fallback) { color = PartColor(rel), name = nm ?? "" });
                        }
                        mr.sharedMaterials = mats;
                    }
                    else
                    {
                        var tex = ResolveDds(dir, sub.Dds);
                        // 見上:翅膀 _TexAnimEx 換幀貼圖 → 交給共用 TryBuildTexAnim(解 .an 幀序列)否則變灰色。
                        Material texAnim = tex == null ? SdoAvatarBuilder.TryBuildTexAnim(go, dir, sub.Dds, sh) : null;
                        if (tex == null && texAnim == null && !string.IsNullOrEmpty(sub.Dds)) Debug.LogWarning($"[avtex] item='{SdoAvatarBuilder.LogLabel}' {rel}: material '{sub.Dds}' unresolved → fallback colour {PartColor(rel)}");
                        mr.sharedMaterial = tex != null ? new Material(sh) { mainTexture = tex } : (texAnim ?? new Material(fallback) { color = PartColor(rel), name = sub.Dds ?? "" });
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
                                      bool bindPoseNoIdle = false, float bodyWeight = 1f)
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
            // 服裝預覽的體型 B：卡片縮圖用預設「正常身材」(bodyWeight=1.0，對任何骨頭都不縮放)；左側「玩家假人」則由呼叫端
            // 帶入玩家自己的體型 (胖瘦) → 換衣服在玩家實際身材上預覽 (user)。
            av.SetBodyShape(bodyWeight);
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
            // 空 → 預設整套。否則「原樣保留全部非空部位」(含飾品/翅膀/眼鏡,index≥6)——舊版只留前 6 個核心部位,把飾品/翅膀
            // 截掉了 → 房間看不到眼鏡/翅膀 (使用者回報)。equippedParts 由 AvatarOutfit.ResolveParts 產生時已含核心預設,故不需再補。
            if (parts == null || parts.Length == 0) return DefaultParts(male);
            var res = new System.Collections.Generic.List<string>(parts.Length);
            foreach (var rel in parts)
                if (!string.IsNullOrEmpty(rel)) res.Add(NormalizeRel(rel));
            return res.Count > 0 ? res.ToArray() : DefaultParts(male);
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
            // Fuzzy fallback (shared with SdoAvatarBuilder): tolerate mesh material-name variants — 'huan_1'→'huan1',
            // 'haun0'→'huan0', 'M_Basic_face01'→'M_Basic_face' — that the strict match misses (→ white faces / fallbacks).
            if (hit == null) hit = SdoAvatarBuilder.FuzzyFindDds(dir, Path.GetFileNameWithoutExtension(name));
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
