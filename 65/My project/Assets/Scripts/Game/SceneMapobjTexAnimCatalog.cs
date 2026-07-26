using System.Collections.Generic;

namespace Sdo.Game
{
    /// <summary>One animated texture overlay: a mapobj mesh whose material is driven through an ordered DDS frame
    /// sequence (the original's UIPicMap frame-swap) instead of by its single MSH material. Frames live in the
    /// mapobj group's own SCENE/MAPOBJ folder.</summary>
    public sealed class MapobjTexAnim
    {
        public readonly string MeshBase;     // mesh file base name (no extension), upper-invariant
        public readonly string[] Frames;     // DDS file names within the group's folder, in cycle order
        public readonly float IntervalMs;    // ms between frames
        public readonly bool Transparent;    // true -> alpha-blended overlay (cutout sprites: crowd/lights);
                                             // false -> keep the opaque material (a solid video screen)
        public readonly bool HoldLast;       // true -> play-once: after intervalMs, lock on last frame forever
        public MapobjTexAnim(string meshBase, string[] frames, float intervalMs, bool transparent, bool holdLast = false)
        { MeshBase = meshBase; Frames = frames; IntervalMs = intervalMs; Transparent = transparent; HoldLast = holdLast; }
    }

    /// <summary>
    /// Hand-authored companion to <see cref="SceneMapobjCatalog"/> for the props the original textures by a per-frame
    /// DDS sequence rather than by their MSH material. Decompiled from Scene_LoadBackground (FUN_004b43c0: the frame
    /// lists are loaded via UIPicMap_LoadEntry) + the scene updates (FUN_004ad250) that advance the frame index on a
    /// timer. The MSH of these props carries only a placeholder material (e.g. SEA_SCREEN = "s00.dds", which doesn't
    /// exist on disk), so the MSH-material path renders them white/untextured — that's why they looked "missing".
    ///
    ///   FIFA day (SCN0012) / night (SCN0013): crowd renqun (9 frames) + spotlight shanguang (4 frames), 300 ms,
    ///     alpha-cutout sprites (people / light beams on a transparent field) -> Transparent.
    ///   Sea (SCN0014): sea_screen video wall (28 frames), 250 ms, an opaque screen -> NOT transparent.
    ///
    /// Keyed by scene FOLDER (SceneMapobjCatalog's key) + mesh base name; frames resolve against the group's folder.
    /// </summary>
    public static class SceneMapobjTexAnimCatalog
    {
        // zero-padded "<prefix>NNN.dds" sequence, 1..count (e.g. Seq("sea_screen",28) -> sea_screen001.dds..028.dds)
        private static string[] Seq(string prefix, int count)
        {
            var a = new string[count];
            for (int i = 0; i < count; i++) a[i] = prefix + (i + 1).ToString("000") + ".dds";
            return a;
        }

        // single-digit "<prefix>N.dds" sequence, 0..count-1 (e.g. SeqFrom0("CHUNTIAN_HUDEI1",4) ->
        // CHUNTIAN_HUDEI10.dds..13.dds — the original's frame arrays are indexed 0..3)
        private static string[] SeqFrom0(string prefix, int count)
        {
            var a = new string[count];
            for (int i = 0; i < count; i++) a[i] = prefix + i.ToString() + ".dds";
            return a;
        }

        // 2-digit "<prefix>NN.dds" sequence, 1..count (e.g. Seq2("19_SUBWAY_VT6",24) -> 19_SUBWAY_VT601.dds..624.dds)
        private static string[] Seq2(string prefix, int count)
        {
            var a = new string[count];
            for (int i = 0; i < count; i++) a[i] = prefix + (i + 1).ToString("00") + ".dds";
            return a;
        }

        private static readonly MapobjTexAnim Shanguang =
            new MapobjTexAnim("FIFA_SHANGUANG", new[] { "s001_.dds", "s002_.dds", "s003_.dds", "s004_.dds" }, 300f, true);

