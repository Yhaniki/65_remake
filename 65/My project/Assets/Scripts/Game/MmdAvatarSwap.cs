using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Sdo.Osu;
using Sdo.Settings;

namespace Sdo.Game
{
    /// <summary>
    /// 「用 MMD 模型顯示角色」的執行期服務。要顯示 MMD 的角色（跳舞的、房間走路的、以及三個各自渲一張
    /// RenderTexture 的頭貼/預覽：男女選擇預覽、房間頭貼、結算左邊頭像）都把 SDO 的身體藏起來、改畫一個
    /// MMD 模型。SDO 的 <see cref="SdoAvatar"/> 仍然活著當**動作驅動器**，所以跳的還是同一套 MOT/DPS。
    ///
    /// <b>這是兩個互相獨立的功能</b>（見 <see cref="MmdDisplayPolicy"/>）：
    ///   ① <b>我要用 MMD 模型</b> —— 在設定面板選一個模型就是要用（「(不使用)」＝維持 SDO 原角色）。
    ///      沒有另外的總開關：選了就是要用。
    ///   ② <b>我要看到別人的 MMD 模型</b> —— <c>mmdShowOthers</c>。別人身上畫的**永遠只可能**是他自己
    ///      宣告的那個模型；他沒穿就是他的 SDO 穿搭，絕不會拿我選的模型頂上去。
    ///
    /// <b>設定全部在 config.ini 的 <c>[Mmd]</c> 區</b>（UI：開場設定面板的「MMD」分頁）。這個類別不畫任何 UI，
    /// 只在 <see cref="Update"/> 比對 <see cref="RoomConfig"/> 的值有沒有變，變了就套用 —— 所以面板拉滑桿當場
    /// 看得到、手改 config.ini 重開也一樣。（改版前這裡掛著一塊自己畫的 IMGUI 除錯面板加 F7/F9/F10，值只活在
    /// 記憶體裡，關掉遊戲就沒了。）
    ///
    /// 用哪個模型不是寫死的：<c>DATA/MODEL/</c>（開發樹 <c>assets/MODEL/</c>）底下每個含 .pmx 的資料夾都是
    /// <see cref="MmdModelCatalog"/> 的一筆，設定面板那一列就是這份清單（<see cref="StartupConfigSchema.MmdModelsProvider"/>
    /// 在 <see cref="Boot"/> 接上去）。每個模型只解析一次並快取，換來換去不會重複付解析成本。
    ///
    /// 自己開機（<see cref="Boot"/>）；各個生成點只要呼叫 <see cref="Register"/>。里程碑訊息走
    /// <see cref="SdoLog.Note"/>，才會落進專案的 log.txt（它會丟掉一般的 Debug.Log）。
    /// </summary>
    public sealed class MmdAvatarSwap : MonoBehaviour
    {
        /// <summary>
        /// 一隻登記過的角色。要畫哪一具身體由 <see cref="MmdDisplayPolicy"/> 決定,而它只看兩件事:
        ///   • <see cref="Remote"/> —— 這是別人還是本機玩家。**別人永遠只可能畫他自己宣告的模型**
        ///     (<see cref="Pack"/>);他沒宣告就是他的 SDO 穿搭,絕不回退到本機選的那個。
        ///     (踩過:遠端與本機共用「沒 packId ⇒ 用設定裡選的」那條回退路徑 → 同房沒穿 MMD 的人
        ///      全被畫成我自己的模型。)
        ///   • 本機這邊選了哪個模型(<c>RoomConfig.mmdModel</c>)。
        ///
        /// 遠端的模型本機還沒有時 <see cref="Failed"/> 不會被設起來 —— 它不是失敗,是「還沒到」:
        /// 這隻角色就停在自己的 SDO 穿搭上,等 <see cref="OnPackInstalled"/> 把它接上去。
        /// </summary>
        private sealed class Reg
        {
            public SdoAvatar Avatar;
            public MmdAvatar Mmd;
            public bool Failed;
            public bool Cloth = true;
            public bool Remote;          // 這一隻是別人(不是本機玩家)
            public string Pack = "";     // 遠端玩家身上的模型 packId(本機一律空)
            public string BuiltFrom;     // 現在畫出來的這具身體是從哪個 .pmx 建的(換模型時比對用)
                                         // 🔴 是 .pmx 路徑不是資料夾:一個資料夾可以裝好幾個模型(見 MmdModelCatalog)
            public bool Shown;           // 上一次套用的結果是「畫 MMD」嗎(只有變了才寫 log)
            public bool NotedReopen;     // 「MMD 在畫、SDO 那具卻又亮起來」已經寫過一行了(只寫一次,見 HideSdoBody)
        }
        private readonly List<Reg> _regs = new List<Reg>();

        private static MmdAvatarSwap _inst;
        private static List<MmdModelCatalog.Entry> _models = new List<MmdModelCatalog.Entry>();
        private static int _sel = -1;                    // index into _models; -1 = nothing installed
        // Parsed models, keyed by .pmx path — switching back to a model you already looked at is free (and MmdAvatar's
        // own mesh/material cache is keyed by the PmxLoader instance, so it stays warm too).
        private static readonly Dictionary<string, PmxLoader> _parsed = new Dictionary<string, PmxLoader>();
        private static readonly HashSet<string> _parseFailed = new HashSet<string>();
        private static string _status = "boot";
        private static string _lastError = "";

        private static MmdModelCatalog.Entry Sel => (_sel >= 0 && _sel < _models.Count) ? _models[_sel] : null;

        // Write to BOTH the editor console (Debug.Log) and log.txt (SdoLog.Note) — the project's SdoLog drops
        // info-level Debug.Log, so a plain Debug.Log milestone would never appear in the file the user inspects.
        private static void Log(string m) { Debug.Log(m); SdoLog.Note("mmd", m); }

        // AfterSceneLoad, and it has to stay that way: this reads RoomConfig, and SettingsBootstrap loads config.ini at
        // BeforeSceneLoad. Move this earlier and every [Mmd] value read here is the compiled-in default instead of the
        // player's — the swap would come up off, and the prewarm below (which only starts when it is on) never runs.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            var inst = Ensure();

