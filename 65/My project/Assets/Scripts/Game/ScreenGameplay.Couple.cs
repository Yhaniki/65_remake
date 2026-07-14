using System;
using System.IO;
using UnityEngine;
using Sdo.Ruleset;

namespace Sdo.Game
{
    /// <summary>
    /// 情侶模式 (LOVER / Couple) gameplay behaviour, added on top of the base <see cref="ScreenGameplay"/> when
    /// <see cref="ScreenGameplay.coupleMode"/> is set (FrontendApp: GameMode==2). Faithful to the CN online client's
    /// couple screen (mode byte +0x62=0x0c). See docs/reverse-engineering/SDO_COUPLE_MODE.md.
    ///
    /// What this partial owns:
    ///   • a second, opposite-gender (MALE) dancer that faces the local one (real Y-axis rotation, LoverFacing);
    ///   • a heart HUD + heart collection (LoverHearts, clamp-20) driven off judgements;
    ///   • the finish "photo" moment: a couple pose + the TakePhoto.cv scripted camera + a PhotoFrameDlg overlay
    ///     (the client saves NO screenshot — 拍照 is a camera move + frame overlay; see §2/§3).
    ///
    /// PLACEHOLDERS (unrecoverable from the .c alone — flagged for Unity eye-tuning / Frida, see §7):
    ///   • the exact per-slot facing angle table (DAT_00b86c64) → tunable <see cref="partnerYawDeg"/>/<see cref="partnerOffset"/>;
    ///   • which judgement/combo emits a heart server-side → port rule "one heart per Perfect" in <see cref="OnCoupleJudge"/>;
    ///   • the PhotoFrame variant-selection + reward-tier rules → variant 0 / no reward tier yet.
    /// </summary>
    public sealed partial class ScreenGameplay
    {
        private const int LocalSlot = 0;
        private const int PartnerSlot = 1;

        // ---- partner (MALE) dancer: default outfit mirrors the WOMAN set (900001..900006_MAN_*). ----
        public string[] partnerParts =
        {
            "AVATAR/900001_MAN_FACE.MSH",
            "AVATAR/900002_MAN_HAIR.MSH",
            "AVATAR/900003_MAN_COAT.MSH",
            "AVATAR/900004_MAN_PANT.MSH",
            "AVATAR/900006_MAN_SHOES.MSH",
            "AVATAR/900005_MAN_HAND.MSH",
        };
        public string partnerHrc = "AVATAR/MALE.HRC";
        public string partnerRestMot = "MOTION/MREST0082.MOT";   // male in-game standby idle (rest cat 0x15)
        public string partnerWinMot = "MWIN0001.MOT";            // male winner 定格 (cat5)
        public string partnerLoseMot = "MREST0004.MOT";          // male loser 定格 (cat4)
        public int partnerBodyShapeIndex = 1;                    // 標準 male build
        // PLACEHOLDER face-to-face transform: the real per-slot angle (DAT_00b86c64) + offset are not in the .c.
        // Tune in the Inspector; proper framing arrives with the couple camera. See SDO_COUPLE_MODE.md §4/§7.
        public Vector3 partnerOffset = new Vector3(0f, 0f, 120f); // world units from the local dance-spot
        public float partnerYawDeg = 180f;                       // real Y-axis model yaw (deg → LoverFacing quaternion)

        // ---- heart HUD (a row of small hearts that fill as the local dancer collects). ----
        public float heartHudX = 300f, heartHudY = 44f, heartHudStep = 13f;
        private static readonly Color HeartFull = Color.white;
        private static readonly Color HeartEmpty = new Color(1f, 1f, 1f, 0.16f);
        public float photoSweepSec = 6f;                         // duration to play the TakePhoto.cv sweep over

        private LoverHearts _hearts;
        private SdoAvatar _partner;
        private Transform _partnerRoot;
        private SpriteRenderer[] _heartHud;
        private Sprite _heartSprite;
        private CvLoader _photoCam;
        private bool _photoActive;
        private float _photoStart;
        private GameObject _photoFrameGO;

        /// <summary>Called from Start() right after TryLoadAvatar() when <see cref="coupleMode"/> is set.</summary>
        private void SetupCoupleMode()
        {
            _hearts = new LoverHearts();
            BuildHeartHud();
            SpawnPartnerAvatar();
            _photoCam = LoadAsset("CAMERA/COUPLE/TAKEPHOTO.CV", b => CvLoader.Load(b));   // CV002 (no screenshot; §2/§5)
            Debug.Log($"[couple] LOVER mode set up (partner={( _partner != null)}, photoCam={(_photoCam != null)})");
        }

