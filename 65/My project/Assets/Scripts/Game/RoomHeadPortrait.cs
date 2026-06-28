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
        public int rtWidth = 192, rtHeight = 152;               // matches the ROOM AvatarView slot aspect (96:76 ≈ 1.263)

        private SdoAvatar _avatar;
        private Camera _cam;
        private RenderTexture _rt;
        private Vector3 _headModelPos = new Vector3(0f, 50f, 0f);
        private float _hairOffsetModel = -1f;

        /// <summary>The live head-portrait texture (null until Init succeeds). Assign to a RawImage.</summary>
        public Texture Texture => _rt;

        /// <summary>Build the isolated head avatar + camera + RT. Returns false if the avatar failed to load.</summary>
        public bool Init()
        {
            var parent = new GameObject("RoomHeadIdleAvatar");
            parent.transform.SetParent(transform, false);
            parent.transform.position = parkSpot;
            _avatar = SdoRoomAvatar.Build(parent, layer, portraitOpaque: true);
            if (_avatar == null) { Destroy(parent); return false; }
            _avatar.DanceEnabled = () => false;   // always hold the standby idle
            _avatar.DanceTimeSec = () => -1f;

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

            // cache the head bone REST model-space position (cam targets this, not the live bone, so it stays fixed)
            Vector3 hp = _avatar.BoneModelPos("Bip01_Head");
            if (hp == Vector3.zero) hp = _avatar.BoneModelPos("Bip01_Neck");
            if (hp != Vector3.zero) _headModelPos = hp;

            UpdateCam();
            return true;
        }

        private void LateUpdate()
        {
            if (_cam != null && _avatar != null) UpdateCam();
        }

        // FIXED head cam: targets the head bone REST pos; auto-frames from the measured hair-top. (Port of
        // ScreenGameplay.UpdateHeadPortraitCam, simplified — always auto-frame, frontal +Z pitched down.)
        private void UpdateCam()
        {
            var t = _avatar.transform;
            t.position = parkSpot;
            t.localScale = Vector3.one * Mathf.Max(0.01f, avatarScale);
            t.localRotation = Quaternion.Euler(0f, yaw, 0f);

            Vector3 restHead = t.TransformPoint(_headModelPos);
            EnsureHairOffset();
            float h = _hairOffsetModel * Mathf.Max(0.01f, avatarScale);   // hair-top height above the head bone (world)
            Vector3 target;
            float dist;
            if (h > 0.001f)
            {
                dist = 1.9f * h * Mathf.Max(0.05f, zoom);
                target = restHead + new Vector3(-2.1f, 0.35f * h, 0f);
            }
            else
            {
                dist = 28f;
                target = restHead + new Vector3(-2.1f, 9f, 0f);
            }
            _cam.fieldOfView = fov;
            Vector3 dir = Quaternion.Euler(pitchDeg, 0f, 0f) * Vector3.forward;   // +Z, pitched down
            _cam.transform.position = target - dir * dist;
            _cam.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
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
