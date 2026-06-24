using System.IO;
using System.Linq;
using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    public class EftFileTests
    {
        private static EftFile LoadEft(string name)
        {
            var path = Path.Combine(SdoExtracted.Root, "3DEFT", name);
            Assert.IsTrue(File.Exists(path), "Missing extracted " + name);
            return EftFile.Load(File.ReadAllBytes(path));
        }

        [Test]
        public void TriggerTable_Wires_Parent_To_Child_Slots()
        {
            var eft = LoadEft("STAGELIGHTB.EFT");
            CollectionAssert.AreEqual(new[] { 2 }, eft.RootSlots);
            CollectionAssert.AreEqual(new[] { 2, 1, 2, 1 }, eft.TriggerParentSlots);
            CollectionAssert.AreEqual(new[] { 0, 0, 3, 3 }, eft.ChildSlots);

            var bySlot = eft.Emitters.ToDictionary(e => e.Slot);
            CollectionAssert.AreEqual(new[] { 0, 3 }, bySlot[2].Children.Select(e => e.Slot).ToArray());
            CollectionAssert.AreEqual(new[] { 0, 3 }, bySlot[1].Children.Select(e => e.Slot).ToArray());
        }

        [Test]
        public void TriggerTable_Preserves_Combo_Burst_Trees()
        {
            var combo100 = LoadEft("100COMBO.EFT");
            var c100 = combo100.Emitters.ToDictionary(e => e.Slot);
            CollectionAssert.AreEqual(new[] { 0 }, combo100.RootSlots);
            CollectionAssert.AreEqual(new[] { 1, 2 }, c100[0].Children.Select(e => e.Slot).ToArray());

            var combo400 = LoadEft("400COMBO.EFT");
            var c400 = combo400.Emitters.ToDictionary(e => e.Slot);
            CollectionAssert.AreEqual(new[] { 0 }, combo400.RootSlots);
            CollectionAssert.AreEqual(new[] { 1, 2 }, c400[0].Children.Select(e => e.Slot).ToArray());
            CollectionAssert.AreEqual(new[] { 3 }, c400[1].Children.Select(e => e.Slot).ToArray());
            CollectionAssert.AreEqual(new[] { 4 }, c400[2].Children.Select(e => e.Slot).ToArray());
        }
    }
}
