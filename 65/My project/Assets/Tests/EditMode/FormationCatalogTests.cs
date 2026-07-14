using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    public class FormationCatalogTests
    {
        // ---- shape / bounds ----------------------------------------------------------------------------------

        [Test]
        public void There_Are_Three_Selectable_Types_And_Six_Max_Dancers()
        {
            Assert.AreEqual(3, FormationCatalog.TypeCount);
            Assert.AreEqual(6, FormationCatalog.MaxDancers);
        }

        [Test]
        public void Slot_Count_Matches_Requested_Dancer_Count_For_Every_Type_And_Count()
        {
            for (int t = 0; t < FormationCatalog.TypeCount; t++)
                for (int n = 1; n <= FormationCatalog.MaxDancers; n++)
                    Assert.AreEqual(n, FormationCatalog.GetSlots(t, n).Length, $"type {t}, count {n}");
        }

        [Test]
        public void Every_Slot_Sits_On_The_Floor_Plane_Y_Zero()
        {
            for (int t = 0; t < FormationCatalog.TypeCount; t++)
                for (int n = 1; n <= FormationCatalog.MaxDancers; n++)
                    foreach (var p in FormationCatalog.GetSlots(t, n))
                        Assert.AreEqual(0f, p.y, 1e-4f);
        }

        // ---- solo = origin (matches EXE entry count=1 = (0,0,0) and SoloDanceSpot) ----------------------------

        [Test]
        public void Solo_Type0_Is_The_Origin()
        {
            var s = FormationCatalog.GetSlots(0, 1);
            Assert.AreEqual(1, s.Length);
            Assert.AreEqual(Vector3.zero, s[0]);
        }

        // ---- verbatim spot-checks of the decompiled table ----------------------------------------------------

        [Test]
        public void Type0_Count3_Is_Verbatim_Wedge_With_Leader_At_Origin()
        {
            var s = FormationCatalog.GetSlots(0, 3);
            Assert.AreEqual(new Vector3(0, 0, 0), s[0]);    // leader / centre-front
            Assert.AreEqual(new Vector3(-50, 0, 50), s[1]);
            Assert.AreEqual(new Vector3(50, 0, 50), s[2]);
        }

        [Test]
        public void Type0_Count6_Is_Verbatim()
        {
            var s = FormationCatalog.GetSlots(0, 6);
            CollectionAssert.AreEqual(new[]
            {
                new Vector3(-25, 0, 0), new Vector3(-100, 0, 50), new Vector3(100, 0, 50),
                new Vector3(-50, 0, 50), new Vector3(50, 0, 50), new Vector3(0, 0, 50),
            }, s);
        }

        [Test]
        public void Type1_Leader_Sits_Forward_At_ZMinus25()
        {
            var s = FormationCatalog.GetSlots(1, 6);
            Assert.AreEqual(new Vector3(-15, 0, -25), s[0]);
            Assert.AreEqual(new Vector3(85, 0, 50), s[5]);
        }

        [Test]
        public void Type2_Count6_Is_Verbatim()
        {
            var s = FormationCatalog.GetSlots(2, 6);
            CollectionAssert.AreEqual(new[]
            {
                new Vector3(-5, 0, 15), new Vector3(-75, 0, 50), new Vector3(25, 0, 50),
                new Vector3(95, 0, 50), new Vector3(-35, 0, -25), new Vector3(45, 0, -25),
            }, s);
        }

        // ---- clamping + isolation --------------------------------------------------------------------------

        [Test]
        public void Out_Of_Range_Args_Clamp_Into_Valid_Range()
        {
            Assert.AreEqual(FormationCatalog.GetSlots(0, 1), FormationCatalog.GetSlots(-3, 0));   // type<0, count<1
            Assert.AreEqual(FormationCatalog.GetSlots(2, 6), FormationCatalog.GetSlots(99, 99));  // type>2, count>6
        }

        [Test]
        public void Returned_Array_Is_A_Copy_The_Caller_Can_Mutate()
        {
            var a = FormationCatalog.GetSlots(0, 2);
            a[0] = new Vector3(123, 456, 789);
            var b = FormationCatalog.GetSlots(0, 2);
            Assert.AreEqual(new Vector3(-25, 0, 0), b[0], "mutating a returned array must not affect the table");
        }
    }
}
