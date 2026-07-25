using NUnit.Framework;
using Sdo.Osu;

namespace Sdo.Tests
{
    public class ExternalDifficultyPickerTests
    {
        // Assign returns [easyIdx, normalIdx, hardIdx]; hard = highest note count, filled downward, -1 = empty.

        [Test]
        public void None_All_Empty()
        {
            var s = ExternalDifficultyPicker.Assign(new int[0]);
            Assert.AreEqual(new[] { -1, -1, -1 }, s);
        }

        [Test]
        public void One_Chart_Goes_To_Hard_Only()
        {
            var s = ExternalDifficultyPicker.Assign(new[] { 800 });
            Assert.AreEqual(new[] { -1, -1, 0 }, s);   // easy/normal empty, hard = the only chart
        }

        [Test]
        public void Two_Charts_Fill_Hard_Then_Normal()
        {
            var s = ExternalDifficultyPicker.Assign(new[] { 500, 900 });
            Assert.AreEqual(new[] { -1, 0, 1 }, s);    // hard = idx1 (900), normal = idx0 (500), easy empty
        }

        [Test]
        public void Three_Charts_Ascending_To_Easy_Normal_Hard()
        {
            var s = ExternalDifficultyPicker.Assign(new[] { 50, 300, 100 });
            // desc: idx1(300), idx2(100), idx0(50) → hard=1, normal=2, easy=0
            Assert.AreEqual(new[] { 0, 2, 1 }, s);
        }

        [Test]
        public void Four_Charts_Keep_Top_Three_Only()
        {
            var s = ExternalDifficultyPicker.Assign(new[] { 10, 20, 30, 40 });
            // top-3 desc: idx3(40), idx2(30), idx1(20); idx0(10) dropped
            Assert.AreEqual(new[] { 1, 2, 3 }, s);
        }

        [Test]
        public void Ties_Break_By_Index()
        {
            var s = ExternalDifficultyPicker.Assign(new[] { 100, 100 });
            Assert.AreEqual(new[] { -1, 1, 0 }, s);    // equal → idx0 first (hard), idx1 (normal)
        }

        // ---- two-key overload: order by level (star), tie-break by note count ----

        // The lapis case: the chart with the MOST notes is NOT the hardest. Slotting by level puts the LV35 chart in
        // hard, the LV29 chart (more notes) in normal — so the hard slot's number is never below the normal slot's.
        [Test]
        public void ByLevel_MoreNotes_But_Lower_Level_Is_Not_Hard()
        {
            var levels = new[] { 29, 35, 23 };          // idx0 Challenge, idx1 Beginner, idx2 Hard
            var notes = new[] { 1395, 1345, 1074 };     // idx0 has the MOST notes but only LV29
            var s = ExternalDifficultyPicker.Assign(levels, notes);
            // desc by level: idx1(35), idx0(29), idx2(23) → hard=1, normal=0, easy=2
            Assert.AreEqual(new[] { 2, 0, 1 }, s);
        }

        [Test]
        public void ByLevel_Equal_Level_Breaks_By_Note_Count()
        {
            var levels = new[] { 30, 30 };
            var notes = new[] { 500, 900 };             // same level → denser chart wins the hard slot
            var s = ExternalDifficultyPicker.Assign(levels, notes);
            Assert.AreEqual(new[] { -1, 0, 1 }, s);     // hard = idx1 (900 notes), normal = idx0
        }

        [Test]
        public void ByLevel_Equal_Level_And_Notes_Breaks_By_Index()
        {
            var s = ExternalDifficultyPicker.Assign(new[] { 30, 30 }, new[] { 700, 700 });
            Assert.AreEqual(new[] { -1, 1, 0 }, s);     // fully tied → stable: idx0 hard, idx1 normal
        }

        [Test]
        public void ByLevel_Null_TieBreak_Falls_Back_To_Index()
        {
            var s = ExternalDifficultyPicker.Assign(new[] { 30, 30 }, null);
            Assert.AreEqual(new[] { -1, 1, 0 }, s);     // no tie-break list → index order, same as legacy overload
        }
    }
}