            // The 設定面板 lists the installed models — it lives in Sdo.Settings, which cannot reference Sdo.Game,
            // so hand it the scan result instead (see StartupConfigSchema.MmdModelsProvider).
            StartupConfigSchema.MmdModelsProvider = () => ModelNames();
            // 同樣是注入(Sdo.Settings 不能反向參照 Sdo.Game):設定面板 MMD 分頁那一列「存檔 / 還原」按鈕。
            StartupConfigSchema.MmdProfileSave = () =>
            {
                string p = SaveProfile();
                return p != null ? "物理已存成 " + Path.GetFileName(p) + "（這個模型專屬）" : (_lastError.Length > 0 ? _lastError : "存檔失敗");
            };
            StartupConfigSchema.MmdProfileDelete = () =>
                DeleteProfile() ? "已刪掉 physics.ini → 回到 .pmx 轉換值" : "本來就沒有存檔（現在跑的就是轉換值）";
            StartupConfigSchema.MmdProfileState = () => ProfileStatus();

            // Command line still wins for a one-off run without touching config.ini: "-mmd" forces MMD display on,
            // "-mmdmodel <name>" (or "=<name>") picks which installed model, by name or any substring of it.
            // ("-mmd" 現在的意思是「這次啟動就用選中的那個模型」—— 沒有總開關可以開了,它改寫的是 mmdModel。)
            bool cli = false; string want = null;
            try
            {
                var argv = System.Environment.GetCommandLineArgs();
                for (int i = 0; i < argv.Length; i++)
                {
                    if (argv[i] == "-mmd") cli = true;
                    else if (argv[i].StartsWith("-mmdmodel=")) want = argv[i].Substring("-mmdmodel=".Length);
                    else if (argv[i] == "-mmdmodel" && i + 1 < argv.Length) want = argv[++i];
                }
            }
            catch { }

            if (cli && RoomConfig.IsMmdNone(RoomConfig.mmdModel)) RoomConfig.mmdModel = want ?? "";   // -mmd → 這次啟動就用選中的
            // 著色後端要在 prewarm（＝第一次建共用材質）之前就定下來，不然預熱出來的是另一個後端的材質，
            // 第一隻角色一上場就得整批重建 —— 預熱等於白做。
            MmdAvatar.UseLilToon = RoomConfig.mmdLilToon;
            Rescan(want ?? RoomConfig.mmdModel);
            if (want != null && Sel != null) RoomConfig.mmdModel = Sel.Name;   // -mmdmodel 也走設定值,之後的比對才不會一直當成「變了」
            inst._applied = Snapshot();
            Log($"[mmd] armed — 設定在 config.ini [Mmd] / 開場設定面板的 MMD 分頁。" +
                $"我自己={(UseLocalMmd ? "MMD" : "SDO 原角色")}, 顯示他人模型={(RoomConfig.mmdShowOthers ? "開" : "關")}. " +
                (cli ? "-mmd → 這次啟動強制用模型. " : "") + ModelSummary());
            if (UseLocalMmd) inst.StartCoroutine(inst.PrewarmCo());
        }

        // 開機就先把「每個模型只做一次」的那兩段做掉:.pmx 解析,以及共用的 mesh/材質/貼圖。
        //
        // 這是 MMD 顯示唯一真正慢的地方,而且量過:初音 = 解析 89 ms + 共用資產 1438 ms(其中貼圖解碼就佔 1401 ms
        // —— 十張 2048² PNG),之後**每一隻舞者只要 17~26 ms**。不先做的話這 1.5 秒會落在第一次進房間/進歌的當下
        // (rig 是跟著舞者生成的),看起來就是「換場景要重新讀取」。開機這裡本來就有載入畫面,藏得住。
        //
        // 貼圖分幀解碼,但用**時間預算**而不是「一張一幀」,而且預算要開得大。實測(打包版,開機掃歌中):
        //   一張一幀   → 24 張分 24 幀 = 10.5 秒,預覽 6.4 秒就生成了 → 預熱整個白做(第一隻付 518 ms)
        //   預算 25 ms → 14 幀 = 7.0 秒,還是差一點點輸(第一隻付 255 ms)
        // 關鍵是**開機那幾幀本來就長達 ~500 ms**(掃歌),所以「讓一幀」的代價不是 16 ms 而是半秒 —— 讓越多次越慢。
        // 解碼工作總共才 ~450 ms,預算開到 150 ms 就是分 3~4 幀做完(≈2 秒內),而在一個已經 500 ms 的幀裡多花
        // 150 ms 是看不出來的;yield 存在的意義只是讓開機進度條還會動,不是讓每幀都很短。
        private const float PrewarmBudgetMs = 150f;

        private System.Collections.IEnumerator PrewarmCo()
        {
            yield return null;
            float t0 = Time.realtimeSinceStartup;
            var pmx = SharedPmx();
            if (pmx == null || Sel == null) yield break;

            int n = MmdAvatar.TextureCount(pmx), frames = 1;
            float frameStart = Time.realtimeSinceStartup;
            for (int i = 0; i < n; i++)
            {
                if (!MmdAvatar.PrewarmTexture(pmx, Sel.Dir, i, Sel.Root)) break;
                if ((Time.realtimeSinceStartup - frameStart) * 1000f < PrewarmBudgetMs) continue;
                yield return null;
                frameStart = Time.realtimeSinceStartup;
                frames++;
            }
            MmdAvatar.Prewarm(pmx, Sel.Dir, Sel.Root);   // 材質 + mesh(貼圖此時全在快取裡)
            Log($"[mmd] prewarm 完成 ({(Time.realtimeSinceStartup - t0) * 1000f:F0} ms, {n} 張貼圖分 {frames} 幀解碼) — " +
                "之後每隻舞者只付自己的骨架/布料(實測 ~11 ms)");
        }

        /// <summary>Re-read the installed models from disk (DATA/MODEL/*, dev assets/MODEL/*, …) and select
        /// <paramref name="want"/> (name or substring) — or keep the current selection if it survived the rescan.</summary>
        public static void Rescan(string want = null)
        {
            string keep = string.IsNullOrEmpty(want) ? Sel?.Name : want;
            _models = MmdModelCatalog.Discover(ModelRoots());
            _sel = MmdModelCatalog.IndexOf(_models, keep);
            _status = _models.Count == 0 ? "NO MODEL" : "found";
            if (_models.Count == 0)
                _lastError = "沒有模型 — 放一個 MMD 資料夾(含 .pmx)到 " + string.Join(" 或 ", new List<string>(ModelRoots()).ToArray());
        }

