using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using Sdo.Game;
using Sdo.Shop;
using Sdo.UI.Core;
using Sdo.UI.Util;

namespace Sdo.UI.Screens
{
    /// <summary>
    /// 儲物櫃 (INVENTORY / 衣櫃) — 忠實重製官方 <c>HOUSECABINETDLG.XML</c> 的 HouseCabinetWin (CabinetAndTv_Dlg 皮，載入器
    /// <see cref="CabinetArt"/>，MYHOUSEDLG 資料夾)。版面座標逐字取自 XML (視窗原點 (30,19)，800×600 左上原點 y-down)：左側
    /// 整身 3D 預覽 (AvatarShow)、右側 3×3 已擁有衣物格 (CabinetButton1..9 + CabinetWear 3D 縮圖 + 使用中旗標)、右緣分類欄
    /// (label_all/head/upcoat/…)、服饰/礼包 分頁 (coat/gift)、換頁箭頭、服饰删除 (DeleteCostume)、M/G 幣量。
    ///
    /// 與商城 (<see cref="ShopScreen"/>) 的差別：格子列的是「已擁有」的衣物 (<see cref="GameSession.Wardrobe"/>.Owned，由
    /// <see cref="WardrobeStore.Load"/> 從 profile.json 載入)；點一件 = 永久換穿 (寫回 profile + 重建房間/遊戲 avatar)，
    /// 不是商城的暫時試穿。3D 預覽/縮圖機制沿用 ShopScreen 的做法 (同一套 SdoRoomAvatar + 正交相機取景)。
    /// </summary>
    public sealed class WardrobeScreen : MonoBehaviour
    {
        private CanvasGroup _cg;
        private RectTransform _root;
        private Image _backdrop;                        // 全螢幕黑幕 (留在 Root，不進旋轉視窗，當點擊擋板)
        private RectTransform _window;                  // 旋轉進出的視窗容器 (參考選歌 SongSelectScreen.WrapInWindow)
        private CanvasGroup _windowCg;
        private WindowAnim _anim;
        private bool _closing;
        private RectTransform _tip;                     // 滑過服飾格顯示服飾名字的浮動提示 (取代官方 CabinetTip.xml)
        private TextMeshProUGUI _tipText;
        private GameSession _session;
        private AvatarItemCatalog _catalog;

        private ItemSex _sex = ItemSex.Female;
        private int _page;
        private int _totalPages = 1;
        private CatFilter _filter = CatFilter.All;   // 分類欄目前選的
        private int _catPage;                         // 分類欄目前頁 (0=身體 8 部位；1=飾品 翅膀/项链)。classup/classdown 換頁
        private int _selected;                        // 目前選中的格子 item id (供 服饰删除)；0=無
        private int _lastClickId; private float _lastClickTime;   // 雙擊偵測 (#2：點兩下換穿/脫下)
        private const float DoubleClickSec = 0.35f;
        private Button _deleteBtn;                    // 服饰删除 (無選中→灰 DeleteCostume_4；#7)

        // 儲物櫃預覽人物的預設 idle 動作 (#10)：男/女各一組，隨機挑一支 (可連續重複)。
        private static readonly string[] FemaleIdles = { "MOTION/WREST0011.MOT", "MOTION/WREST0013.MOT", "MOTION/WREST0016.MOT" };
        private static readonly string[] MaleIdles = { "MOTION/MREST0002_02.MOT", "MOTION/MREST0002_01.MOT" };

        // 官方 HouseCabinetWin 視窗原點 (XML: <Window name="HouseCabinetWin" x="30" y="19">) → 子元件座標都相對它。
        private const float WinX = 30f, WinY = 19f;

        private RectTransform _grid, _catCol;
        private TextMeshProUGUI _moneyM, _moneyG, _moneyP;   // M=Coins / G=Points / P=H幣(Bonus)。Money_J(金葉子)固定 0。
        private TextMeshProUGUI _pageLbl;
        private readonly int[] _slotItemId = new int[PerPage];   // 每格對應的 item id (0=空)
        private readonly Image[] _slotSel = new Image[PerPage];  // 每格選中框 (預設隱藏)

        // ---- 左側 3D 預覽 (同 ShopScreen；整身、可拖動轉身) ----
        private const int PreviewLayer = 12;
        private static readonly Vector3 PreviewSpot = new Vector3(2200f, 0f, 0f);   // 與 ShopScreen 的 PreviewSpot 錯開，避免同層入鏡
        // AvatarShow x=87 y=107 w=220 h=320 (視窗相對) → +Win 後的螢幕框。RT 同尺寸避免拉伸。
        private static readonly Vector2 PreviewRectPos = new Vector2(WinX + 87f, WinY + 107f);
        private static readonly Vector2 PreviewRectSize = new Vector2(220f, 320f);
        private const float PreviewBodyH = 320f;
        private Camera _cam; private RenderTexture _rt; private RawImage _previewImg; private GameObject _avatarRoot;
        private Camera _uiCam; private int _savedUiMask;
        private SdoAvatar _previewAv; private float _idleLen; private float _idleStart;   // #4：預覽 idle 播完一輪換一支隨機
        private float _dragAngle = 0f; private float _pitchAngle;   // 預設 0 = 人物正面朝相機 (#6)
        private const float DragDegPerPixel = 0.4f, PitchDegPerPixel = 0.4f, PitchMin = -30f, PitchMax = 15f;
        private const float DefaultYaw = 30f, PivotY = 30f;
        private const float PreviewAimUp = 5f;   // 相機瞄準點上移 → 人物在框內往下 (修 #9「基準位置太高」；可調)
        private float _previewFeetY;
        private const float RefHeight = 62f, MaleBodyRatio = 1.08f, MaleSizeScale = 1.05f;
        private float _previewHeight = RefHeight;
        private static readonly Vector3 EyeFar = new Vector3(0f, 37f, -140f), LookFar = new Vector3(0f, 27f, 0f);

        // ---- 3×3 格 3D 縮圖 (同 ShopScreen 卡片；靜態 bind-pose，不做 hover 旋轉) ----
        // 一頁 = 9 格 (用滿 3×3)；服饰栏扩充 一次也 +9 格 (一頁)。使用者指定。
        private const int PerPage = 9;
        private const int CardRtW = 100, CardRtH = 100;   // 格子 86×86 → RT 稍大保清晰
        private const float CardEyeDist = 110f, CardOrthoHalfW = 64f, CardNear = 5f, CardFar = 1000f;
        private Camera _cardCam;
        // 縮圖 RT 依 item id 快取：同一件服飾只渲染一次 (靜態 bind-pose)，換穿/重排格子時直接沿用 → 不再每次重建 9 隻縮圖
        // avatar+RT (換穿更順)。開櫃 (Open) 重算、超過上限才整批清。建縮圖用固定 off-screen 位置，avatar 渲完即銷毀不留幾何。
        private readonly Dictionary<int, RenderTexture> _thumbCache = new Dictionary<int, RenderTexture>();
        private const int ThumbCacheCap = 120;
        private static readonly Vector3 ThumbBuildSpot = new Vector3(4000f, 0f, 0f);
        // 穿上的服飾排到格子最前面，且依「穿上先後」排 (先穿在前、後穿接在後面)；未穿的在後、依 ModelId。使用者指定 (task 2)。
        private readonly Dictionary<int, int> _equipOrder = new Dictionary<int, int>();
        private int _equipSeq;

