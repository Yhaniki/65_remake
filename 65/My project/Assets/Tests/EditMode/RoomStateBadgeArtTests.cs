using System.IO;
using NUnit.Framework;
using Sdo.Game;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>
    /// 房間頭貼的狀態徽章(NO SONG / PLAYING,<c>tools/make_room_state_badges.py</c> 烘的)真的到得了執行期嗎。
    ///
    /// 🔴 為什麼要有這個測試:新素材放在 <c>art/generated</c>,要靠**兩支**打包腳本各自複製一份
    /// (<c>package_build.ps1</c> 與 <c>build_clean_data.ps1</c>)。只接一支的結果是
    /// 「編輯器裡有圖、打包版沒圖」—— 而那要等到出貨才會發現。
    /// 這條測試在編輯器這一側把它釘住;打包那一側靠實機截圖。
    ///
    /// 找不到 DATA root 就 skip(同 <c>RoomDlgArtTests</c> 的慣例)—— CI 上沒有資料夾很正常。
    /// </summary>
    public class RoomStateBadgeArtTests
    {
        private static string RoomDir()
        {
            string root;
            try { root = SdoExtracted.Root; } catch { return null; }
            if (string.IsNullOrEmpty(root)) return null;
            var dir = Path.Combine(root, "UI", "ROOM");
            return Directory.Exists(dir) ? dir : null;
        }

        [Test]
        public void The_Missing_And_Playing_Badges_Exist_In_The_Data_Root()
        {
            var dir = RoomDir();
            if (dir == null) Assert.Ignore("找不到 DATA/UI/ROOM —— 這台沒有資料夾");

            foreach (var name in new[] { "MISSING", "PLAYING" })
            {
                var an = Path.Combine(dir, name + ".AN");
                Assert.IsTrue(File.Exists(an),
                    name + ".AN 不在 " + dir + " —— 跑 tools/make_room_state_badges.py --apply,"
                    + "並確認 art/generated 有被兩支打包腳本複製");
                Assert.IsTrue(File.Exists(Path.Combine(dir, name + ".PNG")), name + ".PNG 不見了");
            }
        }

        [Test]
        public void The_Badges_Load_As_Sprites()
        {
            // .AN 存在不代表載得進來(裁切框寫錯 / png 檔名對不上都會回 null,而且是靜默的)。
            if (RoomDir() == null) Assert.Ignore("找不到 DATA/UI/ROOM");

            foreach (var name in new[] { "missing", "playing" })
            {
                var sprite = RoomUiArt.An(name);
                Assert.IsNotNull(sprite, name + " 的 .AN 載不出 sprite —— 檢查裁切框與 png 檔名");
                Assert.Greater(sprite.rect.width, 0f);
                Assert.Greater(sprite.rect.height, 0f);
            }
        }
    }
}
