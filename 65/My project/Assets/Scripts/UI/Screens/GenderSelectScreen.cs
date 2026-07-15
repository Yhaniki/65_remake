using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using Sdo.Game;
using Sdo.Settings;
using Sdo.UI.Core;
using Sdo.UI.Services;
using Sdo.UI.Util;

namespace Sdo.UI.Screens
{
    /// <summary>
    /// 單機開場的男/女選擇畫面 —— 忠實重製原版 LOBBYSEL 的「MainScreen」(DDRLOBBYSEL.XML)：不透明 BG 背景 + 左中一位
    /// 即時 3D 舞者預覽(GenderPreview3D，切男女換模型) + 左下角男/女核取方塊 + 右下角「進入房間 / 離開」按鈕。選性別 ==
    /// 選 profile：女→00000000、男→00000001（ProfileManager.SeededIdForGender）。按進入 → 切 active 使用者、把身分帶回
    /// session、建/進房間（依需求「直接進房間」而不經大廳列表）。離開 → AppQuit。
    ///
    /// 座標逐字取自 DDRLOBBYSEL.XML 的 win5（800×600 4:3、左上原點）。原版頂端的橫幅(LobbySel47..50)不顯示。右側「鍵盤/
    /// 毯子模式」面板照畫，但單機鍵盤是唯一輸入 → 鍵盤固定顯示選中圖 LobbySel0c、跳舞毯固定 LobbySel1a（LobbySel1b 不用），
    /// 按跳舞毯只發音效不換圖。3D 預覽掛在自己的相機+layer→RenderTexture，顯示時把
    /// 該 layer 從前端 UI 相機的 cullingMask 遮掉（同 RoomScreen 掛 3D 房間的做法），OnHide 時整組拆除。
    /// </summary>
    public sealed class GenderSelectScreen : UIScreenBase
    {
        public override ScreenId Id => ScreenId.GenderSel;

        // DDRLOBBYSEL.XML win5 coords (top-left pixel).
        private static readonly Vector2 AvatarView = new Vector2(150f, 0f);   // AvtShow x/y
        private static readonly Vector2 AvatarSize = new Vector2(400f, 600f); // AvtShow w/h
        private const float MaleX = 20f, FemaleX = 74f, CheckY = 530f;        // male/female CheckBox x/y
        private const float EnterX = 586f, QuitX = 684f, BtnY = 527f;         // EnterRoom / Quit x/y

        private GenderPreview3D _preview;
        private RawImage _previewImg;
        private Image _maleBox, _femaleBox;
        private Sprite _maleOn, _maleOff, _femaleOn, _femaleOff;
        private Camera _maskedCam; private int _savedMask;
        private int _gender;   // 0=女, 1=男

        // 改名框（uGUI，可拖動）：改「目前選的性別」對應那個 user 的名字（女 00000000 / 男 00000001）。
        // 用 uGUI TMP_InputField 而非 IMGUI TextField —— IMGUI 在 Windows 上打不了中文 IME；TMP 走 UIFont.Cjk 就能打
        // （同聊天室/房間聊天）。放在畫布 design 座標下 → 自然落在背景圖裡，不用算 pillarbox。
        private TMP_InputField _nameInput; private TextMeshProUGUI _nameStatusLabel; private GameObject _namePanel;

        private static Sprite An(string n) => LobbySelArt.An(n);

