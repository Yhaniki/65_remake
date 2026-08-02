using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 診斷用(Explicit):把「結算大頭貼」(SdoAvatarBuilder.SkinStyle.Portrait) 與「房間大頭貼」
    /// (SdoRoomAvatar.RenderMode.PortraitHead) 兩條管線用同一件衣服各渲一張,比對畫出來的像素量與 shader。
    /// 使用者回報結算左邊的大頭貼「一件式的衣服部分會被省略」,房間那張是對的。
    /// </summary>
    public class ResultHeadPortraitSnapshotTests
    {
        private const string OutDir = "C:/Users/user/AppData/Local/Temp/claude/h--65-remake/fd1d4ba4-9951-4b95-a4e7-7676a0b41c43/scratchpad/";

        // 使用者 profile 00000000 擁有的連身裙 (ownedItems 的 1?xxxxxx → modelId),挑實際有 _WOMAN_ONE mesh 的
        private static readonly string[] OnePieces =
        {
            "AVATAR/000892_WOMAN_ONE.MSH",   // lolita — 之前房間頭貼的那件
            "AVATAR/024976_WOMAN_ONE.MSH",   // 金姬兰 花朵蕾絲 (真紗質 → 官方 blend)
            "AVATAR/000886_WOMAN_ONE.MSH",
            "AVATAR/000898_WOMAN_ONE.MSH",
            "AVATAR/001226_WOMAN_ONE.MSH",
            "AVATAR/001322_WOMAN_ONE.MSH",
            "AVATAR/001324_WOMAN_ONE.MSH",
            "AVATAR/001445_WOMAN_ONE.MSH",
            "AVATAR/001485_WOMAN_ONE.MSH",
            "AVATAR/001562_WOMAN_ONE.MSH",
            "AVATAR/001631_WOMAN_ONE.MSH",
            "AVATAR/001667_WOMAN_ONE.MSH",
            "AVATAR/001711_WOMAN_ONE.MSH",
            "AVATAR/001766_WOMAN_ONE.MSH",
            "AVATAR/012712_WOMAN_ONE.MSH",
            "AVATAR/016803_WOMAN_ONE.MSH",
        };

        private static bool DataMissing(string rel)
        {
            var p = SdoAvatarBuilder.ResolveAvatarFile(rel);
            return string.IsNullOrEmpty(p) || !File.Exists(p);
        }

        /// <summary>對每件連身裙:兩條頭貼管線各渲一張正面圖,報告「畫出來的像素數」。結算那條把所有材質都套
        /// Sdo/PortraitOpaque(clip a&lt;0.3),房間那條依 alpha 類別選 Unlit/Texture(不裁)/UnlitDoubleSided(裁)/
        /// Sheer(alpha-blend) —— 差多少像素就是結算頭貼被裁掉多少。</summary>
        [Test, Explicit]
        public void Compare_ResultVsRoom_OnePieceCoverage()
        {
            var lines = new List<string>();
            foreach (var rel in OnePieces)
            {
                if (DataMissing(rel)) { lines.Add($"{rel}: (資料不在)"); continue; }
                int room = RenderPipeline(rel, roomPipeline: true, null);
                int result = RenderPipeline(rel, roomPipeline: false, null);
                float drop = room > 0 ? 1f - result / (float)room : 0f;
                lines.Add($"{Path.GetFileName(rel)}: room={room} result={result} 結算少畫={drop:P1}");
            }
            foreach (var l in lines) Debug.Log("[onepiece] " + l);
        }

        /// <summary>把差最多的兩件存成 PNG 給人眼看。</summary>
        [Test, Explicit]
        public void Snapshot_ResultVsRoom_TwoGarments()
        {
            foreach (var rel in new[] { "AVATAR/000898_WOMAN_ONE.MSH", "AVATAR/001485_WOMAN_ONE.MSH" })
            {
                if (DataMissing(rel)) continue;
                string stem = Path.GetFileNameWithoutExtension(rel);
                RenderPipeline(rel, roomPipeline: true, OutDir + stem + "-room.png");
                RenderPipeline(rel, roomPipeline: false, OutDir + stem + "-result.png");
            }
        }

        // 建一件衣服 (只有這件,沒有臉/髮),正面正交渲進透明 RT,回傳「有畫到的像素數」;pngPath 非 null 就存檔。
        private static int RenderPipeline(string rel, bool roomPipeline, string pngPath)
        {
            const int W = 256, H = 384;
            GameObject root = null; RenderTexture rt = null; Camera cam = null; Texture2D shot = null;
            try
            {
                root = new GameObject("Probe");
                if (roomPipeline)
                {
                    var av = SdoRoomAvatar.Build(root, 0, portraitOpaque: true, male: false, equippedParts: new[] { rel });
                    Assert.IsNotNull(av, rel + ": room build 失敗");
                    av.enabled = false;
                }
                else
                {
                    // 結算頭貼管線 = ScreenGameplay.BuildIdleHeadAvatar:一定要有骨架 + idle 姿勢,否則各 submesh
                    // 停在自己的 bind space、位置全錯(那是測試的錯,不是產品的 bug)。
                    var av = root.AddComponent<SdoAvatar>();
                    var hrc = AvatarAssetCache.Hrc(SdoAvatarBuilder.ResolveAvatarFile(AvatarOutfit.FemaleHrc));
                    Assert.IsNotNull(hrc, "FEMALE.HRC 讀不到");
                    var idle = SdoRoomAvatar.LoadMot("MOTION/WREST0072.MOT");
                    av.Setup(hrc, idle);
                    av.SetBodyShape(SdoBodyShape.WeightFromIndex(0, false));
                    av.RestMot = idle;
                    av.DanceEnabled = () => false;
                    av.DanceTimeSec = () => -1f;
                    var built = SdoAvatarBuilder.LoadParts(root, av, new[] { rel }, SdoAvatarBuilder.SkinStyle.Portrait, "h_");
                    Assert.AreEqual(1, built.Parts, rel + ": result build 失敗");
                    av.PoseInitialIdle();
                    av.enabled = false;
                }
                DumpShaders(root, rel, roomPipeline ? "ROOM" : "RESULT");

                var b = MergedBounds(root);
                if (b.size.sqrMagnitude < 1e-4f) return 0;
                rt = new RenderTexture(W, H, 16, RenderTextureFormat.ARGB32);
                cam = new GameObject("ProbeCam").AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = b.extents.y * 1.1f + 0.001f;
                cam.transform.position = b.center + new Vector3(0f, 0f, -Mathf.Max(50f, b.size.z * 6f));
                cam.transform.LookAt(b.center);
                cam.nearClipPlane = 0.01f; cam.farClipPlane = 10000f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                cam.targetTexture = rt;
                cam.Render();

                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                shot = new Texture2D(W, H, TextureFormat.RGBA32, false);
                shot.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                shot.Apply();
                RenderTexture.active = prev;

                int lit = 0;
                foreach (var p in shot.GetPixels32()) if (p.a > 25) lit++;
                if (pngPath != null) { File.WriteAllBytes(pngPath, shot.EncodeToPNG()); Debug.Log("[onepiece] wrote " + pngPath); }
                return lit;
            }
            finally
            {
                if (cam != null) { cam.targetTexture = null; Object.DestroyImmediate(cam.gameObject); }
                if (shot != null) Object.DestroyImmediate(shot);
                if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); }
                if (root != null) Object.DestroyImmediate(root);
            }
        }

        private static void DumpShaders(GameObject root, string rel, string label)
        {
            foreach (var mr in root.GetComponentsInChildren<MeshRenderer>())
            {
                var mats = mr.sharedMaterials;
                var sb = new System.Text.StringBuilder();
                sb.Append($"[onepiece-sh] {label} {Path.GetFileName(rel)} {mr.name}:");
                foreach (var m in mats)
                    sb.Append($" | {(m != null ? m.shader.name.Replace("Sdo/", "") : "NULL")}");
                Debug.Log(sb.ToString());
            }
        }

        private static Bounds MergedBounds(GameObject root)
        {
            var rs = root.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return new Bounds(Vector3.zero, Vector3.zero);
            var b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            return b;
        }
    }
}
