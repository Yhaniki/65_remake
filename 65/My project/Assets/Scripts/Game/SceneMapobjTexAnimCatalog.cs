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
                ["SCN0019"] = new[]
                {
                    // 舞鬥競技場天花板燈架。Scene_LoadBackground case 0x13 把 Pkobj_deng_001..003.dds 讀進
                    // param_1[0x4e](= +0x138),StageScene_Update3Frame_004b0940 每 200 ms 讓索引 (i+1)%3。
                    // MSH 佔位材質 deng001_.dds 與第 1 幀位元組完全相同(md5 3C2D1488…)→ 少了這筆的症狀是
                    // 「卡在第 1 幀不閃」而不是畫錯圖。官方材質旗標 +0x194 = 0x1 → 透明批。
                    new MapobjTexAnim("SHAN", new[] { "Pkobj_deng_001.dds", "Pkobj_deng_002.dds", "Pkobj_deng_003.dds" }, 200f, true),
                },
                ["SCN0022"] = new[]
                {
                    // 墓地的兩座墓穴火光。case 0x16 把 dong1 的兩張讀進 param_1[0x51](= +0x144)、dong2 的兩張讀進
                    // param_1[0x52](= +0x148);StageScene_UpdateLightGroups_004b0b30 每 1000 ms 用「同一個」計數器
                    // DAT_00678588 = DAT-1 & 1 推進兩者,所以兩座墓的索引永遠相同。
                    // 反相是美術把兩組檔案「內容對調」做出來的:DONG2 的 01_ 位元組等於 DONG1 的 02_,反之亦然
                    // (md5 實測 b613c26e / d21d7a95 互換)。所以照檔名順序填就會自然反相 —— 不要另外做相位偏移。
                    // 兩組正式幀 alpha 全 255 → 不透明換幀,Transparent = false。
                    // MSH 的佔位材質 mubei01/02.dds 只是同一張圖的 DXT1 版(逐像素平均差 0.19),所以現況是
                    // 「正確的第 1 幀凍住」,補這兩筆是把 1 Hz 的火光交替補回來。
                    new MapobjTexAnim("GUANG4", new[] { "FenMuobj_Dong_mubei01_.dds", "FenMuobj_Dong_mubei02_.dds" }, 1000f, false),
                    new MapobjTexAnim("DONGHUA2", new[] { "FenMuobj_Dong2_mubei01_.dds", "FenMuobj_Dong2_mubei02_.dds" }, 1000f, false),
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
                ["SCN0028"] = new[]
                {
                    // 北京之夜 (鸟巢) 遠處街道/建築上的四組路燈光暈。每支 MSH 只是 2~3 片 250×250 的平面
                    // quad,靠自己 HRC 的 bind 擺到街上(y 72~195、z 1730~2110),材質寫死佔位的 001_.dds。
                    // Scene_LoadBackground case 0x1c 把四個 niaochao/dengN.bin 的幀讀進 param_1[0x61..0x64]
                    // (= +0x184/0x188/0x18c/0x190),每組第 0 張都叫 001_.dds、之後才是 niaochao_dengN00M_.dds;
                    // 場景 0x1c 的每幀更新 StageScene_UpdateBigBillboardSet_004b0fc0 用四個獨立計時器、
                    // 全部 200 ms 換一張:deng1/deng2 三張、索引 (i+1)%3;deng3/deng4 兩張、索引 (i-1)&1
                    // (兩張時等同來回切)。四組的每一張 128×128 DXT3 的 RGB 完全相同(meanLum 199.4),
                    // 只有 alpha 不同(平均 13.4 / 26.6 / 35.3)—— 也就是說這個「動畫」是燈光的明暗脈動,
                    // 不是換圖案。少了這幾筆,四盞路燈就固定停在第一張的亮度,完全不會呼吸。
                    // DXT3 硬去背 → Transparent。
                    new MapobjTexAnim("DENG1_", new[] { "001_.dds", "NIAOCHAO_DENG1002_.dds", "NIAOCHAO_DENG1003_.dds" }, 200f, true),
                    new MapobjTexAnim("DENG2_", new[] { "001_.dds", "NIAOCHAO_DENG2002_.dds", "NIAOCHAO_DENG2003_.dds" }, 200f, true),
                    new MapobjTexAnim("DENG3_", new[] { "001_.dds", "NIAOCHAO_DENG3002_.dds" }, 200f, true),
                    new MapobjTexAnim("DENG4_", new[] { "001_.dds", "NIAOCHAO_DENG4002_.dds" }, 200f, true),
                },
                ["SCN0026"] = new[]
                {
                    // 籃球場營火:case 0x1a 把 lanqiuchang_huo001..009.dds 讀進 param_1[0x5e](= +0x178),
                    // StageScene_Update9And3Frame_004b0f00 每 0x32 = 50 ms 讓索引 (i+1)%9。MSH 佔位材質 001.dds
                    // 是第 10 張獨立的圖(對 9 張正式幀的最小像素差 6.63,遠大於「同圖不同壓縮」的 0.19),
                    // 所以現況是火焰完全靜止、而且畫的還是另一張圖。fracA0≈0.88 硬去背 → Transparent。
                    new MapobjTexAnim("HUO", Seq("lanqiuchang_huo", 9), 50f, true),
                    // 場邊小燈:同一支更新函式的第二段,每 500 ms 索引 (i+1)%3。三張的 alpha 逐像素完全相同,
                    // 只有 RGB 在變 → 固定剪影上的亮度脈動(meanLum 13.4 → 16.4 → 13.9),不是換形狀。
                    // MSH 佔位材質 s01.dds 的 md5 與 001 完全相同 → 現況就是死在第 1 幀。
                    new MapobjTexAnim("XIAODENG", Seq("lanqiuchang_xiaodeng", 3), 500f, true),
                },
                ["SCN0029"] = new[]
                {
                    // 飛機場吧台霓虹。case 0x1d 把 jiku/jiuba.bin 的 8 張讀進 param_1[0x128](= +0x4a0);
                    // StageScene_UpdateFlashCycle_004b1890 用計時器 &DAT_006785f4、200 ms 換一張,索引 (i+1)&7。
                    // JIUBA.MSH 單一 submesh、材質寫死幀 0 的 00014.dds,所以現在永遠定格在第 1 張。
                    // 0002_~0008_ 雖是 DXT3 但 alpha 全 255(minA=255)→ 不透明畫面,Transparent = false。
                    new MapobjTexAnim("JIUBA", new[]
                    {
                        "00014.dds", "0002_.dds", "0003_.dds", "0004_.dds",
                        "0005_.dds", "0006_.dds", "0007_.dds", "0008_.dds",
                    }, 200f, false),
                },
                // SCN0022 坟墓 的三個 prop 不在這張表裡(它們不是 mapobj-mesh 換幀):鬼火(SHAN.MSH)是 3 顆
                // 相機朝向的 BillboardSet(SceneFlameBillboardCatalog),飛鬼 gui/gui2 是 .mot 驅動的相機朝向
                // billboard、其 GUI01↔GUI02 貼圖擺盪由 SceneGhostBillboardCatalog 負責。
                // 但同場景的兩座墓穴(dong1 GUANG4 / dong2 DONGHUA2)「是」標準的 mapobj-mesh 換幀,見上面的 SCN0022 條目。
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