        private static readonly Color CMoney = Hex(0xff004f7c);

        // 分類欄目 → 對應的部位過濾。All = 全部已擁有衣物。
        private enum CatFilter { All, Hair, Top, Bottom, Gloves, Shoes, Face, Glasses, Wings, Necklace }
        private struct CatDef { public CatFilter F; public string Lit, Dim; public float Y; }
        // 官方 label_* 分類鈕 (x=626)，分「頁」：按右緣 classup/classdown 一次把整欄換到上/下一頁。y 取自 XML。
        // lit=bgpushed(選中)、dim=bgnormal。第 0 頁=身體 8 部位；第 1 頁=飾品 (翅膀/项链)。官方第 1 頁另有
        // tail/pate/shoulder/waist/knee/ring 但皆 w=0 h=0 隱藏且本重製無對應資料模型 → 略。
        private static readonly CatDef[][] CatPages =
        {
            new[]
            {
                new CatDef{ F=CatFilter.All,      Lit="label_all_2.an",      Dim="label_all_1.an",      Y=157 },
                new CatDef{ F=CatFilter.Hair,     Lit="label_head_2.an",     Dim="label_head_1.an",     Y=192 },
                new CatDef{ F=CatFilter.Top,      Lit="label_upcoat_2.an",   Dim="label_upcoat_1.an",   Y=226 },
                new CatDef{ F=CatFilter.Bottom,   Lit="label_downcoat_2.an", Dim="label_downcoat_1.an", Y=260 },
                new CatDef{ F=CatFilter.Gloves,   Lit="label_hand_2.an",     Dim="label_hand_1.an",     Y=294 },
                new CatDef{ F=CatFilter.Shoes,    Lit="label_shoes_2.an",    Dim="label_shoes_1.an",    Y=328 },
                new CatDef{ F=CatFilter.Face,     Lit="label_face_2.an",     Dim="label_face_1.an",     Y=362 },
                new CatDef{ F=CatFilter.Glasses,  Lit="label_glasses_2.an",  Dim="label_glasses_1.an",  Y=396 },
            },
            new[]
            {
                new CatDef{ F=CatFilter.Wings,    Lit="label_wing_2.an",     Dim="label_wing_1.an",     Y=157 },
                new CatDef{ F=CatFilter.Necklace, Lit="label_necklace_2.an", Dim="label_necklace_1.an", Y=192 },
            },
        };

        // 3×3 格左上座標 (XML CabinetButton1..9，視窗相對；+Win 後為螢幕座標)。
        private static readonly Vector2[] SlotPos =
        {
            new Vector2(338,145), new Vector2(434,146), new Vector2(528,146),
            new Vector2(338,238), new Vector2(434,238), new Vector2(529,239),
            new Vector2(338,331), new Vector2(434,332), new Vector2(527,332),
        };
        private const float SlotW = 86f, SlotH = 86f;

        public void Build(RectTransform parent, GameSession session)
        {
            _session = session;
            _root = UIKit.NewRect(parent, "Wardrobe");
            UIKit.Stretch(_root);
            _cg = _root.gameObject.AddComponent<CanvasGroup>();

            // 不透明黑幕擋掉後面的房間 3D（點擊不穿透），再蓋官方視窗框。留在 Root，不進旋轉視窗。
            _backdrop = UIKit.AddImage(_root, "Backdrop", new Color(0f, 0f, 0f, 0.55f), true);
            UIKit.Stretch(_backdrop.rectTransform);

            // 官方視窗框 (background.an = CabinetAndTv_Dlg 621×500)；XML Label x=62 y=41 (視窗相對)。
            AddArt(_root, "Frame", CabinetArt.An("background.an"), WinX + 62f, WinY + 41f);

            // 左側 3D 預覽 (AvatarShow)
            var pv = UIKit.NewRect(_root, "Preview");
            pv.anchorMin = pv.anchorMax = new Vector2(0, 1); pv.pivot = new Vector2(0, 1);
            pv.anchoredPosition = new Vector2(PreviewRectPos.x, -PreviewRectPos.y); pv.sizeDelta = PreviewRectSize;
            _previewImg = pv.gameObject.AddComponent<RawImage>(); _previewImg.color = Color.white; _previewImg.raycastTarget = true;
            AddTrigData(pv.gameObject.AddComponent<EventTrigger>(), EventTriggerType.Drag, OnPreviewDrag);

            // 分頁 服饰/礼包 (coat/gift)；礼包離線無資料 → 點了不做事(不跳提示)。
            SpriteBtn(_root, "coat", "coat_2.an", "coat_2.an", WinX + 322f, WinY + 101f, () => { _filter = CatFilter.All; _page = 0; Refresh(); }, noSwap: true);
            SpriteBtn(_root, "gift", "gift_1.an", "gift_1.an", WinX + 488f, WinY + 101f, () => { }, noSwap: true);

            // 分類欄容器 (右緣 label_*) + 3×3 格容器 (依分類/頁重畫)
            _catCol = UIKit.NewRect(_root, "CatCol"); UIKit.Stretch(_catCol);
            _grid = UIKit.NewRect(_root, "Grid"); UIKit.Stretch(_grid);

            // 分類欄換頁 (classup/classdown)：一次把整欄 8 個分類換到上/下一頁 → 才選得到第 2 頁的 翅膀/项链 (XML x=623)。
            SpriteBtn(_root, "classup",   "classup_1.an",   "classup_3.an",   WinX + 623f, WinY + 136f, () => CatPageBy(-1), hoverAn: "classup_2.an");
            SpriteBtn(_root, "classdown", "classdown_1.an", "classdown_3.an", WinX + 623f, WinY + 430f, () => CatPageBy(1),  hoverAn: "classdown_2.an");

            // 換頁箭頭 + 頁碼 (leftarrow/rightarrow/lblPage)
            SpriteBtn(_root, "leftarrow", "leftarrow_1.an", "leftarrow_3.an", WinX + 412f, WinY + 423f, () => PageBy(-1), hoverAn: "leftarrow_2.an");
            SpriteBtn(_root, "rightarrow", "rightarrow_1.an", "rightarrow_3.an", WinX + 520f, WinY + 424f, () => PageBy(1), hoverAn: "rightarrow_2.an");
            _pageLbl = TxtAt(_root, "lblPage", WinX + 436f, WinY + 426f, 84, 20, 14, Hex(0xff780049), TextAlignmentOptions.Center);
            _pageLbl.fontStyle = FontStyles.Bold;

            // 服饰栏扩充 (other_all_coat, #8) + 服饰删除 (DeleteCostume, 存 ref 供灰態 #7) + 關閉 (close)
            SpriteBtn(_root, "other_all_coat", "other_all_coat_1.an", "other_all_coat_3.an", WinX + 376f, WinY + 467f, DoExpandSlots, hoverAn: "other_all_coat_2.an");
            _deleteBtn = SpriteBtn(_root, "DeleteCostume", "DeleteCostume_1.an", "DeleteCostume_3.an", WinX + 505f, WinY + 467f, DoDelete, hoverAn: "DeleteCostume_2.an");
            SpriteBtn(_root, "close", "close_1.an", "close_3.an", WinX + 624f, WinY + 56f, Close, hoverAn: "close_2.an");

            // 幣量：Money_M=Coins / Money_G=Points (上排)；Money_J=金葉子(固定 0) / Money_P=H幣(Bonus) (下排)。(#5)
            _moneyM = TxtAt(_root, "Money_M", WinX + 63f, WinY + 440f, 123, 24, 14, CMoney, TextAlignmentOptions.Right);
            _moneyM.fontStyle = FontStyles.Bold;
            _moneyG = TxtAt(_root, "Money_G", WinX + 180f, WinY + 440f, 123, 24, 14, CMoney, TextAlignmentOptions.Right);
            _moneyG.fontStyle = FontStyles.Bold;
            var moneyJ = TxtAt(_root, "Money_J", WinX + 64f, WinY + 471f, 123, 24, 14, CMoney, TextAlignmentOptions.Right);
            moneyJ.fontStyle = FontStyles.Bold; moneyJ.text = "0";   // 金葉子固定 0 (使用者指定)
            _moneyP = TxtAt(_root, "Money_P", WinX + 181f, WinY + 472f, 123, 24, 14, CMoney, TextAlignmentOptions.Right);
            _moneyP.fontStyle = FontStyles.Bold;

            WrapInWindow();   // 把整個對話框收進會旋轉縮放的視窗容器 (Backdrop 留在 Root)
            BuildTip();       // 服飾名字浮動提示 (建在 WrapInWindow 之後 → 留在 Root，畫在最上層、不隨視窗旋轉)

            SetVisible(false);
        }

