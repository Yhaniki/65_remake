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
        const string RoomKey = "SDO_ROOM";
        const string ShopKey = "SDO_SHOP";
        const string EditorKey = "SDO_EDITOR";   // 譜面編輯器（ChartEditorScreen）；值可以是 "1" 或指定的 sdomNNNNk.gn

        const string MiLobby = "Tools/SDO/Boot Into Lobby (normal)";
        const string MiRoom = "Tools/SDO/Boot Into Room (waiting room)";
        const string MiShop = "Tools/SDO/Boot Into Shop (商城)";
        const string MiEditor = "Tools/SDO/Boot Into Chart Editor (譜面編輯器)";
        const string MiEditorSong = "Tools/SDO/Chart Editor: choose song (.gn)…";
        const string MiScn0008 = "Tools/SDO/Scene-Only: SCN0008 (magic circle)";
        const string MiChoose = "Tools/SDO/Scene-Only: choose scene…";

        [MenuItem(MiLobby, priority = 0)]
        static void Lobby() { ClearAll(); Report(); }
        [MenuItem(MiLobby, true)]
        static bool LobbyValidate() { Menu.SetChecked(MiLobby, string.IsNullOrEmpty(EditorPrefs.GetString(SceneKey, "")) && string.IsNullOrEmpty(EditorPrefs.GetString(RoomKey, "")) && string.IsNullOrEmpty(EditorPrefs.GetString(ShopKey, "")) && string.IsNullOrEmpty(EditorPrefs.GetString(EditorKey, ""))); return true; }

        static void ClearAll()
        {
            EditorPrefs.DeleteKey(SceneKey); EditorPrefs.DeleteKey(OnlyKey);
            EditorPrefs.DeleteKey(RoomKey); EditorPrefs.DeleteKey(ShopKey); EditorPrefs.DeleteKey(EditorKey);
        }

        // 譜面編輯器：純黑背景（不載場景/舞者）的 gameplay 音符板 + 音樂波形，可在裡面直接換歌。
        // 值留 "1" = 開上次那首（ChartEditorScreen 記在 PlayerPrefs）；用下面那個選項可以指定某一首 .gn。
        [MenuItem(MiEditor, priority = 3)]
        static void ChartEditor() { ClearAll(); EditorPrefs.SetString(EditorKey, "1"); Report(); }
        [MenuItem(MiEditor, true)]
        static bool ChartEditorValidate() { Menu.SetChecked(MiEditor, !string.IsNullOrEmpty(EditorPrefs.GetString(EditorKey, ""))); return true; }

        [MenuItem(MiEditorSong, priority = 4)]
        static void ChartEditorSong()
        {
            string cur = EditorPrefs.GetString(EditorKey, "");
            if (cur == "1") cur = "";
            string val = EditorInputDialog.Show("Chart editor", "譜面檔名（例：sdom1197k.gn，留空 = 上次那首）:", cur);
            if (val == null) return;
            ClearAll();
            EditorPrefs.SetString(EditorKey, string.IsNullOrWhiteSpace(val) ? "1" : val.Trim());
            Report();
        }

        // Boot the front-end then jump straight into the waiting room (SCNCHIRSROOM 3D scene + ROOM UI). Clears the
        // scene-only keys so the front-end boots normally first (FrontendApp.EnterRoom runs after the lobby is built).
        [MenuItem(MiRoom, priority = 1)]
        static void Room() { ClearAll(); EditorPrefs.SetString(RoomKey, "1"); Report(); }
        [MenuItem(MiRoom, true)]
        static bool RoomValidate() { Menu.SetChecked(MiRoom, EditorPrefs.GetString(RoomKey, "") == "1"); return true; }

        // Boot the front-end → waiting room → open the 商城 (shop) modal straight away, for working on the shop UI.
        [MenuItem(MiShop, priority = 2)]
        static void Shop() { ClearAll(); EditorPrefs.SetString(ShopKey, "1"); Report(); }
        [MenuItem(MiShop, true)]
        static bool ShopValidate() { Menu.SetChecked(MiShop, EditorPrefs.GetString(ShopKey, "") == "1"); return true; }

        [MenuItem(MiScn0008, priority = 20)]
        static void Scn0008() { ClearAll(); EditorPrefs.SetString(SceneKey, "SCN0008"); EditorPrefs.SetString(OnlyKey, "1"); Report(); }
        [MenuItem(MiScn0008, true)]
        static bool Scn0008Validate() { Menu.SetChecked(MiScn0008, EditorPrefs.GetString(SceneKey, "") == "SCN0008" && EditorPrefs.GetString(OnlyKey, "") == "1"); return true; }

        // Type any SCNxxxx (still scene-only). Handy for the other EFT stages (snow/aurora/bubbles…).
        [MenuItem(MiChoose, priority = 21)]
        static void Choose()
        {
            string cur = EditorPrefs.GetString(SceneKey, "SCN0008");
            string val = EditorInputDialog.Show("Scene-Only boot", "Scene folder (e.g. SCN0008):", string.IsNullOrEmpty(cur) ? "SCN0008" : cur);
            if (!string.IsNullOrEmpty(val)) { ClearAll(); EditorPrefs.SetString(SceneKey, val.Trim().ToUpperInvariant()); EditorPrefs.SetString(OnlyKey, "1"); Report(); }
        }

        static void Report()
        {
            string s = EditorPrefs.GetString(SceneKey, "");
            string o = EditorPrefs.GetString(OnlyKey, "");
            string ed = EditorPrefs.GetString(EditorKey, "");
            bool room = EditorPrefs.GetString(RoomKey, "") == "1";
            bool shop = EditorPrefs.GetString(ShopKey, "") == "1";
            Debug.Log(!string.IsNullOrEmpty(s)
                ? $"[SDO] next Play → {s} ({(o == "1" ? "scene-only: no song/notes/HUD" : "full gameplay")}). Press Play."
                : !string.IsNullOrEmpty(ed)
                    ? $"[SDO] next Play → CHART EDITOR ({(ed == "1" ? "上次那首" : ed)}：純黑底音符板 + 波形，F1 換歌). Press Play."
                    : shop
                        ? "[SDO] next Play → SHOP (商城 over the waiting room). Press Play."
                        : room
                            ? "[SDO] next Play → ROOM (waiting room: SCNCHIRSROOM 3D + ROOM UI). Press Play."
                            : "[SDO] next Play → LOBBY (normal)");
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
