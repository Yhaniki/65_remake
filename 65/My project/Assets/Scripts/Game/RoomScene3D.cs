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
        public float cameraLookHeight = 50f;                     // LOOK-target height above the floor (EXE target = avatar+50, the head)
        public float cameraEyeRise = 20f;                        // eye sits this much ABOVE the head → slight down-tilt (官方 eye 比頭高一點)
        public float cameraBackDistance = -235f;                 // eye Z offset from the anchor (signed; X locked)
        public float cameraEyeMinZ = -378f;                      // keep the eye in front of the back wall (no clip)
        // CAMERA stop box — SEPARATE from the avatar walk (官方: 人還能繼續往下/左右走一段, 但 camera 提早停). The camera
        // anchor is clamped here and the camera LOOKS at the anchor, so it stops at this box while the avatar keeps
        // walking via the MASK (furniture collision) and drifts toward the frame edge. Tighter than the mask floor on
        // purpose (avatar floor ≈ X[-199,178] Z[-234,2.3]); tune to taste — smaller = camera holds the framing sooner.
        public Vector2 cameraBoundsMin = new Vector2(-120f, -130f);   // anchor min (worldX, worldZ)
        public Vector2 cameraBoundsMax = new Vector2(100f, 0f);       // anchor max (worldX, worldZ)
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

        public float headMarkerRise = 18f;   // world Y above the head bone for the floating head portrait (EXE +15)

        /// <summary>The room render — assign to the RoomScreen backdrop RawImage. Null until Build succeeds.</summary>
        public Texture SceneTexture => _rt;
        public bool Ready => _ready;
        public Camera SceneCamForTest => _cam;          // inspection/capture only
        public SdoAvatar AvatarForTest => _avatar;
        public bool IsWalking => _walking;              // so the head portrait can MIRROR the avatar's walk/idle motion
        public float AvatarFacing => _facing;           // so the head portrait can turn with the avatar's facing

        /// <summary>Project the local avatar's head (Bip01_Head + rise) through the scene camera to a viewport point
        /// [0..1] (x right, y up). The scene camera fills the whole 4:3 backdrop, so this maps straight to the UI
        /// canvas. Returns false if the avatar/cam are missing or the head is behind the camera. Used so the head
        /// portrait FOLLOWS the avatar on screen (EXE Player_ComputeHeadRect: the looker's head portrait tracks the
        /// projected Bip01_Head each frame).</summary>
        public bool TryHeadViewport(out Vector2 vp)
        {
            vp = default;
            if (_avatar == null || _cam == null || _avatarRoot == null) return false;
            Vector3 hm = _avatar.BoneModelPos("Bip01_Head");
            if (hm == Vector3.zero) hm = _avatar.BoneModelPos("Bip01_Neck");
            if (hm == Vector3.zero) return false;
            Vector3 hw = _avatarRoot.TransformPoint(hm) + new Vector3(0f, headMarkerRise, 0f);
            Vector3 v = _cam.WorldToViewportPoint(hw);
            if (v.z <= 0f) return false;   // behind the camera
            vp = new Vector2(v.x, v.y);
            return true;
        }

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
            // spawn at the dance-spot origin (EXE default) — the room centre, which the camera frames correctly. (Do
            // NOT spawn at the mask centroid: that sits near the back wall and pushes the follow-cam into the geometry.)
            _walkPos = new Vector3(0f, floorY, 0f);
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
                Vector3 cand = RoomMovement.Step(_walkPos, dir, dtMs, walkSpeed);
                if (_mask != null)
                {
                    // MASK is the authority (furniture collision): accept the step only if it stays on the walkable
                    // floor — unless we're already off it (never trap the avatar). No box clamp; the mask is the wall.
                    if (_mask.IsWalkable(cand.x, cand.z) || !_mask.IsWalkable(_walkPos.x, _walkPos.z))
                        _walkPos = cand;
                }
                else _walkPos = RoomMovement.Clamp(cand);   // no mask → box clamp fallback
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
            // EYE = avatar clamped to the camera stop box, a bit ABOVE the head (cameraEyeRise → slight down-tilt).
            // RE'd from UpdateCameraTarget: EYE.X == TARGET.X == avatarX, so the view has NO X angle → walking LEFT/RIGHT
            // never YAWs the camera; it only translates in X and stops at the X box edge (人漂到側邊、相機不左右轉). In Z,
            // the eye is clamped but LOOK uses the REAL (unclamped) avatar Z, so walking FRONT/BACK PITCHES the camera
            // (前後有轉) to keep tracking the avatar past the Z stop. Y looks at the head (50); eye sits above it.
            float ax = Mathf.Clamp(_walkPos.x, cameraBoundsMin.x, cameraBoundsMax.x);
            float az = Mathf.Clamp(_walkPos.z, cameraBoundsMin.y, cameraBoundsMax.y);
            float ez = Mathf.Max(az + cameraBackDistance, cameraEyeMinZ);
            Vector3 eye = new Vector3(ax, floorY + cameraLookHeight + cameraEyeRise, ez);
            Vector3 look = new Vector3(ax, floorY + cameraLookHeight, _walkPos.z);   // look.X = eye.X → no yaw; look.Z = avatar → pitch
            _cam.transform.position = eye;
            _cam.transform.LookAt(look, Vector3.up);
        }

        private void OnDestroy()
        {
            if (_cam != null) _cam.targetTexture = null;
            if (_rt != null) { _rt.Release(); Destroy(_rt); _rt = null; }
        }
    }
}
