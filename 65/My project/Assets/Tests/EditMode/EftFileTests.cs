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

        [Test]
        public void Fire3_TriggerTree_Is_Slot5_Root_Fanning_To_Slot0_Then_Four_Children()
        {
            // ROOT: slot5 (persistent INVIS carrier) → slot0 (repeating carrier) → slot1/2/3/4
            // slot1 = billboard AEF_1_14 blue flame streaks (tex=84, orient)
            // slot2 = world-quad AEF_1_14 blue flame streaks (tex=84, no orient)
            // slot4 = billboard AEF_4_02 cyan orb (tex=30, orient); NOT isBallCore for scene EFTs (Persistent)
            var eft = LoadEft("FIRE3.EFT");
            CollectionAssert.AreEqual(new[] { 5 }, eft.RootSlots);

            var bySlot = eft.Emitters.ToDictionary(e => e.Slot);
            CollectionAssert.AreEqual(new[] { 0 }, bySlot[5].Children.Select(e => e.Slot).ToArray());
            CollectionAssert.AreEquivalent(new[] { 1, 2, 3, 4 }, bySlot[0].Children.Select(e => e.Slot).ToArray());

            // slot4 is the cyan orb billboard (isBallCore source: orient + tex30)
            Assert.IsTrue(bySlot[4].Orient, "slot4 is a billboard");
            Assert.AreEqual(30, bySlot[4].TexIdx, "slot4 = AEF_4_02 cyan orb");

            // slot1/2 are the visible flame streaks (tex84=AEF_1_14)
            Assert.AreEqual(84, bySlot[1].TexIdx, "slot1 = AEF_1_14 blue flame streak billboard");
            Assert.AreEqual(84, bySlot[2].TexIdx, "slot2 = AEF_1_14 blue flame streak world-quad");
        }

        [Test]
        public void Booklight_TriggerTree_Is_Slot3_Root_Then_Slot0_Then_Orange_Orb()
        {
            // ROOT: slot3 (persistent INVIS) → slot0 (repeating INVIS) → slot1(INVIS) + slot2(orange orb billboard)
            // slot2 = billboard AEF_4_03 orange orb (tex=31) — the visible window-glow
            var eft = LoadEft("BOOKLIGHT.EFT");
            CollectionAssert.AreEqual(new[] { 3 }, eft.RootSlots);

            var bySlot = eft.Emitters.ToDictionary(e => e.Slot);
            CollectionAssert.AreEqual(new[] { 0 }, bySlot[3].Children.Select(e => e.Slot).ToArray());
            CollectionAssert.AreEquivalent(new[] { 1, 2 }, bySlot[0].Children.Select(e => e.Slot).ToArray());

            Assert.IsTrue(bySlot[2].Orient, "slot2 is a billboard");
            Assert.AreEqual(31, bySlot[2].TexIdx, "slot2 = AEF_4_03 orange orb");
        }
    }
}
