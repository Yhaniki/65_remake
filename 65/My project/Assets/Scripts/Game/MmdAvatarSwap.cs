using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Sdo.Osu;
using Sdo.Settings;

namespace Sdo.Game
{
    /// <summary>
    /// 「用 MMD 模型顯示角色」的執行期服務。開著的時候，每一隻登記過的角色（跳舞的、房間走路的、以及三個各自
    /// 渲一張 RenderTexture 的頭貼/預覽：男女選擇預覽、房間頭貼、結算左邊頭像）都把 SDO 的身體藏起來、改畫
    /// 一個 MMD 模型。SDO 的 <see cref="SdoAvatar"/> 仍然活著當**動作驅動器**，所以跳的還是同一套 MOT/DPS。
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
        /// 一隻登記過的角色。<see cref="Pack"/> 是**這一隻**要顯示哪個模型:
        ///   • 空 = 本機玩家自己 → 用設定裡選的那個(<c>RoomConfig.mmdModel</c>);
        ///   • 有值 = 遠端玩家 → 用那個 packId 的模型(他的外觀宣告的)。
        /// 遠端的模型本機還沒有時 <see cref="Failed"/> 不會被設起來 —— 它不是失敗,是「還沒到」:
        /// 這隻角色就停在自己的 SDO 穿搭上,等 <see cref="OnPackInstalled"/> 把它接上去。
        /// </summary>
        private sealed class Reg
        {
            public SdoAvatar Avatar;
            public MmdAvatar Mmd;
            public bool Failed;
            public bool Cloth = true;
            public string Pack = "";     // 遠端玩家身上的模型 packId;空 = 用本機選的那個
            public string BuiltFrom;     // 現在畫出來的這具身體是從哪個資料夾建的(換模型時比對用)
        }
        private readonly List<Reg> _regs = new List<Reg>();
        private bool _mmdOn;

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

            // Command line still wins for a one-off run without touching config.ini: "-mmd" forces MMD display on,
            // "-mmdmodel <name>" (or "=<name>") picks which installed model, by name or any substring of it.
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

