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
    /// 商城 (SHOP) — 忠實重製官方 <c>SHOP.XML</c> 的 WinShop 主畫面。版面 (座標/美術) 逐字取自官方 XML (800×600, 左上原點,
    /// y-down)，用 <see cref="ShopArt"/> 載官方素材：官方整屏底 (Shop0.an) + 底部貨幣條 (Shop129.an) + 右側面板 (Shop7.an)；
    /// 頂端 精品屋 banner + 商店分頁 (专卖店/服装店/饰品店/道具店/伙伴店/礼包店) + 右上功能鈕列 + M/G 幣別切換；面板內是
    /// 部位子分頁 (套装/上装/…) + 服装风格 filter (全部/清纯/…) + 2×4 商品格；左側整屏 3D 試穿預覽 (官方 AvtWoman)。
    /// 只有 服装店 / 饰品店(发型) 有資料 (iteminfo.dat 只含服裝)。分頁亮/暗態實測官方 art：store 分頁 bgnormal=點亮(選中)、
    /// bghover=變暗(未選)；部位分頁反過來 bghover=亮底(選中)、bgnormal=純文字(未選)。
    /// 資料/價格 = <see cref="AvatarItemCatalog"/>，交易 = <see cref="ShopService"/> + <see cref="Wardrobe"/>。
    /// </summary>
    public sealed class ShopScreen : MonoBehaviour
    {
        private CanvasGroup _cg;
        private RectTransform _root;
        private GameSession _session;
        private AvatarItemCatalog _catalog;

        private ItemSex _sex = ItemSex.Female;
        private Store _store = Store.Clothing;      // 預設進 服装店
        private EquipSlot _slot = EquipSlot.Top;    // 預設 上装
        private int _styleIndex;                    // 服装风格 filter (視覺；iteminfo 無 style 欄)
        private bool _showM = true, _showG = false;  // 幣別 filter (M/G 切換鈕)：預設只看 M 幣 (M 暗/選中、G 亮/未選)
        private string _query = "";                 // 搜尋字串 (商品名)
        private int _page;                          // 逐列捲動:目前「最上方可見列」的索引 (非頁碼;每列 GridCols 格)

        private RectTransform _storeRow, _tabRow, _styleRow, _grid;
        private TextMeshProUGUI _gmine, _mmine, _hmine;
        private TMP_InputField _search;
        private Button _mBtn, _gBtn;
        private Button _maleBtn, _femaleBtn, _historyBtn;   // 男/女=CheckBox(選中顯示 pushed 暗態)；歷史鈕=toggle
        // 穿搭歷史 (#6)：按歷史鈕 → 格子改列「試穿過的衣服」(最近在前，去重)。選店/選部位/打字都會退出此模式。
        private bool _showHistory;
        private readonly List<ShopItem> _history = new List<ShopItem>();
        // 精品屋 banner 霓虹燈動畫 (#7)：jingpin1.an 有 18 幀，Update 以 JingpinFps 循環換 sprite。
        private Image _jingpinImg; private Sprite[] _jingpinFrames; private int _jingpinIdx; private float _jingpinTimer;
        private const float JingpinFps = 12f;
        // 右側捲軸 (官方 SHOP.XML <ScrollBarV name="shop_scroll" x="749" y="170" w="25" h="360">, Handle=Shop55.an)：
        // 可拖動 slider + 上下鈕 + 滾輪。軌道 groove 是背景 Shop0.an 畫死的,thumb 頭必須落在 y∈[170,530] 內;
        // 舊值 132/400 讓 thumb 頂端高出 groove 圓角上緣 (超出上/下邊界),改用官方 XML 實值對齊。
        private int _totalPages = 1;   // 可停靠的捲動位置數 (= maxTopRow+1;逐列捲動的步數,非頁數)
        private Image _scrollHandle;
        private const float ScrollX = 749f, ScrollTop = 180f, ScrollTrackH = 350f;

        // 圓球/圓角鈕的 alpha 命中門檻：α ≥ 此值的像素才算點到，讓判定跟著可見圖形走 (不吃透明四角)。
        // 0.5 落在球體羽化邊中段 → 命中邊界貼齊視覺邊緣;premult 貼圖 α 保留完好,取樣正確。
        private const float OrbAlphaHit = 0.5f;

        // 左側 3D 試穿預覽 (官方 AvtWoman/AvtMan 是整屏 AvtShow、人物落在左側；這裡取左半區、全身取景)
        private const int PreviewLayer = 12;
        private static readonly Vector3 PreviewSpot = new Vector3(2000f, 0f, 0f);
        // 官方 AvtShow 其實是整屏渲染、UI 疊在最上面 → 人物有翅膀/高髮不會被裁。remake 把預覽收進一張 RawImage,若只框到
        // 身體,翅膀就被 RT 上緣裁掉 (user #1)。因此把渲染區往上延伸到畫面頂 (多留 PreviewTopHeadroom 專門容納翅膀);
        // 身體投影用「偏移(oblique)視錐」保持完全不變 (見 BuildPreview),只是頭頂多留白、翅膀露出、落在 banner/面板等 UI
        // 之後 (Preview 在 hierarchy 早於那些 UI → 被畫在其下 = user 要的「在 ui 後面」)。
        private const float PreviewBodyH = 462f;        // 身體取景高度 (px；等同舊框高 → 身體大小/位置完全不變)
        private const float PreviewTopHeadroom = 112f;  // 往上延伸的翅膀留白 (px；框頂上移到畫面頂 y=0)
        private static readonly Vector2 PreviewRectPos = new Vector2(25f, 112f - PreviewTopHeadroom);          // 框頂上移 (避開右側 Shop7 面板 x≥312)
        private static readonly Vector2 PreviewRectSize = new Vector2(272f, PreviewBodyH + PreviewTopHeadroom); // 直立框 + 上方翅膀留白，RT 同尺寸避免拉伸
        private Camera _cam; private RenderTexture _rt; private RawImage _previewImg; private GameObject _avatarRoot;
        private string[] _tryOnOutfitParts;   // 非 null = 正在試穿整套 (該套組件 mesh),RebuildAvatar 直接用它覆蓋逐部位裝備
        private Camera _uiCam; private int _savedUiMask;   // 開商城時遮掉主 UI 相機的預覽層(12)，避免 3D 假人被畫平在 UI 上

        // 左側大預覽：顯示目前穿搭，可用滑鼠在人物上「按住拖動」轉身/抬頭 (官方 AvtShow_ApplyDragRotateZoom)。
        private float _dragAngle = -DefaultYaw;         // 左側「人物」預設朝右轉 30° (卡片衣物才朝左 +DefaultYaw)
        private float _pitchAngle;                      // pitch (X)，官方 clamp [-30,15]
        private const float DragDegPerPixel = 0.4f;     // 水平每拖 1px 轉幾度 (官方 0.4)
        private const float PitchDegPerPixel = 0.4f;    // 垂直每拖 1px 抬幾度 (官方線上 = 0.4，同 yaw；Frida 實測確認)
        private const float PitchMin = -30f, PitchMax = 15f;   // 官方線上 pitch clamp [-30,15]：可下看 30°、上抬 15°
        private const float DefaultYaw = 30f;           // 官方預設朝左轉 30° (實測 -30 是朝右 → 用 +30)
        private const float PivotY = 30f;               // 轉身/抬頭的 pivot = 身體中心/腰部 (官方是繞 display-node 原點，不是腳底)
        private float _previewFeetY;                     // 綁定姿勢最低頂點 (落地用)
        // 男女骨架身高不同 (MALE.HRC 比 FEMALE.HRC 高半顆頭)，但取景相機是固定的 → 高的男生會被同一個框放大成「大一號」。
        // 解法：量出每個模型實測身高，相機(eye/look/距離)整組按 身高/基準 線性放大 → 兩性在框裡「一樣大」(比例仍真實，只是填滿一致)。
        private const float RefHeight = 62f;             // 基準身高 = female 髮頂模型高度 (調這個值可整體放大/縮小「兩性」預覽人物)
        private const float MaleBodyRatio = 1.08f;        // 男標準身高 / 女 (男骨架較高;固定值,不受穿搭影響 → 男生依此正規化縮回,不會因較高而變大)
        private const float MaleSizeScale = 1.05f;        // 男生大小微調旋鈕：>1 男生變大、<1 變小 (=1 時男女一樣大)。太大就調小這個
        private float _previewHeight = RefHeight;        // 依「性別固定身高」正規化 (RebuildAvatar 設,非量穿搭 → 穿含帽/翅膀/高髮的套裝不會縮)
        // 官方左側大預覽 (AvtShow_ApplyDisplayPreset preset 1)：顯示節點 pos(0,-37,0)、scale 1.0、yaw 30°、相機「水平不俯仰」
        // 看世界 Y≈0。→ remake 腳落在 PreviewSpot.y=0,官方看 Y≈0 對應模型 Y≈37 (上胸/肩) → 相機水平(eye.y==look.y=37)
        // 瞄上胸,人物落畫面偏下、頭上留白 (修 user「相機太低/視角不對」——官方相機不俯仰,先前 32→29 是往下歪的)。
        private static readonly Vector3 EyeFar = new Vector3(0f, 37f, -140f), LookFar = new Vector3(0f, 27f, 0f);   // 全身取景 (水平,距離/fov 仍可調 zoom)

        // ---- 各商品卡的 3D 縮圖預覽 (官方 avtnormal_N)：卡內只顯示「那件衣物」本身 (膚色 submesh 藏掉；發型/眼鏡才帶頭)；
        //      滑上去 → 2D 放大 (縮圖 scale 1→2) + 3D 旋轉 (spin +5°/10ms≈300°/s、離開角度歸零)。衣物凍結,只在 hover 重畫 RT。
        //      取景=官方原汁：相機 ORTHOGRAPHIC (非透視!)、eye(0,0,-110)看世界 Y=0、ortho 半寬=64(WIDTH=128)、near5/far1000;
        //      模型節點依 per-slot 表放大(5.5~10x)+位移,讓該部位落到 Y≈0。正交下 scale 直接對應螢幕大小 → 官方那張 scale 表
        //      才會剛好塞滿(之前用透視 fov32 是頭爆框的根因)。表值 015_avatar_0042fe80.c:266-288 (PE 抽 DAT_00581be8/c14/
        //      c40/cc8);相機/正交 sdo.bin.c 反編譯 (Camera SingletonA eye -110 + MatrixOrthoLH width128 near5 far1000)。----
        private const int CardRtW = 132, CardRtH = 160;
        private const float CardEnlargeRate = 4.67f;     // 縮圖 localScale 放大速率 (官方 +0.07/15ms → ~0.21s)
        private const float CardEnlargeMax = 1.5f;        // hover 放大上限 (官方 2×,但 remake 卡片矮→2× 會爆出格子,降到 1.5×;可調)
        private const float CardSpinDegPerSec = 300f;    // hover 旋轉 (官方 +5°/10ms,frame-cap 60fps ≈ 300°/s;上限 500°/s@100fps)
        private const float CardEyeDist = 110f;          // 官方 view eye=(0,0,-110)。正交下距離不影響大小,只要在 near..far 內
        private const float CardOrthoHalfW = 64f;        // 官方 ortho 半寬 (WIDTH=128 world);半高由 RT aspect 推 → 方形像素
        private const float CardNear = 5f, CardFar = 1000f;   // 官方 ortho near/far
        private Camera _cardCam;                          // 共用一台相機，手動 Render() 逐張畫
        private readonly GameObject[] _cardAv = new GameObject[PerPage];
        private readonly RenderTexture[] _cardRT = new RenderTexture[PerPage];
        private readonly RawImage[] _cardImg = new RawImage[PerPage];
        private readonly float[] _cardScale = new float[PerPage];
        private readonly float[] _cardAngle = new float[PerPage];
        private readonly bool[] _cardNoSpin = new bool[PerPage];            // 眼鏡卡：靜態不旋轉 (user 指定 眼鏡不轉、只 hover 放大)
        private readonly bool[] _cardUvScroll = new bool[PerPage];          // 炫 hair 卡 (model 40000-49999)：貼圖 V 捲動 → RT 每幀重畫才看得到變色
        private readonly Vector3[] _cardFramePos = new Vector3[PerPage];    // 官方 per-slot 節點位移 (模型空間,y 為負把部位往下推)
        private readonly Vector3[] _cardFrameScale = new Vector3[PerPage];  // 官方 per-slot 節點縮放 (5.5~10x)
        private int _hoverCard = -1;
        private static Vector3 CardSpot(int i) => new Vector3(3000f + i * 400f, 0f, 0f);   // 各卡衣物散開放，避免互相入鏡

        // 官方卡片取景表 (015_avatar_0042fe80.c:266-288；PE 抽值 DAT_00581c40 女位置 / 00581cc8 男位置 / 00581be8 縮放 /
        // 00581c14 男 scaleX)。單位=模型空間 (身高~58,腳 Y=0)。EquipSlot→官方 slot：Hair/Face=0、Top/OnePiece=2、Bottom=3、
        // Gloves=4、Shoes=5、Glasses=7。設計意圖：正交相機看 Y≈0,scale×部位模型Y + posY ≈ 0 → 該部位落到畫面中心。
        private struct CardFrame { public Vector3 Pos, Scale; }
        private static CardFrame FrameFor(EquipSlot slot, ItemSex sex)
        {
            bool f = sex != ItemSex.Male;
            switch (slot)
            {
                // 女(f)=cc8 pos + c14 scaleX + be8 scaleYZ;男=c40 pos + be8 uniform。(0x25c=isMan,0→女→cc8;之前左右欄位標反了。)
                case EquipSlot.Hair:
                case EquipSlot.Face:
                case EquipSlot.Expression: return new CardFrame { Pos = new Vector3(0, f ? -500 : -550, 0), Scale = new Vector3(9, 9, 9) };   // slot0 頭
                case EquipSlot.Necklace: return new CardFrame { Pos = new Vector3(0, -400, 0), Scale = new Vector3(8, 8, 8) };   // 项链=頸部 (官方表未列,估)
                case EquipSlot.Top:
                case EquipSlot.OnePiece: return new CardFrame { Pos = new Vector3(0, f ? -220 : -240, 0), Scale = new Vector3(5.5f, 5.5f, 5.5f) };   // slot2 胸
                case EquipSlot.Bottom:   return new CardFrame { Pos = new Vector3(0, -150, 0),            Scale = new Vector3(5.5f, 5.5f, 5.5f) };   // slot3
                case EquipSlot.Gloves:   return new CardFrame { Pos = new Vector3(0, f ? -360 : -400, 0), Scale = new Vector3(10, 10, 10) };   // slot4 手
                case EquipSlot.Shoes:    return new CardFrame { Pos = new Vector3(f ? 15 : 20, f ? -60 : -50, 0), Scale = new Vector3(f ? 6.5f : 5.8f, 5.8f, 5.8f) };   // slot5 腳(女 scaleX6.5)
                case EquipSlot.Glasses:  return new CardFrame { Pos = new Vector3(0, f ? -550 : -600, 0), Scale = new Vector3(10, 10, 10) };   // slot7 眼
                default:                 return new CardFrame { Pos = Vector3.zero, Scale = Vector3.one };   // Outfit=slot10 全身 scale1
            }
        }
        // 8 個人形不同時建 (會卡頓) → 排進佇列，Update 每幀建 2 個 (漸進載入縮圖)。
        private struct PendingCard { public int I; public RectTransform Card; public ShopItem Item; }
        private readonly List<PendingCard> _pendingCards = new List<PendingCard>();

        // 官方色 (SHOP.XML)
        private static readonly Color CWhite = Color.white;      // 價格
        private static readonly Color CLv = Hex(0xff9d3e55);     // 等級
        private static readonly Color CStyle = Hex(0xff350856);  // 服装风格 未選文字 (官方深紫 #350856, #10)

        // ---- 頂端商店分頁 (SHOP.XML WinShop：Shop_0..Shop_29 @ y44 + jingpin banner @ y1)。
        //      Lit=bgnormal(點亮=選中)、Dim=bghover(變暗=未選) — 實測官方 art 得出，不是一般 normal/pushed 慣例。----
        private enum Store { Exclusive, Clothing, Cosmetology, Property, Shoppet, Giftpack }
        private struct StoreTab { public Store Id; public string Lit, Dim; public float X; }
        private static readonly StoreTab[] Stores =
        {
            new StoreTab{ Id=Store.Exclusive,   Lit="Shop18.an",  Dim="Shop19.an",  X=353 }, // 专卖店
            new StoreTab{ Id=Store.Clothing,    Lit="Shop21.an",  Dim="Shop22.an",  X=420 }, // 服装店
            new StoreTab{ Id=Store.Cosmetology, Lit="Shop24.an",  Dim="Shop25.an",  X=490 }, // 饰品店
            new StoreTab{ Id=Store.Property,    Lit="Shop27.an",  Dim="Shop28.an",  X=558 }, // 道具店
            new StoreTab{ Id=Store.Shoppet,     Lit="Shop186.an", Dim="Shop187.an", X=626 }, // 伙伴店
            new StoreTab{ Id=Store.Giftpack,    Lit="Shop216.an", Dim="Shop217.an", X=694 }, // 礼包店
        };

        // ---- 面板內部位子分頁 (SHOP.XML Clothing/Cosmetology window @ y100)。Slot==None = 擺出來但無資料 (點了沒反應)。
        //      Lit=bghover(亮底=選中)、Dim=bgnormal(純文字=未選)。----
        private struct SlotTab { public EquipSlot Slot; public string Lit, Dim; public float X; }
        private static readonly SlotTab[] ClothingTabs =
        {
            new SlotTab{ Slot=EquipSlot.Outfit,   Lit="Shop94.an", Dim="Shop93.an", X=325 }, // 套装 (=整套 OUTFIT/SET,非連身;離線無 setinfo→暫空)
            new SlotTab{ Slot=EquipSlot.Top,      Lit="Shop70.an", Dim="Shop69.an", X=380 }, // 上装 (含 上衣 Top + 連身 OnePiece,官方連身歸這裡)
            new SlotTab{ Slot=EquipSlot.Bottom,   Lit="Shop73.an", Dim="Shop72.an", X=435 }, // 下装
            new SlotTab{ Slot=EquipSlot.Shoes,    Lit="Shop82.an", Dim="Shop81.an", X=489 }, // 鞋子
            new SlotTab{ Slot=EquipSlot.Gloves,   Lit="Shop79.an", Dim="Shop78.an", X=543 }, // 手套
            new SlotTab{ Slot=EquipSlot.Glasses,  Lit="Shop97.an", Dim="Shop96.an", X=597 }, // 眼镜
        };
        private static readonly SlotTab[] CosmetologyTabs =
        {
            new SlotTab{ Slot=EquipSlot.Hair,       Lit="Shop76.an",  Dim="Shop75.an",  X=325 }, // 发型
            new SlotTab{ Slot=EquipSlot.Expression, Lit="Shop115.an", Dim="Shop114.an", X=379 }, // 表情 (cat6/106,292件)
            new SlotTab{ Slot=EquipSlot.Necklace,   Lit="Shop169.an", Dim="Shop168.an", X=434 }, // 项链 (cat9/109,30件)
            new SlotTab{ Slot=EquipSlot.Wings,      Lit="Shop204.an", Dim="Shop203.an", X=487 }, // 翅膀 (官方 CheckBox name="wing";cat8/108;mesh-only 的也列,見 AllMeshModels #3)
            new SlotTab{ Slot=EquipSlot.None,       Lit="Shop234.an", Dim="Shop233.an", X=541 }, // 尾巴 (離線無購買項,模型孤兒→維持 None)
            new SlotTab{ Slot=EquipSlot.None,       Lit="PateBtn_2.an", Dim="PateBtn_1.an", X=596 }, // 头饰 (官方 CheckBox name="pate";離線無資料→None)
            new SlotTab{ Slot=EquipSlot.None,       Lit="ShoulderBtn_2.an", Dim="ShoulderBtn_1.an", X=650 }, // 肩饰 (官方 name="shoulder";離線無資料→None)
        };
        private static SlotTab[] TabsFor(Store s)
        {
            switch (s)
            {
                case Store.Clothing: return ClothingTabs;
                case Store.Cosmetology: return CosmetologyTabs;
                default: return System.Array.Empty<SlotTab>();
            }
        }

        // ---- 服装风格 filter (SHOP.XML SubItemType0..7 @ y136；Shop68/67 是無字底 pill → 名稱疊字)。全繁體，且依部位不同 (#2)：
        //      鞋子=靴子那組、髮型=長髮/短髮那組、上装/下装/套装=通用那組；手套/眼镜/表情/项链/翅膀/尾巴/头饰/肩饰 官方沒有風格列→留空。----
        private static readonly string[] StyleDefault = { "全部", "清純", "酷炫", "性感", "制服", "夢幻", "時尚", "休閒" };
        private static readonly string[] StyleShoes   = { "全部", "靴子", "性感", "清純", "夢幻", "時尚", "休閒" };
        private static readonly string[] StyleHair    = { "全部", "長髮", "短髮", "配飾", "夢幻" };
        private static string[] StyleNamesFor(EquipSlot slot)
        {
            switch (slot)
            {
                case EquipSlot.Shoes: return StyleShoes;
                case EquipSlot.Hair:  return StyleHair;
                case EquipSlot.Top:
                case EquipSlot.Bottom:
                case EquipSlot.OnePiece:
                case EquipSlot.Outfit: return StyleDefault;
                default: return System.Array.Empty<string>();   // 手套/眼镜/表情/项链/翅膀/尾巴/头饰/肩饰 → 無風格列
            }
        }
        private static readonly float[] StyleX = { 330, 383, 436, 489, 540, 591, 644, 696 };

        private const int PerPage = 8;   // 縮圖陣列上限 (小卡一頁8;大卡一頁2,用索引 0~1)
        private const int GridCols = 2;  // 商品格欄數 (小卡 2×4、大卡 2×1 皆 2 欄)；捲軸「逐列」捲動用

        // 商品格版面：一般 tab = 官方 normalwin 2×4 八張小卡 (Shop5.an 210×101);套装 tab = 官方 suitwin 兩張大卡
        // (Shop145.an 204×388,全身人形)。座標皆 SHOP.XML 實值 (800×600,左上原點,y-down)。RefreshGrid 依 _slot 選 (_L)。
        private struct CardLayout
        {
            public int PerPage;
            public Vector2[] Pos;            // 卡片左上 (canvas y-down)
            public Vector2 Size;             // 卡片尺寸
            public string FrameAn;           // 卡底美術
            public int RtW, RtH;             // 縮圖 RT 尺寸
            public Vector2 AvCenter, AvSize; // avatar RawImage 卡內中心(y-down)+尺寸 (center pivot,由中心 hover 放大)
            public Vector2 NamePos, PricePos, LvPos;
            public float TextW;              // name/price/lv 文字框寬
            public Vector2 FitPos, BuyPos, GiftPos;
            public float NameFont; public TextAlignmentOptions Align; public Color NameColor;
        }
        private static readonly CardLayout SmallLayout = new CardLayout
        {
            PerPage = 8,
            Pos = new[]{ new Vector2(331,160), new Vector2(537,160), new Vector2(331,257), new Vector2(537,257),
                         new Vector2(331,354), new Vector2(537,354), new Vector2(331,451), new Vector2(537,451) },
            Size = new Vector2(210,101), FrameAn = "Shop5.an", RtW = 132, RtH = 160,
            AvCenter = new Vector2(46,-50), AvSize = new Vector2(72,88),
            NamePos = new Vector2(99,13), PricePos = new Vector2(100,36), LvPos = new Vector2(100,52), TextW = 100,
            FitPos = new Vector2(90,66), BuyPos = new Vector2(120,66), GiftPos = new Vector2(159,66),
            NameFont = 12, Align = TextAlignmentOptions.Left, NameColor = Color.white,
        };
        private static readonly CardLayout BigLayout = new CardLayout
        {
            PerPage = 2,
            Pos = new[]{ new Vector2(337,160), new Vector2(539,160) },
            Size = new Vector2(204,388), FrameAn = "Shop145.an", RtW = 300, RtH = 480,   // 2× 150×240 (AA)
            AvCenter = new Vector2(29 + 75, -(44 + 120)), AvSize = new Vector2(150,240),  // 官方全身 150×240 @ 卡內(29,44)
            NamePos = new Vector2(51,16), PricePos = new Vector2(51,315), LvPos = new Vector2(51,334), TextW = 102,
            FitPos = new Vector2(85,356), BuyPos = new Vector2(45,355), GiftPos = new Vector2(118,355),
            NameFont = 12, Align = TextAlignmentOptions.Center, NameColor = Hex(0xffc7405a),
        };
        private CardLayout _L = SmallLayout;   // 當前版面 (RefreshGrid 設)

        public void Build(RectTransform parent, GameSession session)
        {
            _session = session;
            _root = UIKit.NewRect(parent, "Shop");
            UIKit.Stretch(_root);
            _cg = _root.gameObject.AddComponent<CanvasGroup>();

            // 商城有自己的整屏背景 (官方 Shop0.an @ 0,0)，不借用房間當底：先鋪不透明黑幕擋掉後面的房間 3D，
            // 再蓋官方全屏底圖 + 底部貨幣條，最後才是右側面板。就算 Shop0.an 有透空區房間也不會透出來。
            var solid = UIKit.AddImage(_root, "Backdrop", Color.black, true);   // raycast=true → 吃掉點擊，不點穿到房間
            UIKit.Stretch(solid.rectTransform);
            AddArt(_root, "ShopBG", ShopArt.An("Shop0.an"), 0, 0);              // 官方整屏背景
            AddArt(_root, "BottomBar", ShopArt.An("Shop129.an"), 0, 534);       // 官方底部貨幣條

            // 左側 3D 試穿預覽 (官方 AvtWoman：整屏、人物落左側 → 這裡取左半區、全身取景)
            var pv = UIKit.NewRect(_root, "Preview");
            pv.anchorMin = pv.anchorMax = new Vector2(0, 1); pv.pivot = new Vector2(0, 1);
            pv.anchoredPosition = new Vector2(PreviewRectPos.x, -PreviewRectPos.y); pv.sizeDelta = PreviewRectSize;
            _previewImg = pv.gameObject.AddComponent<RawImage>(); _previewImg.color = Color.white; _previewImg.raycastTarget = true;   // 可在人物上拖動轉身
            AddTrigData(pv.gameObject.AddComponent<EventTrigger>(), EventTriggerType.Drag, OnPreviewDrag);

            // 官方右側面板 Shop7.an @ (312,95)
            AddArt(_root, "Panel", ShopArt.An("Shop7.an"), 312, 95);

            // 精品屋 banner (jingpin @ 250,1)：18 幀霓虹燈，Update 循環播 (#7)。+ 商店分頁列容器 (內容在 Refresh 依 _store 重畫)
            _jingpinFrames = ShopArt.AnFrames("jingpin1.an");
            _jingpinImg = AddArt(_root, "Jingpin", _jingpinFrames.Length > 0 ? _jingpinFrames[0] : ShopArt.An("jingpin1.an"), 250, 1);
            _storeRow = UIKit.NewRect(_root, "StoreRow"); UIKit.Stretch(_storeRow);

            // 右上功能鈕列 (SHOP.XML)。除了 shopexit(關閉)，其餘尚無功能 → 按了沒反應 (不彈提示)。
            // shoprank(houseangel 藍底白「?」) 離線無排行 → 移除 (#9)。
            SpriteBtn(_root, "shopfamily", "BtnHeadFamily_1.an", "BtnHeadFamily_3.an", 652, 7);
            SpriteBtn(_root, "shopwedding","BtnHeadRank_1.an",   "BtnHeadRank_3.an",   689, 7);
            SpriteBtn(_root, "shophouse",  "BtnHeadLove_1.an",   "BtnHeadLove_3.an",   725, 7);
            // 關商城：漸黑 → loading → 漸亮，露出底下的房間/男女選擇畫面（同進商城的進出效果；房間仍在底下故不觸發滑入）。
            SpriteBtn(_root, "shopexit",   "BtnHeadReturn_1.an", "BtnHeadReturn_3.an", 760, 7, () => ScreenTransition.Run(() => SetVisible(false)));

            // M/G 幣別切換 (SHOP.XML：M @ 105,75 / G @ 154,75)。切換 → 商品格依幣別過濾。
            // M幣/G幣 = 互斥幣別選擇 (按 M 只看 M 幣、按 G 只看 G 幣)。noSwap=true 關掉 Selectable 的 SpriteSwap,否則它會把
            // RefreshToggles 設的「選中=暗(Shop12/14 y0)」蓋回 idle 的「normal=亮(Shop11/13 y24)」→ 選中的看起來反而是亮的
            // (user:「按下去要變暗」「亮暗反了」)。美術實查:SHOP11/13=M/G亮、SHOP12/14=M/G暗。
            _mBtn = SpriteBtn(_root, "Mtoggle", "Shop11.an", "Shop12.an", 105, 75, () => { _showM = true; _showG = false; _page = 0; Refresh(); }, noSwap: true);
            _gBtn = SpriteBtn(_root, "Gtoggle", "Shop13.an", "Shop14.an", 154, 75, () => { _showG = true; _showM = false; _page = 0; Refresh(); }, noSwap: true);

            // 性別切換 (SHOP.XML：male @ 0,510 / female @ 40,510)。官方是 CheckBox：選中顯示 bgpushed(暗態)、未選 bgnormal(亮)
            // — 同 M/G 幣別 (選中=暗)。noSwap + RefreshToggles 依 _sex 換 sprite (修 user #5「男女亮暗相反」：舊版選中的性別反而亮)。
            _maleBtn   = SpriteBtn(_root, "male",   "Shop45.an", "Shop46.an", 0,  510, () => SwitchGender(ItemSex.Male),   noSwap: true, solo: true, hoverSfx: UiSfx.ButtonFloat, alphaHit: OrbAlphaHit);
            _femaleBtn = SpriteBtn(_root, "female", "Shop47.an", "Shop48.an", 40, 510, () => SwitchGender(ItemSex.Female), noSwap: true, solo: true, hoverSfx: UiSfx.ButtonFloat, alphaHit: OrbAlphaHit);

            // 復原穿搭 (SHOP.XML undochange @ 74,532，紅色 ↻)：清掉試穿、還原成預設穿搭。hover 補官方亮幀 Shop16 (#5)。
            // solo:true → 自貼圖載入,去掉 atlas 鄰居滲出的白邊 (#5 左下角按鈕白邊)。
            SpriteBtn(_root, "reset", "Shop15.an", "Shop17.an", 74, 532, DoResetOutfit, hoverAn: "Shop16.an", solo: true, alphaHit: OrbAlphaHit);

            // 底部：全身购买 (買下整套穿搭，有 info) / 购物车 (尚無功能) / 快速充值 (右下橘鈕)。hover 各補官方亮幀 (#3/#5)。
            // 全身购买 orb 用 solo (自貼圖) 去掉 atlas 鄰居滲出的白邊 (#1)。快速充值離線無金流後端 → 直接把三種幣一次充滿 (試玩用)。
            // alphaHit：這幾顆是圓球/圓角形,命中判定跟著可見像素走,不吃透明四角 (使用者回報「部分區域不在按鈕裡面也會觸發」)。
            SpriteBtn(_root, "buyall",   "Shop174.an",  "Shop176.an",  0,  543, DoBuyAll, hoverAn: "Shop175.an", solo: true, alphaHit: OrbAlphaHit);
            SpriteBtn(_root, "cart",     "Shop206.an",  "Shop208.an",  98, 565, hoverAn: "Shop207.an", hoverSfx: UiSfx.ButtonFloat, alphaHit: OrbAlphaHit);
            SpriteBtn(_root, "recharge", "chongzhi1.an","chongzhi3.an", 678,571, DoRecharge, hoverAn: "chongzhi2.an", hoverSfx: UiSfx.ButtonFloat, alphaHit: OrbAlphaHit);

            // 搜尋框 (SHOP.XML SearchEdit @ 154,579) + 放大鏡 (chakan/Shop199 @ 270,576)。打字即時過濾商品名 (跨部位)。
            // 依需求：無 placeholder 文字、無半透明底 (透明但仍可點 focus)、白字白光標。
            _search = UIKit.AddInputField(_root, "SearchEdit", "", 13);
            var srt = _search.GetComponent<RectTransform>();
            srt.anchorMin = srt.anchorMax = new Vector2(0, 1); srt.pivot = new Vector2(0, 1);
            srt.anchoredPosition = new Vector2(154, -576); srt.sizeDelta = new Vector2(108, 18);
            if (_search.targetGraphic is Image sbg) sbg.color = new Color(1, 1, 1, 0f);   // 透明底 (仍 raycast → 可點 focus)
            if (_search.textComponent != null) _search.textComponent.color = Color.white; // 白字
            if (_search.placeholder is TMP_Text ph) ph.text = "";                          // 不寫「搜尋商品名」
            _search.customCaretColor = true; _search.caretColor = Color.white; _search.caretWidth = 2;   // 白光標
            _search.onValueChanged.AddListener(s => { _query = s; if (_showHistory) { _showHistory = false; SetBtnSprite(_historyBtn, "shop171.an"); } _page = 0; RefreshGrid(); });
            SpriteBtn(_root, "search", "Shop199.an", "Shop201.an", 270, 576, () => { _page = 0; RefreshGrid(); }, hoverAn: "Shop200.an", hoverSfx: UiSfx.ButtonFloat);
            // 穿搭歷史鈕 (SHOP.XML Shop_4 @ 304,568；T恤+循環箭頭)：toggle → 格子改列試穿過的衣服 (#6)。noSwap，選中顯示 pushed。
            _historyBtn = SpriteBtn(_root, "history", "shop171.an", "shop173.an", 304, 568, () =>
            {
                _showHistory = !_showHistory;
                if (_showHistory && _search != null) { _query = ""; _search.SetTextWithoutNotify(""); }
                _page = 0; Refresh();
            }, noSwap: true, hoverSfx: UiSfx.ButtonFloat);

            // 面板內容容器 (依 store/slot/style 重畫)
            _tabRow = UIKit.NewRect(_root, "SubTabs"); UIKit.Stretch(_tabRow);
            _styleRow = UIKit.NewRect(_root, "StyleTabs"); UIKit.Stretch(_styleRow);
            _grid = UIKit.NewRect(_root, "Grid"); UIKit.Stretch(_grid);

            // 底部貨幣數量 (SHOP.XML：Hmine 400 / Mmine 502 / Gmine 606 @ y580)。條上已有 H/M/G 圖示 → 只寫數字。
            _hmine = TxtAt(_root, "Hmine", 400, 580, 60, 12, 12, CWhite, TextAlignmentOptions.Left);
            _mmine = TxtAt(_root, "Mmine", 502, 580, 60, 12, 12, CWhite, TextAlignmentOptions.Left);
            _gmine = TxtAt(_root, "Gmine", 606, 580, 60, 12, 12, CWhite, TextAlignmentOptions.Left);

            // 右側捲軸 = 可拖動的 Handle (Shop55.an，官方 slider) + 上下端點鈕 (官方 empty.an 隱形可點) + 滾輪 (見 Update)。
            SpriteBtn(_root, "pageup",   "empty.an", "empty.an", ScrollX, ScrollTop - 18f,          () => PageBy(-1));
            SpriteBtn(_root, "pagedown", "empty.an", "empty.an", ScrollX, ScrollTop + ScrollTrackH, () => PageBy(1));
            _scrollHandle = UIKit.AddSprite(_root, "ScrollHandle", ShopArt.An("Shop55.an"), ScrollX, ScrollTop, raycast: true);
            AddTrigData(_scrollHandle.gameObject.AddComponent<EventTrigger>(), EventTriggerType.Drag, OnScrollDrag);

            SetVisible(false);
        }

        /// 商城 modal 是否正顯示中（疊在房間/男女選擇畫面上）。底下畫面用它判斷 ESC 該不該由自己處理（見 GenderSelectScreen）。
        public bool IsOpen => _cam != null && _cam.enabled;

        public void Open()
        {
            _catalog = AvatarItemCatalog.Instance;
            _sex = _session != null && _session.Gender == 1 ? ItemSex.Male : ItemSex.Female;   // 依 session 性別開對應性別商城 (開場/房間皆是)
            _store = Store.Clothing; _slot = EquipSlot.Top; _page = 0;   // 每次進來預設 focus 服装店 / 上装
            _showM = true; _showG = false;   // 每次進來預設只顯示 M 幣清單 (M 暗/選中、G 亮/未選)
            if (_search != null) { _search.SetTextWithoutNotify(""); }
            _query = "";
            _dragAngle = -DefaultYaw; _pitchAngle = 0f;   // 人物預設朝右 30°
            // 對齊 active 帳號到 session 性別 (修「從男女選擇畫面直接開商城」時 active 帳號可能還停在別的性別),並載入該帳號
            // 的「實際穿戴」+ 錢包 (清掉上次的試穿殘留)；試穿不落地,購買才改真的穿搭。
            if (_session != null) ActivateGenderProfile(_session.Gender);
            _tryOnOutfitParts = null;
            BuildPreview();
            // 遮掉主 UI 相機的預覽層(12)，避免 3D 假人被主相機畫平到 UI 上 (同 RoomScreen 對場景/頭像層的做法)。
            var ui = FrontendApp.Instance != null ? FrontendApp.Instance.UiCam : null;
            if (ui != null) { _uiCam = ui; _savedUiMask = ui.cullingMask; ui.cullingMask &= ~(1 << PreviewLayer); }
            RebuildAvatar();
            SetVisible(true);   // 先顯示 (alpha=1) 再 Refresh：分頁按鈕在「可見」狀態下建立，避免首幀亮暗未套用
            Refresh();
        }

        // 切性別 == 切角色帳號 (女→00000000 / 男→00000001，與 GenderSelectScreen 同一套 profile)。使用者選定：在商城按
        // 男/女不再只是換瀏覽/預覽,而是真的切換登入角色 —— 用該性別帳號的錢包買、存進該帳號衣櫃、離開商城後房間/遊戲也
        // 是該性別。因為女裝綁女骨架、男裝綁男骨架,唯有連角色一起切,買到的衣服才穿得上 (修「切女角只能買同性別的衣服」)。
        private void SwitchGender(ItemSex sex)
        {
            if (_session == null || _sex == sex) return;   // 已是該性別 → 不重載
            _sex = sex;
            ActivateGenderProfile(sex == ItemSex.Male ? 1 : 0);   // 換帳號 → 換錢包/擁有/穿搭/身分
            _tryOnOutfitParts = null;
            _page = 0; _showHistory = false;
            RebuildAvatar();                   // 左側預覽換成新帳號的穿搭 (新性別骨架)
            Refresh();                         // 商品格/幣別/性別鈕亮暗/底條錢包 依新帳號重畫
            Nav.RefreshRoomAvatar?.Invoke();   // 房間背後的本機 3D avatar + 頭貼同步換新性別 (RoomScreen.RefreshLocalAvatar 讀 session.Gender)
        }

        // 對齊 active 使用者帳號到指定性別,並載入該帳號的錢包 + 擁有 + 穿搭。只有「帳號真的換了」才重種房間面板預設/同步
        // 身分 (避免從房間開商城時,把玩家在房間改過的面板值被重置)；RoomConfig 由 SetActive 內部重載。
        private void ActivateGenderProfile(int gender)
        {
            _session.Gender = gender;
            string id = Sdo.Settings.ProfileManager.SeededIdForGender(gender);
            var active = Sdo.Settings.ProfileManager.Active;
            if (active == null || active.id != id)
            {
                Sdo.Settings.ProfileManager.SetActive(id);   // 載入該帳號 profile(衣服)；收藏/設定是全帳號共用不重載
                var p = Sdo.Settings.ProfileManager.Active;
                if (p != null) { _session.LocalPlayerId = p.id; _session.LocalPlayerName = p.name; }
                _session.SeedRoomDefaults();                 // 換帳號才重種房間面板預設 (per-user)
            }
            WardrobeStore.Load(_session);   // 一律重載該帳號錢包/擁有/穿搭 (清掉上一帳號殘留 + 商城試穿)
        }

        private void Refresh()
        {
            RefreshStoreRow();
            RefreshSubTabs();
            RefreshStyleRow();
            RefreshGrid();
            UpdateWallet();
            RefreshToggles();
        }

        // ---- 頂端商店分頁 (亮=選中 Lit / 暗=未選 Dim；hover 一律點亮) ----
        private void RefreshStoreRow()
        {
            UIKit.Clear(_storeRow);
            foreach (var st in Stores)
            {
                bool active = st.Id == _store;
                var id = st.Id;
                SpriteBtn(_storeRow, "store" + st.Id, active ? st.Lit : st.Dim, active ? st.Lit : st.Dim, st.X, 44, () => SelectStore(id), noSwap: true);
            }
        }

        private void SelectStore(Store s)
        {
            _store = s;
            _page = 0;
            _showHistory = false;   // 換店 → 退出穿搭歷史模式 (#6)
            // 服装店預設落在「上装」(有貨),不落在「套装」(=OUTFIT/SET,離線無 setinfo 資料→空);其餘店取第一個有效分頁。
            _slot = s == Store.Clothing ? EquipSlot.Top : FirstRealSlot(TabsFor(s));
            Refresh();
        }

        private static EquipSlot FirstRealSlot(SlotTab[] tabs)
        {
            foreach (var t in tabs) if (t.Slot != EquipSlot.None) return t.Slot;
            return EquipSlot.None;
        }

        // ---- 面板內部位子分頁 (亮底=選中 Lit / 純文字=未選 Dim；hover 一律點亮；空分類點了沒反應) ----
        private void RefreshSubTabs()
        {
            UIKit.Clear(_tabRow);
            foreach (var t in TabsFor(_store))
            {
                bool active = t.Slot != EquipSlot.None && t.Slot == _slot;
                var slot = t.Slot;
                UnityEngine.Events.UnityAction click = slot == EquipSlot.None
                    ? (UnityEngine.Events.UnityAction)null                                  // 無資料分類 → 按了沒反應
                    : () => { _slot = slot; _page = 0; _showHistory = false; Refresh(); };  // 選部位 → 退出穿搭歷史 (#6)
                SpriteBtn(_tabRow, "tab" + t.X, active ? t.Lit : t.Dim, active ? t.Lit : t.Dim, t.X, 100, click, noSwap: true);
            }
        }

        // ---- 服装风格 filter (視覺；名稱疊在 pill 上。選中=Shop67 亮 pill / 未選=Shop68 透空只留字)。名稱依部位不同、留空者不畫 (#2) ----
        private void RefreshStyleRow()
        {
            UIKit.Clear(_styleRow);
            if (_showHistory) return;                                               // 歷史模式不顯示風格列
            if (_store != Store.Clothing && _store != Store.Cosmetology) return;    // 只在服裝/飾品顯示
            var names = StyleNamesFor(_slot);
            if (names.Length == 0) return;                                          // 手套/眼镜/表情/项链/翅膀… 官方沒有風格列
            if (_styleIndex >= names.Length) _styleIndex = 0;
            for (int i = 0; i < names.Length && i < StyleX.Length; i++)
            {
                int idx = i; bool active = i == _styleIndex;
                SpriteBtn(_styleRow, "style" + i, active ? "Shop67.an" : "Shop68.an", active ? "Shop67.an" : "Shop68.an", StyleX[i], 136, () => { _styleIndex = idx; Refresh(); }, noSwap: true);
                var t = TxtAt(_styleRow, "styletx" + i, StyleX[i], 138, 50, 16, 14, active ? Color.white : CStyle, TextAlignmentOptions.Center);
                t.text = names[i];
            }
        }

        // 收合代表挑選：永久(-1) 最優先,其次天數大者。
        private static int TierRank(ShopItem it) => it.DurationDays < 0 ? int.MaxValue : it.DurationDays;

        // 一個部位分頁要顯示的商品：官方「上装」= 上衣 Top + 連身 OnePiece 併在一起 (連身不歸套装;套装=整套 OUTFIT/SET)。
        // AllMeshModels = 該部位所有商品:髮型/上衣/下裝/鞋子(及翅膀/表情) 連只有 mesh、iteminfo 沒登錄名字的也帶出來
        // (序號當名字、100M；user);其餘部位 AllMeshModels 等同原本的 Group(僅 iteminfo 有名的)。
        // 搜尋涵蓋的所有部位 (服装店 + 饰品店 有實際商品的頁)。SlotItems 已含無名 mesh 道具 + 有快取 (AllMeshModels)，
        // 故每次搜尋 union 這 9 個部位很便宜。讓「不同類型只要名字對都搜得到」(user)。
        private static readonly EquipSlot[] SearchSlots =
        {
            EquipSlot.Outfit, EquipSlot.Hair, EquipSlot.Top, EquipSlot.Bottom, EquipSlot.Gloves, EquipSlot.Shoes,
            EquipSlot.Glasses, EquipSlot.Expression, EquipSlot.Necklace, EquipSlot.Wings,
        };

        private IEnumerable<ShopItem> SearchSource()
        {
            foreach (var slot in SearchSlots)
                foreach (var it in SlotItems(slot))
                    yield return it;
        }

        private IEnumerable<ShopItem> SlotItems(EquipSlot slot)
        {
            foreach (var it in _catalog.AllMeshModels(_sex, slot)) yield return it;
            if (slot == EquipSlot.Top)
                foreach (var it in _catalog.Group(_sex, EquipSlot.OnePiece)) yield return it;
        }

        private void RefreshGrid()
        {
            DestroyCardPreviews();   // 清掉上一頁的 3D 縮圖人形/RT (RawImage 隨 _grid.Clear 一起清)
            UIKit.Clear(_grid);
            if (_catalog == null) { _totalPages = 1; UpdateScrollHandle(); return; }

            bool searching = !string.IsNullOrEmpty(_query);
            bool meshSlot = _slot == EquipSlot.Wings || _slot == EquipSlot.Expression;        // 翅膀/表情 = 只有 mesh 沒名字 (6 碼當名)
            IEnumerable<ShopItem> src = _showHistory ? (IEnumerable<ShopItem>)_history        // 穿搭歷史 → 只列試穿過的 (#6)
                                       : searching ? SearchSource()                          // 搜尋 → 跨所有部位(含無名 6 碼道具),只要名字/id 對就找得到
                                       : _slot == EquipSlot.None ? System.Array.Empty<ShopItem>()
                                       : meshSlot ? _catalog.AllMeshModels(_sex, _slot)       // 翅膀/表情 → 連只有 mesh 沒名字的也列 (100M/序號當名字)
                                       : SlotItems(_slot);

            // 搜尋字串折成簡體一次 (使用者打繁體也搜得到簡體名;數字 id 不受影響)。
            string qSimp = searching ? Sdo.UI.Util.TradSimp.ToSimp(_query.Trim()) : null;
            var items = new List<ShopItem>();
            foreach (var it in src)
            {
                if (ItemTypes.GenderOf(it.Category, it.Name) != _sex) continue;                // 只顯示目前性別 (GenderOf 修 cat203 套装)
                if (!_catalog.IsRenderable(it)) continue;                                      // 無模型(未 extract)的 item 直接隱藏,不列出 (user 指定)
                // 搜尋比對：商品名(繁→簡折疊) OR 6碼 modelId OR 物品 id → 無名道具(只顯示 6 碼,如 003598)也搜得到。
                if (searching)
                {
                    bool hit = Sdo.UI.Util.TradSimp.ToSimp(it.Name ?? "").IndexOf(qSimp, System.StringComparison.OrdinalIgnoreCase) >= 0
                               || it.ModelId.ToString("D6").Contains(qSimp)
                               || it.ModelId.ToString().Contains(qSimp)
                               || it.Id.ToString().Contains(qSimp);
                    if (!hit) continue;
                }
                // 幣別 filter 只在「瀏覽分頁」時套用;搜尋時跨幣別找(否則 priceCat 0=Points/G 幣的 夏日新娘 個別部件在預設 M 頁被擋掉,搜不到)。
                if (!searching)
                { string z = CurrencyZh(it.Currency); if ((z == "G" && !_showG) || (z == "M" && !_showM)) continue; }
                items.Add(it);
            }
            // 同一件商品在 iteminfo 有 7天/30天/永久 三筆 (ModelId 相同,只 Id/Duration/Price 不同,價 1×/2×/6×) → 官方一件
            // 一張卡。收合：以 (Category,ModelId) 為鍵,保留永久(-1)那筆為代表 (無永久取天數最長);維持首見順序。所有 tab 皆適用
            // (衣服也是三筆一件)。AvatarItemCatalog 仍保留全部檔位 → ById/Owns/購買各時效仍可解析。
            if (items.Count > 1)
            {
                var rep = new Dictionary<(int, int), int>(items.Count);
                var kept = new List<ShopItem>(items.Count);
                foreach (var it in items)
                {
                    var key = (it.Category, it.ModelId);
                    if (!rep.TryGetValue(key, out var ki)) { rep[key] = kept.Count; kept.Add(it); }
                    else if (TierRank(it) > TierRank(kept[ki])) kept[ki] = it;   // 永久 > 30天 > 7天
                }
                items = kept;
            }
            // 所有瀏覽分頁一律依 ModelId 真正降冪「合併」排序 → 有名/無名(序號)依序號穿插,不再把無名整塊堆到第一頁。
            // 之前只有 上装 這樣做,其餘 tab 用 Reverse:但 AllMeshModels 把無名 extras append 在有名之後 (那裡是升冪),
            // Reverse 只翻轉整條 → 無名區塊被整塊翻到最前面 (user 回報「第一頁都是沒名字的服裝」)。改成全部 Sort 即真穿插。
            if (_showHistory) { }                        // 歷史：保留 _history 的「最近在前」順序，不 reverse/排序
            else if (!searching) items.Sort((a, b) => b.ModelId.CompareTo(a.ModelId));   // 瀏覽：ModelId 降冪合併 (含 上装/連身)
            else items.Reverse();                        // 搜尋結果：跨部位混合,維持原本反轉行為

            // 套装 tab → 官方 suitwin 大卡 (2張);其餘小卡 (8張)。搜尋時是跨部位混合結果 → 一律小卡 (使用者:套装 tab 搜尋要小格)。
            _L = (!_showHistory && !searching && _slot == EquipSlot.Outfit) ? BigLayout : SmallLayout;

            // 捲軸改「逐列」捲動 (user)：往下一單位只把最上一列 (GridCols 格) 捲出、底部補進新的一列，
            // 而非整頁 8 格全換。→ _page 現在代表「最上方可見列」的索引 (非頁碼)，每步 = 1 列 = GridCols 件；
            // _totalPages = 可停靠的捲動位置數 (含頂端，最多捲到「最後一列貼齊底部」)。
            int cols = GridCols;                                   // 兩種版面都是 2 欄 (小卡 2×4、大卡 2×1)
            int visRows = Mathf.Max(1, _L.PerPage / cols);         // 一次看得到幾列 (小卡 4、大卡 1)
            int totalRows = Mathf.Max(1, (items.Count + cols - 1) / cols);
            int maxTopRow = Mathf.Max(0, totalRows - visRows);     // 最後一列貼齊底部時的最上列索引
            _page = Mathf.Clamp(_page, 0, maxTopRow);
            _totalPages = maxTopRow + 1;
            UpdateScrollHandle();
            int start = _page * cols;                              // 首格 = 最上列 × 欄數 → 逐列滑動

            for (int i = 0; i < _L.PerPage; i++)
            {
                int idx = start + i;
                var pos = _L.Pos[i];

                // 卡片容器。官方連沒商品的格子也畫卡底框 → 每格都先鋪卡底,沒商品就只留空框 (#8)。
                var card = UIKit.NewRect(_grid, "card" + i);
                card.anchorMin = card.anchorMax = new Vector2(0, 1); card.pivot = new Vector2(0, 1);
                card.anchoredPosition = new Vector2(pos.x, -pos.y); card.sizeDelta = _L.Size;
                AddArt(card, "bg", ShopArt.An(_L.FrameAn), 0, 0);   // 官方卡底 (空格也畫)

                if (idx >= items.Count) continue;                    // 這頁沒有第 i 件商品 → 只保留空框
                var item = items[idx];

                var nm = TxtAt(card, "name", _L.NamePos.x, _L.NamePos.y, _L.TextW, 16, _L.NameFont, _L.NameColor, _L.Align);
                nm.fontWeight = FontWeight.Thin;   // 白色細字
                nm.text = item.Name;   // 已擁有不加勾勾/標記 (使用者指定)
                var pr = TxtAt(card, "price", _L.PricePos.x, _L.PricePos.y, _L.TextW, 16, 12, CWhite, _L.Align);
                pr.fontStyle = FontStyles.Bold;
                pr.text = (item.IsPermanent ? "永久 " : item.DurationDays + "天 ") + item.Price + CurrencyZh(item.Currency);
                var lv = TxtAt(card, "lv", _L.LvPos.x, _L.LvPos.y, _L.TextW, 16, 11, CLv, _L.Align);
                lv.text = "等级限制:LV" + Mathf.Max(1, item.MinLevel);   // 無模型的 item 已在 RefreshGrid 過濾掉 → 不再需要「無模型」標記

                var itLocal = item;
                // 套装大卡不放中間的購物車(試穿)鈕 (user 指定) → 只有 買/送;試穿改由點卡片 (AddTryOnHit)。小卡才有 fit 鈕。
                if (_slot != EquipSlot.Outfit)
                    SpriteBtn(card, "fit",  "Shop148.an", "Shop150.an", _L.FitPos.x,  _L.FitPos.y,  () => DoTryOn(itLocal), hoverAn: "Shop149.an");  // 試穿 (不需擁有)
                SpriteBtn(card, "buy",  "Shop123.an", "Shop125.an", _L.BuyPos.x,  _L.BuyPos.y,  () => DoBuy(itLocal),  hoverAn: "Shop124.an");  // 購買
                SpriteBtn(card, "gift", "Shop126.an", "Shop128.an", _L.GiftPos.x, _L.GiftPos.y, hoverAn: "Shop127.an");                         // 送禮 (尚無功能)
                _pendingCards.Add(new PendingCard { I = i, Card = card, Item = item });   // 卡內 3D 縮圖 → 漸進建
                AddTryOnHit(card, item, i);        // 點=試穿；滑上去=該卡縮圖放大旋轉
            }
        }

        // 購買/全身購買 = 使用者主動花錢 → 要有 info 回饋 (其餘按鈕才靜默)。
        private void DoBuy(ShopItem item)
        {
            switch (ShopService.Buy(_session.Wardrobe, item, Now()))
            {
                case BuyResult.Ok:
                    EquipOwned(item);                        // 購買=直接穿戴 (使用者指定)
                    WardrobeStore.SaveAll(_session);         // 落地 擁有+錢包+穿搭 (只存已擁有的)
                    Nav.RefreshRoomAvatar?.Invoke();         // 房間/大廳的人同步換上
                    RebuildAvatar();                         // 左側預覽更新
                    Toast.Show("購買並穿上：" + item.Name);
                    break;
                case BuyResult.NotEnoughMoney: Toast.Show("餘額不足"); break;
                case BuyResult.AlreadyOwned: Toast.Show("已經擁有：" + item.Name); break;
                case BuyResult.NoRoom: Toast.Show("服飾欄已滿，請到儲物櫃「服饰栏扩充」"); break;   // 預設 3 格，需擴充
                default: Toast.Show("購買失敗"); break;
            }
            Refresh();
        }

        // 全身購買：把目前穿在身上 (試穿/裝備) 的每一件都買下來。
        private void DoBuyAll()
        {
            if (_catalog == null) return;
            int bought = 0, already = 0, noRoom = 0, noMoney = 0;
            foreach (var kv in new List<KeyValuePair<EquipSlot, int>>(_session.Wardrobe.Equipped))
            {
                var it = _catalog.ById(kv.Value);
                if (it == null) continue;
                switch (ShopService.Buy(_session.Wardrobe, it, Now()))
                {
                    case BuyResult.Ok: bought++; break;
                    case BuyResult.AlreadyOwned: already++; break;
                    case BuyResult.NoRoom: noRoom++; break;
                    case BuyResult.NotEnoughMoney: noMoney++; break;
                }
            }
            if (bought > 0) WardrobeStore.SaveOwnedWallet(_session);   // 落地 profile.json (擁有+錢包)
            // 有件數因「服飾欄已滿」買不下 → 講清楚 (預設 9 格,不夠請到儲物櫃 服饰栏扩充)。
            string msg;
            if (noRoom > 0) msg = "全身購買 " + bought + " 件；服飾欄已滿(" + _session.Wardrobe.ClothSlotCount + "格)，還有 " + noRoom + " 件請先到儲物櫃「服饰栏扩充」";
            else if (noMoney > 0) msg = "全身購買 " + bought + " 件；" + noMoney + " 件餘額不足";
            else if (bought > 0) msg = "全身購買成功（" + bought + " 件）";
            else if (already > 0) msg = "全身穿搭已全部擁有";
            else msg = "沒有可購買的穿搭";
            Toast.Show(msg);
            Refresh();
        }

        // 快速充值：離線版無金流後端 → 一鍵把三種幣 (M=Coins / G=Points / H=Bonus) 全部充滿,方便試玩。
        // 錢包由 WardrobeStore 持久化到 active user 的 profile.json → 設值後存檔 + 更新底條數字。
        private void DoRecharge()
        {
            const int Full = 999999999;   // 充滿 (int 上限內的大額;三幣一致,顯示 9 位仍在各幣位之間留有間距不重疊)
            var w = _session.Wardrobe.Wallet;
            w.Coins = Full; w.Points = Full; w.Bonus = Full;
            WardrobeStore.SaveOwnedWallet(_session);   // 錢包落地 profile.json (充值也持久化)
            UpdateWallet();               // 只需刷新底條數字 (不動商品格/預覽)
            Toast.Show("快速充值：M／G／H 幣已全部充滿");
        }

        // 試穿：官方 fitnormal/试穿 是「不需擁有」的即時預覽——只換左側大預覽的模型，不是真正裝備/扣款。
        // 直接把該部位設成此商品 id → 重建預覽 avatar。連身裙(OnePiece)與上/下身互斥，換上時清掉衝突部位。
        // 穿搭歷史：試穿過的衣服放進 _history (最近在前、依 Id 去重、上限 64 件)。供搜尋列右邊的歷史鈕列出 (#6)。
        private void AddToHistory(ShopItem item)
        {
            if (item == null) return;
            _history.RemoveAll(h => h.Id == item.Id);
            _history.Insert(0, item);
            if (_history.Count > 64) _history.RemoveRange(64, _history.Count - 64);
        }

        private void DoTryOn(ShopItem item)
        {
            if (item == null || item.EquipSlot == EquipSlot.None) return;
            AddToHistory(item);   // 記進「穿搭歷史」(最近在前，去重) — 供歷史鈕列出 (#6)
            var w = _session.Wardrobe;
            if (item.EquipSlot == EquipSlot.Outfit)
            {
                _tryOnOutfitParts = ComposeOutfitParts(item, useCurrent: true);   // 試穿到身上:沿用現況(沒涵蓋的部位保留),RebuildAvatar 走這組
                RebuildAvatar();
                return;
            }
            // 目前正顯示套裝(或已連續試穿疊加)→ 把這件疊到「目前顯示的穿搭」上:只換該部位、其餘保留
            // (使用者:穿白色星辰套裝再選它的上衣,褲子要留著,不是整組脫回 default)。
            if (_tryOnOutfitParts != null && item.EquipSlot != EquipSlot.None)
            {
                _tryOnOutfitParts = ComposeParts(_tryOnOutfitParts, new[] { item.MshRelPath });
                RebuildAvatar();
                return;
            }
            _tryOnOutfitParts = null;   // (無套裝在試穿)換單件 → 逐部位裝備到 Wardrobe
            if (item.EquipSlot == EquipSlot.OnePiece)
            {
                w.ClearEquipped(EquipSlot.Top);
                w.ClearEquipped(EquipSlot.Bottom);
                w.SetEquipped(EquipSlot.OnePiece, item.Id);
            }
            else
            {
                if (item.EquipSlot == EquipSlot.Top || item.EquipSlot == EquipSlot.Bottom)
                    w.ClearEquipped(EquipSlot.OnePiece);
                w.SetEquipped(item.EquipSlot, item.Id);
            }
            RebuildAvatar();   // 靜默試穿：只換左側預覽,不彈提示 (無模型者預覽不變)
            // 不呼叫 RefreshGrid()：試穿只改左側大人物,卡片縮圖畫的是「商品本身」與試穿無關。
            // 之前多呼叫 RefreshGrid 會 DestroyCardPreviews 把整頁 8 張 3D 縮圖全砍掉重建 → 使用者看到「按一張、其餘 6 張同時重 load」。
        }

        // 買了直接穿上 (單件；套装另走 tryOn 預覽,不在這裡自動穿)。與 DoTryOn 同一套互斥規則。
        private void EquipOwned(ShopItem item)
        {
            if (item == null || item.EquipSlot == EquipSlot.None || item.EquipSlot == EquipSlot.Outfit) return;
            var w = _session.Wardrobe;
            _tryOnOutfitParts = null;
            if (item.EquipSlot == EquipSlot.OnePiece)
            {
                w.ClearEquipped(EquipSlot.Top); w.ClearEquipped(EquipSlot.Bottom);
                w.SetEquipped(EquipSlot.OnePiece, item.Id);
            }
            else
            {
                if (item.EquipSlot == EquipSlot.Top || item.EquipSlot == EquipSlot.Bottom) w.ClearEquipped(EquipSlot.OnePiece);
                w.SetEquipped(item.EquipSlot, item.Id);
            }
        }

        // 復原穿搭：回到「儲物櫃實際穿戴」的樣子 (從 profile 重載擁有+穿搭)，不是清成裸體預設 (使用者指定)。
        private void DoResetOutfit()
        {
            WardrobeStore.Load(_session);   // 重載存檔的真實穿搭
            _tryOnOutfitParts = null;
            _dragAngle = -DefaultYaw; _pitchAngle = 0f;   // 連轉動角度一起復原 (人物朝右 30°)
            RebuildAvatar();   // 靜默復原：只換左側預覽,不重建卡片縮圖
        }

        // 覆蓋卡片上半 (名稱 + 縮圖區、避開底排買/送/試穿鈕) 的透明命中區：點一下 → 試穿到左側預覽；滑上去 → 該卡縮圖放大旋轉。
        // 命中區依「左邊衣物縮圖 / 右邊名稱價格」拆成兩塊 (user 指定)：只有滑到「左邊衣物」那塊才會放大旋轉;滑到右邊文字區不會。
        // 兩塊都仍可點=試穿 (縮圖 raycastTarget=false → 放大時不搶 raycast,故拆塊不會抖動)。
        private void AddTryOnHit(RectTransform card, ShopItem item, int i)
        {
            var it = item; int idx = i; var theCard = card;
            // 放大/命中區:左塊=整片深紫色縮圖格子(卡片整高,使用者指定);右塊=名稱/價格,留在按鈕列上方。大卡(套裝)=整張縮圖。
            float top, h, rightTop, rightH;
            if (_slot == EquipSlot.Outfit)
            {
                top = _L.AvCenter.y + _L.AvSize.y / 2f; h = _L.AvSize.y;   // 縮圖上緣 / 縮圖高
                rightTop = top; rightH = h;
            }
            else
            {
                top = -3f; h = _L.Size.y - 6f;              // 左塊=整片縮圖格子 (卡片幾乎整高)；按鈕在右側 x≥90,不會被蓋
                rightTop = -4f; rightH = _L.FitPos.y - 8f;  // 右塊留在按鈕列 (y=FitPos.y) 上方,才不擋 買/送/試穿 鈕
            }
            float avatarRight = _L.AvCenter.x + _L.AvSize.x / 2f;    // 左邊衣物縮圖的右緣 → 左右兩塊的分界

            // 左塊 (衣物縮圖)：點=試穿；滑上去=該卡放大旋轉。
            var left = UIKit.AddImage(card, "tryhitL", new Color(1, 1, 1, 0.001f), true);
            var lrt = left.rectTransform;
            lrt.anchorMin = lrt.anchorMax = new Vector2(0, 1); lrt.pivot = new Vector2(0, 1);
            lrt.anchoredPosition = new Vector2(2, top); lrt.sizeDelta = new Vector2(Mathf.Max(1f, avatarRight - 2f), h);
            AddTryOnClick(left, it);
            var trig = left.gameObject.AddComponent<EventTrigger>();
            AddTrigData(trig, EventTriggerType.PointerEnter, _ => { _hoverCard = idx; theCard.SetAsLastSibling(); });   // 放大的縮圖要蓋過鄰卡
            AddTrigData(trig, EventTriggerType.PointerExit, _ => { if (_hoverCard == idx) _hoverCard = -1; });

            // 右塊 (名稱/價格)：只點=試穿,不掛 PointerEnter/Exit → 滑到這裡「不」放大旋轉 (user 指定右邊區域不觸發)。
            float rightW = _L.Size.x - 2f - avatarRight;
            if (rightW > 1f)
            {
                var right = UIKit.AddImage(card, "tryhitR", new Color(1, 1, 1, 0.001f), true);
                var rrt = right.rectTransform;
                rrt.anchorMin = rrt.anchorMax = new Vector2(0, 1); rrt.pivot = new Vector2(0, 1);
                rrt.anchoredPosition = new Vector2(avatarRight, rightTop); rrt.sizeDelta = new Vector2(rightW, rightH);
                AddTryOnClick(right, it);
            }
        }

        // 命中區共用的「點=試穿」掛法 (透明 Image + Button + 按下音效)。
        private void AddTryOnClick(Image hit, ShopItem it)
        {
            var btn = hit.gameObject.AddComponent<Button>();
            btn.targetGraphic = hit; btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(() => DoTryOn(it));
            UiSfx.AttachPress(btn, UiSfx.Click);   // 點卡試穿也算按下 → SE_0001
        }

        private static void AddTrigData(EventTrigger t, EventTriggerType type, UnityEngine.Events.UnityAction<BaseEventData> fn)
        {
            var e = new EventTrigger.Entry { eventID = type };
            e.callback.AddListener(fn);
            t.triggers.Add(e);
        }

        // 卡片小圖的假人骨架 bind pose (不套 mot)——官方 AvtShow_LoadByItemId 按 category 選不同假人,各自 bind pose 把
        // 該部位「擺出來」展示：鞋(cat5)→MSHOP0003(兩性別共用,腳擺成 3/4 展示姿);手套(cat4)→WSHOP0002/MSHOP0002(手前伸);
        // 其餘(衣/髮/連身)→WSHOP0001/MSHOP0001(手垂放直腿)。鞋 mesh 只綁腳骨{4,5,8,9},在 55/58 骨都同索引→照樣正確蒙皮。
        private static string ShopHrcFor(ItemSex sex, EquipSlot slot)
        {
            bool male = sex == ItemSex.Male;
            switch (slot)
            {
                case EquipSlot.Shoes:  return "AVATAR/MSHOP0003.HRC";                                    // cat5 兩性別共用
                case EquipSlot.Gloves: return male ? "AVATAR/MSHOP0002.HRC" : "AVATAR/WSHOP0002.HRC";   // cat4 手前伸
                default:               return male ? "AVATAR/MSHOP0001.HRC" : "AVATAR/WSHOP0001.HRC";   // 衣/髮/連身/眼鏡…
            }
        }

        // 左側「大預覽=實際穿的人」用自然站姿 rest idle (男 MREST0082 / 女 WREST0072,手垂放)，骨架 FEMALE/MALE.HRC。
        // (user 指定：卡片小圖用假人 bind、左側穿的人用這組 idle。)
        private void ApplyLeftPose(SdoAvatar av)
        {
            var mot = SdoRoomAvatar.LoadMot(_sex == ItemSex.Male ? "MOTION/MREST0082.MOT" : "MOTION/WREST0072.MOT");
            if (mot != null) { av.RestMot = mot; av.SetClip(mot); av.PoseInitialIdle(); }
        }

        // ---- 各商品卡的 3D 縮圖 ----

        private void BuildCardCam()
        {
            if (_cardCam != null) return;
            var go = new GameObject("ShopCardCam");
            _cardCam = go.AddComponent<Camera>();
            // 官方 AvtShow 是 ORTHOGRAPHIC (D3DXMatrixOrthoLH),不是透視。ortho WIDTH=128(半寬64),半高由 aspect 推 → 方形像素。
            // Unity ortho: 水平半寬 = orthographicSize × aspect(=W/H) → orthographicSize = 64 / (W/H)。
            _cardCam.orthographic = true;
            _cardCam.orthographicSize = CardOrthoHalfW / ((float)_L.RtW / _L.RtH);   // 依當前版面 RT 比例 (RenderCard 每次也重設,大小卡切換才對)
            _cardCam.nearClipPlane = CardNear; _cardCam.farClipPlane = CardFar;
            _cardCam.cullingMask = 1 << PreviewLayer;
            _cardCam.clearFlags = CameraClearFlags.SolidColor;
            _cardCam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            _cardCam.enabled = false;   // 只手動 Render()
        }

        // 格子只畫「那件衣物」本身 (發型/表情才帶頭；眼鏡不帶頭,只放大眼鏡)；官方正交相機 + per-slot 表 (scale/位移) 取景。
        private void BuildCardPreview(int i, RectTransform card, ShopItem item)
        {
            GameObject root = null;
            try
            {
                if (_catalog == null || !_catalog.IsRenderable(item)) return;   // 無模型 → 不做縮圖
                BuildCardCam();
                _cardRT[i] = new RenderTexture(_L.RtW, _L.RtH, 16, RenderTextureFormat.ARGB32) { name = "ShopCardRT" + i, antiAliasing = 2 };
                root = new GameObject("ShopCardAvatar" + i);
                root.transform.position = CardSpot(i);
                root.transform.rotation = Quaternion.Euler(0f, RoomMovement.FacingDegrees(2) + DefaultYaw, 0f);   // 衣物預設朝左 30°
                SdoAvatarBuilder.LogLabel = string.IsNullOrEmpty(item.Name) ? item.ModelId.ToString("D6") : item.Name;   // [avtex] log 標名 (user)
                var av = SdoRoomAvatar.Build(root, PreviewLayer, false, ComposeCardParts(item), ShopHrcFor(_sex, item.EquipSlot), bindPoseNoIdle: true);
                if (av == null) { Destroy(root); _cardRT[i].Release(); Destroy(_cardRT[i]); _cardRT[i] = null; return; }
                av.enabled = false;  // 凍結 (bind pose 已在 Build 內 PoseFrame(0) 蒙皮好)
                ApplyCardCutoutShader(root);   // 卡片畫進透空 RT → 所有部位改 cutout,衣物鏤空(alpha0)真的透空不糊成實心 (N1)
                // 帶「頭/臉」的卡不藏膚色：女 FACE 貼圖就叫 W_Basic_face、部分假髮內嵌耳/頸膚色 range，藏了會消失/破洞；
                // 表情本身就是臉 (膚色 mesh) → 一併排除。髮型/表情才留膚色,其餘一律藏 → 格子只剩布料。
                // 眼鏡不再補頭 (ComposeCardParts) → 也走藏膚色路徑 (眼鏡本身無膚色材質,等同 no-op,但語意=眼鏡卡不顯示頭)。
                if (item.EquipSlot != EquipSlot.Hair
                    && item.EquipSlot != EquipSlot.Expression && item.EquipSlot != EquipSlot.Outfit)
                    HideSkinSubmeshes(root);   // 藏膚色 → 格子裡只剩布料 (官方視覺)
                ForceLightExpressionFace(root, item);   // 表情縮圖統一最白膚色 (huan0)
                // 取景=官方原汁：不再 auto-center/auto-fit。官方用對的假人骨架 (ShopHrcFor 按 slot 選) 把該部位「擺出來」,再套
                // 固定 per-slot 表 (FrameFor)。例外:套装=全身穿搭 → 官方 slot10(scale1),但 2×4 格小 → 依全身幾何 fit 填 90%+置中。
                // 眼鏡：維持原本「有頭時」的 FrameFor(scale10/眼窩位) → 大小/位置/角度跟以前一模一樣,只是頭被藏掉 (user 指定,故不走 auto-fit)。
                var slot = item.EquipSlot;
                if (slot == EquipSlot.Outfit)
                {
                    // 套装全身：用「固定標準身高」框景,不依實際幾何 fit → 翅膀/高髮再大也不會把人縮小 (user:「翅膀大的不用
                    // 自動縮小,官方沒有這樣」)。身體填框高度 OutfitCardFill (官方卡片人物比框小一號,不是滿版 → user:「套装的
                    // 人太大,官方沒那麼大」);縮小後翅膀/高髮在卡內上方也多露一些。身體恆為同一大小、置中。
                    float ofh = CardOrthoHalfW / ((float)_L.RtW / _L.RtH);
                    const float BodyH = 58f, BodyCy = 29f;                 // 標準身高/中心 (腳0~頭58,中心29;固定值)
                    const float OutfitCardFill = 0.72f;                    // 身體填卡框高度比例 (調小=人變小;官方 ≈ 這個大小)
                    float os = ofh * 2f * OutfitCardFill / BodyH;
                    _cardFrameScale[i] = new Vector3(os, os, os);
                    _cardFramePos[i] = new Vector3(0f, -os * BodyCy, 0f);
                }
                else if (slot == EquipSlot.Wings)   // 單件翅膀=背飾 mesh,尺寸差異大且無 per-slot 表值 → auto-fit 填滿+置中
                {
                    VisibleYBounds(root, null, out float owmn, out float owmx);
                    VisibleXBounds(root, out float oxmn, out float oxmx);
                    float ofh = CardOrthoHalfW / ((float)_L.RtW / _L.RtH);
                    float os = Mathf.Min(CardOrthoHalfW * 2f * 0.9f / Mathf.Max(oxmx - oxmn, 1e-3f),
                                         ofh * 2f * 0.9f / Mathf.Max(owmx - owmn, 1e-3f));
                    _cardFrameScale[i] = new Vector3(os, os, os);
                    _cardFramePos[i] = new Vector3(-os * (oxmn + oxmx) * 0.5f, -os * (owmn + owmx) * 0.5f, 0f);
                }
                else
                {
                    var fr = FrameFor(slot, _sex);
                    _cardFrameScale[i] = fr.Scale;
                    _cardFramePos[i] = fr.Pos;
                }
                _cardAv[i] = root;
                _cardNoSpin[i] = slot == EquipSlot.Glasses;   // 眼鏡卡：hover 不旋轉,只放大 (user 指定)
                // 炫 hair (model 40000-49999)：AvatarUvScroll 已由 LoadParts 掛上、每幀捲 V,但卡 RT 只在 hover 重畫 →
                // 縮圖凍結。標記此卡,Update 每幀重畫 RT,小圖也會「不斷變色」(user 指定)。SdoAvatarBuilder.IsUvScrollHair 同判準。
                _cardUvScroll[i] = SpecialMotionItems.IsUvScrollHair(item.ModelId);

                // 卡內縮圖 RawImage (版面 _L 決定尺寸/位置；小卡 72×88、套装大卡 150×240；pivot 置中以便由中心放大)
                var img = new GameObject("preview", typeof(RectTransform)).AddComponent<RawImage>();
                img.transform.SetParent(card, false);
                var irt = img.rectTransform;
                irt.anchorMin = irt.anchorMax = new Vector2(0, 1); irt.pivot = new Vector2(0.5f, 0.5f);
                irt.anchoredPosition = _L.AvCenter; irt.sizeDelta = _L.AvSize;
                img.texture = _cardRT[i]; img.raycastTarget = false;
                _cardImg[i] = img;
                _cardScale[i] = 1f; _cardAngle[i] = 0f;
                RenderCard(i);
            }
            catch (System.Exception e)
            {
                if (root != null && _cardAv[i] != root) Destroy(root);   // 半成品別留在 CardSpot (下次重建會入鏡疊影)
                Debug.LogWarning("[shop] card preview " + i + " failed (non-fatal): " + e.Message);
            }
        }

        // 把第 i 件衣物畫進它的 RT：官方=正交相機看世界 Y≈0，節點依 per-slot 表放大(scale)+位移(pos)把該部位頂進中心；
        // 節點原點=模型中心線(x=0,z=0),繞 Y 自轉即原地轉 (spin 直接加 yaw,不需 RotateAround)。
        private void RenderCard(int i)
        {
            if (_cardCam == null || _cardRT[i] == null || _cardAv[i] == null) return;
            var t = _cardAv[i].transform;
            t.localScale = _cardFrameScale[i];                       // 官方 per-slot 放大
            t.position = CardSpot(i) + _cardFramePos[i];             // 官方 per-slot 位移 (y 為負把部位往下推到相機中心)
            // 朝左 30° + hover 自轉。眼鏡卡維持原本角度(30°),只是不轉 → 靠 Update 讓 _cardAngle 恆 0 (_cardNoSpin),此處照舊加 DefaultYaw。
            t.rotation = Quaternion.Euler(0f, RoomMovement.FacingDegrees(2) + DefaultYaw + _cardAngle[i], 0f);
            _cardCam.orthographicSize = CardOrthoHalfW / ((float)_L.RtW / _L.RtH);   // 依當前版面 RT 比例 (大小卡切換才對)
            _cardCam.transform.position = CardSpot(i) + new Vector3(0f, 0f, -CardEyeDist);   // 官方 eye=(0,0,-110),看 Y≈0
            _cardCam.transform.LookAt(CardSpot(i));
            _cardCam.targetTexture = _cardRT[i];
            _cardCam.Render();
        }

        // 卡片畫進透空 RT：官方遊戲內衣物用 Unlit/Texture,但它 UNITY_OPAQUE_ALPHA 逼 alpha=1 且不 clip → 衣物 DDS 的鏤空
        // (alpha 0)在透空 RT 被畫成實心。全部改用 cutout(Sdo/UnlitDoubleSided：clip(a-cutoff)+a=1)→ 鏤空真的透空、其餘全不透。
        // 材質「名」不變 → HideSkinSubmeshes/IsSkinMat 仍認得膚色 range;髮本來就這 shader(no-op),不透明布料也安全(Cull Off+ZWrite On)。
        private static void ApplyCardCutoutShader(GameObject root)
        {
            var cut = Shader.Find("Sdo/UnlitDoubleSided");
            if (cut == null) return;
            foreach (var mr in root.GetComponentsInChildren<MeshRenderer>())
                foreach (var m in mr.sharedMaterials)
                    if (m != null && m.mainTexture != null)
                    {
                        string sn = m.shader != null ? m.shader.name : "";
                        // 髮/鏤空布料 (Sdo/UnlitDoubleSided) 本來就帶 authored _Cutoff(0.3);讀出保留,別壓到 0.05 (見 CardCutoutFor)。
                        float authored = sn == "Sdo/UnlitDoubleSided" ? m.GetFloat("_Cutoff") : 0f;
                        m.shader = cut;   // 只改有貼圖的 (無貼圖回退材質留給 ForceLightExpressionFace 處理)
                        m.SetFloat("_Cutoff", CardCutoutFor(sn, authored));
                    }
        }

        /// <summary>透空-RT 卡片縮圖:每個部位都被強制成 cutout shader,alpha-clip 門檻依「原本的 shader」決定。Pure → 單元測試。
        ///   • <c>Unlit/Texture</c> (opaque 衣服,含 alpha 壞掉被強制 opaque 的布料) → 0：不裁 + alpha 逼 1 (實心),
        ///     否則它 94~100% 的 alpha0 texel 被打穿成透明線框 (粉紅舞會/無限迷戀 小格子)。
        ///   • <c>Sdo/UnlitDoubleSided</c> (髮/鏤空布料) → 保留 authored cutoff(預設 0.3)。髮飾的「去背」底不是全透 (a=0),
        ///     而是半透明 a≈0.07~0.25 (DXT3 量化底色);壓到 0.05 裁不掉 → 縮圖露出方框實底 (070028 蝴蝶結髮飾「沒去背」)。
        ///     0.3 與遊戲內 / 左側大預覽同一 shader、同一門檻,縮圖才一致。
        ///   • 其餘 (blend = 去背刺青/紗/眼鏡 Sdo/UnlitAvatarAlpha) → 0.05：只裁真洞、留半透布料。</summary>
        public static float CardCutoutFor(string shaderName, float authoredCutoff)
        {
            if (shaderName == "Unlit/Texture") return 0f;
            if (shaderName == "Sdo/UnlitDoubleSided") return authoredCutoff > 0f ? authoredCutoff : 0.3f;
            return 0.05f;
        }

        // 消掉衣物網格上的「膚色」part/submesh (材質名 = W_Basic_* / M_Basic_*，即裸身手臂/腿) → 格子裡只剩布料。
        // 兩種擺法都處理：膚色是獨立 part(整個 renderer 關掉) / 或衣物 mesh 內的 skin range(清該 submesh 三角形)。
        // 全量掃 22,092 件 PANT/COAT/SHOES：膚色貼圖名只有 8 種且全含 "Basic"(大小寫混雜+一種尾端多空白)，
        // 無任何布料貼圖含 "Basic" → 不分大小寫 substring 判斷零誤判；判斷必須逐材質(range)，不能整個 submesh 一起看。
        private static void HideSkinSubmeshes(GameObject root)
        {
            bool anyCloth = false;   // 整件全是膚色材質 (極少數異常件) → 什麼都不藏，寧可照原樣畫也不給空格子
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
                    if (mats.Length > 0 && IsSkinMat(mats[0])) mr.enabled = false;   // 整個 part 是膚色 → 關掉
                }
                else
                {
                    for (int s = 0; s < mesh.subMeshCount && s < mats.Length; s++)
                        if (IsSkinMat(mats[s])) mesh.SetTriangles(new int[0], s);    // 清膚色 submesh
                }
            }
        }

        private static bool IsSkinMat(Material m)
            => m != null && m.name.IndexOf("BASIC", System.StringComparison.OrdinalIgnoreCase) >= 0;

        // 「看得見」幾何的模型空間 Y 範圍 (min,max)：只算仍有三角形的 submesh 頂點 (膚色被 SetTriangles 清掉的頂點仍在
        // mesh 裡但不引用 → 不計入,裸腿/手臂不撐大)。nameFilter!=null 時只算物件名含該字串的 part (如 "FACE" 只框頭,不含
        // 長髮 → 頭恆置中)。頂點已是 bind-pose 蒙皮後位置;parts 在 root 下 local identity → 頂點座標 = 模型空間。
        private static bool VisibleYBounds(GameObject root, string nameFilter, out float mn, out float mx)
        {
            bool any = false; mn = 0f; mx = 0f;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>())
            {
                var mr = mf.GetComponent<MeshRenderer>(); var mesh = mf.sharedMesh;
                if (mesh == null || mr == null || !mr.enabled) continue;
                if (nameFilter != null && mf.name.IndexOf(nameFilter, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                var verts = mesh.vertices;
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    var tris = mesh.GetTriangles(s);
                    for (int k = 0; k < tris.Length; k++)
                    {
                        float y = verts[tris[k]].y;
                        if (!any) { mn = mx = y; any = true; } else { if (y < mn) mn = y; if (y > mx) mx = y; }
                    }
                }
            }
            return any;
        }

        // 「看得見」幾何的模型空間 X 範圍 (min,max) — 鞋/手套水平 fit+置中用 (手套 mesh 含雙前臂,很寬)。同 VisibleYBounds 走法。
        private static bool VisibleXBounds(GameObject root, out float mn, out float mx)
        {
            bool any = false; mn = 0f; mx = 0f;
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
                        float x = verts[tris[k]].x;
                        if (!any) { mn = mx = x; any = true; } else { if (x < mn) mn = x; if (x > mx) mx = x; }
                    }
                }
            }
            return any;
        }

        // 每張卡只載「該件」的 .msh (官方=素體假人+該件+per-slot 鏡頭放大 → 視覺上只有衣物本身)。
        // 發型例外：官方鏡頭推到「頭」上 (slot0/1 posY-500 scale9) → 補 FACE 讓髮有頭可掛。眼鏡不補頭 (user: 眼鏡卡不顯示頭,
        // 只放大眼鏡本身),其餘一律純衣物。
        private string[] ComposeCardParts(ShopItem item)
        {
            if (item.EquipSlot == EquipSlot.Outfit) return ComposeOutfitParts(item, useCurrent: false);   // 卡片縮圖:套装穿在 default 假人上
            var rel = item.MshRelPath;
            if (item.EquipSlot == EquipSlot.Hair)
            {
                var d = AvatarOutfit.DefaultsFor(_sex);
                if (d.TryGetValue(EquipSlot.Face, out var face) && !string.IsNullOrEmpty(face))
                    return new[] { face, rel };
            }
            return new[] { rel };
        }

        // 套装卡：把該套所有組件穿到預設身體上 (組件覆蓋對應部位;連身取代上衣並移除下著;翅膀/眼鏡/項鍊為附加),沒被覆蓋
        // 的部位保留預設 (臉/髮/手…) → 呈現完整穿搭的全身人形。
        private string[] ComposeOutfitParts(ShopItem item, bool useCurrent)
        {
            var gender = ItemTypes.GenderOf(item.Category, item.Name);
            var baseParts = new List<string>();
            foreach (var kv in AvatarOutfit.DefaultsFor(gender)) baseParts.Add(kv.Value);   // default 打底(補臉/手/髮等)
            // 只有「試穿到身上」(useCurrent) 才把目前顯示的穿搭蓋上去 → 套裝沒涵蓋的部位沿用現況(含連續試穿上一套)。
            // 卡片縮圖(右邊假人)useCurrent=false → 純 default,不沿用玩家目前穿搭(使用者:右邊假人頭髮要用 default)。
            if (useCurrent) baseParts.AddRange(CurrentDisplayedParts());
            return ComposeParts(baseParts, _catalog.OutfitComponentMeshes(item));
        }

        // 目前左側預覽實際顯示的穿搭:正在試穿套裝(或已疊過單件)→ 那份;否則 → 現有裝備。連續試穿疊加的底。
        private IEnumerable<string> CurrentDisplayedParts()
            => _tryOnOutfitParts != null ? (IEnumerable<string>)_tryOnOutfitParts : AvatarOutfit.ResolveParts(_sex, EquippedItems());

        // 把 overrides(套裝組件 / 單件)逐部位疊到 baseParts 上:連身取代上下著;眼鏡/項鍊/翅膀=附加(依 mesh token 去重);
        // 其餘覆蓋該部位。回傳完整 parts。沒被 overrides 覆蓋的部位保留 base(這就是「套裝沒有的部位沿用現況」)。
        private string[] ComposeParts(IEnumerable<string> baseParts, IEnumerable<string> overrides)
        {
            var slots = new Dictionary<EquipSlot, string>();
            var additive = new Dictionary<string, string>();   // token(GLASS/LINGDANG/CHIBANG…) → mesh,去重(base 與 override 同類只留一)
            void Apply(string rel)
            {
                if (string.IsNullOrEmpty(rel)) return;
                var s = SlotFromMeshToken(rel);
                if (s == EquipSlot.OnePiece) { slots[EquipSlot.Top] = rel; slots.Remove(EquipSlot.Bottom); }
                else if (s == EquipSlot.Glasses || s == EquipSlot.Necklace || s == EquipSlot.None) additive[MeshToken(rel)] = rel;
                else { if (s == EquipSlot.Top || s == EquipSlot.Bottom) slots.Remove(EquipSlot.OnePiece); slots[s] = rel; }
            }
            foreach (var rel in baseParts) Apply(rel);
            foreach (var rel in overrides) Apply(rel);
            var list = new List<string>();
            foreach (var s in new[] { EquipSlot.Face, EquipSlot.Hair, EquipSlot.Top, EquipSlot.Bottom, EquipSlot.Shoes, EquipSlot.Gloves })
                if (slots.TryGetValue(s, out var p) && !string.IsNullOrEmpty(p)) list.Add(p);
            list.AddRange(additive.Values);
            return list.ToArray();
        }

        // mesh 檔名最後一段部位 token:'AVATAR/023424_WOMAN_HAIR.MSH' → 'HAIR'(附加類去重用)。
        private static string MeshToken(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return "";
            var n = rel; int dot = n.LastIndexOf('.'); if (dot > 0) n = n.Substring(0, dot);
            int us = n.LastIndexOf('_'); return (us >= 0 ? n.Substring(us + 1) : n).ToUpperInvariant();
        }

        // 從組件 mesh 檔名的部位 token 推 EquipSlot (CHIBANG 翅膀無對應 slot → None=附加)。
        private static EquipSlot SlotFromMeshToken(string rel)
        {
            string u = rel.ToUpperInvariant();
            if (u.Contains("_ONE")) return EquipSlot.OnePiece;
            if (u.Contains("_COAT")) return EquipSlot.Top;
            if (u.Contains("_PANT")) return EquipSlot.Bottom;
            if (u.Contains("_HAIR")) return EquipSlot.Hair;
            if (u.Contains("_SHOES")) return EquipSlot.Shoes;
            if (u.Contains("_HAND")) return EquipSlot.Gloves;
            if (u.Contains("_GLASS")) return EquipSlot.Glasses;
            if (u.Contains("_LINGDANG")) return EquipSlot.Necklace;
            if (u.Contains("_FACE")) return EquipSlot.Face;   // FACE / FACE_HUAN
            return EquipSlot.None;   // CHIBANG 翅膀等 → 附加
        }

        // 表情臉統一用「最白」膚色變體 (huan0)。官方 FACE_HUAN mesh 的 material[0] 各自綁不同深淺膚色——不少綁最深的 huan4
        // (亮度~51,看起來像黑人),另有一批綁到打錯字/不存在的檔名 (haun4/huan_1) → 回退平塗膚色。強制改用該 model 的
        // *_FACE_HUAN0.DDS (亮度~190,同一表情最白膚色) → 一次修好深膚色與檔名回退兩種。231 件中僅 1 件無任何 huan 貼圖。
        private void ForceLightExpressionFace(GameObject root, ShopItem item)
        {
            if (root == null || item == null || item.EquipSlot != EquipSlot.Expression) return;
            var rel = item.MshRelPath;
            if (rel == null) return;
            string dir = System.IO.Path.GetDirectoryName(SdoAvatarBuilder.ResolveAvatarFile(rel));
            string id = item.ModelId.ToString("D6"), g = item.GenderFolder;
            Texture2D tex = null;
            foreach (var cand in new[] { id + "_" + g + "_FACE_HUAN0.DDS", id + "_" + g + "_FACE_HUAN_0.DDS", id + "_" + g + "_FACE_HUAN.DDS" })
                if ((tex = SdoAvatarBuilder.ResolveDds(dir, cand)) != null) break;
            if (tex == null) return;
            // 25 個表情 mesh 的 material[0] 貼圖名打錯字 (haun/huan_1…) → LoadParts resolve 失敗、退回 Unlit/Color(無 _MainTex 取樣器)
            // → 我們設的 mainTexture 被忽略 → 臉變平塗「空白」。連 shader 一起改成會取樣貼圖的 cutout,臉才顯示出來 (N3)。
            var faceShader = Shader.Find("Sdo/UnlitDoubleSided") ?? Shader.Find("Unlit/Texture");
            foreach (var mr in root.GetComponentsInChildren<MeshRenderer>())
                if (mr.name.IndexOf("FACE_HUAN", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var mats = mr.sharedMaterials;   // 每卡 material 都是新建實例 → 直接改安全
                    for (int s = 0; s < mats.Length; s++)
                        // 把「膚色臉底」統一成最白 huan0,但**保留裝飾疊層**(面具/口罩/腮紅 = base …_face_huan)自己的貼圖。
                        // 要壓白的兩種:①膚色底=材質名有數字尾 huan[0-4](含深膚 huan4 與打錯字 haun4/huan_1);②貼圖**解不到**
                        // 的破損材質(mainTexture==null → LoadParts 退回平塗色 = 白頭)——如 030252,mesh 的兩個 submesh 都引到
                        // 磁碟不存在的 base huan、但 huan0-4 都在 → 必須回退到 huan0(原本 ForceLightExpressionFace 無差別壓白
                        // 有救到它,我改成只壓數字尾後它變全白 = 迴歸)。要保留的:有解到貼圖的裝飾疊層(面具/口罩,base huan
                        // DDS 存在)與有效 base 膚色(012882)。否則疊層被膚色蓋掉,疊層幾何(眼周/口鼻)帶臉底 UV → 帶鬼影五官
                        // 的素臉(使用者回報 017675 化妝舞會眼罩 / 015353 貓咪口罩「貼圖錯誤」)。
                        if (mats[s] != null && (IsFaceSkinVariant(mats[s].name) || mats[s].mainTexture == null))
                        { if (faceShader != null) mats[s].shader = faceShader; mats[s].mainTexture = tex; }
                }
        }

        // 表情 mesh 的膚色臉底材質貼圖名 = …_face_huan[0-4].dds (含打錯字 haun[0-4]、分隔底線 huan_0)。裝飾疊層
        // (面具/口罩/腮紅) 貼圖名 = base …_face_huan.dds (無數字尾) → 回傳 false 讓 ForceLightExpressionFace 略過、
        // 保留疊層原貼圖。非 huan/haun 材質 (W_Basic_ 頸/耳膚色…) 也回傳 false (本來就是膚色,不需再壓白)。
        private static bool IsFaceSkinVariant(string matName)
        {
            if (string.IsNullOrEmpty(matName)) return false;
            string n = matName.ToLowerInvariant();
            int i = n.IndexOf("huan", System.StringComparison.Ordinal);
            if (i < 0) i = n.IndexOf("haun", System.StringComparison.Ordinal);   // 打錯字檔名 haun
            if (i < 0) return false;
            int j = i + 4;
            if (j < n.Length && n[j] == '_') j++;   // huan_0 / haun_4 的分隔底線
            return j < n.Length && n[j] >= '0' && n[j] <= '9';   // 有數字尾 = 膚色底;base huan = 裝飾疊層 → 略過
        }

        private void DestroyCardPreviews()
        {
            for (int i = 0; i < PerPage; i++)
            {
                // DestroyImmediate (非 Destroy)：Destroy 延遲到幀尾,捲頁時同幀就重建新卡於同一 CardSpot(i)、共用相機把
                // 舊+新兩隻一起 Render 進 RT → 頂部格(i=0,1 同幀先建)殘留前一頁。立即銷毀確保只拍到新的。這些是本畫面自建的
                // 獨立 runtime GameObject,立即銷毀安全。
                if (_cardAv[i] != null) { DestroyImmediate(_cardAv[i]); _cardAv[i] = null; }
                if (_cardRT[i] != null) { _cardRT[i].Release(); Destroy(_cardRT[i]); _cardRT[i] = null; }
                _cardImg[i] = null; _cardScale[i] = 1f; _cardAngle[i] = 0f; _cardNoSpin[i] = false; _cardUvScroll[i] = false;
            }
            _pendingCards.Clear();
            _hoverCard = -1;
        }

        // 每幀推進 hover 卡的放大 (2D 縮圖 scale) + 旋轉 (3D 人形，重畫 RT)。離開 → 縮回 1×、角度歸零。
        private void Update()
        {
            if (_cam == null || !_cam.enabled) return;

            // 輸入法選字框跟著搜尋框:World-Space canvas 下 Unity 不會自動設候選框位置 → 跑到螢幕左上角。聚焦時把
            // compositionCursorPos 設到搜尋框(螢幕座標,原點左下,與 WorldToScreenPoint 一致)→ 候選框出現在框旁邊(使用者)。
            if (_search != null && _search.isFocused)
            {
                var corners = new Vector3[4];
                _search.GetComponent<RectTransform>().GetWorldCorners(corners);   // 0=左下
                var sp = _uiCam != null ? _uiCam.WorldToScreenPoint(corners[0]) : corners[0];
                // WorldToScreenPoint 原點在左下(y 上);compositionCursorPos 的 y 原點在左上 → Y 要翻(否則框跑到螢幕頂端)。
                Input.compositionCursorPos = new Vector2(sp.x, Screen.height - sp.y);
            }

            // ESC → 關商城（等同右上 shopexit 鈕）→ 露出底下的房間或選角色畫面。走轉場漸黑漸亮。
            if (Input.GetKeyDown(KeyCode.Escape) && !ScreenTransition.Busy)
            {
                ScreenTransition.Run(() => SetVisible(false));
                return;
            }

            // 精品屋 banner 霓虹燈：循環播 jingpin1.an 的 18 幀 (#7)。各幀同尺寸 → 直接換 sprite 不需 resize。
            if (_jingpinImg != null && _jingpinFrames != null && _jingpinFrames.Length > 1)
            {
                _jingpinTimer += Time.deltaTime;
                float step = 1f / JingpinFps;
                while (_jingpinTimer >= step)
                {
                    _jingpinTimer -= step;
                    _jingpinIdx = (_jingpinIdx + 1) % _jingpinFrames.Length;
                    if (_jingpinFrames[_jingpinIdx] != null) _jingpinImg.sprite = _jingpinFrames[_jingpinIdx];
                }
            }

            // 滾輪捲動 (商城可見時)：一格 = 一列 (2 格出、2 格進)
            float sw = Input.mouseScrollDelta.y;
            if (sw != 0f) PageBy(sw < 0f ? 1 : -1);

            // 漸進建卡內縮圖：每幀最多 2 個，避免切分頁時一次建 8 個人形卡頓。
            for (int n = 0; n < 2 && _pendingCards.Count > 0; n++)
            {
                var pc = _pendingCards[0]; _pendingCards.RemoveAt(0);
                if (pc.Card != null) BuildCardPreview(pc.I, pc.Card, pc.Item);
            }

            for (int i = 0; i < PerPage; i++)
            {
                if (_cardAv[i] == null || _cardImg[i] == null) continue;
                bool hov = i == _hoverCard;
                float prevScale = _cardScale[i], prevAngle = _cardAngle[i];
                _cardScale[i] = Mathf.MoveTowards(_cardScale[i], hov ? CardEnlargeMax : 1f, Time.deltaTime * CardEnlargeRate);
                // 眼鏡卡不旋轉 (角度恆 0);其餘 hover 自轉、離開歸零 (官方 snap)。放大對所有卡都保留。
                _cardAngle[i] = (hov && !_cardNoSpin[i]) ? Mathf.Repeat(_cardAngle[i] + Time.deltaTime * CardSpinDegPerSec, 360f) : 0f;
                if (_cardScale[i] != prevScale) _cardImg[i].rectTransform.localScale = Vector3.one * _cardScale[i];   // 2D 放大 (不需重畫 RT)
                // 旋轉/回正才重畫 RT;但 炫 hair 卡的貼圖每幀在捲 V → 需每幀重畫,小圖才會持續變色 (AvatarUvScroll 已在動材質)。
                if (hov || _cardAngle[i] != prevAngle || _cardUvScroll[i]) RenderCard(i);
            }
        }

        private void UpdateWallet()
        {
            var w = _session.Wardrobe.Wallet;   // 底條上已有 H/M/G 圖示 → 只寫數字。M=Coins、G=Points (與 CurrencyZh 一致,見上)
            if (_mmine) _mmine.text = w.Coins.ToString();
            if (_gmine) _gmine.text = w.Points.ToString();
            if (_hmine) _hmine.text = w.Bonus.ToString();
        }

        // M/G 幣別 + 男/女 + 歷史鈕的持久狀態 (選中顯示 pushed=暗態；官方 CheckBox 語意)。
        private void RefreshToggles()
        {
            SetBtnSprite(_mBtn, _showM ? "Shop12.an" : "Shop11.an");
            SetBtnSprite(_gBtn, _showG ? "Shop14.an" : "Shop13.an");
            SetBtnSprite(_maleBtn,   _sex == ItemSex.Male   ? "Shop46.an" : "Shop45.an");   // 選中性別=pushed(暗) (#5)
            SetBtnSprite(_femaleBtn, _sex == ItemSex.Female ? "Shop48.an" : "Shop47.an");
            SetBtnSprite(_historyBtn, _showHistory ? "shop173.an" : "shop171.an");          // 歷史模式開啟=pushed (#6)
        }

        private static void SetBtnSprite(Button b, string an)
        {
            if (b != null && b.targetGraphic is Image img) UIKit.ApplySprite(img, ShopArt.An(an));
        }

        // ---- 右側捲軸 (slider) ----
        // 逐列捲動:d=±1 = 上/下移一列 (2 格出、2 格進)。列沒變 (已在頭/尾) → 不重建,否則滾到底每一格都 RefreshGrid
        // 重建 3D 縮圖 → 一直閃 (user #1)。捲動只需 RefreshGrid (start 依 _page 逐列滑窗)。
        private void PageBy(int d)
        {
            int np = Mathf.Clamp(_page + d, 0, _totalPages - 1);
            if (np == _page) return;
            _page = np; RefreshGrid();
        }

        // 依 _page/_totalPages (最上列索引/可捲步數) 把 Handle 定位在軌道上 (canvas y-down；只有一列就隱藏)。
        private void UpdateScrollHandle()
        {
            if (_scrollHandle == null) return;
            _scrollHandle.enabled = _totalPages > 1;
            var rt = _scrollHandle.rectTransform;
            float handleH = rt.sizeDelta.y > 0 ? rt.sizeDelta.y : 30f;
            float topY = -ScrollTop, botY = -(ScrollTop + ScrollTrackH - handleH);
            float t = _totalPages > 1 ? (float)_page / (_totalPages - 1) : 0f;
            rt.anchoredPosition = new Vector2(ScrollX, Mathf.Lerp(topY, botY, t));
        }

        // 拖動 Handle → 捲到某列：用滑鼠在軌道上的實際位置對應最上列 (位置式，clamp 在軌道內，不受螢幕縮放影響)。
        private void OnScrollDrag(BaseEventData ev)
        {
            if (!(ev is PointerEventData p) || _totalPages <= 1) return;
            var uiCam = FrontendApp.Instance != null ? FrontendApp.Instance.UiCam : null;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_root, p.position, uiCam, out var local)) return;
            float canvasY = 300f - local.y;                                     // _root 800×600 置中 → 轉成頂左 y-down (0..600)
            float frac = Mathf.Clamp01((canvasY - ScrollTop) / ScrollTrackH);   // 0(頂)..1(底)，clamp 不超出軌道
            int newRow = Mathf.Clamp(Mathf.RoundToInt(frac * (_totalPages - 1)), 0, _totalPages - 1);
            if (newRow != _page) { _page = newRow; RefreshGrid(); }             // RefreshGrid → UpdateScrollHandle 依最上列定位 handle
        }

        // ---- 3D 試穿預覽 ----

        private void BuildPreview()
        {
            if (_cam != null) return;
            int rtW = Mathf.RoundToInt(PreviewRectSize.x), rtH = Mathf.RoundToInt(PreviewRectSize.y);
            _rt = new RenderTexture(rtW, rtH, 16, RenderTextureFormat.ARGB32) { name = "ShopPreviewRT", antiAliasing = 4 };
            var camGo = new GameObject("ShopPreviewCam");
            _cam = camGo.AddComponent<Camera>();
            _cam.orthographic = false; _cam.fieldOfView = 32f;   // 垂直 fov：全身取景 (人物腳 y=0、頭骨 y~50、髮頂 y~58)
            _cam.nearClipPlane = 0.3f; _cam.farClipPlane = 3000f;
            _cam.cullingMask = 1 << PreviewLayer;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0f, 0f, 0f, 0f);    // 透空 → RawImage 疊在 Shop0.an 上，只露出人物
            _cam.targetTexture = _rt;
            // 偏移(oblique)視錐：只把近平面視窗的「上緣」往上推 PreviewTopHeadroom 個像素。身體 (下方 PreviewBodyH 像素)
            // 的投影完全不變 (fov=32 的原視窗原封不動),上方新增的透空區用來顯示翅膀/高髮。水平不動 → 身體寬度不變;
            // 方形像素 (水平/垂直每像素世界高相等) → 不變形。view 矩陣仍由 ApplyCamera 的 transform/LookAt 決定,互不影響。
            float halfV = _cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
            float pn = _cam.nearClipPlane;
            float top = pn * Mathf.Tan(halfV);                     // 身體取景的近平面上緣
            float bottom = -top;
            float perPx = (top - bottom) / PreviewBodyH;           // 近平面每像素世界高
            float right = top * (PreviewRectSize.x / PreviewBodyH);// 水平半寬 (依身體框比例 → 方形像素)
            float left = -right;
            float topExt = top + PreviewTopHeadroom * perPx;       // 上緣往上延伸,只加頭頂空間 (下緣/左右不動)
            _cam.projectionMatrix = Matrix4x4.Frustum(left, right, bottom, topExt, pn, _cam.farClipPlane);
            ApplyCamera();   // 相機在 -Z 後方、看向身體中心；hover 放大時在 Update 內拉近
            if (_previewImg != null) _previewImg.texture = _rt;
        }

        private void RebuildAvatar()
        {
            try
            {
                if (_avatarRoot != null) Destroy(_avatarRoot);
                _avatarRoot = new GameObject("ShopPreviewAvatar");
                _avatarRoot.transform.position = PreviewSpot;
                // 試穿整套 → 直接用該套組件覆蓋;否則依逐部位裝備解析。
                var parts = _tryOnOutfitParts != null
                    ? new List<string>(_tryOnOutfitParts)
                    : AvatarOutfit.ResolveParts(_sex, EquippedItems());
                // 左側「玩家假人」跟隨玩家自己的體型 (胖瘦)：商城切性別=切帳號(SetActive)，故 Active 對得上 _sex。
                float bodyB = SdoBodyShape.WeightFromIndex(Sdo.Settings.ProfileManager.Active.bodyShapeIndex, _sex == ItemSex.Male);
                var av = SdoRoomAvatar.Build(_avatarRoot, PreviewLayer, false, parts.ToArray(), AvatarOutfit.HrcFor(_sex), bodyWeight: bodyB);   // FEMALE/MALE.HRC
                if (av == null) { Destroy(_avatarRoot); _avatarRoot = null; return; }
                ApplyLeftPose(av);              // 左側穿的人 = WREST0072/MREST0082 自然站姿 idle (非假人 bind)
                foreach (var it in EquippedItems())
                    if (it.EquipSlot == EquipSlot.Expression) ForceLightExpressionFace(_avatarRoot, it);   // 試穿表情也統一最白膚色
                _previewFeetY = av.FeetYAt(0f); // 站姿最低頂點 → 腳對齊地面
                // 不再依「實測身高」正規化:穿含帽子/翅膀/高髮的套裝會撐高頂點 → 相機拉遠 → 人變小 (user:「RAIN挚爱穿了會變小」)。
                // 改用「性別固定身高」(女=RefHeight、男=RefHeight×MaleBodyRatio) → 穿任何套裝都同大小,且男生仍依身高正規化縮回
                // (修 #6「男生又變太大」——上輪固定成 RefHeight 反而移除了男生正規化)。男生最終大小微調 MaleSizeScale。
                _previewHeight = RefHeight * (_sex == ItemSex.Male ? MaleBodyRatio : 1f);
                ApplyCamera();
                ApplyPreviewRotation();         // 繞「身體中心」pivot 套 yaw/pitch (官方 pivot 在腰部，非腳底)
            }
            catch (System.Exception e) { Debug.LogWarning("[shop] preview build failed (non-fatal): " + e.Message); }
        }

        private IEnumerable<ShopItem> EquippedItems()
        {
            if (_catalog == null) yield break;
            foreach (var kv in _session.Wardrobe.Equipped)
            {
                var it = _catalog.ById(kv.Value);
                if (it != null && ItemTypes.SexFromCategory(it.Category) == _sex) yield return it;   // 只套目前性別的裝備
            }
        }

        // 在左側人物上按住拖動：水平 → 轉身 (yaw)、垂直 → 抬頭 (pitch，官方線上 clamp [-30,15])。
        private void OnPreviewDrag(BaseEventData ev)
        {
            if (!(ev is PointerEventData p)) return;
            // 官方版本差異 (AvtShow_ApplyDragRotateZoom)：
            //   離線 sdo_stand_alone.exe (FUN_0042fe80)：拖動只改 yaw(0x1e4 −=dx*0.4)，垂直量存 0x1f0 但從不使用 → 無 pitch。
            //   線上 sdo.bin        (FUN_0044f900)：yaw(0x308 −=dx*0.4) 且 pitch(0x304 −=dy*0.4，clamp[-30,15]，除非 mode@0x1b5==5)。
            // 我們照「線上」行為 (有 pitch)。倍率/clamp 由 Frida 實錄 shop_pitch_drag_online_log 確認：0.4/px、[-30,15]、mode=2。
            _dragAngle -= p.delta.x * DragDegPerPixel;
            // 滑鼠往上(Unity delta.y>0=往上) → 人往上抬 → _pitchAngle 增加(+15 上限)；往下 → -30 下限 (官方可下看多於上抬)。
            _pitchAngle = Mathf.Clamp(_pitchAngle + p.delta.y * PitchDegPerPixel, PitchMin, PitchMax);
            ApplyPreviewRotation();
        }

        // 繞「身體中心」pivot (PivotY) 套用 yaw+pitch —— 官方是繞 avatar 顯示節點原點 (落在腰/中身)，不是繞腳底。
        // 這樣抬 pitch 時人物是「原地微傾」而不是整個身體以腳為軸大幅甩動 (修 user 說的「傾斜基準不對/角度太多」)。
        private void ApplyPreviewRotation()
        {
            if (_avatarRoot == null) return;
            var pivot = PreviewSpot + new Vector3(0f, PivotY, 0f);
            var basePos = PreviewSpot + new Vector3(0f, -_previewFeetY, 0f);
            // 官方引擎 (sdo.bin 反編譯) 把節點旋轉建成 Q = quat(axis=(1,0,0),pitch) · quat(axis=(0,1,0),yaw)：
            //   先繞「世界 Y 軸」轉身，再繞「固定的世界 X 軸」抬頭 → 轉身後抬頭是「側邊抬起」，
            //   不是像 Quaternion.Euler(pitch,yaw,0) 那樣繞頭部朝向的局部軸點頭 (user 回饋)。
            //   軸常數 DAT_00581760=(1,0,0) / DAT_0058176c=(0,1,0) 已從 exe 驗過。
            float yawDeg = RoomMovement.FacingDegrees(2) + _dragAngle;
            var q = Quaternion.AngleAxis(_pitchAngle, Vector3.right) * Quaternion.AngleAxis(yawDeg, Vector3.up);
            _avatarRoot.transform.rotation = q;
            _avatarRoot.transform.position = pivot + q * (basePos - pivot);
        }

        private void ApplyCamera()
        {
            if (_cam == null) return;
            // 相機整組(eye 高度+距離、look 高度)按 實測身高/基準 縮放。腳釘在 PreviewSpot.y=0，模型佔 y∈[0,身高]，
            // 相機做等比相似縮放 → 高矮不同的模型在畫面上投影一樣大 (男=女)。RefHeight 是整體大小旋鈕。
            // 男生另乘 MaleSizeScale：基準放大 MaleSizeScale 倍 → 相機拉近 → 只有男生變大 (女生 refH 不變)。
            float refH = RefHeight * (_sex == ItemSex.Male ? MaleSizeScale : 1f);
            float k = _previewHeight / refH;
            _cam.transform.position = PreviewSpot + new Vector3(EyeFar.x, EyeFar.y * k, EyeFar.z * k);
            _cam.transform.LookAt(PreviewSpot + new Vector3(LookFar.x, LookFar.y * k, LookFar.z * k));
        }

        // ---- helpers ----

        // 官方左上角座標 (x,y) 放一張 sprite（世界畫布置中 y-up → anchorMin=max=(0,1), pivot=(0,1), pos=(x,-y)）
        private static Image AddArt(Transform parent, string name, Sprite s, float x, float y)
            => UIKit.AddSprite(parent, name, s, x, y);

        // onClick 可為 null (裝飾/尚無功能鈕 → 按了沒反應，但座標/美術仍照官方擺出)。
        // noSwap=true (分頁用)：關掉 Selectable 的 SpriteSwap，外觀完全由 base sprite (Lit/Dim) 決定 → 第一幀就正確亮暗，
        // 不會有「要點一次分頁才變亮」的狀態延遲。
        // hoverAn = 官方 bghover (滑過幀，通常最亮/發光)：沒給就沿用 pushed (舊行為)。給了 → 滑過變亮而非變暗 (修 #3/#4/#5「亮暗相反」)。
        // solo = orb 類鈕用自貼圖載入 (ShopArt.AnSolo) 去掉 atlas 鄰居滲出的白邊 (#1)。
        private Button SpriteBtn(Transform parent, string name, string normalAn, string pushedAn, float x, float y, UnityEngine.Events.UnityAction onClick = null, bool noSwap = false, string hoverAn = null, bool solo = false, string hoverSfx = null, float alphaHit = 0f)
        {
            System.Func<string, Sprite> res = solo ? (System.Func<string, Sprite>)ShopArt.AnSolo : ShopArt.An;
            var n = res(normalAn); var p = res(pushedAn);
            var h = hoverAn != null ? res(hoverAn) : p;
            var btn = UIKit.AddSpriteButton(parent, name, n, h, p, x, y);
            if (noSwap) btn.transition = Selectable.Transition.None;
            if (n == null)   // 缺圖 → 給個可點的透明區塊，仍能操作 (座標仍照官方)
            {
                var img = btn.targetGraphic as Image; if (img != null) { img.color = new Color(1, 1, 1, 0.001f); img.raycastTarget = true; }
                var rt = btn.GetComponent<RectTransform>(); rt.sizeDelta = new Vector2(48, 20);
            }
            // 圓球/圓角橫幅鈕：命中判定改「只有 α ≥ 門檻的像素才算點到」→ 透明四角不再誤觸 (使用者回報「部分區域不在按鈕裡面也會觸發」)。
            UIKit.SetAlphaHit(btn.targetGraphic, alphaHit);
            if (onClick != null) btn.onClick.AddListener(onClick);
            UiSfx.AttachPress(btn, UiSfx.Click);                      // 所有商城按鈕按下 → SE_0001
            if (hoverSfx != null) UiHoverSfx.Attach(btn, hoverSfx);   // 指定底排鈕滑過 → Buttonfloat
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

        private static Color Hex(uint argb)
            => new Color(((argb >> 16) & 0xff) / 255f, ((argb >> 8) & 0xff) / 255f, (argb & 0xff) / 255f, ((argb >> 24) & 0xff) / 255f);

        private static string CurrencyZh(ItemPriceCurrency c)
        {
            // 實查 iteminfo：PriceCat=1(Coins) 才是 M 幣 (如「寒風伴我 女装」永久 2640 = M 幣)、PriceCat=0(Points)=G 幣。
            // (之前 Coins→G / Points→M 標反了 → 也是「按 G 出 M list」的真根因。)
            switch (c) { case ItemPriceCurrency.Coins: return "M"; case ItemPriceCurrency.Bonus: return "H"; default: return "G"; }
        }

        private static long Now() => System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        private void SetVisible(bool on)
        {
            if (_cg != null) { _cg.alpha = on ? 1f : 0f; _cg.interactable = on; _cg.blocksRaycasts = on; }
            if (_cam != null) _cam.enabled = on;
            if (!on && _uiCam != null) { _uiCam.cullingMask = _savedUiMask; _uiCam = null; }   // 關商城 → 還原主 UI 相機遮罩
            // 關商城 → 若底下是男女選擇畫面(modal 不會重跑其 OnShow)，叫它用最新穿搭/性別刷新預覽 (hook 只在該畫面在底下時非 null；
            // 關回房間時為 null → 由 RefreshRoomAvatar 那條處理)。修「女角商城買衣穿上，回選性別畫面沒穿上、進 room 才有」。
            if (!on) Nav.RefreshGenderPreview?.Invoke();
        }

        private void OnDestroy()
        {
            if (_avatarRoot != null) Destroy(_avatarRoot);
            if (_rt != null) { _rt.Release(); Destroy(_rt); }
            DestroyCardPreviews();
            if (_cardCam != null) Destroy(_cardCam.gameObject);
        }
    }
}