        protected override void BuildUI()
        {
            // opaque 800×600 backdrop (drawn FIRST = at the back; the original lists BG.an last because its engine draws
            // back-to-front). Fallback to the app bg colour if the art is missing.
            var bg = An("BG");
            if (bg != null) UIKit.AddSprite(Root, "Bg", bg, 0f, 0f);
            else UIKit.Stretch(UIKit.AddImage(Root, "Bg", UITheme.Bg).rectTransform);

            // live 3D dancer preview window (texture wired in OnShow once the RT exists)
            _previewImg = AddRaw("AvatarView", AvatarView.x, AvatarView.y, AvatarSize.x, AvatarSize.y);
            _previewImg.color = new Color(1f, 1f, 1f, 0f);   // hidden until the RT is assigned

            // decorative bands / logo (tolerant of missing art). 原版頂端還有一排橫幅 (LobbySel47..50)，這裡不顯示。
            UIKit.AddSprite(Root, "Label134", An("LobbySel134"), 13f, 518f);
            UIKit.AddSprite(Root, "Label135", An("LobbySel135"), 269f, 518f);
            UIKit.AddSprite(Root, "Label136", An("LobbySel136"), 525f, 518f);
            UIKit.AddSprite(Root, "Label137", An("LobbySel137"), 781f, 518f);

            // DDRLOBBYSEL 右側輸入模式面板。單機只有鍵盤能玩 → 鍵盤固定顯示選中的 LobbySel0c，跳舞毯固定 LobbySel1a
            // (從不換成 LobbySel1b)；按跳舞毯只會發出音效 (AddToggleSprite 裡的 UiSfx)，圖不變。
            UIKit.AddSprite(Root, "ModeFrame", An("LobbySel138"), 593f, 276f);
            UIKit.ApplySprite(AddToggleSprite("KeyboardMode", 599f, 286f, null), An("LobbySel0c"));
            UIKit.ApplySprite(AddToggleSprite("DancingPadMode", 599f, 394f, null), An("LobbySel1a"));
            var twt = UIKit.AddSprite(Root, "Twt", An("twt"), 627f, 435f);
            var twtAnim = twt.gameObject.AddComponent<SpriteSeqAnim>();
            twtAnim.Frames = LobbySelArt.AnFrames("twt");
            twtAnim.Fps = 12f;

            // male / female checkboxes (mutually exclusive). 130a/b = male off/on, 131a/b = female off/on.
            _maleOff = An("LobbySel130a"); _maleOn = An("LobbySel130b");
            _femaleOff = An("LobbySel131a"); _femaleOn = An("LobbySel131b");
            _maleBox = AddCheckbox("MaleCheck", MaleX, CheckY, () => SelectGender(1));
            _femaleBox = AddCheckbox("FemaleCheck", FemaleX, CheckY, () => SelectGender(0));

            // right-bottom action buttons: 進入房間 (29/30/31) + 商城 (32/33/34 — 原「離開」鍵位，美術已重繪成商城入口).
            // Positions swapped: 進入房間 sits on the right (QuitX); the 商城 button on the left (EnterX). Each keeps its
            // own art + click handler, only the X slot is exchanged.
            var enter = UIKit.AddSpriteButton(Root, "EnterRoom", An("LobbySel29"), An("LobbySel30"), An("LobbySel31"), QuitX, BtnY);
            enter.onClick.AddListener(OnEnter);
            UiSfx.AttachClick(enter);   // 按下 → SE_0001
            var shop = UIKit.AddSpriteButton(Root, "Shop", An("LobbySel32"), An("LobbySel33"), An("LobbySel34"), EnterX, BtnY);
            shop.onClick.AddListener(OnOpenShop);
            UiSfx.AttachClick(shop);    // 按下 → SE_0001

            // Official AvtShow is composited over the lobby chrome; keep the transparent RT on top.
            if (_previewImg != null) _previewImg.transform.SetAsLastSibling();

            BuildNamePanel();   // 改名框（最後建 → 疊在最上層可點/可拖）
        }

        // 改名框：畫布 design 座標下的小面板（標題 + 輸入框 + 儲存），整塊可拖。輸入框走 UIKit.AddInputField
        // （內建 UIFont.Cjk）→ 中文能打。放在 design (x,y) → 落在 800×600 背景圖裡，不用管螢幕 pillarbox。
        private void BuildNamePanel()
        {
            var panel = UIKit.AddImage(Root, "NamePanel", new Color(0f, 0f, 0f, 0.5f), raycast: true).rectTransform;
            _namePanel = panel.gameObject;
            PlaceTL(panel, 14f, 232f, 224f, 100f);   // 靠左、垂直略偏中；可拖，之後想動就拖
            var drag = panel.gameObject.AddComponent<PanelDrag>();
            drag.Target = panel; drag.Area = Root;

            var title = UIKit.AddText(panel, "Title", "玩家名稱", 16f, UITheme.Text);
            PlaceTL(title.rectTransform, 12f, 8f, 200f, 22f);

            _nameInput = UIKit.AddInputField(panel, "NameInput", "", 15f);
            PlaceTL(_nameInput.GetComponent<RectTransform>(), 12f, 36f, 146f, 30f);
            _nameInput.characterLimit = 24;
            _nameInput.onSubmit.AddListener(_ => SaveName());

            var save = UIKit.AddButton(panel, "NameSave", out var saveLabel, UITheme.Accent, UITheme.OnPrimary, 15f);
            saveLabel.text = "儲存";
            PlaceTL(save.GetComponent<RectTransform>(), 166f, 36f, 46f, 30f);
            save.onClick.AddListener(SaveName);

            _nameStatusLabel = UIKit.AddText(panel, "Status", "", 12f, new Color(0.75f, 0.75f, 0.75f));
            PlaceTL(_nameStatusLabel.rectTransform, 12f, 70f, 200f, 22f);
        }