        // 參考選歌 SongSelectScreen.WrapInWindow：把 Root 底下的所有內容 (Backdrop 除外) 收進單一置中、pivot 0.5 的視窗
        // 容器，開/關時就能把整組對話框一起旋轉+縮放+淡入淡出。Backdrop 留在 Root 當穩定的全螢幕點擊擋板。
        private void WrapInWindow()
        {
            _window = UIKit.NewRect(_root, "Window");
            UIKit.Stretch(_window);
            _window.pivot = new Vector2(0.5f, 0.5f);
            _windowCg = _window.gameObject.AddComponent<CanvasGroup>();
            _anim = _window.gameObject.AddComponent<WindowAnim>();

            var kids = new List<Transform>();
            foreach (Transform c in _root)
                if (c != (Transform)_window && c != (Transform)_backdrop.transform) kids.Add(c);
            foreach (var c in kids) c.SetParent(_window, false);
        }

        // 滑過服飾格時顯示服飾名字的浮動提示 (取代官方 CabinetButton 的 tip_xml="CabinetTip.xml")。建在 Root、最上層。
        private void BuildTip()
        {
            _tip = UIKit.NewRect(_root, "ItemTip");
            _tip.anchorMin = _tip.anchorMax = new Vector2(0f, 1f);
            _tip.pivot = new Vector2(0.5f, 0f);   // 底邊中心 → 貼在服飾格上方
            var bg = _tip.gameObject.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.85f);
            bg.raycastTarget = false;
            _tipText = UIKit.AddText(_tip, "Text", "", 13, Color.white, TextAlignmentOptions.Center);
            UIKit.Stretch(_tipText, 8, 2, 8, 2);
            _tip.gameObject.SetActive(false);
        }

        // 顯示某格服飾的名字提示 (貼在該格上方置中)。空格或無名 → 隱藏。
        private void ShowTip(int slot)
        {
            if (_tip == null || slot < 0 || slot >= PerPage || _slotItemId[slot] == 0) { HideTip(); return; }
            var it = _catalog != null ? _catalog.ById(_slotItemId[slot]) : null;
            if (it == null) { HideTip(); return; }
            string nm = string.IsNullOrEmpty(it.Name) ? ("服飾 " + it.ModelId) : it.Name;
            _tipText.text = nm;
            _tipText.ForceMeshUpdate();
            float w = Mathf.Clamp(_tipText.preferredWidth + 16f, 44f, 240f);
            _tip.sizeDelta = new Vector2(w, 22f);
            var pos = SlotPos[slot];
            _tip.anchoredPosition = new Vector2(WinX + pos.x + SlotW / 2f, -(WinY + pos.y - 4f));
            _tip.gameObject.SetActive(true);
            _tip.SetAsLastSibling();
        }

        private void HideTip()
        {
            if (_tip != null) _tip.gameObject.SetActive(false);
        }

        /// 更衣間 modal 是否正顯示中（疊在房間上）。房間用它 gate ESC，避免開櫃時 ESC 誤退回選角色。
        public bool IsOpen => _cg != null && _cg.alpha > 0f;

        public void Open()
        {
            _catalog = AvatarItemCatalog.Instance;
            _sex = _session != null && _session.Gender == 1 ? ItemSex.Male : ItemSex.Female;
            // 開櫃時重載 profile → 從實際存檔的穿搭開始 (不吃商城遺留的試穿狀態)。
            WardrobeStore.Load(_session);
            ClearThumbCache();                        // 縮圖快取重算 (性別/穿搭可能已變)；之後同一 session 的換穿沿用
            _equipOrder.Clear(); _equipSeq = 0;
            ReconcileEquipOrder();                    // 依 ModelId 給目前已穿的服飾初始排序 → 穿上的先排在最前 (task 2)
            _filter = CatFilter.All; _catPage = 0; _page = 0; _selected = 0;
            _dragAngle = 0f; _pitchAngle = 0f;   // 正面朝相機 (#6)
            BuildPreview();
            var ui = FrontendApp.Instance != null ? FrontendApp.Instance.UiCam : null;
            if (ui != null) { _uiCam = ui; _savedUiMask = ui.cullingMask; ui.cullingMask &= ~(1 << PreviewLayer); }
            RebuildAvatar();
            SetVisible(true);
            Refresh();

            // 開櫃 whoosh + 快速旋轉放大進場 (Frameround.wav；參考選歌 SongSelectScreen.OnShow)。
            _closing = false;
            if (_windowCg != null) _windowCg.blocksRaycasts = true;
            if (_anim != null) { _anim.ResetOpen(); _anim.PlayIn(); }
            UiSfx.Play(UiSfx.FrameRound);
        }

