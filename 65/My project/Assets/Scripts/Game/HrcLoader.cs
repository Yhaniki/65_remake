using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Loads an SDO ".hrc" skeleton ("Hierachy0020": 16-byte header + bone_count, then 112 bytes/bone:
    /// 4x4 rest matrix (row-major, row-vector) | bone_id | subtree_next | "Reserved    " | char[28] name).
    /// Parents are derived from 3ds-Max Biped naming (the file's subtree_next is not enough). Verified in
    /// Python: bind FK gives a clean T-pose human (head ~y51, hands ±20, feet at ground). Conversion to
    /// Unity (column-vector, X negated to match MshLoader): L = X * Rᵀ * X.  See MOT_HRC_FORMAT.md.
    /// </summary>
    public sealed class HrcLoader
    {
        public string[] Names;
        public int[] Parent;
        public Matrix4x4[] RawRest;       // per-bone rest matrix as stored (D3D row-major, row-vector)
        public Matrix4x4[] LocalRest;     // per-bone local (Unity space)
        public Matrix4x4[] BindWorld;     // bind-pose world
        public Matrix4x4[] InvBindWorld;  // inverse of BindWorld (for skinning)
        public Dictionary<string, int> Index;

        public static HrcLoader Load(byte[] d)
        {
            if (d == null || d.Length < 16) return null;
            if (System.Text.Encoding.ASCII.GetString(d, 0, 8) != "Hierachy") return null;
            int bc = BitConverter.ToInt32(d, 12);
            if (bc <= 0 || 16 + bc * 112 > d.Length) return null;

            var h = new HrcLoader
            {
                Names = new string[bc], Parent = new int[bc], RawRest = new Matrix4x4[bc],
                LocalRest = new Matrix4x4[bc], BindWorld = new Matrix4x4[bc], InvBindWorld = new Matrix4x4[bc],
                Index = new Dictionary<string, int>()
            };
            var rest = h.RawRest;
            var childLink = new int[bc]; var sibLink = new int[bc];   // HRC first_child / next_sibling links (+64/+68)
            for (int i = 0; i < bc; i++)
            {
                int o = 16 + i * 112;
                var R = new Matrix4x4();
                for (int r = 0; r < 4; r++)
                    for (int c = 0; c < 4; c++)
                        R[r, c] = BitConverter.ToSingle(d, o + (r * 4 + c) * 4);   // row-major as stored
                rest[i] = R;
                childLink[i] = BitConverter.ToInt32(d, o + 64);
                sibLink[i] = BitConverter.ToInt32(d, o + 68);
                int n = 0; while (n < 28 && d[o + 84 + n] != 0) n++;
                h.Names[i] = System.Text.Encoding.ASCII.GetString(d, o + 84, n);
                if (!h.Index.ContainsKey(h.Names[i])) h.Index[h.Names[i]] = i;
                h.LocalRest[i] = rest[i].transpose;             // row-major -> column-vector (reference: raw.reshape(4,4).T)
            }
            // Parents from the HRC's OWN child/next_sibling links (reference _parents_from_hrc_links) — these put the
            // CLAVICLE under the NECK, which Biped naming gets wrong (it would say Spine). Biped naming is the fallback.
            for (int i = 0; i < bc; i++) h.Parent[i] = -1;
            var seen = new bool[bc];
            void Visit(int parentIdx, int childIdx)
            {
                while (childIdx > 0 && childIdx < bc && !seen[childIdx])   // link value 0 = null
                {
                    seen[childIdx] = true;
                    h.Parent[childIdx] = parentIdx;
                    Visit(childIdx, childLink[childIdx]);
                    childIdx = sibLink[childIdx];
                }
            }
            Visit(0, childLink[0]);   // bone 0 = root
            for (int i = 1; i < bc; i++)   // any bone with no link -> Biped naming fallback
                if (h.Parent[i] < 0)
                {
                    string pn = ParentName(h.Names[i], h.Index);
                    h.Parent[i] = (pn != null && h.Index.TryGetValue(pn, out int pi)) ? pi : -1;
                }
            for (int i = 0; i < bc; i++)
            {
                h.BindWorld[i] = h.Parent[i] < 0 ? h.LocalRest[i] : h.BindWorld[h.Parent[i]] * h.LocalRest[i];
                h.InvBindWorld[i] = h.BindWorld[i].inverse;
            }
            return h;
        }

        /// <summary>Convert a (row-major, row-vector) D3D matrix to Unity column-vector: just transpose. D3D9 and
        /// Unity are BOTH left-handed, so NO axis negation — an X-flip here mirrors the model left-right (reference
        /// mot_player.read_hrc / mesh_skin do only raw.reshape(4,4).T).</summary>
        public static Matrix4x4 ToUnityLocal(Matrix4x4 rowVectorD3d) => rowVectorD3d.transpose;

        private static string Highest(string prefix, Dictionary<string, int> idx)
        {
            for (int k = 5; k >= 1; k--) if (idx.ContainsKey(prefix + k)) return prefix + k;
            return idx.ContainsKey(prefix) ? prefix : null;
        }

        private static string ParentName(string n, Dictionary<string, int> idx)
        {
            if (n == "Bip01") return null;
            if (n == "Bip01_Pelvis") return "Bip01";
            if (n == "Bip01_Spine") return "Bip01_Pelvis";
            var m = Regex.Match(n, @"^Bip01_Spine(\d+)$");
            if (m.Success) { int k = int.Parse(m.Groups[1].Value); return k > 1 ? "Bip01_Spine" + (k - 1) : "Bip01_Spine"; }
            string st = Highest("Bip01_Spine", idx) ?? "Bip01_Spine";
            if (n == "Bip01_Neck") return st;
            m = Regex.Match(n, @"^Bip01_Neck(\d+)$");
            if (m.Success) { int k = int.Parse(m.Groups[1].Value); return k > 1 ? "Bip01_Neck" + (k - 1) : "Bip01_Neck"; }
            if (n == "Bip01_Head") return idx.ContainsKey("Bip01_Neck") ? (Highest("Bip01_Neck", idx) ?? "Bip01_Neck") : st;
            foreach (var s in new[] { "L", "R" })
            {
                if (n == $"Bip01_{s}_Thigh") return "Bip01_Pelvis";
                if (n == $"Bip01_{s}_Calf") return $"Bip01_{s}_Thigh";
                if (n == $"Bip01_{s}_Foot") return $"Bip01_{s}_Calf";
                if (n == $"Bip01_{s}_Toe0") return $"Bip01_{s}_Foot";
                if (n == $"Bip01_{s}_Clavicle") return st;
                if (n == $"Bip01_{s}_UpperArm") return $"Bip01_{s}_Clavicle";
                if (n == $"Bip01_{s}_Forearm") return $"Bip01_{s}_UpperArm";
                if (n == $"Bip01_{s}_Hand") return $"Bip01_{s}_Forearm";
                m = Regex.Match(n, $@"^Bip01_{s}_Finger(\d)(\d?)$");
                if (m.Success)
                {
                    string dd = m.Groups[1].Value, sg = m.Groups[2].Value;
                    return sg == "" ? $"Bip01_{s}_Hand" : (sg == "1" ? $"Bip01_{s}_Finger{dd}" : $"Bip01_{s}_Finger{dd}1");
                }
            }
            m = Regex.Match(n, @"^Bip01_Ponytail(\d)(\d?)$");
            if (m.Success)
            {
                string a = m.Groups[1].Value, b = m.Groups[2].Value;
                return b == "" ? "Bip01_Head" : (b == "1" ? $"Bip01_Ponytail{a}" : $"Bip01_Ponytail{a}1");
            }
            return "Bip01";
        }
    }
}
