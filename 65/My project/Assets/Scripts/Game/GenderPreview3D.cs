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
        // DDRLOBBYSEL's AvtShow slot is 400×600 within the logical 800×600 frame → 2:3. Drives both the RT size and the
        // pinned camera aspect (see BuildCamera / RtSizing).
        public const float SlotW = 400f, SlotH = 600f;
        public float previewSupersample = RtSizing.DefaultSupersample;   // set to 1 for window-native resolution

        // off-stage park spot (own layer + own camera → no conflict with anything; a far spot is just tidy)
        //
        // 🔴 **每個實例要停在自己的格子上。** 這裡曾經是一個 static 的定點,但相機的 cullingMask 只有
        //    PreviewLayer —— 兩個 GenderPreview3D 同時活著時(大廳那尊 + 個人資料視窗那尊就是這種情況),
        //    兩尊角色會疊在同一個座標,而**兩台相機都拍得到它們**,結果兩張 RT 裡都是兩個人疊在一起
        //    (各自播各自的 idle,看起來像鬼影)。改成開機時各自認領一格、相隔 <see cref="ParkStride"/>:
        //    取景距離約 225、水平半視野 ≈ 35,間隔 500 遠遠超過,誰也照不到誰。
        private static readonly Vector3 ParkBase = new Vector3(0f, 0f, 4000f);
        private const float ParkStride = 500f;
        private static readonly HashSet<int> UsedParkSlots = new HashSet<int>();
        private int _parkSlot = -1;
        private Vector3 _park = ParkBase;
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

        // ---- 拖曳轉身 / 抬頭(官方 AvtShow_ApplyDragRotateZoom) ----
        //
        // 參數與數學**逐字取自商城那份已經校好的實作**(ShopScreen.OnPreviewDrag / ApplyPreviewRotation),
        // 那邊的出處註解記著:線上 sdo.bin FUN_0044f900 —— yaw −= dx×0.4、pitch −= dy×0.4 並 clamp[-30,15];
        // 離線版只有 yaw。旋轉要建成 <c>Q = AngleAxis(pitch, 世界X) · AngleAxis(yaw, 世界Y)</c>,
        // **不是** <c>Quaternion.Euler(pitch, yaw, 0)</c> —— 後者是繞頭部朝向的局部軸點頭,轉身之後抬頭會歪掉。
        //
        // 🔴 繞的是**身體中心**(PivotY,腰的高度),不是腳底:繞腳底的話抬 pitch 會變成整個人以腳為軸大幅甩動。
        //
        // (商城那邊維持原樣沒動 —— 那是已經校好的畫面。之後若要收斂成一份,把這裡與 ShopScreen 的
        //  _dragAngle/_pitchAngle 一起抽成共用元件即可,兩邊的常數本來就是同一組。)
        public const float DragDegPerPixel = 0.4f;
        public const float PitchMin = -30f, PitchMax = 15f;
        private const float OrbitPivotY = 30f;

        private float _dragYaw, _dragPitch;
        private float _feetOffsetY;   // BuildAvatar 當下量到的落地位移(轉身時要用它還原基準位置)

        private Camera _cam;
        private RenderTexture _rt;
        private RtResizeTracker _rtTrack;   // debounced window-resize → RT re-allocation (see MaintainRt)
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
            TakeParkSlot();   // 🔴 一定要在 BuildCamera / BuildAvatar 之前:兩者都要用 _park 定位
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

        /// <summary>即時改某性別預覽角色的體型（胖瘦 index 0..4），**不重建模型** —— 開場設定面板的體型滑桿拖曳中
        /// 每幀都會叫，走 <see cref="SetOutfits"/> 的話每一幀都要 Destroy+重建兩具角色。</summary>
        public void SetBodyShape(int gender, int index)
        {
            bool male = gender == 1;
            index = Mathf.Clamp(index, 0, 4);
            if (male) _maleBodyIndex = index; else _femaleBodyIndex = index;
            var t = male ? _male : _female;
            var av = t != null ? t.GetComponent<SdoAvatar>() : null;
            if (av != null) av.SetBodyShape(SdoBodyShape.WeightFromIndex(index, male));
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
                FrameTo(av, show, gender == 1);
            }
        }

        /// <summary>
        /// 用**現在**的取景參數(<see cref="fillFrac"/> / <see cref="framePadTop"/> / <see cref="verticalBias"/> /
        /// <see cref="fieldOfView"/>)重新擺一次相機。取景是在 <see cref="SetGender"/> 裡定下來的,而那個方法
        /// 同性別會直接 no-op —— 所以「跑起來之後改 fillFrac」必須走這裡,不能靠再 SetGender 一次。
        /// (大廳的 F4 角色調校面板就是用它做即時預覽,見 <c>LobbyScreen.AvatarDebug.cs</c>。)
        /// </summary>
        public void Reframe()
        {
            var show = _gender == 1 ? _male : _female;
            if (show == null) return;
            FrameTo(show.GetComponent<SdoAvatar>(), show, _gender == 1);
        }

        /// <summary>
        /// 在角色身上「按住拖動」:水平轉身、垂直抬頭 —— 與商城左側那隻同一組官方參數(見上方欄位的註解)。
        /// <paramref name="delta"/> 直接餵 <c>PointerEventData.delta</c>。
        /// </summary>
        public void Orbit(Vector2 delta)
        {
            _dragYaw -= delta.x * DragDegPerPixel;
            // 滑鼠往上(Unity delta.y>0)→ 人往上抬。官方可下看 30°、上抬 15°,不對稱。
            _dragPitch = Mathf.Clamp(_dragPitch + delta.y * DragDegPerPixel, PitchMin, PitchMax);
            ApplyOrbit();
        }

        /// <summary>把轉身角度歸零(換性別/換穿搭時回到官方預設的 yaw)。</summary>
        public void ResetOrbit()
        {
            _dragYaw = 0f;
            _dragPitch = 0f;
            ApplyOrbit();
        }

        private void ApplyOrbit()
        {
            var show = _gender == 1 ? _male : _female;
            if (show == null) return;
            // 🔴 先繞世界 Y 轉身、再繞**固定的世界 X** 抬頭(官方引擎就是這個順序,見欄位註解),
            //    然後把整個人繞腰的高度轉一圈 —— 位置也要跟著轉,否則抬 pitch 時人會離開原地。
            var q = Quaternion.AngleAxis(_dragPitch, Vector3.right)
                  * Quaternion.AngleAxis(avatarYaw + _dragYaw, Vector3.up);
            var pivot = new Vector3(_park.x, _park.y + OrbitPivotY, _park.z);
            var basePos = new Vector3(_park.x, _park.y + _feetOffsetY, _park.z);
            show.SetPositionAndRotation(pivot + q * (basePos - pivot), q);
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
            float feet = GroundFeetY(av, male);
            _feetOffsetY = avatarYOffset - feet;   // 拖曳旋轉要繞 pivot 重算位置,得記住這個基準
            go.transform.position = new Vector3(_park.x, _park.y + _feetOffsetY, _park.z);
            go.transform.localRotation = Quaternion.Euler(0f, avatarYaw, 0f);
            // F7 swaps this preview to the MMD model too. Register BEFORE parking it inactive, so a build that's needed
            // right now (MMD already on) happens on a live GameObject; MmdDebug retries the other gender when it's shown.
            // The MMD rig is height-matched to the SDO body, so the head-to-toe framing below needs no MMD-specific case.
            MmdDebug.RegisterSwappable(av);
            go.SetActive(false);
            return go.transform;
        }

        private void Update()
        {
            MaintainRt();
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

        /// <summary>認領一格沒人用的停車位(見 <see cref="ParkBase"/> 的註解);Destroy 時還回去。</summary>
        private void TakeParkSlot()
        {
            if (_parkSlot >= 0) return;
            for (int i = 0; i < 64 && _parkSlot < 0; i++)
                if (UsedParkSlots.Add(i)) _parkSlot = i;
            // 64 格都滿了(不可能:同時最多兩三個實例)→ 退回第 0 格,寧可重疊也不要不顯示。
            _park = ParkBase + new Vector3(Mathf.Max(_parkSlot, 0) * ParkStride, 0f, 0f);
        }

        private void BuildCamera()
        {
            // RT follows the WINDOW (oversampled), not the slot's 2:3 aspect — the AvtShow slot is 400×600 of the logical
            // 800×600 frame, and Stretch mode scales those two axes by DIFFERENT factors, so a 2:3 RT ends up narrower
            // than the pixels it's shown across (see RtSizing). The 2:3 PROJECTION is pinned below instead.
            RtSizing.SlotRtSize(Screen.width, Screen.height, SlotW, SlotH, previewSupersample, out int rtW, out int rtH);
            _rtTrack.Reset(Screen.width, Screen.height);
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
            _cam.aspect = SlotW / SlotH;                        // pin the slot's 2:3 (the RT is window-shaped now)
        }

        /// <summary>Window resize → re-allocate the preview RT (same instance, so the screen's RawImage stays wired).
        /// Debounced via <see cref="RtResizeTracker"/>; the aspect pin is re-applied in case anything reset it.</summary>
        private void MaintainRt()
        {
            if (_cam != null) _cam.aspect = SlotW / SlotH;
            if (_rt == null) return;
            if (!_rtTrack.Tick(Screen.width, Screen.height, Time.unscaledTime)) return;
            RtSizing.SlotRtSize(Screen.width, Screen.height, SlotW, SlotH, previewSupersample, out int w, out int h);
            RtSizing.Apply(_rt, w, h);
        }

        /// <summary>落地/取景的基準腳高 — 一律用「地面站姿」量,不用當下播的 clip。<see cref="SdoAvatar.FeetYAt(float)"/>
        /// 會先 pose 當前 clip 再取最低頂點,而這個畫面播的是隨機 idle 池(每次抽到的姿勢不同),穿飛行翅膀時更是 flystay
        /// (腳收起來) → 基準會跟著姿勢跳,人的落點與取景距離跟著抖。用固定站姿量,基準才是身體的常數。
        /// 註:這裡不加 <see cref="SpecialMotionItems.HoverY"/> —— 預覽 RT 是透明底、沒有地面可參照,把人整體上移只會讓
        /// 取景偏上,看不出「浮空」。浮空要看得見的地方是房間與舞台。見 [[sdo-special-item-idle-walk]]。</summary>
        private static float GroundFeetY(SdoAvatar av, bool male)
            => av.FeetYAt(0f, SdoRoomAvatar.LoadMot(male ? SdoRoomAvatar.MaleIdleMot : SdoRoomAvatar.IdleMot));

        // Frame the dancer head-to-toe: feet rest on y=0 (set at build), the head bone (+ hair pad) is the top. Place a
        // level camera on −Z at a distance that fits the body height into fillFrac of the vertical FOV.
        private void FrameTo(SdoAvatar av, Transform root, bool male)
        {
            if (_cam == null || av == null || root == null) return;
            av.PoseFrame(0f);
            float headY = av.BoneModelPos("Bip01_Head").y;
            if (headY <= 0f) headY = av.BoneModelPos("Bip01_Neck").y;
            float feet = GroundFeetY(av, male);   // 同一把尺:取景高度不隨隨機 idle / flystay 抖動
            float bodyTop = (headY > 0f ? (headY - feet) : nominalHeight) * (1f + framePadTop);   // world Y of hair top (feet at 0)
            float viewH = Mathf.Max(bodyTop, 1f) / Mathf.Max(fillFrac, 0.1f);                      // vertical extent to frame
            float centerY = bodyTop * 0.5f + verticalBias;
            float dist = viewH * 0.5f / Mathf.Tan(fieldOfView * 0.5f * Mathf.Deg2Rad);
            var eye = new Vector3(_park.x, centerY, _park.z - dist);
            var look = new Vector3(_park.x, centerY, _park.z);
            _cam.transform.position = eye;
            _cam.transform.LookAt(look, Vector3.up);
        }

        private void OnDestroy()
        {
            if (_parkSlot >= 0) { UsedParkSlots.Remove(_parkSlot); _parkSlot = -1; }   // 把格子還回去
            if (_cam != null) _cam.targetTexture = null;
            if (_rt != null) { _rt.Release(); Destroy(_rt); _rt = null; }
        }
    }
}
