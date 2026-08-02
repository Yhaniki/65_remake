using System.Collections;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 診斷用:把**真正的**結算大頭貼(ScreenGameplay.BuildLocalHeadPortrait → BuildIdleHeadAvatar + 頭部相機 +
    /// headPortraitLayer 隔離)用使用者實際的穿搭跑起來,把 RT 存成 PNG,並印出每個 renderer 的 subMesh/材質/貼圖。
    /// Run: -runTests -testPlatform PlayMode -testFilter Sdo.Tests.ResultHeadPortraitLiveTest
    /// </summary>
    public class ResultHeadPortraitLiveTest
    {
        private static readonly string[] UserParts =
        {
            "AVATAR/012974_WOMAN_FACE_HUAN.MSH",
            "AVATAR/020466_WOMAN_HAIR.MSH",
            "AVATAR/000892_WOMAN_ONE.MSH",
            "AVATAR/002179_WOMAN_SHOES.MSH",
            "AVATAR/023297_WOMAN_HAND.MSH",
        };

        private const string OutDir = "C:/Users/user/AppData/Local/Temp/claude/h--65-remake/fd1d4ba4-9951-4b95-a4e7-7676a0b41c43/scratchpad/";

        [UnityTearDown]
        public IEnumerator TearDown() => GameplayBoot.Teardown();

        [UnityTest, Explicit]
        public IEnumerator LiveResultHeadPortrait_RendersWholeOnePiece()
        {
            ScreenGameplay game = null;
            yield return GameplayBoot.Boot(g => game = g, g => { g.avatarParts = UserParts; });
            Assert.IsNotNull(game);
            for (int i = 0; i < 8; i++) yield return null;

            var mi = typeof(ScreenGameplay).GetMethod("BuildLocalHeadPortrait",
                                                      BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(mi, "BuildLocalHeadPortrait 不見了");
            var tex = mi.Invoke(game, null) as Texture;
            Assert.IsNotNull(tex, "結算頭貼 RT 是 null (resultHeadPortrait 關掉了?)");
            for (int i = 0; i < 4; i++) yield return null;

            var head = GameObject.Find("HeadIdleAvatar");
            Assert.IsNotNull(head, "頭貼 avatar 沒建出來");
            Debug.Log("[livehead] ==== 真正的結算頭貼 avatar ====");
            foreach (var mr in head.GetComponentsInChildren<MeshRenderer>())
            {
                var mesh = mr.GetComponent<MeshFilter>()?.sharedMesh;
                var mats = mr.sharedMaterials;
                var sb = new System.Text.StringBuilder();
                sb.Append($"[livehead] {mr.name}: layer={mr.gameObject.layer} enabled={mr.enabled} " +
                          $"subMesh={(mesh != null ? mesh.subMeshCount : -1)} mats={mats.Length}");
                for (int i = 0; i < mats.Length; i++)
                    sb.Append($" | [{i}] {(mats[i] != null ? mats[i].shader.name : "NULL")} " +
                              $"tex={(mats[i] != null && mats[i].mainTexture != null ? "yes" : "NO")} " +
                              $"nm='{(mats[i] != null ? mats[i].name : "")}'");
                Debug.Log(sb.ToString());
            }

            var cam = GameObject.Find("HeadPortraitCam")?.GetComponent<Camera>();
            Assert.IsNotNull(cam, "頭貼相機不見了");
            cam.Render();

            var rt = tex as RenderTexture;
            Assert.IsNotNull(rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var shot = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            shot.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            shot.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(OutDir + "head-live-result.png", shot.EncodeToPNG());
            Debug.Log($"[livehead] wrote head-live-result.png ({rt.width}x{rt.height})");
            Object.DestroyImmediate(shot);
        }
    }
}
