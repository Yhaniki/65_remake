using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Data-driven UV-scroll commands for stage render objects the original animates by writing texture-coordinate
    /// offsets into render states (0x58=U, 0x5c=V). Targets are structural: scene folder is optional, object key is
    /// SCENE or a mapobj base name, and material id is optional. No target depends on a DDS file name.
    /// </summary>
    public static class SceneMapobjUvScrollCatalog
    {
        public const string SceneObject = "SCENE";

        public enum RenderMode
        {
            KeepMaterial,
            AdditiveOverlay,
            // Force standard alpha-blend (SrcAlpha,OneMinusSrcAlpha) regardless of what the MSH loader assigned.
            // Needed when LooksLikeAdditiveGlow() misclassifies a texture that D3D9 confirmed uses DST=INVSRCALPHA.
            ForceAlphaBlend,
            // Like ForceAlphaBlend but TWO-SIDED and at FULL opacity (Sdo/UnlitOverlay) — for a glow prop the MSH loader
            // wrongly made additive (LooksLikeAdditiveGlow false positive) whose additive edge reads hard/opaque. Unlike
            // ForceAlphaBlend it doesn't cull (a sweeping beam mustn't vanish edge-on) or dim to 0.2 (keep the beam visible).
            AlphaBlendOverlay,
            // Soft searchlight beam: additive, but blur the texture along its width so the light spreads sideways
            // and the narrow hard alpha edge becomes a gradual soft falloff (SCN0016 JIGUANG spotlights).
            SpotGlow,
            // Let the prop's OFFICIAL per-material flag decide (MSH material record +0x194): a material the artist
            // marked transparent (flags & 0x3f != 0) renders as STANDARD alpha-blend, one marked 0 keeps whatever the
            // opaque path gave it. The engine has no alpha-test and no additive material mode, so this both undoes the
            // LooksLikeAdditiveGlow false positives (a bright soft-alpha glow blown out to solid white) and the
            // cutout heuristic. Unlike ForceAlphaBlend/AlphaBlendOverlay this is PER-MATERIAL, so a multi-material
            // prop (SCN0014 TV: screen + frame + projector opaque, only the light beam transparent) stays correct.
            OfficialMaterialAlpha,
        }

        /// <summary>How a target's UV offset moves. The original uses all three; a single "UV/s" cannot express
        /// the last two, which is why SCN0004 / SCN0012 / SCN0013 / SCN0029 had no entry at all before.</summary>
        public enum Motion
        {
            // offset += Speed·dt, wrapping at 1 (the common streaming case)
            Linear,
            // offset = Amplitude·sin(phase), phase += Speed(rad/s)·dt, phase wrapping at 2π — rocks back and forth
            Sine,
            // hold Dwell ms at rest, then sweep one signed Step at Speed UV/s, repeat — a cut with a wipe
            DwellStep,
        }

        public readonly struct Target
        {
            public readonly string Folder;     // null/empty = any scene
            public readonly string ObjectKey;
            public readonly int MaterialId;    // -1 = all materials on the object
            public readonly Vector2 Speed;     // Linear: UV/s · Sine: rad/s · DwellStep: UV/s while sweeping
            public readonly RenderMode Mode;
            public readonly Motion Motion;
            public readonly Vector2 Amplitude; // Sine only: peak signed UV offset per axis
            public readonly Vector2 Step;      // DwellStep only: signed UV covered by one leg
            public readonly float DwellMs;     // DwellStep only
            public readonly Vector2 Start;     // DwellStep only: UV offset the prop rests at before the first sweep
            public readonly int Queue;         // 0 = leave the shader's queue alone; else force this render queue

            public Target(string folder, string objectKey, int materialId, Vector2 speed, RenderMode mode = RenderMode.KeepMaterial)
                : this(folder, objectKey, materialId, speed, mode, Motion.Linear, Vector2.zero, Vector2.zero, 0f, Vector2.zero, 0)
            {
            }

            private Target(string folder, string objectKey, int materialId, Vector2 speed, RenderMode mode,
                           Motion motion, Vector2 amplitude, Vector2 step, float dwellMs, Vector2 start, int queue)
            {
                Folder = folder;
                ObjectKey = objectKey;
                MaterialId = materialId;
                Speed = speed;
                Mode = mode;
                Motion = motion;
                Amplitude = amplitude;
                Step = step;
                DwellMs = dwellMs;
                Start = start;
                Queue = queue;
            }

            /// <summary>Sinusoidal rocking: offset = <paramref name="amplitude"/>·sin(phase), phase advancing at
            /// <paramref name="radPerSec"/>. Per-axis, so the unused axis is simply left at 0 in both vectors.</summary>
            public static Target Sine(string folder, string objectKey, Vector2 amplitude, Vector2 radPerSec,
                                      RenderMode mode = RenderMode.KeepMaterial, int queue = 0)
                => new Target(folder, objectKey, -1, radPerSec, mode, Motion.Sine, amplitude, Vector2.zero, 0f, Vector2.zero, queue);

            /// <summary>Hold, then wipe: rest at <paramref name="start"/> for <paramref name="dwellMs"/>, sweep one
            /// signed <paramref name="step"/> at <paramref name="sweepPerSec"/>, hold again, forever.</summary>
            public static Target Dwell(string folder, string objectKey, Vector2 sweepPerSec, Vector2 step,
                                       float dwellMs, Vector2 start, RenderMode mode = RenderMode.KeepMaterial, int queue = 0)
                => new Target(folder, objectKey, -1, sweepPerSec, mode, Motion.DwellStep, Vector2.zero, step, dwellMs, start, queue);

            /// <summary>Does this entry actually move the UVs? (Entries that exist only to carry a
            /// <see cref="RenderMode"/> — SCN0016 JIGUANG, SCN0022 SHEGUANG, SCN0024 DONGHUA — do not.)</summary>
            public bool Animates =>
                Motion == Motion.Sine ? Amplitude != Vector2.zero
              : Motion == Motion.DwellStep ? Step != Vector2.zero
              : Speed != Vector2.zero;
        }

        // SCN0014 FUN_004b0330 writes SEVEN texture-coord targets every 50 ms, in three groups that line up exactly
        // with the scene's props: the 3 coral TREES get V = −t, the 3 coral BRANCHES get V = −2t, and the 7th — the
        // only one written in U, at 4× the rate — is the projector BEAM (t += _DAT_00589034 = 0.004 per 50 ms).
        // U is the beam's around-the-axis coordinate (its mesh is a 6-segment cone unwrapped U 0.018‥0.976 with only
        // two V rows), so scrolling U spins the light pattern about the beam axis — the "光自己也在轉" on top of the
        // whole prop orbiting the stage on its .mot. Coral UVs are not cylindrical, so U scroll only makes sense there.
        private static readonly Vector2 CoralV = new Vector2(0f, -0.08f);        // trees:    V += 0.004 per 50 ms
        private static readonly Vector2 CoralBranchV = new Vector2(0f, -0.16f);  // branches: V += 0.008 per 50 ms (2×)
        private static readonly Vector2 BeamSpinU = new Vector2(0.32f, 0f);      // beam:     U += 0.016 per 50 ms (4×)
        // D3D9 V += 0.003/50ms = +0.06/s. Test confirmed positive sign is correct (unlike CoralV).
        // Angular-edge issue tracked in decomp doc; suspect UV scale transform not yet captured.
        private static readonly Vector2 Scn0015WindowUv = new Vector2(0f, 0.06f);

        /// <summary>Render queue that puts a transparent prop BEHIND the stage. The base scene mesh renders with
        /// Sdo/SceneVertexCutout at AlphaTest (2450) and writes depth; a ZWrite-off prop placed before it is drawn
        /// first and then simply painted over wherever the stage has geometry — i.e. the stage always wins.
        /// Used for SCN0004's sea/shore water, which must never appear in front of the huts or the pier.</summary>
        public const int WaterBehindSceneQueue = 2400;

        private static readonly Target[] Targets =
        {
            // SCN0011 StageScene_UpdateScrollLights: UV scroll V += 0.003/frame on Vector_at4b(0).
            // Vector_at4b[0] = CAIDAI — only uVar13==1 (CAIDAI) calls AvatarScene_Create(..., param3=1) which
            // registers it as the UV-scroll target; all others pass 0 and are skipped.
            // caidai.dds (32×128 DXT1) tiles V −1~2 (3× repeat) on the 彩帶 vertical light strip next to the speaker.
            // D3D9 positive V → Unity negative V (DDS raw-load flips V axis, same as CoralV convention).
            new Target("SCN0011", "CAIDAI", -1, new Vector2(0f, 1.775f)),   // measured: online sdo.bin @ 593fps → 0.003×593 = 1.775 UV/s
            // SCN0020 subway FUN_004b09a0: the TV1 filmstrip screen (TV01.dds 256×1024, dancers + "BROADWAY") is the
            // ONLY object registered with AvatarScene_Create(...,param3=1) → scroll object 0. Every 300 ms the update
            // sets render state +0x48|=0x10000 (texture transform), U=0, V += _DAT_00589040 (=0.03), wrapping at 1.0
            // ⇒ 0.1 UV/s in V. Sign confirmed by visual check (BROADWAY filmstrip scrolled the wrong way at -0.1) → +0.1.
            new Target("SCN0020", "TV1", -1, new Vector2(0f, 0.1f)),
            // SCN0015 FUN_004b0620: every 50 ms set U=0 and V=DAT_00678534, then DAT_00678534 += 0.003.
            // 15_UV is the only mapobj created with param3=1 in scene-load case 0xf; HUA/SHU1-4 pass 0.
            // The texture itself is diagonal, so a pure V scroll reads as the window beam sliding diagonally down.
            // D3D9 capture (hook onLeave after RenderObjPre): ABL=1 SRC=SRCALPHA(5) DST=INVSRCALPHA(6)
            // = STANDARD alpha blend (NOT additive). ZWrite=1, CULL=3, TTF0=COUNT2(2), ADDR=WRAP, FILTER=LINEAR.
            // ForceAlphaBlend overrides the MSH loader — LooksLikeAdditiveGlow returns true for GUANG1_.DDS
            // (it matches the "soft alpha, low opaque, mid lum" heuristic for radial glow sprites), so without
            // the override the material becomes Sdo/UnlitAdditiveOverlay, producing a hard bright mesh-edge band.
            new Target("SCN0015", "15_UV", -1, Scn0015WindowUv, RenderMode.ForceAlphaBlend),
            // ── SCN0004 海灘:海面與岸浪的正弦擺盪 ────────────────────────────────────────────────
            // StageScene_UpdateWaveTilt_004afd30 每一幀(沒有計時器)推兩個獨立相位,各自 2π 歸零,然後:
            //   objects[3] = beach/LANG (岸浪): U 寫 0、V = −sin(b)×0.25,b += _DAT_00589020 = 0.004/frame
            //   objects[4] = SEA     (海面): V 寫 0、U = +sin(a)×0.5 ,a += _DAT_00589024 = 0.001/frame
            // 這是「來回盪」不是「單向流」—— 用等速捲動根本表達不出來,所以這兩支之前完全沒有條目、海面是死的。
            // 每幀累加換算成秒沿用本檔既有的官方幀率慣例(SCN0011 CAIDAI 的 0.003×593):
            //   浪 0.004×593 = 2.372 rad/s(週期約 2.65 s)、海 0.001×593 = 0.593 rad/s(週期約 10.6 s)。
            // 軸的選擇有 mesh 依據:SEA 的 U 跑到 −1.772..2.312(在 U 上 tiling)、LANG 的 V 是 −0.215..0.617。
            //
            // ★ 兩支都必須套 OfficialMaterialAlpha(官方旗標都是 0x1 = 一般透明批)。LANG 的 wave_.dds 是
            //   「亮 RGB + 幾乎全軟 alpha」(min 0 / mean 11.6)——正是 LooksLikeAdditiveGlow 的誤判樣本
            //   (同 SCN0014 投影光 / SCN0015 窗光 / SCN0024 探照燈)。被誤判成加法之後套上的
            //   Sdo/UnlitAdditiveOverlay 除了 Blend SrcAlpha One,還帶 **Offset -1,-1 深度偏移**;
            //   岸浪是一片 1261×3051、幾乎與視線平行的大水面,斜率式深度偏移會把它整片往鏡頭方向擠、
            //   穿過棧道與房子,再用加法把藍白刷上去 = 使用者看到的「海浪漫到房子上」。
            //   實測截圖(Scn0004CaptureTest)確認:誤判前藍色浪片直接畫在木棧道上。
            //   改成 OfficialMaterialAlpha → Sdo/UnlitInstancedAlpha(標準 SrcAlpha/InvSrcAlpha、**沒有**
            //   polygon offset),深度穿透與加法過曝一次解決。SEA 本來就落在同一支 shader,套上去是 no-op,
            //   但把「官方說它是一般透明批」這件事釘住,免得日後 heuristic 漂移又把它改成加法。
            //   教訓:這兩支之前看起來「沒問題」只是因為它們不會動;一旦 UV 開始動,誤判就藏不住了。
            //
            // ★ Queue = WaterBehindSceneQueue:光把混色改對還不夠 —— 官方的海浪永遠在房子後面(使用者對過原版
            //   確認)。這兩片水面是 ZWrite Off 的透明批,原本排在 Transparent(3000),也就是「場景畫完之後」才畫,
            //   所以只要哪個像素比場景近就會蓋上去;水面又大又掠角,棧橋/房子基座那一帶就一直被水紋刷到。
            //   把它們壓到場景(Sdo/SceneVertexCutout,queue 2450)之前 → 水面先畫、又不寫深度,接著場景以
            //   不透明/alpha-test 覆蓋上去,結果就是「有場景幾何的地方一律是場景贏」= 水永遠在房子後面。
            //   水面本身在 y ≈ −49、比整個棧橋都低,沒有任何它「應該」擋住的東西,所以這個取捨沒有副作用。
            // ★ 只有 LANG 壓到場景之前,SEA 不動 —— 判準是「這片水的高度跟甲板差多少」,不是「哪片比較大」:
            //   SEA  平躺在 y = −48.9,比棧橋甲板(y ≈ 0)低了整整 49 單位。相機一定在水面之上,射線會先打到
            //        甲板才碰得到那個高度,所以正常深度測試本來就不會讓它爬上甲板;而沙灘沿岸低於水面的部分
            //        則會被它正確蓋住 —— 這正是「海水蓋在沙灘上」。**維持預設佇列(3000)**,壓下去反而會
            //        變成沙灘蓋掉海水。
            //   LANG 是岸浪,包圍盒 y −69.5..−5.7,**上緣幾乎跟甲板同高**,所以它會從甲板下面戳出來、
            //        整條浪打在木板上。這片才要壓到 2400(場景本體 Sdo/SceneVertexCutout 的 AlphaTest 2450
            //        之前):水面先畫又不寫深度、場景後畫覆蓋上去 → 有場景幾何的地方一律場景贏 = 浪永遠在
            //        甲板與房子後面。
            //   房子/甲板與沙灘同屬一顆 SCENE.MSH(單一 renderer、單一佇列),佇列分不開它們,所以唯一能
            //   表達「擋住甲板但別擋住沙灘」的維度就是分開設定這兩片水。
            Target.Sine("SCN0004", "SEA",  new Vector2(0.5f, 0f), new Vector2(0.593f, 0f),
                        RenderMode.OfficialMaterialAlpha),
            Target.Sine("SCN0004", "LANG", new Vector2(0f, -0.25f), new Vector2(0f, 2.372f),
                        RenderMode.OfficialMaterialAlpha, WaterBehindSceneQueue),
            // ── SCN0012/0013 足球場:廣告看板每 2 秒換一幅 ───────────────────────────────────────
            // StageScene_UpdatePulse_004b0090(case 0xc 與 0xd 共用)是一台「停→快掃」的狀態機:
            //   v>=1 → v=0;若 v!=0 且 (v<=0.5 或 prev>=0.5) → 每幀 v += _DAT_00589030 (0.003) 並寫出;
            //   否則走 2000 ms 計時器,時間到才寫出並 bump 一次。
            // 展開就是:停在 V=0 兩秒 → 用 0.003/frame 快掃到 0.5 → 停兩秒 → 快掃到 1.0 → 回 0。
            // 官方同時寫 slot 0 與 slot 1 —— FIFA_GUANGGAO.MSH 正好是一個 submesh 兩顆材質
            // (biaozhi.dds / biaozhi1.dds),而 mesh 的 V 只吃 0..0.493,所以 +0.5 就是切到貼圖下半張的
            // 另一幅廣告。remake 這邊本來就把整支道具的材質一起餵給 MapobjUvScroll,對得上。
            // 0.003×593 = 1.775 UV/s ⇒ 半格快掃約 0.28 秒。材質旗標 0x0 → 不要動 RenderMode。
            // ObjectKey 是 MSH 檔名:兩個場景的 mesh 都叫 FIFA_GUANGGAO.MSH(夜間版只有貼圖換名)。
            Target.Dwell("SCN0012", "FIFA_GUANGGAO", new Vector2(0f, 1.775f), new Vector2(0f, 0.5f), 2000f, Vector2.zero),
            Target.Dwell("SCN0013", "FIFA_GUANGGAO", new Vector2(0f, 1.775f), new Vector2(0f, 0.5f), 2000f, Vector2.zero),
            // ── SCN0029 飛機場:大螢幕四格輪播 ──────────────────────────────────────────────────
            // StageScene_UpdateFlashCycle_004b1890 的前半段:DAT_00589050 從 1.0 起每幀 −= _DAT_0058904c
            // (0.001);每當 (邊界 − v) 落在 (0, 0.001] —— 邊界是 1.0/0.75/0.5/0.25 —— 就把 DAT_006785fc
            // 設 1 並記時,持續到 5000 ms 過去才解除並補減一次;v<=0 時回 1.0。寫出 U=0、V=v。
            // 也就是四個停格、每格停 5 秒、之間用 0.001/frame(= 0.593 UV/s)滑 0.25。
            // PINGMU.MSH 是單一 quad,U 幾乎滿幅、V 只吃 0..0.25,而 PINGMU7.DDS 是 256×1024 —— 直排四張
            // 256×256,所以 V 每階 0.25 剛好切一張。**方向是遞減**(官方 `-=`,和 SCN0011 的 `+=` 相反)。
            // 官方實際停在 0.999/0.749/0.499/0.249(判定式的 0.001 誤差),這裡取整到 1.0/0.75/0.5/0.25 ——
            // 差 0.001 UV 在 1024 高的貼圖上是 1 texel,肉眼無差。Start 用 0(≡1.0,貼圖 Repeat)。
            // PINGMU7.DDS 是 DXT1 無 alpha → RenderMode 保持 KeepMaterial。
            Target.Dwell("SCN0029", "PINGMU", new Vector2(0f, -0.593f), new Vector2(0f, -0.25f), 5000f, Vector2.zero),
            // SCN0018 豪華郵輪 旋轉燈 (18_Boat/zhuandeng):case 0x12 的 9 個 mapobj 裡只有 index 8 (zhuandeng)
            // 走 AvatarScene_Create(..., param3=1) 註冊成貼圖座標捲動目標,其餘 8 個都傳 0(同 SCN0011 CAIDAI 的慣例)。
            // StageScene_UpdateManyBillboards_004b0740 每 100 ms 設 +0x48 |= 0x10000,U = 累加值
            // (_DAT_0058903c = 0.03,到 1.0 歸零)、V 明確寫 0 ⇒ +0.3 UV/s 在 U。mesh 的 U 跑到 -0.918..1.381
            // (超出 0..1 → 在 U 方向 tiling),正是「燈光沿著燈條跑」的軸。
            // 官方材質旗標 +0x194 = 0x0(不透明批)→ 保持 KeepMaterial,別套 OfficialMaterialAlpha(會變成 no-op)。
            new Target("SCN0018", "ZHUANDENG", -1, new Vector2(0.3f, 0f)),
            // SCN0016 繁華街道 遠景天際線 (16/FANGZI/CHENGSHI):官方 case 0x10 對 objects[30] 的 NoteGroup
            // 手寫 +0xc=4 / +0x10=1 → RenderSysD3D_SetBlendMode 的 SRCALPHA/ONE = 加法。它自己的 MSH 材質旗標
            // 只有 0x1(一般透明批),所以這是整份反編譯裡唯一一支被官方「硬改混色」的道具 —— 走 alpha blend
            // 的話夜裡的城市燈火會變成暗剪影。Speed = 0:這筆只帶 render mode,天際線不捲 UV。
            new Target("SCN0016", "CHENGSHI", -1, Vector2.zero, RenderMode.AdditiveOverlay),
            // SCN0016 spotlights (JIGUANG1/2/3): guang1_.dds has a narrow (~3-texel) alpha edge, so a plain additive
            // beam reads hard at its left/right. SpotGlow blurs the texture along its width to spread the light
            // sideways into a soft falloff. Speed=0 — these don't UV-scroll; the entry only carries the render mode.
            new Target("SCN0016", "JIGUANG1", -1, Vector2.zero, RenderMode.SpotGlow),
            new Target("SCN0016", "JIGUANG2", -1, Vector2.zero, RenderMode.SpotGlow),
            new Target("SCN0016", "JIGUANG3", -1, Vector2.zero, RenderMode.SpotGlow),
            // SCN0022 坟墓 射光 (sheguang1-3 = light-ray spotlights): the MSH loader makes these DXT3 glows ADDITIVE
            // (LooksLikeAdditiveGlow: bright RGB + all-soft alpha), but that's the SCN0015-窗光 false-positive — additive
            // reads as a hard, edgy beam with no transparency variation. Force TWO-SIDED standard alpha-blend so the ray
            // is a soft translucent shaft (the sweep must not cull edge-on). gui/gui2 (LABA11/12) are NOT here — they're
            // rebuilt as camera-facing billboards (SpawnSceneGhosts), which set their own alpha-blend material.
            new Target("SCN0022", "SHEGUANG",  -1, Vector2.zero, RenderMode.AlphaBlendOverlay),
            new Target("SCN0022", "SHEGUANG2", -1, Vector2.zero, RenderMode.AlphaBlendOverlay),
            new Target("SCN0022", "SHEGUANG3", -1, Vector2.zero, RenderMode.AlphaBlendOverlay),
            // SCN0014 海底 projector beams (TOUYINGGUANG_.DDS): the stage-centre GUANG prop and the beam material
            // inside the spinning TV prop. Both were wrong before, but by DIFFERENT heuristics — the texture is a
            // bright (meanLum 186) 82%-soft-alpha glow, and NewMapobjMat's ternary is
            // `alpha && additiveGlow && !cutout ? additive : cutout ? alpha-test : ...`:
            //   • GUANG = its own 14-vertex mesh → not volumetric → additiveGlow WINS → additive, and the overlapping
            //     beam quads saturate to a solid white blob.
            //   • TV's beam = ONE range inside a 773-vertex submesh whose bounds are 298×262×452, and `volumetric`
            //     is computed PER-SUBMESH (outside the range loop) → cutout WINS over additive for every range that
            //     has alpha → the 12-triangle beam got clip(a-0.5)+ZWrite On, i.e. a hard speckled edge.
            // Both read as "光沒去背". The OFFICIAL material flags cut through both: GUANG mat0 = 1 and TV mat1 = 1
            // (transparent batch = standard alpha blend), while TV's zhuanpan/gangjia/tv/touyingji_c are all 0
            // (opaque). Per-material so only the beam changes.
            // GUANG additionally SPINS: it is the 7th (U, 4×) target of FUN_004b0330 — see BeamSpinU. Its .mot only
            // carries ONE animated bone ('Box02', 550 keys of yaw) which orbits the whole prop around the stage, so
            // the light pattern turning about its own axis is this U scroll, nothing else.
            new Target("SCN0014", "GUANG", -1, BeamSpinU, RenderMode.OfficialMaterialAlpha),
            new Target("SCN0014", "TV", -1, Vector2.zero, RenderMode.OfficialMaterialAlpha),
            // SCN0025 春天 fountain water (CHUNTIANDONGHUA / SHUI_C_.DDS, official flags = 0x11 → transparent batch):
            // 227 verts / bounds 364×109×358 → volumetric, so like the TV beam it took the alpha-TEST branch, not the
            // additive one. Only 33% of SHUI_C_'s texels survive clip(a-0.5) and its RGB is near-white (meanLum 252,
            // 98% soft alpha), so the fountain rendered as a hard speckled white splash instead of flowing water.
            // It also FLOWS: FUN_004b0d20's last block sets texture-transform U=0, V += _DAT_00589044 (=0.05) every
            // 50 ms ⇒ +1.0 UV/s in V, wrapping at 1.0. Positive sign copied verbatim (like SCN0015's window beam).
            new Target("SCN0025", "CHUNTIANDONGHUA", -1, new Vector2(0f, 1.0f), RenderMode.OfficialMaterialAlpha),
            // SCN0028 北京之夜 (鸟巢) 噴水池 (NIAOCHAO/PENGSHUI / PENGSHUI_.DDS,官方材質旗標 = 0x1 → 透明批)。
            // 這支 mapobj 沒有 .mot、也沒有換幀序列 —— case 0x1c 載入的七個道具裡,chuan/feichuan 靠 .mot 飛,
            // deng1~4 靠換幀閃,只剩 pengshui 什麼都沒有;它的動全部來自 StageScene_UpdateBigBillboardSet_004b0fc0
            // 最後那段:每 100 ms 設 render state +0x48 |= 0x10000(貼圖座標轉換),U = 累加值、V = 0,
            // 累加量 _DAT_00589048 = 0.05、到 1.0 歸零 ⇒ +0.5 UV/s 在 U。U 是水流方向:mesh 的 U 跑到 1.364
            // (超過 1 → 貼圖在 U 上重複),V 只有 0.024~0.896 一段,所以捲 U 才是「水往下沖」,和 SCN0025 春天
            // 噴水池捲 V 是不同軸。正負號照抄官方(與 SCN0015/SCN0025 同慣例)。
            // OfficialMaterialAlpha:唯一那顆材質旗標 0x1 = 延後透明批 = 一般 SrcAlpha/InvSrcAlpha,
            // 不是加法、也不是 alpha-test —— 和 SCN0025 噴水池同一條規則,免得柔和水花被 clip 成硬白斑。
            new Target("SCN0028", "PENGSHUI_", -1, new Vector2(0.5f, 0f), RenderMode.OfficialMaterialAlpha),
            // SCN0024 雪景 探照燈 (XUEJING/DONGHUA / DENGGUANG_.DDS): 128×128 DXT3 的暖色光錐,alpha 最高只有 238、
            // 平均 34.8、45.5% 全透明 —— 又是 LooksLikeAdditiveGlow 的誤判樣本(亮 RGB + 全軟 alpha)。官方材質
            // 旗標 +0x194 = 0x1 = 延後透明批 = 一般 SrcAlpha/InvSrcAlpha,不是加法。這支只有一個材質,
            // 但仍用 per-material 的 OfficialMaterialAlpha,和 SCN0014/SCN0025 同一條規則。Speed=0:場景 0x18
            // 的每幀更新只換招牌貼圖,完全沒有 0x58/0x5c 貼圖座標寫入,光柱的動全部來自 .mot。
            new Target("SCN0024", "DONGHUA", -1, Vector2.zero, RenderMode.OfficialMaterialAlpha),
            // SCN0014 FUN_004b0330 coral glow: the three TREES scroll V at 1×, the three BRANCHES at 2× (verbatim
            // −t / −2t groups; the branches used to share the trees' rate).
            new Target(null, "SHANHU-BAI", -1, CoralV),
            new Target(null, "SHANHU-HONG", -1, CoralV),
            new Target(null, "SHANHU-LV", -1, CoralV),
            new Target(null, "SHANHUZHI-BAI", -1, CoralBranchV),
            new Target(null, "SHANHUZHI-HONG", -1, CoralBranchV),
            new Target(null, "SHANHUZHI-LV", -1, CoralBranchV),
        };

        /// <summary>LINEAR UV-scroll speed (UV/s) for a scene object/material slot, or Vector2.zero if it does not
        /// linearly scroll. Sine / DwellStep targets report zero here — their Speed means something else (rad/s,
        /// sweep rate), so handing it to a linear scroller would be wrong. Use <see cref="TryFindTarget"/> for those.</summary>
        public static Vector2 Find(string folder, string objectKey, int materialId = -1)
        {
            return TryFind(folder, objectKey, materialId, out var target) && target.Motion == Motion.Linear
                ? target.Speed : Vector2.zero;
        }

        /// <summary>The full entry for a scene object/material slot (any motion).</summary>
        public static bool TryFindTarget(string folder, string objectKey, out Target target, int materialId = -1)
        {
            return TryFind(folder, objectKey, materialId, out target);
        }

        public static RenderMode FindRenderMode(string folder, string objectKey, int materialId = -1)
        {
            return TryFind(folder, objectKey, materialId, out var target) ? target.Mode : RenderMode.KeepMaterial;
        }

        /// <summary>Does <paramref name="mode"/> apply to a material whose OFFICIAL flags are <paramref name="matFlags"/>
        /// (MSH record +0x194)? <see cref="RenderMode.OfficialMaterialAlpha"/> is the only PER-MATERIAL mode — it only
        /// touches materials the artist marked transparent, so a multi-material prop (SCN0014 TV: beam transparent,
        /// screen/frame/projector opaque) keeps its opaque parts opaque. Every other mode is prop-wide as before.</summary>
        public static bool AppliesToMaterial(RenderMode mode, uint matFlags)
            => mode != RenderMode.OfficialMaterialAlpha || MshLoader.IsOfficialTransparent(matFlags);

        public static bool UsesAdditiveOverlay(string folder, string objectKey, int materialId = -1)
        {
            return FindRenderMode(folder, objectKey, materialId) == RenderMode.AdditiveOverlay;
        }

        private static bool TryFind(string folder, string objectKey, int materialId, out Target target)
        {
            target = default;
            if (string.IsNullOrEmpty(objectKey)) return false;
            for (int i = 0; i < Targets.Length; i++)
            {
                var t = Targets[i];
                if (!string.IsNullOrEmpty(t.Folder) &&
                    !string.Equals(t.Folder, folder, System.StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(t.ObjectKey, objectKey, System.StringComparison.OrdinalIgnoreCase)) continue;
                if (t.MaterialId >= 0 && materialId >= 0 && t.MaterialId != materialId) continue;
                if (t.MaterialId >= 0 && materialId < 0) continue;
                target = t;
                return true;
            }
            return false;
        }
    }
}