        // design (x,y,w,h) → RectTransform（top-left 原點、y 向下，同 UIKit.AddSprite/AddRaw/Place 的慣例）。
        private static void PlaceTL(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
        }

        // 讓面板可拖：只用「這一幀相對上一幀」的位移（在世界空間畫布上要透過 UI 相機轉換，不能用原始像素 delta），
        // 加到 anchoredPosition 上。用 delta 所以座標系原點是誰不重要（會相消）。同 RoomScreen 拖聊天泡泡的做法。
        private sealed class PanelDrag : MonoBehaviour, IBeginDragHandler, IDragHandler
        {
            public RectTransform Target, Area;
            private Vector2 _last;
            public void OnBeginDrag(PointerEventData e) => _last = Local(e);
            public void OnDrag(PointerEventData e)
            {
                var now = Local(e);
                if (Target != null) Target.anchoredPosition += now - _last;
                _last = now;
            }
            private Vector2 Local(PointerEventData e)
            {
                var cam = FrontendApp.Instance != null ? FrontendApp.Instance.UiCam : null;
                RectTransformUtility.ScreenPointToLocalPointInRectangle(Area, e.position, cam, out var p);
                return p;
            }
        }

        public override void OnShow()
        {
            // initial selection follows the current active profile's gender (00000000 女 on first boot).
            _gender = Ctx != null && Ctx.Session != null && Ctx.Session.Gender == 1 ? 1 : 0;

            // 每個性別用它對應 profile 的「實際穿戴」顯示 (女 00000000 / 男 00000001)；換裝後回到本畫面也刷新。
            string[] fParts = PartsForGender(0), mParts = PartsForGender(1);
            if (_preview == null)
            {
                var go = new GameObject("GenderPreview3D");
                _preview = go.AddComponent<GenderPreview3D>();
                _preview.Build(_gender, fParts, mParts);
            }
            else
            {
                _preview.SetOutfits(_gender, fParts, mParts);
            }
            if (_previewImg != null && _preview != null && _preview.PreviewTexture != null)
            {
                _previewImg.texture = _preview.PreviewTexture;
                _previewImg.color = Color.white;
            }

            // mask the preview's 3D layer off the front-end UI camera (it renders ~everything, so it would draw the
            // dancer flat/ortho otherwise). Restored in OnHide.
            var ui = FrontendApp.Instance != null ? FrontendApp.Instance.UiCam : null;
            if (ui != null) { _maskedCam = ui; _savedMask = ui.cullingMask; ui.cullingMask &= ~(1 << GenderPreview3D.PreviewLayer); }

            SelectGender(_gender);            // sync checkbox sprites + preview

            // 商城是疊在本畫面上的 modal，關閉時不會重跑 OnShow → 綁一個 refresh 讓「在商城買了衣服/換了性別」回到本畫面時，
            // 3D 預覽能用最新穿搭/性別刷新（否則預覽停在開商城前的樣子，要進房間才看得到；見 ShopScreen.SetVisible(false)）。
            Nav.RefreshGenderPreview = RefreshFromShop;
        }

        // 從商城 modal 關閉回到本畫面時呼叫：性別同步成 session（商城可能切了性別=切帳號），並用兩個帳號最新的「實際穿戴」
        // 刷新 3D 預覽（PartsForGender 直接讀 profile.json → 商城購買已 SaveAll 落地）。
        private void RefreshFromShop()
        {
            int g = Ctx != null && Ctx.Session != null && Ctx.Session.Gender == 1 ? 1 : 0;
            _gender = g;
            if (_preview != null) _preview.SetOutfits(g, PartsForGender(0), PartsForGender(1));
            UIKit.ApplySprite(_maleBox, g == 1 ? _maleOn : _maleOff);
            UIKit.ApplySprite(_femaleBox, g == 0 ? _femaleOn : _femaleOff);
        }

