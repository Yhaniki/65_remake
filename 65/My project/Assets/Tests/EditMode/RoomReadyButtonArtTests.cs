using NUnit.Framework;
using UnityEngine;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>
    /// 房間右下角那顆球的兩張面孔:<c>Room12/13/14</c>「準備」與 <c>c_ready0/1/2</c>「取消」。
    ///
    /// 官方把兩顆球烘在同一張圖集裡(WaitingRoom.png:準備 y=313、取消 y=385,就在正下方一列),
    /// 各自的 .an 只是不同的裁切。裁錯一格的症狀是「按了準備,球上的字沒變」—— 跟狀態沒同步
    /// 長得一模一樣,所以這裡把「兩張確實是不同的圖、而且一樣大」釘住。
    /// </summary>
    public class RoomReadyButtonArtTests
    {
        [Test]
        public void Cancel_Ball_Exists_And_Matches_The_Ready_Ball_Size()
        {
            var ready = RoomUiArt.An("Room12");
            if (ready == null) Assert.Ignore("ROOM 美術不在這個環境裡(沒有 DATA root)。");

            var cancel = RoomUiArt.An("c_ready0");
            Assert.IsNotNull(cancel, "c_ready0 載不到 —— DATA/UI/ROOM 少了取消鈕的 .an");
            // 同一格版位(Win3 706,43)上換圖:尺寸不一樣就會位移/被拉爆。
            Assert.AreEqual(ready.rect.width, cancel.rect.width, 0.5f, "取消球的寬度要與準備球一致");
            Assert.AreEqual(ready.rect.height, cancel.rect.height, 0.5f, "取消球的高度要與準備球一致");
        }

        [Test]
        public void Cancel_Ball_Is_Not_The_Same_Crop_As_Ready()
        {
            var ready = RoomUiArt.An("Room12");
            if (ready == null) Assert.Ignore("ROOM 美術不在這個環境裡。");
            var cancel = RoomUiArt.An("c_ready0");
            Assert.IsNotNull(cancel);

            // 兩張球底色相同、差別只在中間那兩個字 → 比「不透明像素的分佈」才看得出來。
            Assert.AreNotEqual(OpaqueSignature(ready), OpaqueSignature(cancel),
                "取消鈕裁到了跟準備鈕同一塊圖(檢查 C_READY0.AN 的裁切座標)");
        }

        /// <summary>
        /// 把 sprite 的不透明像素分佈壓成一個數 —— 同一塊裁切必然相同,不同的字必然不同。
        /// **只讀 sprite 自己那一塊**:<see cref="RoomUiArt.An"/> 走共享底圖快取,兩個 .an 的
        /// <c>sprite.texture</c> 是同一張 1024×1024 圖集,整張讀的話任何兩個 crop 都會「相同」。
        /// </summary>
        private static int OpaqueSignature(Sprite s)
        {
            var r = s.textureRect;
            var px = s.texture.GetPixels((int)r.x, (int)r.y, (int)r.width, (int)r.height);
            int h = 17;
            for (int i = 0; i < px.Length; i++)
                if (px[i].a > 0.5f)
                    h = h * 31 + (Mathf.RoundToInt(px[i].r * 255f) + Mathf.RoundToInt(px[i].g * 255f) * 3 + i);
            return h;
        }
    }
}
