using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Debug switch for the "display an MMD model instead of the SDO avatar" experiment. Press <b>F7</b> (or click the
    /// on-screen button) to swap EVERY registered avatar between its native SDO body and the MMD model — the gameplay
    /// dancer, the room walker, and the three portrait/preview surfaces that render their own private avatar into a
    /// RenderTexture (the 男/女 select preview, the room 頭貼, and the 結算 left headshot). The SDO <see cref="SdoAvatar"/>
    /// stays alive as the hidden motion driver either way, so the MMD model plays the exact same MOT/DPS.
    ///
    /// WHICH model is not hardcoded: every folder holding a .pmx under <c>DATA/MODEL/</c> (dev: <c>assets/MODEL/</c>) is
    /// an entry in <see cref="MmdModelCatalog"/>, listed in the panel and switchable live (◀ ▶ buttons, or F9 for the
    /// next one, or <c>-mmdmodel &lt;name&gt;</c> on the command line). Each model is parsed once and cached, so flipping
    /// back and forth between two models only pays the parse cost once each.
    ///
    /// Self-bootstraps (<see cref="Boot"/>) and announces itself at scene load; the two build sites just call
    /// <see cref="RegisterSwappable"/>, which also eagerly parses the model so you get "[mmd] parsed …" confirmation
    /// WITHOUT needing to toggle. Milestone lines are written through <see cref="SdoLog.Note"/> so they land in the
    /// project's log.txt (which drops plain Debug.Log/info) AND mirrored to the editor console. A small overlay shows
    /// the current state, the parse result, and any error — an on-screen liveness check independent of any log filter.
    /// </summary>
    public sealed class MmdDebug : MonoBehaviour
    {
        public KeyCode ToggleKey = KeyCode.F7;    // swap SDO⇄MMD avatar (was F8; F8 now free for gameplay auto-play)
        public KeyCode ModelKey  = KeyCode.F9;    // next installed model (DATA/MODEL/*)
        public KeyCode PanelKey  = KeyCode.F10;   // show/hide this whole debug panel

        private sealed class Reg { public SdoAvatar Avatar; public MmdAvatar Mmd; public bool Failed; public bool Cloth = true; }
        private readonly List<Reg> _regs = new List<Reg>();
        private bool _mmdOn;
        private bool _panelOn = true;   // the on-screen debug panel starts visible; PanelKey (F10) or its 隱藏 button hides it

        private static MmdDebug _inst;
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

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            var inst = Ensure();

            // -mmd starts the whole session in MMD mode (every avatar, from the 男/女 select screen on) — otherwise the
            // run starts on the SDO bodies and F7 swaps. "-mmdmodel <name>" (or "-mmdmodel=<name>") picks which of the
            // installed models to start on, by name or any substring of it.
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

            Rescan(want);
            if (cli) inst._mmdOn = true;   // (no Apply: nothing is registered yet — each dancer swaps as it's built)
            Log("[mmd] armed — F7 swaps SDO⇄MMD, F9 next model, F10 shows/hides the panel. " +
                (cli ? "-mmd → starting in MMD mode. " : "") + ModelSummary());
        }

        /// <summary>Re-read the installed models from disk (DATA/MODEL/*, dev assets/MODEL/*, …) and select
        /// <paramref name="want"/> (name or substring) — or keep the current selection if it survived the rescan.
        /// Called at boot and from the panel's 重新掃描 button, so you can drop a model in and pick it up without a restart.</summary>
        public static void Rescan(string want = null)
        {
            string keep = want ?? Sel?.Name;
            _models = MmdModelCatalog.Discover(ModelRoots());
            _sel = MmdModelCatalog.IndexOf(_models, keep);
            _status = _models.Count == 0 ? "NO MODEL" : "found";
            if (_models.Count == 0)
                _lastError = "沒有模型 — 放一個 MMD 資料夾(含 .pmx)到 " + string.Join(" 或 ", new List<string>(ModelRoots()).ToArray());
        }

        private static string ModelSummary()
            => _models.Count == 0
                ? "NO MODEL — 放 .pmx 資料夾到 DATA/MODEL/ (開發樹: assets/MODEL/)"
                : $"models={_models.Count}, using '{Sel.Name}' ({Path.GetFileName(Sel.PmxPath)})";

        private static MmdDebug Ensure()
        {
            if (_inst != null) return _inst;
            var go = new GameObject("MmdDebug");
            DontDestroyOnLoad(go);
            _inst = go.AddComponent<MmdDebug>();
            return _inst;
        }

        /// <summary>Register an in-scene dancer as swappable. Called right after each SDO dancer is built. Eagerly parses
        /// the model so its "[mmd] parsed …" (or "not found") confirmation appears on room entry, before any toggle. If
        /// MMD mode is already on, the new dancer is swapped immediately.
        /// <paramref name="cloth"/> false → build this one WITHOUT the hair/skirt sim (the head portraits: the sway is
        /// invisible at that size and the cloth solver is the most expensive part of a rig).</summary>
        public static void RegisterSwappable(SdoAvatar avatar, bool cloth = true)
        {
            if (avatar == null) return;
            var inst = Ensure();
            inst._regs.RemoveAll(r => r.Avatar == null);   // drop destroyed dancers (scene changes / rebuilds)
            if (inst._regs.Exists(r => r.Avatar == avatar)) return;
            inst._regs.Add(new Reg { Avatar = avatar, Cloth = cloth });
            Log($"[mmd] registered dancer '{avatar.name}' (now {inst._regs.Count} swappable) — parsing model…");
            SharedPmx();   // eager parse → logs "[mmd] parsed …" or the not-found/parse-fail reason right now
            if (inst._mmdOn) inst.Apply(inst._regs[inst._regs.Count - 1], true);
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

        private void Update()
        {
            if (Input.GetKeyDown(ToggleKey)) Toggle();
            if (Input.GetKeyDown(ModelKey)) NextModel();
            if (Input.GetKeyDown(PanelKey)) _panelOn = !_panelOn;

            // Build the MMD body for any dancer that could NOT be built when it was last applied because its GameObject
            // was inactive — the gender-select screen keeps BOTH previews alive and only activates the selected one, and
            // Magica Cloth / the skinned rig need a live GameObject. Retried once the dancer is shown.
            if (!_mmdOn) return;
            for (int i = 0; i < _regs.Count; i++)
            {
                var r = _regs[i];
                if (r.Mmd != null || r.Failed || r.Avatar == null || !r.Avatar.gameObject.activeInHierarchy) continue;
                Apply(r, true);
            }
        }

        private void Toggle() => SetEnabled(!_mmdOn);

        /// <summary>Show the MMD body (true) or the native SDO body (false) on every registered avatar. What F7 and the
        /// on-screen button call; also the entry point for the <c>-mmd</c> launch flag and the tests.</summary>
        public static void SetEnabled(bool on)
        {
            var inst = Ensure();
            inst._mmdOn = on;
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

        /// <summary>The installed models, in panel order.</summary>
        public static IReadOnlyList<MmdModelCatalog.Entry> Models => _models;

        /// <summary>Switch to another installed model: every MMD body already built is thrown away and rebuilt from the
        /// new .pmx (the SDO driver underneath is untouched, so the dance keeps playing). No-op if the index is the
        /// current one or out of range.</summary>
        public static void SelectModel(int index)
        {
            if (index < 0 || index >= _models.Count || index == _sel) return;
            _sel = index;
            var inst = Ensure();
            inst._regs.RemoveAll(r => r.Avatar == null);
            foreach (var r in inst._regs)
            {
                if (r.Mmd != null) Destroy(r.Mmd.gameObject);
                r.Mmd = null;
                r.Failed = false;   // a model that failed to build is no reason to distrust the next one
            }
            Log($"[mmd] model → '{Sel.Name}' ({Path.GetFileName(Sel.PmxPath)})");
            if (inst._mmdOn) foreach (var r in inst._regs) inst.Apply(r, true);
        }

        /// <summary>Cycle to the next installed model (F9 / the panel's ▶).</summary>
        public static void NextModel(int step = 1)
        {
            if (_models.Count < 2) return;
            SelectModel(((_sel + step) % _models.Count + _models.Count) % _models.Count);
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
                var pmx = SharedPmx();
                if (pmx == null) { r.Failed = true; _lastError = "model not parsed (" + _status + ")"; Debug.LogWarning("[mmd] no model → staying on SDO body"); return true; }
                r.Mmd = MmdAvatar.Build(r.Avatar, pmx, Sel.Dir, r.Avatar.gameObject.layer, r.Cloth);
                if (r.Mmd == null) { r.Failed = true; _lastError = "MmdAvatar.Build returned null"; Debug.LogWarning("[mmd] build failed → staying on SDO body"); return true; }
                r.Mmd.UseAim = _aim; r.Mmd.DriveRootTranslation = _rootMove; r.Mmd.SetSphere(_sphere); r.Mmd.SetFlipV(_flipV);   // honour the live debug toggles
                r.Mmd.SetToon(_toon); r.Mmd.SetOutline(_outline); r.Mmd.SetPhysics(_physics); r.Mmd.TunePhysics(_stiff, 0.6f, _gravMul); r.Mmd.SetColliderRadius(_colMul);
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

        // Parse (once) the SELECTED model. Cached per .pmx path, so F9-ing through the installed models parses each one
        // the first time it is shown and is instant afterwards.
        private static PmxLoader SharedPmx()
        {
            var e = Sel;
            if (e == null) { _status = "NO MODEL"; Debug.LogWarning("[mmd] " + _lastError); return null; }
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
        /// One folder holding a .pmx = one model (see <see cref="MmdModelCatalog"/>).</summary>
        public static IEnumerable<string> ModelRoots()
        {
            string root = null; try { root = SdoExtracted.Root; } catch { }
            string assets = null;
            if (!string.IsNullOrEmpty(root))
            {
                try { assets = Directory.GetParent(root)?.Parent?.FullName; } catch { }   // <repo>/assets
                yield return Path.Combine(root, "MODEL");                                  // built: DATA/MODEL
            }
            if (!string.IsNullOrEmpty(assets))
            {
                yield return Path.Combine(assets, "MODEL");                                // dev: <repo>/assets/MODEL
                yield return Path.Combine(assets, "IkaHatunemiku2025");                    // legacy single-model layout
            }
            string sa = null; try { sa = Application.streamingAssetsPath; } catch { }
            if (!string.IsNullOrEmpty(sa)) yield return Path.Combine(sa, "MODEL");
        }

        // live retarget/render toggles (diagnose / tune without a recompile)
        private static bool _aim = true;        // matches MmdAvatar.UseAim default
        private static bool _sphere = true;     // matches MmdAvatar.ShowSphere default
        private static bool _rootMove = true;   // matches MmdAvatar.DriveRootTranslation default
        private static bool _flipV = true;      // matches MmdAvatar.FlipV default
        private static bool _toon = true;       // cel shading
        private static bool _outline = true;    // pencil edge
        private static bool _physics = true;    // hair/skirt sway
        private static float _gravMul = 1f;     // spring-bone gravity multiplier
        private static float _stiff = 0.12f;    // spring stiffness (matches MmdSpringBones default; low = gravity hangs it)
        private static float _colMul = 1f;      // body-collider radius multiplier

        private void ApplyOpts()
        {
            foreach (var r in _regs) if (r.Mmd != null)
            {
                r.Mmd.UseAim = _aim; r.Mmd.DriveRootTranslation = _rootMove; r.Mmd.SetSphere(_sphere); r.Mmd.SetFlipV(_flipV);
                r.Mmd.SetToon(_toon); r.Mmd.SetOutline(_outline); r.Mmd.SetPhysics(_physics); r.Mmd.TunePhysics(_stiff, 0.6f, _gravMul); r.Mmd.SetColliderRadius(_colMul);
            }
        }

        // Unmissable on-screen state + click-to-toggle buttons (so a key conflict / editor focus can't hide the feature).
        private void OnGUI()
        {
            const int w = 344, h = 352;
            if (!_panelOn)
            {
                // panel hidden — leave only a small re-opener so a key conflict / editor focus can't strand the feature
                if (GUI.Button(new Rect(8, 8, 150, 22), "MMD 面板 (F10)")) _panelOn = true;
                return;
            }
            GUI.Box(new Rect(8, 8, w, h), "MMD 顯示實驗");
            if (GUI.Button(new Rect(8 + w - 60, 12, 52, 18), "隱藏")) { _panelOn = false; return; }
            GUI.Label(new Rect(16, 30, w - 16, 20), $"狀態: {(_mmdOn ? "MMD 模型" : "SDO 原角色")}   解析: {_status}");
            GUI.Label(new Rect(16, 48, w - 16, 20), $"可切換舞者: {_regs.Count}" + (string.IsNullOrEmpty(_lastError) ? "" : "   err: " + _lastError));
            if (GUI.Button(new Rect(16, 68, 200, 22), _mmdOn ? "切回 SDO 角色 (F7)" : "切成 MMD 模型 (F7)")) Toggle();

            // ---- model picker: every folder holding a .pmx under DATA/MODEL (dev: assets/MODEL) ----
            string label = Sel == null ? "(沒有模型 — 放進 DATA/MODEL/)" : $"{Sel.Name}  [{_sel + 1}/{_models.Count}]";
            if (GUI.Button(new Rect(16, 94, 24, 20), "◀")) NextModel(-1);
            GUI.Label(new Rect(44, 94, 232, 20), "模型: " + label);
            if (GUI.Button(new Rect(280, 94, 24, 20), "▶")) NextModel(1);
            if (GUI.Button(new Rect(308, 94, 28, 20), "⟳")) Rescan();   // re-read the folder without restarting

            if (GUI.Button(new Rect(16, 118, 320, 20), $"貼圖V翻轉 flipV: {(_flipV ? "ON" : "OFF")}  ←領帶錯就切這個")) { _flipV = !_flipV; ApplyOpts(); }
            if (GUI.Button(new Rect(16, 140, 320, 20), $"aim 重定向: {(_aim ? "ON" : "OFF")}  (手腳姿勢)")) { _aim = !_aim; ApplyOpts(); }
            if (GUI.Button(new Rect(16, 162, 320, 20), $"sphere 反光: {(_sphere ? "ON" : "OFF")}")) { _sphere = !_sphere; ApplyOpts(); }
            if (GUI.Button(new Rect(16, 184, 320, 20), $"toon 卡通著色: {(_toon ? "ON" : "OFF")}")) { _toon = !_toon; ApplyOpts(); }
            if (GUI.Button(new Rect(16, 206, 320, 20), $"outline 描邊: {(_outline ? "ON" : "OFF")}")) { _outline = !_outline; ApplyOpts(); }
            if (GUI.Button(new Rect(16, 228, 320, 20), $"physics 頭髮裙擺物理: {(_physics ? "ON" : "OFF")}")) { _physics = !_physics; ApplyOpts(); }
            GUI.Label(new Rect(16, 251, 130, 20), $"重力 ×{_gravMul:F2}  硬度 ×{_stiff:F2}");
            if (GUI.Button(new Rect(150, 249, 40, 20), "重-")) { _gravMul = Mathf.Max(0.05f, _gravMul * 0.7f); ApplyOpts(); }
            if (GUI.Button(new Rect(192, 249, 40, 20), "重+")) { _gravMul = Mathf.Min(8f, _gravMul * 1.4f); ApplyOpts(); }
            if (GUI.Button(new Rect(250, 249, 40, 20), "硬-")) { _stiff = Mathf.Max(0.03f, _stiff * 0.75f); ApplyOpts(); }
            if (GUI.Button(new Rect(292, 249, 40, 20), "硬+")) { _stiff = Mathf.Min(0.9f, _stiff * 1.3f); ApplyOpts(); }
            GUI.Label(new Rect(16, 275, 140, 20), $"身體碰撞半徑 ×{_colMul:F2}");
            if (GUI.Button(new Rect(150, 273, 40, 20), "碰-")) { _colMul = Mathf.Max(0.2f, _colMul * 0.85f); ApplyOpts(); }
            if (GUI.Button(new Rect(192, 273, 40, 20), "碰+")) { _colMul = Mathf.Min(4f, _colMul * 1.18f); ApplyOpts(); }
            if (GUI.Button(new Rect(16, 299, 320, 20), $"根位移 rootMove: {(_rootMove ? "ON" : "OFF")}")) { _rootMove = !_rootMove; ApplyOpts(); }
            GUI.Label(new Rect(16, 322, w - 32, 20), "F9 換下一個模型 · 模型放 DATA/MODEL/<名稱>/*.pmx");
        }
    }
}
