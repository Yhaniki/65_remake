using System.IO;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// The 3D waiting room (開房間的大廳): loads the SCNCHIRSROOM stage mesh, drops the local player's avatar onto the
    /// floor, lets the arrow keys walk it around (RoomMovement), and a camera follows it — all rendered to a
    /// RenderTexture (<see cref="SceneTexture"/>) that the RoomScreen shows as its full-screen backdrop behind the ROOM
    /// UI overlay. The scene + avatar live on <see cref="SceneLayer"/>; the camera renders only that layer (its objects
    /// are masked off the front-end UI camera by RoomScreen). Created/destroyed by RoomScreen.OnShow/OnHide.
    ///
    /// Reuses the validated render path: SceneLoader.Load for the mesh (SCNCHIRSROOM is a single-block 14-material
    /// scene, fully compatible) and the exact decompiled scene-camera projection (fovY 45, near 5, far 7500).
    /// </summary>
    public sealed class RoomScene3D : MonoBehaviour
    {
        public const int SceneLayer = 4;   // the perspective stage layer (same as gameplay; the play screen isn't alive here)
        public const string ScenePath = "SCENE/SCNCHIRSROOM";

        // ---- tunables (floor height / back distance need one visual calibration pass; see risks) ----
        public float floorY = RoomLayout.FloorY;                 // plane the local avatar stands on (EXE looker tables = 0)
        // EXE StateRoom_UpdateCameraTarget: look-target = (avatarX, avatarY+50, avatarZ); eye.x locked to avatarX,
        // eye.y at the same head height → HORIZONTAL line of sight (平視), eye offset purely in Z by the back distance.
        public float cameraLookHeight = 50f;                     // eye & look height above the floor (EXE avatar+50)
        public float cameraBackDistance = -180f;                 // eye Z offset from the anchor (signed; X/Y locked)
        public float cameraEyeMinZ = -378f;                      // keep the eye in front of the back wall (no clip)
        // CAMERA follow box = the EXE StateRoom_ClampCameraPos bounds (.data ints @0x583744..50: X[-278,100], Z[-279,100]).
        // The camera anchor (0x918) is clamped here and the camera looks at it, so the camera STOPS at this box. The
        // avatar walks a bit FURTHER (avatarWalkMargin past each side), so it keeps moving — drifting toward the frame
        // edge — after the camera has stopped (官方: 人還能繼續往下/左右走一段, 但 camera 提早停).
        public Vector2 cameraBoundsMin = new Vector2(RoomLayout.MinX, RoomLayout.MinZ);   // (-278, -279)
        public Vector2 cameraBoundsMax = new Vector2(RoomLayout.MaxX, RoomLayout.MaxZ);   // ( 100,  100)
        public float avatarWalkMargin = 60f;                     // how far the avatar may walk PAST the camera box
        public float walkSpeed = RoomMovement.WalkSpeed;         // free-walk speed mult (3.0); no run in the lobby
        public bool useMask = true;                              // sample MASK.MSK for furniture collision (else box clamp)

        private RoomMask _mask;
        private SdoAvatar _avatar;
        private Transform _avatarRoot;
        private Camera _cam;
        private RenderTexture _rt;
        private MotLoader _walkMot, _idleMot;
        private Vector3 _walkPos;     // logical floor position (X, floorY, Z)
        private float _feetY;         // model-space feet offset so the feet rest on floorY
        private float _facing;        // current Unity yaw (degrees)
        private bool _walking;
        private bool _ready;

        /// <summary>The room render — assign to the RoomScreen backdrop RawImage. Null until Build succeeds.</summary>
        public Texture SceneTexture => _rt;
        public bool Ready => _ready;
        public Camera SceneCamForTest => _cam;          // inspection/capture only
        public SdoAvatar AvatarForTest => _avatar;

        public void Build()
        {
            if (_ready) return;
            LoadScene();
            LoadMask();
            LoadAvatar();
            BuildCamera();
            _ready = true;
        }

        // Decode the room's walkable/furniture mask (SCNCHIRSROOM/MASK.MSK). Null on missing/parse-fail → box clamp.
        private void LoadMask()
        {
            if (!useMask) return;
            var path = Path.Combine(SdoExtracted.Root, ScenePath.Replace('/', Path.DirectorySeparatorChar), "MASK.MSK");
            if (!File.Exists(path)) { Debug.LogWarning("[room-mask] missing " + path); return; }
            try { _mask = RoomMask.Parse(File.ReadAllBytes(path)); }
            catch (System.Exception e) { Debug.LogWarning("[room-mask] parse fail: " + e.Message); }
            if (_mask != null) Debug.Log($"[room-mask] {RoomMask.Width}x{RoomMask.Height}, {_mask.WalkableCount()} walkable cells");
        }

        private void LoadScene()
        {
            var dir = Path.Combine(SdoExtracted.Root, ScenePath.Replace('/', Path.DirectorySeparatorChar));
            var mshPath = Path.Combine(dir, "SCENE.MSH");
            if (!File.Exists(mshPath)) { Debug.LogWarning("[room-scene] missing " + mshPath); return; }
            SceneLoader.Result res;
            try { res = SceneLoader.Load(File.ReadAllBytes(mshPath), dir); }
            catch (System.Exception e) { Debug.LogWarning("[room-scene] load fail: " + e.Message); return; }
            if (res == null || res.Mesh == null) { Debug.LogWarning("[room-scene] parse fail"); return; }

            var go = new GameObject("RoomStageScene") { layer = SceneLayer };
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().mesh = res.Mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = res.Materials;   // native SDO coords (verbatim), no lift
            Debug.Log($"[room-scene] SCNCHIRSROOM: {res.Materials.Length} subsets, bounds c={res.Mesh.bounds.center} s={res.Mesh.bounds.size}");
        }

        private void LoadAvatar()
        {
            var parent = new GameObject("RoomLocalAvatar");
            parent.transform.SetParent(transform, false);
            _avatar = SdoRoomAvatar.Build(parent, SceneLayer, portraitOpaque: false);
            _avatarRoot = parent.transform;
            _walkMot = SdoRoomAvatar.LoadMot(SdoRoomAvatar.WalkMot);
            _idleMot = SdoRoomAvatar.LoadMot(SdoRoomAvatar.IdleMot);

            _feetY = _avatar != null ? _avatar.FeetYAt(0f) : 0f;   // lowest skinned vertex at the bind pose
            _walkPos = new Vector3(0f, floorY, 0f);                 // EXE 0x918 init = dance-spot origin (0,0,0)
            _facing = RoomMovement.FacingDegrees(2);               // face DOWN by default (toward the camera/front)
            ApplyAvatarTransform();
        }

        private void BuildCamera()
        {
            int rtH = Mathf.Clamp(Screen.height, 600, 1600);
            int rtW = Mathf.RoundToInt(rtH * (4f / 3f));
            _rt = new RenderTexture(rtW, rtH, 24) { name = "roomSceneRT", antiAliasing = 4, filterMode = FilterMode.Bilinear };
            var camGo = new GameObject("RoomSceneCam") { layer = SceneLayer };
            camGo.transform.SetParent(transform, false);
            _cam = camGo.AddComponent<Camera>();
            _cam.orthographic = false;
            _cam.fieldOfView = 45f;                                 // EXACT decompiled projection (Camera_ctor): fovY=45,
            _cam.nearClipPlane = 5f; _cam.farClipPlane = 7500f;     //  near=5, far=7500 (4:3 implied by the RT aspect)
            _cam.cullingMask = 1 << SceneLayer;
            _cam.targetTexture = _rt;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = Color.black;
            UpdateCamera();
        }

        private void Update()
        {
            if (!_ready || _avatar == null) return;

            int dir = CurrentDir();
            if (dir >= 0)
            {
                float dtMs = Time.deltaTime * 1000f;
                // the avatar walks the camera box EXPANDED by avatarWalkMargin, so it can keep moving a bit after the
                // camera (clamped to the tighter camera box) has stopped — drifting toward the frame edge.
                Vector3 cand = RoomMovement.Step(_walkPos, dir, dtMs, walkSpeed);
                cand.x = Mathf.Clamp(cand.x, cameraBoundsMin.x - avatarWalkMargin, cameraBoundsMax.x + avatarWalkMargin);
                cand.z = Mathf.Clamp(cand.z, cameraBoundsMin.y - avatarWalkMargin, cameraBoundsMax.y + avatarWalkMargin);
                _walkPos = cand;
                _walkPos.y = floorY;
                _facing = RoomMovement.FacingDegrees(dir);   // face the way we're pressing even when blocked
                if (!_walking) { _walking = true; _avatar.SetClip(_walkMot); }
                ApplyAvatarTransform();
            }
            else if (_walking)
            {
                _walking = false;
                _avatar.SetClip(_idleMot);
            }

            UpdateCamera();
        }

        // current movement direction from the held arrow keys (priority UP/DOWN/LEFT/RIGHT), or -1 if none.
        private static int CurrentDir()
        {
            if (Input.GetKey(KeyCode.UpArrow)) return 0;
            if (Input.GetKey(KeyCode.DownArrow)) return 2;
            if (Input.GetKey(KeyCode.LeftArrow)) return 1;
            if (Input.GetKey(KeyCode.RightArrow)) return 3;
            return -1;
        }

        private void ApplyAvatarTransform()
        {
            if (_avatarRoot == null) return;
            _avatarRoot.position = new Vector3(_walkPos.x, floorY - _feetY, _walkPos.z);
            _avatarRoot.localRotation = Quaternion.Euler(0f, _facing, 0f);
        }

        // Follow camera (EXE StateRoom_UpdateCameraTarget): HORIZONTAL eye-level view (平視) of the avatar. The eye locks
        // to the avatar's X and to the look height (avatarY+50), offset only along Z by cameraBackDistance — so the line
        // of sight is level with the avatar's head, never tilted down. The EXE clamps the AVATAR (not the eye), which we
        // already do via the walk mask/box, so the eye just tracks.
        private void UpdateCamera()
        {
            if (_cam == null) return;
            // EXE: the camera tracks the clamped follow-position (StateRoom_ClampCameraPos box), looking at it at head
            // height (平視). Anchor = avatar clamped to the camera box; eye sits cameraBackDistance behind, kept in front
            // of the back wall so it never clips. (Avatar is already box-clamped, so the anchor == the avatar.)
            float ax = Mathf.Clamp(_walkPos.x, cameraBoundsMin.x, cameraBoundsMax.x);
            float az = Mathf.Clamp(_walkPos.z, cameraBoundsMin.y, cameraBoundsMax.y);
            float ey = floorY + cameraLookHeight;
            float ez = Mathf.Max(az + cameraBackDistance, cameraEyeMinZ);
            _cam.transform.position = new Vector3(ax, ey, ez);
            _cam.transform.LookAt(new Vector3(ax, ey, az), Vector3.up);
        }

        private void OnDestroy()
        {
            if (_cam != null) _cam.targetTexture = null;
            if (_rt != null) { _rt.Release(); Destroy(_rt); _rt = null; }
        }
    }
}
