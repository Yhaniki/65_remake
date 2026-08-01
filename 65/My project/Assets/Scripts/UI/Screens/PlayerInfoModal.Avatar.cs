using System.Globalization;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Sdo.Game;
using Sdo.Settings;
using Sdo.UI.Util;

namespace Sdo.UI.Screens
{
    /// <summary>
    /// 個人資料視窗左半邊那尊 **3D 角色**(官方 <c>&lt;AvtShow name="AvatarShow" x="105" y="111" w="230" h="391"&gt;</c>)。
    ///
    /// 做法與大廳左側那尊**完全相同**:<see cref="GenderPreview3D"/> 自己開一台相機把角色渲進一張透明的
    /// RenderTexture,這裡只是把那張貼圖掛到 RawImage 上;位置/大小由 <c>F5</c> 的調校面板決定
    /// (共用 <see cref="AvatarTuner"/>,見那邊的說明)。
    ///
    /// 🔴 **官方那個框是 230×391,不是 2:3。** <c>GenderPreview3D.SlotW/SlotH</c> 釘死了相機 aspect,
    ///    RawImage 的比例一旦不是 2:3 角色就被拉扁 —— 所以這裡取「高度對齊官方框(391)、寬度照 2:3 補到
    ///    260.67」,再把框往左推 15.33 讓**中心**與官方框的中心重合。多出來的左右兩側是透明的留白,
    ///    角色本身只佔 RT 中間約三分之一寬,不會壓到旁邊的分頁板(335 起)。
    ///
    /// 🔴 **看別人時我們沒有他的穿搭**(server 的座位快照只帶得到 Id / 名字 / 等級 / 家族),所以顯示的是
    ///    **預設整套**;連性別都只能用呼叫端傳進來的那個值,而 <c>RoomScreen.SeatGender</c> 查不到時會退回
    ///    本機的性別(見 <see cref="Open"/> 的註解)—— 也就是說看別人時那尊的性別**可能是錯的**。
    ///    這是目前這套連線拿得到的資料上限,不是這裡的 bug;等封包帶得到對方穿搭再接上就會自動正確。
    ///    (使用者要求「人要顯示出來」,空著一塊比顯示一個預設造型更難看。)
    ///
    /// 🔴 兩尊角色會**同時活著**(大廳那尊在背景、這尊在視窗裡)。它們共用 PreviewLayer,以前 park 點是
    ///    static 的定點 → 兩尊疊在一起、兩台相機互相拍到對方,兩張 RT 都出現鬼影。現在
    ///    <see cref="GenderPreview3D"/> 會給每個實例分一個停車位(見那邊 ParkBase 的註解),不必特地把
    ///    大廳那尊藏起來。
    /// </summary>
    public sealed partial class PlayerInfoModal
    {
        // ---- 版位(官方 AvtShow 105,111,230,391 → 補成 2:3 之後的框)----
        private const float InfoAvatarX = 89.67f, InfoAvatarY = 111f, InfoAvatarW = 260.67f, InfoAvatarH = 391f;

        /// <summary>角色佔預覽高度的比例。官方那個框裡的人幾乎頂天立地 → 比大廳(0.605)高一些。
        /// 實際看得到的人比 <c>FrameTo</c> 算的 bodyTop 還高約 9%(髮型高過頭骨、idle 會抬腳)——
        /// 0.78 × 391 × 1.09 ≈ 332px,約佔框高的 85%。要調就按 F5 拖滑桿,不要用算的。</summary>
        private const float InfoAvatarFillFrac = 0.78f;

        // 「按住拖動轉身」的命中區。🔴 **不能**用 RawImage 整塊:它右緣到 350,會蓋住分頁條(329 起)
        // 與左側那排功能鈕(VipX 296),那幾顆就按不動了。這裡只圈住角色本人:右緣 277,離 296 還有 19px。
        private const float InfoAvatarDragX = 162.7f, InfoAvatarDragY = 130.6f,
                            InfoAvatarDragW = 114.7f, InfoAvatarDragH = 351.9f;

        // 熱區跟著角色走(同大廳):記比例,角色挪到哪、放多大,熱區就等比跟到哪。
        private const float InfoDragRelX = (InfoAvatarDragX - InfoAvatarX) / InfoAvatarW;
        private const float InfoDragRelY = (InfoAvatarDragY - InfoAvatarY) / InfoAvatarH;
        private const float InfoDragRelW = InfoAvatarDragW / InfoAvatarW;
        private const float InfoDragRelH = InfoAvatarDragH / InfoAvatarH;

