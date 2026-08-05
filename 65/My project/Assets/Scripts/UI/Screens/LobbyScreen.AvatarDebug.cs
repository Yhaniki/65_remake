using System.Globalization;
using UnityEngine;
using Sdo.UI.Util;

namespace Sdo.UI.Screens
{
    /// <summary>
    /// 大廳左側那尊 3D 角色的**即時調校**:面板本體是共用的 <see cref="AvatarTuner"/>(大廳按 <c>F4</c> 開,
    /// editor 限定),這裡只負責「把調出來的值套到大廳的畫面上」。個人資料視窗那尊走同一套(<c>F5</c>)。
    ///
    /// 🔴 調過的值存在 LocalPrefs、**build 版也照吃** —— 面板是 editor 限定,但載入不是,不然調完關掉又變回去。
    ///    要回到 <c>LobbyScreen.cs</c> 那組常數,按面板上的「重設」。
    /// </summary>
    public sealed partial class LobbyScreen
    {
        // 拖曳命中區跟著角色走 —— 記的是**比例**而不是另一組座標:角色挪到哪、放多大,熱區就等比跟到哪,
        // 不然人搬走之後就轉不動了(熱區留在原地),而那正是最容易被漏掉的一半。
        private const float AvDragRelX = (AvatarDragX - AvatarX) / AvatarW;
        private const float AvDragRelY = (AvatarDragY - AvatarY) / AvatarH;
        private const float AvDragRelW = AvatarDragW / AvatarW;
        private const float AvDragRelH = AvatarDragH / AvatarH;

        private AvatarTuner _avTuner;

        /// <summary>大廳角色的調校面板(F4)。第一次用到才建 —— 建構子會讀 LocalPrefs。</summary>
        private AvatarTuner AvTuner
        {
            get
            {
                if (_avTuner == null)
                {
                    _avTuner = new AvatarTuner("lobby.avatar", "大廳角色 位置 / 大小", "Avatar",
                                               KeyCode.F4, 65091,
                                               AvatarX, AvatarY, AvatarW, AvatarH, AvatarFillFrac);
                    _avTuner.Applied = ApplyAvatarTuning;
                    _avTuner.ShowHitBox = ShowAvatarDragBox;
                    _avTuner.ExtraCode = AvatarDragCode;
                }
                return _avTuner;
            }
        }

        /// <summary>給 <see cref="ShowAvatar"/> 用的取景比例:沒調過就是程式碼裡的 <see cref="AvatarFillFrac"/>。</summary>
        private float AvFill => AvTuner.Fill;

        /// <summary>
        /// 把目前的調校值擺到畫面上:角色的 RawImage、跟著它走的轉身熱區,還有相機取景。
        /// 🔴 fillFrac 改了一定要 <c>GenderPreview3D.Reframe</c> —— 那個值是在取景當下算進相機距離的,
        ///    光是寫欄位不會有任何變化(而且 <c>SetGender</c> 同性別會直接 no-op,借不到它重算)。
        /// </summary>
        private void ApplyAvatarTuning()
        {
            var t = AvTuner;
            float w = t.W, h = t.H;

            if (_previewImg != null) PlaceTopLeft(_previewImg.rectTransform, t.X, t.Y, w, h);
            if (_avatarDrag != null)
                PlaceTopLeft(_avatarDrag.rectTransform,
                             t.X + AvDragRelX * w, t.Y + AvDragRelY * h, AvDragRelW * w, AvDragRelH * h);

            if (_preview != null && !Mathf.Approximately(_preview.fillFrac, t.Fill))
            {
                _preview.fillFrac = t.Fill;
                _preview.Reframe();
            }
        }

        /// <summary>離開大廳時把調校值真的寫進磁碟,順手把熱區的紅框收掉。</summary>
        private void FlushAvatarTuning() => AvTuner.CloseWindow();

        /// <summary>F4 開關面板;開著時方向鍵移位置(Shift ×10)、PageUp/PageDown 縮放。</summary>
        private void AvatarDebugUpdate()
        {
            // 方向鍵在聊天輸入框有焦點時不能吃:不然一邊打字一邊就把角色推到畫面外去了。
            // F4 本身不受這條限制 —— 功能鍵不會在輸入框裡產生字元(同 RoomScreen 的 F3)。
            bool typing = _chatInput != null && _chatInput.isFocused;
            // 個人資料視窗疊在大廳上時,鍵盤讓給它那塊面板(它自己有一顆 F5,兩塊同時吃鍵會很難用)。
            AvTuner.HandleKeys(!PlayerInfoIsOpen, !typing);
        }

        private void OnGUI() => AvTuner.Draw(Visible && !PlayerInfoIsOpen);

        /// <summary>個人資料視窗現在開著嗎 —— 它是疊在大廳上的 modal,開著時大廳的調校面板要讓路。</summary>
        private bool PlayerInfoIsOpen => FrontendApp.Instance != null && FrontendApp.Instance.PlayerInfoOpen;

        private void ShowAvatarDragBox(bool on)
        {
            if (_avatarDrag == null) return;
            // 熱區本來就是一塊透明的接盤(raycastTarget=true、alpha=0),染紅只是把它畫出來給人看,
            // 不影響它照樣收拖曳事件。
            _avatarDrag.color = on ? new Color(1f, 0.25f, 0.25f, 0.22f) : new Color(0f, 0f, 0f, 0f);
        }

        /// <summary>「複製 const」時附在後面的那行:熱區是等比跟著角色走的,所以由這裡現算。</summary>
        private string AvatarDragCode()
        {
            var ic = CultureInfo.InvariantCulture;
            var t = AvTuner;
            float w = t.W, h = t.H;
            float dx = t.X + AvDragRelX * w, dy = t.Y + AvDragRelY * h;
            return $"private const float AvatarDragX = {dx.ToString("0.##", ic)}f, AvatarDragY = {dy.ToString("0.##", ic)}f, "
                 + $"AvatarDragW = {(AvDragRelW * w).ToString("0.##", ic)}f, AvatarDragH = {(AvDragRelH * h).ToString("0.##", ic)}f;";
        }
    }
}
