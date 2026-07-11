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
    }
}
