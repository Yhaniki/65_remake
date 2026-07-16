using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>Guards the letter-spacing tightening (字靠緊一點): the tracked styles (head nameplate + ranking
    /// roster) lay out one TextMesh cell per character (true tracking, glyphs undistorted), while the untracked
    /// Looker/spectator style keeps the single-mesh path regardless of length.</summary>
    public class TextStylesTests
    {
        private static Label3D Make(TextStyles.Style style, string text)
        {
            var l = TextStyles.NewLabel(style.ToString(), style, 0, 22f, TextAnchor.MiddleLeft);
            l.Text = text;
            return l;
        }

        private static int MeshCount(Label3D l) => l.root.GetComponentsInChildren<TextMesh>(true).Length;

        [Test]
        public void TrackedStyles_BuildOneCellPerCharacter_UntrackedDoesNot()
        {
            var head1 = Make(TextStyles.Style.HeadName, "A");
            var head3 = Make(TextStyles.Style.HeadName, "ABC");
            var list1 = Make(TextStyles.Style.ListOther, "A");
            var list3 = Make(TextStyles.Style.ListOther, "ABC");
            var look1 = Make(TextStyles.Style.Looker, "A");
            var look3 = Make(TextStyles.Style.Looker, "ABC");
            try
            {
                Assert.Greater(MeshCount(head3), MeshCount(head1), "head nameplate should build one cell per character");
                Assert.Greater(MeshCount(list3), MeshCount(list1), "ranking roster should build one cell per character");
                Assert.AreEqual(MeshCount(look1), MeshCount(look3), "spectator (Looker) line must use a single mesh regardless of length");
            }
            finally
            {
                Object.DestroyImmediate(head1.root); Object.DestroyImmediate(head3.root);
                Object.DestroyImmediate(list1.root); Object.DestroyImmediate(list3.root);
                Object.DestroyImmediate(look1.root); Object.DestroyImmediate(look3.root);
            }
        }

        [Test]
        public void SameLengthTextUpdate_ReusesCells_NoChurn()
        {
            var l = Make(TextStyles.Style.ListOther, "17408");
            try
            {
                int before = MeshCount(l);
                l.Text = "17500";   // same digit count → cells reused, not rebuilt
                Assert.AreEqual(before, MeshCount(l), "same-length update must reuse the existing per-char cells");
            }
            finally { Object.DestroyImmediate(l.root); }
        }

        [Test]
        public void TrackedTextMesh_OneCellPerChar_ReusesOnSameLength()
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var t = new TrackedTextMesh("song", font, 64, 11 * 0.2f, Color.white, 0, TextAnchor.MiddleLeft, TextStyles.SongTitleTrackEm);
            try
            {
                t.Text = "ABCDE";
                Assert.AreEqual(5, t.root.GetComponentsInChildren<TextMesh>(true).Length, "one cell per character");
                t.Text = "12345";   // same length → reuse the existing cells (no rebuild)
                Assert.AreEqual(5, t.root.GetComponentsInChildren<TextMesh>(true).Length, "same-length update must reuse cells");
            }
            finally { Object.DestroyImmediate(t.root); }
        }

        [Test]
        public void Track_TightensButStaysReasonable()
        {
            // >0 pulls characters closer; a sane ceiling so glyphs don't collide into an unreadable blob.
            Assert.Greater(TextStyles.HeadNameTrackEm, 0f);
            Assert.LessOrEqual(TextStyles.HeadNameTrackEm, 0.25f);
            Assert.Greater(TextStyles.RosterTrackEm, 0f);
            Assert.LessOrEqual(TextStyles.RosterTrackEm, 0.25f);
            Assert.Greater(TextStyles.SongTitleTrackEm, 0f);
            Assert.LessOrEqual(TextStyles.SongTitleTrackEm, 0.25f);
            // gameplay song title may be turned fully off (0 = natural spacing).
            Assert.GreaterOrEqual(TextStyles.GameSongTitleTrackEm, 0f);
            Assert.LessOrEqual(TextStyles.GameSongTitleTrackEm, 0.25f);
        }
    }
}
