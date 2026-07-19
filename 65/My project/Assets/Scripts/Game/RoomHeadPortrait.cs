using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Renders one live 3D avatar HEAD into a RenderTexture for a room head-portrait slot — the same technique the
    /// result screen uses (ScreenGameplay BuildIdleHeadAvatar / UpdateHeadPortraitCam): an isolated idle avatar parked
    /// far off the room, on its own layer, framed head-on (3/4 yaw) by a dedicated camera with a transparent
    /// background. The camera targets the head bone's REST position so the idle head-bob plays inside the frame instead
    /// of being chased, and auto-frames off the FACE box raised to the measured hair-top (ComputeHeadBox) so the whole
    /// head fits, the hair is never cut, and a 長髮 mesh can't push the camera away (商城卡片同招:VisibleYBounds "FACE").
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

        private SdoAvatar _avatar;
        private Renderer[] _headRends;     // FACE + HAIR renderers (fallback: no FACE mesh → union of these)
        private Renderer[] _faceRends;     // FACE* only → 頭本體 = 取景基準 (換髮型不變)
        private Renderer[] _hairRends;     // HAIR* only → 只用「髮頂」決定鏡頭上下,不影響頭的大小
        private MotLoader _walkMot, _idleMot;
        private bool _mirrorWalking;
        private Camera _cam;
        private RenderTexture _rt;
        private Vector3 _headModelPos = new Vector3(0f, 50f, 0f);
        private float _hairOffsetModel = -1f;
        private float _chatActionUntil = -1f;   // 頭貼跟著房間 avatar 做聊天動作:此時間前不被 walk/idle 鏡射覆寫

        // ---- 凍結的取景(模型空間) ---------------------------------------------------------------------------------
        // 相機的目標點與距離只算「一次」(idle 第 0 幀的姿勢),之後不再跟著每幀的 renderer.bounds 跑。
        // 追活 bounds 會晃:人左右擺動時,臉的世界 AABB 高度與中心 z 每幀都在變 → dist/相機 z 跟著變 → 頭忽大忽小,
        // 看起來就是「人在前後晃」(使用者回報;動作本身只有左右)。凍結後,擺動就在框內自然演出(=官方作法)。
        private bool _framed;
        private Vector3 _aimModel;      // 取景目標(模型空間)
        private float _distModel;       // 相機距離(模型單位;世界距離 = × avatarScale)
        private Vector3 _rootModel0;    // 凍結當下的 root 骨位置 → 走路時的位移補償(只補平移,不補擺動)

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
            bool flying = SpecialMotionItems.WearsFlyingWing(avatarParts);
            _walkMot = SdoRoomAvatar.LoadMot(flying ? SpecialMotionItems.FlyWalkMot(male) : (male ? SdoRoomAvatar.MaleWalkMot : SdoRoomAvatar.WalkMot));
            _idleMot = SdoRoomAvatar.LoadMot(flying ? SpecialMotionItems.FlyIdleMot(male) : (male ? SdoRoomAvatar.MaleIdleMot : SdoRoomAvatar.IdleMot));

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

            // cache the head bone REST model-space position (fallback target when bounds aren't ready)
            Vector3 hp = _avatar.BoneModelPos("Bip01_Head");
            if (hp == Vector3.zero) hp = _avatar.BoneModelPos("Bip01_Neck");
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

        private void LateUpdate()
        {
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
                // 只補「root 骨的平移」(走路時整個人前進/浮沉),頭的擺動不補 → 擺動在框內演出,相機不追、不晃。
                Vector3 drift = _avatar.BoneModelPos(RootBone) - _rootModel0;
                target = t.TransformPoint(_aimModel + drift);
                dist = _distModel * Mathf.Max(0.01f, avatarScale);
            }
            else   // 姿勢/bounds 還沒好 → 退回頭骨 + 量到的髮頂
            {
                EnsureHairOffset();
                float h = _hairOffsetModel * Mathf.Max(0.01f, avatarScale);
                Vector3 restHead = t.TransformPoint(_headModelPos);
                target = restHead + new Vector3(0f, h > 0.001f ? 0.35f * h : 9f, 0f);
                dist = (h > 0.001f ? 1.9f * h : 28f) * Mathf.Max(0.05f, zoom);
            }
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

        // Measure (once) the hair-top height above the head bone from the posed avatar's renderer bounds (valid after
        // the first CPU skin). Scale-independent (model units) so the auto-frame captures the whole head regardless of
        // the model's unit scale.
        private void EnsureHairOffset()
        {
            if (_hairOffsetModel > 0f || _avatar == null) return;
            var rends = _avatar.GetComponentsInChildren<Renderer>();
            if (rends == null || rends.Length == 0) return;
            float top = float.NegativeInfinity; bool any = false;
            foreach (var r in rends)
            {
                if (r == null) continue;
                var b = r.bounds;
                if (b.size.sqrMagnitude < 1e-6f) continue;
                top = Mathf.Max(top, b.max.y); any = true;
            }
            if (!any) return;
            float headBoneY = _avatar.transform.TransformPoint(_headModelPos).y;
            float offW = top - headBoneY;
            if (offW <= 0.001f) return;
            _hairOffsetModel = offW / Mathf.Max(0.01f, avatarScale);
        }

        private void OnDestroy()
        {
            if (_cam != null) _cam.targetTexture = null;
            if (_rt != null) { _rt.Release(); Destroy(_rt); _rt = null; }
        }
    }
}
