using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Game;
using Sdo.Shop;

namespace Sdo.Tests
{
    /// <summary>Guards <see cref="AvatarItemCatalog.ShouldSynthGarment"/> — the phantom-duplicate filter for the 商城
    /// mesh-only synth rows. A modelId named in one body-outfit slot (上衣/下裝/連身) ships companion COAT/PANT/ONE
    /// meshes for the SAME design; synthesising a mesh-only row for the OTHER slot listed a visual duplicate
    /// (使用者回報: 合成上衣「001278」==「性感嘻哈」001277、「002247」==「快乐舞会」2247 顯示同一件衣服). Pure logic — the
    /// set of named body-outfit (sex, modelId) is injected, so no Unity/disk is needed.</summary>
    public class AvatarItemCatalogSynthTests
    {
        // 快乐舞会 = 連身裙 (cat 150, model 2247); 野战迷彩中裤 = 下裝 (cat 103, model 1278). Both female-named body outfits.
        private static ISet<(ItemSex, int)> Named =>
            new HashSet<(ItemSex, int)> { (ItemSex.Female, 2247), (ItemSex.Female, 1278), (ItemSex.Female, 1277) };

        [Test]
        public void NamedOnePiece_SuppressesSynthTop_SameModelId()
        {
            // 快乐舞会 is a named 連身裙 (2247). Its 002247_WOMAN_COAT.MSH must NOT become a phantom 上衣 "002247".
            Assert.IsFalse(AvatarItemCatalog.ShouldSynthGarment(ItemSex.Female, EquipSlot.Top, 2247, Named));
        }

        [Test]
        public void NamedBottom_SuppressesSynthTop_CompanionMesh()
        {
            // 野战迷彩中裤 is a named 下裝 (1278). Its companion 001278_WOMAN_COAT.MSH (which re-uses 001277 的 coat 貼圖)
            // must NOT become a phantom 上衣 "001278" duplicating 性感嘻哈.
            Assert.IsFalse(AvatarItemCatalog.ShouldSynthGarment(ItemSex.Female, EquipSlot.Top, 1278, Named));
        }

        [Test]
        public void NamedTop_SuppressesSynthBottom_ReverseCompanion()
        {
            // 性感嘻哈 is a named 上衣 (1277). Its companion 001277_WOMAN_PANT.MSH (re-uses 001278 的 pant 貼圖) must NOT
            // become a phantom 下裝 "001277".
            Assert.IsFalse(AvatarItemCatalog.ShouldSynthGarment(ItemSex.Female, EquipSlot.Bottom, 1277, Named));
        }

        [Test]
        public void UnnamedMeshModel_IsSynthesised()
        {
            // A modelId that no named row claims stays a browsable mesh-only row (無名的也上架/序號當名字).
            Assert.IsTrue(AvatarItemCatalog.ShouldSynthGarment(ItemSex.Female, EquipSlot.Top, 9999, Named));
        }

        [Test]
        public void OtherGender_IsNotSuppressed()
        {
            // The named set is female; a MALE synth of the same numeric modelId is a different mesh family → allowed.
            Assert.IsTrue(AvatarItemCatalog.ShouldSynthGarment(ItemSex.Male, EquipSlot.Top, 2247, Named));
        }

        [Test]
        public void NonBodyOutfitSlot_IsNeverSuppressed()
        {
            // 髮型/鞋子/翅膀/表情 don't share a modelId across a 上↔下 split, so the plain "any mesh" rule stands even when
            // the id happens to be a named body outfit.
            Assert.IsTrue(AvatarItemCatalog.ShouldSynthGarment(ItemSex.Female, EquipSlot.Shoes, 2247, Named));
            Assert.IsTrue(AvatarItemCatalog.ShouldSynthGarment(ItemSex.Female, EquipSlot.Hair, 2247, Named));
            Assert.IsTrue(AvatarItemCatalog.ShouldSynthGarment(ItemSex.Female, EquipSlot.Wings, 2247, Named));
        }

        [Test]
        public void NullNamedSet_AllowsSynth()
        {
            Assert.IsTrue(AvatarItemCatalog.ShouldSynthGarment(ItemSex.Female, EquipSlot.Top, 2247, null));
        }
    }
}
