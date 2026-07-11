using System.Collections.Generic;
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
    /// Reuses the validated render path: SceneLoader.Load for the mesh (SCNROOM is a single-block 17-material scene,
    /// fully compatible) and the exact decompiled scene-camera projection (fovY 45, near 5, far 7500). SCNROOM is the
    /// official "開房間" lobby (Scene_LoadBackground id 37 / 0x25); its animated stage props (the TV/dianshi, the
    /// speakers/laba, the waiting lights/guang and the tiered dais/taizi) are loaded by <see cref="RoomMapobjs"/>.
    /// </summary>
    public sealed class RoomScene3D : MonoBehaviour
    {
        public const int SceneLayer = 4;   // the perspective stage layer (same as gameplay; the play screen isn't alive here)
        public const string ScenePath = "SCENE/SCNROOM";   // official open-room lobby (id 37); SCNCHIRSROOM is off-table

        public bool loadMapobjs = true;          // load the Room_obj stage props (dianshi/laba/guang/taizi)
        public bool fillTestAvatars = false;     // OFF: only the local host is shown (matches the offline solo room). Set
                                                 // true to drop the same avatar on the other 15 slots for layout testing.
        public bool overview = false;            // frame the whole room from a fixed high vantage (verification captures)

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
        // Arrow-key walking gate. RoomScreen clears this while the 選歌(MusicSelDlg) modal is open so the room keeps
        // rendering (dimmed) behind the dialog but the avatar can't be walked around by stray arrow presses.
        public bool InputEnabled = true;

        private RoomMask _mask;
        private SdoAvatar _avatar;
        private Transform _avatarRoot;
        private Camera _cam;
        private RenderTexture _rt;
        private MotLoader _walkMot, _idleMot;
        private readonly Dictionary<string, MotLoader> _chatActionMots = new Dictionary<string, MotLoader>(System.StringComparer.OrdinalIgnoreCase);
        private bool _male;
        private string[] _avatarParts;
        private Vector3 _walkPos;     // logical floor position (X, floorY, Z)
        private float _feetY;         // model-space feet offset so the feet rest on floorY
        private float _facing;        // current Unity yaw (degrees)
        private float _chatActionUntil = -1f;
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

        public bool PlayChatAction(string motionRelPath)
        {
            if (!_ready || _avatar == null || string.IsNullOrEmpty(motionRelPath)) return false;
            var mot = LoadChatActionMot(motionRelPath);
            if (mot == null || mot.MaxTime <= 0f) return false;

            _walking = false;
            _avatar.SetClip(_idleMot);
            _avatar.PlayOneShot(mot, false);
            _chatActionUntil = Time.time + (mot.MaxTime + 1f) / Mathf.Max(1f, _avatar.Fps);
            return true;
        }

        private MotLoader LoadChatActionMot(string motionRelPath)
        {
            if (string.IsNullOrEmpty(motionRelPath)) return null;
            if (_chatActionMots.TryGetValue(motionRelPath, out var mot)) return mot;
            mot = SdoRoomAvatar.LoadMot(motionRelPath);
            _chatActionMots[motionRelPath] = mot;
            return mot;
        }

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

        /// <summary>Bubble anchor sits at the SHOULDER (neck bone), not the chest — the bubble body then floats up to
        /// head/name height with its tail pointing down at the shoulder. (Earlier this lerped down toward the spine and
        /// the bubble landed at the waist; RoomScreen places the body above this anchor.)</summary>
        public bool TryChatBubbleViewport(out Vector2 vp)
        {
            vp = default;
            if (_avatar == null || _cam == null || _avatarRoot == null) return false;
            Vector3 bp = _avatar.BoneModelPos("Bip01_Neck");
            if (bp == Vector3.zero) bp = _avatar.BoneModelPos("Bip01_Head");
            if (bp == Vector3.zero) bp = _avatar.BoneModelPos("Bip01_Spine1");
            if (bp == Vector3.zero) bp = _avatar.BoneModelPos("Bip01_Spine");
            if (bp == Vector3.zero) return false;
            Vector3 bw = _avatarRoot.TransformPoint(bp);
            Vector3 v = _cam.WorldToViewportPoint(bw);
            if (v.z <= 0f) return false;
            vp = new Vector2(v.x, v.y);
            return true;
        }

        public void Build(bool male = false, string[] avatarParts = null)
        {
            if (_ready) return;
            _male = male;
            _avatarParts = avatarParts;
            LoadScene();
            LoadMask();
            LoadAvatar();
            if (loadMapobjs) LoadMapobjs();
            if (fillTestAvatars) FillTestAvatars();
            BuildCamera();
            _ready = true;
        }

        // Load the room's animated stage props (Room_obj mapobjs) the official open-room loads (case 0x25): the TV,
        // the four speakers, the eight waiting lights and the tiered dais — all geometry-baked at the origin.
        private void LoadMapobjs()
        {
            var go = new GameObject("RoomMapobjs") { layer = SceneLayer };
            go.transform.SetParent(transform, false);
            var m = go.AddComponent<RoomMapobjs>();
            m.layer = SceneLayer;
            m.BuildScnRoom();
        }

        // Dance area the random dancers (slots 1-5) cluster in — kept near the MIDDLE of the room (per request), on the
        // open floor a little in front of the sofa. Tunable in the inspector.
        public Vector2 dancerAreaCenter = new Vector2(-25f, -75f);
        public float dancerAreaRadius = 65f;
        public float dancerSpacing = 24f;

        // TEST scaffold: populate the room. Slot 0 = the local HOST (the separate walkable avatar at HostSpawn, so it's
        // skipped here). Slots 1-5 = the other dancers: the offline EXE has NO per-dancer formation (all dancers spawn at
        // HostSpawn and the server spreads them), so we drop them at RANDOM WALKABLE spots clustered near the room middle.
        // Slots 6-15 = the ten lookers at their RE'd .data positions (af0). All hold their cat-0/cat-0x21 standby motions.
        private void FillTestAvatars()
        {
            var dancerSpots = RandomDancerSpots(RoomLayout.SeatCount - 1);   // slots 1..5
            int di = 0;
            for (int slot = 0; slot < RoomLayout.SlotCount; slot++)
            {
                if (slot == 0) continue;   // slot 0 = the local host (already spawned as the walkable avatar at HostSpawn)
                var parent = new GameObject("RoomSlotAvatar" + slot);
                parent.transform.SetParent(transform, false);
                var av = SdoRoomAvatar.Build(parent, SceneLayer, portraitOpaque: false);
                if (av == null) { Destroy(parent); continue; }

                // Measure the feet offset from the STANDING idle BEFORE swapping in the slot motion: a bent WAITING pose's
                // frame-0 lowest vertex isn't the feet, which mis-grounded (sank) some lookers. The model is identical for
                // all, so this one standing offset grounds every avatar regardless of its looping clip.
                float feet = av.FeetYAt(0f);

                av.DanceEnabled = () => false;     // hold the standby idle (no DPS in the lobby)
                av.DanceTimeSec = () => -1f;
                // dancers 1-5 hold the cat-0 standby idle; lookers 6-15 their distinct cat-0x21 WAITING pose. All female
                // (default WOMAN). Desync the loop phase so same-clip avatars aren't in lockstep.
                var slotMot = SdoRoomAvatar.LoadMot("MOTION/" + RoomLayout.SlotMotionName(slot, female: true) + ".MOT");
                if (slotMot != null) { av.RestMot = slotMot; av.SetClip(slotMot); av.PhaseOffsetSec = slot * 0.31f; }

                Vector3 a = slot < RoomLayout.SeatCount
                    ? (di < dancerSpots.Length ? dancerSpots[di++] : RoomLayout.HostSpawn)   // dancers 1-5: random walkable
                    : RoomLayout.SpectatorAnchors[slot - RoomLayout.SeatCount];              // lookers 6-15: af0
                parent.transform.position = new Vector3(a.x, floorY - feet, a.z);
                parent.transform.localRotation = Quaternion.Euler(0f, RoomLayout.SlotFacingDegrees(slot), 0f);
            }
        }

        // Pick <count> RANDOM WALKABLE spots for the filler dancers, clustered (uniform-in-disk) around the central
        // dance area, kept apart by dancerSpacing and clear of the host. Rejection-samples the SCNROOM mask so none land
        // on the sofa/furniture or off-map. Fixed seed → reproducible spread (change the seed for a different layout).
        private Vector3[] RandomDancerSpots(int count)
        {
            var rng = new System.Random(0x5D0);
            var pts = new System.Collections.Generic.List<Vector3>();
            var host = new Vector2(RoomLayout.HostSpawn.x, RoomLayout.HostSpawn.z);
            float sp2 = dancerSpacing * dancerSpacing;
            for (int guard = 0; guard < 9000 && pts.Count < count; guard++)
            {
                double ang = rng.NextDouble() * 6.2831853;
                double rad = System.Math.Sqrt(rng.NextDouble()) * dancerAreaRadius;
                float x = dancerAreaCenter.x + (float)(rad * System.Math.Cos(ang));
                float z = dancerAreaCenter.y + (float)(rad * System.Math.Sin(ang));
                if (!WalkableRobust(x, z)) continue;
                var v = new Vector2(x, z);
                if ((v - host).sqrMagnitude < sp2) continue;
                bool clash = false;
                foreach (var p in pts) if ((new Vector2(p.x, p.z) - v).sqrMagnitude < sp2) { clash = true; break; }
                if (!clash) pts.Add(new Vector3(x, floorY, z));
            }
            return pts.ToArray();
        }

        // walkable at (x,z) AND a small footprint around it (so a dancer isn't on a thin sliver / edge). No mask → true.
        private bool WalkableRobust(float x, float z)
        {
            if (_mask == null) return true;
            for (int dx = -8; dx <= 8; dx += 8)
                for (int dz = -8; dz <= 8; dz += 8)
                    if (!_mask.IsWalkable(x + dx, z + dz)) return false;
            return true;
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
            Debug.Log($"[room-scene] {ScenePath}: {res.Materials.Length} subsets, bounds c={res.Mesh.bounds.center} s={res.Mesh.bounds.size}");
        }

        private void LoadAvatar()
        {
            var parent = new GameObject("RoomLocalAvatar");
            parent.transform.SetParent(transform, false);
            _avatar = SdoRoomAvatar.Build(parent, SceneLayer, portraitOpaque: false, male: _male, equippedParts: _avatarParts);
            _avatarRoot = parent.transform;
            _walkMot = SdoRoomAvatar.LoadMot(_male ? SdoRoomAvatar.MaleWalkMot : SdoRoomAvatar.WalkMot);
            _idleMot = SdoRoomAvatar.LoadMot(_male ? SdoRoomAvatar.MaleIdleMot : SdoRoomAvatar.IdleMot);

            _feetY = _avatar != null ? _avatar.FeetYAt(0f) : 0f;   // lowest skinned vertex at the bind pose
            // Host spawn = (-100, 0, -26): the REAL fixed offline spawn, captured via Frida from the running official EXE
            // (the host avatar slot-0 object position) and then confirmed in the decompile — flat sdo_stand_alone.exe.c
            // 99644-99660 loops the 6 dancer slots and writes each player +4/+8/+0xc = (-100, 0, -26); offline only the
            // host (slot 0) exists, so it stays here (the other dancers would be moved by server move-packets). This is
            // on the walkable floor (mask-validated). NOT origin (origin is on the non-walkable dais).
            _walkPos = new Vector3(RoomLayout.HostSpawn.x, floorY, RoomLayout.HostSpawn.z);
            _facing = RoomMovement.FacingDegrees(2);               // face DOWN by default (toward the camera/front)
            ApplyAvatarTransform();
        }

        /// <summary>Rebuild the local host avatar with a new outfit (儲物櫃 換穿) without rebuilding the whole scene —
        /// preserves the current walk position/facing and returns it to its idle pose. No-op (just stores) until Build ran.</summary>
        public void RebuildLocalAvatar(bool male, string[] avatarParts)
        {
            _male = male;
            _avatarParts = avatarParts;
            if (!_ready) return;
            var oldRoot = _avatarRoot;
            _avatarRoot = null; _avatar = null;
            var parent = new GameObject("RoomLocalAvatar");
            parent.transform.SetParent(transform, false);
            _avatar = SdoRoomAvatar.Build(parent, SceneLayer, portraitOpaque: false, male: _male, equippedParts: _avatarParts);
            _avatarRoot = parent.transform;
            _walkMot = SdoRoomAvatar.LoadMot(_male ? SdoRoomAvatar.MaleWalkMot : SdoRoomAvatar.WalkMot);
            _idleMot = SdoRoomAvatar.LoadMot(_male ? SdoRoomAvatar.MaleIdleMot : SdoRoomAvatar.IdleMot);
            _feetY = _avatar != null ? _avatar.FeetYAt(0f) : 0f;
            _walking = false;
            if (_avatar != null && _idleMot != null) _avatar.SetClip(_idleMot);
            ApplyAvatarTransform();
            if (oldRoot != null) Destroy(oldRoot.gameObject);
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
            if (_chatActionUntil > 0f && Time.time >= _chatActionUntil)
            {
                _avatar.ClearOneShot();
                _chatActionUntil = -1f;
                if (!_walking) _avatar.SetClip(_idleMot);
            }

            int dir = InputEnabled ? CurrentDir() : -1;   // 選歌 modal 開著時凍結走動(房間仍在後面 render)
            if (dir >= 0)
            {
                if (_chatActionUntil > 0f)
                {
                    _avatar.ClearOneShot();
                    _chatActionUntil = -1f;
                }
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
            if (overview)
            {
                // fixed high-back vantage that frames the whole room (all 16 slots span ~X[-185,168] Z[-168,110]) for
                // verification captures — not used in normal play (the follow-cam below tracks the local avatar).
                _cam.transform.position = new Vector3(0f, 250f, -430f);
                _cam.transform.LookAt(new Vector3(0f, 10f, -40f), Vector3.up);
                return;
            }
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
