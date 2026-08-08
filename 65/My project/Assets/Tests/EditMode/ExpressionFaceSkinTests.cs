using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 表情 (<c>*_FACE_HUAN.MSH</c>) 臉底膚色正規化 —— 純邏輯 + 真實資料 + 「每個畫面都要套」的迴歸守門。
    ///
    /// 使用者回報:「女生表情有問題,在商城看都是好的,放到儲物櫃後顯示錯誤 —— devil neko f 臉黑的、深邃的眸
    /// 臉黑的、032585 沒表情」。真因不是資料,是**同一份修正只寫在商城**:<c>ShopScreen</c> 有
    /// ForceLightExpressionFace,<c>WardrobeScreen</c> 沒有 → 儲物櫃直接畫 mesh 自己綁的臉底貼圖。
    ///
    /// 三件的 mesh 材質名(真實資料,下面的測試會再驗一次):
    ///   • 002012 Devil Neko F     → <c>002012_woman_face_huan4.dds</c> = 最深膚色 → 臉黑的
    ///   • 008354 深邃的眸(女)      → <c>008354_woman_face_huan4.dds</c> = 最深膚色 → 臉黑的
    ///   • 032585 (合成表情,無名)  → <c>032585_woman_face_new_huan0.dds</c> —— 磁碟上只有 <c>032585_WOMAN_FACE_HUAN0.DDS</c>
    ///     (沒有 <c>_new_</c>) → resolve 不到 → 退平塗色 = 一張沒有五官的素臉 = 「沒表情」
    /// 三件的 <c>*_FACE_HUAN0.DDS</c> 都在磁碟上,所以壓成 huan0 一定救得回來。
    /// </summary>
    public class ExpressionFaceSkinTests
    {
        // ---------------- 純邏輯:哪些材質該被壓成最白膚色 ----------------

        [TestCase("L@002012_woman_face_huan4.dds")]   // Devil Neko F:最深膚色底
        [TestCase("L@008354_woman_face_huan4.dds")]   // 深邃的眸(女):最深膚色底
        [TestCase("L@032585_woman_face_new_huan0.dds")]  // 032585:數字尾在 huan 後面,只是中間多了 _new_
        [TestCase("_face_huan_1")]                    // 多一條分隔底線的變體
        [TestCase("_face_haun0.dds")]                 // haun↔huan 轉位錯字
        public void SkinBase_IsRecognised(string matName)
            => Assert.IsTrue(ExpressionFaceSkin.IsFaceSkinVariant(matName), matName + " 是膚色臉底,必須壓成 huan0");

        [TestCase("L@017675_woman_face_huan.dds")]    // 化妝舞會眼罩:裝飾疊層 (base huan,無數字尾)
        [TestCase("L@015353_woman_face_huan.dds")]    // 貓咪口罩:裝飾疊層
        [TestCase("W_Basic_face.dds")]                // 頸/耳膚色,本來就是膚色
        [TestCase("")]
        [TestCase(null)]
        public void Decoration_IsLeftAlone(string matName)
            => Assert.IsFalse(ExpressionFaceSkin.IsFaceSkinVariant(matName),
                              matName + " 不是膚色臉底,壓白會把面具/口罩蓋成帶鬼影五官的素臉");

        // ---------------- 真實資料:使用者回報的那三件 ----------------

        private static string AvatarDir()
        {
            var probe = SdoAvatarBuilder.ResolveAvatarFile("AVATAR/002012_WOMAN_FACE_HUAN.MSH");
            if (string.IsNullOrEmpty(probe) || !File.Exists(probe)) return null;
            return Path.GetDirectoryName(probe);
        }

        [TestCase(2012, "Devil Neko F")]
        [TestCase(8354, "深邃的眸(女)")]
        [TestCase(32585, "032585 (合成表情)")]
        public void ReportedExpression_NeedsTheHuan0Override(int modelId, string label)
        {
            var dir = AvatarDir();
            if (dir == null) Assert.Ignore("AVATAR data root not found — 需要遊戲資料 (data_root.txt)");
            string id = modelId.ToString("D6");
            var names = MshLoader.ReadMaterialNames(File.ReadAllBytes(Path.Combine(dir, id + "_WOMAN_FACE_HUAN.MSH")));
            CollectionAssert.IsNotEmpty(names, label + ": mesh 沒有任何材質名");
            // mesh 自己綁的臉底 = 會被畫出來的那張。三件都是「該壓白」的膚色底 → 沒套修正的畫面就會壞。
            foreach (var n in names)
                Assert.IsTrue(ExpressionFaceSkin.IsFaceSkinVariant(n),
                    $"{label}: 材質 '{n}' 不再是膚色臉底變體 —— 資料換了還是判準改了?");
            // 救援貼圖必須在磁碟上,否則 ForceLightSkin 會原樣放過。
            Assert.IsNotNull(SdoAvatarBuilder.FindDdsPath(dir, id + "_WOMAN_FACE_HUAN0.DDS"),
                $"{label}: 找不到最白膚色 {id}_WOMAN_FACE_HUAN0.DDS");
        }

        /// <summary>032585 的材質名 (<c>…_face_new_huan0.dds</c>) 在磁碟上**沒有**對應檔 —— 這正是「沒表情」的機制:
        /// 貼圖 resolve 不到 → 退平塗。這條顧的是「別哪天讓 fuzzy 比對意外接上別的檔」而讓上面的推論悄悄失效。</summary>
        [Test]
        public void Expression032585_MaterialTexture_DoesNotExistOnDisk()
        {
            var dir = AvatarDir();
            if (dir == null) Assert.Ignore("AVATAR data root not found — 需要遊戲資料 (data_root.txt)");
            Assert.IsFalse(File.Exists(Path.Combine(dir, "032585_WOMAN_FACE_NEW_HUAN0.DDS")),
                "032585 的材質名現在有對應檔了 → 這件的『沒表情』成因要重新確認");
        }

        // ---------------- 迴歸守門:每個會畫出表情的畫面都要套同一份修正 ----------------

        /// <summary>這條測試存在的理由就是這次的 bug:修正只寫在商城,儲物櫃漏了,而漏掉不會編譯失敗、不會拋例外 ——
        /// 只有玩家打開儲物櫃才看得到一張黑臉。之後再多一個會畫表情的地方就往清單裡加一行。
        /// 兩條「所有角色的必經之路」都要套:<c>SdoAvatarBuilder.LoadParts</c>(遊戲內舞者/遊戲中頭貼/商城卡與預覽)
        /// 與 <c>SdoRoomAvatar.Build</c> 的 RenderMode overload(房間本尊/房間頭貼/選性別 —— 那條自帶迴圈,不經 builder)。
        /// 商城/儲物櫃另外在卡片的 cutout 之後再套一次(順序:LoadParts → ApplyCardCutoutShader → 再壓一次臉底)。</summary>
        [TestCase("UI/Screens/ShopScreen.cs")]
        [TestCase("UI/Screens/WardrobeScreen.cs")]
        [TestCase("Game/SdoRoomAvatar.cs")]
        [TestCase("Game/SdoAvatarBuilder.cs")]
        public void ScreenThatRendersExpressions_CallsForceLightSkin(string relPath)
        {
            string path = Path.Combine(Application.dataPath, "Scripts", relPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) Assert.Ignore(relPath + " 不在這份簽出裡");
            string src = File.ReadAllText(path);
            Assert.IsTrue(Regex.IsMatch(src, @"ExpressionFaceSkin\.(ForceLightSkin|ApplyToParts)"),
                relPath + " 會畫出表情 mesh,卻沒有套 ExpressionFaceSkin → 臉會是 mesh 自己綁的深膚/破圖");
        }

        /// <summary>房間/遊戲那條只拿得到 mesh 路徑 → 貼圖候選是從檔名 stem 推的。尾綴 (底線/數字/大小寫) 都要切乾淨。</summary>
        [TestCase("032585_WOMAN_FACE_HUAN", "032585_WOMAN_FACE_HUAN")]
        [TestCase("002012_WOMAN_FACE_HUAN_", "002012_WOMAN_FACE_HUAN")]
        [TestCase("008354_woman_face_huan2", "008354_woman_face_huan")]
        [TestCase("012882_MAN_FACE", "012882_MAN_FACE")]          // 不是表情 → 原樣 (ApplyToParts 也不會挑到它)
        public void MeshStem_TrimsToTheHuanToken(string stem, string expect)
            => Assert.AreEqual(expect, ExpressionFaceSkin.TrimAfterToken(stem));

        /// <summary>房間那條的入口:一組部位路徑裡沒有表情就什麼都不做 (不能因為沒穿表情就丟例外/亂改別的材質)。</summary>
        [Test]
        public void ApplyToParts_WithNoExpression_IsANoOp()
        {
            var go = new UnityEngine.GameObject("t");
            try
            {
                Assert.DoesNotThrow(() => ExpressionFaceSkin.ApplyToParts(go, new[] { "AVATAR/900002_WOMAN_HAIR.MSH", null, "" }));
                Assert.DoesNotThrow(() => ExpressionFaceSkin.ApplyToParts(null, new[] { "AVATAR/032585_WOMAN_FACE_HUAN.MSH" }));
                Assert.DoesNotThrow(() => ExpressionFaceSkin.ApplyToParts(go, null));
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }
    }
}
