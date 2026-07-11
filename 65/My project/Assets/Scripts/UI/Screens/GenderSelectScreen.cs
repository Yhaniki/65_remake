using UnityEngine;
using UnityEngine.UI;
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
    /// 座標逐字取自 DDRLOBBYSEL.XML 的 win5（800×600 4:3、左上原點）。原版另有「鍵盤/毯子模式」選擇——單機鍵盤唯一輸入，
    /// 毯子(跳舞毯)無意義，故省略那組核取方塊與其面板(LobbySel138)。3D 預覽掛在自己的相機+layer→RenderTexture，顯示時把
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
        private Image _keyboardMode, _dancingPadMode;
        private Sprite _keyboardOn, _keyboardOff, _dancingPadOn, _dancingPadOff;
        private Camera _maskedCam; private int _savedMask;
        private int _gender;   // 0=女, 1=男

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

            // decorative bands / logo (tolerant of missing art). Top strips 47..50, bottom labels 134..137, logo + twt.
            UIKit.AddSprite(Root, "Strip47", An("LobbySel47"), 0f, 0f);
            UIKit.AddSprite(Root, "Strip48", An("LobbySel48"), 256f, 0f);
            UIKit.AddSprite(Root, "Strip49", An("LobbySel49"), 512f, 0f);
            UIKit.AddSprite(Root, "Strip50", An("LobbySel50"), 768f, 0f);
            UIKit.AddSprite(Root, "Label134", An("LobbySel134"), 13f, 518f);
            UIKit.AddSprite(Root, "Label135", An("LobbySel135"), 269f, 518f);
            UIKit.AddSprite(Root, "Label136", An("LobbySel136"), 525f, 518f);
            UIKit.AddSprite(Root, "Label137", An("LobbySel137"), 781f, 518f);

            // DDRLOBBYSEL right-side input mode panel.
            UIKit.AddSprite(Root, "ModeFrame", An("LobbySel138"), 593f, 276f);
            _keyboardOff = An("LobbySel0a"); _keyboardOn = An("LobbySel0b");
            _dancingPadOff = An("LobbySel1a"); _dancingPadOn = An("LobbySel1b");
            _keyboardMode = AddToggleSprite("KeyboardMode", 599f, 286f, () => SelectInputMode(keyboard: true));
            _dancingPadMode = AddToggleSprite("DancingPadMode", 599f, 394f, () => SelectInputMode(keyboard: false));
            var twt = UIKit.AddSprite(Root, "Twt", An("twt"), 627f, 435f);
            var twtAnim = twt.gameObject.AddComponent<SpriteSeqAnim>();
            twtAnim.Frames = LobbySelArt.AnFrames("twt");
            twtAnim.Fps = 12f;
            SelectInputMode(keyboard: true);

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
            SelectInputMode(keyboard: true);  // 預設 = 鍵盤被選中(藍邊 LobbySel0b)；每次顯示都重置成 keyboard

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

        private void SelectGender(int g)
        {
            _gender = g == 1 ? 1 : 0;
            UIKit.ApplySprite(_maleBox, _gender == 1 ? _maleOn : _maleOff);
            UIKit.ApplySprite(_femaleBox, _gender == 0 ? _femaleOn : _femaleOff);
            if (_preview != null) _preview.SetGender(_gender);
        }

        private void SelectInputMode(bool keyboard)
        {
            UIKit.ApplySprite(_keyboardMode, keyboard ? _keyboardOn : _keyboardOff);
            UIKit.ApplySprite(_dancingPadMode, keyboard ? _dancingPadOff : _dancingPadOn);
        }

        // 進入：切 active 使用者(女/男 → 00000000/00000001)、把身分帶回 session、建/進房間（不經大廳列表，直接進房）。
        private void OnEnter()
        {
            string id = ProfileManager.SeededIdForGender(_gender);
            ProfileManager.SetActive(id);            // 載入該帳號 profile + 收藏 + config.ini，觸發 ActiveChanged
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
            if (Ctx != null && Ctx.Rooms != null && Ctx.Rooms.CurrentRoom == null)
                Ctx.Rooms.CreateRoom(GameMode.Normal);
            GoTo(ScreenId.Room);
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

        private Image AddToggleSprite(string name, float x, float y, System.Action onClick)
        {
            var img = UIKit.AddSprite(Root, name, null, x, y, raycast: true);
            var btn = img.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(() => onClick());
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
