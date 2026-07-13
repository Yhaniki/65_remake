using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Sdo.Game;
using Sdo.Settings;
using Sdo.UI.Core;
using Sdo.UI.Util;

namespace Sdo.UI.Screens
{
    /// <summary>
    /// 官方「個人資訊」對話框 (PlayerInformationDlg / WinPlayerInfo) 的忠實重製。房間右鍵點一個人就開他的這一頁。
    ///
    /// 版面座標與素材逐字取自線上版 <c>UI/PLAYERINFORMATIONDLG/PLAYERINFORMATIONDLG.XML</c>（女版粉皮，見
    /// docs/reference/PLAYER_INFO_DLG.md）。中文全部是烘在官方圖上的，我們只疊動態數值。
    ///
    /// 兩個分頁：
    ///   0 基本信息 — 官方那一頁幾乎全是線上系統（天使等级/TP/魅力/幸运/知名度/家族/QQ…），離線沒有來源 → 只填
    ///                得出名字、等級、MVP(=第一名場次)，其餘留白（底圖上的欄位名仍在，忠於官方）。
    ///   1 技术统计 — **就是使用者要的「戰績數據」**：勝率 / 命中率 / Perfect率 / Cool率 / Bad率 / Miss率，
    ///                六個百分比 + 六條進度條。資料來自 profile.json 的累計 <see cref="PlayerStats"/>。
    ///                開啟時預設就停在這一頁。
    ///
    /// 官方頂端的「超舞战绩 / 劲舞战绩 / 目前排名」是伺服器算的舞技分與全服排名，離線沒有任何對應物 → 留白。
    /// </summary>
    public sealed class PlayerInfoModal : MonoBehaviour
    {
        // ---- 官方座標 (PLAYERINFORMATIONDLG.XML, 800×600 左上原點) ----
        private const float FrameX = 93f, FrameY = 56f;
        private const float CloseX = 662f, CloseY = 62f;
        private const float ConfirmX = 608f, ConfirmY = 504f;
        private const float TabX = 336f, TabY = 108f;          // 5 個分頁條全疊在這
        private const float BoardX = 336f, BoardY = 147f;      // 分頁內容底圖
        private const float SkillBgX = 350f, SkillBgY = 245f;
        private const float EffortBtnX = 350f, SkillBtnX = 457f, SubBtnY = 217f;
        private const float BarX = 433f;                        // pro_* 進度條
        private const float RateTextX = 440f;                   // winrate/hitrate/… Label 的 x
        private const float BarW = 236f, BarH = 19f;

        // 六列的 y：用官方**進度條**的 y（pro_winrate…pro_missrate = 258/287/316/345/374/403，剛好 29px 一列，
        // 對得上 SkillBg 底圖的六個凹槽）。官方那六個文字 Label 自己的 y (236/270/303/339/372/407) 排不成 29px 的
        // 格子、第一列還會跑到「统计明细」按鈕上 —— 是舊版面留下來的殘值，所以文字改壓在自己那條 bar 上。
        private static readonly float[] RowY = { 258f, 287f, 316f, 345f, 374f, 403f };

        // 基本信息頁：官方 name/level 在左側人物欄
        private const float NameX = 132f, NameY = 129f, LevelX = 132f, LevelY = 144f;
        private const float MvpX = 443f, MvpY = 414f;

        private static readonly Color RateWhite = Color.white;                        // XML color="0xffffffff"
        private static readonly Color32 NameCream = new Color32(0xFA, 0xFF, 0x74, 0xFF); // XML color="0xfffaff74"
        private static readonly Color32 NameEdge = new Color32(0x5A, 0x1B, 0x45, 0xFF);   // 描邊：面板的深紫紅

        // ---- 左側 3D 人物 (AvtShow x=105 y=111 w=230 h=391) ----
        private const int PreviewLayer = 12;
        private static readonly Vector3 PreviewSpot = new Vector3(3400f, 0f, 0f);   // 與商城(0)/儲物櫃(2200)/頭貼(5200) 錯開
        private static readonly Vector2 AvatarRectPos = new Vector2(105f, 111f);
        private static readonly Vector2 AvatarRectSize = new Vector2(230f, 391f);
        private const float RefHeight = 62f, MaleBodyRatio = 1.08f, MaleSizeScale = 1.05f;
        private static readonly Vector3 EyeFar = new Vector3(0f, 37f, -150f), LookFar = new Vector3(0f, 27f, 0f);
        private const float AimUp = 4f;

        private CanvasGroup _cg;
        private RectTransform _window; private WindowAnim _anim;
        private RectTransform _tab0, _tab1;
        private Image _tab0Img, _tab1Img;
        private Image _board0, _board1;
        private int _page = 1;                       // 預設停在「技术统计」

