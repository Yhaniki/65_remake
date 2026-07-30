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
            // 官方旗標 0 = 不透明批:引擎連 ALPHABLENDENABLE 都沒開,更沒有 alpha test,貼圖的 alpha 通道**整個是死的**。
            // 我們的貼圖啟發式卻可能把「帶淡出漸層」的貼圖判成 cutout,於是 clip(a-0.5) 會在漸層的中段一刀切掉,
            // 留下貼圖最暗的那一列、還是 100% 不透明、邊緣沿著 texel 鋸齒 —— 畫面上就是一條硬邊深色帶。
            // 這個模式把 clip 關掉(_Cutoff = -1),回到官方的「照畫、不看 alpha」。
            ForceOpaque,
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

        /// <summary>
        /// 水面「蓋在地面上、但輸給場景所有透明道具」的佇列。SCN0004 的岸浪就靠這個位置。
        ///
        /// 場景一顆 SCENE.MSH 會依貼圖 alpha 分成兩種材質(SceneLoader):
        ///   • 不透明/cutout → Sdo/SceneVertexCutout,**AlphaTest 2450 且 ZWrite On**(沙 SHA*、甲板 DIBAN*、
        ///     房子 FANGZI*、島 HAIDAO11 …)
        ///   • 軟 alpha     → Sdo/SceneVertexAlpha,**Transparent 3000 且 ZWrite Off**(欄杆 LANGAN*、棚架 JIAZI、
        ///     房子基座 D1、陽傘 SAN*、椅子 YIZI*、稻草 DAOCAO、樹葉 YEZI/SHUYE …)
        /// 這一格(2450 < q < 3000)剛好把水面夾在兩者中間:
        ///   1. 排在不透明批之後 → 深度測試照常生效。浪片本來就疊在沙灘上方,於是**浪畫得到沙灘**;
        ///      而甲板/房子在浪前面的地方仍然由深度擋掉。
        ///   2. 排在場景透明批之前 → 欄杆/棚架/基座那些**不寫深度**的道具後畫,一律覆蓋水面 = 浪永遠不會
        ///      刷到棧橋欄杆與房子基座。深度救不了它們(ZWrite Off),只有佇列先後能。
        /// 把水面壓到 2400 會連第 1 點一起放棄(沙灘後畫、把浪整條蓋掉)——那正是「沙灘上沒有海浪」的成因。
        /// </summary>
        public const int WaterOverGroundQueue = 2600;

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
            // 軸的選擇有 mesh 依據:SEA 的 U 跑到 −1.772..2.312(在 U 上 tiling)、LANG 的 V 是 −0.215..0.617。
            //
            // ★ 「哪個相位打哪一片」曾經只是推論(反編譯把目標寫成 FUN_0041a100(0),四次呼叫參數都是 0,
            //   看不出差別)。已用線上版 sdo.bin 的機器碼釘死:那支是 __thiscall 的 GetMaterial(this, idx),
            //   **ECX 才是目標物件**,堆疊上的 0 只是材質索引(位元組 8b4120/8b4c2404/8b0488/c20400)。
            //   線上版 FUN_00969c70 內:
            //       00969cfe  mov ecx,[eax+0x0c]   ← objects[3]   → 00969d28 fstp [eax+0x5c]  V = −sin(快)×0.25
            //       00969d49  mov ecx,[eax+0x10]   ← objects[4]   → 00969d6f fstp [eax+0x58]  U = +sin(慢)×0.5
            //   兩塊材質記憶體不重疊,所以後者寫的 V=0 不會蓋掉前者 —— 兩片都真的在動。
            //   索引→名字的鏈也驗過:名稱表 0xb8f430 = sea_up/sea_down/chuan/beach/sea(.bin),
            //   0xb8f444 同序的 .msh = …/lang/sea,載入迴圈用同一個 edi 同時索引名稱表與物件陣列
            //   (無重排、無壓縮補位),故 objects[3]=LANG、objects[4]=SEA。
            //   資產側獨立佐證:wave_.dds 的 alpha 內容在 V 方向留了 0.496 tile 的透明跑道(官方 V 峰對峰
            //   0.5,差 0.8%),U 方向卻被逐像素貼齊圖案邊界(餘裕 0.005/0.0095 tile)—— 捲 U 會把浪沫切兩半。
            //
            // ★★ 最後由**執行期實測**一槌定音(tools/probe_scn0004_identify.js,線上版 sdo.bin 實機 Frida):
            //   hook GetMaterial 錄下每次回傳的材質物件,再從物件記憶體撈出貼圖檔名 ——
            //       寫 V = −sin(快 0.004)×0.25 的那次 → 材質是 **wave_.dds**       = LANG 岸浪
            //       寫 U = +sin(慢 0.001)×0.5  的那次 → 材質是 **haishuei2_.dds** = SEA  海面
            //   同一份 log 還順帶證實換幀:每 100 ms 那一幀會多出兩次呼叫,依序拿到 **b001.dds(SEA_UP)**
            //   與 **a001.dds(SEA_DOWN)** —— 兩片海床共用同一個索引、同一幀一起換,與反編譯的
            //   [esi+0x7c]/[esi+0x80] 順序一致。相位每次呼叫 +0.004 / +0.001 也逐輪對上。
            //   → 這條對應已無推論成分,不必再查。
            //
            // ★ 官方每次只寫**材質槽 0**(push 0);這裡用 MaterialId = -1(整支道具的所有材質)。
            //   SEA.MSH 與 LANG.MSH 各只有一個材質(haishuei2_.dds / wave_.dds),所以實務上等價。
            //   哪天 MshLoader 把它們拆成多段(例如依 alpha 分批),這裡就會與官方分岔 —— 屆時要改回只驅動槽 0。
            //
            // 每幀累加換算成秒沿用本檔既有的官方幀率慣例(SCN0011 CAIDAI 的 0.003×593):
            //   浪 0.004×593 = 2.372 rad/s(週期約 2.65 s)、海 0.001×593 = 0.593 rad/s(週期約 10.6 s)。
            // ★ 已在線上版 sdo.bin 實測驗證(tools/measure_scn0004_wave.py,輪詢相位 + hook FUN_00969c70 對帳):
            //   場景更新函式的呼叫率瞬時值 530~689/s、**中位數 625**(n=11 個 2 秒區間),換算岸浪 2.501 /
            //   海面 0.625 rad/s。與此處的 593 慣例差 5.2%,而呼叫率自身漂移就有 ±13% —— 差距埋在雜訊裡,
            //   故維持 593 不動(也讓本檔對 SCN0011/0012/0013/0029 的慣例保持一致)。
            //   官方宣告 D3DPRESENT_INTERVAL_IMMEDIATE、主迴圈是 PeekMessage busy-loop,沒有任何 frame
            //   limiter,所以「機器越快水流越快、換幀動畫卻釘死 100 ms」是官方設計的固有性質:
            //   岸浪週期 ~2.5 s 對換幀一輪 3.2 s,兩者永遠合不上,而且比例隨機器而變。**這不是 remake 的 bug。**
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
            // ★ Queue = WaterOverGroundQueue(2600):岸浪要同時滿足兩件互相打架的事 ——「打在沙灘上」與
            //   「不刷到棧橋欄杆/房子基座」。之前以為場景是單一佇列、分不開,所以只能二選一(壓 2400 = 保住
            //   房子但沙灘上的浪整條不見,正是使用者回報的症狀)。實機探針(Scn0004CaptureTest 逐 submesh 印
            //   shader/queue)推翻了這個前提:SCENE.MSH 被 SceneLoader 依貼圖 alpha 拆成兩批 ——
            //     沙 SHA*/甲板 DIBAN*/房子 FANGZI* → SceneVertexCutout,2450,**ZWrite On**
            //     欄杆 LANGAN*/棚架 JIAZI/基座 D1/陽傘 SAN* → SceneVertexAlpha,3000,**ZWrite Off**
            //   當初「浪蓋到棧橋與房子基座」的真正原因是後面這批不寫深度、又跟浪同在 3000,勝負只看排序;
            //   不是「深度測試擋不住」。把浪放進 2450 與 3000 中間那格就兩件事一起成立:
            //     不透明批先畫 → 深度測試生效,浪疊在沙灘上方所以蓋得到沙、被甲板/房子正確擋住;
            //     浪再畫 → 場景透明道具最後畫,一律覆蓋浪 = 欄杆與基座永遠乾淨。
            //   詳見 WaterOverGroundQueue 的註解。
            // ★ SEA(大洋)維持預設佇列(3000)不動:它是平躺在 y = −48.9 的一張平面,比棧橋甲板低 49 單位,
            //   相機一定在水面之上,正常深度測試本來就不會讓它爬上甲板;沿岸低於水面的沙灘則被它正確蓋住
            //   (= 海水蓋在沙灘上)。壓它反而會變成沙灘蓋掉海水 —— 這條 0032d6f 已經用截圖踩過一次。
            Target.Sine("SCN0004", "SEA",  new Vector2(0.5f, 0f), new Vector2(0.593f, 0f),
                        RenderMode.OfficialMaterialAlpha),
            Target.Sine("SCN0004", "LANG", new Vector2(0f, -0.25f), new Vector2(0f, 2.372f),
                        RenderMode.OfficialMaterialAlpha, WaterOverGroundQueue),
            // ── SCN0004 淺灘海床 SEA_DOWN:那條「黑色的邊界」───────────────────────────────────────
            // A001..A032(32 幀焦散動畫)的下緣是一段**淡出漸層**:第 0..95 列 alpha 255(藍綠焦散 → 橄欖),
            // 第 96..127 列 alpha 220→34、顏色是靠岸的濕沙。官方材質旗標 = 0 = 不透明批,引擎不看 alpha,
            // 整片照畫,所以那段漸層是「顏色慢慢收進沙灘」的過渡。
            // 我們的貼圖啟發式把它判成 Cutout,clip(a-0.5) 正好切在漸層中段 —— 保留下來的最後一列是整張圖
            // **最暗**的橄欖褐、而且 100% 不透明,邊緣還沿 texel 鋸齒。實機分層截圖(只留 SEA_DOWN)拍得一清二楚:
            // 沿著海岸線一條硬邊深褐帶,就是使用者說的黑色邊界。ForceOpaque 把 clip 關掉即可。
            // 不動 UV(這片是換幀動畫不是捲動),所以振幅/速度都是 0 —— Animates == false,只帶 RenderMode。
            new Target("SCN0004", "SEA_DOWN", -1, Vector2.zero, RenderMode.ForceOpaque),
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
            // SCN0019 舞鬥競技場 場後的四支聚光燈 (PK/GUANG1..4)。和 SCN0016 的 JIGUANG 是同一種東西:
            // 4 頂點的光錐 quad、貼一張全軟 alpha 的亮色圖 (dengzhu_.dds,soft/visible = 1.000、平均 32.4)、
            // 各自帶 .MOT 掃動、官方材質旗標 0x1。這種貼圖必然被 LooksLikeAdditiveGlow 判成加法,而純加法
            // 會讓光錐左右兩側出現硬邊 —— 使用者說的「後面的聚光燈很硬」。走和其他聚光燈一樣的 SpotGlow:
            // 沿光錐寬度(U)把光暈往外抹開,內部核心不動、只在邊緣補上漸層衰減。
            //
            // 逐字比照 SCN0016 的 JIGUANG 三筆(不另外調 shader 的 _Spread):使用者要求「跟其他聚光燈
            // 用一樣方式」。註:dengzhu_.dds 是「一張圖並排兩個光錐」(左 U 0.125~0.375、右 0.625~0.875),
            // GUANG1/2 取左錐(mesh U 0.085~0.418)、GUANG3/4 取右錐(0.590~0.923);shader 預設的 0.2 橫向
            // 模糊在 GUANG3/4 右緣會繞回 0.123,離左錐起點 0.125 只差 0.002 —— 貼邊但沒吃到,所以安全。
            new Target("SCN0019", "GUANG1", -1, Vector2.zero, RenderMode.SpotGlow),
            new Target("SCN0019", "GUANG2", -1, Vector2.zero, RenderMode.SpotGlow),
            new Target("SCN0019", "GUANG3", -1, Vector2.zero, RenderMode.SpotGlow),
            new Target("SCN0019", "GUANG4", -1, Vector2.zero, RenderMode.SpotGlow),
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
