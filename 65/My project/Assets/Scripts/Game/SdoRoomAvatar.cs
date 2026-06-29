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

        /// <summary>
        /// Build the avatar onto <paramref name="parent"/>: load FEMALE.HRC, the 6 WOMAN parts and the idle clip, set
        /// the default (thin) body shape, arm the idle pose, and put everything on <paramref name="layer"/>. When
        /// <paramref name="portraitOpaque"/> the parts use the Sdo/PortraitOpaque shader (clean opaque head for the
        /// portrait RT); otherwise normal Unlit/Texture + two-sided hair, with the 2-material COAT/PANT skin submeshes
        /// resolved per-range (so arms/legs aren't painted with cloth). Returns the SdoAvatar, or null if the skeleton
        /// or every part failed to load.
        /// </summary>
        public static SdoAvatar Build(GameObject parent, int layer, bool portraitOpaque)
        {
            string root = SdoExtracted.Root;
            string hrcPath = Path.Combine(root, FemaleHrc.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(hrcPath)) { Debug.LogWarning("[room-avatar] missing " + hrcPath); return null; }
            var hrc = HrcLoader.Load(File.ReadAllBytes(hrcPath));
            if (hrc == null) { Debug.LogWarning("[room-avatar] HRC parse fail"); return null; }

            var idle = LoadMot(IdleMot);
            var av = parent.AddComponent<SdoAvatar>();
            av.Setup(hrc, idle);
            av.SetBodyShape(SdoBodyShape.WeightFromIndex(0, false));   // default thin female (matches the dancer)
            av.RestMot = idle;
            av.BlendSec = 0f;   // no idle↔walk crossfade in the lobby — start walking immediately (no 1s smooth)

            var bodyShader = Shader.Find("Unlit/Texture");
            var hairShader = Shader.Find("Sdo/UnlitDoubleSided") ?? bodyShader;
            var portraitShader = Shader.Find("Sdo/PortraitOpaque") ?? bodyShader;
            var fallback = Shader.Find("Unlit/Color");

            int parts = 0;
            foreach (var rel in WomanParts)
            {
                var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) { Debug.LogWarning("[room-avatar] missing " + rel); continue; }
                var r = MshLoader.Load(File.ReadAllBytes(path));
                if (r == null || r.Submeshes.Count == 0) { Debug.LogWarning("[room-avatar] parse fail " + rel); continue; }
                var dir = Path.GetDirectoryName(path);
                bool hair = rel.ToUpperInvariant().Contains("HAIR");
                var sh = portraitOpaque ? portraitShader : (hair ? hairShader : bodyShader);
                int si = 0;
                foreach (var sub in r.Submeshes)
                {
                    var go = new GameObject(Path.GetFileNameWithoutExtension(rel) + "_" + si++);
                    go.transform.SetParent(parent.transform, false);
                    go.AddComponent<MeshFilter>().mesh = sub.Mesh;
                    var mr = go.AddComponent<MeshRenderer>();

                    // 2-material skin submeshes (COAT/PANT): cloth range -> garment DDS, skin range -> shared W_Basic DDS.
                    // Only meaningful for the full-body avatar; the head portrait never shows them, so keep it single.
                    if (!portraitOpaque && sub.Ranges != null && sub.Ranges.Count > 1 && sub.Mesh.subMeshCount == sub.Ranges.Count)
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

        public static MotLoader LoadMot(string rel)
        {
            try
            {
                var path = Path.Combine(SdoExtracted.Root, rel.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(path) ? MotLoader.Load(File.ReadAllBytes(path)) : null;
            }
            catch { return null; }
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

        public static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform c in go.transform) SetLayerRecursive(c.gameObject, layer);
        }
    }
}
