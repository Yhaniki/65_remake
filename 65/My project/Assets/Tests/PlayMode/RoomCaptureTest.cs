using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// Not an assertion test — spawns the waiting-room 3D scene + a head-portrait rig standalone and dumps each
    /// RenderTexture to disk so the camera framing (room scene RT) and the head-portrait angle (head RT) can be
    /// reviewed headlessly. Run: -runTests -testPlatform PlayMode -testFilter Sdo.Tests.RoomCaptureTest
    /// </summary>
    public class RoomCaptureTest
    {
        [UnityTest]
        public IEnumerator Capture_Room()
        {
            var sceneGo = new GameObject("RoomScene3D_cap");
            var scene = sceneGo.AddComponent<RoomScene3D>();
            scene.overview = true;        // frame the whole room (all 16 slots + mapobjs) for the review capture
            scene.Build();

            var headGo = new GameObject("RoomHead_cap");
            var head = headGo.AddComponent<RoomHeadPortrait>();
            head.Init();

            // let the avatar pose, the camera settle and the head auto-frame measure the hair-top
            for (int i = 0; i < 12; i++) yield return null;
            yield return new WaitForSecondsRealtime(0.8f);

            int n = 0;
            foreach (var c in Camera.allCameras)
            {
                if (c == null || c.targetTexture == null) continue;
                c.Render();
                Save(c.targetTexture, "H:/65_remake/room-cap-" + c.name + ".png");
                n++;
            }
            Debug.Log("[RoomCaptureTest] captured " + n + " RT cameras; scene.Ready=" + scene.Ready);
            Assert.Greater(n, 0, "no RT cameras captured");
        }

        private static void Save(RenderTexture rt, string path)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var t = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            t.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); t.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(path, t.EncodeToPNG());
            Object.Destroy(t);
            Debug.Log("[RoomCaptureTest] saved " + path + " (" + rt.width + "x" + rt.height + ")");
        }
    }
}