        /// <summary>設定面板「我用的模型」那一列的選項:<b>第一個永遠是「(不使用)」</b>(＝維持 SDO 原角色),
        /// 後面才是裝了哪些模型。沒有另外的總開關 —— 選了模型就是要用它,而「不用」也是一個選項。</summary>
        public static string[] ModelNames()
        {
            var names = new string[_models.Count + 1];
            names[0] = RoomConfig.mmdModelNone;
            for (int i = 0; i < _models.Count; i++) names[i + 1] = _models[i].Name;
            return names;
        }

        private static string ModelSummary()
            => _models.Count == 0
                ? "NO MODEL — 放 .pmx 資料夾到 DATA/MODEL/ (開發樹: assets/MODEL/)"
                : $"models={_models.Count}, using '{Sel.Name}' ({Path.GetFileName(Sel.PmxPath)})";

        private static MmdAvatarSwap Ensure()
        {
            if (_inst != null) return _inst;
            var go = new GameObject("MmdAvatarSwap");
            DontDestroyOnLoad(go);
            _inst = go.AddComponent<MmdAvatarSwap>();
            return _inst;
        }

        /// <summary>Register an in-scene dancer as swappable. Called right after each SDO dancer is built. Eagerly parses
        /// the model so its "[mmd] parsed …" (or "not found") confirmation appears on room entry, before any toggle. If
        /// MMD display is already on, the new dancer is swapped immediately.
        /// <paramref name="cloth"/> false → build this one WITHOUT the hair/skirt sim (the head portraits: the sway is
        /// invisible at that size and the cloth solver is the most expensive part of a rig).</summary>
        public static void Register(SdoAvatar avatar, bool cloth = true) => Register(avatar, false, "", cloth);

        /// <summary>
        /// 登記一隻**遠端**玩家的角色,連同他外觀宣告的模型 <paramref name="packId"/>。
        ///
        /// 本機還沒有那份模型時,這隻就停在他的 SDO 穿搭上 —— 那**不是退化的畫面**,那就是他的樣子:
        /// MMD 模型本來就是疊在 SDO 骨架上顯示的(SDO 那隻永遠是動作驅動器),所以「還沒下載完」的
        /// 正確畫面天生就是他的穿搭,沒有空白、沒有替身。模型到了之後 <see cref="OnPackInstalled"/>
        /// 直接把身體換掉,**不重建**這隻角色(位置、朝向、正在播的動作全都留著)。
        /// </summary>
        public static void RegisterRemote(SdoAvatar avatar, string packId, bool cloth = true)
            => Register(avatar, true, packId ?? "", cloth);

        private static void Register(SdoAvatar avatar, bool remote, string packId, bool cloth)
        {
            if (avatar == null) return;
            var inst = Ensure();
            inst._regs.RemoveAll(r => r.Avatar == null);   // drop destroyed dancers (scene changes / rebuilds)
            var existing = inst._regs.Find(r => r.Avatar == avatar);
            if (existing != null)
            {
                if (existing.Remote == remote && string.Equals(existing.Pack, packId, StringComparison.Ordinal)) return;
                existing.Remote = remote;
                existing.Pack = packId;      // 同一隻改穿別的模型 → 重建它的身體(不動 SDO 驅動器)
                existing.Failed = false;
                inst.DropBody(existing);
            }
            else inst._regs.Add(new Reg { Avatar = avatar, Cloth = cloth, Remote = remote, Pack = packId ?? "" });

            var reg = existing ?? inst._regs[inst._regs.Count - 1];
            // 這一隻根本不會用到 MMD(我沒選模型 / 他沒穿 / 我不看別人的)→ 什麼都別做,連 log 都不寫。
            // 兩個功能都關著時這整條要是 0 成本。
            if (SourceOf(reg) == MmdSource.Sdo) return;
            Log($"[mmd] registered dancer '{avatar.name}'"
                + (reg.Remote ? " (別人的模型 " + Short(reg.Pack) + ")" : " (我自己選的模型)")
                + $" — now {inst._regs.Count} swappable");
            inst.Apply(reg);
        }

        /// <summary>packId 的短寫法(log 用) —— 完整的 40 字在一行 log 裡只會擋住真正要看的東西。</summary>
        private static string Short(string packId)
            => string.IsNullOrEmpty(packId) ? "(本機)"
             : (packId.Length > SongPackId.Prefix.Length + 8 ? packId.Substring(SongPackId.Prefix.Length, 8) : packId);

        /// <summary>丟掉這一隻現在畫出來的 MMD 身體(SDO 驅動器不動)。換模型 / 重建時用。</summary>
        private void DropBody(Reg r)
        {
            // SDO 那具要在 MMD 還「在」的時候恢復:Destroy 要到幀尾才生效,先丟再掃的話
            // 這一趟會把正在被銷毀的 MMD 渲染器一起打開(見 SetSdoBodyVisible 的排除條件)。
            if (r.Avatar != null) SetSdoBodyVisible(r, true);
            if (r.Mmd != null) Destroy(r.Mmd.gameObject);
            r.Mmd = null;
            r.BuiltFrom = null;
            r.Shown = false;   // 重建之後那一行「MMD shown」還是要出現(log 只在狀態變了才寫)
            r.NotedReopen = false;
        }

        /// <summary>
        /// 這個 packId 的模型剛裝好(下載完成)→ 把所有在等它的角色接上去。
        ///
        /// **不重建角色**,只是把 SDO 的身體藏起來、把 MMD 的身體建出來掛上去 —— 位置、朝向、
        /// 正在播的動作都在 SDO 那隻身上,而那隻自始至終沒有動過。所以換上去的那一幀,人不會瞬移、
        /// 不會回到待機、也不會有一幀空白。
        /// </summary>
        public static void OnPackInstalled(string packId)
        {
            if (_inst == null || string.IsNullOrEmpty(packId)) return;
            MmdModelStore.Forget(MmdModelStore.NetDirFor(packId));
            _inst._regs.RemoveAll(r => r.Avatar == null);
            int n = 0;
            foreach (var r in _inst._regs)
            {
                if (r.Mmd != null || !r.Remote || !string.Equals(r.Pack, packId, StringComparison.Ordinal)) continue;
                r.Failed = false;
                if (_inst.Apply(r)) n++;
            }
            if (n > 0) Log($"[mmd] 模型 {Short(packId)} 裝好了 → {n} 隻角色當場換上(沒有重建)");
        }

