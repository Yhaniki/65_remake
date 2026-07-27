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

        /// <summary>Oversample factor for the room render target (1 = window-native, 1.5 = 1.5× then filtered down).
        /// The backdrop is upscaled to the window by the RawImage, so rendering ABOVE window size is what actually makes
        /// the avatar/room crisp — same trick the head portrait uses (192×152 RT in a 96×76 slot). Turn down to 1 if
        /// fill-rate ever matters on a big window. See <see cref="SceneRtSize"/>.</summary>
        public float sceneSupersample = 1.5f;
        public const int SceneRtMaxDim = RtSizing.MaxDim;     // per-axis RT cap (a 4K window ×1.5 would be 5760 wide)
        public const float ProjectionAspect = 800f / 600f;   // official 4:3 frame — pinned regardless of RT/window shape

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
        public float walkSpeed = RoomMovement.WalkSpeed;         // free-walk speed mult; 3.0 default, 5.0 with 加速鞋 (SpecialMotionItems)
        public bool useMask = true;                              // sample MASK.MSK for furniture collision (else box clamp)
        // Arrow-key walking gate. RoomScreen clears this while the 選歌(MusicSelDlg) modal is open so the room keeps
        // rendering (dimmed) behind the dialog but the avatar can't be walked around by stray arrow presses.
        public bool InputEnabled = true;

        private RoomMask _mask;
        private SdoAvatar _avatar;
        private Transform _avatarRoot;
        private Camera _cam;
        private RenderTexture _rt;
        private RtResizeTracker _rtTrack;     // debounced window-resize → RT re-allocation (see LateUpdate)
        private MotLoader _walkMot, _idleMot;
        private bool _flying;   // 飛行翅膀已裝備:idle=flystay、走路=fly 前傾滑動、移動時 +10 懸浮 (SpecialMotionItems)
        private readonly Dictionary<string, MotLoader> _chatActionMots = new Dictionary<string, MotLoader>(System.StringComparer.OrdinalIgnoreCase);
        private bool _male;
        private string[] _avatarParts;
        private int _bodyIndex;       // 本機角色自己的體型 (胖瘦) index 0..4;預設 0=瘦 (見 UserProfile.bodyShapeIndex)
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

        public void Build(bool male = false, string[] avatarParts = null, int bodyIndex = 0)
        {
            if (_ready) return;
            _male = male;
            _avatarParts = avatarParts;
            _bodyIndex = bodyIndex;   // 本機角色自己的體型 (胖瘦;由 RoomScreen 從 profile 帶入)
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

        // ---- 房間裡的其他玩家(連線模式)----

        /// <summary>房間裡一位遠端玩家的外觀。位置不在裡面 —— 站哪由座位編號決定(見 <see cref="SyncRemotePlayers"/>)。</summary>
        public struct RemotePlayer
        {
            public int UserId;
            public int Seat;          // 0..5:決定他站哪(每台算出來一樣)
            public bool Male;
            public string[] Parts;    // 穿搭;null → 預設整套
            public int BodyIndex;     // 體型 0..4
        }

        private readonly Dictionary<int, GameObject> _remotes = new Dictionary<int, GameObject>();
        private readonly Dictionary<int, SdoAvatar> _remoteAvatars = new Dictionary<int, SdoAvatar>();
        private readonly HashSet<int> _remoteScratch = new HashSet<int>();
        private readonly List<int> _remoteGone = new List<int>();
        private Vector3[] _remoteSpots;

        /// <summary>
        /// 讓房間裡站著的其他玩家與 server 的座位表一致:名單裡新出現的生出來、不在名單裡的拆掉。
        ///
        /// 位置怎麼決定:協定裡**還沒有走路封包**(官方是 server 發 move packet 移動每個舞者),
        /// 所以遠端玩家先站在「由座位編號決定的固定點」—— 那組點是同一顆固定種子在同一份遮罩上取樣出來的,
        /// 所以每台算出來都一樣,不會出現「你看他站在沙發上、他看自己站在中間」。
        /// (官方離線的 EXE 其實也是把六個舞者都生在 HostSpawn,再靠 server 挪開。)
        ///
        /// 只有 idle:房間裡不跳舞(沒有 DPS),所以遠端角色一律播站立待機動作。
        /// </summary>
        public void SyncRemotePlayers(List<RemotePlayer> players)
        {
            if (!_ready) return;

            // 先拆走掉的人、再生新來的 —— 反過來的話「有人剛走又有人剛進」會多佔一個位置。
            _remoteScratch.Clear();
            if (players != null) foreach (var p in players) _remoteScratch.Add(p.UserId);
            _remoteGone.Clear();
            foreach (var kv in _remotes) if (!_remoteScratch.Contains(kv.Key)) _remoteGone.Add(kv.Key);
            foreach (var id in _remoteGone)
            {
                if (_remotes[id] != null) Destroy(_remotes[id]);
                _remotes.Remove(id);
                _remoteAvatars.Remove(id);
            }

            if (players == null) return;
            foreach (var p in players)
            {
                if (p.UserId == 0 || _remotes.ContainsKey(p.UserId)) continue;
                SpawnRemote(p);
            }
        }

        private void SpawnRemote(RemotePlayer p)
        {
            var parent = new GameObject("RoomRemoteAvatar" + p.UserId);
            parent.transform.SetParent(transform, false);
            var av = SdoRoomAvatar.Build(parent, SceneLayer, portraitOpaque: false,
                                         male: p.Male, equippedParts: p.Parts, bodyIndex: p.BodyIndex);
            if (av == null) { Destroy(parent); return; }

            // 腳的偏移要在換 clip **之前**量:彎腰姿勢的第 0 幀最低點不是腳,會把人埋進地板。
            float feet = av.FeetYAt(0f);
            av.DanceEnabled = () => false;
            av.DanceTimeSec = () => -1f;
            // 飛行翅膀的浮空 idle 也照本機那套判斷 —— 不然穿翅膀的人在別人畫面上是站著的。
            string idleRel = SpecialMotionItems.IdleMotFor(p.Parts, p.Male,
                p.Male ? SdoRoomAvatar.MaleIdleMot : SdoRoomAvatar.IdleMot);
            var mot = SdoRoomAvatar.LoadMot(idleRel);
            if (mot != null)
            {
                av.RestMot = mot;
                av.SetClip(mot);
                av.PhaseOffsetSec = (p.Seat + 1) * 0.37f;   // 同一段 clip 不要整齊得像複製人
            }

            Vector3 spot = RemoteSpot(p.Seat);
            parent.transform.position = new Vector3(spot.x, floorY - feet, spot.z);
            parent.transform.localRotation = Quaternion.Euler(0f, RoomMovement.FacingDegrees(2), 0f);   // 面向鏡頭
            _remotes[p.UserId] = parent;
            _remoteAvatars[p.UserId] = av;
        }

        /// <summary>座位 → 站位。固定種子 + 同一份遮罩 → 每台算出來都一樣。</summary>
        private Vector3 RemoteSpot(int seat)
        {
            if (_remoteSpots == null) _remoteSpots = RandomDancerSpots(RoomLayout.SeatCount);
            if (_remoteSpots.Length == 0) return RoomLayout.HostSpawn;
            return _remoteSpots[seat >= 0 ? seat % _remoteSpots.Length : 0];
        }

        /// <summary>某位遠端玩家頭頂在畫面上的位置(viewport 0..1),用來擺他的名字牌。看不到 → false。</summary>
        public bool TryRemoteHeadViewport(int userId, out Vector2 vp)
        {
            vp = default;
            SdoAvatar av; GameObject go;
            if (_cam == null || !_remoteAvatars.TryGetValue(userId, out av) || av == null
                || !_remotes.TryGetValue(userId, out go) || go == null) return false;
            Vector3 hm = av.BoneModelPos("Bip01_Head");
            if (hm == Vector3.zero) hm = av.BoneModelPos("Bip01_Neck");
            if (hm == Vector3.zero) return false;
            Vector3 v = _cam.WorldToViewportPoint(go.transform.TransformPoint(hm) + new Vector3(0f, headMarkerRise, 0f));
            if (v.z <= 0f) return false;
            vp = new Vector2(v.x, v.y);
            return true;
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
            _avatar = SdoRoomAvatar.Build(parent, SceneLayer, portraitOpaque: false, male: _male, equippedParts: _avatarParts, bodyIndex: _bodyIndex);
            _avatarRoot = parent.transform;
            ApplyOutfitMotion();   // 飛行翅膀→flystay 浮空 idle;加速鞋→walkSpeed 5.0 (SpecialMotionItems)
            if (_avatar != null && _idleMot != null) _avatar.SetClip(_idleMot);   // 從生成起就用對的 idle (flystay 也是,不必等走一步)

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
        public void RebuildLocalAvatar(bool male, string[] avatarParts, int bodyIndex = 0)
        {
            bool wasFlying = _flying;   // 舊穿搭是否在飛(要在 ApplyOutfitMotion 覆寫 _flying 前捕捉)
            _male = male;
            _avatarParts = avatarParts;
            _bodyIndex = bodyIndex;   // 換穿時一併帶入最新體型 (胖瘦)
            if (!_ready) return;
            var oldRoot = _avatarRoot;
            _avatarRoot = null; _avatar = null;
            // Destroy 要到幀尾才生效 → 先關掉舊的，否則新舊兩隻同位置疊畫一幀 (換穿當場重建時會看到閃一下)
            if (oldRoot != null) oldRoot.gameObject.SetActive(false);
            var parent = new GameObject("RoomLocalAvatar");
            parent.transform.SetParent(transform, false);
            _avatar = SdoRoomAvatar.Build(parent, SceneLayer, portraitOpaque: false, male: _male, equippedParts: _avatarParts, bodyIndex: _bodyIndex);
            _avatarRoot = parent.transform;
            ApplyOutfitMotion();   // 飛行翅膀→flystay 浮空 idle;加速鞋→walkSpeed 5.0 (SpecialMotionItems)
            _feetY = _avatar != null ? _avatar.FeetYAt(0f) : 0f;
            _walking = false;
            ApplyRebuildIdle(wasFlying);
            ApplyAvatarTransform();
            if (oldRoot != null) Destroy(oldRoot.gameObject);
        }

        /// <summary>Arm the rebuilt avatar's idle. Normally an instant idle pose, BUT when the outfit change 脱下飛行翅膀
        /// (was flying, now grounded) settle the body from the flystay 浮空 pose down to the ground idle over 1s instead
        /// of popping — prime the flystay pose as the crossfade source, then blend to the new idle (使用者需求 #2)。</summary>
        private void ApplyRebuildIdle(bool wasFlying)
        {
            if (_avatar == null || _idleMot == null) return;
            if (wasFlying && !_flying)
            {
                var flystay = SdoRoomAvatar.LoadMot(SpecialMotionItems.FlyIdleMot(_male));
                if (flystay != null)
                {
                    _avatar.PrimeBlendFrom(flystay);   // 顯示 flystay 當 crossfade 起點
                    _avatar.BlendNextClip(1f);         // 只此一次用 1 秒(不影響之後 idle↔walk 的預設混色)
                    _avatar.SetClip(_idleMot);         // → 1 秒平滑 flystay→地面 idle
                    return;
                }
            }
            _avatar.SetClip(_idleMot);   // 一般:從生成起就用對的 idle(flystay 也是,不必等走一步)
        }

        /// <summary>Resolve the idle/walk clips + walk speed for the CURRENT outfit — the decompiled special-item traits
        /// ([[sdo-special-item-idle-walk]] / <see cref="SpecialMotionItems"/>): a 飛行翅膀 (flying wing) swaps the idle to
        /// the flystay 浮空 clip (rest cat 0x2c); a 加速鞋 (speed shoe) bumps the free-walk speed to 5.0 (unless a wing
        /// is also worn, which forces 3.0). Called after every (re)build so 換裝 picks the trait up immediately.</summary>
        private void ApplyOutfitMotion()
        {
            _flying = SpecialMotionItems.WearsFlyingWing(_avatarParts);
            bool fast = SpecialMotionItems.WearsFastWalkShoe(_avatarParts);
            // 飛行翅膀:idle→flystay 浮空,走路→fly(前傾滑動),速度強制 3.0(028:2774),移動時 body Y +10 懸浮(028:2852)。
            // 「哪些翅膀會飛」離線推不出來 → SpecialMotionItems 用硬編 5 id + 線上實測名單(見該檔)。
            string idleRel = SpecialMotionItems.IdleMotFor(_avatarParts, _male, _male ? SdoRoomAvatar.MaleIdleMot : SdoRoomAvatar.IdleMot);
            string walkRel = SpecialMotionItems.WalkMotFor(_avatarParts, _male, _male ? SdoRoomAvatar.MaleWalkMot : SdoRoomAvatar.WalkMot);
            _idleMot = SdoRoomAvatar.LoadMot(idleRel);
            _walkMot = SdoRoomAvatar.LoadMot(walkRel);
            walkSpeed = SpecialMotionItems.WalkSpeedMult(fast, _flying);
        }

        private void BuildCamera()
        {
            SceneRtSize(Screen.width, Screen.height, sceneSupersample, out int rtW, out int rtH);
            _rtTrack.Reset(Screen.width, Screen.height);
            _rt = new RenderTexture(rtW, rtH, 24) { name = "roomSceneRT", antiAliasing = 4, filterMode = FilterMode.Bilinear };
            var camGo = new GameObject("RoomSceneCam") { layer = SceneLayer };
            camGo.transform.SetParent(transform, false);
            _cam = camGo.AddComponent<Camera>();
            _cam.orthographic = false;
            _cam.fieldOfView = 45f;                                 // EXACT decompiled projection (Camera_ctor): fovY=45,
            _cam.nearClipPlane = 5f; _cam.farClipPlane = 7500f;     //  near=5, far=7500
            _cam.cullingMask = 1 << SceneLayer;
            _cam.targetTexture = _rt;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = Color.black;
            UpdateCamera();
        }

        /// <summary>
        /// Pixel size of the room-render RT for a given window size. The RT is NOT 4:3 — it matches the WINDOW shape and
        /// is oversampled by <paramref name="supersample"/>, because the backdrop RawImage covers the whole window and
        /// AspectController runs in Stretch mode (the 4:3 frame is non-uniformly stretched to fill it). Sizing the RT
        /// 4:3 (the old <c>height×4/3</c>) left it NARROWER than the window on any wide screen, so the backdrop was
        /// magnified horizontally — the reason the room looked soft while the 192×152 head-portrait RT (rendered ABOVE
        /// its 96×76 slot, i.e. downsampled) stayed crisp. The 4:3 PROJECTION is preserved by pinning
        /// <see cref="Camera.aspect"/> in <see cref="UpdateCamera"/>, so framing is unchanged — only sharper.
        /// Floors at 800×600 (the official frame; keeps small windows from going below the original) and caps each axis
        /// at <see cref="SceneRtMaxDim"/> so a 4K window can't blow up VRAM/fill-rate.
        /// </summary>
        public static void SceneRtSize(int screenW, int screenH, float supersample, out int w, out int h)
            => RtSizing.SlotRtSize(screenW, screenH, RtSizing.LogicalW, RtSizing.LogicalH, supersample, out w, out h);

        /// <summary>Window resize / fullscreen toggle → re-allocate the RT at the new size. The SAME RenderTexture
        /// instance is kept (Release → set width/height → Create), so RoomScreen's backdrop RawImage and the head-slot
        /// references stay valid without re-wiring — the texture was only assigned once, in RoomScreen.OnShow.
        /// Debounced: dragging a window edge changes Screen.width/height EVERY frame, and re-allocating a multi-megabyte
        /// 4×MSAA RT per frame would stutter the drag. During the drag the old RT just keeps being upscaled (i.e. it
        /// looks like it did before this fix), then snaps sharp once the size holds still.</summary>
        private void LateUpdate()
        {
            if (_rt == null) return;
            if (!_rtTrack.Tick(Screen.width, Screen.height, Time.unscaledTime)) return;
            SceneRtSize(Screen.width, Screen.height, sceneSupersample, out int w, out int h);
            RtSizing.Apply(_rt, w, h);
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
                ApplyAvatarTransform();   // 停下:移除飛行懸浮 (+10)，回地面高度(否則 flystay 停在半空)
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
            // 飛行翅膀移動時 body Y +10 懸浮(Player_StepMovement 028:2852 fStack_8 += 10);停下(flystay)回地面高度。
            float hover = (_flying && _walking) ? SpecialMotionItems.FlyHoverY : 0f;
            _avatarRoot.position = new Vector3(_walkPos.x, floorY - _feetY + hover, _walkPos.z);
            _avatarRoot.localRotation = Quaternion.Euler(0f, _facing, 0f);
        }

        // Follow camera (EXE StateRoom_UpdateCameraTarget): HORIZONTAL eye-level view (平視) of the avatar. The eye locks
        // to the avatar's X and to the look height (avatarY+50), offset only along Z by cameraBackDistance — so the line
        // of sight is level with the avatar's head, never tilted down. The EXE clamps the AVATAR (not the eye), which we
        // already do via the walk mask/box, so the eye just tracks.
        private void UpdateCamera()
        {
            if (_cam == null) return;
            // Pin the official 4:3 projection: the RT is window-shaped (see SceneRtSize), so WITHOUT this the aspect
            // would be inferred from the RT and a wide window would widen the field of view — the framing would change.
            // Forcing 4:3 into a wide viewport reproduces exactly what AspectController does for the gameplay camera in
            // Stretch mode, so the backdrop keeps the same composition it has today. (Set every frame: it's free, and
            // an aspect assignment is undone by any ResetAspect elsewhere.)
            _cam.aspect = ProjectionAspect;
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