        // 取某性別對應 profile (女 00000000 / 男 00000001) 的「實際穿戴」部位；找不到 → null (GenderPreview3D 用預設整套)。
        // 從 id-based equippedItems 經 catalog 現算 (含合成 翅膀/表情/项链)，而非讀可能過時的 equippedParts 快取 →
        // 選性別畫面才會跟儲物櫃/房間一致顯示飾品 (user: 選性別沒顯示)。
        private static string[] PartsForGender(int gender)
        {
            string id = Sdo.Settings.ProfileManager.SeededIdForGender(gender);
            foreach (var p in Sdo.Settings.ProfileManager.List())
                if (p != null && p.id == id)
                    return WardrobeStore.ResolveEquippedParts(p, gender, cid => AvatarItemCatalog.Instance.ById(cid));
            return null;
        }

        public override void OnHide()
        {
            if (Nav.RefreshGenderPreview == RefreshFromShop) Nav.RefreshGenderPreview = null;   // 離開本畫面 → 解綁 (避免刷到已拆的預覽)
            if (_maskedCam != null) { _maskedCam.cullingMask = _savedMask; _maskedCam = null; }
            if (_previewImg != null) { _previewImg.texture = null; _previewImg.color = new Color(1f, 1f, 1f, 0f); }
            if (_preview != null) { Destroy(_preview.gameObject); _preview = null; }
        }

        // 開場的選角色畫面沒有「離開」鈕（原「離開」鍵位美術已改成商城入口），所以用 ESC 退出遊戲。走 AppQuit 即時 hard-kill，
        // 避開 Unity shutdown 卡死（同 LobbyScreen 登出）。商城 modal 疊在本畫面上時本畫面仍 Visible → 讓商城保有 ESC 空間，
        // 只有商城沒開（本畫面在最上層）時才吃 ESC。
        private void Update()
        {
            // 改名框：商城 modal 疊上來時（本畫面仍 Visible）先藏起來，關掉商城再現。!Visible 時 CanvasGroup 已讓整個
            // 畫面透明，不用另外管。放在早退之前處理。
            if (_namePanel != null)
            {
                bool show = Visible && !(FrontendApp.Instance != null && FrontendApp.Instance.ShopOpen);
                if (_namePanel.activeSelf != show) _namePanel.SetActive(show);
            }

            if (!Visible || ScreenTransition.Busy) return;   // 轉場中(進房/進商城漸黑漸亮)先不吃 ESC
            if (FrontendApp.Instance != null && FrontendApp.Instance.ShopOpen) return;
            // F2：開發用 —— 進譜面編輯器。先把前端收掉並註冊「編輯器 ESC 退出時還原前端」（Sdo.Game 不能反向引用
            // Sdo.UI，所以用回呼把還原注進去），再開編輯器。編輯器開著時本畫面的 canvas 會被停用 → Update 不再跑。
            if (Input.GetKeyDown(KeyCode.F2) && ChartEditorScreen.Instance == null)
            {
                ChartEditorScreen.OnExit = () => { var f = FrontendApp.Instance; if (f != null) f.ShowAfterTool(); };
                FrontendApp.Instance?.HideForTool();
                ChartEditorScreen.Launch();
                return;
            }
            if (Input.GetKeyDown(KeyCode.Escape)) Sdo.Game.AppQuit.Now();
        }

        // 讀某性別 seed 帳號目前的名字（唯讀，不動 active）。List() 掃磁碟，只在切性別時呼叫一次，不是每幀。
        private static string SeedName(int gender)
        {
            string id = ProfileManager.SeededIdForGender(gender);
            foreach (var p in ProfileManager.List()) if (p.id == id) return p.name;
            return gender == 1 ? "玩家002" : "玩家001";
        }

        // 切性別時把輸入框填成該性別目前的名字、清掉狀態訊息。
        private void RefreshNameField()
        {
            if (_nameInput != null) _nameInput.SetTextWithoutNotify(SeedName(_gender));
            if (_nameStatusLabel != null) _nameStatusLabel.text = "";
        }