        private readonly TextMeshProUGUI[] _rate = new TextMeshProUGUI[6];
        private readonly Image[] _bar = new Image[6];
        private OutlinedLabel _name, _level;
        private TextMeshProUGUI _mvp;

        private Camera _cam; private RenderTexture _rt; private RawImage _avatarImg; private GameObject _avatarRoot;
        private Camera _uiCam; private int _savedUiMask;
        private SdoAvatar _av; private float _feetY; private float _bodyH = RefHeight;
        private bool _male;

        private PlayerInfoTarget _target;

        public bool IsOpen => _cg != null && _cg.alpha > 0f;

        // ---------------------------------------------------------------- build
        public void Build(RectTransform parent)
        {
            var root = UIKit.NewRect(parent, "PlayerInfoModal");
            UIKit.Stretch(root);
            _cg = root.gameObject.AddComponent<CanvasGroup>();

            var dim = UIKit.AddImage(root, "Dim", new Color(0f, 0f, 0f, 0.55f), true);
            UIKit.Stretch(dim.rectTransform);

            UIKit.AddSprite(root, "Frame", PlayerInfoArt.Frame, FrameX, FrameY);

            // 左側即時 3D 人物（RT → RawImage，同儲物櫃/商城機制）
            var av = UIKit.NewRect(root, "AvatarShow");
            av.anchorMin = av.anchorMax = new Vector2(0f, 1f); av.pivot = new Vector2(0f, 1f);
            av.anchoredPosition = new Vector2(AvatarRectPos.x, -AvatarRectPos.y);
            av.sizeDelta = AvatarRectSize;
            _avatarImg = av.gameObject.AddComponent<RawImage>();
            _avatarImg.color = Color.white; _avatarImg.raycastTarget = false;

            // 官方的 name/level 是 #faff74 淡黃色，壓在左側人物區上 —— 但官方左側還有一張深色的人物襯底
            // (CharBack，線上執行期才貼)，離線沒有 → 黃字直接壓在淺色底板上會看不見。用專案既有的假描邊
            // (OutlinedLabel，房間名字牌同一套) 加一圈深色邊，保住官方字色又讀得到。
            _name = OutlinedLabel.Create(root, "Name", NameX, NameY, 96f, 18f, 14f, NameCream, NameEdge,
                edgePx: 1.5f, bold: true, align: TextAlignmentOptions.Left);
            _level = OutlinedLabel.Create(root, "Level", LevelX, LevelY, 96f, 18f, 13f, NameCream, NameEdge,
                edgePx: 1.5f, bold: true, align: TextAlignmentOptions.Left);

            // 分頁條：每個 .an 都是整條 350×39、只畫自己那一格 → 兩條疊起來就是官方的分頁列
            _tab0Img = UIKit.AddSprite(root, "Tab0", PlayerInfoArt.Tab0N, TabX, TabY, raycast: true);
            Clickable(_tab0Img, () => ShowPage(0));
            _tab1Img = UIKit.AddSprite(root, "Tab1", PlayerInfoArt.Tab1N, TabX, TabY, raycast: true);
            Clickable(_tab1Img, () => ShowPage(1));

            _board0 = UIKit.AddSprite(root, "Board0", PlayerInfoArt.Board0, BoardX, BoardY);
            _board1 = UIKit.AddSprite(root, "Board1", PlayerInfoArt.Board1, BoardX, BoardY);

            _tab0 = Body(root, "Tab0Body");
            _tab1 = Body(root, "Tab1Body");
            BuildBasic(_tab0);
            BuildSkill(_tab1);

            var confirm = UIKit.AddSpriteButton(root, "Confirm", PlayerInfoArt.ConfirmN, PlayerInfoArt.ConfirmH,
                PlayerInfoArt.ConfirmP, ConfirmX, ConfirmY);
            confirm.onClick.AddListener(Close);
            var close = UIKit.AddSpriteButton(root, "Close", PlayerInfoArt.CloseN, PlayerInfoArt.CloseH,
                PlayerInfoArt.CloseP, CloseX, CloseY);
            close.onClick.AddListener(Close);

            // 除了 dim 以外全部包進 _window → 開關時跟選歌/OPTION 一樣旋轉縮放進出
            _window = UIKit.NewRect(root, "Window");
            UIKit.Stretch(_window);
            _window.pivot = new Vector2(0.5f, 0.5f);
            _window.gameObject.AddComponent<CanvasGroup>();
            _anim = _window.gameObject.AddComponent<WindowAnim>();
            var kids = new System.Collections.Generic.List<Transform>();
            foreach (Transform c in root)
                if (c != (Transform)_window && c.gameObject != dim.gameObject) kids.Add(c);
            foreach (var c in kids) c.SetParent(_window, false);

            foreach (var b in _window.GetComponentsInChildren<Button>(true)) UiSfx.AttachClick(b);

            SetVisible(false);
        }