        /// <summary>
        /// 現在有哪些遠端模型是「有人穿著、但本機還沒有」的 —— 傳輸編排(<c>NetModelTransfer</c>)
        /// 拿它決定要去跟 server 要什麼。<c>mmdShowOthers</c> 關掉時一律是空的(不看別人的就不該產生任何流量)。
        /// </summary>
        public static void CollectMissingPacks(List<string> into)
        {
            if (into == null) return;
            into.Clear();
            if (_inst == null || !RoomConfig.mmdShowOthers) return;
            foreach (var r in _inst._regs)
            {
                if (r.Avatar == null || r.Mmd != null || !r.Remote || string.IsNullOrEmpty(r.Pack)) continue;
                if (MmdModelStore.DirForPack(r.Pack, _models) != null) continue;   // 其實有,只是還沒套上
                if (!into.Contains(r.Pack)) into.Add(r.Pack);
            }
        }

        /// <summary>The MMD body currently DISPLAYED for <paramref name="avatar"/>, or null when the native SDO body is
        /// the one on screen. The head-portrait cameras (room 頭貼 / 結算頭貼) ask this so they can frame the MMD head —
        /// the SDO FACE/HAIR geometry they normally measure is hidden while MMD is shown.</summary>
        public static MmdAvatar ActiveFor(SdoAvatar avatar)
        {
            if (_inst == null || avatar == null) return null;
            foreach (var r in _inst._regs)
                if (r.Avatar == avatar) return (r.Mmd != null && r.Mmd.Visible) ? r.Mmd : null;
            return null;
        }

        /// <summary>
        /// 手部光條要從哪兩根骨長出來 —— 交給 <see cref="HandRibbon.Source"/>,它每幀問一次。
        ///
        /// MMD 顯示開著時,畫面上的手是 MMD 身體的手,而它跟驅動它的 SDO 骨架**長度不一樣**:retarget 只把 MMD 的
        /// 骨頭指向跟 SDO 一樣的方向,骨頭多長是模型自己的事(初音的肩→手腕鏈只有 SDO 的 77%,等高縮放後差 4.33 ≈
        /// 身高的 8%)。所以掛在 SDO 骨頭上的光條會在畫面上那隻手外面浮一截 ——「手的光沒接好,有一段隔空」。
        ///
        /// 沒有 MMD 身體(關掉顯示 / 遠端模型還沒到 / 那個模型缺手骨)就回 false,光條照舊用它自己的 SDO 錨點。
        /// </summary>
        public static HandRibbon.BoneSource HandSourceFor(SdoAvatar avatar, bool left)
            => (out Transform h, out Transform f) =>
            {
                var m = ActiveFor(avatar);
                if (m != null) return m.TryHandBones(left, out h, out f);
                h = null; f = null; return false;
            };

        // ---------------------------------------------------------------- config.ini → 場上
        // 設定的快照。面板改了值就直接寫進 RoomConfig 的 static 欄位(沒有事件可以訂閱),所以這裡每幀比一次:
        // 12 個欄位的比對比任何一種通知機制都便宜，而且手改 config.ini 之後重讀也同樣會生效。
        private struct Snap
        {
            public bool ShowOthers, Toon, Outline, Sphere, Physics, Aim, RootMove, FlipV, LilToon;
            public string Model;
            public float Grav, Stiff, Col, Scale;
        }
        private Snap _applied;

        private static Snap Snapshot() => new Snap
        {
            Model = RoomConfig.mmdModel ?? "", ShowOthers = RoomConfig.mmdShowOthers, LilToon = RoomConfig.mmdLilToon,
            Toon = RoomConfig.mmdToon, Outline = RoomConfig.mmdOutline, Sphere = RoomConfig.mmdSphere,
            Physics = RoomConfig.mmdPhysics, Aim = RoomConfig.mmdAim, RootMove = RoomConfig.mmdRootMotion,
            FlipV = RoomConfig.mmdFlipV,
            Grav = RoomConfig.mmdGravity, Stiff = RoomConfig.mmdStiffness, Col = RoomConfig.mmdColliderScale,
            Scale = RoomConfig.mmdScale,
        };

        /// <summary>「要摸磁碟才知道有沒有」的那幾件事多久做一次:遠端模型到了沒(Directory.Exists)、
        /// 以及 SDO 那具身體有沒有又冒出新的 MeshRenderer(GetComponentsInChildren)。
        /// 每幀做的話,每一隻永遠不會有模型的遠端角色都在白付這筆錢。
        /// <b>本機那條不吃這個節流</b> —— 它只是讀一個已經在記憶體裡的路徑,而且被停用的預覽一旦顯示出來
        /// 就要**下一幀**接上(性別選擇畫面切換性別的手感)。</summary>
        private const float ProbeSec = 0.25f;
        private float _nextProbeAt;

