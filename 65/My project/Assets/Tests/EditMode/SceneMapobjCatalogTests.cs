using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    public class SceneMapobjCatalogTests
    {
        [Test]
        public void Scn0009_Loads_Guatan_X4_With_Validated_Transforms()
        {
            var groups = SceneMapobjCatalog.ForFolder("SCN0009");
            Assert.AreEqual(1, groups.Count);
            var g = groups[0];
            Assert.AreEqual("GUATAN", g.Folder);
            Assert.AreEqual("GUATAN.MSH", g.Msh);
            Assert.AreEqual("GUATAN.HRC", g.Hrc);
            Assert.AreEqual("GUATAN.MOT", g.Mot);
            Assert.AreEqual(4, g.Instances.Length);
            // matches the previously-validated hardcoded palace placements (pos + uniform scale)
            Assert.AreEqual(-45.79f, g.Instances[0].X, 1e-3f);
            Assert.AreEqual(1f, g.Instances[0].Scale, 1e-3f);
            Assert.AreEqual(-95.79f, g.Instances[2].X, 1e-3f);
            Assert.AreEqual(0.65f, g.Instances[2].Scale, 1e-3f);
        }

        [Test]
        public void Folder_Lookup_Is_Case_Insensitive()
        {
            Assert.AreEqual(1, SceneMapobjCatalog.ForFolder("scn0009").Count);
        }

        [Test]
        public void Beach_Group_Resolves_Folder_Vs_File_Name()
        {
            // SCN0004's "beach" mapobj lives in folder BEACH but its mesh file is LANG.MSH — the file name must
            // come from the table, not be derived from the folder.
            var groups = SceneMapobjCatalog.ForFolder("SCN0004");
            Assert.Greater(groups.Count, 1);
            MapobjGroup beach = null;
            foreach (var g in groups) if (g.Folder == "BEACH") beach = g;
            Assert.IsNotNull(beach, "SCN0004 should mount the BEACH prop");
            Assert.AreEqual("LANG.MSH", beach.Msh);
            Assert.AreEqual("LANG.HRC", beach.Hrc);
        }

        [Test]
        public void Static_Prop_Has_Null_Mot()
        {
            // SCN0010's house has no .mot in the table -> static (bind-pose) prop.
            var groups = SceneMapobjCatalog.ForFolder("SCN0010");
            MapobjGroup house = null;
            foreach (var g in groups) if (g.Folder == "HOUSE") house = g;
            Assert.IsNotNull(house);
            Assert.IsNull(house.Mot);
        }

        [Test]
        public void Nested_Scene_Resolves_Subfolder_Paths()
        {
            // SCN0014 (haidi/underwater) nests every prop under 14_HAIDI/<sub>; the loader joins Folder + Msh,
            // so Folder must carry the full relative sub-path.
            var groups = SceneMapobjCatalog.ForFolder("SCN0014");
            Assert.Greater(groups.Count, 5);
            MapobjGroup guang = null;
            foreach (var g in groups) if (g.Folder == "14_HAIDI/GUANG") guang = g;
            Assert.IsNotNull(guang, "SCN0014 should mount 14_HAIDI/GUANG");
            Assert.AreEqual("GUANG.MSH", guang.Msh);
        }

        [Test]
        public void Nested_Scene_Resolves_Renamed_Mesh_File()
        {
            // SCN0022 (fenmu): the "fenmu_dong1" archive contains GUANG4.MSH under FENMU/DONG1 — the file name
            // differs from the prop name, so it must be resolved from the real tree, not derived.
            var groups = SceneMapobjCatalog.ForFolder("SCN0022");
            MapobjGroup dong1 = null;
            foreach (var g in groups) if (g.Folder == "FENMU/DONG1") dong1 = g;
            Assert.IsNotNull(dong1, "SCN0022 should mount FENMU/DONG1");
            Assert.AreEqual("GUANG4.MSH", dong1.Msh);
            Assert.AreEqual("GUANG4.HRC", dong1.Hrc);
        }

        [Test]
        public void Scene_Without_Mapobj_Returns_Empty()
        {
            Assert.AreEqual(0, SceneMapobjCatalog.ForFolder("SCN0027").Count);   // base scene only, no props
            Assert.AreEqual(0, SceneMapobjCatalog.ForFolder("DOES_NOT_EXIST").Count);
            Assert.AreEqual(0, SceneMapobjCatalog.ForFolder(null).Count);
        }
    }
}