        // 存：把名字寫回該性別的 profile.json（＋這次執行的 session，房間/遊戲頭上名牌就吃得到）。
        // SetActive 到該性別（OnEnter 本來也會做同一件事）→ 改 name → Save。空白名字 Sanitize 會擋，這裡先攔。
        private void SaveName()
        {
            string name = (_nameInput != null ? _nameInput.text : "").Trim();
            if (name.Length == 0) { if (_nameStatusLabel != null) _nameStatusLabel.text = "名稱不可空白"; return; }
            string id = ProfileManager.SeededIdForGender(_gender);
            ProfileManager.SetActive(id);
            ProfileManager.Active.name = name;
            ProfileManager.Save();
            if (Ctx != null && Ctx.Session != null) Ctx.Session.LocalPlayerName = name;
            if (_nameStatusLabel != null) _nameStatusLabel.text = "已儲存，進入房間後生效";
        }

        private void SelectGender(int g)
        {
            _gender = g == 1 ? 1 : 0;
            UIKit.ApplySprite(_maleBox, _gender == 1 ? _maleOn : _maleOff);
            UIKit.ApplySprite(_femaleBox, _gender == 0 ? _femaleOn : _femaleOff);
            if (_preview != null) _preview.SetGender(_gender);
            RefreshNameField();   // 輸入框跟著顯示該性別目前的名字
        }

        // 進入：切 active 使用者(女/男 → 00000000/00000001)、把身分帶回 session、建/進房間（不經大廳列表，直接進房）。
        private void OnEnter()
        {
            string id = ProfileManager.SeededIdForGender(_gender);
            ProfileManager.SetActive(id);            // 載入該帳號 profile(衣服)，觸發 ActiveChanged；收藏/設定是全帳號共用不重載
            var p = ProfileManager.Active;
            if (p != null && p.id == id && p.gender != _gender)
            {
                p.gender = _gender;
                ProfileManager.Save();
            }
            if (Ctx != null && Ctx.Session != null && p != null)
            {
                Ctx.Session.LocalPlayerId = p.id;
                Ctx.Session.LocalPlayerName = p.name;
                Ctx.Session.Gender = _gender;
                Ctx.Session.SeedRoomDefaults();      // 房間面板預設是 per-user（換帳號要重種）
            }
            // 選性別 == 進「我自己」的房間當房主：確保當前身分真的擁有房主座位。若殘留了上一個身分/別人的房
            // (例如 ESC 退出未清房)，先離開再重建，否則 IsHost=false → 房主標記消失。
            if (Ctx != null && Ctx.Rooms != null)
                RoomEntry.EnsureOwnHostRoom(Ctx.Rooms, GameMode.Normal);
            // 進房間轉場：漸黑 → loading → 漸亮，房間 UI 從四邊滑入（Nav.PlayRoomEntrance）。切畫面(GoTo)在全黑時執行。
            ScreenTransition.Run(() => GoTo(ScreenId.Room), onReveal: Nav.PlayRoomEntrance);
        }

        // 開場畫面直接進 商城 modal：先把目前選的性別帶進 session（商城依 session 性別開對應性別的 avatar），再開 商城，
        // 疊在本畫面上；商城的 shopexit 只是隱藏 modal → 關閉後回到開場畫面（不離開遊戲）。
        private void OnOpenShop()
        {
            if (Ctx != null && Ctx.Session != null) Ctx.Session.Gender = _gender;
            Nav.OpenShop?.Invoke();
        }

        // ---- helpers ----

        // A checkbox = a single sprite Image with a Button; SelectGender swaps the on/off sprite (we drive the visual, so
        // no SpriteSwap transition). Placed at XML top-left pixel (x,y).
        private Image AddCheckbox(string name, float x, float y, System.Action onClick)
            => AddToggleSprite(name, x, y, onClick);

        // onClick 可為 null → 按下只有音效、圖不變（跳舞毯：單機不支援，但保留原版的按鈕回饋）。
        private Image AddToggleSprite(string name, float x, float y, System.Action onClick)
        {
            var img = UIKit.AddSprite(Root, name, null, x, y, raycast: true);
            var btn = img.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.transition = Selectable.Transition.None;
            if (onClick != null) btn.onClick.AddListener(() => onClick());
            UiSfx.AttachPress(btn, UiSfx.Click);   // 性別畫面按鈕按下 → SE_0001
            return img;
        }

        // RawImage at XML top-left pixel (x,y), size (w,h). anchorMin=max=(0,1), pivot=(0,1) → 1px=1unit like the sprites.
        private RawImage AddRaw(string name, float x, float y, float w, float h)
        {
            var rt = UIKit.NewRect(Root, name);
            var ri = rt.gameObject.AddComponent<RawImage>();
            ri.raycastTarget = false;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            return ri;
        }
    }
}
