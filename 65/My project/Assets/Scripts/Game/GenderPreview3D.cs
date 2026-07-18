using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Live full-body avatar preview for the standalone 男/女 select screen (GenderSelectScreen), faithful to the
    /// original LOBBYSEL AvtShow (Avatarmale/Avatarfemale): a real 3D dancer holding the lobby standby idle, framed
    /// head-to-toe. Both a female and a male avatar are built once (default WOMAN / MAN costume via SdoRoomAvatar) and
    /// parked off-stage on <see cref="PreviewLayer"/>; only the selected one is shown. A dedicated perspective camera
    /// renders that layer into a TRANSPARENT RenderTexture (<see cref="PreviewTexture"/>) which the screen shows as a
    /// RawImage over the LOBBYSEL art — same render-to-texture pattern as RoomScene3D / the result head portrait, so the
    /// dancer composites cleanly over the 2D frame. The screen masks <see cref="PreviewLayer"/> off the front-end UI
    /// camera while shown, and destroys this object on hide (it owns its own 3D lifecycle).
    /// </summary>
    public sealed class GenderPreview3D : MonoBehaviour
    {
        public const int PreviewLayer = 12;   // free user layer (RoomScene3D=4, head portrait=11); masked off the UI cam

        // ---- framing tunables (one visual calibration pass; sensible defaults frame a ~head-to-toe dancer) ----
        public float fieldOfView = 26f;        // vertical FOV; narrow-ish to keep a full body from fish-eyeing
        public float avatarYaw = -30f;         // DDRLOBBYSEL AvtShow writes yaw 0x2b4 = -30 degrees after loading.
        public float fillFrac = 0.68f;         // official AvtShow leaves more margin inside its 400x600 preview.
        public float framePadTop = 0.14f;      // extra headroom above the head BONE (hair sits above it)
        public float avatarYOffset = -5f;      // DDRLOBBYSEL AvtShow writes model position y = -5.
        public float verticalBias = 2f;        // shift the framing window up (+) / down (−) in model units
        public float nominalHeight = 55f;      // fallback body height if the head bone can't be read (model units)

        // off-stage park spot (own layer + own camera → no conflict with anything; a far spot is just tidy)
        private static readonly Vector3 Park = new Vector3(0f, 0f, 4000f);
        private const float PreviewMotBlendSec = 1f;
        private static readonly string[] MalePreviewMotPaths =
        {
            "MOTION/MREST0002_02.MOT",
            "MOTION/MREST0002_01.MOT",
        };
        private static readonly string[] FemalePreviewMotPaths =
        {
            "MOTION/WREST0013.MOT",
            "MOTION/WREST0016.MOT",
            "MOTION/WREST0011.MOT",
        };

        private Camera _cam;
        private RenderTexture _rt;
        private Transform _female, _male;
        private int _gender = -1;
        private MotLoader[] _femalePreviewMots, _malePreviewMots;
        private int _femaleMotIndex = -1, _maleMotIndex = -1;
        private float _femaleNextSwitch, _maleNextSwitch;

        /// <summary>The preview render — assign to the screen's RawImage. Null until Build succeeds.</summary>
        public Texture PreviewTexture => _rt;

        // 每個性別預覽要穿的實際部位 (由 UI 層從對應 profile 帶入；null → 用預設整套)。
        private string[] _femaleParts, _maleParts;
        // 每個性別對應 profile 自己的體型 (胖瘦) index 0..4 (由 UI 層帶入;選性別畫面就是角色本人,故用角色自己的身材)。
        private int _femaleBodyIndex, _maleBodyIndex;

        /// <summary>Build the camera, RT and both dancers, then show <paramref name="gender"/> (0=女,1=男). Optional
        /// <paramref name="femaleParts"/>/<paramref name="maleParts"/> = each gender's ACTUAL worn outfit (else default).
        /// <paramref name="femaleBody"/>/<paramref name="maleBody"/> = each profile's own 體型 (胖瘦) index (0=瘦)。</summary>
        public void Build(int gender, string[] femaleParts = null, string[] maleParts = null, int femaleBody = 0, int maleBody = 0)
        {
            _femaleParts = femaleParts; _maleParts = maleParts;
            _femaleBodyIndex = femaleBody; _maleBodyIndex = maleBody;
            BuildCamera();
            _femalePreviewMots = BuildPreviewMots(male: false);
            _malePreviewMots = BuildPreviewMots(male: true);
            _female = BuildAvatar(male: false, name: "GenderPreviewFemale");
            _male = BuildAvatar(male: true, name: "GenderPreviewMale");
            SetGender(gender);
        }

        /// <summary>Rebuild both dancers with new outfits (換裝後回到選性別畫面時刷新)；相機/RT 保留。</summary>
        public void SetOutfits(int gender, string[] femaleParts, string[] maleParts, int femaleBody = 0, int maleBody = 0)
        {
            _femaleParts = femaleParts; _maleParts = maleParts;
            _femaleBodyIndex = femaleBody; _maleBodyIndex = maleBody;
            // 換裝可能新增/移除飛行翅膀 → 重挑每個性別的預覽動作(穿飛行翅膀=flystay 浮空,否則隨機 idle)。
            _femalePreviewMots = BuildPreviewMots(male: false);
            _malePreviewMots = BuildPreviewMots(male: true);
            _femaleMotIndex = -1; _maleMotIndex = -1;
            if (_female != null) Destroy(_female.gameObject);
            if (_male != null) Destroy(_male.gameObject);
            _female = BuildAvatar(male: false, name: "GenderPreviewFemale");
            _male = BuildAvatar(male: true, name: "GenderPreviewMale");
            _gender = -1;   // 強制 SetGender 重新顯示/取景
            SetGender(gender);
        }

        /// <summary>Show the dancer for <paramref name="gender"/> (0=女,1=男) and re-frame the camera to it. No-op if unchanged.</summary>
        public void SetGender(int gender)
        {
            gender = gender == 1 ? 1 : 0;
            if (gender == _gender) return;
            _gender = gender;
            var show = gender == 1 ? _male : _female;
            var hide = gender == 1 ? _female : _male;
            if (hide != null) hide.gameObject.SetActive(false);
            if (show != null)
            {
                show.gameObject.SetActive(true);
                var av = show.GetComponent<SdoAvatar>();
                EnsureRandomMotion(av, gender == 1);
                FrameTo(av, show);
            }
        }

        private Transform BuildAvatar(bool male, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            // PreviewBody: full body but with the opaque-cutout portrait shader, so on the TRANSPARENT preview RT the
            // hair cutout doesn't punch see-through holes / write depth over the face (portraitOpaque:false did that).
            var parts = male ? _maleParts : _femaleParts;   // 實際穿戴 (null → 預設整套)
            int bodyIndex = male ? _maleBodyIndex : _femaleBodyIndex;   // 角色自己的體型 (胖瘦)
            var av = SdoRoomAvatar.Build(go, PreviewLayer, SdoRoomAvatar.RenderMode.PreviewBody, male: male, equippedParts: parts, bodyIndex: bodyIndex);
            if (av == null) { Destroy(go); return null; }
            av.DanceEnabled = () => false;   // no DPS in the select screen — hold the standby idle (which auto-loops)
            av.DanceTimeSec = () => -1f;
            av.BlendSec = PreviewMotBlendSec;
            ApplyRandomMotion(av, male, restart: true);
            // feet on y=0 at the park spot; yaw 0 faces the −Z camera (RoomMovement.FacingDegrees(2) = 0°)
            float feet = av.FeetYAt(0f);
            go.transform.position = new Vector3(Park.x, Park.y + avatarYOffset - feet, Park.z);
            go.transform.localRotation = Quaternion.Euler(0f, avatarYaw, 0f);
            go.SetActive(false);
            return go.transform;
        }

        private void Update()
        {
            TickRandomMotion(_female, male: false);
            TickRandomMotion(_male, male: true);
        }

        /// <summary>The preview clips for a gender: when the previewed outfit wears a 飛行翅膀 (fly wing), the single
        /// flystay 浮空 idle (rest cat 0x2c) held on loop — the select screen shows the character actually hovering
        /// (使用者需求 #1);otherwise the usual pool of random standby idles.</summary>
        private MotLoader[] BuildPreviewMots(bool male)
        {
            var parts = male ? _maleParts : _femaleParts;
            if (SpecialMotionItems.WearsFlyingWing(parts))
            {
                var fly = SdoRoomAvatar.LoadMot(SpecialMotionItems.FlyIdleMot(male));
                if (fly != null) return new[] { fly };   // 只 flystay,不隨機切換(單元素清單 → 循環同一支)
                Debug.LogWarning("[gender-preview] missing flystay MOT " + SpecialMotionItems.FlyIdleMot(male));
            }
            return LoadPreviewMots(male ? MalePreviewMotPaths : FemalePreviewMotPaths);
        }

        private static MotLoader[] LoadPreviewMots(string[] rels)
        {
            var clips = new List<MotLoader>(rels.Length);
            foreach (var rel in rels)
            {
                var clip = SdoRoomAvatar.LoadMot(rel);
                if (clip != null) clips.Add(clip);
                else Debug.LogWarning("[gender-preview] missing MOT " + rel);
            }
            return clips.ToArray();
        }

        private void EnsureRandomMotion(SdoAvatar av, bool male)
        {
            int current = male ? _maleMotIndex : _femaleMotIndex;
            float nextSwitch = male ? _maleNextSwitch : _femaleNextSwitch;
            if (current < 0 || Time.time >= nextSwitch) ApplyRandomMotion(av, male, restart: true);
        }

        private void TickRandomMotion(Transform root, bool male)
        {
            if (root == null || !root.gameObject.activeInHierarchy) return;
            float nextSwitch = male ? _maleNextSwitch : _femaleNextSwitch;
            if (Time.time < nextSwitch) return;
            ApplyRandomMotion(root.GetComponent<SdoAvatar>(), male, restart: false);
        }

        private void ApplyRandomMotion(SdoAvatar av, bool male, bool restart)
        {
            if (av == null) return;
            var clips = male ? _malePreviewMots : _femalePreviewMots;
            if (clips == null || clips.Length == 0) return;

            int current = male ? _maleMotIndex : _femaleMotIndex;
            int next = PickNextIndex(clips.Length, current);
            var clip = clips[next];

            if (male) _maleMotIndex = next;
            else _femaleMotIndex = next;

            av.RestMot = clip;
            av.SetClip(clip);
            av.PhaseOffsetSec = -Time.time;
            if (restart) av.PoseInitialIdle();

            float switchAt = Time.time + ClipDurationSec(clip, av);
            if (male) _maleNextSwitch = switchAt;
            else _femaleNextSwitch = switchAt;
        }

        private static int PickNextIndex(int count, int current)
        {
            if (count <= 1) return 0;
            if (current < 0 || current >= count) return Random.Range(0, count);
            int next = Random.Range(0, count - 1);
            return next >= current ? next + 1 : next;
        }

        private static float ClipDurationSec(MotLoader clip, SdoAvatar av)
        {
            if (clip == null || clip.MaxTime <= 0f) return 3f;
            float fps = av != null && av.Fps > 0f ? av.Fps : 30f;
            return Mathf.Max(0.5f, (clip.MaxTime + 1f) / fps);
        }

        private void BuildCamera()
        {
            int rtH = Mathf.Clamp(Screen.height, 600, 1600);
            int rtW = Mathf.RoundToInt(rtH * (2f / 3f));   // AvtShow is 400×600 → 2:3, so the dancer isn't stretched
            _rt = new RenderTexture(rtW, rtH, 24) { name = "genderPreviewRT", antiAliasing = 4, filterMode = FilterMode.Bilinear };
            var camGo = new GameObject("GenderPreviewCam") { layer = PreviewLayer };
            camGo.transform.SetParent(transform, false);
            _cam = camGo.AddComponent<Camera>();
            _cam.orthographic = false;
            _cam.fieldOfView = fieldOfView;
            _cam.nearClipPlane = 1f; _cam.farClipPlane = 4000f;
            _cam.cullingMask = 1 << PreviewLayer;
            _cam.targetTexture = _rt;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0f, 0f, 0f, 0f);   // transparent → only the dancer shows over the LOBBYSEL art
        }

        // Frame the dancer head-to-toe: feet rest on y=0 (set at build), the head bone (+ hair pad) is the top. Place a
        // level camera on −Z at a distance that fits the body height into fillFrac of the vertical FOV.
        private void FrameTo(SdoAvatar av, Transform root)
        {
            if (_cam == null || av == null || root == null) return;
            av.PoseFrame(0f);
            float headY = av.BoneModelPos("Bip01_Head").y;
            if (headY <= 0f) headY = av.BoneModelPos("Bip01_Neck").y;
            float feet = av.FeetYAt(0f);
            float bodyTop = (headY > 0f ? (headY - feet) : nominalHeight) * (1f + framePadTop);   // world Y of hair top (feet at 0)
            float viewH = Mathf.Max(bodyTop, 1f) / Mathf.Max(fillFrac, 0.1f);                      // vertical extent to frame
            float centerY = bodyTop * 0.5f + verticalBias;
            float dist = viewH * 0.5f / Mathf.Tan(fieldOfView * 0.5f * Mathf.Deg2Rad);
            var eye = new Vector3(Park.x, centerY, Park.z - dist);
            var look = new Vector3(Park.x, centerY, Park.z);
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
