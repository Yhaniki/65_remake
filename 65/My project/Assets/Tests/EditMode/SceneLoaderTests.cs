using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// Covers SceneLoader.ParseBlocks — the SCENE.MSH block splitter. The bug it guards against: SCN0026 is the
    /// one shipped multi-block scene (35 blocks); the old loader read only block 0 and the court rendered black.
    /// These build synthetic buffers in the exact on-disk layout (verified against the decompiled
    /// Model_loadFromData_0041a7e0) so the block-boundary maths is exercised without Unity / game data.
    /// </summary>
    public class SceneLoaderTests
    {
        // One type-2 mesh block: header [fvf,idxBytes,opt] + u16 indices + [vertBytes,stride] + verts + 24-byte
        // bbox + nMat + 408-byte material record + footer + the type-2 tail (per-object floats, u16 index list,
        // 64-byte node tree). count == numMat == 1; footerSize = count*28 (24 record + 4 tail-list).
        private static byte[] MakeType2Block(int faces, int verts, string ddsName, int nTailIdx, int nNodes)
        {
            const int stride = 24;
            var b = new List<byte>();
            void U32(int v) => b.AddRange(BitConverter.GetBytes(v));
            void Pad(int n) => b.AddRange(new byte[n]);

            U32(0x142);                 // fvf
            U32(faces * 3 * 2);         // idxBytes
            U32(0x65);                  // opt
            for (int i = 0; i < faces * 3; i++) { b.Add((byte)(i & 0xff)); b.Add((byte)((i >> 8) & 0xff)); } // u16 indices
            U32(verts * stride);        // vertBytes
            U32(stride);                // stride
            Pad(verts * stride);        // vertex buffer (content irrelevant to ParseBlocks)
            Pad(24);                    // bbox
            U32(1);                     // nMat
            var mat = new byte[408];
            var nm = System.Text.Encoding.ASCII.GetBytes(ddsName);
            Array.Copy(nm, 0, mat, 17 * 4, nm.Length);   // name lives at record offset 68
            b.AddRange(mat);
            // footer header [type, _, footerSize, count]
            U32(2); U32(0); U32(1 * 28); U32(1);
            // count*24-byte record [matId, faceStart, faceCount, vertStart, vertCount, ext]
            U32(0); U32(0); U32(faces); U32(0); U32(verts); U32(0);
            U32(0);                     // count*4 tail list
            // type-2 tail
            Pad(1 * 12);                // count*3 dwords
            U32(0);                     // *(mesh+0x44)
            U32(nTailIdx);              // n index entries
            Pad(nTailIdx * 2);          // u16 index list
            U32(nNodes);               // node count
            Pad(nNodes * 64);          // 64-byte nodes
            return b.ToArray();
        }

        // 16-byte file header: "Mesh" + an 8-byte tag + the u32 block count at offset 12 → blocks start at 16.
        private static byte[] MakeFile(int blockCountField, params byte[][] blocks)
        {
            var b = new List<byte>();
            b.AddRange(System.Text.Encoding.ASCII.GetBytes("Mesh"));
            b.AddRange(new byte[8]);                            // 8-byte tag
            b.AddRange(BitConverter.GetBytes(blockCountField)); // block count @12
            foreach (var blk in blocks) b.AddRange(blk);
            return b.ToArray();
        }

        [Test]
        public void ParseBlocks_SingleBlock_ReturnsOne()
        {
            var blk = MakeType2Block(faces: 4, verts: 8, ddsName: "scene.dds", nTailIdx: 3, nNodes: 1);
            var blocks = SceneLoader.ParseBlocks(MakeFile(1, blk));
            Assert.AreEqual(1, blocks.Count);
            Assert.AreEqual("scene.dds", blocks[0].DdsNames[0]);
            Assert.AreEqual(8, blocks[0].VertCount);
            Assert.AreEqual(12, blocks[0].IdxCount);          // 4 faces * 3
            Assert.AreEqual(1, blocks[0].Subsets.Length);
            Assert.AreEqual(4, blocks[0].Subsets[0].FaceCount);
        }

        [Test]
        public void ParseBlocks_MultiBlock_SplitsAllAndBoundariesChain()
        {
            // Two differently-sized blocks back-to-back — the SCN0026 shape in miniature.
            var b0 = MakeType2Block(faces: 6, verts: 10, ddsName: "a.dds", nTailIdx: 5, nNodes: 2);
            var b1 = MakeType2Block(faces: 3, verts: 4, ddsName: "b.dds", nTailIdx: 2, nNodes: 1);
            var blocks = SceneLoader.ParseBlocks(MakeFile(2, b0, b1));

            Assert.AreEqual(2, blocks.Count);
            Assert.AreEqual("a.dds", blocks[0].DdsNames[0]);
            Assert.AreEqual("b.dds", blocks[1].DdsNames[0]);
            // block 0's computed Next must land exactly on block 1's start (16-byte header + len(b0)).
            Assert.AreEqual(16, blocks[0].Offset);
            Assert.AreEqual(16 + b0.Length, blocks[0].Next);
            Assert.AreEqual(blocks[0].Next, blocks[1].Offset);
            // last block consumes to EOF.
            Assert.AreEqual(16 + b0.Length + b1.Length, blocks[1].Next);
        }

        [Test]
        public void ParseBlocks_BlockCountField_CapsParsing()
        {
            // A 2-block buffer whose count field says 1 yields only the first block (the trailing block 1 bytes
            // are left unparsed) — matching the original loader honouring the offset-12 count.
            var b0 = MakeType2Block(faces: 4, verts: 8, ddsName: "a.dds", nTailIdx: 3, nNodes: 1);
            var b1 = MakeType2Block(faces: 4, verts: 8, ddsName: "b.dds", nTailIdx: 3, nNodes: 1);
            var blocks = SceneLoader.ParseBlocks(MakeFile(1, b0, b1));
            Assert.AreEqual(1, blocks.Count);
        }

        [Test]
        public void ParseBlocks_GarbageHeader_ReturnsEmpty()
        {
            Assert.AreEqual(0, SceneLoader.ParseBlocks(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 1, 0, 0, 0 }).Count);
            Assert.AreEqual(0, SceneLoader.ParseBlocks(null).Count);
            Assert.AreEqual(0, SceneLoader.ParseBlocks(new byte[4]).Count);
        }
    }
}
