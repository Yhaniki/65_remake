using UnityEngine;
using UnityEngine.UI;
using Sdo.Game;
using Sdo.Net;          // NetAvatarLook(看別人時套對方真正的穿搭)
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
    ///
    /// 🔴 **這尊正對鏡頭,而且不給滑鼠拖曳轉身**(使用者要求):
    ///   • <c>avatarYaw = 0</c> —— 官方 AvtShow 那個朝左 30°(<c>0x2b4</c>)是**大廳**那尊的姿;
    ///     個人資料是拿來看一個人長什麼樣的,側身會擋掉半邊穿搭。
    ///   • 沒有轉身熱區 —— 連帶不必再算那塊「跟著角色走」的命中區,也就沒有它壓到分頁條(329 起)或
    ///     左側功能鈕(296)的風險。**大廳那尊照舊拖得動**(那邊是官方行為):yaw 與拖曳都是實例層級的,
    ///     兩邊互不影響。
    /// </summary>
    public sealed partial class PlayerInfoModal
    {
        // ---- 版位 ----
        // 起點是官方 AvtShow(105,111,230,391)補成 2:3 的框;下面這組是**使用者用 F5 面板調出來的落點**
        // (往左 3、往下 36.7)—— 官方那個座標是配「名字疊在角色左上角」的排版,我們的名字是靠右對齊的
        // 動態字,人站高一點會與那兩行字打架。大小沒動(仍是 0.6517 倍的槽位)。
        private const float InfoAvatarX = 86.62f, InfoAvatarY = 147.71f, InfoAvatarW = 260.67f, InfoAvatarH = 391.01f;

        /// <summary>角色佔預覽高度的比例。官方那個框裡的人幾乎頂天立地 → 比大廳(0.605)高一些。
        /// 實際看得到的人比 <c>FrameTo</c> 算的 bodyTop 還高約 9%(髮型高過頭骨、idle 會抬腳)——
        /// 0.78 × 391 × 1.09 ≈ 332px,約佔框高的 85%。要調就按 F5 拖滑桿,不要用算的。</summary>
        private const float InfoAvatarFillFrac = 0.78f;

        /// <summary>這尊面朝哪 —— **0 = 正對鏡頭**(使用者要求)。大廳那尊維持官方 AvtShow 的 −30°
        /// (<see cref="GenderPreview3D.avatarYaw"/> 的預設),兩邊各自設定,互不影響。</summary>
        private const float InfoAvatarYaw = 0f;

        private GenderPreview3D _preview;
        private RawImage _previewImg;
        private Camera _maskedCam;
        private int _savedMask;
        /// <summary>上一次顯示的是「自己」嗎 —— 換了對象才需要重套穿搭(重建兩隻角色不便宜)。</summary>
        private bool _previewSelf, _previewBuilt;

        /// <summary>
        /// 現在身上套的是**別人的**穿搭嗎(<see cref="ShowRemoteAvatar"/> 套上去的)?
        ///
        /// 需要它的理由與 <see cref="_previewSelf"/> 一樣:<see cref="ShowInfoAvatar"/> 的「看別人」那條路
        /// 原本假設「上一次不是自己 ⇒ 身上就是預設整套,不用重套」。名片會把真的穿搭套上去之後那個假設就不成立了
        /// —— 少了這個旗標,連看兩個人時第二個人會**穿著第一個人的衣服**,而且看起來完全正常。
        /// </summary>
        private bool _previewRemote;
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
                    // 沒有 ShowHitBox / ExtraCode:這尊不給拖曳 → 沒有熱區可顯示,也沒有第三行 const 要產。
                    _infoTuner.Applied = ApplyInfoAvatarTuning;
                }
                return _infoTuner;
            }
        }

        // ================================================================ 版面

        /// <summary>
        /// 建角色的畫布。**一定要在 <c>BuildIdentity</c> 之前呼叫** —— 官方把名字/等級疊在角色的左上角,
        /// 後建的才畫在上面;顛倒過來名字會被角色蓋掉。
        ///
        /// 🔴 這裡**不建轉身熱區**(使用者要求關掉拖曳)。RawImage 自己 <c>raycastTarget = false</c>,
        ///    所以這一整塊蓋在板子上也不吃射線,底下那些鈕照樣按得到。
        /// </summary>
        private void BuildAvatar(RectTransform parent)
        {
            var rt = UIKit.NewRect(parent, "AvatarView");
            _previewImg = rt.gameObject.AddComponent<RawImage>();
            _previewImg.raycastTarget = false;
            _previewImg.color = new Color(1f, 1f, 1f, 0f);   // 還沒有 RT 之前不要畫一塊白
            Place(rt, InfoAvatarX, InfoAvatarY, InfoAvatarW, InfoAvatarH);

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
                // 🔴 yaw 也要在 Build 之前:BuildAvatar 擺人的時候就把它寫進 localRotation 了。
                _preview.avatarYaw = InfoAvatarYaw;   // 0 = 正對鏡頭(大廳那尊才是官方的 −30°)
                _preview.Build(gender, fParts, mParts, fBody, mBody);
                _previewBuilt = true;
                _previewSelf = self;
            }
            else
            {
                _preview.gameObject.SetActive(true);
                // 看自己:穿搭可能剛換過(去商城買了東西)→ 每次都重套。
                // 看別人:「上一次是自己」或「上一次身上是別人的穿搭」時要換回預設整套 ——
                //   後者少了的話,連看兩個人時第二個會穿著第一個人的衣服(見 _previewRemote)。
                if (self || _previewSelf || _previewRemote) _preview.SetOutfits(gender, fParts, mParts, fBody, mBody);
                _previewSelf = self;
            }

            _preview.SetGender(gender);
            _preview.ResetOrbit();   // 回到 avatarYaw(這裡是 0:正面),pitch 也歸零
            _previewRemote = false;

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
        /// 名片回來了 → 把預覽角色換成**對方真正的穿搭**(<c>setLook</c> 那份,與房間 3D 建他角色的來源相同)。
        ///
        /// 之前這裡永遠是預設整套,因為 client 之間拿不到彼此的穿搭 —— 現在 server 有了,就用它。
        ///
        /// 🔴 只在 <see cref="ShowInfoAvatar"/> 已經跑過(<see cref="_previewBuilt"/>)之後才有意義:
        ///    名片是非同步回來的,而視窗一定是先開起來的,所以正常順序就是這樣;沒建好就安靜地不做事。
        /// 🔴 性別以**對方的 look** 為準,不是呼叫端猜的那個 —— look 是對方自己報的,那是唯一的權威。
        /// </summary>
        private void ShowRemoteAvatar(NetAvatarLook look)
        {
            if (look == null || _preview == null || !_previewBuilt) return;

            int gender = look.Male ? 1 : 0;
            // GenderPreview3D 一次養兩隻(男/女),所以只餵對方那一邊,另一邊給 null = 維持預設。
            string[] fParts = gender == 0 ? look.Parts : null;
            string[] mParts = gender == 1 ? look.Parts : null;
            int fBody = gender == 0 ? look.BodyIndex : 0;
            int mBody = gender == 1 ? look.BodyIndex : 0;

            _preview.SetOutfits(gender, fParts, mParts, fBody, mBody);
            _preview.SetGender(gender);
            _previewSelf = false;
            _previewRemote = true;
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
            InfoTuner.CloseWindow();   // 面板收掉 + 調校值落地
        }

        private void OnDestroy()
        {
            if (_maskedCam != null) { _maskedCam.cullingMask = _savedMask; _maskedCam = null; }
            if (_preview != null) { Destroy(_preview.gameObject); _preview = null; }
        }

        // ================================================================ 調校面板(F5)

        /// <summary>把調校值擺到畫面上:角色的 RawImage 與相機取景(這尊沒有熱區,不給拖曳)。</summary>
        private void ApplyInfoAvatarTuning()
        {
            var t = InfoTuner;
            if (_previewImg != null) Place(_previewImg.rectTransform, t.X, t.Y, t.W, t.H);

            // fillFrac 改了一定要 Reframe:那個值是在取景當下算進相機距離的,光是寫欄位不會有任何變化。
            if (_preview != null && _previewBuilt && !Mathf.Approximately(_preview.fillFrac, t.Fill))
            {
                _preview.fillFrac = t.Fill;
                _preview.Reframe();
            }
        }

        /// <summary>F5 開關調校面板(只在視窗開著時收鍵)。由 <c>Update</c> 呼叫。</summary>
        private void InfoAvatarUpdate() => InfoTuner.HandleKeys(IsOpen);

        private void OnGUI() => InfoTuner.Draw(IsOpen);
    }
}