        private void Refresh()
        {
            RefreshCatColumn();
            RefreshGrid();
            UpdateWallet();
        }

        // ---- 右緣分類欄 (目前頁；classup/classdown 換頁) ----
        private void RefreshCatColumn()
        {
            UIKit.Clear(_catCol);
            _catPage = Mathf.Clamp(_catPage, 0, CatPages.Length - 1);
            foreach (var c in CatPages[_catPage])
            {
                bool active = c.F == _filter;
                var f = c.F;
                SpriteBtn(_catCol, "cat" + c.F, active ? c.Lit : c.Dim, active ? c.Lit : c.Dim,
                          WinX + 626f, WinY + c.Y, () => { _filter = f; _page = 0; Refresh(); }, noSwap: true);
            }
        }

        // classup/classdown：把整欄分類換到上/下一頁。只換顯示的 label，不動目前選的分類/格子 (再點新 label 才換內容)，忠實官方。
        private void CatPageBy(int d)
        {
            int np = Mathf.Clamp(_catPage + d, 0, CatPages.Length - 1);
            if (np == _catPage) return;
            _catPage = np;
            RefreshCatColumn();
        }

        // 目前分類要顯示的「已擁有」衣物。
        private List<ShopItem> OwnedForFilter()
        {
            var res = new List<ShopItem>();
            if (_catalog == null || _session == null) return res;
            foreach (var kv in _session.Wardrobe.Owned)
            {
                var it = _catalog.ById(kv.Key);
                if (it == null || it.SlotType != ItemSlotType.Clothes) continue;
                if (!MatchesFilter(it)) continue;   // #1：擁有的都列出來 (不再用 IsRenderable 過濾掉沒 3D 縮圖的)
                res.Add(it);
            }
            res.Sort(CompareForGrid);   // 穿上的排到最前 (依穿上先後)，未穿的在後依 ModelId (task 2)
            return res;
        }

        // 格子排序：穿上的在前 (依「穿上先後」序，先穿在前、後穿接在後面)，其餘依 ModelId (再以 Id 打破平手求穩定)。
        private int CompareForGrid(ShopItem a, ShopItem b)
        {
            bool aw = IsWorn(a), bw = IsWorn(b);
            if (aw != bw) return aw ? -1 : 1;                       // 穿上的排到最前面
            if (aw)                                                 // 兩件都穿著 → 依穿上先後
            {
                int ao = _equipOrder.TryGetValue(a.Id, out var x) ? x : int.MaxValue;
                int bo = _equipOrder.TryGetValue(b.Id, out var y) ? y : int.MaxValue;
                if (ao != bo) return ao.CompareTo(bo);
            }
            int m = a.ModelId.CompareTo(b.ModelId);
            return m != 0 ? m : a.Id.CompareTo(b.Id);
        }

        // 維護「穿上先後」序：移除已脫下的，給新穿上的指派遞增序號 (→ 接在既有穿上的後面)。開櫃初始化時同一批依 ModelId 穩定排。
        private void ReconcileEquipOrder()
        {
            if (_session == null) return;
            var w = _session.Wardrobe;
            var stale = new List<int>();
            foreach (var id in _equipOrder.Keys) if (!w.IsEquipped(id)) stale.Add(id);
            foreach (var id in stale) _equipOrder.Remove(id);
            var fresh = new List<ShopItem>();
            foreach (var kv in w.Equipped)
            {
                var it = _catalog != null ? _catalog.ById(kv.Value) : null;
                if (it != null && !_equipOrder.ContainsKey(it.Id)) fresh.Add(it);
            }
            fresh.Sort((a, b) => a.ModelId.CompareTo(b.ModelId));
            foreach (var it in fresh) _equipOrder[it.Id] = _equipSeq++;
        }

        private bool MatchesFilter(ShopItem it)
        {
            switch (_filter)
            {
                case CatFilter.All: return true;
                case CatFilter.Top: return it.EquipSlot == EquipSlot.Top || it.EquipSlot == EquipSlot.OnePiece;
                case CatFilter.Hair: return it.EquipSlot == EquipSlot.Hair;
                case CatFilter.Bottom: return it.EquipSlot == EquipSlot.Bottom;
                case CatFilter.Gloves: return it.EquipSlot == EquipSlot.Gloves;
                case CatFilter.Shoes: return it.EquipSlot == EquipSlot.Shoes;
                case CatFilter.Face: return it.EquipSlot == EquipSlot.Expression;
                case CatFilter.Glasses: return it.EquipSlot == EquipSlot.Glasses;
                case CatFilter.Wings: return it.EquipSlot == EquipSlot.Wings;
                case CatFilter.Necklace: return it.EquipSlot == EquipSlot.Necklace;
                default: return true;
            }
        }

        private void RefreshGrid()
        {
            HideTip();   // 重建格子前先收掉提示 (被 hover 的格子會被銷毀，PointerExit 不一定會觸發)
            if (_thumbCache.Count > ThumbCacheCap) ClearThumbCache();   // 綁定記憶體：翻很多頁後才整批清一次
            UIKit.Clear(_grid);   // 格子 (含縮圖 RawImage) 隨之銷毀；縮圖 RT 留在快取不重建 3D
            if (_catalog == null) { _totalPages = 1; UpdatePageLabel(); return; }

            var items = OwnedForFilter();
            // 依「服飾欄容量」分頁 (擴充後頁數變多；至少容得下已擁有的)。一頁 PerPage(9) 格。
            int capacity = Mathf.Max(_session.Wardrobe.ClothSlotCount, items.Count);
            int pages = Mathf.Max(1, (capacity + PerPage - 1) / PerPage);
            _page = Mathf.Clamp(_page, 0, pages - 1);
            _totalPages = pages;
            UpdatePageLabel();
            int start = _page * PerPage;

            for (int i = 0; i < PerPage; i++)
            {
                int idx = start + i;
                var pos = SlotPos[i];
                var card = UIKit.NewRect(_grid, "slot" + i);
                card.anchorMin = card.anchorMax = new Vector2(0, 1); card.pivot = new Vector2(0, 1);
                card.anchoredPosition = new Vector2(WinX + pos.x, -(WinY + pos.y)); card.sizeDelta = new Vector2(SlotW, SlotH);
                _slotItemId[i] = 0; _slotSel[i] = null;
                if (idx >= items.Count) continue;
                var item = items[idx];
                _slotItemId[i] = item.Id;

                BuildCardPreview(i, card, item);                 // 3D 縮圖
                if (IsWorn(item))                                // 使用中：整格壓暗 (#4) + 使用中旗標 (HouseCabinetDlg46.an)
                {
                    var dark = UIKit.AddImage(card, "dark", new Color(0f, 0f, 0f, 0.5f), false);
                    var drt = dark.rectTransform; drt.anchorMin = drt.anchorMax = new Vector2(0, 1); drt.pivot = new Vector2(0, 1);
                    drt.anchoredPosition = Vector2.zero; drt.sizeDelta = new Vector2(SlotW, SlotH);
                    AddArt(card, "worn", CabinetArt.An("HouseCabinetDlg46.an"), 0, 0);
                }
                // 選中框 (HouseCabinetDlg32.an)：先建好、預設隱藏，選中時才顯示 (單擊選中不重建整頁縮圖)。
                var sel = AddArt(card, "sel", CabinetArt.An("HouseCabinetDlg32.an"), 0, 0);
                if (sel != null) { sel.enabled = item.Id == _selected; _slotSel[i] = sel; }

                // 透明命中區：單擊=選中、雙擊=換穿/脫下 (#2)。
                var itLocal = item;
                var hit = UIKit.AddImage(card, "hit", new Color(1, 1, 1, 0.001f), true);
                var hrt = hit.rectTransform;
                hrt.anchorMin = hrt.anchorMax = new Vector2(0, 1); hrt.pivot = new Vector2(0, 1);
                hrt.anchoredPosition = Vector2.zero; hrt.sizeDelta = new Vector2(SlotW, SlotH);
                var btn = hit.gameObject.AddComponent<Button>(); btn.targetGraphic = hit; btn.transition = Selectable.Transition.None;
                btn.onClick.AddListener(() => OnCardClick(itLocal));
                // 滑過該格 → 顯示服飾名字提示 (取代官方 CabinetTip；#2)。
                int slot = i;
                var trig = hit.gameObject.AddComponent<EventTrigger>();
                AddTrigData(trig, EventTriggerType.PointerEnter, _ => ShowTip(slot));
                AddTrigData(trig, EventTriggerType.PointerExit, _ => HideTip());
            }
            UpdateDeleteBtn();
        }

