using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// The waiting-room walkable/furniture-collision mask (SCNCHIRSROOM/MASK.MSK), reverse-engineered from the
    /// decompiled walk-collision path (Player_HitTestLane_004ab9e0 case 6 "ScnRoom" + Bitset_Test_0041f1b0). It is a
    /// 1-bit-per-cell occupancy grid: 748×525 = 392700 cells packed into 12272 u32 words (LSB-first). The world→cell
    /// transform (cell = 1 SDO unit) is the game's case-6 ScnRoom mapping — validated by overlaying the decoded
    /// walkable polygon onto SCNCHIRSROOM/SCENE.MSH's floor geometry (they coincide exactly):
    ///   ix = round(worldX + 444.4)  ∈ [0,748)   (X column)
    ///   iz = round(262.3 − worldZ)  ∈ [0,525)   (Z row, Z-flipped)
    ///   walkable = bit (iz*748 + ix) of the packed mask.
    ///
    /// Note: the offline EXE actually loads the room as scene 0x27 (ScnRoom_Night), whose HitTestLane branch is
    /// default→false, so the shipped offline build does NOT sample the mask (it free-walks the camera box). This data
    /// IS the daytime ScnRoom (0x25/0x26) walkable map for the same geometry; the remake decodes + samples it to give
    /// real furniture collision (the user's goal). Pure logic (only UnityEngine.Mathf), unit-tested against the file.
    /// </summary>
    public sealed class RoomMask
    {
        public const int Width = 748;        // X extent (cells), = row stride
        public const int Height = 525;       // Z extent (cells)
        public const float OriginX = 444.4f; // worldX + OriginX = column (ix)
        public const float OriginZ = 262.3f; // OriginZ − worldZ = row (iz)  (Z axis flipped)

        public readonly int CellCount;       // header[0] = 392700 = Width*Height
        public readonly int WordCount;       // header[1] = 12272 = ceil(CellCount/32)
        private readonly uint[] _words;

        private RoomMask(int cellCount, int wordCount, uint[] words)
        {
            CellCount = cellCount; WordCount = wordCount; _words = words;
        }

        /// <summary>Parse a MASK.MSK buffer. Returns null if the header/size is invalid. PURE (byte[] -> mask).</summary>
        public static RoomMask Parse(byte[] d)
        {
            if (d == null || d.Length < 12) return null;
            int cellCount = (int)U(d, 0);
            int wordCount = (int)U(d, 4);
            // header[2] (d@8) = auxiliary/size field, unused by the walk test.
            if (wordCount <= 0 || wordCount > (1 << 24)) return null;
            if (cellCount <= 0 || cellCount > wordCount * 32) return null;
            long need = 12L + (long)wordCount * 4;
            if (need > d.Length) return null;
            var words = new uint[wordCount];
            for (int i = 0; i < wordCount; i++) words[i] = U(d, 12 + i * 4);
            return new RoomMask(cellCount, wordCount, words);
        }

        /// <summary>Test the raw packed bit at a cell index (word[idx>>5] &amp; (1 &lt;&lt; (idx&amp;31))).</summary>
        public bool TestBit(int idx)
        {
            if (idx < 0 || idx >= CellCount) return false;
            return (_words[idx >> 5] & (1u << (idx & 31))) != 0u;
        }

        /// <summary>Cell column/row for a world XZ (no bounds check). Exposed for tests/tools.</summary>
        public static int ColumnX(float worldX) => Mathf.RoundToInt(worldX + OriginX);
        public static int RowZ(float worldZ) => Mathf.RoundToInt(OriginZ - worldZ);

        /// <summary>Is the floor walkable at this world XZ? False outside the grid (= blocked, like a wall).</summary>
        public bool IsWalkable(float worldX, float worldZ)
        {
            int ix = ColumnX(worldX);
            if (ix < 0 || ix >= Width) return false;
            int iz = RowZ(worldZ);
            if (iz < 0 || iz >= Height) return false;
            return TestBit(iz * Width + ix);
        }

        /// <summary>Count of walkable cells (decode sanity / tests). 59692 for the shipped SCNCHIRSROOM mask.</summary>
        public int WalkableCount()
        {
            int n = 0;
            for (int idx = 0; idx < CellCount; idx++)
                if ((_words[idx >> 5] & (1u << (idx & 31))) != 0u) n++;
            return n;
        }

        /// <summary>World-space centroid of the walkable region (a safe default spawn that is on the floor, not the
        /// raised dais). Returns false if the mask has no walkable cell.</summary>
        public bool TryWalkableCentroid(out Vector3 world)
        {
            double sx = 0, sz = 0; long n = 0;
            for (int idx = 0; idx < CellCount; idx++)
            {
                if ((_words[idx >> 5] & (1u << (idx & 31))) == 0u) continue;
                int iz = idx / Width, ix = idx % Width;
                sx += ix - OriginX;          // invert ColumnX
                sz += OriginZ - iz;          // invert RowZ
                n++;
            }
            if (n == 0) { world = Vector3.zero; return false; }
            world = new Vector3((float)(sx / n), 0f, (float)(sz / n));
            return true;
        }

        private static uint U(byte[] d, int o) => (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
    }
}
