using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Renders one live 3D avatar HEAD into a RenderTexture for a room head-portrait slot — the same technique the
    /// result screen uses (ScreenGameplay BuildIdleHeadAvatar / UpdateHeadPortraitCam): an isolated idle avatar parked
    /// far off the room, on its own layer, framed head-on (3/4 yaw) by a dedicated camera with a transparent
    /// background. The camera targets the head bone's REST position so the idle head-bob plays inside the frame instead
    /// of being chased, and auto-frames from the measured hair-top so the whole head fits with the hair never cut.
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
        public float headFrameDist = 1.9f;                       // cam distance = headFrameDist × head height (size)
        public float headAimUp = 0.11f;                          // shift aim down by this × head height → head sits centred
                                                                 // (the FACE+HAIR AABB centre is a bit below the visual centre)
        public int rtWidth = 192, rtHeight = 152;               // matches the ROOM AvatarView slot aspect (96:76 ≈ 1.263)

        /// <summary>Set by the host: returns true while the room avatar is walking, so the framed head mirrors it.</summary>
        public System.Func<bool> WalkingProvider;
        /// <summary>Set by the host: the room avatar's facing yaw (deg), so the framed head turns left/right with it.</summary>
        public System.Func<float> FacingProvider;

        private SdoAvatar _avatar;
        private Renderer[] _headRends;     // FACE + HAIR renderers → the head's VISUAL bounds (for true centering)
        private MotLoader _walkMot, _idleMot;
        private bool _mirrorWalking;
        private Camera _cam;
        private RenderTexture _rt;
        private Vector3 _headModelPos = new Vector3(0f, 50f, 0f);
        private float _hairOffsetModel = -1f;
        private float _chatActionUntil = -1f;   // 頭貼跟著房間 avatar 做聊天動作:此時間前不被 walk/idle 鏡射覆寫

        /// <summary>The live head-portrait texture (null until Init succeeds). Assign to a RawImage.</summary>
        public Texture Texture => _rt;

        /// <summary>Build the isolated head avatar + camera + RT. Returns false if the avatar failed to load.</summary>
        public bool Init(bool male = false, string[] avatarParts = null)
        {
            var parent = new GameObject("RoomHeadIdleAvatar");
            parent.transform.SetParent(transform, false);
            parent.transform.position = parkSpot;
            _avatar = SdoRoomAvatar.Build(parent, layer, portraitOpaque: true, male: male, equippedParts: avatarParts);
            if (_avatar == null) { Destroy(parent); return false; }
            _avatar.DanceEnabled = () => false;
            _avatar.DanceTimeSec = () => -1f;
            // mirror the room avatar's motion: same walk/idle clips, both loop on Time.time → the framed head matches
            // the avatar's live pose (官方頭像框跟著實際動作做動作).
            _walkMot = SdoRoomAvatar.LoadMot(male ? SdoRoomAvatar.MaleWalkMot : SdoRoomAvatar.WalkMot);
            _idleMot = SdoRoomAvatar.LoadMot(male ? SdoRoomAvatar.MaleIdleMot : SdoRoomAvatar.IdleMot);

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

            // collect the FACE + HAIR renderers (named "*_WOMAN_FACE_*" / "*_HAIR_*") → their combined world bounds is
            // the head's true visual centre, so the cam centres the HEAD (not the head BONE, which is off-centre).
            var all = _avatar.GetComponentsInChildren<Renderer>();
            var list = new System.Collections.Generic.List<Renderer>();
            foreach (var r in all)
            {
                if (r == null) continue;
                string n = r.gameObject.name.ToUpperInvariant();
                if (n.Contains("FACE") || n.Contains("HAIR")) list.Add(r);
            }
            _headRends = list.ToArray();

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

        // Head cam: aim at the head's VISUAL bounds CENTRE (face+hair) so the head is truly centred in the RT — both
        // horizontally and vertically — regardless of the head-bone offset, the motion (walk root drift) or the facing
        // yaw. Size = headFrameDist × head height. Yaws the avatar to mirror the room avatar's facing.
        private void UpdateCam()
        {
            var t = _avatar.transform;
            t.position = parkSpot;
            t.localScale = Vector3.one * Mathf.Max(0.01f, avatarScale);
            float facing = FacingProvider != null ? FacingProvider() : 0f;
            t.localRotation = Quaternion.Euler(0f, yaw + facing, 0f);

            Vector3 target; float dist;
            if (TryHeadBounds(out var b))
            {
                target = b.center;                                    // head visual centre → centred X/Z
                target.y -= headAimUp * b.size.y;                     // nudge so the FACE (not the neck-biased AABB) centres
                dist = Mathf.Max(b.size.y, 1f) * headFrameDist * Mathf.Max(0.05f, zoom);
            }
            else   // bounds not ready yet → fall back to the head bone + measured hair-top
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

        // Combined world AABB of the head (FACE+HAIR) renderers, after the avatar has been CPU-skinned this frame. False
        // until the meshes have valid bounds (first pose).
        private bool TryHeadBounds(out Bounds b)
        {
            b = default; bool any = false;
            if (_headRends != null)
                foreach (var r in _headRends)
                {
                    if (r == null) continue;
                    var rb = r.bounds;
                    if (rb.size.sqrMagnitude < 1e-6f) continue;
                    if (!any) { b = rb; any = true; } else b.Encapsulate(rb);
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