        private void Update()
        {
            var now = Snapshot();
            if (!string.Equals(now.Model, _applied.Model, System.StringComparison.OrdinalIgnoreCase))
            {
                // 換模型(含切到「(不使用)」):先照名字重選(掃過的清單裡找),再把每一隻**本機**已經建好的身體丟掉
                // 重建 —— SDO 驅動器不動,舞繼續跳。遠端角色穿的是他們自己的,與我選什麼無關,不能跟著動。
                _applied = now;
                Rescan(now.Model);
                Log($"[mmd] 我用的模型 → {(!UseLocalMmd ? "(不使用,維持 SDO 原角色)" : $"'{Sel.Name}' ({Path.GetFileName(Sel.PmxPath)})")}");
                RebuildWhere(r => !r.Remote);
            }
            else if (now.ShowOthers != _applied.ShowOthers)
            {
                // 「顯示他人模型」只牽動遠端角色 —— 我自己身上那具與這個開關無關。
                _applied = now;
                Log($"[mmd] 顯示他人模型 → {(now.ShowOthers ? "開" : "關")}");
                RebuildWhere(r => r.Remote);
            }
            else if (now.LilToon != _applied.LilToon)
            {
                // 換著色後端＝整批材質重建（材質是整個模型共用的，見 MmdAvatar.GetShared 的快取比對）。
                // 這是本機的顯示設定，所以連遠端角色也一起換 —— 我畫面上的每一隻都該長一樣。
                _applied = now;
                MmdAvatar.UseLilToon = now.LilToon;
                Log($"[mmd] 著色後端 → {(now.LilToon ? "lilToon（cel 陰影＋邊緣光）" : "Sdo/MmdModel（MMD 原本的畫法）")}");
                RebuildWhere(null);
            }
            else if (!Mathf.Approximately(now.Scale, _applied.Scale))
            {
                // 模型大小是「建的時候」決定的(骨架縮放 + 布料的重力/粒子半徑/速度上限全從它推),所以跟換模型一樣要重建。
                _applied = now;
                Log($"[mmd] scale → {now.Scale:F2}×");
                RebuildWhere(r => !r.Remote);
            }
            else if (!SameLooks(now, _applied)) { _applied = now; ApplyOpts(); }

            bool probe = Time.unscaledTime >= _nextProbeAt;   // 這一幀要不要做「摸磁碟」那幾件事(見 ProbeSec)
            if (probe) _nextProbeAt = Time.unscaledTime + ProbeSec;
            // 兩種「當時建不起來、之後可以」的角色都在這裡補上:
            //  ① GameObject 那時是停用的 —— 性別選擇畫面把兩具預覽都留著、只啟用選中的那一具,而骨架/Magica Cloth
            //     需要活著的 GameObject;顯示出來之後這裡把它換過去。
            //  ② 遠端的模型那時還沒下載完(OnPackInstalled 是主要路徑,這裡是它的保險)。
            // 另外順手把「MMD 已經在畫、SDO 卻又冒出 MeshRenderer」的情況壓回去(部件是分批長出來的:
            // 翅膀/道具會在角色建好之後才掛上,那時 Apply 早就跑完了 → 兩具身體疊在一起)。
            for (int i = 0; i < _regs.Count; i++)
            {
                var r = _regs[i];
                if (r.Avatar == null) continue;
                if (r.Mmd != null) { if (probe) HideSdoBody(r); continue; }
                if (r.Failed || !r.Avatar.gameObject.activeInHierarchy) continue;
                if (r.Remote && !probe) continue;  // 遠端那條要 Directory.Exists → 節流(本機那條每幀都跑)
                ResolveModel(r, out _, out string want, out _);
                if (want == null) continue;        // 沒東西可建(沒選模型 / 別人沒穿 / 他的模型還沒到)
                Apply(r);
            }
        }

        /// <summary>
        /// MMD 身體正在畫的時候,把 SDO 那具(後來才長出來的部件也算)關掉。
        ///
        /// 亮著的數量會被記一行 log(每隻只記一次):部件是分批長出來的(翅膀/道具在角色建好之後才掛上),
        /// 所以**第一次**幾乎一定會抓到幾個,那是正常的。但「兩具身體疊在一起」這個回報要能分辨
        /// 「壓不住」與「根本不是這條路的問題」—— 沒有這一行的話,兩者在 log 上長得一模一樣(都是安靜的)。
        /// </summary>
        private static void HideSdoBody(Reg r)
        {
            if (r.Avatar == null) return;
            var mmdRoot = r.Mmd != null ? r.Mmd.transform : null;
            int live = 0; string first = null;
            foreach (var rend in r.Avatar.GetComponentsInChildren<Renderer>(true))
            {
                if (mmdRoot != null && rend.transform.IsChildOf(mmdRoot)) continue;
                if (!rend.enabled) continue;
                if (first == null) first = rend.name + "(" + rend.GetType().Name + ")";
                live++;
                rend.enabled = false;
            }
            if (live > 0 && !r.NotedReopen)
            {
                r.NotedReopen = true;
                Log($"[mmd]   '{r.Avatar.name}': SDO 那具又亮了 {live} 個渲染器(第一個 {first})→ 已壓回");
            }
        }

        /// <summary>
        /// 開/關這一隻的 <b>SDO</b> 那具身體,回傳動到幾個渲染器。
        ///
        /// 🔴 <b>要抓 <see cref="Renderer"/>,不能只抓 <see cref="MeshRenderer"/>。</b>
        /// 舊版只關 MeshRenderer,靠的是「SDO 的部件一定是 MeshRenderer」這個**沒有人保證**的隱含假設
        /// (<see cref="SdoAvatar.AddGpuSmr"/> 就會生 SkinnedMeshRenderer,之後新增的部件也可能是別的型別)。
        /// 那個假設一旦破掉,症狀是**兩具身體完全重合疊在一起**,而 log 還老實寫著「N 個 MeshRenderer hidden」——
        /// 數字對、東西還在,根因完全指不到。抓 Renderer 基底就沒有這條路。
        ///
        /// MMD 那具自己的渲染器**必須跳過**:它是 driver 的子物件(<see cref="MmdAvatar.Build"/> 把
        /// rootGo 掛在 driver 底下),所以同一趟 <c>GetComponentsInChildren</c> 一定會掃到它,
        /// 不排除的話這個函式會把剛建好的 MMD 身體自己關掉。
        /// </summary>
        private static int SetSdoBodyVisible(Reg r, bool visible)
        {
            if (r.Avatar == null) return 0;
            var mmdRoot = r.Mmd != null ? r.Mmd.transform : null;
            int n = 0;
            foreach (var rend in r.Avatar.GetComponentsInChildren<Renderer>(true))
            {
                if (mmdRoot != null && rend.transform.IsChildOf(mmdRoot)) continue;
                rend.enabled = visible;
                n++;
            }
            return n;
        }

        // 「外觀/物理旋鈕」有沒有變(不含模型與「看別人的」—— 那兩個要走重建那條路)。
        private static bool SameLooks(in Snap a, in Snap b)
            => a.Toon == b.Toon && a.Outline == b.Outline && a.Sphere == b.Sphere && a.Physics == b.Physics
            && a.Aim == b.Aim && a.RootMove == b.RootMove && a.FlipV == b.FlipV
            && Mathf.Approximately(a.Grav, b.Grav) && Mathf.Approximately(a.Stiff, b.Stiff)
            && Mathf.Approximately(a.Col, b.Col);