        private static RectTransform Body(RectTransform root, string name)
        {
            var rt = UIKit.NewRect(root, name);
            UIKit.Stretch(rt);   // 全 800×600；子元件用官方絕對座標
            return rt;
        }

        private static void Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
        }

        private static void Clickable(Image img, UnityEngine.Events.UnityAction onClick)
        {
            var btn = img.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.transition = Selectable.Transition.None;   // 選中狀態自己換 sprite（normal/pushed）
            btn.onClick.AddListener(onClick);
        }

        // ---- 基本信息 (官方 playerTabWindow0) ----
        // 這一頁官方的欄位幾乎都是線上系統，離線只有 MVP(第一名場次) 填得出來。其餘欄位名烘在底圖上，留白。
        private void BuildBasic(RectTransform body)
        {
            _mvp = UIKit.AddText(body, "Mvp", "", 12f, RateWhite, TextAlignmentOptions.Left);
            Place(_mvp.rectTransform, MvpX, MvpY, 60f, 16f);
        }

        // ---- 技术统计 = 戰績數據 (官方 playerTabWindow1 / SkillStat) ----
        private void BuildSkill(RectTransform body)
        {
            UIKit.AddSprite(body, "SkillBg", PlayerInfoArt.SkillBg, SkillBgX, SkillBgY);
            // 成就 / 统计明细 兩個子分頁鈕：官方「成就」(EffortStat) 是線上的努力值道具格，離線沒有內容 →
            // 只畫出來、固定停在「统计明细」(SkillStat)，按了不切頁。
            UIKit.AddSprite(body, "EffortBtn", PlayerInfoArt.EffortBtnN, EffortBtnX, SubBtnY);
            UIKit.AddSprite(body, "SkillBtn", PlayerInfoArt.SkillBtnP, SkillBtnX, SubBtnY);

            for (int i = 0; i < 6; i++)
            {
                _bar[i] = Bar(body, "Bar" + i, RowY[i]);
                _rate[i] = UIKit.AddText(body, "Rate" + i, "0.00%", 12f, RateWhite, TextAlignmentOptions.Left);
                Place(_rate[i].rectTransform, RateTextX, RowY[i], 230f, BarH);
                _rate[i].alignment = TextAlignmentOptions.Left;
                _rate[i].verticalAlignment = VerticalAlignmentOptions.Middle;
            }
        }

        // 一條進度條：官方 forename=PlayerInformationDlg65.an (232×19) 疊在底圖烘好的凹槽上，
        // 用水平 Filled 模擬 ProgressBar 的填充（minrange=0 maxrange=99 → 我們用 0..1 的比例）。
        private static Image Bar(RectTransform body, string name, float y)
        {
            var img = UIKit.AddSprite(body, name, PlayerInfoArt.BarFill, BarX, y);
            img.rectTransform.sizeDelta = new Vector2(BarW, BarH);
            img.type = Image.Type.Filled;
            img.fillMethod = Image.FillMethod.Horizontal;
            img.fillOrigin = (int)Image.OriginHorizontal.Left;
            img.fillAmount = 0f;
            return img;
        }

        // ---------------------------------------------------------------- open / close
        /// <summary>開啟某個人的個人資訊面板。素材缺（data root 沒接線上 UI）→ 安靜地不開。</summary>
        public void Open(PlayerInfoTarget t)
        {
            if (t == null) return;
            if (!PlayerInfoArt.Available)
            {
                Debug.LogWarning("[playerinfo] UI/PLAYERINFORMATIONDLG 不在 data root 裡 — 跑 tools/link_data_root.ps1");
                return;
            }
            _target = t;
            Render();
            BuildPreview();
            RebuildAvatar();
            SetVisible(true);
            ShowPage(1);            // 預設開在「技术统计」(戰績)
            if (_anim != null) _anim.PlayIn();
            UiSfx.Play(UiSfx.FrameRound);
        }

        public void Close()
        {
            if (_cg == null || _cg.alpha <= 0f) return;
            if (_anim != null) _anim.PlayOut(() => SetVisible(false));
            else SetVisible(false);
        }

        private void Render()
        {
            var st = _target.Stats ?? new PlayerStats();
            if (_name != null) _name.SetText(_target.Name ?? "");
            if (_level != null) _level.SetText("Lv " + Mathf.Max(1, _target.Level));
            if (_mvp != null) _mvp.text = st.wins.ToString();

            double[] pct = { st.WinRate, st.HitRate, st.PerfectRate, st.CoolRate, st.BadRate, st.MissRate };
            for (int i = 0; i < 6; i++)
            {
                if (_rate[i] != null) _rate[i].text = PlayerStats.FormatPercent(pct[i]);
                if (_bar[i] != null) _bar[i].fillAmount = PlayerStats.BarFill(pct[i]);
            }
        }