        // 單擊=選中 (只換選中框，不重建縮圖)；雙擊=換穿/脫下 (#2)。
        private void OnCardClick(ShopItem item)
        {
            if (item == null || item.EquipSlot == EquipSlot.None) return;
            float now = Time.unscaledTime;
            bool dbl = item.Id == _lastClickId && (now - _lastClickTime) < DoubleClickSec;
            _lastClickId = item.Id; _lastClickTime = now;
            if (dbl) { _lastClickId = 0; ToggleEquip(item); return; }
            _selected = item.Id;
            UpdateSelectionHighlight();
            UpdateDeleteBtn();
        }

        // 雙擊：已穿著 → 脫下 (回預設)；未穿 → 換穿。寫回 profile + 重建房間/遊戲 avatar + 左側預覽。
        private void ToggleEquip(ShopItem item)
        {
            UiSfx.Play(UiSfx.Run);   // 穿脫衣物的布料摩擦音 (SE\Run.wav；使用者指定；#4)
            var w = _session.Wardrobe;
            if (w.IsEquipped(item.Id))
            {
                w.ClearEquipped(item.EquipSlot);   // 點兩下脫下
            }
            else if (item.EquipSlot == EquipSlot.OnePiece)
            {
                w.ClearEquipped(EquipSlot.Top); w.ClearEquipped(EquipSlot.Bottom);
                w.SetEquipped(EquipSlot.OnePiece, item.Id);
            }
            else
            {
                if (item.EquipSlot == EquipSlot.Top || item.EquipSlot == EquipSlot.Bottom) w.ClearEquipped(EquipSlot.OnePiece);
                w.SetEquipped(item.EquipSlot, item.Id);
            }
            ReconcileEquipOrder();                      // 更新「穿上先後」序 (新穿的接在既有穿上的後面；task 2)
            WardrobeStore.SaveAll(_session);            // 落地 profile.json (穿搭 + equippedParts)
            Nav.RefreshRoomAvatar?.Invoke();            // 櫃子後方的房間人 + 頭貼當場換裝 (不等關櫃)
            RebuildAvatar();                            // 左側預覽換裝 (焦點，立即)
            RefreshGrid();                              // 重排格子 (穿上的移到最前) + 更新使用中壓暗；縮圖走快取不重建 3D
        }

        private void UpdateSelectionHighlight()
        {
            for (int i = 0; i < PerPage; i++)
                if (_slotSel[i] != null) _slotSel[i].enabled = _slotItemId[i] != 0 && _slotItemId[i] == _selected;
        }

        // 服饰删除：沒選中就是灰的、按了提示；有選中才真的刪 (若正穿著先脫下)。
        private void DoDelete()
        {
            if (_selected == 0 || _session == null) { Toast.Show("請先選擇要刪除的服飾"); return; }
            var w = _session.Wardrobe;
            var it = _catalog != null ? _catalog.ById(_selected) : null;
            string nm = it != null && !string.IsNullOrEmpty(it.Name) ? it.Name : (it != null ? "服飾 " + it.ModelId : "服飾");
            bool wasWorn = it != null && w.IsEquipped(_selected);
            if (wasWorn) w.ClearEquipped(it.EquipSlot);   // 穿著中 → 先脫下
            w.RemoveOwned(_selected);
            _selected = 0;
            ReconcileEquipOrder();                        // 脫下的從「穿上先後」序移除
            WardrobeStore.SaveAll(_session);
            if (wasWorn) Nav.RefreshRoomAvatar?.Invoke();  // 刪的是穿著中的才需重建房間人 + 頭貼 (當場)
            RebuildAvatar();
            Refresh();
            Toast.Show("已刪除服飾：" + nm);
        }

        // 服饰栏扩充 (#8)：一次擴充「一頁」= PerPage(9) 格 (使用者指定)，上限 1000，落地 profile.json。
        private void DoExpandSlots()
        {
            if (_session == null) return;
            var w = _session.Wardrobe;
            if (w.ClothSlotCount >= 1000) { Toast.Show("服飾欄已達上限 1000"); return; }
            w.ClothSlotCount = Mathf.Min(1000, w.ClothSlotCount + PerPage);   // +一頁 (9 格)
            WardrobeStore.SaveAll(_session);
            RefreshGrid();   // 頁數立即更新 (1/N)
            Toast.Show("服飾欄擴充成功（目前 " + w.ClothSlotCount + " 格）");
        }

        // 服饰删除 按鈕態：沒選中 → 灰 (DeleteCostume_4)；有選中 → 正常。
        private void UpdateDeleteBtn()
        {
            if (_deleteBtn == null) return;
            var img = _deleteBtn.targetGraphic as Image;
            if (img != null) UIKit.ApplySprite(img, CabinetArt.An(_selected == 0 ? "DeleteCostume_4.an" : "DeleteCostume_1.an"));
        }

        private bool IsWorn(ShopItem it) => it != null && _session != null && _session.Wardrobe.IsEquipped(it.Id);

        private void PageBy(int d)
        {
            int np = Mathf.Clamp(_page + d, 0, _totalPages - 1);
            if (np == _page) return;
            _page = np; RefreshGrid();
        }

        private void UpdatePageLabel()
        {
            if (_pageLbl != null) _pageLbl.text = (_page + 1) + " / " + _totalPages;
        }