        /// <summary>
        /// 本機玩家自己要不要用 MMD 模型:設定裡選了一個**裝得到**的模型就是要用
        /// (「(不使用)」＝不用)。沒有第二個總開關 —— 選了就是要用它。
        /// </summary>
        public static bool UseLocalMmd => !RoomConfig.IsMmdNone(RoomConfig.mmdModel) && Sel != null;

        /// <summary>把「我自己用不用 MMD 模型」開/關。<b>只動本機這一邊</b>(別人身上顯示什麼是
        /// <c>mmdShowOthers</c> 的事)。實際做的就是改 <c>RoomConfig.mmdModel</c> —— 那是唯一的事實來源。
        /// 遊戲裡走設定面板改那個值;這個函式留給測試與 <c>-mmd</c>。</summary>
        public static void SetEnabled(bool on)
        {
            var inst = Ensure();
            RoomConfig.mmdModel = on ? (Sel != null ? Sel.Name : "") : RoomConfig.mmdModelNone;
            Rescan(on ? RoomConfig.mmdModel : null);
            inst._applied = Snapshot();
            inst._regs.RemoveAll(r => r.Avatar == null);
            int n = 0;
            foreach (var r in inst._regs)
            {
                if (r.Remote) continue;
                inst.DropBody(r);
                r.Failed = false;
                if (inst.Apply(r)) n++;
            }
            Log($"[mmd] 我自己 → {(UseLocalMmd ? "MMD" : "SDO 原角色")} on {n} dancer(s)" +
                (n == 0 ? " (NO swappable dancer registered — enter a room or a song first)" : ""));
        }

        /// <summary>本機玩家自己身上畫的是 MMD 模型嗎(＝<see cref="UseLocalMmd"/>)。</summary>
        public static bool Enabled => UseLocalMmd;

        /// <summary>The selected model's .pmx, or null when none is installed (the tests skip themselves without it).</summary>
        public static string ModelPath => Sel?.PmxPath;

        /// <summary>本機現在選的那個模型的資料夾(沒裝模型時 null)。</summary>
        public static string ModelDir => Sel?.Dir;

        /// <summary>本機現在選的那個模型的顯示名稱(＝資料夾名)。</summary>
        public static string ModelName => Sel?.Name ?? "";

        /// <summary>
        /// 「我身上穿的模型」的 packId —— <c>setLook</c> 要送出去的那個值。
        ///
        /// 空字串代表「別人看到的是我的 SDO 穿搭」,發生在三種情況:我自己沒在用 MMD 模型、沒裝模型、
        /// 或設定裡把分享關掉了(<c>mmdShareModel=0</c>)。**分享關掉時連算都不算** ——
        /// 算 packId 要把整份模型讀過一遍,不打算分享就不該付那個成本。
        /// </summary>
        public static string LocalPackId
        {
            get
            {
                if (!UseLocalMmd) return "";
                if (!RoomConfig.mmdShareModel) return "";
                var e = Sel;
                return e == null ? "" : MmdModelStore.PackIdOf(e.Dir);
            }
        }

        /// <summary>The installed models, in panel order.</summary>
        public static IReadOnlyList<MmdModelCatalog.Entry> Models => _models;

        /// <summary>Write the cloth tuning the dancer is running right now (the values converted from the .pmx, plus the
        /// live gravity/stiffness/collider knobs from config.ini) into the model's own folder as physics.ini. From then
        /// on THAT file is what the model loads — on this machine and in a packaged build, since DATA/MODEL ships whole.
        /// Returns the file written, or null (no MMD body on screen / nothing writable).</summary>
        public static string SaveProfile()
        {
            var e = Sel;
            var cloth = FirstCloth();
            if (e == null || cloth == null) { _lastError = "沒有可存的布料(先在設定面板的「我用的模型」選一個)"; return null; }
            string path = MmdClothProfile.Save(e.Dir, cloth.CurrentSimulationFrequency, cloth.CurrentColliderMul, cloth.CurrentParts);
            _lastError = path == null ? "physics.ini 寫入失敗(資料夾唯讀?)" : "";
            Log(path != null ? "[mmd] 物理已存到 " + path : "[mmd] physics.ini 寫入失敗: " + MmdClothProfile.PathFor(e.Dir));
            return path;
        }

        /// <summary>Delete the model's physics.ini and rebuild → back to the values converted straight from the .pmx.</summary>
        public static bool DeleteProfile()
        {
            var e = Sel;
            if (e == null) return false;
            bool gone = MmdClothProfile.Delete(e.Dir);
            Log(gone ? "[mmd] 刪掉 physics.ini → 回到 .pmx 轉換值" : "[mmd] 本來就沒有 physics.ini");
            if (gone) RebuildWhere(null);
            return gone;
        }

        /// <summary>Is the displayed body running a physics.ini (vs the converted values)?</summary>
        public static string ProfileStatus()
        {
            var cloth = FirstCloth();
            if (cloth != null) return cloth.ProfilePath != null ? "physics.ini" : "轉換值";
            var e = Sel;
            return e != null && File.Exists(MmdClothProfile.PathFor(e.Dir)) ? "physics.ini (未套用)" : "轉換值";
        }

        // 只看**本機自己**那幾隻 —— physics.ini 是寫進「我選的那個模型」的資料夾,拿別人模型的布料
        // 去存我的模型,存出來的數字跟畫面上看到的是兩回事。
        private static MmdMagicaCloth FirstCloth()
        {
            if (_inst == null) return null;
            foreach (var r in _inst._regs)
                if (!r.Remote && r.Mmd != null && r.Mmd.Cloth != null) return r.Mmd.Cloth;
            return null;
        }

        /// <summary>Throw away every built MMD body and build it again — what to call after the model's physics.ini was
        /// changed on disk (hand-editing the file + this = see your edit without restarting).</summary>
        public static void Rebuild() => RebuildWhere(null);

        // 丟掉建好的 MMD 身體重建(換了模型 / 改了 physics.ini 之後)。
        // <paramref name="which"/> = 只重建符合的那些(null = 全部)。換自己的模型只重建本機那幾隻 ——
        // 遠端角色穿的是他們自己的,與我選什麼無關,牽動它們等於每隻白付一次 rig 成本。
        private static void RebuildWhere(System.Predicate<Reg> which)
        {
            var inst = Ensure();
            inst._regs.RemoveAll(r => r.Avatar == null);
            foreach (var r in inst._regs)
            {
                if (which != null && !which(r)) continue;
                inst.DropBody(r);
                r.Failed = false;
            }
            foreach (var r in inst._regs)
            {
                if (which != null && !which(r)) continue;
                inst.Apply(r);
            }
        }

