using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Debug switch for the "display an MMD model instead of the SDO avatar" experiment. Press <b>F7</b> (or click the
    /// on-screen button) to swap every registered in-scene dancer (the lobby walker and the gameplay dancer) between
    /// its native SDO body and the MMD model — the SDO <see cref="SdoAvatar"/> stays alive as the hidden motion driver
    /// either way, so the MMD model dances the exact same MOT/DPS. The model is the Miku .pmx under
    /// <c>assets/IkaHatunemiku2025</c>, parsed once (<see cref="PmxLoader"/>) and reused.
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
        public KeyCode PanelKey  = KeyCode.F10;   // show/hide this whole debug panel

        private sealed class Reg { public SdoAvatar Avatar; public MmdAvatar Mmd; }
        private readonly List<Reg> _regs = new List<Reg>();
        private bool _mmdOn;
        private bool _panelOn = true;   // the on-screen debug panel starts visible; PanelKey (F10) or its 隱藏 button hides it

        private static MmdDebug _inst;
        private static PmxLoader _pmx;      // parsed once, shared
        private static bool _pmxTried;
        private static string _mikuDir;
        private static string _mikuPath;   // resolved .pmx (or null)
        private static string _status = "boot";
        private static string _lastError = "";

        // Write to BOTH the editor console (Debug.Log) and log.txt (SdoLog.Note) — the project's SdoLog drops
        // info-level Debug.Log, so a plain Debug.Log milestone would never appear in the file the user inspects.
        private static void Log(string m) { Debug.Log(m); SdoLog.Note("mmd", m); }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            Ensure();
            _mikuPath = ResolveMikuPmx(out _mikuDir);
            Log("[mmd] armed — F7 (or on-screen button) swaps SDO⇄MMD; F10 shows/hides the panel. model=" +
                (_mikuPath ?? "NOT FOUND under assets/IkaHatunemiku2025"));
        }

        private static MmdDebug Ensure()
        {
            if (_inst != null) return _inst;
            var go = new GameObject("MmdDebug");
            DontDestroyOnLoad(go);
            _inst = go.AddComponent<MmdDebug>();
            return _inst;
        }

        /// <summary>Register an in-scene dancer as swappable. Called right after each SDO dancer is built (lobby /
        /// gameplay). Eagerly parses the model so its "[mmd] parsed …" (or "not found") confirmation appears on room
        /// entry, before any toggle. If MMD mode is already on, the new dancer is swapped immediately.</summary>
        public static void RegisterSwappable(SdoAvatar avatar)
        {
            if (avatar == null) return;
            var inst = Ensure();
            inst._regs.RemoveAll(r => r.Avatar == null);   // drop destroyed dancers (scene changes / rebuilds)
            if (inst._regs.Exists(r => r.Avatar == avatar)) return;
            inst._regs.Add(new Reg { Avatar = avatar });
            Log($"[mmd] registered dancer '{avatar.name}' (now {inst._regs.Count} swappable) — parsing model…");
            SharedPmx();   // eager parse → logs "[mmd] parsed …" or the not-found/parse-fail reason right now
            if (inst._mmdOn) inst.Apply(inst._regs[inst._regs.Count - 1], true);
        }

        private void Update()
        {
            if (Input.GetKeyDown(ToggleKey)) Toggle();
            if (Input.GetKeyDown(PanelKey)) _panelOn = !_panelOn;
        }

        private void Toggle()
        {
            _mmdOn = !_mmdOn;
            _regs.RemoveAll(r => r.Avatar == null);
            int n = 0;
            foreach (var r in _regs) if (Apply(r, _mmdOn)) n++;
            _status = _mmdOn ? "MMD" : "SDO";
            Log($"[mmd] toggle → {_status} display on {n} dancer(s)" +
                (n == 0 ? " (NO swappable dancer registered — enter a room or a song first)" : ""));
        }

        // Swap one dancer. Building the MMD model is lazy (first time it's shown). Returns true if the dancer is live.
        private bool Apply(Reg r, bool mmdOn)
        {
            if (r.Avatar == null) return false;
            if (mmdOn && r.Mmd == null)
            {
                var pmx = SharedPmx();
                if (pmx == null) { _lastError = "model not parsed (" + _status + ")"; Debug.LogWarning("[mmd] no model → staying on SDO body"); return true; }
                r.Mmd = MmdAvatar.Build(r.Avatar, pmx, _mikuDir, r.Avatar.gameObject.layer);
                if (r.Mmd == null) { _lastError = "MmdAvatar.Build returned null"; Debug.LogWarning("[mmd] build failed → staying on SDO body"); return true; }
                r.Mmd.UseAim = _aim; r.Mmd.DriveRootTranslation = _rootMove; r.Mmd.SetSphere(_sphere); r.Mmd.SetFlipV(_flipV);   // honour the live debug toggles
                r.Mmd.SetToon(_toon); r.Mmd.SetOutline(_outline); r.Mmd.SetPhysics(_physics); r.Mmd.TunePhysics(_stiff, 0.6f, _gravMul); r.Mmd.SetColliderRadius(_colMul);
            }
            // SDO body parts are MeshRenderers; the MMD body is a SkinnedMeshRenderer — so toggling MeshRenderers
            // never touches the MMD mesh (and vice-versa). The SdoAvatar component keeps running as the motion driver.
            int hidden = 0;
            foreach (var mr in r.Avatar.GetComponentsInChildren<MeshRenderer>(true)) { mr.enabled = !mmdOn; hidden++; }
            if (r.Mmd != null) r.Mmd.SetVisible(mmdOn);
            Log($"[mmd]   '{r.Avatar.name}': {(mmdOn ? "MMD shown" : "SDO shown")}, {hidden} SDO MeshRenderer(s) {(mmdOn ? "hidden" : "shown")}");
            return true;
        }

        private static PmxLoader SharedPmx()
        {
            if (_pmxTried) return _pmx;
            _pmxTried = true;
            if (_mikuPath == null) _mikuPath = ResolveMikuPmx(out _mikuDir);
            if (_mikuPath == null) { _status = "NOT FOUND"; _lastError = "Miku .pmx not found under assets/IkaHatunemiku2025"; Debug.LogWarning("[mmd] " + _lastError); return null; }
            var t0 = Time.realtimeSinceStartup;
            try { _pmx = PmxLoader.Load(File.ReadAllBytes(_mikuPath)); }
            catch (System.Exception e) { _lastError = "read/parse fail: " + e.Message; Debug.LogWarning("[mmd] " + _lastError); }
            if (_pmx != null)
            {
                _status = "parsed";
                Log($"[mmd] parsed {Path.GetFileName(_mikuPath)} in {(Time.realtimeSinceStartup - t0) * 1000f:F0} ms " +
                    $"({_pmx.VertexCount} verts, {_pmx.Materials.Count} mats, {_pmx.Bones.Count} bones)");
            }
            else if (string.IsNullOrEmpty(_lastError)) { _status = "parse=null"; _lastError = "PmxLoader.Load returned null (bad magic/format)"; Debug.LogWarning("[mmd] " + _lastError); }
            return _pmx;
        }

        // The model lives beside the SDO game data. Probe several layouts so it resolves in the editor AND a built
        // player: <assets>/IkaHatunemiku2025 (grandparent-of-Root; dev), <DATA>/IkaHatunemiku2025 (packaged), and
        // StreamingAssets. Prefer the JP file (Japanese bone names = the map keys). Returns null if none exist.
        private static string ResolveMikuPmx(out string dir)
        {
            dir = null;
            foreach (var d in ModelDirCandidates())
            {
                try
                {
                    if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) continue;
                    string best = null;
                    foreach (var f in Directory.GetFiles(d))
                    {
                        if (Path.GetExtension(f).ToLowerInvariant() != ".pmx") continue;
                        if (f.ToUpperInvariant().Contains("-JP")) { dir = d; return f; }
                        if (best == null) best = f;
                    }
                    if (best != null) { dir = d; return best; }
                }
                catch { }
            }
            return null;
        }

        private static IEnumerable<string> ModelDirCandidates()
        {
            string root = null; try { root = SdoExtracted.Root; } catch { }
            if (!string.IsNullOrEmpty(root))
            {
                string gp = null; try { gp = Directory.GetParent(root)?.Parent?.FullName; } catch { }
                if (!string.IsNullOrEmpty(gp)) yield return Path.Combine(gp, "IkaHatunemiku2025");   // dev: <repo>/assets/IkaHatunemiku2025
                yield return Path.Combine(root, "IkaHatunemiku2025");                                 // built: DATA/IkaHatunemiku2025
            }
            string sa = null; try { sa = Application.streamingAssetsPath; } catch { }
            if (!string.IsNullOrEmpty(sa)) yield return Path.Combine(sa, "IkaHatunemiku2025");
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
            const int w = 344, h = 326;
            if (!_panelOn)
            {
                // panel hidden — leave only a small re-opener so a key conflict / editor focus can't strand the feature
                if (GUI.Button(new Rect(8, 8, 150, 22), "MMD 面板 (F10)")) _panelOn = true;
                return;
            }
            GUI.Box(new Rect(8, 8, w, h), "MMD 顯示實驗");
            if (GUI.Button(new Rect(8 + w - 60, 12, 52, 18), "隱藏")) { _panelOn = false; return; }
            GUI.Label(new Rect(16, 30, w - 16, 20), $"狀態: {(_mmdOn ? "MMD (初音)" : "SDO 原角色")}   模型: {_status}");
            GUI.Label(new Rect(16, 48, w - 16, 20), $"可切換舞者: {_regs.Count}" + (string.IsNullOrEmpty(_lastError) ? "" : "   err: " + _lastError));
            if (GUI.Button(new Rect(16, 68, 200, 22), _mmdOn ? "切回 SDO 角色 (F7)" : "切成 MMD 初音 (F7)")) Toggle();
            if (GUI.Button(new Rect(16, 94, 320, 20), $"貼圖V翻轉 flipV: {(_flipV ? "ON" : "OFF")}  ←領帶錯就切這個")) { _flipV = !_flipV; ApplyOpts(); }
            if (GUI.Button(new Rect(16, 116, 320, 20), $"aim 重定向: {(_aim ? "ON" : "OFF")}  (手腳姿勢)")) { _aim = !_aim; ApplyOpts(); }
            if (GUI.Button(new Rect(16, 138, 320, 20), $"sphere 反光: {(_sphere ? "ON" : "OFF")}")) { _sphere = !_sphere; ApplyOpts(); }
            if (GUI.Button(new Rect(16, 160, 320, 20), $"toon 卡通著色: {(_toon ? "ON" : "OFF")}")) { _toon = !_toon; ApplyOpts(); }
            if (GUI.Button(new Rect(16, 182, 320, 20), $"outline 描邊: {(_outline ? "ON" : "OFF")}")) { _outline = !_outline; ApplyOpts(); }
            if (GUI.Button(new Rect(16, 204, 320, 20), $"physics 頭髮裙擺物理: {(_physics ? "ON" : "OFF")}")) { _physics = !_physics; ApplyOpts(); }
            GUI.Label(new Rect(16, 227, 130, 20), $"重力 ×{_gravMul:F2}  硬度 ×{_stiff:F2}");
            if (GUI.Button(new Rect(150, 225, 40, 20), "重-")) { _gravMul = Mathf.Max(0.05f, _gravMul * 0.7f); ApplyOpts(); }
            if (GUI.Button(new Rect(192, 225, 40, 20), "重+")) { _gravMul = Mathf.Min(8f, _gravMul * 1.4f); ApplyOpts(); }
            if (GUI.Button(new Rect(250, 225, 40, 20), "硬-")) { _stiff = Mathf.Max(0.03f, _stiff * 0.75f); ApplyOpts(); }
            if (GUI.Button(new Rect(292, 225, 40, 20), "硬+")) { _stiff = Mathf.Min(0.9f, _stiff * 1.3f); ApplyOpts(); }
            GUI.Label(new Rect(16, 251, 140, 20), $"身體碰撞半徑 ×{_colMul:F2}");
            if (GUI.Button(new Rect(150, 249, 40, 20), "碰-")) { _colMul = Mathf.Max(0.2f, _colMul * 0.85f); ApplyOpts(); }
            if (GUI.Button(new Rect(192, 249, 40, 20), "碰+")) { _colMul = Mathf.Min(4f, _colMul * 1.18f); ApplyOpts(); }
            if (GUI.Button(new Rect(16, 275, 320, 20), $"根位移 rootMove: {(_rootMove ? "ON" : "OFF")}")) { _rootMove = !_rootMove; ApplyOpts(); }
        }
    }
}
