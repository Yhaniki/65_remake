using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Loads the waiting-room's stage props (Room_obj mapobjs), faithful to the decompiled Scene_LoadBackground
    /// case 0x25 (ScnRoom, id 37): <b>dianshi</b> (the wall TV, 21-frame screen animation), <b>laba1-4</b> (animated
    /// speakers), <b>guang1-8</b> (the "DENGDAI" waiting lights) and <b>taizi</b> (the tiered dais the six dancers sit
    /// on). Every instance is placed at the ORIGIN — the EXE's per-instance position table (DAT_00678408) is all-zero
    /// BSS, so the world coordinates are baked into each mesh's geometry and nothing is recentred.
    ///
    /// All Room_obj props are RIGID no-weight meshes (FVF 0x112, no skin): a static prop bakes its HRC leaf bone's
    /// bind-world into the verts once; an animated prop (laba) rides its leaf bone each frame, driven by one SdoAvatar
    /// whose looping .mot plays the FK. This is the room-scoped twin of ScreenGameplay.AddMapobj's rigid path — kept
    /// separate so the validated gameplay loader is never disturbed — reusing the same low-level loaders, the SdoAvatar
    /// bone baker and the MapobjTexAnimator. Created/destroyed by RoomScene3D.
    /// </summary>
    public sealed class RoomMapobjs : MonoBehaviour
    {
        public int layer = RoomScene3D.SceneLayer;

        // One prop group: a sub-folder under SCENE/MAPOBJ/ROOM_OBJ, its mesh/skeleton/optional motion, and an optional
        // texture-frame animation (frame-name prefix + count + interval ms) for screen-style props (the dianshi TV).
        private struct Group
        {
            public readonly string Folder, Msh, Hrc, Mot, TexPrefix;
            public readonly int TexCount; public readonly float TexMs;
            public Group(string folder, string msh, string hrc, string mot, string texPrefix = null, int texCount = 0, float texMs = 0f)
            { Folder = folder; Msh = msh; Hrc = hrc; Mot = mot; TexPrefix = texPrefix; TexCount = texCount; TexMs = texMs; }
        }

        // ScnRoom (id 37) Room_obj set, verbatim from the decompiled case-0x25 mapobj pointer table
        // (PTR_s_Datas_scene_Mapobj_Room_obj_*). dianshi cycles ROOMOBJ_TVDH00001..00021 on its screen.
        // NOTE: the table has 14 entries — the 14th, TAIZI (the round dais), is DETACHED by Scene_LoadBackground
        // (030_scene:3250: `if (DAT_00674f04+0xc != 6 && idx==0xd) Scene_DetachChild(...)`) in the normal open room, so
        // the floor stays clear (the official has no centre disc). We simply don't load it (only +0xc==6 keeps it).
        private static readonly Group[] ScnRoomGroups =
        {
            new Group("DIANSHI", "TVDH.MSH",    "TVDH.HRC",    null, "ROOMOBJ_TVDH", 21, 80f),
            new Group("LABA1",   "LABADH1.MSH", "LABADH1.HRC", "LABADH1.MOT"),
            new Group("LABA2",   "LABADH2.MSH", "LABADH2.HRC", "LABADH2.MOT"),
            new Group("LABA3",   "LABADH3.MSH", "LABADH3.HRC", "LABADH3.MOT"),
            new Group("LABA4",   "LABADH4.MSH", "LABADH4.HRC", "LABADH4.MOT"),
            new Group("GUANG1",  "GUANG1.MSH",  "GUANG1.HRC",  null),
            new Group("GUANG2",  "GUANG2.MSH",  "GUANG2.HRC",  null),
            new Group("GUANG3",  "GUANG3.MSH",  "GUANG3.HRC",  null),
            new Group("GUANG4",  "GUANG4.MSH",  "GUANG4.HRC",  null),
            new Group("GUANG5",  "GUANG5.MSH",  "GUANG5.HRC",  null),
            new Group("GUANG6",  "GUANG6.MSH",  "GUANG6.HRC",  null),
            new Group("GUANG7",  "GUANG7.MSH",  "GUANG7.HRC",  null),
            new Group("GUANG8",  "GUANG8.MSH",  "GUANG8.HRC",  null),
            // TAIZI (round dais, table index 13) intentionally omitted — detached in the normal room (see note above).
        };

        private const string RoomObjRel = "SCENE/MAPOBJ/ROOM_OBJ";

        /// <summary>Load the ScnRoom day set (the only one wired so far; ScnMerryRoom drops taizi, ScnRoom_Night uses a
        /// different Room_night_obj set).</summary>
        public void BuildScnRoom()
        {
            foreach (var g in ScnRoomGroups) BuildGroup(g);
        }

        private void BuildGroup(Group g)
        {
            var dir = Path.Combine(SdoExtracted.Root, RoomObjRel.Replace('/', Path.DirectorySeparatorChar), g.Folder);
            var mshPath = Path.Combine(dir, g.Msh);
            if (!File.Exists(mshPath)) { Debug.LogWarning("[room-mapobj] missing " + mshPath); return; }
            MshLoader.Result r;
            try { r = MshLoader.Load(File.ReadAllBytes(mshPath)); }
            catch (System.Exception e) { Debug.LogWarning("[room-mapobj] msh fail " + g.Folder + ": " + e.Message); return; }
            if (r == null || r.Submeshes.Count == 0) { Debug.LogWarning("[room-mapobj] parse fail " + g.Folder); return; }

            HrcLoader hrc = null;
            var hrcPath = Path.Combine(dir, g.Hrc);
            if (File.Exists(hrcPath)) { try { hrc = HrcLoader.Load(File.ReadAllBytes(hrcPath)); } catch { } }
            MotLoader mot = null;
            if (!string.IsNullOrEmpty(g.Mot))
            {
                var motPath = Path.Combine(dir, g.Mot);
                if (File.Exists(motPath)) { try { mot = MotLoader.Load(File.ReadAllBytes(motPath)); } catch { } }
            }

            // one shared material per submesh (read-only; the renderer shares it)
            var mats = new Material[r.Submeshes.Count];
            for (int s = 0; s < r.Submeshes.Count; s++) mats[s] = BuildMaterial(dir, r.Submeshes[s].Dds);

            // rigid no-weight prop iff the HRC carries a bind-world and no submesh ships per-vertex weights.
            bool rigid = hrc != null && hrc.BindWorld != null;
            if (rigid) foreach (var sub in r.Submeshes) if (sub.BoneHrc != null) { rigid = false; break; }
            int[] leaves = rigid ? HrcLeafBones(hrc) : System.Array.Empty<int>();
            bool animated = hrc != null && mot != null;

            var root = new GameObject(g.Folder);
            root.transform.SetParent(transform, false);   // instance transform = identity (origin, scale 1)

            if (rigid && animated && leaves.Length > 0)
            {
                // ANIMATED rigid prop (laba speakers): the mesh rides its leaf bone each frame so the .mot plays
                // without per-vertex skinning. One SdoAvatar drives the bone FK; a per-submesh baked clone carries the
                // mesh. (Same mechanism as the gameplay SEA_SCREEN / DING wheel.)
                var avatar = root.AddComponent<SdoAvatar>();
                avatar.Setup(hrc, mot);
                for (int s = 0; s < r.Submeshes.Count; s++)
                {
                    int bone = leaves[Mathf.Min(s, leaves.Length - 1)];
                    var srcMesh = r.Submeshes[s].Mesh;
                    var src = srcMesh.vertices;
                    var bakeMesh = Object.Instantiate(srcMesh);
                    bakeMesh.name = g.Folder + "_bake" + s;
                    AddMeshChild(root.transform, g.Folder + "_mesh" + s, bakeMesh, mats[s]);
                    avatar.AddBoneMeshBaker(bone, bakeMesh, src, true);
                }
            }
            else
            {
                // STATIC prop (taizi / guang / dianshi screen): bake each submesh's leaf-bone bind-world into its verts
                // once (the prop is authored in bone-local space — usually a 3ds-Max Z-up leaf that stands it up), then
                // render frozen. If the bind is identity the verts already carry world coords (no-op).
                for (int s = 0; s < r.Submeshes.Count; s++)
                {
                    var mesh = r.Submeshes[s].Mesh;
                    if (rigid && leaves.Length > 0)
                    {
                        Matrix4x4 m = hrc.BindWorld[leaves[Mathf.Min(s, leaves.Length - 1)]];
                        if (!m.isIdentity)
                        {
                            var vts = mesh.vertices;
                            for (int i = 0; i < vts.Length; i++) vts[i] = m.MultiplyPoint3x4(vts[i]);
                            mesh.vertices = vts; mesh.RecalculateBounds();
                        }
                    }
                    AddMeshChild(root.transform, g.Folder + "_mesh" + s, mesh, mats[s]);
                }
            }

            // dianshi screen: cycle the TVDH frame sequence over the shared materials (faithful to the original's
            // per-frame texture swap). The geometry stays frozen; only the bound texture changes.
            if (!string.IsNullOrEmpty(g.TexPrefix) && g.TexCount > 0)
            {
                var frames = GatherFrames(dir, g.TexPrefix, g.TexCount);
                if (frames.Count > 0)
                {
                    foreach (var m in mats) if (m != null) m.color = Color.white;   // show true frame colour, untinted
                    root.AddComponent<MapobjTexAnimator>().Init(mats, frames.ToArray(), g.TexMs > 0f ? g.TexMs : 80f);
                }
            }

            SetLayerRecursive(root, layer);
            Debug.Log($"[room-mapobj] {g.Folder}: subs={r.Submeshes.Count} {(animated ? "animated" : "static")} rigid={rigid} leaves={leaves.Length}");
        }

        // ---- helpers (room-scoped twins of the gameplay loader's; kept local so gameplay isn't touched) ----

        private static void AddMeshChild(Transform parent, string name, Mesh mesh, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().mesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        }

        private static Material BuildMaterial(string dir, string ddsName)
        {
            Texture2D tex = null; DdsAlphaMode mode = DdsAlphaMode.Opaque;
            var hit = ResolveDdsPath(dir, ddsName);
            if (hit != null)
            {
                try { var bytes = File.ReadAllBytes(hit); tex = DdsLoader.Load(bytes); mode = DdsLoader.GetAlphaMode(bytes); }
                catch { }
            }
            string shaderName = mode == DdsAlphaMode.Blend ? "Unlit/Transparent"
                              : mode == DdsAlphaMode.Cutout ? "Unlit/Transparent Cutout"
                              : "Unlit/Texture";
            var shader = Shader.Find(shaderName) ?? Shader.Find("Unlit/Texture");
            var m = new Material(shader);
            if (tex != null) m.mainTexture = tex; else m.color = new Color(0.6f, 0.6f, 0.65f);
            return m;
        }

        // exact filename first, then a case-insensitive stem match within the prop folder (mirror of SdoRoomAvatar).
        private static string ResolveDdsPath(string dir, string ddsName)
        {
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(ddsName)) return null;
            string name = Path.GetFileName(ddsName.Replace('\\', '/'));
            string direct = Path.Combine(dir, name);
            if (File.Exists(direct)) return direct;
            string stem = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
            foreach (var f in Directory.GetFiles(dir, "*.*"))
                if (Path.GetExtension(f).ToLowerInvariant() == ".dds" && Path.GetFileNameWithoutExtension(f).ToLowerInvariant() == stem)
                    return f;
            return null;
        }

        // Collect a numbered DDS frame sequence "<prefix>NNNNN.dds" in the folder, sorted by the trailing integer (so
        // ...00009 precedes ...000010 regardless of zero-padding). Caps at <count>.
        private static List<Texture2D> GatherFrames(string dir, string prefix, int count)
        {
            var hits = new List<(int n, string path)>();
            string pfx = prefix.ToLowerInvariant();
            foreach (var f in Directory.GetFiles(dir, "*.dds"))
            {
                string stem = Path.GetFileNameWithoutExtension(f);
                if (!stem.ToLowerInvariant().StartsWith(pfx)) continue;
                int i = stem.Length; while (i > 0 && char.IsDigit(stem[i - 1])) i--;
                if (i >= stem.Length || !int.TryParse(stem.Substring(i), out int n)) continue;
                hits.Add((n, f));
            }
            hits.Sort((a, b) => a.n.CompareTo(b.n));
            var frames = new List<Texture2D>();
            foreach (var h in hits)
            {
                if (frames.Count >= count) break;
                try { var t = DdsLoader.Load(File.ReadAllBytes(h.path)); if (t != null) frames.Add(t); } catch { }
            }
            return frames;
        }

        private static int[] HrcLeafBones(HrcLoader hrc)
        {
            if (hrc == null || hrc.Names == null) return System.Array.Empty<int>();
            int bc = hrc.Names.Length;
            var hasChild = new bool[bc];
            for (int i = 0; i < bc; i++) { int p = hrc.Parent[i]; if (p >= 0 && p < bc) hasChild[p] = true; }
            var leaves = new List<int>();
            for (int i = 0; i < bc; i++) if (!hasChild[i]) leaves.Add(i);
            return leaves.ToArray();
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform c in go.transform) SetLayerRecursive(c.gameObject, layer);
        }
    }
}
