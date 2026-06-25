#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Sdo.EditorTools
{
    /// <summary>
    /// Editor convenience: choose what the NEXT Play does. The editor is launched by Unity Hub and does NOT inherit a
    /// terminal's `$env:SDO_SCENE`, so these menu items write EditorPrefs that ScreenGameplay.DevVar / FrontendApp read at
    /// boot (env var still wins in a player build). Pick a mode under Tools ▸ SDO, then press Play.
    /// </summary>
    public static class SdoDevBootMenu
    {
        const string SceneKey = "SDO_SCENE";
        const string OnlyKey = "SDO_SCENE_ONLY";

        const string MiLobby = "Tools/SDO/Boot Into Lobby (normal)";
        const string MiScn0008 = "Tools/SDO/Scene-Only: SCN0008 (magic circle)";
        const string MiChoose = "Tools/SDO/Scene-Only: choose scene…";

        [MenuItem(MiLobby, priority = 0)]
        static void Lobby() { EditorPrefs.DeleteKey(SceneKey); EditorPrefs.DeleteKey(OnlyKey); Report(); }
        [MenuItem(MiLobby, true)]
        static bool LobbyValidate() { Menu.SetChecked(MiLobby, string.IsNullOrEmpty(EditorPrefs.GetString(SceneKey, ""))); return true; }

        [MenuItem(MiScn0008, priority = 20)]
        static void Scn0008() { EditorPrefs.SetString(SceneKey, "SCN0008"); EditorPrefs.SetString(OnlyKey, "1"); Report(); }
        [MenuItem(MiScn0008, true)]
        static bool Scn0008Validate() { Menu.SetChecked(MiScn0008, EditorPrefs.GetString(SceneKey, "") == "SCN0008" && EditorPrefs.GetString(OnlyKey, "") == "1"); return true; }

        // Type any SCNxxxx (still scene-only). Handy for the other EFT stages (snow/aurora/bubbles…).
        [MenuItem(MiChoose, priority = 21)]
        static void Choose()
        {
            string cur = EditorPrefs.GetString(SceneKey, "SCN0008");
            string val = EditorInputDialog.Show("Scene-Only boot", "Scene folder (e.g. SCN0008):", string.IsNullOrEmpty(cur) ? "SCN0008" : cur);
            if (!string.IsNullOrEmpty(val)) { EditorPrefs.SetString(SceneKey, val.Trim().ToUpperInvariant()); EditorPrefs.SetString(OnlyKey, "1"); Report(); }
        }

        static void Report()
        {
            string s = EditorPrefs.GetString(SceneKey, "");
            string o = EditorPrefs.GetString(OnlyKey, "");
            Debug.Log(string.IsNullOrEmpty(s)
                ? "[SDO] next Play → LOBBY (normal)"
                : $"[SDO] next Play → {s} ({(o == "1" ? "scene-only: no song/notes/HUD" : "full gameplay")}). Press Play.");
        }
    }

    // Minimal one-field modal so 'choose scene…' needs no extra asset. OK button returns the text; Cancel returns null.
    public sealed class EditorInputDialog : EditorWindow
    {
        string _value; string _label; bool _ok; static string _result;

        public static string Show(string title, string label, string initial)
        {
            var w = CreateInstance<EditorInputDialog>();
            w.titleContent = new GUIContent(title); w._label = label; w._value = initial; _result = null;
            w.position = new Rect(Screen.currentResolution.width / 2f - 170, Screen.currentResolution.height / 2f - 50, 340, 100);
            w.ShowModal();
            return _result;
        }

        void OnGUI()
        {
            GUILayout.Space(8);
            GUILayout.Label(_label, EditorStyles.boldLabel);
            GUI.SetNextControlName("f"); _value = EditorGUILayout.TextField(_value);
            GUI.FocusControl("f");
            GUILayout.Space(8);
            using (new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Cancel")) { _result = null; Close(); }
                if (GUILayout.Button("OK") || (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return))
                { _result = _value; Close(); }
            }
        }
    }
}
#endif