        private void UpdateWallet()
        {
            if (_session == null) return;
            var wl = _session.Wardrobe.Wallet;
            if (_moneyM != null) _moneyM.text = wl.Coins.ToString();
            if (_moneyG != null) _moneyG.text = wl.Points.ToString();
            if (_moneyP != null) _moneyP.text = wl.Bonus.ToString();   // 右下 H幣
        }

        // ================= 3D 預覽 (整身) — 同 ShopScreen 機制 =================

        private void BuildPreview()
        {
            if (_cam != null) return;
            int rtW = Mathf.RoundToInt(PreviewRectSize.x), rtH = Mathf.RoundToInt(PreviewRectSize.y);
            _rt = new RenderTexture(rtW, rtH, 16, RenderTextureFormat.ARGB32) { name = "WardrobePreviewRT", antiAliasing = 4 };
            var camGo = new GameObject("WardrobePreviewCam");
            _cam = camGo.AddComponent<Camera>();
            _cam.orthographic = false; _cam.fieldOfView = 32f;
            _cam.nearClipPlane = 0.3f; _cam.farClipPlane = 3000f;
            _cam.cullingMask = 1 << PreviewLayer;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            _cam.targetTexture = _rt;
            if (_previewImg != null) _previewImg.texture = _rt;
            ApplyCamera();
        }

        private void RebuildAvatar()
        {
            try
            {
                if (_avatarRoot != null) Destroy(_avatarRoot);
                _avatarRoot = new GameObject("WardrobePreviewAvatar");
                _avatarRoot.transform.position = PreviewSpot;
                var parts = AvatarOutfit.ResolveParts(_sex, EquippedItems());
                // 左側「玩家假人」跟隨玩家自己的體型 (胖瘦)：儲物櫃是本人衣櫃，_sex = active profile 的性別。
                float bodyB = SdoBodyShape.WeightFromIndex(Sdo.Settings.ProfileManager.Active.bodyShapeIndex, _sex == ItemSex.Male);
                var av = SdoRoomAvatar.Build(_avatarRoot, PreviewLayer, false, parts.ToArray(), AvatarOutfit.HrcFor(_sex), bodyWeight: bodyB);
                if (av == null) { Destroy(_avatarRoot); _avatarRoot = null; return; }
                ApplyLeftPose(av);
                _previewFeetY = av.FeetYAt(0f);
                _previewHeight = RefHeight * (_sex == ItemSex.Male ? MaleBodyRatio : 1f);
                ApplyCamera();
                ApplyPreviewRotation();
            }
            catch (System.Exception e) { Debug.LogWarning("[wardrobe] preview build failed (non-fatal): " + e.Message); }
        }

        private IEnumerable<ShopItem> EquippedItems()
        {
            if (_catalog == null) yield break;
            foreach (var kv in _session.Wardrobe.Equipped)
            {
                var it = _catalog.ById(kv.Value);
                if (it != null) yield return it;
            }
        }

        private void ApplyLeftPose(SdoAvatar av)
        {
            _previewAv = av;
            ApplyRandomIdle(initial: true);
        }

        // #10：男/女各一組預設 idle 隨機挑一支 (可連續重複)。記錄本支長度 → Update 播完一輪再換一支 (#4：每播完一次 random 一次)。
        private void ApplyRandomIdle(bool initial)
        {
            var av = _previewAv;
            if (av == null) return;
            var list = _sex == ItemSex.Male ? MaleIdles : FemaleIdles;
            MotLoader mot = null;
            if (list != null && list.Length > 0)
                mot = SdoRoomAvatar.LoadMot(list[UnityEngine.Random.Range(0, list.Length)]);
            if (mot == null) mot = SdoRoomAvatar.LoadMot(_sex == ItemSex.Male ? "MOTION/MREST0082.MOT" : "MOTION/WREST0072.MOT");
            if (mot == null) return;
            av.RestMot = mot; av.SetClip(mot);
            if (initial) av.PoseInitialIdle();
            _idleLen = av.Fps > 0f ? mot.MaxTime / av.Fps : 0f;   // 秒 = 幀數 / fps(30)
            _idleStart = Time.time;
        }

        private void Update()
        {
            if (_cg == null || _cg.alpha <= 0f) return;   // 只在儲物櫃開著時
            // X 鍵：跟按關閉一樣旋轉出去 (使用者指定)。
            if (!_closing && Input.GetKeyDown(KeyCode.X)) { Close(); return; }
            if (_previewAv == null || _idleLen <= 0f) return;
            if (Time.time - _idleStart >= _idleLen) ApplyRandomIdle(initial: false);   // 播完一輪 → 換一支隨機 (#4)
        }

        private void OnPreviewDrag(BaseEventData ev)
        {
            if (!(ev is PointerEventData p)) return;
            _dragAngle -= p.delta.x * DragDegPerPixel;
            _pitchAngle = Mathf.Clamp(_pitchAngle + p.delta.y * PitchDegPerPixel, PitchMin, PitchMax);
            ApplyPreviewRotation();
        }

        private void ApplyPreviewRotation()
        {
            if (_avatarRoot == null) return;
            var pivot = PreviewSpot + new Vector3(0f, PivotY, 0f);
            var basePos = PreviewSpot + new Vector3(0f, -_previewFeetY, 0f);
            var q = Quaternion.Euler(_pitchAngle, RoomMovement.FacingDegrees(2) + _dragAngle, 0f);
            _avatarRoot.transform.rotation = q;
            _avatarRoot.transform.position = pivot + q * (basePos - pivot);
        }

        private void ApplyCamera()
        {
            if (_cam == null) return;
            float refH = RefHeight * (_sex == ItemSex.Male ? MaleSizeScale : 1f);
            float k = _previewHeight / refH;
            // 瞄準點整體上移 PreviewAimUp → 人物在框內往下 (修 #9 基準太高)。eye/look 同步上移保持水平不俯仰。
            _cam.transform.position = PreviewSpot + new Vector3(EyeFar.x, EyeFar.y * k + PreviewAimUp, EyeFar.z * k);
            _cam.transform.LookAt(PreviewSpot + new Vector3(LookFar.x, LookFar.y * k + PreviewAimUp, LookFar.z * k));
        }

        // ================= 3D 縮圖 (每格一件；靜態 bind-pose) — 同 ShopScreen 機制 =================

        private void BuildCardCam()
        {
            if (_cardCam != null) return;
            var go = new GameObject("WardrobeCardCam");
            _cardCam = go.AddComponent<Camera>();
            _cardCam.orthographic = true;
            _cardCam.orthographicSize = CardOrthoHalfW / ((float)CardRtW / CardRtH);
            _cardCam.nearClipPlane = CardNear; _cardCam.farClipPlane = CardFar;
            _cardCam.cullingMask = 1 << PreviewLayer;
            _cardCam.clearFlags = CameraClearFlags.SolidColor;
            _cardCam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            _cardCam.enabled = false;
        }