        /// <summary>這一隻該畫哪一具身體(純決策,見 <see cref="MmdDisplayPolicy"/>)。</summary>
        private static MmdSource SourceOf(Reg r)
            => MmdDisplayPolicy.SourceFor(r.Remote, r.Pack, UseLocalMmd, RoomConfig.mmdShowOthers);

        // Swap one dancer. Building the MMD model is lazy (first time it's shown). Returns true if the dancer is live.
        private bool Apply(Reg r)
        {
            if (r.Avatar == null) return false;
            string dir = null, pmxPath = null, root = null;
            if (!r.Failed) ResolveModel(r, out dir, out pmxPath, out root);

            // 已經畫著的身體不是現在該畫的那一份(換了模型 / 別人換了 / 關掉了)→ 丟掉,下面重建或就此回 SDO。
            if (r.Mmd != null && (pmxPath == null || !string.Equals(r.BuiltFrom, pmxPath, StringComparison.OrdinalIgnoreCase)))
                DropBody(r);

            if (pmxPath != null && r.Mmd == null)
            {
                // An inactive dancer (the gender preview parks the unselected gender) can't be built yet — the rig and
                // Magica Cloth need a live GameObject. Leave it on its SDO body; Update() swaps it the moment it's shown.
                if (!r.Avatar.gameObject.activeInHierarchy) return true;

                var pmx = ParsePmxFile(pmxPath);
                if (pmx == null) { r.Failed = true; Debug.LogWarning("[mmd] 解析不了 " + pmxPath + " → staying on SDO body"); pmxPath = null; }
                else
                {
                    // 布料是建一隻 rig 最貴的一段 → 設定關掉布料時就整組不建(不是建了再關),換場景才會明顯變快。
                    r.Mmd = MmdAvatar.Build(r.Avatar, pmx, dir, r.Avatar.gameObject.layer, r.Cloth && RoomConfig.mmdPhysics, root);
                    if (r.Mmd == null) { r.Failed = true; _lastError = "MmdAvatar.Build returned null"; Debug.LogWarning("[mmd] build failed → staying on SDO body"); pmxPath = null; }
                    else { r.BuiltFrom = pmxPath; ApplyOptsTo(r.Mmd); }
                }
            }

            bool on = r.Mmd != null;
            // The portrait / preview cameras cull by LAYER, and a dancer's layer is assigned after its parts are built —
            // so keep the rig on whatever layer its driver ended up on (else the 頭貼 cam renders an empty RT).
            if (on) r.Mmd.SetLayer(r.Avatar.gameObject.layer);
            int n = SetSdoBodyVisible(r, !on);
            if (on) r.Mmd.SetVisible(true);
            // 只在真的變了才寫 log —— 這個函式是補建迴圈每 0.25 秒會回頭跑的。
            if (on != r.Shown)
                Log($"[mmd]   '{r.Avatar.name}': {(on ? "MMD shown" : "SDO shown")}, {n} SDO renderer(s) {(on ? "hidden" : "shown")}");
            r.Shown = on;
            return true;
        }

        private void ApplyOpts() { foreach (var r in _regs) if (r.Mmd != null) ApplyOptsTo(r.Mmd); }

        // 把 config.ini [Mmd] 的外觀/物理旋鈕套到一隻已經建好的身體上。
        private static void ApplyOptsTo(MmdAvatar m)
        {
            m.UseAim = RoomConfig.mmdAim;
            m.DriveRootTranslation = RoomConfig.mmdRootMotion;
            m.SetSphere(RoomConfig.mmdSphere);
            m.SetFlipV(RoomConfig.mmdFlipV);
            m.SetToon(RoomConfig.mmdToon);
            m.SetOutline(RoomConfig.mmdOutline);
            m.SetPhysics(RoomConfig.mmdPhysics);
            m.TunePhysics(RoomConfig.mmdStiffness, 0.6f, RoomConfig.mmdGravity);
            m.SetColliderRadius(RoomConfig.mmdColliderScale);
        }

        /// <summary>
        /// 這一隻角色要載哪一份模型:<paramref name="dir"/> ＝貼圖的基準資料夾,<paramref name="pmxPath"/> ＝
        /// 要解析的那個 .pmx。<paramref name="pmxPath"/> null = 畫 SDO 原本的身體。
        ///
        /// 🔴 <b>本機這一條一定要走 <see cref="Entry.PmxPath"/></b>,不能從資料夾反推:一個資料夾可以裝
        /// 好幾個模型(見 <see cref="MmdModelCatalog"/>),反推只會永遠拿到同一個,設定裡選另一個等於沒選。
        /// 遠端那一條相反 —— 我們手上只有一個下載回來的 packId 資料夾,那裡就是一包一個模型。
        ///
        /// 🔴 遠端角色**只可能**用他自己宣告的 packId。本機還沒有那份模型時回 null —— 那不是失敗,
        /// 是「還沒到」,他就先維持自己的 SDO 穿搭(絕不拿本機選的模型頂上去:那會讓同房沒穿 MMD 的人
        /// 全變成我的模型)。
        /// </summary>
        private static void ResolveModel(Reg r, out string dir, out string pmxPath, out string root)
        {
            dir = null; pmxPath = null; root = null;
            switch (SourceOf(r))
            {
                case MmdSource.RemoteModel:
                    string d = MmdModelStore.DirForPack(r.Pack, _models);
                    if (string.IsNullOrEmpty(d)) return;
                    string p;
                    try { p = MmdModelCatalog.PickPmx(Directory.GetFiles(d)); }
                    catch { p = null; }
                    if (p == null) return;
                    dir = d; pmxPath = p; root = d;   // 下載回來的一包就是一個模型,整包都可以拿來找貼圖
                    return;

                case MmdSource.LocalModel:
                    var e = Sel;
                    if (e == null || string.IsNullOrEmpty(e.PmxPath)) return;
                    dir = e.Dir; pmxPath = e.PmxPath; root = e.Root;
                    return;
            }
        }

