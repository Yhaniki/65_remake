using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Game;
using Sdo.Shop;

namespace Sdo.Tests
{
    /// <summary>Layering rules for <see cref="AvatarOutfit.ResolveParts"/> — the seam the 商城 換裝/試穿 drives.
    /// Pure logic: mesh existence is injected so no Unity/disk is needed.</summary>
    public class AvatarOutfitTests
    {
        private static ShopItem Item(int model, int cat)
            => new ShopItem { Id = model, ModelId = model, Category = cat, Quantity = -1, Name = "x" };

        // Pretend every mesh exists so the layering (not disk state) is what's under test.
        private static readonly System.Func<string, bool> AllExist = _ => true;

        [Test]
        public void Wing_IsAdditive_KeepsDefaultTopAndBottom()
        {
            // cat 108 = female wings → mesh _CHIBANG, layered ON TOP of the starter costume (does not replace top/bottom).
            var parts = AvatarOutfit.ResolveParts(ItemSex.Female, new[] { Item(23108, ItemCategory.WingsFemale) }, AllExist);
            CollectionAssert.Contains(parts, "AVATAR/023108_WOMAN_CHIBANG.MSH");
            CollectionAssert.Contains(parts, AvatarOutfit.WomanDefaults[EquipSlot.Top]);      // 上衣仍在 (翅膀不取代)
            CollectionAssert.Contains(parts, AvatarOutfit.WomanDefaults[EquipSlot.Bottom]);   // 下著仍在
        }

        [Test]
        public void MaleWing_UsesManChibangMesh()
        {
            var parts = AvatarOutfit.ResolveParts(ItemSex.Male, new[] { Item(23104, ItemCategory.WingsMale) }, AllExist);
            CollectionAssert.Contains(parts, "AVATAR/023104_MAN_CHIBANG.MSH");
        }

        [Test]
        public void Wing_SkippedWhenMeshMissing()
        {
            // 無模型(未 extract) → 不加入 parts (預設穿搭不變),與 UI 隱藏無模型 item 一致。
            var parts = AvatarOutfit.ResolveParts(ItemSex.Female, new[] { Item(23108, ItemCategory.WingsFemale) }, _ => false);
            CollectionAssert.DoesNotContain(parts, "AVATAR/023108_WOMAN_CHIBANG.MSH");
        }

        // ---- ComposeParts (商城試穿疊加) ----

        private static string[] FemaleBase() => new List<string>(AvatarOutfit.WomanDefaults.Values).ToArray();

        [Test]
        public void Compose_OnePiece_ReplacesTopAndDropsBottom()
        {
            var parts = AvatarOutfit.ComposeParts(ItemSex.Female, FemaleBase(), new[] { "AVATAR/002247_WOMAN_ONE.MSH" });
            CollectionAssert.Contains(parts, "AVATAR/002247_WOMAN_ONE.MSH");
            CollectionAssert.DoesNotContain(parts, AvatarOutfit.WomanDefaults[EquipSlot.Top]);
            CollectionAssert.DoesNotContain(parts, AvatarOutfit.WomanDefaults[EquipSlot.Bottom]);   // 連身=沒有獨立下著
        }

        [Test]
        public void Compose_TopAfterOnePiece_RestoresDefaultBottom()
        {
            // 使用者回報:女生穿連身裙後選上衣,下半身變透明——腿的皮膚幾何長在 PANT mesh 裡,
            // 連身拔掉 Bottom 後換單件上衣必須補回預設褲,否則 parts 沒有任何 PANT → 腿整段消失。
            var wearingDress = AvatarOutfit.ComposeParts(ItemSex.Female, FemaleBase(), new[] { "AVATAR/002247_WOMAN_ONE.MSH" });
            var parts = AvatarOutfit.ComposeParts(ItemSex.Female, wearingDress, new[] { "AVATAR/000123_WOMAN_COAT.MSH" });
            CollectionAssert.Contains(parts, "AVATAR/000123_WOMAN_COAT.MSH");
            CollectionAssert.DoesNotContain(parts, "AVATAR/002247_WOMAN_ONE.MSH");                  // 連身脫掉
            CollectionAssert.Contains(parts, AvatarOutfit.WomanDefaults[EquipSlot.Bottom]);         // 預設褲補回
        }

        [Test]
        public void Compose_BottomAfterOnePiece_RestoresDefaultTop()
        {
            var wearingDress = AvatarOutfit.ComposeParts(ItemSex.Female, FemaleBase(), new[] { "AVATAR/002247_WOMAN_ONE.MSH" });
            var parts = AvatarOutfit.ComposeParts(ItemSex.Female, wearingDress, new[] { "AVATAR/000456_WOMAN_PANT.MSH" });
            CollectionAssert.Contains(parts, "AVATAR/000456_WOMAN_PANT.MSH");
            CollectionAssert.DoesNotContain(parts, "AVATAR/002247_WOMAN_ONE.MSH");                  // 連身與下裝互斥
            CollectionAssert.Contains(parts, AvatarOutfit.WomanDefaults[EquipSlot.Top]);            // 預設上衣補回
        }

        [Test]
        public void Compose_TopOverNormalOutfit_KeepsWornBottom()
        {
            // 沒有連身時換上衣,原本穿著的褲子要保留 (不能被「補預設」規則誤傷)。
            var wearingSet = AvatarOutfit.ComposeParts(ItemSex.Female, FemaleBase(),
                new[] { "AVATAR/000111_WOMAN_COAT.MSH", "AVATAR/000111_WOMAN_PANT.MSH" });
            var parts = AvatarOutfit.ComposeParts(ItemSex.Female, wearingSet, new[] { "AVATAR/000123_WOMAN_COAT.MSH" });
            CollectionAssert.Contains(parts, "AVATAR/000123_WOMAN_COAT.MSH");
            CollectionAssert.Contains(parts, "AVATAR/000111_WOMAN_PANT.MSH");                       // 褲子沿用現況
            CollectionAssert.DoesNotContain(parts, AvatarOutfit.WomanDefaults[EquipSlot.Bottom]);
        }

        [Test]
        public void Compose_MaleTopAfterOnePiece_RestoresManDefaultBottom()
        {
            var maleBase = new List<string>(AvatarOutfit.ManDefaults.Values).ToArray();
            var wearingDress = AvatarOutfit.ComposeParts(ItemSex.Male, maleBase, new[] { "AVATAR/000777_MAN_ONE.MSH" });
            var parts = AvatarOutfit.ComposeParts(ItemSex.Male, wearingDress, new[] { "AVATAR/000123_MAN_COAT.MSH" });
            CollectionAssert.Contains(parts, AvatarOutfit.ManDefaults[EquipSlot.Bottom]);           // 男版補回 MAN_PANT
        }
    }
}