        // 一格縮圖：只是一張指向快取 RT 的 RawImage。RT 由 GetOrRenderThumb 建一次、之後沿用 → 換穿/重排不重建 3D。
        private void BuildCardPreview(int i, RectTransform card, ShopItem item)
        {
            if (_catalog == null || !_catalog.IsRenderable(item)) return;
            var rt = GetOrRenderThumb(item);
            if (rt == null) return;
            var img = new GameObject("thumb", typeof(RectTransform)).AddComponent<RawImage>();
            img.transform.SetParent(card, false);
            var irt = img.rectTransform;
            irt.anchorMin = irt.anchorMax = new Vector2(0.5f, 0.5f); irt.pivot = new Vector2(0.5f, 0.5f);
            irt.anchoredPosition = Vector2.zero; irt.sizeDelta = new Vector2(SlotW - 6f, SlotH - 6f);
            img.texture = rt; img.raycastTarget = false;
        }

        // 取得某件服飾的縮圖 RT：快取命中直接回；否則建一次 avatar → 渲染到新 RT → 立即銷毀 avatar (縮圖已進 RT) → 快取。
        private RenderTexture GetOrRenderThumb(ShopItem item)
        {
            if (_thumbCache.TryGetValue(item.Id, out var cached) && cached != null) return cached;
            GameObject root = null;
            RenderTexture rt = null;
            try
            {
                BuildCardCam();
                rt = new RenderTexture(CardRtW, CardRtH, 16, RenderTextureFormat.ARGB32) { name = "WardrobeThumb" + item.Id, antiAliasing = 2 };
                root = new GameObject("WardrobeThumbBuild");
                root.transform.position = ThumbBuildSpot;
                root.transform.rotation = Quaternion.Euler(0f, RoomMovement.FacingDegrees(2) + DefaultYaw, 0f);
                var av = SdoRoomAvatar.Build(root, PreviewLayer, false, ComposeCardParts(item), ShopHrcFor(_sex, item.EquipSlot), bindPoseNoIdle: true);
                if (av == null) { DestroyImmediate(root); rt.Release(); Destroy(rt); return null; }
                av.enabled = false;
                ApplyCardCutoutShader(root);
                if (item.EquipSlot != EquipSlot.Hair && item.EquipSlot != EquipSlot.Expression && item.EquipSlot != EquipSlot.Outfit)
                    HideSkinSubmeshes(root);

                Vector3 framePos, frameScale;
                var slot = item.EquipSlot;
                if (slot == EquipSlot.Wings)
                {
                    VisibleBounds(root, out float xmn, out float xmx, out float ymn, out float ymx);
                    float ofh = CardOrthoHalfW / ((float)CardRtW / CardRtH);
                    float os = Mathf.Min(CardOrthoHalfW * 2f * 0.9f / Mathf.Max(xmx - xmn, 1e-3f),
                                         ofh * 2f * 0.9f / Mathf.Max(ymx - ymn, 1e-3f));
                    frameScale = new Vector3(os, os, os);
                    framePos = new Vector3(-os * (xmn + xmx) * 0.5f, -os * (ymn + ymx) * 0.5f, 0f);
                }
                else
                {
                    var fr = FrameFor(slot, _sex);
                    frameScale = fr.Scale; framePos = fr.Pos;
                }

                root.transform.localScale = frameScale;
                root.transform.position = ThumbBuildSpot + framePos;
                root.transform.rotation = Quaternion.Euler(0f, RoomMovement.FacingDegrees(2) + DefaultYaw, 0f);
                _cardCam.orthographicSize = CardOrthoHalfW / ((float)CardRtW / CardRtH);
                _cardCam.transform.position = ThumbBuildSpot + new Vector3(0f, 0f, -CardEyeDist);
                _cardCam.transform.LookAt(ThumbBuildSpot);
                _cardCam.targetTexture = rt;
                _cardCam.Render();

                // 立即銷毀 (非延遲 Destroy)：同一次 RefreshGrid 會在同一個 ThumbBuildSpot 連續建多件縮圖，若延到幀末才銷毀，
                // 前一件的幾何還在原地 → 會疊進下一件的 render。縮圖已寫進 RT，幾何不再需要。
                DestroyImmediate(root);
                _thumbCache[item.Id] = rt;
                return rt;
            }
            catch (System.Exception e)
            {
                if (root != null) DestroyImmediate(root);
                if (rt != null) { rt.Release(); Destroy(rt); }
                Debug.LogWarning("[wardrobe] thumb " + item.Id + " failed (non-fatal): " + e.Message);
                return null;
            }
        }

        private void ClearThumbCache()
        {
            foreach (var kv in _thumbCache)
                if (kv.Value != null) { kv.Value.Release(); Destroy(kv.Value); }
            _thumbCache.Clear();
        }

        // ---- ported card helpers (verbatim from ShopScreen) ----

        private static string ShopHrcFor(ItemSex sex, EquipSlot slot)
        {
            bool male = sex == ItemSex.Male;
            switch (slot)
            {
                case EquipSlot.Shoes:  return "AVATAR/MSHOP0003.HRC";
                case EquipSlot.Gloves: return male ? "AVATAR/MSHOP0002.HRC" : "AVATAR/WSHOP0002.HRC";
                default:               return male ? "AVATAR/MSHOP0001.HRC" : "AVATAR/WSHOP0001.HRC";
            }
        }

        private string[] ComposeCardParts(ShopItem item)
        {
            var rel = item.MshRelPath;
            if (item.EquipSlot == EquipSlot.Hair)
            {
                var d = AvatarOutfit.DefaultsFor(_sex);
                if (d.TryGetValue(EquipSlot.Face, out var face) && !string.IsNullOrEmpty(face))
                    return new[] { face, rel };
            }
            return new[] { rel };
        }

        private struct CardFrame { public Vector3 Pos, Scale; }
        // 官方 per-slot 卡片取景表 (逐字取自 ShopScreen.FrameFor；之前 Bottom/Shoes 值是我自估的錯值 → 褲子/鞋子縮圖跑出框 #3)。
        private static CardFrame FrameFor(EquipSlot slot, ItemSex sex)
        {
            bool f = sex != ItemSex.Male;
            switch (slot)
            {
                case EquipSlot.Hair:
                case EquipSlot.Face:
                case EquipSlot.Expression: return new CardFrame { Pos = new Vector3(0, f ? -500 : -550, 0), Scale = new Vector3(9, 9, 9) };   // slot0 頭
                case EquipSlot.Necklace:   return new CardFrame { Pos = new Vector3(0, -400, 0), Scale = new Vector3(8, 8, 8) };   // 项链=頸部
                case EquipSlot.Top:
                case EquipSlot.OnePiece:   return new CardFrame { Pos = new Vector3(0, f ? -220 : -240, 0), Scale = new Vector3(5.5f, 5.5f, 5.5f) };   // slot2 胸
                case EquipSlot.Bottom:     return new CardFrame { Pos = new Vector3(0, -150, 0), Scale = new Vector3(5.5f, 5.5f, 5.5f) };   // slot3
                case EquipSlot.Gloves:     return new CardFrame { Pos = new Vector3(0, f ? -360 : -400, 0), Scale = new Vector3(10, 10, 10) };   // slot4 手
                case EquipSlot.Shoes:      return new CardFrame { Pos = new Vector3(f ? 15 : 20, f ? -60 : -50, 0), Scale = new Vector3(f ? 6.5f : 5.8f, 5.8f, 5.8f) };   // slot5 腳
                case EquipSlot.Glasses:    return new CardFrame { Pos = new Vector3(0, f ? -550 : -600, 0), Scale = new Vector3(10, 10, 10) };   // slot7 眼
                default:                   return new CardFrame { Pos = Vector3.zero, Scale = Vector3.one };
            }
        }

