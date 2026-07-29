using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Sdo.Game;
using UnityEngine;

namespace Sdo.Tests
{
    public class ResultPanelOnlineRefreshTests
    {
        [Test]
        public void ResultRows_UseOwnPortraitAndCanBeReplacedInPlace()
        {
            if (!File.Exists(Path.Combine(SdoExtracted.ResultStatisDir, "Statis25.an")))
                Assert.Ignore("STATISTIC art not present in this environment.");

            GameObject hudGo = null, root = null;
            Texture2D first = null, second = null;
            try
            {
                hudGo = new GameObject("HudCamResultRefresh");
                var hud = hudGo.AddComponent<Camera>();
                hud.enabled = false;
                first = new Texture2D(2, 2) { name = "FirstRemoteHead" };
                second = new Texture2D(2, 2) { name = "AuthoritativeRemoteHead" };

                var result = new ResultScreen();
                result.Build(hud);
                result.Show("song", "Lv 1", new[]
                {
                    new ResultScreen.Row
                    {
                        Rank = 1,
                        UserId = 7,
                        Name = "remote",
                        Head = first,
                        Score = 10,
                    },
                }, localWon: true, expGained: 1, coinsGained: 0);
                root = (GameObject)typeof(ResultScreen)
                    .GetField("_root", BindingFlags.NonPublic | BindingFlags.Instance)
                    .GetValue(result);

                MeshRenderer firstHead = null;
                foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (renderer.sharedMaterial == null || renderer.sharedMaterial.mainTexture != first) continue;
                    firstHead = renderer;
                    break;
                }
                Assert.IsNotNull(firstHead, "a remote row must use Row.Head, not the placeholder");
                float showStart = (float)typeof(ResultScreen)
                    .GetField("_showStart", BindingFlags.NonPublic | BindingFlags.Instance)
                    .GetValue(result);

                result.ReplaceRows(new[]
                {
                    new ResultScreen.Row
                    {
                        Rank = 2,
                        UserId = 7,
                        Name = "remote",
                        Head = second,
                        Score = 999,
                    },
                }, localWon: false, expGained: 77, coinsGained: 3);

                Assert.IsTrue(result.Visible);
                Assert.AreEqual(showStart, (float)typeof(ResultScreen)
                    .GetField("_showStart", BindingFlags.NonPublic | BindingFlags.Instance)
                    .GetValue(result));
                Assert.IsTrue(firstHead == null,
                    "the provisional portrait must be destroyed immediately in EditMode");

                MeshRenderer secondHead = null;
                foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (renderer.sharedMaterial == null || renderer.sharedMaterial.mainTexture != second) continue;
                    secondHead = renderer;
                    break;
                }
                Assert.IsNotNull(secondHead, "authoritative rows must replace the visible portrait");

                var rowRoots = (List<GameObject>)typeof(ResultScreen)
                    .GetField("_rowRoots", BindingFlags.NonPublic | BindingFlags.Instance)
                    .GetValue(result);
                Assert.AreEqual(1, rowRoots.Count);
                Assert.AreEqual("Row2", rowRoots[0].name);
                Assert.AreEqual(77L, (long)typeof(ResultScreen)
                    .GetField("_expTarget", BindingFlags.NonPublic | BindingFlags.Instance)
                    .GetValue(result));
                Assert.AreEqual(3L, (long)typeof(ResultScreen)
                    .GetField("_coinsTarget", BindingFlags.NonPublic | BindingFlags.Instance)
                    .GetValue(result));
                Assert.IsFalse((bool)typeof(ResultScreen)
                    .GetField("_bannerLocalWon", BindingFlags.NonPublic | BindingFlags.Instance)
                    .GetValue(result));
            }
            finally
            {
                if (root != null) Object.DestroyImmediate(root);
                if (hudGo != null) Object.DestroyImmediate(hudGo);
                if (first != null) Object.DestroyImmediate(first);
                if (second != null) Object.DestroyImmediate(second);
            }
        }
    }
}