        private GenderPreview3D _preview;
        private RawImage _previewImg;
        private Image _previewDrag;
        private Camera _maskedCam;
        private int _savedMask;
        /// <summary>上一次顯示的是「自己」嗎 —— 換了對象才需要重套穿搭(重建兩隻角色不便宜)。</summary>
        private bool _previewSelf, _previewBuilt;
        private AvatarTuner _infoTuner;

        private AvatarTuner InfoTuner
        {
            get
            {
                if (_infoTuner == null)
                {
                    _infoTuner = new AvatarTuner("playerinfo.avatar", "個人資料角色 位置 / 大小", "InfoAvatar",
                                                 KeyCode.F5, 65092,
                                                 InfoAvatarX, InfoAvatarY, InfoAvatarW, InfoAvatarH,
                                                 InfoAvatarFillFrac, winHomeX: 420f, winHomeY: 12f);
                    _infoTuner.Applied = ApplyInfoAvatarTuning;
                    _infoTuner.ShowHitBox = ShowInfoAvatarDragBox;
                    _infoTuner.ExtraCode = InfoAvatarDragCode;
                }
                return _infoTuner;
            }
        }

        // ================================================================ 版面

        /// <summary>
        /// 建角色的畫布與轉身熱區。**一定要在 <c>BuildIdentity</c> 之前呼叫** —— 官方把名字/等級疊在角色的
        /// 左上角,後建的才畫在上面;顛倒過來名字會被角色蓋掉。
        /// </summary>
        private void BuildAvatar(RectTransform parent)
        {
            var rt = UIKit.NewRect(parent, "AvatarView");
            _previewImg = rt.gameObject.AddComponent<RawImage>();
            _previewImg.raycastTarget = false;   // 蓋在板子上但不吃射線,底下的鈕照樣按得到
            _previewImg.color = new Color(1f, 1f, 1f, 0f);   // 還沒有 RT 之前不要畫一塊白
            Place(rt, InfoAvatarX, InfoAvatarY, InfoAvatarW, InfoAvatarH);

            _previewDrag = UIKit.AddImage(parent, "AvatarDrag", new Color(0f, 0f, 0f, 0f), raycast: true);
            Place(_previewDrag.rectTransform, InfoAvatarDragX, InfoAvatarDragY, InfoAvatarDragW, InfoAvatarDragH);
            var trig = _previewDrag.gameObject.AddComponent<EventTrigger>();
            var entry = new EventTrigger.Entry { eventID = EventTriggerType.Drag };
            entry.callback.AddListener(ev =>
            {
                if (_preview != null && ev is PointerEventData p) _preview.Orbit(p.delta);
            });
            trig.triggers.Add(entry);

            ApplyInfoAvatarTuning();   // 版位改由調校值決定(預設就是上面那組常數)
        }

        // ================================================================ 顯示 / 收起

        /// <summary>
        /// 開視窗時把角色叫出來。<paramref name="self"/> = 看自己(用本機的實際穿搭與體型);
        /// 看別人時一律預設整套(拿不到對方的穿搭,見類別註解)。
        ///
        /// 🔴 一定要把 PreviewLayer 從前端 UI 相機的 cullingMask 遮掉 —— 那台相機幾乎什麼都照,沒遮的話
        ///    角色會被它用正交投影再畫一次(扁扁的疊在畫面上)。大廳已經遮過了也沒關係:這裡存的是
        ///    「進來時的值」,還原時寫回去,不會把大廳的遮罩弄丟。從房間開這個視窗時房間**沒有**遮它。
        /// </summary>
        private void ShowInfoAvatar(int gender, bool self)
        {
            gender = gender == 1 ? 1 : 0;
            string[] fParts = self ? AvatarOutfits.PartsForGender(0) : null;
            string[] mParts = self ? AvatarOutfits.PartsForGender(1) : null;
            int fBody = self ? AvatarOutfits.BodyIndexForGender(0) : 0;
            int mBody = self ? AvatarOutfits.BodyIndexForGender(1) : 0;

            if (_preview == null)
            {
                var go = new GameObject("PlayerInfoAvatar3D");
                _preview = go.AddComponent<GenderPreview3D>();
                // 🔴 取景參數要在 Build **之前**設(Build 最後會 SetGender → FrameTo,那一刻相機距離就定下來了),
                //    而且那兩個偏移要歸零 —— 它們是官方 LOBBYSEL 那個 400×600 預覽框專用的校正值,
                //    在別的框裡只會讓角色相對取景窗往下偏(大廳踩過這個坑,見 LobbyScreen.ShowAvatar)。
                _preview.avatarYOffset = 0f;
                _preview.verticalBias = 0f;
                _preview.fillFrac = InfoTuner.Fill;
                _preview.Build(gender, fParts, mParts, fBody, mBody);
                _previewBuilt = true;
                _previewSelf = self;
            }
            else
            {
                _preview.gameObject.SetActive(true);
                // 看自己:穿搭可能剛換過(去商城買了東西)→ 每次都重套。
                // 看別人:只有「上一次是自己」時才需要換回預設整套,連看兩個人不必重建。
                if (self || _previewSelf) _preview.SetOutfits(gender, fParts, mParts, fBody, mBody);
                _previewSelf = self;
            }

            _preview.SetGender(gender);
            _preview.ResetOrbit();   // 每次開窗都回到官方那個朝左 30° 的預設姿

            if (_previewImg != null && _preview.PreviewTexture != null)
            {
                _previewImg.texture = _preview.PreviewTexture;
                _previewImg.color = Color.white;
            }
            ApplyInfoAvatarTuning();

            var ui = FrontendApp.Instance != null ? FrontendApp.Instance.UiCam : null;
            if (ui != null && _maskedCam == null)
            {
                _maskedCam = ui;
                _savedMask = ui.cullingMask;
                ui.cullingMask &= ~(1 << GenderPreview3D.PreviewLayer);
            }
        }

