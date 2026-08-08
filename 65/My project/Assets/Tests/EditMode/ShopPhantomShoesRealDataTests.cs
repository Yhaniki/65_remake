using System.IO;
using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// REAL-DATA guard for 使用者回報「003541 / 003542 這兩個鞋子沒顯示,透明的」—— 商城鞋子分頁那兩張卡是全空的。
    ///
    /// 真相:<c>003541_WOMAN_SHOES.MSH</c> / <c>003542_WOMAN_SHOES.MSH</c> 根本不是鞋子。它們是具名下裝
    /// 「熱舞短褲」(modelId 3541/3542, iteminfo id 43541/53541/63541) 的 companion 幾何 —— 同一條牛仔短褲+腰帶
    /// (186 verts,PANT 版 140 verts),材質旗標 0x2、UV、貼圖全都正常,只是這塊幾何長在**臀部**。鞋子卡片的官方
    /// 取景框對準腳踝(<c>ShopScreen.FrameFor(EquipSlot.Shoes)</c>: pos.y=-60、scale 6.5),所以那塊短褲整個落在
    /// 鏡頭外 → 卡片畫出來是空的。官方 iteminfo 從沒登錄這兩件鞋(官方商城也沒有這兩張卡),是我們的 mesh-only
    /// 合成(<see cref="AvatarItemCatalog.AllMeshModels"/>)把磁碟上任何 <c>*_SHOES.MSH</c> 都當商品上架才冒出來的。
    ///
    /// 判準(<see cref="AvatarItemCatalog.ShoesTextureIsBottomCopy"/>):鞋子自己的 <c>{id}_{G}_SHOES.dds</c> 與同 id
    /// 同性別的 <c>{id}_{G}_{PANT|COAT|ONE}.dds</c> **位元組完全相同** —— 美術只是把下裝貼圖複製一份改了檔名。
    ///
    /// 全語料實測(<c>DATA/AVATAR</c>,4170 個 <c>_SHOES.MSH</c>):只有 7 個鞋子的 id 同時存在同性別的
    /// PANT/COAT/ONE 貼圖,其中只有這 2 個貼圖位元組相同。下面的對照組(尤其 027643 —— 同 id 也有 PANT **mesh**,
    /// 但鞋貼圖是自己畫的真長靴)必須留在架上,證明這條規則不是靠 id 黑名單、也不會誤殺一整批鞋。
    /// </summary>
    public class ShopPhantomShoesRealDataTests
    {
        // 沒有真實 AVATAR 資料時 Ignore(與 GarmentAlphaRealDataTests 同一套解析:data_root.txt → Root)。
        private static bool HaveAvatarData()
        {
            var probe = SdoAvatarBuilder.ResolveAvatarFile("AVATAR/003541_WOMAN_SHOES.MSH");
            return !string.IsNullOrEmpty(probe) && File.Exists(probe);
        }

        [TestCase(3541)]
        [TestCase(3542)]
        public void HotPantsCompanion_IsNotListedAsShoes(int modelId)
        {
            if (!HaveAvatarData()) Assert.Ignore("AVATAR data root not found — 需要遊戲資料 (data_root.txt)");
            Assert.IsTrue(AvatarItemCatalog.IsBottomCompanionShoes(modelId, "WOMAN"),
                $"{modelId:D6}_WOMAN_SHOES 是「熱舞短褲」的下裝 companion 幾何,合成成鞋子會是一張空白卡片");
        }

        // 對照組:鞋子 id 同時有同性別 PANT/COAT/ONE 貼圖(或 mesh),但鞋貼圖是自己畫的 → 必須照常上架。
        [TestCase(27643, "WOMAN")]   // 同 id 有 PANT mesh + PANT 貼圖,鞋是真的長靴 (679 verts)
        [TestCase(24141, "MAN")]     // 同 id 有 PANT 貼圖,大小相同但內容不同
        [TestCase(24411, "MAN")]
        [TestCase(15281, "WOMAN")]   // 同 id 有 COAT 貼圖
        [TestCase(26400, "WOMAN")]   // 同 id 有 ONE 貼圖
        public void RealShoes_StayListed(int modelId, string gender)
        {
            if (!HaveAvatarData()) Assert.Ignore("AVATAR data root not found — 需要遊戲資料 (data_root.txt)");
            Assert.IsFalse(AvatarItemCatalog.IsBottomCompanionShoes(modelId, gender),
                $"{modelId:D6}_{gender}_SHOES 是自己畫貼圖的真鞋,不可以被 companion 規則掃掉");
        }

        [Test]
        public void PlainShoesWithNoBottomTexture_IsNeverACompanion()
        {
            if (!HaveAvatarData()) Assert.Ignore("AVATAR data root not found — 需要遊戲資料 (data_root.txt)");
            // 絕大多數鞋子的 id 上根本沒有同性別的下裝貼圖 → 連讀檔比對都不會發生(TexFamilies 預篩擋掉)。
            Assert.IsFalse(AvatarItemCatalog.IsBottomCompanionShoes(999999, "WOMAN"));
        }
    }
}
