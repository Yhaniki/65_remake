using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>Guards the head-top nameplate letter-spacing (字靠緊一點): the shared HeadName style lays out one
    /// TextMesh cell per character (true tracking, glyphs undistorted), while roster/looker lines keep the single-mesh
    /// path regardless of length.</summary>
    public class TextStylesTests
    {
        private static Label3D Head(string text)
        {
            var l = TextStyles.NewLabel("h", TextStyles.Style.HeadName, 0, 22f, TextAnchor.MiddleCenter);
            l.Text = text;
            return l;
        }

        private static Label3D Roster(string text)
        {
            var l = TextStyles.NewLabel("l", TextStyles.Style.ListLocal, 0, 22f, TextAnchor.MiddleLeft);
            l.Text = text;
            return l;
        }

        [Test]
        public void HeadName_BuildsOneCellPerCharacter_RosterDoesNot()
        {
            var head1 = Head("A");
            var head3 = Head("ABC");
            var list1 = Roster("A");
            var list3 = Roster("ABC");
            try
            {
                int h1 = head1.root.GetComponentsInChildren<TextMesh>(true).Length;
                int h3 = head3.root.GetComponentsInChildren<TextMesh>(true).Length;
                Assert.Greater(h3, h1, "head nameplate should build one cell per character (per-char tracking)");

                int l1 = list1.root.GetComponentsInChildren<TextMesh>(true).Length;
                int l3 = list3.root.GetComponentsInChildren<TextMesh>(true).Length;
                Assert.AreEqual(l1, l3, "roster line must use a single mesh regardless of text length");
            }
            finally
            {
                Object.DestroyImmediate(head1.root); Object.DestroyImmediate(head3.root);
                Object.DestroyImmediate(list1.root); Object.DestroyImmediate(list3.root);
            }
        }

        [Test]
        public void HeadNameTrack_TightensButStaysReasonable()
        {
            // >0 pulls characters closer; a sane ceiling so glyphs don't collide into an unreadable blob.
            Assert.Greater(TextStyles.HeadNameTrackEm, 0f);
            Assert.LessOrEqual(TextStyles.HeadNameTrackEm, 0.25f);
        }
    }
}
