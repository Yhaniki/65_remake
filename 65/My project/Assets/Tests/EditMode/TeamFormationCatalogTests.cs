using NUnit.Framework;
using UnityEngine;
using Sdo.Game;
using L = Sdo.Game.TeamFormationCatalog.Layout;

namespace Sdo.Tests
{
    public class TeamFormationCatalogTests
    {
        [Test]
        public void Team_And_Total_Counts_Are_Correct()
        {
            Assert.AreEqual(2, TeamFormationCatalog.TeamCount(L.V2v2));
            Assert.AreEqual(2, TeamFormationCatalog.TeamCount(L.V3v3));
            Assert.AreEqual(3, TeamFormationCatalog.TeamCount(L.V2v2v2));

            Assert.AreEqual(4, TeamFormationCatalog.TotalDancers(L.V2v2));
            Assert.AreEqual(6, TeamFormationCatalog.TotalDancers(L.V3v3));
            Assert.AreEqual(6, TeamFormationCatalog.TotalDancers(L.V2v2v2));
        }

        [Test]
        public void Names_Match_The_Modes()
        {
            Assert.AreEqual("2v2", TeamFormationCatalog.Name(L.V2v2));
            Assert.AreEqual("3v3", TeamFormationCatalog.Name(L.V3v3));
            Assert.AreEqual("2v2v2", TeamFormationCatalog.Name(L.V2v2v2));
        }

        [Test]
        public void Every_Slot_Is_On_The_Floor_Plane()
        {
            foreach (var l in TeamFormationCatalog.All)
                foreach (var team in TeamFormationCatalog.GetTeams(l))
                    foreach (var p in team)
                        Assert.AreEqual(0f, p.y, 1e-4f);
        }

        [Test]
        public void Each_Teams_First_Member_Is_The_Front_Row_At_Z0()
        {
            foreach (var l in TeamFormationCatalog.All)
                foreach (var team in TeamFormationCatalog.GetTeams(l))
                {
                    Assert.AreEqual(0f, team[0].z, 1e-4f, "leader is the front (z=0) member");
                    for (int i = 1; i < team.Length; i++)
                        Assert.AreEqual(50f, team[i].z, 1e-4f, "back members at z=50");
                }
        }

        // ---- verbatim EXE values --------------------------------------------------------------------------

        [Test]
        public void V2v2_Is_Verbatim()
        {
            var t = TeamFormationCatalog.GetTeams(L.V2v2);
            CollectionAssert.AreEqual(new[] { new Vector3(-25, 0, 0), new Vector3(-75, 0, 50) }, t[0]);
            CollectionAssert.AreEqual(new[] { new Vector3(25, 0, 0), new Vector3(75, 0, 50) }, t[1]);
        }

        [Test]
        public void V3v3_Is_Verbatim()
        {
            var t = TeamFormationCatalog.GetTeams(L.V3v3);
            CollectionAssert.AreEqual(new[] { new Vector3(-60, 0, 0), new Vector3(-90, 0, 50), new Vector3(-30, 0, 50) }, t[0]);
            CollectionAssert.AreEqual(new[] { new Vector3(60, 0, 0), new Vector3(30, 0, 50), new Vector3(90, 0, 50) }, t[1]);
        }

        [Test]
        public void V2v2v2_Is_Verbatim()
        {
            var t = TeamFormationCatalog.GetTeams(L.V2v2v2);
            CollectionAssert.AreEqual(new[] { new Vector3(-45, 0, 0), new Vector3(-75, 0, 50) }, t[0]);
            CollectionAssert.AreEqual(new[] { new Vector3(15, 0, 0), new Vector3(-15, 0, 50) }, t[1]);
            CollectionAssert.AreEqual(new[] { new Vector3(75, 0, 0), new Vector3(45, 0, 50) }, t[2]);
        }

        [Test]
        public void GetTeams_Returns_A_Deep_Copy()
        {
            var a = TeamFormationCatalog.GetTeams(L.V2v2v2);
            a[0][0] = new Vector3(999, 999, 999);
            var b = TeamFormationCatalog.GetTeams(L.V2v2v2);
            Assert.AreEqual(new Vector3(-45, 0, 0), b[0][0]);
        }
    }
}
