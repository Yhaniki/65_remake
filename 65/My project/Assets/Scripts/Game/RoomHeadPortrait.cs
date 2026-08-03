using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Renders one live 3D avatar HEAD into a RenderTexture for a room head-portrait slot — the same technique the
    /// result screen uses (ScreenGameplay BuildIdleHeadAvatar / UpdateHeadPortraitCam): an isolated idle avatar parked
    /// far off the room, on its own layer, framed head-on (3/4 yaw) by a dedicated camera with a transparent
    /// background. The frame is measured ONCE off the FACE box (ComputeFraming) so 換髮型/戴帽子頭都恆等大;after that the
    /// camera keeps its distance to the head through <see cref="HeadPortraitAimFilter"/>: 待機/走路的小擺動落在死區內 →
    /// 相機一動也不動,擺動在框內演出;飛行滑翔那種「一直偏同一邊」的大幅前傾會被慢爬吃掉 → 距離守住,頭不會爆大。
    /// Owns its avatar + camera + RT; release via OnDestroy (the host destroys this GameObject).
    /// </summary>
    public sealed class RoomHeadPortrait : MonoBehaviour
    {
        public int layer = 11;                                  // dedicated portrait layer (head cam renders ONLY this)
        public Vector3 parkSpot = new Vector3(5200f, 0f, 5200f); // far from the room scene, isolated for the head cam
        public float fov = 45f;                                  // 官方 fovY = π/4
        public float pitchDeg = 0f;                              // 正視: level, no down-pitch (room AvatarView is front-on)
        public float yaw = 0f;                                   // 正視: front-facing (NOT the result screen's 30° 3/4)
        public float avatarScale = 1.05f;
        public float zoom = 1f;                                  // auto-frame fine multiplier (>1 = zoom out)
        public float headFrameDist = 1.9f;                       // cam distance = headFrameDist × 框高(只由臉決定)
        public float headAimUp = 0.11f;                          // shift aim down by this × 框高 → head sits centred
        /// <summary>true = 蓬鬆髮/髮飾頂出框時,把鏡頭往上挪(頭大小仍不變,但構圖會隨髮型上下移)。
        /// 預設 false:使用者要「完全不看頭髮」——大小與位置固定,超出框的髮頂就讓它被切掉。</summary>
        public bool fitHairTop = false;
        public int rtWidth = 192, rtHeight = 152;               // matches the ROOM AvatarView slot aspect (96:76 ≈ 1.263)

        /// <summary>Set by the host: returns true while the room avatar is walking, so the framed head mirrors it.</summary>
        public System.Func<bool> WalkingProvider;
        /// <summary>Set by the host: the room avatar's facing yaw (deg), so the framed head turns left/right with it.</summary>
        public System.Func<float> FacingProvider;

        // ---- 取景框(以「臉」為單位) -------------------------------------------------------------------------------
        // 臉 mesh (900007/900001) 每套裝扮都一樣 → 拿它定框,換髮型頭就恆等大、位置也不動(使用者:「不看頭髮」)。
        // 框 = 下巴 → 顱頂再往上 BoxTopPadFaces 個臉高。0.216 是校正值:逐字取自「男生預設頭貼」那個框(使用者說那個
        // 大小/位置剛好) —— 實測男生 臉 58.05..68.81(臉高 10.76)、髮頂 71.13 → (71.13-68.81)/10.76 = 0.216。女生用
        // 同一個比例(臉高 10.45)自動對齊同樣的觀感。頭髮完全不參與框的大小。
        public const float BoxBottomPadFaces = 0f;
        public const float BoxTopPadFaces    = 0.216f;
        /// <summary>髮頂/下巴離畫面邊緣至少留這麼多(臉高單位)。</summary>
        public const float FitMarginFaces = 0.05f;

        // ---- 跟頭(帶死區的低通) ---------------------------------------------------------------------------------
        /// <summary>相機跟頭的死區半徑(臉高單位)。頭離基準姿勢在這個範圍內 → 相機一動也不動(擺動在框內演出);
        /// 超出才跟,而且只跟超出的那一段。見 <see cref="HeadPortraitAimFilter"/>。</summary>
        public float aimDeadZoneFaces = HeadPortraitAimFilter.DefaultDeadZoneFaces;
        /// <summary>跟頭的指數平滑時間常數(秒)。</summary>
        public float aimSmoothSec = HeadPortraitAimFilter.DefaultSmoothSec;
        /// <summary>慢爬的時間常數(秒)→ 死區留下的殘餘距離最後也會補回來。</summary>
        public float aimCreepSec = HeadPortraitAimFilter.DefaultCreepSec;

        private SdoAvatar _avatar;
        private Renderer[] _headRends;     // FACE + HAIR renderers (fallback: no FACE mesh → union of these)
        private Renderer[] _faceRends;     // FACE* only → 頭本體 = 取景基準 (換髮型不變)
        private Renderer[] _hairRends;     // HAIR* only → 只用「髮頂」決定鏡頭上下,不影響頭的大小
        private MotLoader _walkMot, _idleMot;
        private bool _mirrorWalking;
        private bool _male;          // 重挑鏡射 clip 要用(飛行↔地面切換,見 SetSpectating)
        private bool _wingFlying;    // 這身穿搭有飛行翅膀嗎(換裝才會變)
        private bool _flyClips;      // 現在鏡射的是不是飛行 clip(= _wingFlying 且不在旁觀席)
        private Camera _cam;
        private RenderTexture _rt;
        private Vector3 _headModelPos = new Vector3(0f, 50f, 0f);
        private float _chatActionUntil = -1f;   // 頭貼跟著房間 avatar 做聊天動作:此時間前不被 walk/idle 鏡射覆寫

        // ---- 凍結的取景(模型空間) ---------------------------------------------------------------------------------
        // 相機的目標點與距離只算「一次」(idle 第 0 幀的姿勢),之後不再跟著每幀的 renderer.bounds 跑。
        // 追活 bounds 會晃:人左右擺動時,臉的世界 AABB 高度與中心 z 每幀都在變 → dist/相機 z 跟著變 → 頭忽大忽小,
        // 看起來就是「人在前後晃」(使用者回報;動作本身只有左右)。凍結後,擺動就在框內自然演出(=官方作法)。
        private bool _framed;
        private Vector3 _aimModel;      // 取景目標(模型空間)
        private float _distModel;       // 相機距離(模型單位;世界距離 = × avatarScale)
        private Vector3 _rootModel0;    // 凍結當下的 root 骨位置 → 走路時的位移補償(只補平移,不補擺動)
        private Vector3 _headRelRoot0;  // 凍結當下「頭骨相對 root」的位置 → 姿勢把頭挪走多少,就是下面要跟的量
        private float _faceHeightModel; // 凍結當下的臉高(模型單位)→ 死區的尺標
        private Vector3 _aimFollow;     // 濾波後的「跟頭」補償(模型空間;死區內恆為上一幀的值)
        private string _headBone = "Bip01_Head";

        /// <summary>The live head-portrait texture (null until Init succeeds). Assign to a RawImage.</summary>
        public Texture Texture => _rt;

        /// <summary>Build the isolated head avatar + camera + RT. Returns false if the avatar failed to load.
        /// <paramref name="bodyIndex"/> = 這個角色自己的體型 (胖瘦;跟房間全身 avatar 同一個值,頭貼才一致)。</summary>
        public bool Init(bool male = false, string[] avatarParts = null, int bodyIndex = 0)
        {
            var parent = new GameObject("RoomHeadIdleAvatar");
            parent.transform.SetParent(transform, false);
            parent.transform.position = parkSpot;
            _avatar = SdoRoomAvatar.Build(parent, layer, portraitOpaque: true, male: male, equippedParts: avatarParts, bodyIndex: bodyIndex);
            if (_avatar == null) { Destroy(parent); return false; }
            _avatar.DanceEnabled = () => false;
            _avatar.DanceTimeSec = () => -1f;
            // mirror the room avatar's motion: same walk/idle clips, both loop on Time.time → the framed head matches
            // the avatar's live pose (官方頭像框跟著實際動作做動作). 穿飛行翅膀時比照房間 avatar 用 flystay 浮空 idle /
            // fly 前傾滑動,頭貼才跟著一樣做飛行動作 (使用者需求 #3;SpecialMotionItems 同一條規則)。
            _male = male;
            _wingFlying = SpecialMotionItems.WearsFlyingWing(avatarParts);
            _flyClips = _wingFlying;   // 站上旁觀席時由 SetSpectating 關掉(全身也不飛,見 RoomScene3D.FlyingAt)
            LoadMirrorClips();

            _rt = new RenderTexture(rtWidth, rtHeight, 16, RenderTextureFormat.ARGB32) { name = "RoomHeadPortraitRT" };
            var camGo = new GameObject("RoomHeadPortraitCam");
            camGo.transform.SetParent(transform, false);
            _cam = camGo.AddComponent<Camera>();
            _cam.orthographic = false;
            _cam.fieldOfView = fov;
            _cam.nearClipPlane = 0.5f; _cam.farClipPlane = 500f;
            _cam.cullingMask = 1 << layer;     // ONLY the isolated head avatar
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0f, 0f, 0f, 0f);   // transparent → the UI/scene shows through
            _cam.targetTexture = _rt;
            _cam.depth = -20;

            // cache the head bone REST model-space position (fallback target when bounds aren't ready) and remember WHICH
            // bone answered — UpdateCam reads that same bone live each frame to follow a leaning pose (see _aimFollow).
            Vector3 hp = _avatar.BoneModelPos("Bip01_Head");
            if (hp == Vector3.zero) { hp = _avatar.BoneModelPos("Bip01_Neck"); if (hp != Vector3.zero) _headBone = "Bip01_Neck"; }
            if (hp != Vector3.zero) _headModelPos = hp;

            // collect the FACE renderers (the head proper — same mesh for every costume) and the HAIR renderers
            // separately: the frame is anchored on the FACE box and only the HAIR *TOP* pushes it up (see
            // ComputeHeadBox). Taking the FACE+HAIR union instead would let a 長髮 mesh (hair falling to the chest)
            // triple the box height → cam distance → 頭貼變小.
            var all = _avatar.GetComponentsInChildren<Renderer>();
            var head = new System.Collections.Generic.List<Renderer>();
            var face = new System.Collections.Generic.List<Renderer>();
            var hair = new System.Collections.Generic.List<Renderer>();
            foreach (var r in all)
            {
                if (r == null) continue;
                string n = r.gameObject.name.ToUpperInvariant();
                bool isFace = n.Contains("FACE"), isHair = n.Contains("HAIR");
                if (!isFace && !isHair) continue;
                head.Add(r);
                (isFace ? face : hair).Add(r);
            }
            _headRends = head.ToArray();
            _faceRends = face.ToArray();
            _hairRends = hair.ToArray();

            UpdateCam();
            return true;
        }

        private void LoadMirrorClips()
        {
            _walkMot = SdoRoomAvatar.LoadMot(_flyClips ? SpecialMotionItems.FlyWalkMot(_male)
                                                       : (_male ? SdoRoomAvatar.MaleWalkMot : SdoRoomAvatar.WalkMot));
            _idleMot = SdoRoomAvatar.LoadMot(_flyClips ? SpecialMotionItems.FlyIdleMot(_male)
                                                       : (_male ? SdoRoomAvatar.MaleIdleMot : SdoRoomAvatar.IdleMot));
        }

        /// <summary>本機是不是站在旁觀席上。旁觀時**頭貼也不做飛行動作** —— 全身那邊已經把整組飛行特性
        /// 關掉、改回地面的看戲姿勢(<see cref="RoomScene3D.FlyingAt"/>),頭貼再演 flystay 就變成
        /// 「頭在飛、身體在看戲」。沒穿飛行翅膀的人本來就是地面 clip → 這裡是 no-op。
        /// 換過去是**硬切**,與房間 avatar 同一條規則(旁觀動作不用平滑過場)。</summary>
        public void SetSpectating(bool spectating)
        {
            bool fly = _wingFlying && !spectating;
            if (fly == _flyClips) return;
            _flyClips = fly;
            LoadMirrorClips();
            if (_avatar == null || _idleMot == null || _mirrorWalking || _chatActionUntil > 0f) return;
            _avatar.SnapNextClip();
            _avatar.SetClip(_idleMot);
        }

        /// <summary>Mirror a room-chat action on the framed head — play the SAME one-shot motion the room avatar plays
        /// (see RoomScene3D.PlayChatAction), so the 頭貼 做動作 too when the local player types a keyword. LateUpdate
        /// holds off the walk/idle mirror until the action finishes, then re-syncs to the avatar's current pose.</summary>
        public bool PlayChatAction(string motionRelPath)
        {
            if (_avatar == null || string.IsNullOrEmpty(motionRelPath)) return false;
            var mot = SdoRoomAvatar.LoadMot(motionRelPath);
            if (mot == null || mot.MaxTime <= 0f) return false;
            _avatar.SetClip(_idleMot);
            _avatar.PlayOneShot(mot, false);
            _chatActionUntil = Time.time + (mot.MaxTime + 1f) / Mathf.Max(1f, _avatar.Fps);
            return true;
        }

        /// <summary>The room avatar this portrait belongs to. When set, the portrait stops running its own walk/idle
        /// and one-shot clocks and simply shows whatever pose the body is in this frame — 官方的房間頭貼畫的就是本體,
        /// so head and body can never drift apart (they used to: the walk/idle mirror lagged a frame, and the chat
        /// one-shot plus each clip switch's crossfade were timed independently on both avatars).</summary>
        /// A provider (not a plain reference) because the room rebuilds its local avatar on every costume change —
        /// resolving it per frame means the portrait always follows the CURRENT body.
        public System.Func<SdoAvatar> MirrorSourceProvider;

        private void LateUpdate()
        {
            var body = MirrorSourceProvider != null ? MirrorSourceProvider() : null;
            if (_avatar != null && body != null && body.CurrentClip != null)
            {
                // Same clip + same frame => same pose. The body's LateUpdate may run after ours, in which case this is
                // the body's previous frame; that is one frame of lag on a breathing loop, i.e. invisible - and unlike
                // the old mirror it cannot accumulate drift.
                if (_chatActionUntil > 0f) { _avatar.ClearOneShot(); _chatActionUntil = -1f; }
                _avatar.SetClip(body.CurrentClip);
                _avatar.FrameOverride = body.CurrentPoseTime;
                // keep the fallback path's latch in step, so if the body ever goes away mid-session the walk/idle
                // mirror resumes with the right clip instead of the one it happened to hold before mirroring started
                if (WalkingProvider != null) _mirrorWalking = WalkingProvider();
                if (_cam != null) UpdateCam();
                return;
            }
            // Not mirroring (no body yet, or it went away): release the frame lock, otherwise the portrait would
            // stay frozen on the last mirrored frame forever.
            if (_avatar != null && _avatar.FrameOverride >= 0f) _avatar.FrameOverride = -1f;
            if (_avatar != null && WalkingProvider != null)
            {
                bool walking = WalkingProvider();
                // 聊天動作結束(計時到)或玩家開始走動(房間 avatar 已中斷動作) → 清掉 one-shot 循環,回走路/idle。
                // 關鍵:PlayOneShot(mot,false) 的 one-shot 優先級最高且會「無限循環」(f % MaxTime),只有 ClearOneShot 能停;
                // 先前只 SetClip 沒清 one-shot → 頭貼卡在 hi 動作一直循環(mirror RoomScene3D.Update 的 ClearOneShot)。
                if (_chatActionUntil > 0f && (walking || Time.time >= _chatActionUntil))
                {
                    _avatar.ClearOneShot();
                    _chatActionUntil = -1f;
                    _mirrorWalking = !walking;   // 強制下面重套正確的 clip
                }
                if (_chatActionUntil <= 0f && walking != _mirrorWalking)   // 非動作中 → 鏡射 avatar 的走路/idle
                {
                    _mirrorWalking = walking;
                    _avatar.SetClip(walking ? _walkMot : _idleMot);
                }
            }
            if (_cam != null && _avatar != null) UpdateCam();
        }

        // Head cam: size + position come from the FACE alone (ComputeFraming) → 換什麼髮型頭都一樣大、一樣位置。
        // Yaws the avatar to mirror the room avatar's facing.
        private void UpdateCam()
        {
            var t = _avatar.transform;
            t.position = parkSpot;
            t.localScale = Vector3.one * Mathf.Max(0.01f, avatarScale);
            float facing = FacingProvider != null ? FacingProvider() : 0f;
            t.localRotation = Quaternion.Euler(0f, yaw + facing, 0f);

            if (!_framed) TryFreezeFraming();

            Vector3 target; float dist;
            if (_framed)
            {
                // (1) root 骨的平移(走路時整個人前進/浮沉)照補,一格不差。
                Vector3 root = _avatar.BoneModelPos(RootBone);
                Vector3 drift = root - _rootModel0;
                // (2) 姿勢把頭挪離基準多少 —— 過濾之後才補。純凍結(只補 (1))時,身體大幅前傾的姿勢會讓頭朝相機
                //     衝過去、相機到頭的距離被吃掉 → 頭爆大:飛行滑翔 (FLY_NV/FLY_NAN) 就是這樣,前傾「持續」把頭往
                //     相機挪 11 個模型單位(相機距離才 ~25)。反過來每幀直接追頭又會被待機/走路的小擺動帶著抖。
                //     所以交給 HeadPortraitAimFilter:死區擋掉擺動(實測待機 1.39 / 走路 1.61,死區 3.14),
                //     慢爬把「一直偏同一邊」的前傾整個吃掉(實測殘餘 0.17)。
                Vector3 headRel = _avatar.BoneModelPos(_headBone) - root;
                _aimFollow = HeadPortraitAimFilter.Step(_aimFollow, headRel - _headRelRoot0,
                                                        aimDeadZoneFaces * _faceHeightModel,
                                                        aimSmoothSec, aimCreepSec, Time.deltaTime);
                target = t.TransformPoint(_aimModel + drift + _aimFollow);
                dist = _distModel * Mathf.Max(0.01f, avatarScale);
            }
            else   // 臉的 bounds 還沒好 → 退回「只對頭骨」的固定取景(HeadBoneFraming;不量任何 mesh)
                HeadBoneFraming.Compute(t.TransformPoint(_headModelPos), avatarScale, zoom,
                                        new Vector3(0f, HeadBoneFraming.AimUpModel, 0f), out target, out dist);
            _cam.fieldOfView = fov;
            Vector3 dir = Quaternion.Euler(pitchDeg, 0f, 0f) * Vector3.forward;   // +Z, (optionally pitched down)
            _cam.transform.position = target - dir * dist;
            _cam.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
        }

        private const string RootBone = "Bip01";

        // 凍結取景:用 idle 第 0 幀的姿勢量一次臉框(模型空間),算出目標點/距離就定案。mesh.bounds 就是模型空間的框
        // (各 part 掛在 avatar 底下、local transform 是 identity),所以量到的值與 avatar 的 yaw/park 位置無關。
        private void TryFreezeFraming()
        {
            if (_avatar == null) return;
            if (_idleMot != null) { _avatar.SetClip(_idleMot); _avatar.PoseFrame(0f); }   // 每次都從同一個姿勢量 → 可重現
            if (!TryMeshUnion(_faceRends, out var face) || face.size.y <= 1e-4f) return;  // 還沒蒙皮好 → 下一幀再試
            bool hairFound = TryMeshUnion(_hairRends, out var hair);
            float hairTop = hairFound ? hair.max.y : 0f;
            ComputeFraming(face, hairFound && fitHairTop, hairTop,
                           headFrameDist, zoom, headAimUp, fov, out _aimModel, out _distModel);
            _rootModel0 = _avatar.BoneModelPos(RootBone);
            _headRelRoot0 = _avatar.BoneModelPos(_headBone) - _rootModel0;   // 跟頭的基準姿勢 = 量框的這個姿勢
            _faceHeightModel = Mathf.Max(face.size.y, 1e-4f);                // 死區的尺標(臉高單位 → 模型單位)
            _aimFollow = Vector3.zero;
            _framed = true;
            LogFramingOnce(face, hairFound, hairTop, _aimModel, _distModel);   // 髮頂照印(診斷用),即使不參與取景
        }

        /// <summary>純幾何:算出頭貼相機的目標點與距離。
        ///
        /// 頭的「大小」只看臉(face):臉 mesh 每套裝扮都一樣,所以換髮型頭恆等大、位置也不動 —— 這是使用者要的
        /// 「不看頭髮」。頭髮只有一件事可做:當它蓬到會被切掉時,把鏡頭「往上挪」(下巴下面本來就有餘裕),頭一樣大,
        /// 只是整體構圖上移;挪到下巴要出框了還塞不下,才退遠(上限 MaxZoomOut)。fitHairTop=false 就完全不理頭髮。
        ///
        /// 舊版拿 FACE∪HAIR 的 AABB 當框 → 框高 = 相機距離。實測(執行期 renderer.bounds):預設髮 900017 貼著頭皮
        /// (髮頂 64.36,顱頂 63.67)→框高 11.13;037916 髮蓬到 69.23 →框高 16.00 → 相機遠了 1.44 倍、頭縮小。
        /// (更早之前連垂到胸口的髮尾也算進框,遠到 2 倍以上。)</summary>
        public static void ComputeFraming(Bounds face, bool hasHair, float hairTopY,
                                          float frameDist, float zoom, float aimUp, float fovDeg,
                                          out Vector3 target, out float dist)
        {
            float faceH = Mathf.Max(face.size.y, 1e-4f);
            float bottom = face.min.y - BoxBottomPadFaces * faceH;
            float top = face.max.y + BoxTopPadFaces * faceH;
            float boxH = top - bottom;
            dist = boxH * Mathf.Max(0.01f, frameDist) * Mathf.Max(0.05f, zoom);   // ← 只跟臉有關
            target = new Vector3(face.center.x, (bottom + top) * 0.5f - aimUp * boxH, face.center.z);
            if (!hasHair) return;

            float margin = FitMarginFaces * faceH;
            float half = dist * Mathf.Tan(fovDeg * 0.5f * Mathf.Deg2Rad);   // 相機在 dist 處看得到的「半個畫面高」
            float lo = hairTopY + margin - half;               // target 至少要這麼高,髮頂才在框內
            float hi = face.min.y - margin + half;             // 再高就切到下巴
            // 頭永遠不縮小(使用者:「不看頭髮」) → 只在「下巴到髮頂」塞得進畫面時才往上挪。塞不下的極端髮型
            // (>1 個臉高的高髮髻/大帽子,或離群頂點/飄帶把髮頂算到天上)一律維持基準構圖,髮頂讓它被切。
            if (lo <= hi) target.y = Mathf.Clamp(target.y, lo, hi);
        }

        // 一次性診斷:把每個頭部 part 的實際 mesh 框(模型空間) + 凍結下來的相機目標/距離寫進 log.txt。
        // 換裝後頭貼被拉遠的回報都靠這行定位 —— 離線量 MSH 的 raw 頂點會騙人(預設髮 900017 是 scaled bone-local
        // 綁定,raw 頂點根本不是模型座標),只有這行 log 講真話。
        private void LogFramingOnce(Bounds face, bool hasHair, float hairTopY, Vector3 aimModel, float distModel)
        {
            if (_loggedFraming) return;
            _loggedFraming = true;
            var sb = new System.Text.StringBuilder();
            sb.Append("face[").Append(face.min.y.ToString("F2")).Append("..").Append(face.max.y.ToString("F2"))
              .Append("] faceH=").Append(face.size.y.ToString("F2")).Append(' ')
              .Append("hairTop=").Append(hasHair ? hairTopY.ToString("F2") : "(none)")
              .Append(" aimY=").Append(aimModel.y.ToString("F2"))
              .Append(" dist=").Append(distModel.ToString("F2"));
            if (_headRends != null)
                foreach (var r in _headRends)
                {
                    if (r == null) continue;
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    var mb = mf.sharedMesh.bounds;
                    sb.Append("\n    ").Append(r.gameObject.name)
                      .Append(" y[").Append(mb.min.y.ToString("F2")).Append("..").Append(mb.max.y.ToString("F2"))
                      .Append("] x[").Append(mb.min.x.ToString("F2")).Append("..").Append(mb.max.x.ToString("F2")).Append(']');
                }
            SdoLog.Note("headframe", sb.ToString());
        }

        private bool _loggedFraming;

        // 模型空間的合併框:各 part 掛在 avatar 底下且 local transform 是 identity → mesh.bounds 就是模型空間的框。
        // (不能用 renderer.bounds:那是世界 AABB,會被 avatar 的 yaw/park 位置汙染,而且每幀隨姿勢變 → 就是晃的來源。)
        private static bool TryMeshUnion(Renderer[] rends, out Bounds b)
        {
            b = default; bool any = false;
            if (rends != null)
                foreach (var r in rends)
                {
                    if (r == null) continue;
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    var mb = mf.sharedMesh.bounds;
                    if (mb.size.sqrMagnitude < 1e-6f) continue;
                    if (!any) { b = mb; any = true; } else b.Encapsulate(mb);
                }
            return any;
        }

        private void OnDestroy()
        {
            if (_cam != null) _cam.targetTexture = null;
            if (_rt != null) { _rt.Release(); Destroy(_rt); _rt = null; }
        }
    }
}