        // Per-frame couple upkeep (heart HUD). Hooked from Update() under `if (coupleMode)`.
        private void CoupleUpdate()
        {
            UpdateHeartHud();
        }

        // ---- hearts ------------------------------------------------------------------------------------------
        private void BuildHeartHud()
        {
            string dir = Path.Combine(SdoExtracted.Root, "UI", "HEARTS");
            var frames = SdoExtracted.LoadAn(dir, "SMALL_HEART.AN");
            _heartSprite = (frames != null && frames.Length > 0) ? frames[0] : null;
            _heartHud = new SpriteRenderer[LoverHearts.MaxPerDancer];
            for (int i = 0; i < _heartHud.Length; i++)
            {
                var sr = NewSR("Heart" + i, _heartSprite, 46);
                SdoLayout.PlaceTopLeft(sr, heartHudX + i * heartHudStep, heartHudY, -1f);
                sr.color = HeartEmpty;
                _heartHud[i] = sr;
            }
        }

        private void UpdateHeartHud()
        {
            if (_heartHud == null || _hearts == null) return;
            int n = _hearts.Count(LocalSlot);
            for (int i = 0; i < _heartHud.Length; i++)
                if (_heartHud[i]) _heartHud[i].color = i < n ? HeartFull : HeartEmpty;
        }

        // Hooked from ApplyEvent() after the score/health apply. Port rule (send-side unrecovered, §2/§7):
        // a Perfect grants the local dancer one heart (clamped at 20).
        private void OnCoupleJudge(Judgment j)
        {
            if (_hearts == null) return;
            if (j == Judgment.Perfect) _hearts.AddHeart(LocalSlot);
        }

        // ---- partner avatar (MALE, face-to-face) ------------------------------------------------------------
        private void SpawnPartnerAvatar()
        {
            if (!use3dCamera || !_camReady) return;   // partner only in the faithful 3D (camera) path
            var parent = new GameObject("PartnerAvatar");
            _partner = BuildCoupleAvatar(parent, partnerParts, partnerHrc, partnerRestMot, true, partnerBodyShapeIndex);

            // Real Y-axis model rotation so the pair physically turns to face each other (FUN_009307c0, §4).
            LoverFacing.YawQuaternion(partnerYawDeg, out var qx, out var qy, out var qz, out var qw);
            var rot = new Quaternion((float)qx, (float)qy, (float)qz, (float)qw);
            float feetY = _partner != null ? _partner.FeetYAt(0f) : 0f;
            parent.transform.SetPositionAndRotation(
                new Vector3(_danceSpot.x + partnerOffset.x, _danceSpot.y - feetY + partnerOffset.y, _danceSpot.z + partnerOffset.z),
                rot);

            if (_partner != null)
            {
                // share the local dancer's choreography so the partner 對跳 in sync (biped clips retarget to MALE.HRC)
                var dps = LoadAsset(dpsPath, b => DpsLoader.Load(b));
                if (dps != null)
                {
                    _partner.Dps = dps;
                    _partner.MotResolver = ResolveMot;
                    _partner.DanceTimeSec = () => (float)(Time.timeAsDouble - _clockStart);
                    _partner.DanceEnabled = () => _dancing && !_failed;
                }
                _partner.PoseInitialIdle();
            }
            SetLayerRecursive(parent, SceneLayer);
            _partnerRoot = parent.transform;
        }

