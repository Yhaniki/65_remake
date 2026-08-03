using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// DEBUG / study tool for the floor formations ("隊形"). Toggled with F2 inside ScreenGameplay, it stands
    /// lightweight DUMMY figures ("假人") on the stage floor at the exact slot positions the offline client uses,
    /// so you can eyeball how each formation is laid out and how the slots relate to standing position / rank / team.
    ///
    /// A single ← / → list scrolls through every reproduced formation:
    ///   個人1 · 個人2 · 個人3   (individual — <see cref="FormationCatalog"/>, ↑/↓ picks 1..6 dancers)
    ///   2v2 · 3v3 · 2v2v2       (team/versus — <see cref="TeamFormationCatalog"/>, dancer count is fixed)
    ///
    /// Individual: slot 0 is GOLD — the leader slot (room host at setup, the current #1 slides into it every frame
    /// during a real dance; the gameplay camera anchors on it). The rest are blue, numbered by fill order.
    /// Team: each team gets its own colour (red / blue / green); every team's front member (z=0, marked ★) is that
    /// team's leader anchor. Purely a visualiser — no scoring, notes, or networking.
    /// </summary>
    public sealed class FormationPreview : MonoBehaviour
    {
        public Camera Cam;                 // perspective stage camera (projects the slot tags to screen)
        public Vector3 Anchor;             // floor dance-spot the formation is laid out around (solo = origin)
        public int Layer = 4;              // SceneLayer — the perspective stage layer ScreenGameplay renders

        private const int IndivCount = FormationCatalog.TypeCount;                 // 0..2 individual types
        private int SelMax => IndivCount + TeamFormationCatalog.All.Length;        // + the team layouts

        private int _sel;                  // 0..IndivCount-1 = individual type; then one per team layout (← →)
        private int _count = 3;            // 1..6 individual dancer count (↑ ↓); ignored for team layouts
        private bool _active;
        private readonly List<GameObject> _dummies = new List<GameObject>();
        private readonly List<Marker> _markers = new List<Marker>();               // world pos + label for OnGUI tags
        private readonly List<string> _rows = new List<string>();                  // HUD per-dancer info lines
        private string _title = "";
        private GUIStyle _panel, _line, _tag;

        private struct Marker { public Vector3 World; public string Text; public Color Col; }

        private bool IsTeam => _sel >= IndivCount;
        private TeamFormationCatalog.Layout TeamLayout => TeamFormationCatalog.All[_sel - IndivCount];

        // team colours (leader at full brightness, others dimmed); individual slot 0 = gold.
        private static readonly Color[] TeamCol = { new Color(1f, 0.35f, 0.35f), new Color(0.4f, 0.55f, 1f), new Color(0.4f, 1f, 0.5f) };
        private static readonly Color Gold = new Color(1f, 0.84f, 0.2f);

        public bool Active => _active;

        public void Toggle() => SetActive(!_active);

        public void SetActive(bool on)
        {
            _active = on;
            if (on) Rebuild();
            else Clear();
        }

        private void Update()
        {
            if (!_active) return;
            int sel = _sel, c = _count;
            if (Input.GetKeyDown(KeyCode.RightArrow)) sel = (sel + 1) % SelMax;
            if (Input.GetKeyDown(KeyCode.LeftArrow)) sel = (sel - 1 + SelMax) % SelMax;
            if (Input.GetKeyDown(KeyCode.UpArrow)) c = Mathf.Min(FormationCatalog.MaxDancers, c + 1);
            if (Input.GetKeyDown(KeyCode.DownArrow)) c = Mathf.Max(1, c - 1);
            if (sel != _sel || c != _count) { _sel = sel; _count = c; Rebuild(); }
        }

        private void Clear()
        {
            foreach (var d in _dummies) if (d != null) Destroy(d);
            _dummies.Clear();
            _markers.Clear();
            _rows.Clear();
        }

        private void Rebuild()
        {
            Clear();
            if (IsTeam) RebuildTeam();
            else RebuildIndividual();
        }

        private void RebuildIndividual()
        {
            var slots = FormationCatalog.GetSlots(_sel, _count);
            _title = $"個人 {FormationCatalog.TypeName(_sel)}  ×{slots.Length}";
            for (int i = 0; i < slots.Length; i++)
            {
                Color col = i == 0 ? Gold : Color.Lerp(new Color(0.35f, 0.9f, 1f), new Color(0.2f, 0.35f, 1f),
                                                       slots.Length > 1 ? (i - 1) / (float)(slots.Length - 1) : 0f);
                Spawn(Anchor + slots[i], col, i == 0 ? "0 ★" : i.ToString());
                string role = i == 0 ? "領隊/名次1" : "第" + (i + 1) + "位";
                _rows.Add($"slot {i} [{role}]  ({slots[i].x:0},{slots[i].z:0})");
            }
        }

        private void RebuildTeam()
        {
            var teams = TeamFormationCatalog.GetTeams(TeamLayout);
            _title = $"組隊 {TeamFormationCatalog.Name(TeamLayout)}  ({teams.Length} 隊 / {TeamFormationCatalog.TotalDancers(TeamLayout)} 人)";
            for (int t = 0; t < teams.Length; t++)
            {
                char letter = (char)('A' + t);
                Color baseCol = TeamCol[t % TeamCol.Length];
                for (int m = 0; m < teams[t].Length; m++)
                {
                    bool leader = m == 0;
                    Color col = leader ? baseCol : baseCol * 0.6f;
                    Spawn(Anchor + teams[t][m], col, leader ? letter + "★" : letter.ToString() + (m + 1));
                    string role = leader ? "隊長(前排)" : "隊員";
                    _rows.Add($"隊{letter} #{m + 1} [{role}]  ({teams[t][m].x:0},{teams[t][m].z:0})");
                }
            }
        }

        private void Spawn(Vector3 pos, Color col, string tag)
        {
            _dummies.Add(BuildDummy(pos, col));
            _markers.Add(new Marker { World = pos + new Vector3(0f, 60f, 0f), Text = tag, Col = col });
        }

        // A cheap humanoid stand-in: capsule body + sphere head + a small nub marking the facing (toward the
        // camera, −Z). Feet rest on the floor (anchor.y); proportions match the stage's ~y38 chest / ~y54 head.
        private GameObject BuildDummy(Vector3 pos, Color col)
        {
            var root = new GameObject("FormationDummy");
            root.transform.position = pos;
            root.transform.rotation = Quaternion.identity;   // dancers face −Z (the audience/camera side)

            var mat = new Material(Shader.Find("Unlit/Color")) { color = col };
            AddPart(root.transform, PrimitiveType.Capsule, new Vector3(0f, 20f, 0f), new Vector3(14f, 20f, 14f), mat);
            AddPart(root.transform, PrimitiveType.Sphere, new Vector3(0f, 46f, 0f), new Vector3(16f, 16f, 16f), mat);
            var nubMat = new Material(Shader.Find("Unlit/Color")) { color = col * 0.5f };
            AddPart(root.transform, PrimitiveType.Cube, new Vector3(0f, 30f, -9f), new Vector3(6f, 6f, 6f), nubMat);

            SetLayer(root, Layer);
            return root;
        }

        private static void AddPart(Transform parent, PrimitiveType prim, Vector3 localPos, Vector3 localScale, Material mat)
        {
            var g = GameObject.CreatePrimitive(prim);
            var col = g.GetComponent<Collider>(); if (col != null) Destroy(col);   // visual only
            g.transform.SetParent(parent, false);
            g.transform.localPosition = localPos;
            g.transform.localScale = localScale;
            g.GetComponent<Renderer>().sharedMaterial = mat;
        }

        private static void SetLayer(GameObject g, int layer)
        {
            g.layer = layer;
            foreach (Transform c in g.transform) SetLayer(c.gameObject, layer);
        }

        private void OnGUI()
        {
            if (!_active) return;
            EnsureStyles();

            // per-dummy tag, projected through the stage camera
            if (Cam != null)
            {
                foreach (var mk in _markers)
                {
                    Vector3 sp = Cam.WorldToScreenPoint(mk.World);
                    if (sp.z <= 0f) continue;
                    _tag.normal.textColor = mk.Col;
                    GUI.Label(new Rect(sp.x - 24f, Screen.height - sp.y - 12f, 48f, 22f), mk.Text, _tag);
                }
            }

            // control / info panel
            GUILayout.BeginArea(new Rect(10f, 10f, 340f, 46f + 20f * (_rows.Count + 5)), GUI.skin.box);
            GUILayout.Label("<b>隊形預覽 Formation Preview</b>", _panel);
            GUILayout.Label($"<b>{_title}</b>", _line);
            GUILayout.Label("←→ 切換隊形    ↑↓ 人數(個人)    F2 關閉", _line);
            if (IsTeam)
                GUILayout.Label("每隊<b>前排(★)</b>=隊長；隊伍以顏色區分", _line);
            else
                GUILayout.Label("<color=#FFD633>slot 0 (★)</color>=領隊/名次1/鏡頭錨點（比賽中第一名即時滑入）", _line);
            GUILayout.Space(4);
            foreach (var r in _rows) GUILayout.Label(r, _line);
            GUILayout.EndArea();
        }

        private void EnsureStyles()
        {
            if (_panel != null) return;
            _panel = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 15, fontStyle = FontStyle.Bold };
            _line = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 13 };
            _tag = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        }

        private void OnDestroy() => Clear();
    }
}
