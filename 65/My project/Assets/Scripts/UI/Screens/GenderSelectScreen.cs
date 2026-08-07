using UnityEngine;
using UnityEngine.UI;
using Sdo.Game;
using Sdo.Localization;
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
        private const float EnterX = 586f, QuitX = 684f, BtnY = 527f;         // 商城 / 登入 x/y

        /// <summary>右下角那顆「登入」鈕。登入中要把它變灰擋重複按 → 得留著 reference。</summary>
        private Button _enterBtn;

        private GenderPreview3D _preview;
        private RawImage _previewImg;
        private Image _maleBox, _femaleBox;
        private Sprite _maleOn, _maleOff, _femaleOn, _femaleOff;
        private Camera _maskedCam; private int _savedMask;
        private int _gender;   // 0=女, 1=男

        // 左側那塊面板：上面是改名（改「目前選的性別」對應那個 user 的名字，女 00000000 / 男 00000001），
        // 展開後是 config.ini 裡「遊戲內沒有其它 UI 可以改」的設定全集（見 StartupConfigPanel / StartupConfigSchema）。
        private StartupConfigPanel _panel;
        private int _nameEditFor = -1;   // 面板目前帶的是哪個性別的名字（換性別要重帶）
        // 面板上的體型（胖瘦）滑桿：兩個性別各一份工作副本，OnShow 從各自的 profile 種進來。拖曳中只改這裡＋
        // 即時刷新 3D 預覽（＋連線中即時報給 server），落地 profile.json 留到「儲存設定」或按登入進房那一刻。
        private readonly int[] _bodyIndex = { -1, -1 };

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
            twtAnim.Fps = 24f;   // 旋轉速度 ×2（原 12fps）

            // male / female checkboxes (mutually exclusive). 130a/b = male off/on, 131a/b = female off/on.
            _maleOff = An("LobbySel130a"); _maleOn = An("LobbySel130b");
            _femaleOff = An("LobbySel131a"); _femaleOn = An("LobbySel131b");
            _maleBox = AddCheckbox("MaleCheck", MaleX, CheckY, () => SelectGender(1));
            _femaleBox = AddCheckbox("FemaleCheck", FemaleX, CheckY, () => SelectGender(0));

            // right-bottom action buttons: 登入 (29/30/31) + 商城 (32/33/34 — 原「離開」鍵位，美術已重繪成商城入口).
            // Positions swapped: 登入 sits on the right (QuitX); the 商城 button on the left (EnterX). Each keeps its
            // own art + click handler, only the X slot is exchanged.
            //
            // 🔴 **版面固定兩顆**,不再依連線狀態長出第三顆。開機已經不連線了(見 AppContext.Create),
            //    建畫面這一刻永遠是單機 —— 依 Ctx.Net 決定版面只會永遠走到離線那半邊。
            //    「加入別人的房間」現在走大廳的房間列表(登入成功就進大廳),不需要在這裡放「加入」鈕。
            _enterBtn = UIKit.AddSpriteButton(Root, "EnterRoom",
                An("LobbySel29"), An("LobbySel30"), An("LobbySel31"), QuitX, BtnY);
            _enterBtn.onClick.AddListener(OnEnter);
            UiSfx.AttachClick(_enterBtn);   // 按下 → SE_0001
            UIKit.SetAlphaHit(_enterBtn.targetGraphic);   // 命中判定跟著可見圖形走,透明四角不再誤觸

            var shop = UIKit.AddSpriteButton(Root, "Shop", An("LobbySel32"), An("LobbySel33"), An("LobbySel34"),
                                             EnterX, BtnY);
            shop.onClick.AddListener(OnOpenShop);
            UiSfx.AttachClick(shop);    // 按下 → SE_0001
            UIKit.SetAlphaHit(shop.targetGraphic);

            // Official AvtShow is composited over the lobby chrome; keep the transparent RT on top.
            if (_previewImg != null) _previewImg.transform.SetAsLastSibling();
        }

        public override void OnShow()
        {
            // initial selection follows the current active profile's gender (00000000 女 on first boot).
            _gender = Ctx != null && Ctx.Session != null && Ctx.Session.Gender == 1 ? 1 : 0;

            // 體型滑桿的工作副本：每次進本畫面都從 profile 重種（上次沒存就丟掉，不要跨場次殘留）。
            _bodyIndex[0] = BodyIndexForGender(0);
            _bodyIndex[1] = BodyIndexForGender(1);

            // 每個性別用它對應 profile 的「實際穿戴」顯示 (女 00000000 / 男 00000001)；換裝後回到本畫面也刷新。
            string[] fParts = PartsForGender(0), mParts = PartsForGender(1);
            if (_preview == null)
            {
                var go = new GameObject("GenderPreview3D");
                _preview = go.AddComponent<GenderPreview3D>();
                _preview.Build(_gender, fParts, mParts, _bodyIndex[0], _bodyIndex[1]);
            }
            else
            {
                _preview.SetOutfits(_gender, fParts, mParts, _bodyIndex[0], _bodyIndex[1]);
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
            // 體型用「面板上調到一半、還沒存」的工作副本，不是 profile 上的舊值 —— 否則調完體型去逛個商城回來就被打回去了。
            if (_preview != null) _preview.SetOutfits(g, PartsForGender(0), PartsForGender(1), _bodyIndex[0], _bodyIndex[1]);
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

        // ---- 體型（胖瘦）滑桿 ----

        /// <summary>目前選的性別那個角色的體型 index（0..4）。工作副本沒種過就現場從 profile 讀。</summary>
        private int CurrentBodyIndex()
        {
            if (_bodyIndex[_gender] < 0) _bodyIndex[_gender] = BodyIndexForGender(_gender);
            return _bodyIndex[_gender];
        }

        /// <summary>滑桿拖動：更新工作副本 → 左邊 3D 預覽立刻變胖/變瘦 →（連線中）馬上把新體型報給 server，
        /// 別人畫面上的這個角色才會跟著變。落地 profile.json 走 <see cref="PersistBodyShapes"/>（存檔/進房時）。</summary>
        private void SetBodyIndex(int index)
        {
            index = Mathf.Clamp(index, 0, 4);
            if (_bodyIndex[_gender] == index) return;
            _bodyIndex[_gender] = index;
            if (_preview != null) _preview.SetBodyShape(_gender, index);   // 不重建模型，直接改骨架縮放
            // 連線中就即時廣播（值只有 0..4 五格，拖曳不會刷爆封包）。沒連線時由 CommitIdentity 在登入那一刻補送。
            if (Ctx != null && Ctx.Net != null) Ctx.Net.SendLook(_gender, index, PartsForGender(_gender));
        }

        /// <summary>把兩個性別的體型工作副本寫回各自的 profile.json（值沒變的不寫）。最後把 active 切回目前選的性別。</summary>
        private void PersistBodyShapes()
        {
            for (int g = 0; g < 2; g++)
            {
                if (_bodyIndex[g] < 0) continue;                       // 這個性別沒動過 → 不碰
                ProfileManager.SetActive(ProfileManager.SeededIdForGender(g));
                var p = ProfileManager.Active;
                if (p == null || p.bodyShapeIndex == _bodyIndex[g]) continue;
                p.bodyShapeIndex = _bodyIndex[g];
                ProfileManager.Save();
            }
            ProfileManager.SetActive(ProfileManager.SeededIdForGender(_gender));   // 回到目前選的性別
        }

        // 取某性別對應 profile 自己的體型 (胖瘦) index 0..4；找不到 → 0 (瘦)。選性別畫面是角色本人，故用角色自己的身材。
        private static int BodyIndexForGender(int gender)
        {
            string id = Sdo.Settings.ProfileManager.SeededIdForGender(gender);
            foreach (var p in Sdo.Settings.ProfileManager.List())
                if (p != null && p.id == id)
                    return p.bodyShapeIndex;
            return 0;
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
            if (!Visible || ScreenTransition.Busy) return;   // 轉場中(進房/進商城漸黑漸亮)先不吃 ESC
            // 商城 / 輸入房號 框疊在本畫面上時,ESC 是它們的(關框),不是本畫面的(結束遊戲)。
            if (FrontendApp.Instance != null && (FrontendApp.Instance.ShopOpen || FrontendApp.Instance.JoinRoomOpen)) return;
            // 設定面板展開時鍵盤歸它管（欄位裡在打字）：F2 不開編輯器、ESC 只收合面板而不是結束遊戲。
            bool panelOpen = _panel != null && _panel.Expanded;
            // F2：開發用 —— 進譜面編輯器。先把前端收掉並註冊「編輯器 ESC 退出時還原前端」（Sdo.Game 不能反向引用
            // Sdo.UI，所以用回呼把還原注進去），再開編輯器。編輯器開著時本畫面的 canvas 會被停用 → Update 不再跑。
            if (!panelOpen && Input.GetKeyDown(KeyCode.F2) && ChartEditorScreen.Instance == null)
            {
                ChartEditorScreen.OnExit = () => { var f = FrontendApp.Instance; if (f != null) f.ShowAfterTool(); };
                FrontendApp.Instance?.HideForTool();
                ChartEditorScreen.Launch();
                return;
            }
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (_panel != null && _panel.HandleEscape()) return;   // 展開中 → 只收合面板
                Sdo.Game.AppQuit.Now();
            }
        }

        // 左側面板：改名 + （展開後）config.ini 裡沒有其它 UI 的設定全集。IMGUI（沿用 ChartEditorScreen 的
        // GUI.skin.box + TextField 樣式），版面寫在設計像素、由 StartupConfigPanel 自己縮放到 4:3 內容區。
        // 編輯器開著時本畫面 canvas 被停用 → OnGUI 本來就不會跑，這裡的 Instance 守門只是保險。
        private void OnGUI()
        {
            if (!Visible || ScreenTransition.Busy) return;
            if (FrontendApp.Instance != null && (FrontendApp.Instance.ShopOpen || FrontendApp.Instance.JoinRoomOpen)) return;
            if (ChartEditorScreen.Instance != null) return;

            if (_panel == null)
                _panel = new StartupConfigPanel
                {
                    SaveName = SaveName,
                    BodyShapeGet = () => CurrentBodyIndex(),
                    BodyShapeSet = SetBodyIndex,
                    SaveExtra = PersistBodyShapes,
                };
            if (_nameEditFor != _gender) { _panel.SetName(SeedName(_gender)); _nameEditFor = _gender; }

            _panel.Draw(ContentRect());
        }

        // 800×600 內容在螢幕上的矩形 —— 一定要跟 AspectController 現在的取景模式一致，面板才會**貼在背景圖上的
        // 同一個位置**（IMGUI 用螢幕像素，猜錯的話面板會整塊偏掉、甚至壓到底下的男/女核取方塊）：
        //   * Stretch（預設，畫面拉滿）＝整個視窗就是那 800×600，橫豎縮放比例可以不一樣。
        //   * Pillarbox ＝置中的 4:3 子矩形，寬螢幕留左右黑邊、窄螢幕留上下黑邊。
        private static Rect ContentRect()
        {
            float sw = Screen.width, sh = Mathf.Max(1f, Screen.height);
            if (Sdo.Game.AspectController.Mode != Sdo.Game.AspectMode.Pillarbox) return new Rect(0f, 0f, sw, sh);
            const float aspect = 800f / 600f;
            if (sw / sh > aspect) { float cw = sh * aspect; return new Rect((sw - cw) * 0.5f, 0f, cw, sh); }
            float ch = sw / aspect; return new Rect(0f, (sh - ch) * 0.5f, sw, ch);
        }

        // 讀某性別 seed 帳號目前的名字（唯讀，不動 active）。List() 掃磁碟，只在切性別時呼叫一次，不是每幀。
        private static string SeedName(int gender)
        {
            string id = ProfileManager.SeededIdForGender(gender);
            foreach (var p in ProfileManager.List()) if (p.id == id) return p.name;
            return gender == 1 ? "玩家002" : "玩家001";
        }

        // 存：把名字寫回該性別的 profile.json（＋這次執行的 session，房間/遊戲頭上名牌就吃得到）。
        // SetActive 到該性別（OnEnter 本來也會做同一件事）→ 改 name → Save。空白名字 Sanitize 會擋，這裡先攔。
        // 回傳給面板顯示的狀態訊息。
        private string SaveName(string edited)
        {
            string name = (edited ?? "").Trim();
            if (name.Length == 0) return LocalizationManager.Get("cfg.name.empty");
            string id = ProfileManager.SeededIdForGender(_gender);
            ProfileManager.SetActive(id);
            ProfileManager.Active.name = name;
            ProfileManager.Save();
            if (Ctx != null && Ctx.Session != null) Ctx.Session.LocalPlayerName = name;
            return LocalizationManager.Get("cfg.name.saved");
        }

        private void SelectGender(int g)
        {
            _gender = g == 1 ? 1 : 0;
            UIKit.ApplySprite(_maleBox, _gender == 1 ? _maleOn : _maleOff);
            UIKit.ApplySprite(_femaleBox, _gender == 0 ? _femaleOn : _femaleOff);
            if (_preview != null) _preview.SetGender(_gender);
        }

        // 選性別 == 選帳號：切 active 使用者(女/男 → 00000000/00000001)、把身分帶回 session。
        // 「開房」與「加入」按下去的第一件事都是這個 —— 進房之後房間裡顯示的名字/衣服/性別都吃它。
        private void CommitIdentity()
        {
            // 面板上調過的體型先落地 —— 底下 SendLook 報上去的、以及房間/遊戲建角色時讀的都是 profile.json 的值。
            PersistBodyShapes();
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
            // 連線模式:把外觀報給 server —— 別人是靠這份資料把你的角色建出來的。
            // 握手時報的那份是開機狀態(還沒選性別、穿搭也還沒解析),所以這裡一定要再報一次,
            // 否則你在別人的房間畫面上會是預設的女角。
            if (Ctx != null && Ctx.Net != null)
            {
                Ctx.Net.SendLook(_gender, CurrentBodyIndex(), PartsForGender(_gender));
                // 名字也一樣要重報 —— 女角與男角是兩個 profile,名字不一樣。只送外觀的話
                // 別人看到的是「新的男角」配「舊的女角名字」,而且那個名字之後永遠不會變
                // (座位名字是進房那一刻抄過去的)。
                Ctx.Net.PublishIdentity();
            }
        }

        /// <summary>
        /// 「登入」—— 右下角那顆鈕。
        ///
        /// 先把身分定下來(選性別 == 選帳號),再嘗試連伺服器:
        /// * 連上 → 進**大廳**,房間從那邊的列表建或加入(官方就是這個流程)。
        /// * 連不上、或 config.ini 根本沒填伺服器 → 照**原本的單機路徑**:直接進自己的房間當房主。
        ///
        /// 🔴 身分要在連線**之前**定下來:握手會把名字/性別/穿搭一起報上去,
        ///    先連再切帳號的話 server 記到的是上一個角色。
        /// </summary>
        private void OnEnter()
        {
            CommitIdentity();

            var app = FrontendApp.Instance;
            if (app == null) { EnterOwnRoomOffline(); return; }

            SetEnterInteractable(false);   // 登入中把鈕變灰:握手最多 6 秒,沒有回饋的話玩家會一直按
            app.TryLogin(ok =>
            {
                SetEnterInteractable(true);
                if (ok)
                {
                    // 連上了 —— 身分要**再報一次**:CommitIdentity 那時 Ctx.Net 還是 null,
                    // 所以那次的 SendLook/PublishIdentity 整個沒送出去。
                    if (Ctx != null && Ctx.Net != null)
                    {
                        Ctx.Net.SendLook(_gender, CurrentBodyIndex(), PartsForGender(_gender));
                        Ctx.Net.PublishIdentity();
                    }
                    ScreenTransition.Run(() => GoTo(ScreenId.Lobby));
                    return;
                }
                // 「這個名字已經有人在線上」→ **留在這個畫面**。旁邊的「玩家名稱」小視窗就是改名的地方,
                // 而 Toast 說的正是去換一個名字 —— 把他丟進單機房間的話那句建議就做不到了
                // (而且伺服器明明連得上,不是該退回單機的情況)。
                if (app.LoginBlockedByName) return;
                EnterOwnRoomOffline();
            });
        }

        /// <summary>單機:進「我自己」的房間當房主。連不上伺服器時走的就是這條原本的路。</summary>
        private void EnterOwnRoomOffline()
        {
            // 選性別 == 進「我自己」的房間當房主：確保當前身分真的擁有房主座位。若殘留了上一個身分/別人的房
            // (例如 ESC 退出未清房)，先離開再重建，否則 IsHost=false → 房主標記消失。
            // 自己的房 = 房間設定回到自己的預設(出廠自由模式)。理由見 GameSession.ResetRoomSettingsToDefaults。
            if (Ctx != null && Ctx.Session != null) Ctx.Session.ResetRoomSettingsToDefaults();
            if (Ctx != null && Ctx.Rooms != null)
                RoomEntry.EnsureOwnHostRoom(Ctx.Rooms,
                    NetRoomMapping.ToUiMode(Ctx.Session != null ? Ctx.Session.GameMode : 0));
            // 進房間轉場：漸黑 → loading → 漸亮，房間 UI 從四邊滑入（Nav.PlayRoomEntrance）。切畫面(GoTo)在全黑時執行。
            ScreenTransition.Run(() => GoTo(ScreenId.Room), onReveal: Nav.PlayRoomEntrance);
        }

        private void SetEnterInteractable(bool on)
        {
            if (_enterBtn != null) _enterBtn.interactable = on;
        }

        // 開場畫面直接進 商城 modal：先把目前選的性別帶進 session（商城依 session 性別開對應性別的 avatar），再開 商城，
        // 疊在本畫面上；商城的 shopexit 只是隱藏 modal → 關閉後回到開場畫面（不離開遊戲）。
        private void OnOpenShop()
        {
            // 面板上調過的體型先落地 —— 商城左側那個穿衣服的人是 RebuildAvatar 直接讀 profile.json 的
            // bodyShapeIndex 建的，不落地的話「這裡調成胖、進商城人還是瘦」(而且商城買東西存檔時
            // 寫回去的也會是舊值)。與 CommitIdentity(進房) 同一個處理。
            PersistBodyShapes();
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