        private void ShowPage(int page)
        {
            _page = page;
            if (_tab0 != null) _tab0.gameObject.SetActive(page == 0);
            if (_tab1 != null) _tab1.gameObject.SetActive(page == 1);
            if (_board0 != null) _board0.enabled = page == 0;
            if (_board1 != null) _board1.enabled = page == 1;
            UIKit.ApplySprite(_tab0Img, page == 0 ? PlayerInfoArt.Tab0P : PlayerInfoArt.Tab0N);
            UIKit.ApplySprite(_tab1Img, page == 1 ? PlayerInfoArt.Tab1P : PlayerInfoArt.Tab1N);
            // 選中的分頁要壓在另一條之上（亮的那格帶橘底線，會蓋到鄰居）
            (page == 0 ? _tab0Img : _tab1Img).transform.SetAsLastSibling();
        }

        private void SetVisible(bool on)
        {
            if (_cg == null) return;
            _cg.alpha = on ? 1f : 0f;
            _cg.interactable = on;
            _cg.blocksRaycasts = on;
            if (_cam != null) _cam.enabled = on;
            if (!on && _uiCam != null) { _uiCam.cullingMask = _savedUiMask; _uiCam = null; }
            if (on && _uiCam == null)
            {
                // 前端 world canvas 的相機不能看到 layer 12，否則預覽人物會直接出現在畫面上（RT 之外）
                var ui = FrontendApp.Instance != null ? FrontendApp.Instance.UiCam : null;
                if (ui != null) { _uiCam = ui; _savedUiMask = ui.cullingMask; ui.cullingMask &= ~(1 << PreviewLayer); }
            }
        }

        private void Update()
        {
            if (!IsOpen) return;
            if (Input.GetKeyDown(KeyCode.Escape)) Close();
        }

        // ---------------------------------------------------------------- 3D 人物 (同儲物櫃機制)
        private void BuildPreview()
        {
            if (_cam != null) return;
            int w = Mathf.RoundToInt(AvatarRectSize.x), h = Mathf.RoundToInt(AvatarRectSize.y);
            _rt = new RenderTexture(w, h, 16, RenderTextureFormat.ARGB32) { name = "PlayerInfoRT", antiAliasing = 4 };
            var go = new GameObject("PlayerInfoPreviewCam");
            _cam = go.AddComponent<Camera>();
            _cam.orthographic = false; _cam.fieldOfView = 32f;
            _cam.nearClipPlane = 0.3f; _cam.farClipPlane = 3000f;
            _cam.cullingMask = 1 << PreviewLayer;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            _cam.targetTexture = _rt;
            if (_avatarImg != null) _avatarImg.texture = _rt;
        }

        private void RebuildAvatar()
        {
            try
            {
                if (_avatarRoot != null) Destroy(_avatarRoot);
                _male = _target.Male;
                _avatarRoot = new GameObject("PlayerInfoAvatar");
                _avatarRoot.transform.position = PreviewSpot;
                _av = SdoRoomAvatar.Build(_avatarRoot, PreviewLayer, portraitOpaque: false,
                    male: _male, equippedParts: _target.AvatarParts);
                if (_av == null) { Destroy(_avatarRoot); _avatarRoot = null; return; }
                _av.DanceEnabled = () => false;
                _av.DanceTimeSec = () => -1f;
                var idle = SdoRoomAvatar.LoadMot(_male ? "MOTION/MREST0082.MOT" : "MOTION/WREST0072.MOT");
                if (idle != null) { _av.RestMot = idle; _av.SetClip(idle); }
                _feetY = _av.FeetYAt(0f);
                _bodyH = RefHeight * (_male ? MaleBodyRatio : 1f);
                _avatarRoot.transform.position = PreviewSpot + new Vector3(0f, -_feetY, 0f);
                _avatarRoot.transform.rotation = Quaternion.Euler(0f, RoomMovement.FacingDegrees(2), 0f);
                ApplyCamera();
            }
            catch (System.Exception e) { Debug.LogWarning("[playerinfo] 3D 預覽建不起來 (不致命): " + e.Message); }
        }

        private void ApplyCamera()
        {
            if (_cam == null) return;
            float refH = RefHeight * (_male ? MaleSizeScale : 1f);
            float k = _bodyH / refH;
            _cam.transform.position = PreviewSpot + new Vector3(EyeFar.x, EyeFar.y * k + AimUp, EyeFar.z * k);
            _cam.transform.LookAt(PreviewSpot + new Vector3(LookFar.x, LookFar.y * k + AimUp, LookFar.z * k));
        }

        private void OnDestroy()
        {
            if (_rt != null) { _rt.Release(); Destroy(_rt); }
            if (_avatarRoot != null) Destroy(_avatarRoot);
        }
    }
}