        // The frame lists / intervals / transparency below are decompiled from Scene_LoadBackground (load) +
        // Scene_UpdateSceneObjects (timers) and grounded against the on-disk DDS sequences; Transparent matches each
        // sequence's measured alpha (opaque screens/water vs alpha cut-outs). See SDO_SCENE_MAPOBJ docs.
        private static readonly Dictionary<string, MapobjTexAnim[]> ByFolder =
            new Dictionary<string, MapobjTexAnim[]>
            {
                // SCN0003 disco floor is NOT here — its 256 tiles animate as a per-tile moving formation
                // (BoxFloorPattern / BoxFloorAnimator), not a single shared-material cycle.
                ["SCN0004"] = new[]
                {
                    // 海灘 water surface waves: sea_up = B001..B032, sea_down = A001..A032 @100ms, opaque.
                    new MapobjTexAnim("SEA_UP", Seq("B", 32), 100f, false),
                    new MapobjTexAnim("SEA_DOWN", Seq("A", 32), 100f, false),
                },
                ["SCN0005"] = new[]
                {
                    // Christmas reindeer billboard + the ground "Merry Christmas" decal: the MSH materials are
                    // placeholders (xunlu.dds / 001.dds, absent) so without these they rendered as beige boxes
                    // ("奇怪方塊在天上飛"). Frames CHRISTMAS001..004 / MERRYCHRISTMAS001..004 @500ms, alpha cut-outs.
                    new MapobjTexAnim("CHRISTMAS", Seq("CHRISTMAS", 4), 500f, true),
                    new MapobjTexAnim("MERRYCHRISTMAS", Seq("MERRYCHRISTMAS", 4), 500f, true),
                },
                ["SCN0011"] = new[]
                {
                    new MapobjTexAnim("JIGUANG", new[] { "01_.dds", "02_.dds", "03_.dds", "04_.dds", "05_.dds", "06_.dds", "07_.dds", "08_.dds", "09_.dds" }, 300f, true),
                    new MapobjTexAnim("DIDENG", new[] { "343.dds", "344.dds", "345.dds", "346.dds" }, 300f, false),   // opaque floor light
                    new MapobjTexAnim("DENGGUANG", new[] { "guangx1_.dds", "guangx11.dds" }, 600f, true),
                },
                ["SCN0012"] = new[]
                {
                    new MapobjTexAnim("FIFA_RENQUN", Seq("", 9), 300f, true),   // 001.dds..009.dds
                    Shanguang,
                },
                ["SCN0013"] = new[]
                {
                    new MapobjTexAnim("FIFA_RENQUN", Seq("fifanight_renqun", 9), 300f, true),
                    Shanguang,
                },
                ["SCN0014"] = new[]
                {
                    new MapobjTexAnim("SEA_SCREEN", Seq("sea_screen", 28), 250f, false),   // opaque video wall
                },
                ["SCN0017"] = new[]
                {
                    new MapobjTexAnim("DIANSHI", Seq("DIANSHI", 30), 150f, false),   // opaque subway TV wall
                },
                ["SCN0020"] = new[]
                {
                    // 19_subway TV6 video screen: 24 frames 19_SUBWAY_VT601..624 @80 ms, opaque (DXT3 but full alpha,
                    // a solid video wall like DIANSHI). FUN_004b09a0 cycles param_1[0x4f] every 0x50=80 ms, %0x18=24.
                    new MapobjTexAnim("TV6", Seq2("19_SUBWAY_VT6", 24), 80f, false),
                },
                ["SCN0018"] = new[]
                {
                    new MapobjTexAnim("NIHONG", Seq("NIHONG", 12), 500f, true),         // neon, alpha
                    new MapobjTexAnim("BOAT_SCREEN", Seq("BOAT_SCREEN", 4), 500f, false),// opaque screen
                    new MapobjTexAnim("SHUIMO", Seq("SHUIMO", 5), 125f, true),          // water-ink ripple, alpha
                    new MapobjTexAnim("WATER", Seq("WATER", 10), 150f, false),          // river surface, opaque
                },
                ["SCN0024"] = new[]
                {
                    // 雪景 (SCN0024) 的 Harrahs 光球招牌:整塊招牌是「變色」而不是換形狀。
                    // Scene_LoadBackground case 0x18 先切到 biaodonghua 的封裝,再把 Xuejing_Donghua_biao01_.dds..
                    // biao05_.dds 這 5 張讀進 param_1[0x59](= +0x164);場景 0x18 唯一的每幀更新 FUN_004b0cc0
                    // 每 500 ms 讓索引 (i+1) % 5,把該張綁到「第二個載入的道具」(objects[1] = biaodonghua)的
                    // 材質槽 0 —— 反編譯出來就是 mov edx,[ecx+4] / mov ecx,[edx+0C0h] 那串。
                    // 5 張裡 01 與 03 位元組完全相同,所以看起來是 粉紅→洋紅→粉紅→深藍→青藍,一輪 2.5 秒。
                    // 與 SCN0005/0014/0025 不同的是:MSH 材質 biao1_.dds 在磁碟上「真的存在」,所以少了這筆
                    // 條目招牌不會變白,而是「卡在同一個顏色不動」——正是使用者回報的症狀。
                    // DXT3 硬去背(55% alpha=0、LooksLikeAdditiveGlow=false)→ Transparent。
                    new MapobjTexAnim("BIAODONGHUA", new[]
                    {
                        "XUEJING_DONGHUA_BIAO01_.dds", "XUEJING_DONGHUA_BIAO02_.dds", "XUEJING_DONGHUA_BIAO03_.dds",
                        "XUEJING_DONGHUA_BIAO04_.dds", "XUEJING_DONGHUA_BIAO05_.dds",
                    }, 500f, true),
                    // 同場景的 XUEJING/DONGHUA(探照燈)不在這裡:它不換貼圖,是靠 DONGHUA.MOT 的 182 支旋轉
                    // key 掃動,見 ScreenGameplay.ShouldApplyRigidBindScale 的 SCN0024 例外與 UvScroll 的
                    // OfficialMaterialAlpha;背景那三顆光暈是 GUANG_.TGA billboard,見 SceneFlameBillboardCatalog。
                },
                ["SCN0025"] = new[]
                {
                    // 春天 butterflies: four flocks, each a .mot-flown quad whose WING FLAP is a 4-frame texture cycle
                    // (Scene_LoadBackground case 0x19 loads CHUNTIAN_HUDEI<N>0..3 into param_1[0x5a..0x5d];
                    // FUN_004b0d20 advances each with its OWN timer: 0x32/0x28/0x3c/0x1e ms, index +1 & 3).
                    // Their MSH material is the placeholder 01.dds (= a copy of frame 0), so without this they fly
                    // with frozen wings. Alpha cut-out sprites on a transparent field -> Transparent.
                    new MapobjTexAnim("HUDEICHUNTIANDONGHUA", SeqFrom0("CHUNTIAN_HUDEI1", 4), 50f, true),
                    new MapobjTexAnim("HUDEICHUNTIANDONGHUA2", SeqFrom0("CHUNTIAN_HUDEI2", 4), 40f, true),
                    new MapobjTexAnim("HUDEICHUNTIANDONGHUA3", SeqFrom0("CHUNTIAN_HUDEI3", 4), 60f, true),
                    new MapobjTexAnim("HUDEICHUNTIANDONGHUA4", SeqFrom0("CHUNTIAN_HUDEI4", 4), 30f, true),
                },
                // SCN0022 坟墓 is NOT here: the flame (鬼火) is 3 camera-facing BillboardSet sprites
                // (SceneFlameBillboardCatalog), and the flying ghosts (gui/gui2) are .mot-driven camera-facing billboards
                // whose GUI01↔GUI02 texture swing is carried by SceneGhostBillboardCatalog — neither is a mapobj-mesh anim.
            };

        /// <summary>The frame sequence for a (scene folder, mesh base) pair, or null if that prop isn't a sequence.</summary>
        public static MapobjTexAnim Find(string folder, string meshBase)
        {
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(meshBase)) return null;
            if (!ByFolder.TryGetValue(folder.ToUpperInvariant(), out var arr)) return null;
            string mb = meshBase.ToUpperInvariant();
            foreach (var a in arr) if (a.MeshBase == mb) return a;
            return null;
        }
    }
}