        private static void ApplyCardCutoutShader(GameObject root)
        {
            var cut = Shader.Find("Sdo/UnlitDoubleSided");
            if (cut == null) return;
            foreach (var mr in root.GetComponentsInChildren<MeshRenderer>())
                foreach (var m in mr.sharedMaterials)
                    if (m != null && m.mainTexture != null)
                    {
                        // OPAQUE 衣服(Unlit/Texture,含 alpha 壞掉被強制實心的布料)不可裁,否則卡片變透明線框(璀璨繁星 褲子)。
                        bool opaque = m.shader != null && m.shader.name == "Unlit/Texture";
                        m.shader = cut;
                        m.SetFloat("_Cutoff", opaque ? 0f : 0.05f);
                    }
        }

        private static bool IsSkinMat(Material m)
            => m != null && m.name.IndexOf("BASIC", System.StringComparison.OrdinalIgnoreCase) >= 0;

        private static void HideSkinSubmeshes(GameObject root)
        {
            bool anyCloth = false;
            foreach (var mr in root.GetComponentsInChildren<MeshRenderer>())
                foreach (var m in mr.sharedMaterials)
                    if (m != null && !IsSkinMat(m)) { anyCloth = true; break; }
            if (!anyCloth) return;
            foreach (var mr in root.GetComponentsInChildren<MeshRenderer>())
            {
                var mf = mr.GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                var mats = mr.sharedMaterials;
                if (mesh == null || mats == null) continue;
                if (mesh.subMeshCount <= 1)
                {
                    if (mats.Length > 0 && IsSkinMat(mats[0])) mr.enabled = false;
                }
                else
                {
                    for (int s = 0; s < mesh.subMeshCount && s < mats.Length; s++)
                        if (IsSkinMat(mats[s])) mesh.SetTriangles(new int[0], s);
                }
            }
        }

        private static void VisibleBounds(GameObject root, out float xmn, out float xmx, out float ymn, out float ymx)
        {
            bool any = false; xmn = xmx = ymn = ymx = 0f;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>())
            {
                var mr = mf.GetComponent<MeshRenderer>(); var mesh = mf.sharedMesh;
                if (mesh == null || mr == null || !mr.enabled) continue;
                var verts = mesh.vertices;
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    var tris = mesh.GetTriangles(s);
                    for (int k = 0; k < tris.Length; k++)
                    {
                        var v = verts[tris[k]];
                        if (!any) { xmn = xmx = v.x; ymn = ymx = v.y; any = true; }
                        else { if (v.x < xmn) xmn = v.x; if (v.x > xmx) xmx = v.x; if (v.y < ymn) ymn = v.y; if (v.y > ymx) ymx = v.y; }
                    }
                }
            }
        }

        // ================= helpers (同 ShopScreen 風格) =================

        private static Image AddArt(Transform parent, string name, Sprite s, float x, float y)
            => UIKit.AddSprite(parent, name, s, x, y);

        private Button SpriteBtn(Transform parent, string name, string normalAn, string pushedAn, float x, float y, UnityEngine.Events.UnityAction onClick = null, bool noSwap = false, string hoverAn = null)
        {
            var n = CabinetArt.An(normalAn); var p = CabinetArt.An(pushedAn);
            var h = hoverAn != null ? CabinetArt.An(hoverAn) : p;
            var btn = UIKit.AddSpriteButton(parent, name, n, h, p, x, y);
            if (noSwap) btn.transition = Selectable.Transition.None;
            if (n == null)
            {
                var img = btn.targetGraphic as Image; if (img != null) { img.color = new Color(1, 1, 1, 0.001f); img.raycastTarget = true; }
                var rt = btn.GetComponent<RectTransform>(); rt.sizeDelta = new Vector2(48, 20);
            }
            UiSfx.AttachClick(btn);   // 儲物櫃所有按鈕按下 → SE_0001 (使用者指定；#3)
            if (onClick != null) btn.onClick.AddListener(onClick);
            return btn;
        }

        private static TextMeshProUGUI TxtAt(Transform parent, string name, float x, float y, float w, float h, float size, Color color, TextAlignmentOptions align)
        {
            var t = UIKit.AddText(parent, name, "", size, color, align);
            var rt = t.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x, -y); rt.sizeDelta = new Vector2(w, h);
            return t;
        }

        private static void AddTrigData(EventTrigger t, EventTriggerType type, UnityEngine.Events.UnityAction<BaseEventData> fn)
        {
            var e = new EventTrigger.Entry { eventID = type };
            e.callback.AddListener(fn);
            t.triggers.Add(e);
        }

        private static Color Hex(uint argb)
            => new Color(((argb >> 16) & 0xff) / 255f, ((argb >> 8) & 0xff) / 255f, (argb & 0xff) / 255f, ((argb >> 24) & 0xff) / 255f);

        // 關櫃 whoosh + 縮小淡出 (Frameround.wav；參考選歌 SongSelectScreen.CloseTo)，動畫結束才真的隱藏。
        // 淡出期間凍結視窗互動，避免殘留點擊誤觸換穿/刪除。
        private void Close()
        {
            if (_closing) return;
            _closing = true;
            HideTip();
            if (_windowCg != null) _windowCg.blocksRaycasts = false;
            UiSfx.Play(UiSfx.FrameRound);
            if (_anim != null) _anim.PlayOut(() => { SetVisible(false); _closing = false; });
            else { SetVisible(false); _closing = false; }
        }

        private void SetVisible(bool on)
        {
            if (_cg != null) { _cg.alpha = on ? 1f : 0f; _cg.interactable = on; _cg.blocksRaycasts = on; }
            if (_cam != null) _cam.enabled = on;
            if (!on && _uiCam != null) { _uiCam.cullingMask = _savedUiMask; _uiCam = null; }
        }

        private void OnDestroy()
        {
            if (_avatarRoot != null) Destroy(_avatarRoot);
            if (_rt != null) { _rt.Release(); Destroy(_rt); }
            ClearThumbCache();
            if (_cardCam != null) Destroy(_cardCam.gameObject);
        }
    }
}