        // Self-contained skinned-avatar build (mirrors TryLoadAvatar's mesh loop; kept separate so the working solo
        // path is untouched). Loads each MSH submesh, resolves its DDS, and skins it to the HRC via SdoAvatar.AddPart.
        private SdoAvatar BuildCoupleAvatar(GameObject parent, string[] parts, string hrcRel, string restRel, bool male, int bodyIdx)
        {
            HrcLoader hrc = LoadAsset(hrcRel, b => HrcLoader.Load(b));
            SdoAvatar av = null;
            if (hrc != null)
            {
                av = parent.AddComponent<SdoAvatar>();
                av.Setup(hrc, null);
                av.SetBodyShape(SdoBodyShape.WeightFromIndex(bodyIdx, male));
                av.RestMot = LoadAsset(restRel, b => MotLoader.Load(b));
            }
            foreach (var rel in parts)
            {
                var path = Path.Combine(SdoExtracted.Root, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) { Debug.LogWarning("[couple] missing " + rel); continue; }
                var r = MshLoader.Load(File.ReadAllBytes(path));
                if (r == null || r.Submeshes.Count == 0) { Debug.LogWarning("[couple] parse fail " + rel); continue; }
                var avatarDir = Path.GetDirectoryName(path);
                int si = 0;
                foreach (var sub in r.Submeshes)
                {
                    var go = new GameObject(Path.GetFileNameWithoutExtension(rel) + "_" + si++);
                    go.transform.SetParent(parent.transform, false);
                    go.AddComponent<MeshFilter>().mesh = sub.Mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    bool twoSided = rel.ToUpperInvariant().Contains("HAIR");
                    var ds = twoSided ? Shader.Find("Sdo/UnlitDoubleSided") : null;
                    var texShader = ds != null ? ds : Shader.Find("Unlit/Texture");
                    if (sub.Ranges != null && sub.Ranges.Count > 1 && sub.Mesh.subMeshCount == sub.Ranges.Count)
                    {
                        var mats = new Material[sub.Ranges.Count];
                        for (int s = 0; s < sub.Ranges.Count; s++)
                        {
                            int a = sub.Ranges[s].Attrib;
                            string nm = (sub.DdsNames != null && a >= 0 && a < sub.DdsNames.Length && !string.IsNullOrEmpty(sub.DdsNames[a])) ? sub.DdsNames[a] : sub.Dds;
                            Texture2D t = ResolveDds(avatarDir, nm);
                            mats[s] = t != null ? new Material(texShader) { mainTexture = t }
                                                : new Material(Shader.Find("Unlit/Color")) { color = PartColor(rel) };
                        }
                        mr.sharedMaterials = mats;
                    }
                    else
                    {
                        Texture2D tex = ResolveDds(avatarDir, sub.Dds);
                        if (tex != null) mr.sharedMaterial = new Material(texShader) { mainTexture = tex };
                        else mr.sharedMaterial = new Material(Shader.Find("Unlit/Color")) { color = PartColor(rel) };
                    }
                    if (av != null && sub.BindVerts != null && sub.BoneHrc != null)
                        av.AddPart(sub.Mesh, sub.BindVerts, sub.BoneHrc, sub.BoneWt, sub.MshInvBindByHrc);
                }
            }
            return av;
        }

        // ---- finish: couple pose + TakePhoto camera + photo-frame overlay (NO screenshot; §2/§3) --------------
        private void CoupleFinish()
        {
            if (_partner != null)   // partner joins the win/lose 定格 pose
            {
                var mot = ResolveMot(_localWon ? partnerWinMot : partnerLoseMot);
                if (mot != null) _partner.PlayOneShot(mot, true);
            }
            if (_photoCam != null) { _photoActive = true; _photoStart = Time.time; }   // start the TakePhoto.cv sweep
            ShowPhotoFrame();
        }

        // Camera override consumed by Update()'s camera block while the photo sweep is active.
        private bool SamplePhotoCam(out Vector3 eye, out Vector3 tgt)
        {
            eye = default; tgt = default;
            if (_photoCam == null || !_photoActive) return false;
            float t = Mathf.Clamp01((Time.time - _photoStart) / Mathf.Max(0.1f, photoSweepSec));
            _photoCam.Sample(t, out eye, out tgt);
            return true;
        }

        // Full-screen photo frame = a 4×3 grid of PhotoFrameDlg tiles (transparent centre) over the posed dancers.
        // Variant-selection rule unrecovered (§3/§7) → variant 0 (tiles 0..11), row-major guess; tune in Unity.
        private void ShowPhotoFrame()
        {
            if (_photoFrameGO != null) { _photoFrameGO.SetActive(true); return; }
            _photoFrameGO = new GameObject("PhotoFrame");
            string dir = Path.Combine(SdoExtracted.Root, "UI", "PHOTOFRAMEDLG");
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 4; c++)
                {
                    int idx = r * 4 + c;
                    var frames = SdoExtracted.LoadAn(dir, "PHOTOFRAME" + idx + ".AN");
                    var spr = (frames != null && frames.Length > 0) ? frames[0] : null;
                    if (spr == null) continue;
                    var sr = NewSR("PhotoFrameTile" + idx, spr, 60);
                    sr.transform.SetParent(_photoFrameGO.transform, false);
                    SdoLayout.PlaceBox(sr, c * 200f, r * 200f, 200f, 200f, -2f);
                }
        }
    }
}