        /// <summary>
        /// 關視窗時收起來。🔴 **只停用、不拆掉** —— 這個視窗是拿來反覆開關看人的,每次重建一整套骨骼 + 貼圖
        /// 會卡一下;停用之後相機不渲染、角色不更新,成本只剩一張閒置的 RT。真正的拆除在 <see cref="OnDestroy"/>。
        /// </summary>
        private void HideInfoAvatar()
        {
            if (_maskedCam != null) { _maskedCam.cullingMask = _savedMask; _maskedCam = null; }
            if (_preview != null) _preview.gameObject.SetActive(false);
            if (_previewImg != null) _previewImg.color = new Color(1f, 1f, 1f, 0f);
            InfoTuner.CloseWindow();   // 面板收掉 + 熱區的紅框拿掉 + 調校值落地
        }

        private void OnDestroy()
        {
            if (_maskedCam != null) { _maskedCam.cullingMask = _savedMask; _maskedCam = null; }
            if (_preview != null) { Destroy(_preview.gameObject); _preview = null; }
        }

        // ================================================================ 調校面板(F5)

        /// <summary>把調校值擺到畫面上:角色的 RawImage、跟著它走的轉身熱區,還有相機取景。</summary>
        private void ApplyInfoAvatarTuning()
        {
            var t = InfoTuner;
            float w = t.W, h = t.H;

            if (_previewImg != null) Place(_previewImg.rectTransform, t.X, t.Y, w, h);
            if (_previewDrag != null)
                Place(_previewDrag.rectTransform,
                      t.X + InfoDragRelX * w, t.Y + InfoDragRelY * h, InfoDragRelW * w, InfoDragRelH * h);

            // fillFrac 改了一定要 Reframe:那個值是在取景當下算進相機距離的,光是寫欄位不會有任何變化。
            if (_preview != null && _previewBuilt && !Mathf.Approximately(_preview.fillFrac, t.Fill))
            {
                _preview.fillFrac = t.Fill;
                _preview.Reframe();
            }
        }

        private void ShowInfoAvatarDragBox(bool on)
        {
            if (_previewDrag == null) return;
            _previewDrag.color = on ? new Color(1f, 0.25f, 0.25f, 0.22f) : new Color(0f, 0f, 0f, 0f);
        }

        /// <summary>「複製 const」時附在後面的那行:熱區是等比跟著角色走的,所以由這裡現算。</summary>
        private string InfoAvatarDragCode()
        {
            var ic = CultureInfo.InvariantCulture;
            var t = InfoTuner;
            float w = t.W, h = t.H;
            float dx = t.X + InfoDragRelX * w, dy = t.Y + InfoDragRelY * h;
            return $"private const float InfoAvatarDragX = {dx.ToString("0.##", ic)}f, InfoAvatarDragY = {dy.ToString("0.##", ic)}f,\n"
                 + $"                    InfoAvatarDragW = {(InfoDragRelW * w).ToString("0.##", ic)}f, "
                 + $"InfoAvatarDragH = {(InfoDragRelH * h).ToString("0.##", ic)}f;";
        }

        /// <summary>F5 開關調校面板(只在視窗開著時收鍵)。由 <c>Update</c> 呼叫。</summary>
        private void InfoAvatarUpdate() => InfoTuner.HandleKeys(IsOpen);

        private void OnGUI() => InfoTuner.Draw(IsOpen);
    }
}
