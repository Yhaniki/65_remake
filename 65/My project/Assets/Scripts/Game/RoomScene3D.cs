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

        /// <summary>
        /// 遠端玩家角色專屬的 layer。
        ///
        /// 為什麼要與場景分開:六格頭貼的相機要拍「某一個人的臉」,而房間的家具(沙發/喇叭/電視)
        /// 與角色若同層,頭貼就會拍到家具。分層之後頭貼相機只看這一層 ——
        /// 家具留在 <see cref="SceneLayer"/>(那是對的,家具不該入頭貼)。
        /// 房間主相機兩層都看,所以畫面完全不變。
        /// </summary>
        public const int RemoteAvatarLayer = 13;

        /// <summary>
        /// 頭上聊天泡的 layer(TagManager 命名 "RoomBubble")。
        ///
        /// 泡的**畫面**住在這一層、由房間相機一起 render,這樣它才會吃 GPU 的深度測試 ——
        /// 站在說話者前面的人就能逐像素把泡切掉(使用者要的前後景)。泡的**排版與滑鼠命中**
        /// 仍留在 UI 層(RoomScreen 那邊的透明代理),所以整套鏈物理/拖曳一行都沒改。
        ///
        /// 與角色分層的理由跟 <see cref="RemoteAvatarLayer"/> 一樣:六格頭貼相機只看角色那層,
        /// 泡不該入頭貼。前端 UI 相機也要把這層遮掉(RoomScreen 進房時做),否則泡會被畫兩次。
        /// </summary>
        public const int BubbleLayer = 14;

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
        private int _localSeat;       // 本機座位 → 出生點(見 SpawnSpot);離線恆 0
        private bool _hasWalked;      // 本機走過一步了嗎 —— 走過之後就不許再被 SetLocalSeat 挪動

        public float headMarkerRise = 18f;   // world Y above the head bone for the floating head portrait (EXE +15)

        /// <summary>The room render — assign to the RoomScreen backdrop RawImage. Null until Build succeeds.</summary>
        public Texture SceneTexture => _rt;
        public bool Ready => _ready;
        public Camera SceneCamForTest => _cam;          // inspection/capture only
        /// <summary>房間那台相機。頭上泡的畫住在它的世界裡(<see cref="BubbleLayer"/>),所以 RoomScreen
        /// 需要它的位置/朝向/投影矩陣來擺泡的平面。Null 直到 Build 成功。</summary>
        public Camera SceneCamera => _cam;
        public SdoAvatar AvatarForTest => _avatar;
        public bool IsWalking => _walking;              // so the head portrait can MIRROR the avatar's walk/idle motion
        public float AvatarFacing => _facing;           // so the head portrait can turn with the avatar's facing

        // 本機在房間地板上的位置。RoomScreen 靠這兩個把位置送上網(RoomScene3D 在 Sdo.Game,
        // 不認識連線層;RoomScreen 是唯一的黏合層,離線時整段跳過)。
        public float LocalWalkX => _walkPos.x;
        public float LocalWalkZ => _walkPos.z;

        /// <summary>
        /// 晚到的本機座位。
        ///
        /// 🔴 連線模式下「進房」與「拿到房間快照」是兩件事:<c>OnShow</c> 那一刻 <c>CurrentRoom</c>
        /// 常常還是 null,座位查不到就會退回 0 —— 而座位 0 的出生點是房主的位置,
        /// 於是**客戶端會生在房主身上**(實測:兩隻角色完全疊在一起,兩張名字牌重疊)。
        /// 所以座位一確定就要補正一次。已經走過一步就不動了(那時玩家自己的位置才是真相)。
        /// </summary>
        public void SetLocalSeat(int seat)
        {
            if (seat < 0 || seat == _localSeat || _hasWalked || !_ready) return;
            _localSeat = seat;
            Vector3 spawn = SpawnSpot(seat);
            _walkPos = new Vector3(spawn.x, floorY, spawn.z);
            ApplyAvatarTransform();
        }

        /// <summary>
        /// 座位 → 出生點。**本機與遠端都用它**,所以「六個人都還沒走過」時每台算出來的站位都一樣。
        ///
        /// 座位 0 保留給 <c>RoomLayout.HostSpawn</c>(那是從官方 EXE 抓出來的真實出生點,
        /// 也是離線單人房的相機校準基準與既有截圖的依據);1..5 用固定種子在遮罩上取樣的點。
        /// </summary>
        public Vector3 SpawnSpot(int seat)
        {
            if (seat <= 0) return RoomLayout.HostSpawn;
            if (_remoteSpots == null) _remoteSpots = RandomDancerSpots(RoomLayout.SeatCount);
            if (_remoteSpots.Length == 0) return RoomLayout.HostSpawn;
            return _remoteSpots[seat % _remoteSpots.Length];
        }

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
            Vector3 bw;
            if (_cam == null || !TryChatBubbleAnchorWorld(out bw)) return false;
            Vector3 v = _cam.WorldToViewportPoint(bw);
            if (v.z <= 0f) return false;
            vp = new Vector2(v.x, v.y);
            return true;
        }

        /// <summary>本機聊天泡錨點的**世界座標**(泡的畫進了房間相機之後要用它算平面;見
        /// <see cref="RoomBubbleWorldAnchor"/>)。與 <see cref="TryChatBubbleViewport"/> 是同一個點 ——
        /// 刻意共用同一段骨骼 fallback,不然螢幕位置(UI 排版用 viewport)與深度(3D 用世界點)會對不上。</summary>
        public bool TryChatBubbleAnchorWorld(out Vector3 world)
        {
            world = default;
            if (_avatar == null || _avatarRoot == null) return false;
            Vector3 bp = _avatar.BoneModelPos("Bip01_Neck");
            if (bp == Vector3.zero) bp = _avatar.BoneModelPos("Bip01_Head");
            if (bp == Vector3.zero) bp = _avatar.BoneModelPos("Bip01_Spine1");
            if (bp == Vector3.zero) bp = _avatar.BoneModelPos("Bip01_Spine");
            if (bp == Vector3.zero) return false;
            world = _avatarRoot.TransformPoint(bp);
            return true;
        }

        /// <param name="localSeat">
        /// 本機在房間裡的座位(0..5)。決定本機角色的出生點 —— 與遠端用的是**同一張表**
        /// (<see cref="SpawnSpot"/>),所以「大家都還沒走過」時每台看到的站位都一致。
        /// 離線傳 0 → 與加連線之前完全一樣(HostSpawn)。
        /// </param>
        public void Build(bool male = false, string[] avatarParts = null, int bodyIndex = 0, int localSeat = 0)
        {
            if (_ready) return;
            _male = male;
            _avatarParts = avatarParts;
            _bodyIndex = bodyIndex;   // 本機角色自己的體型 (胖瘦;由 RoomScreen 從 profile 帶入)
            _localSeat = localSeat < 0 ? 0 : localSeat;
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

            /// <summary>
            /// 外觀的比較鍵(<c>NetAvatarLook.Key()</c>)。**「要不要重建這隻角色」只看它。**
            ///
            /// 🔴 不能用 rev 判斷:rev 會因為別人按準備、換歌、換隊…一直變,而重建一隻 avatar
            /// 要讀十幾個部件檔(50-100ms hitch)。也不能不判斷 —— 那就是「換裝在別人畫面上
            /// 永遠不生效」的根因(舊版看到 userId 已存在就 continue,外觀更新永遠被跳過)。
            /// </summary>
            public string LookKey;
        }

        /// <summary>一位遠端玩家在房間裡的執行期狀態(位置插值 + 走路/待機 clip)。</summary>
        private sealed class Remote
        {
            public GameObject Go;
            public SdoAvatar Av;
            public float FeetY;           // 站立 idle 的腳偏移(生成時量一次)
            public MotLoader Idle, Walk;
            public bool Flying;           // 飛行翅膀 → 移動時 body Y +10 懸浮(與本機同一條規則)
            public Vector3 Pos;           // 現在畫出來的位置(插值後)
            public Vector3 Target;        // server 最新一筆
            public float Facing, TargetFacing;
            public bool Walking;          // server 說他在走
            public float LastMoveAt;      // 最後一次收到位置的時間(秒)
            public bool ClipIsWalk;       // 目前掛的是走路 clip 嗎(避免每幀重設)
            public float ActionUntil;     // 聊天動作播到什麼時候(秒);<=0 = 沒在播。**per-avatar**,見 PlayRemoteChatAction
            public string LookKey;        // 生這隻時用的外觀鍵 —— 與新快照不同就重建
            public int Seat;
        }

        private readonly Dictionary<int, Remote> _remotes = new Dictionary<int, Remote>();
        private readonly HashSet<int> _remoteScratch = new HashSet<int>();
        private readonly List<int> _remoteGone = new List<int>();
        private Vector3[] _remoteSpots;

        /// <summary>
        /// 位置插值的收斂速率(1/秒)。官方 formation 的做法是每幀 <c>cur*0.9 + target*0.1</c>,
        /// 但那個寫法的速度會跟著幀率變(120fps 的人看起來比 30fps 的人跟得緊)——
        /// 這裡換成時間無關的等效式 <c>1 - exp(-rate*dt)</c>,rate 取 12 大約等於 60fps 下的 0.9/0.1。
        /// </summary>
        private const float RemoteLerpRate = 12f;

        /// <summary>
        /// 多久沒收到新位置就當他停了(秒)。位置流是 10 Hz,所以 0.35 秒 = 漏了三筆才判定停下 ——
        /// 這條是為了「走路 clip 卡住不停」那個症狀:走到一半斷線或封包被丟掉時,
        /// 沒有這個保險他會原地踏步到永遠。
        /// </summary>
        private const float RemoteWalkTimeoutSec = 0.35f;

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
                if (_remotes[id] != null && _remotes[id].Go != null) Destroy(_remotes[id].Go);
                _remotes.Remove(id);
            }

            if (players == null) return;
            foreach (var p in players)
            {
                if (p.UserId == 0) continue;
                Remote cur;
                if (!_remotes.TryGetValue(p.UserId, out cur) || cur == null)
                {
                    SpawnRemote(p);           // 新來的人
                    continue;
                }
                if (string.Equals(cur.LookKey, p.LookKey, System.StringComparison.Ordinal))
                    continue;                 // 外觀沒變(rev 是因為別的事變的)→ 什麼都不做
                RebuildRemote(p, cur);        // 換裝/晚到的 setLook → 重建,但**留在原地**
            }
        }

        /// <summary>
        /// 換裝(或晚到的第一份正確外觀)→ 重建這隻角色。
        ///
        /// 🔴 **必須沿用當前的位置與朝向** —— 直接用 <c>RemoteSpot(seat)</c> 重生的話,
        /// 對方在房間裡走到一半換件衣服就會被瞬移回座位起點。
        /// 先 <c>SetActive(false)</c> 再 <c>Destroy</c>:Destroy 要到幀尾才生效,
        /// 不關掉的話新舊兩隻會疊在同一個位置一幀(本機的 RebuildLocalAvatar 已經踩過這個坑)。
        /// </summary>
        private void RebuildRemote(RemotePlayer p, Remote old)
        {
            Vector3 pos = old.Pos, target = old.Target;
            float facing = old.Facing, targetFacing = old.TargetFacing;
            bool walking = old.Walking;
            float lastMoveAt = old.LastMoveAt;

            if (old.Go != null) { old.Go.SetActive(false); Destroy(old.Go); }
            _remotes.Remove(p.UserId);

            SpawnRemote(p);

            Remote fresh;
            if (!_remotes.TryGetValue(p.UserId, out fresh) || fresh == null) return;
            fresh.Pos = pos; fresh.Target = target;
            fresh.Facing = facing; fresh.TargetFacing = targetFacing;
            fresh.Walking = walking; fresh.LastMoveAt = lastMoveAt;
            ApplyRemoteTransform(fresh);
        }

        private void SpawnRemote(RemotePlayer p)
        {
            var parent = new GameObject("RoomRemoteAvatar" + p.UserId);
            parent.transform.SetParent(transform, false);
            var av = SdoRoomAvatar.Build(parent, RemoteAvatarLayer, portraitOpaque: false,
                                         male: p.Male, equippedParts: p.Parts, bodyIndex: p.BodyIndex);
            if (av == null) { Destroy(parent); return; }

            // 腳的偏移要在換 clip **之前**量:彎腰姿勢的第 0 幀最低點不是腳,會把人埋進地板。
            float feet = av.FeetYAt(0f);
            av.DanceEnabled = () => false;
            av.DanceTimeSec = () => -1f;
            // 飛行翅膀的浮空 idle / 滑行走路也照本機那套判斷 —— 不然穿飛行翅膀的人在別人畫面上是走路的。
            string idleRel = SpecialMotionItems.IdleMotFor(p.Parts, p.Male,
                p.Male ? SdoRoomAvatar.MaleIdleMot : SdoRoomAvatar.IdleMot);
            string walkRel = SpecialMotionItems.WalkMotFor(p.Parts, p.Male,
                p.Male ? SdoRoomAvatar.MaleWalkMot : SdoRoomAvatar.WalkMot);

            var r = new Remote
            {
                Go = parent,
                Av = av,
                FeetY = feet,
                Idle = SdoRoomAvatar.LoadMot(idleRel),
                Walk = SdoRoomAvatar.LoadMot(walkRel),
                Flying = SpecialMotionItems.WearsFlyingWing(p.Parts),
                LookKey = p.LookKey,
                Seat = p.Seat,
            };
            if (r.Idle != null)
            {
                av.RestMot = r.Idle;
                av.PhaseOffsetSec = (p.Seat + 1) * 0.37f;   // 同一段 clip 不要整齊得像複製人
                av.PoseFrame(av.AutoLoopFrame);             // 上面 FeetYAt 把人停在第 0 幀 → 擺回迴圈當下(見換裝那條)
                av.SetClip(r.Idle);
            }

            // 還沒收到他的位置之前先站在座位對應的固定點(每台算出來一樣),收到第一筆就會滑過去。
            Vector3 spot = SpawnSpot(p.Seat);
            r.Pos = r.Target = new Vector3(spot.x, floorY, spot.z);
            r.Facing = r.TargetFacing = RoomMovement.FacingDegrees(2);   // 面向鏡頭
            _remotes[p.UserId] = r;
            ApplyRemoteTransform(r);
        }

        /// <summary>
        /// 套用 server 推來的位置。收到的是**目標**,不是直接搬過去 —— 位置流是 10 Hz,
        /// 直接寫的話遠端角色會一格一格跳;每幀往目標插值才看得出是在走。
        ///
        /// 還沒生出來的人(座位快照比位置流晚到)直接忽略:下一輪位置就會到。
        /// </summary>
        public void ApplyRemoteMove(int userId, float x, float z, float facing, bool walking)
        {
            Remote r;
            if (!_remotes.TryGetValue(userId, out r) || r == null) return;
            r.Target = new Vector3(x, floorY, z);
            r.TargetFacing = facing;
            r.Walking = walking;
            r.LastMoveAt = Time.time;
        }

        // 每幀:位置/朝向插值 + 走路/待機 clip 切換。放在 Update 的本機走動之後。
        private void UpdateRemotes()
        {
            if (_remotes.Count == 0) return;
            float k = 1f - Mathf.Exp(-RemoteLerpRate * Time.deltaTime);
            float now = Time.time;

            foreach (var kv in _remotes)
            {
                var r = kv.Value;
                if (r == null || r.Go == null || r.Av == null) continue;

                TickRemoteChatAction(r, now);
                r.Pos = Vector3.Lerp(r.Pos, r.Target, k);
                r.Facing = Mathf.LerpAngle(r.Facing, r.TargetFacing, k);

                // 「他在走嗎」= server 說在走 **且** 最近有收到位置。少了後半條的話,
                // 走到一半斷線的人會原地踏步到永遠(見 RemoteWalkTimeoutSec)。
                bool walking = r.Walking && (now - r.LastMoveAt) < RemoteWalkTimeoutSec;
                if (walking != r.ClipIsWalk)
                {
                    r.ClipIsWalk = walking;
                    // 正在播聊天動作時只記狀態、不換 clip —— 換了會把動作切掉(動作結束後 TickRemoteChatAction 會補上)。
                    if (r.ActionUntil <= 0f)
                    {
                        var clip = walking ? r.Walk : r.Idle;
                        if (clip != null) r.Av.SetClip(clip);
                    }
                }
                ApplyRemoteTransform(r);
            }
        }

        private void ApplyRemoteTransform(Remote r)
        {
            if (r.Go == null) return;
            // 飛行翅膀移動時 +10 懸浮 —— 與本機的 ApplyAvatarTransform 同一條規則。
            float hover = (r.Flying && r.ClipIsWalk) ? SpecialMotionItems.FlyHoverY : 0f;
            r.Go.transform.position = new Vector3(r.Pos.x, floorY - r.FeetY + hover, r.Pos.z);
            r.Go.transform.localRotation = Quaternion.Euler(0f, r.Facing, 0f);
        }

        /// <summary>某位遠端玩家頭頂在畫面上的位置(viewport 0..1),用來擺他的名字牌。看不到 → false。</summary>
        /// <summary>
        /// 拿某位遠端玩家的 avatar 與它的 root transform(頭貼相機要靠它算取景與朝向)。
        /// 回 false = 這個人現在沒有角色(旁觀者、或剛好在重建中)。
        /// </summary>
        public bool TryGetRemoteAvatar(int userId, out SdoAvatar av, out Transform root)
        {
            av = null; root = null;
            Remote r;
            if (!_remotes.TryGetValue(userId, out r) || r == null || r.Av == null || r.Go == null) return false;
            av = r.Av; root = r.Go.transform;
            return true;
        }

        /// <summary>
        /// 這個人的角色沿相機視線的**半厚度**(世界單位)—— 頭上泡的 depthBias 就是它。
        ///
        /// 為什麼需要:泡的錨點是**肩膀骨**,而頭髮/胸口比肩膀骨更靠近相機。泡的平面若剛好放在
        /// 肩膀骨的深度,說話者**自己**的頭髮就會把泡的尾巴切掉 —— 那看起來會像美術破圖,
        /// 完全指不到「深度差了 20 個單位」這個原因。把平面往相機拉開自己的半厚度就沒事了。
        ///
        /// 🔴 這個值一定要量出來,不可以寫死一個常數:寫太小 → 自己的頭髮切自己的泡;
        /// 寫太大 → 泡被推到所有人前面,擋人功能整個失效**而且不會報錯**。
        ///
        /// <paramref name="userId"/> 0 = 本機角色。回 0 表示量不到(呼叫端就當沒有 bias)。
        /// </summary>
        public float OwnerDepthExtent(int userId, Vector3 camFwd)
        {
            Transform root = userId == 0 ? _avatarRoot : null;
            if (userId != 0)
            {
                Remote r;
                if (_remotes.TryGetValue(userId, out r) && r != null && r.Go != null) root = r.Go.transform;
            }
            if (root == null) return 0f;

            var rends = root.GetComponentsInChildren<Renderer>();
            if (rends == null || rends.Length == 0) return 0f;
            bool any = false;
            var bounds = new Bounds();
            foreach (var rd in rends)
            {
                if (rd == null || !rd.enabled) continue;
                if (!any) { bounds = rd.bounds; any = true; }
                else bounds.Encapsulate(rd.bounds);
            }
            if (!any) return 0f;

            // 只取**水平**方向的厚度(相機視線投影到地面之後),刻意不算垂直項。
            // 理由:房間相機是俯視的,把整個身高(ey≈90)乘上 camFwd.y 會得到「一個人很厚」的假結果,
            // 泡就會被推得太靠近相機 → 擋人功能失效。真正會切到泡的是說話者的頭/髮,
            // 而那是水平方向的厚度(≈ 一個人的胸厚)。
            Vector3 fh = new Vector3(camFwd.x, 0f, camFwd.z);
            float fl = fh.magnitude;
            if (fl < 1e-4f) return 0f;   // 正上方俯視 → 沒有「前後」可言
            fh /= fl;
            Vector3 e = bounds.extents;
            return Mathf.Abs(e.x * fh.x) + Mathf.Abs(e.z * fh.z);
        }

        /// <summary>這個人現在有 3D 角色嗎?(旁觀者不在座位上 → 沒有角色可以掛泡/名字牌)</summary>
        public bool HasRemote(int userId)
        {
            Remote r;
            return _remotes.TryGetValue(userId, out r) && r != null && r.Go != null;
        }

        public bool TryRemoteHeadViewport(int userId, out Vector2 vp)
            => TryRemoteBoneViewport(userId, "Bip01_Head", "Bip01_Neck", headMarkerRise, out vp);

        /// <summary>某位遠端玩家**肩膀**在畫面上的位置 —— 頭上泡的錨點(泡身往上飄、尾巴指著肩膀)。</summary>
        public bool TryRemoteBubbleViewport(int userId, out Vector2 vp)
            => TryRemoteBoneViewport(userId, "Bip01_Neck", "Bip01_Head", 0f, out vp);

        /// <summary>某位遠端玩家聊天泡錨點的**世界座標**(同 <see cref="TryChatBubbleAnchorWorld"/> 的用途)。</summary>
        public bool TryRemoteChatBubbleAnchorWorld(int userId, out Vector3 world)
            => TryRemoteBoneWorld(userId, "Bip01_Neck", "Bip01_Head", 0f, out world);

        private bool TryRemoteBoneWorld(int userId, string bone, string fallbackBone, float rise, out Vector3 world)
        {
            world = default;
            Remote r;
            if (!_remotes.TryGetValue(userId, out r) || r == null || r.Av == null || r.Go == null) return false;
            Vector3 bm = r.Av.BoneModelPos(bone);
            if (bm == Vector3.zero) bm = r.Av.BoneModelPos(fallbackBone);
            if (bm == Vector3.zero) return false;
            world = r.Go.transform.TransformPoint(bm) + new Vector3(0f, rise, 0f);
            return true;
        }

        private bool TryRemoteBoneViewport(int userId, string bone, string fallbackBone, float rise, out Vector2 vp)
        {
            vp = default;
            Vector3 bw;
            if (_cam == null || !TryRemoteBoneWorld(userId, bone, fallbackBone, rise, out bw)) return false;
            Vector3 v = _cam.WorldToViewportPoint(bw);
            if (v.z <= 0f) return false;
            // 🔴 走出畫面的人要回 false:PlaceFollow 不夾邊界,vp 是負的就會把名字牌/泡畫到
            // 800×600 設計框外面(遠端會動之後才踩得到這個坑)。留一點餘裕讓邊緣不要閃。
            if (v.x < -0.1f || v.x > 1.1f || v.y < -0.1f || v.y > 1.1f) return false;
            vp = new Vector2(v.x, v.y);
            return true;
        }

        /// <summary>
        /// 讓某位遠端玩家播一段房間動作(聊天關鍵字觸發的那種)。播完自動回他自己的 idle。
        ///
        /// 與本機的 <see cref="PlayChatAction"/> 是同一件事,但**計時器必須是 per-avatar 的** ——
        /// 本機那顆用的是欄位 <c>_chatActionUntil</c>,如果遠端也共用它,兩個人同時說話就會互相打斷。
        /// </summary>
        public bool PlayRemoteChatAction(int userId, string motionRelPath)
        {
            Remote r;
            if (string.IsNullOrEmpty(motionRelPath) || !_remotes.TryGetValue(userId, out r)
                || r == null || r.Av == null) return false;

            var mot = LoadChatActionMot(motionRelPath);   // 快取共用(存 null 也存 → 載不到的路徑不會每次說話都重試)
            if (mot == null || mot.MaxTime <= 0f) return false;

            // 與本機 PlayChatAction 同一套:先回 idle 再疊 one-shot,時間長度用 clip 幀數 / fps 算。
            r.ClipIsWalk = false;
            if (r.Idle != null) r.Av.SetClip(r.Idle);
            r.Av.PlayOneShot(mot, false);
            r.ActionUntil = Time.time + (mot.MaxTime + 1f) / Mathf.Max(1f, r.Av.Fps);
            return true;
        }

        // 遠端動作播完 → 回他自己的 idle/walk。與位置插值同一個迴圈裡處理。
        private void TickRemoteChatAction(Remote r, float now)
        {
            if (r.ActionUntil <= 0f || now < r.ActionUntil) return;
            r.ActionUntil = -1f;
            if (r.Av != null)
            {
                r.Av.ClearOneShot();
                var clip = r.ClipIsWalk ? r.Walk : r.Idle;
                if (clip != null) r.Av.SetClip(clip);
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
            // 出生點依座位(與遠端共用 SpawnSpot);座位 0 就是官方那個 HostSpawn,離線行為不變。
            Vector3 spawn = SpawnSpot(_localSeat);
            _walkPos = new Vector3(spawn.x, floorY, spawn.z);
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
            // FeetYAt 是「把人擺到第 0 幀」量出來的 → 顯示姿勢現在停在第 0 幀。擺回迴圈當下那一格,
            // 否則接著真的換 clip 時(戴上飛行翅膀:地面 idle→flystay)混色會從第 0 幀起跳 = 抖一下。
            if (_avatar != null) _avatar.PoseFrame(_avatar.AutoLoopFrame);
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
            // 房間畫面要同時看到場景、遠端角色、以及頭上泡 —— 泡進來這台相機才吃得到深度測試(BubbleLayer)。
            _cam.cullingMask = (1 << SceneLayer) | (1 << RemoteAvatarLayer) | (1 << BubbleLayer);
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
            if (!_ready) return;
            // 🔴 遠端要在本機的守衛**之前**更新:本機 avatar 建失敗(缺檔/解析失敗)時
            // 整個房間的其他人不該一起凍在原地 —— 那會看起來像「連線斷了」。
            UpdateRemotes();
            if (_avatar == null) return;
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
                _hasWalked = true;   // 之後 SetLocalSeat 不再挪動我 —— 玩家自己走到的位置才是真相
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