            Rescan(want ?? RoomConfig.mmdModel);
            if (want != null && Sel != null) RoomConfig.mmdModel = Sel.Name;   // -mmdmodel 也走設定值,之後的比對才不會一直當成「變了」
            if (cli) RoomConfig.mmdEnabled = true;
            inst._mmdOn = RoomConfig.mmdEnabled;   // (no Apply: nothing is registered yet — each dancer swaps as it's built)
            inst._applied = Snapshot();
            Log($"[mmd] armed — 設定在 config.ini [Mmd] / 開場設定面板的 MMD 分頁。顯示={(inst._mmdOn ? "MMD" : "SDO")}. " +
                (cli ? "-mmd → 這次啟動強制 MMD. " : "") + ModelSummary());
            if (inst._mmdOn) inst.StartCoroutine(inst.PrewarmCo());
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
                if (!MmdAvatar.PrewarmTexture(pmx, Sel.Dir, i)) break;
                if ((Time.realtimeSinceStartup - frameStart) * 1000f < PrewarmBudgetMs) continue;
                yield return null;
                frameStart = Time.realtimeSinceStartup;
                frames++;
            }
            MmdAvatar.Prewarm(pmx, Sel.Dir);   // 材質 + mesh(貼圖此時全在快取裡)
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

        /// <summary>安裝了哪些模型（資料夾名）。設定面板那一列的選項來源。</summary>
        public static string[] ModelNames()
        {
            var names = new string[_models.Count];
            for (int i = 0; i < _models.Count; i++) names[i] = _models[i].Name;
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
        public static void Register(SdoAvatar avatar, bool cloth = true) => Register(avatar, "", cloth);

        /// <summary>
        /// 登記一隻**遠端**玩家的角色,連同他外觀宣告的模型 <paramref name="packId"/>。
        ///
        /// 本機還沒有那份模型時,這隻就停在他的 SDO 穿搭上 —— 那**不是退化的畫面**,那就是他的樣子:
        /// MMD 模型本來就是疊在 SDO 骨架上顯示的(SDO 那隻永遠是動作驅動器),所以「還沒下載完」的
        /// 正確畫面天生就是他的穿搭,沒有空白、沒有替身。模型到了之後 <see cref="OnPackInstalled"/>
        /// 直接把身體換掉,**不重建**這隻角色(位置、朝向、正在播的動作全都留著)。
        /// </summary>
        public static void RegisterRemote(SdoAvatar avatar, string packId, bool cloth = true)
            => Register(avatar, packId ?? "", cloth);

        private static void Register(SdoAvatar avatar, string packId, bool cloth)
        {
            if (avatar == null) return;
            var inst = Ensure();
            inst._regs.RemoveAll(r => r.Avatar == null);   // drop destroyed dancers (scene changes / rebuilds)
            var existing = inst._regs.Find(r => r.Avatar == avatar);
            if (existing != null)
            {
                if (string.Equals(existing.Pack, packId, StringComparison.Ordinal)) return;
                existing.Pack = packId;      // 同一隻改穿別的模型 → 重建它的身體(不動 SDO 驅動器)
                existing.Failed = false;
                inst.DropBody(existing);
            }
            else inst._regs.Add(new Reg { Avatar = avatar, Cloth = cloth, Pack = packId ?? "" });

            if (!inst._mmdOn) return;   // 沒開就別碰:不解析、不建、也不寫 log(關著的時候這整條要 0 成本)
            var reg = existing ?? inst._regs[inst._regs.Count - 1];
            Log($"[mmd] registered dancer '{avatar.name}'"
                + (string.IsNullOrEmpty(reg.Pack) ? " (本機選的模型)" : " (遠端模型 " + Short(reg.Pack) + ")")
                + $" — now {inst._regs.Count} swappable");
            inst.Apply(reg, true);
        }

        /// <summary>packId 的短寫法(log 用) —— 完整的 40 字在一行 log 裡只會擋住真正要看的東西。</summary>
        private static string Short(string packId)
            => string.IsNullOrEmpty(packId) ? "(本機)"
             : (packId.Length > SongPackId.Prefix.Length + 8 ? packId.Substring(SongPackId.Prefix.Length, 8) : packId);

        /// <summary>丟掉這一隻現在畫出來的 MMD 身體(SDO 驅動器不動)。換模型 / 重建時用。</summary>
        private void DropBody(Reg r)
        {
            if (r.Mmd != null) Destroy(r.Mmd.gameObject);
            r.Mmd = null;
            r.BuiltFrom = null;
            if (r.Avatar != null)
                foreach (var mr in r.Avatar.GetComponentsInChildren<MeshRenderer>(true)) mr.enabled = true;
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
                if (r.Mmd != null || !string.Equals(r.Pack, packId, StringComparison.Ordinal)) continue;
                r.Failed = false;
                if (_inst.Apply(r, _inst._mmdOn)) n++;
            }
            if (n > 0) Log($"[mmd] 模型 {Short(packId)} 裝好了 → {n} 隻角色當場換上(沒有重建)");
        }

        /// <summary>
        /// 現在有哪些遠端模型是「有人穿著、但本機還沒有」的 —— 傳輸編排(<c>NetModelTransfer</c>)
        /// 拿它決定要去跟 server 要什麼。MMD 顯示關掉時一律是空的(關掉就不該產生任何流量)。
        /// </summary>
        public static void CollectMissingPacks(List<string> into)
        {
            if (into == null) return;
            into.Clear();
            if (_inst == null || !_inst._mmdOn) return;
            foreach (var r in _inst._regs)
            {
                if (r.Avatar == null || r.Mmd != null || string.IsNullOrEmpty(r.Pack)) continue;
                if (MmdModelStore.DirForPack(r.Pack, _models) != null) continue;   // 其實有,只是還沒套上
                if (!into.Contains(r.Pack)) into.Add(r.Pack);
            }
        }

        /// <summary>The MMD body currently DISPLAYED for <paramref name="avatar"/>, or null when the native SDO body is
        /// the one on screen. The head-portrait cameras (room 頭貼 / 結算頭貼) ask this so they can frame the MMD head —
        /// the SDO FACE/HAIR geometry they normally measure is hidden while MMD is shown.</summary>
        public static MmdAvatar ActiveFor(SdoAvatar avatar)
        {
            if (_inst == null || !_inst._mmdOn || avatar == null) return null;
            foreach (var r in _inst._regs)
                if (r.Avatar == avatar) return (r.Mmd != null && r.Mmd.Visible) ? r.Mmd : null;
            return null;
        }

        // ---------------------------------------------------------------- config.ini → 場上
        // 設定的快照。面板改了值就直接寫進 RoomConfig 的 static 欄位(沒有事件可以訂閱),所以這裡每幀比一次:
        // 12 個欄位的比對比任何一種通知機制都便宜，而且手改 config.ini 之後重讀也同樣會生效。
        private struct Snap
        {
            public bool On, Toon, Outline, Sphere, Physics, Aim, RootMove, FlipV;
            public string Model;
            public float Grav, Stiff, Col;
        }
        private Snap _applied;

        private static Snap Snapshot() => new Snap
        {
            On = RoomConfig.mmdEnabled, Model = RoomConfig.mmdModel ?? "",
            Toon = RoomConfig.mmdToon, Outline = RoomConfig.mmdOutline, Sphere = RoomConfig.mmdSphere,
            Physics = RoomConfig.mmdPhysics, Aim = RoomConfig.mmdAim, RootMove = RoomConfig.mmdRootMotion,
            FlipV = RoomConfig.mmdFlipV,
            Grav = RoomConfig.mmdGravity, Stiff = RoomConfig.mmdStiffness, Col = RoomConfig.mmdColliderScale,
        };

        private void Update()
        {
            var now = Snapshot();
            if (now.On != _applied.On) { _applied = now; SetEnabled(now.On); }
            else if (!string.Equals(now.Model, _applied.Model, System.StringComparison.OrdinalIgnoreCase))
            {
                // 換模型:先照名字重選(掃過的清單裡找),再把每一隻已經建好的身體丟掉重建 —— SDO 驅動器不動,舞繼續跳。
                _applied = now;
                Rescan(now.Model);
                Log($"[mmd] model → {(Sel != null ? $"'{Sel.Name}' ({Path.GetFileName(Sel.PmxPath)})" : "(找不到 '" + now.Model + "')")}");
                RebuildAll(localOnly: true);
            }
            else if (!SameLooks(now, _applied)) { _applied = now; ApplyOpts(); }

            if (!_mmdOn) return;
            // Build the MMD body for any dancer that could NOT be built when it was last applied because its GameObject
            // was inactive — the gender-select screen keeps BOTH previews alive and only activates the selected one, and
            // Magica Cloth / the skinned rig need a live GameObject. Retried once the dancer is shown.
            for (int i = 0; i < _regs.Count; i++)
            {
                var r = _regs[i];
                if (r.Mmd != null || r.Failed || r.Avatar == null || !r.Avatar.gameObject.activeInHierarchy) continue;
                Apply(r, true);
            }
        }

        // 「外觀/物理旋鈕」有沒有變(不含總開關與模型 —— 那兩個要走重建那條路)。
        private static bool SameLooks(in Snap a, in Snap b)
            => a.Toon == b.Toon && a.Outline == b.Outline && a.Sphere == b.Sphere && a.Physics == b.Physics
            && a.Aim == b.Aim && a.RootMove == b.RootMove && a.FlipV == b.FlipV
            && Mathf.Approximately(a.Grav, b.Grav) && Mathf.Approximately(a.Stiff, b.Stiff)
            && Mathf.Approximately(a.Col, b.Col);

        /// <summary>Show the MMD body (true) or the native SDO body (false) on every registered avatar. Normally driven
        /// by <c>config.ini mmdEnabled</c> via <see cref="Update"/>; called directly by the tests.</summary>
        public static void SetEnabled(bool on)
        {
            var inst = Ensure();
            inst._mmdOn = on;
            RoomConfig.mmdEnabled = on;      // 單一事實來源:直接呼叫(測試)也要讓下一次比對看到一樣的值
            inst._applied.On = on;
            inst._regs.RemoveAll(r => r.Avatar == null);
            int n = 0;
            foreach (var r in inst._regs) if (inst.Apply(r, on)) n++;
            Log($"[mmd] display → {(on ? "MMD" : "SDO")} on {n} dancer(s)" +
                (n == 0 ? " (NO swappable dancer registered — enter a room or a song first)" : ""));
        }

        /// <summary>Is the MMD body the one being displayed?</summary>
        public static bool Enabled => _inst != null && _inst._mmdOn;

        /// <summary>The selected model's .pmx, or null when none is installed (the tests skip themselves without it).</summary>
        public static string ModelPath => Sel?.PmxPath;

        /// <summary>本機現在選的那個模型的資料夾(沒裝模型時 null)。</summary>
        public static string ModelDir => Sel?.Dir;

        /// <summary>本機現在選的那個模型的顯示名稱(＝資料夾名)。</summary>
        public static string ModelName => Sel?.Name ?? "";

        /// <summary>
        /// 「我身上穿的模型」的 packId —— <c>setLook</c> 要送出去的那個值。
        ///
        /// 空字串代表「別人看到的是我的 SDO 穿搭」,發生在三種情況:MMD 顯示關著、沒裝模型、
        /// 或設定裡把分享關掉了(<c>mmdShareModel=0</c>)。**分享關掉時連算都不算** ——
        /// 算 packId 要把整份模型讀過一遍,不打算分享就不該付那個成本。
        /// </summary>
        public static string LocalPackId
        {
            get
            {
                if (_inst == null || !_inst._mmdOn) return "";
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
            if (e == null || cloth == null) { _lastError = "沒有可存的布料(先在設定面板開 MMD 顯示)"; return null; }
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
            if (gone) RebuildAll();
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

        private static MmdMagicaCloth FirstCloth()
        {
            if (_inst == null) return null;
            foreach (var r in _inst._regs)
                if (r.Mmd != null && r.Mmd.Cloth != null) return r.Mmd.Cloth;
            return null;
        }

        /// <summary>Throw away every built MMD body and build it again — what to call after the model's physics.ini was
        /// changed on disk (hand-editing the file + this = see your edit without restarting).</summary>
        public static void Rebuild() => RebuildAll();

        // 丟掉建好的 MMD 身體重建(換了模型 / 改了 physics.ini 之後)。
        // <paramref name="localOnly"/> = 只重建「用本機選的模型」的那些 —— 換自己的模型不該把遠端玩家
        // 身上的模型也重建一遍(那是他們的外觀,與我選什麼無關,而且每隻要重付一次 rig 成本)。
        private static void RebuildAll(bool localOnly = false)
        {
            var inst = Ensure();
            inst._regs.RemoveAll(r => r.Avatar == null);
            foreach (var r in inst._regs)
            {
                if (localOnly && !string.IsNullOrEmpty(r.Pack)) continue;
                inst.DropBody(r);
                r.Failed = false;
            }
            if (!inst._mmdOn) return;
            foreach (var r in inst._regs)
            {
                if (localOnly && !string.IsNullOrEmpty(r.Pack)) continue;
                inst.Apply(r, true);
            }
        }

        // Swap one dancer. Building the MMD model is lazy (first time it's shown). Returns true if the dancer is live.
        private bool Apply(Reg r, bool mmdOn)
        {
            if (r.Avatar == null) return false;
            if (mmdOn && r.Mmd == null && !r.Failed)
            {
                // An inactive dancer (the gender preview parks the unselected gender) can't be built yet — the rig and
                // Magica Cloth need a live GameObject. Leave it on its SDO body; Update() swaps it the moment it's shown.
                if (!r.Avatar.gameObject.activeInHierarchy) return true;

                // 這一隻要用哪個資料夾的模型:遠端 = 他宣告的 packId,本機 = 設定裡選的那個。
                string dir = DirFor(r);
                if (dir == null)
                {
                    // 遠端模型還沒下載到 —— **這不是失敗**,是「還沒到」。不要設 Failed,
                    // 否則模型到了之後 OnPackInstalled 會被 Failed 擋住,那個人永遠停在 SDO 穿搭。
                    if (!string.IsNullOrEmpty(r.Pack)) return true;
                    r.Failed = true; _lastError = "model not parsed (" + _status + ")";
                    Debug.LogWarning("[mmd] no model → staying on SDO body");
                    return true;
                }
                var pmx = ParsePmx(dir);
                if (pmx == null) { r.Failed = true; Debug.LogWarning("[mmd] 解析不了 " + dir + " → staying on SDO body"); return true; }
                // 布料是建一隻 rig 最貴的一段 → 設定關掉布料時就整組不建(不是建了再關),換場景才會明顯變快。
                r.Mmd = MmdAvatar.Build(r.Avatar, pmx, dir, r.Avatar.gameObject.layer, r.Cloth && RoomConfig.mmdPhysics);
                if (r.Mmd == null) { r.Failed = true; _lastError = "MmdAvatar.Build returned null"; Debug.LogWarning("[mmd] build failed → staying on SDO body"); return true; }
                r.BuiltFrom = dir;
                ApplyOptsTo(r.Mmd);
            }
            // The portrait / preview cameras cull by LAYER, and a dancer's layer is assigned after its parts are built —
            // so keep the rig on whatever layer its driver ended up on (else the 頭貼 cam renders an empty RT).
            if (r.Mmd != null) r.Mmd.SetLayer(r.Avatar.gameObject.layer);
            // SDO body parts are MeshRenderers; the MMD body is a SkinnedMeshRenderer — so toggling MeshRenderers
            // never touches the MMD mesh (and vice-versa). The SdoAvatar component keeps running as the motion driver.
            int hidden = 0;
            foreach (var mr in r.Avatar.GetComponentsInChildren<MeshRenderer>(true)) { mr.enabled = !mmdOn; hidden++; }
            if (r.Mmd != null) r.Mmd.SetVisible(mmdOn);
            Log($"[mmd]   '{r.Avatar.name}': {(mmdOn ? "MMD shown" : "SDO shown")}, {hidden} SDO MeshRenderer(s) {(mmdOn ? "hidden" : "shown")}");
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

        /// <summary>這一隻角色要用哪個資料夾的模型。遠端 = 他宣告的 packId(本機沒有就回 null),
        /// 本機 = 設定裡選的那個。</summary>
        private static string DirFor(Reg r)
        {
            if (!string.IsNullOrEmpty(r.Pack)) return MmdModelStore.DirForPack(r.Pack, _models);
            var e = Sel;
            return e != null ? e.Dir : null;
        }

        // Parse (once) the SELECTED model. Cached per .pmx path, so switching between the installed models parses each
        // one the first time it is shown and is instant afterwards.
        private static PmxLoader SharedPmx()
        {
            var e = Sel;
            if (e == null) { _status = "NO MODEL"; Debug.LogWarning("[mmd] " + _lastError); return null; }
            return ParsePmx(e.Dir);
        }

        /// <summary>解析這個資料夾裡的模型(依 .pmx 路徑快取)。同一份模型第二次要用是免費的,
        /// 而且 <see cref="MmdAvatar"/> 的共用 mesh/材質快取是以 PmxLoader 實例為 key,所以也跟著暖著。</summary>
        private static PmxLoader ParsePmx(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            string pmxPath;
            try { pmxPath = MmdModelCatalog.PickPmx(Directory.GetFiles(dir)); }
            catch { pmxPath = null; }
            if (pmxPath == null) { _status = "NO MODEL"; return null; }
            var e = new MmdModelCatalog.Entry { Dir = dir, PmxPath = pmxPath, Name = MmdModelCatalog.LeafName(dir) };
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
            string root = null; try { root = SdoExtracted.Root; } catch { }
            if (!string.IsNullOrEmpty(root))
            {
                yield return Path.Combine(root, "MODEL");                                  // built / overridden: <DATA>/MODEL
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
