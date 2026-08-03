using System.IO;
using NUnit.Framework;
using UnityEngine;
using Sdo.UI.Util;

namespace Sdo.Tests
{
    /// <summary>
    /// 選男女畫面新加的「開房 / 加入」三態鈕(<c>LOBBYSEL200..205</c>)。
    ///
    /// 這些圖原版沒有,是照官方 <c>LOBBYSEL29..31</c>(LOGIN/登入)的底板重烘的
    /// (<c>tools/make_lobbysel_room_buttons.py</c>:抹掉字 → 用官方量到的版位把新字烘回去)。
    ///
    /// 🔴 這個檔存在的主要理由是**第一條測試**:新素材必須同時接進 <c>package_build.ps1</c> 與
    /// <c>build_clean_data.ps1</c> 兩支腳本。只接一支的症狀是「編輯器裡看得到圖、打包版沒圖」——
    /// 那要等到出貨(或使用者截圖)才會被發現,而且看起來像程式 bug 而不是打包漏了一步。
    /// </summary>
    public class RoomButtonArtTests
    {
        /// <summary>建立房間 / 加入房間 各三態(normal / hover / pushed)。</summary>
        private static readonly string[] Baked =
        {
            "LobbySel200", "LobbySel201", "LobbySel202",
            "LobbySel203", "LobbySel204", "LobbySel205",
        };

        /// <summary>官方的 LOGIN 鈕 —— 新鈕的底板來源,尺寸與版位一律以它為準。</summary>
        private const string OfficialPlate = "LobbySel29";

        // Application.dataPath = <repo>/65/My project/Assets(EditMode run)
        private static string RepoRoot
            => new DirectoryInfo(Application.dataPath).Parent.Parent.Parent.FullName;

        [Test]
        public void Both_Build_Scripts_Overlay_The_Generated_Art_Tree()
        {
            // art\generated 與 art\upscaled 是兩棵不同語意的樹(取代既有檔 / 全新檔名),
            // 兩支打包腳本都各自 overlay 一次。少接一邊 = 打包版缺圖。
            foreach (var name in new[] { "package_build.ps1", "build_clean_data.ps1" })
            {
                string p = Path.Combine(RepoRoot, "tools", name);
                Assert.IsTrue(File.Exists(p), "找不到打包腳本:" + p);
                StringAssert.Contains(@"art\generated", File.ReadAllText(p),
                    name + " 沒有 overlay art\\generated —— 新烘的按鈕在這條管線裡會缺圖");
            }
        }

        [Test]
        public void Generated_Overlay_Tree_Holds_All_Six_Frames()
        {
            // 這條測的是 repo 裡的 overlay 樹(打包的來源),不是已經 copy 進 DATA 的結果 ——
            // 「忘了跑烘圖工具就 commit」在這裡就會被抓到。
            string dir = Path.Combine(RepoRoot, "art", "generated", "UI", "LOBBYSEL");
            Assert.IsTrue(Directory.Exists(dir), "art/generated/UI/LOBBYSEL 不存在(跑 tools/make_lobbysel_room_buttons.py)");
            foreach (var n in Baked)
            {
                string upper = n.ToUpperInvariant();
                Assert.IsTrue(File.Exists(Path.Combine(dir, upper + ".PNG")), upper + ".PNG 缺檔");
                // .AN 是官方那種「單行一個 png 檔名」的格式,載入端才不用寫任何新程式。
                string an = Path.Combine(dir, upper + ".AN");
                Assert.IsTrue(File.Exists(an), upper + ".AN 缺檔");
                Assert.AreEqual(n + ".png", File.ReadAllText(an).Trim(),
                    upper + ".AN 的內容要正好是它自己的 png 檔名");
            }
        }

        [Test]
        public void Baked_Buttons_Load_At_The_Official_Plate_Size()
        {
            var plate = LobbySelArt.An(OfficialPlate);
            if (plate == null) Assert.Ignore("LOBBYSEL 美術不在這個環境裡(沒有 DATA root)。");

            foreach (var n in Baked)
            {
                var s = LobbySelArt.An(n);
                Assert.IsNotNull(s, n + " 載不到 —— DATA/UI/LOBBYSEL 少了這張(重跑 build_clean_data.ps1)");
                // 尺寸不對就會被按鈕的版位拉爆。LOBBYSEL47..49 就是踩過這個坑的反例(256×40 的橫幅)。
                Assert.AreEqual(plate.rect.width, s.rect.width, 0.5f, n + " 寬度要與官方底板一致");
                Assert.AreEqual(plate.rect.height, s.rect.height, 0.5f, n + " 高度要與官方底板一致");
            }
        }

        [Test]
        public void The_Three_States_Are_Actually_Different_Images()
        {
            if (LobbySelArt.An(OfficialPlate) == null) Assert.Ignore("LOBBYSEL 美術不在這個環境裡。");

            // 三態的差別是底板亮度(normal / hover 最亮 / pushed 最暗)。烘圖工具如果把同一張底板寫三次,
            // 按鈕就會完全沒有按壓回饋 —— 那是「看起來壞掉但不會報錯」的那種 bug。
            for (int b = 0; b < 2; b++)
            {
                var lum = new float[3];
                for (int i = 0; i < 3; i++)
                {
                    var s = LobbySelArt.An(Baked[b * 3 + i]);
                    Assert.IsNotNull(s);
                    lum[i] = MeanLuminance(s);
                }
                Assert.AreNotEqual(lum[0], lum[1], "normal 與 hover 不該是同一張圖(" + Baked[b * 3] + ")");
                Assert.AreNotEqual(lum[0], lum[2], "normal 與 pushed 不該是同一張圖(" + Baked[b * 3] + ")");
                Assert.Greater(lum[1], lum[2], "hover 要比 pushed 亮(" + Baked[b * 3] + ")");
            }
        }

        private static float MeanLuminance(Sprite s)
        {
            var px = s.texture.GetPixels32();
            double sum = 0;
            for (int i = 0; i < px.Length; i++) sum += (px[i].r + px[i].g + px[i].b) * (px[i].a / 255.0);
            return (float)(sum / px.Length);
        }
    }
}