        // Parse (once) the SELECTED model. Cached per .pmx path, so switching between the installed models parses each
        // one the first time it is shown and is instant afterwards.
        private static PmxLoader SharedPmx()
        {
            var e = Sel;
            if (e == null) { _status = "NO MODEL"; Debug.LogWarning("[mmd] " + _lastError); return null; }
            return ParseEntry(e);
        }

        /// <summary>解析這一個 .pmx(依路徑快取)。同一份模型第二次要用是免費的,而且
        /// <see cref="MmdAvatar"/> 的共用 mesh/材質快取是以 PmxLoader 實例為 key,所以也跟著暖著。</summary>
        private static PmxLoader ParsePmxFile(string pmxPath)
        {
            if (string.IsNullOrEmpty(pmxPath)) { _status = "NO MODEL"; return null; }
            var e = new MmdModelCatalog.Entry
            {
                Dir = MmdModelCatalog.DirOf(pmxPath),
                PmxPath = pmxPath,
                Name = Path.GetFileNameWithoutExtension(pmxPath),
            };
            return ParseEntry(e);
        }

        private static PmxLoader ParseEntry(MmdModelCatalog.Entry e)
        {
            if (_parsed.TryGetValue(e.PmxPath, out var hit)) { _status = "parsed"; return hit; }
            if (_parseFailed.Contains(e.PmxPath)) return null;

            var t0 = Time.realtimeSinceStartup;
            PmxLoader pmx = null;
            try { pmx = PmxLoader.Load(File.ReadAllBytes(e.PmxPath)); }
            catch (System.Exception ex) { _lastError = "read/parse fail: " + ex.Message; Debug.LogWarning("[mmd] " + _lastError); }
            if (pmx == null)
            {
                _parseFailed.Add(e.PmxPath);
                _status = "parse fail";
                if (string.IsNullOrEmpty(_lastError)) { _lastError = "PmxLoader.Load returned null (bad magic/format)"; Debug.LogWarning("[mmd] " + _lastError); }
                return null;
            }
            _parsed[e.PmxPath] = pmx;
            _status = "parsed";
            _lastError = "";
            Log($"[mmd] parsed {Path.GetFileName(e.PmxPath)} in {(Time.realtimeSinceStartup - t0) * 1000f:F0} ms " +
                $"({pmx.VertexCount} verts, {pmx.Materials.Count} mats, {pmx.Bones.Count} bones, " +
                $"{pmx.RigidBodies.Count} 剛體 → {(pmx.RigidBodies.Count > 0 ? "用模型自帶物理" : "退回內建碰撞體")})");
            return pmx;
        }

        /// <summary>Where models are installed, in priority order. They live BESIDE the SDO game data, so the same drop
        /// resolves in the editor AND in a built player: <c>&lt;DATA&gt;/MODEL</c> (packaged — package_build.ps1 fills it
        /// from the dev tree), <c>&lt;repo&gt;/assets/MODEL</c> (the dev drop-box), StreamingAssets/MODEL, plus the
        /// original single-model folder (<c>assets/IkaHatunemiku2025</c>) so an existing checkout keeps working unmoved.
        /// One folder holding a .pmx = one model (see <see cref="MmdModelCatalog"/>).
        ///
        /// <b>The dev drop-box is derived from the PROJECT, not from the data root.</b> Deriving it from
        /// <see cref="SdoExtracted.Root"/> only works when the data root is the in-repo one — with a <c>data_root.txt</c>
        /// override (which points at an out-of-tree clean\DATA) it walks up into a folder that has no <c>assets</c> at
        /// all, and the editor then reports「沒有模型」 while the model sits right there in the repo.</summary>
        public static IEnumerable<string> ModelRoots()
        {
            // 正式位置:<DATA>/ADDON/MODEL —— ADDON 就是「玩家自己丟東西進來」的那棵樹
            // (SONG / NOTESKIN / THEME / MODEL),EnsureAddonDirs 會把它建好，config.ini 的
            // AddonFolder= 還能把整棵 ADDON 指到別的碟。**而且 ADDON 是 reserved 目錄、永遠不進 pak**，
            // 所以模型放這裡自動就不會被打包 —— 不必在 build_pak.py 另外開一條 loose 規則。
            string addonModel = null; try { addonModel = SdoExtracted.AddonModelDir; } catch { }
            if (!string.IsNullOrEmpty(addonModel)) yield return addonModel;

            string root = null; try { root = SdoExtracted.Root; } catch { }
            if (!string.IsNullOrEmpty(root))
            {
                // 舊位置 <DATA>/MODEL —— 早期打包腳本放這裡。仍然掃，免得既有安裝的模型突然消失。
                yield return Path.Combine(root, "MODEL");
                string beside = null;
                try { beside = Directory.GetParent(root)?.Parent?.FullName; } catch { }     // <repo>/assets, when DATA is in-repo
                if (!string.IsNullOrEmpty(beside))
                {
                    yield return Path.Combine(beside, "MODEL");
                    yield return Path.Combine(beside, "IkaHatunemiku2025");                 // legacy single-model layout
                }
            }
            string assets = RepoAssetsDir();
            if (!string.IsNullOrEmpty(assets))
            {
                yield return Path.Combine(assets, "MODEL");                                // dev: <repo>/assets/MODEL
                yield return Path.Combine(assets, "IkaHatunemiku2025");                    // legacy single-model layout
            }
            string sa = null; try { sa = Application.streamingAssetsPath; } catch { }
            if (!string.IsNullOrEmpty(sa)) yield return Path.Combine(sa, "MODEL");
        }

        /// <summary>The repo's <c>assets/</c> folder as seen from the Unity PROJECT (editor only; in a player
        /// <c>Application.dataPath</c> is <c>&lt;exe&gt;_Data</c> and there is no repo, so this returns null and the
        /// <c>&lt;DATA&gt;/MODEL</c> root above is the one that resolves). <c>&lt;repo&gt;/65/My project/Assets</c> → up 3.</summary>
        public static string RepoAssetsDir()
        {
            if (!Application.isEditor) return null;
            try
            {
                var repo = Directory.GetParent(Application.dataPath)?.Parent?.Parent;      // Assets → project → 65 → repo
                if (repo == null) return null;
                string assets = Path.Combine(repo.FullName, "assets");
                return Directory.Exists(assets) ? assets : null;
            }
            catch { return null; }
        }
    }
}
