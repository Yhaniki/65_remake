using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 頭貼(房間左上大頭貼 / 結算大頭貼)的「多材質 range」回歸測試。
    ///
    /// 起因(使用者回報):女生穿 lolita 連身裙 <c>000892_WOMAN_ONE</c> 時,房間左上的大頭貼「一部分沒有顯示出來,
    /// 顯示完全藍色」。那件衣服的第一顆 submesh 帶兩條材質 range —— range0 = 深藍裙 (000892_WOMAN_COAT.DDS)、
    /// range1 = 白圍裙+荷葉邊+紅蝴蝶結 (000892_WOMAN_COAT2.DDS)。<see cref="MshLoader"/> 早就依 range 把 mesh 拆成
    /// 兩個 subMesh,但頭貼那條路徑刻意「收成單一材質」(舊假設:頭貼看不到衣服)。Unity 在 materials 少於
    /// subMeshCount 時**不畫**多出來的 subMesh → 白圍裙整片消失,只剩藍裙 = 使用者看到的一片藍。
    ///
    /// 這不是單一件衣服的問題:語料裡多 range 的部位非常普遍 (001934_WOMAN_ONE 三張貼圖、003136_WOMAN_COAT 四條
    /// range、連 023297_WOMAN_HAND 都有兩條),頭貼一律只畫得出第一段。所以下面第一個測試是**不變式**:頭貼建出來
    /// 的每個 renderer,材質數必須蓋滿它的 subMesh 數。
    ///
    /// 兩條頭貼管線都測:房間頭貼走 <see cref="SdoRoomAvatar.Build"/> 的 <c>RenderMode.PortraitHead</c>,
    /// 遊戲內/結算頭貼走 <see cref="SdoAvatarBuilder.LoadParts"/> 的 <c>SkinStyle.Portrait</c>。
    /// EditMode 可跑 (Camera.Render 不需要 play mode);沒有遊戲資料時自動 Ignore。
    /// </summary>
    public class PortraitMultiRangeGarmentTests
    {
        private const string Lolita = "AVATAR/000892_WOMAN_ONE.MSH";   // 藍裙 + 白圍裙紅蝴蝶結,使用者回報的那件

        /// <summary>頭貼會入鏡的部位裡,已知帶多條材質 range 的真實檔案。</summary>
        private static readonly string[] MultiRangeParts =
        {
            Lolita,                                 // 連身裙:裙身 + 圍裙 (兩張不同貼圖) —— 使用者回報的那件
            "AVATAR/001934_WOMAN_ONE.MSH",          // 連身裙:三條 range、三張不同貼圖
            "AVATAR/003136_WOMAN_COAT.MSH",         // 上衣:一顆 submesh 四條 range (同一張貼圖,仍要四份材質才畫得完)
            "AVATAR/023297_WOMAN_HAND.MSH",         // 連手部件都有兩條 range
        };

        private static bool DataMissing(string rel)
        {
            var p = SdoAvatarBuilder.ResolveAvatarFile(rel);
            return string.IsNullOrEmpty(p) || !File.Exists(p);
        }

        private static IEnumerable<string> Parts() => MultiRangeParts;

        /// <summary>不變式:頭貼建出來的每個 renderer,材質數必須 ≥ 它 mesh 的 subMeshCount。少一個材質,Unity 就少畫
        /// 一段幾何 —— 那正是「大頭貼一部分沒有顯示出來」。房間頭貼管線 (RenderMode.PortraitHead)。</summary>
        [Test, TestCaseSource(nameof(Parts))]
        public void RoomPortrait_EverySubMeshGetsAMaterial(string rel)
        {
            if (DataMissing(rel)) Assert.Ignore("AVATAR 資料不在 (data_root.txt) — 略過");

            var root = new GameObject("PortraitProbe");
            try
            {
                var av = SdoRoomAvatar.Build(root, 0, portraitOpaque: true, male: false, equippedParts: new[] { rel });
                Assert.IsNotNull(av, rel + ": 頭貼 avatar 建不起來");
                av.enabled = false;
                AssertMaterialsCoverSubMeshes(root, rel, "房間頭貼 (RenderMode.PortraitHead)");
            }
            finally { Object.DestroyImmediate(root); }
        }

        /// <summary>同一個不變式,走遊戲內/結算頭貼的管線 (SdoAvatarBuilder.SkinStyle.Portrait)。</summary>
        [Test, TestCaseSource(nameof(Parts))]
        public void GameplayHeadPortrait_EverySubMeshGetsAMaterial(string rel)
        {
            if (DataMissing(rel)) Assert.Ignore("AVATAR 資料不在 (data_root.txt) — 略過");

            var root = new GameObject("HeadPortraitProbe");
            try
            {
                var built = SdoAvatarBuilder.LoadParts(root, null, new[] { rel }, SdoAvatarBuilder.SkinStyle.Portrait, "h_");
                Assert.AreEqual(1, built.Parts, rel + ": 部位沒載進來");
                AssertMaterialsCoverSubMeshes(root, rel, "結算/遊戲內頭貼 (SkinStyle.Portrait)");
            }
            finally { Object.DestroyImmediate(root); }
        }

        private static void AssertMaterialsCoverSubMeshes(GameObject root, string rel, string pipeline)
        {
            int checkedRenderers = 0, multi = 0;
            foreach (var mr in root.GetComponentsInChildren<MeshRenderer>())
            {
                var mesh = mr.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null) continue;
                checkedRenderers++;
                if (mesh.subMeshCount > 1) multi++;
                Assert.GreaterOrEqual(mr.sharedMaterials.Length, mesh.subMeshCount,
                    $"{rel} 在{pipeline}裡,renderer '{mr.name}' 有 {mesh.subMeshCount} 個 subMesh 卻只有 " +
                    $"{mr.sharedMaterials.Length} 個材質 → Unity 不會畫多出來的那幾段幾何。\n" +
                    "= 使用者回報的「大頭貼一部分沒有顯示出來」(000892 lolita 的白圍裙+紅蝴蝶結整片不見,只剩藍裙)。");
                foreach (var m in mr.sharedMaterials)
                    Assert.IsNotNull(m, $"{rel} 在{pipeline}裡 renderer '{mr.name}' 有 null 材質槽 → 該段一樣不會畫");
            }
            Assert.Greater(checkedRenderers, 0, rel + ": 一個 renderer 都沒建出來");
            Assert.Greater(multi, 0, rel + ": 這件應該有多 subMesh 的部位 —— 測試資料選錯了,換一件真的多 range 的");
        }

        /// <summary>使用者那件 lolita 的具體斷言:**同一顆 submesh** 的兩條 range 要拿到兩張不同貼圖 —— 裙身
        /// (000892_WOMAN_COAT.DDS) 與圍裙 (000892_WOMAN_COAT2.DDS)。注意不能改看「整個 avatar 用到幾張貼圖」:
        /// 衣服自帶的身體皮膚 (W_Basic) 是另一顆 submesh,收成單一材質時那個數字照樣 ≥2,測不到這個 bug。</summary>
        [Test]
        public void RoomPortrait_Lolita_AssignsBothSkirtAndApronTextures()
        {
            if (DataMissing(Lolita)) Assert.Ignore("AVATAR 資料不在 (data_root.txt) — 略過");

            var root = new GameObject("LolitaPortraitProbe");
            try
            {
                var av = SdoRoomAvatar.Build(root, 0, portraitOpaque: true, male: false, equippedParts: new[] { Lolita });
                Assert.IsNotNull(av, "lolita 頭貼 avatar 建不起來");
                av.enabled = false;

                MeshRenderer garment = null;
                foreach (var mr in root.GetComponentsInChildren<MeshRenderer>())
                {
                    var mesh = mr.GetComponent<MeshFilter>()?.sharedMesh;
                    if (mesh != null && mesh.subMeshCount > 1) { garment = mr; break; }
                }
                Assert.IsNotNull(garment, "找不到多 subMesh 的衣服 renderer — 000892 的第一顆 submesh 應該有兩條 range");

                var textures = new HashSet<Texture>();
                foreach (var m in garment.sharedMaterials)
                    if (m != null && m.mainTexture != null) textures.Add(m.mainTexture);

                Assert.GreaterOrEqual(textures.Count, 2,
                    $"000892 的衣服 submesh 有兩條 range(藍裙 coat.dds / 白圍裙+紅蝴蝶結 coat2.dds),頭貼卻只給了 " +
                    $"{textures.Count} 張貼圖 → 圍裙那段沒有自己的材質,不會被畫出來。");
            }
            finally { Object.DestroyImmediate(root); }
        }

        /// <summary>端到端像素:把頭貼管線建出來的 lolita 正面渲進透明 RT,圍裙那張貼圖 (000892_WOMAN_COAT2.DDS)
        /// 獨有的**紅蝴蝶結**必須真的出現在畫面上。判準挑紅色而不是白色是有原因的:藍裙貼圖自己的裙襬就有白蕾絲,
        /// 拿白色當判準時,圍裙整段沒畫也照樣「有白像素」(這個測試第一版就是這樣漏掉的)。紅色只存在於 coat2.dds,
        /// 沒紅 = 圍裙那段 subMesh 沒被畫 = 使用者截圖裡的一片藍。</summary>
        [Test]
        public void RoomPortrait_Lolita_RendersWhiteApronPixels()
        {
            if (DataMissing(Lolita)) Assert.Ignore("AVATAR 資料不在 (data_root.txt) — 略過");

            const int W = 192, H = 256;
            GameObject root = null; RenderTexture rt = null; Camera cam = null; Texture2D shot = null;
            try
            {
                root = new GameObject("LolitaPixelProbe");
                var av = SdoRoomAvatar.Build(root, 0, portraitOpaque: true, male: false, equippedParts: new[] { Lolita });
                Assert.IsNotNull(av, "lolita 頭貼 avatar 建不起來");
                av.enabled = false;

                var b = MergedBounds(root);
                Assert.Greater(b.size.magnitude, 0.01f, "沒有任何 renderer bounds");

                rt = new RenderTexture(W, H, 16, RenderTextureFormat.ARGB32);
                var camGo = new GameObject("LolitaPixelCam");
                cam = camGo.AddComponent<Camera>();
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

                int lit = 0, white = 0, red = 0;
                foreach (var p in shot.GetPixels32())
                {
                    if (p.a <= 25) continue;
                    lit++;
                    int min = Mathf.Min(p.r, Mathf.Min(p.g, p.b)), max = Mathf.Max(p.r, Mathf.Max(p.g, p.b));
                    if (min >= 200 && max - min <= 40) white++;              // 近白低飽和 (含藍裙自己的白蕾絲,僅診斷用)
                    if (p.r >= 130 && p.g <= 90 && p.b <= 90) red++;         // 紅蝴蝶結 — 只有圍裙貼圖 coat2.dds 有紅
                }
                Assert.Greater(lit, 0, "整張圖都是空的 —— 衣服根本沒畫");
                Debug.Log($"[lolita-portrait] lit={lit} white={white} ({white / (float)lit:P2}) red={red} ({red / (float)lit:P2})");

                Assert.Greater(red, 0,
                    $"頭貼管線畫出來的 lolita 完全沒有紅色像素 (共 {lit} 個有色像素,白 {white}) — 紅蝴蝶結只存在於圍裙貼圖 " +
                    "000892_WOMAN_COAT2.DDS,代表圍裙那條 range 的 subMesh 沒被畫出來,整件只剩深藍裙。\n" +
                    "= 使用者回報「大頭貼一部分沒有顯示出來,顯示完全藍色」。");
            }
            finally
            {
                if (cam != null) { cam.targetTexture = null; Object.DestroyImmediate(cam.gameObject); }
                if (shot != null) Object.DestroyImmediate(shot);
                if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); }
                if (root != null) Object.DestroyImmediate(root);
            }
        }

        /// <summary>人眼複核用 (預設不跑,-testFilter 點名才跑):走**真正的** <see cref="RoomHeadPortrait"/> 取景,
        /// 把使用者當時的整套穿搭渲成 PNG,並附一張「修正前」對照 —— 對照組把多 subMesh 的 renderer 手動收回單一
        /// 材質(等於舊的 singleMaterial 行為),不必改回產品程式碼就能並排比較。寫到 repo 根 portrait-lolita-*.png。</summary>
        [Test, Explicit]
        public void Snapshot_RoomHeadPortrait_UserOutfit()
        {
            if (DataMissing(Lolita)) Assert.Ignore("AVATAR 資料不在 (data_root.txt) — 略過");

            // 使用者 profile 00000000 的實際穿搭 (臉/髮/lolita 連身裙/鞋/手/天使之翼)
            var parts = new[]
            {
                "AVATAR/012974_WOMAN_FACE_HUAN.MSH", "AVATAR/020466_WOMAN_HAIR.MSH",
                "AVATAR/000892_WOMAN_ONE.MSH",       "AVATAR/002179_WOMAN_SHOES.MSH",
                "AVATAR/023297_WOMAN_HAND.MSH",      "AVATAR/023691_WOMAN_CHIBANG.MSH",
            };

            foreach (var variant in new[] { "after", "before" })
            {
                var host = new GameObject("PortraitHost_" + variant);
                try
                {
                    var hp = host.AddComponent<RoomHeadPortrait>();
                    hp.rtWidth = 384; hp.rtHeight = 304;             // 2× 房間槽位尺寸,看得清楚
                    Assert.IsTrue(hp.Init(male: false, avatarParts: parts, bodyIndex: 0), "頭貼 Init 失敗");

                    if (variant == "before")                          // 模擬舊行為:多 subMesh 只餵一個材質
                        foreach (var mr in host.GetComponentsInChildren<MeshRenderer>())
                        {
                            var mesh = mr.GetComponent<MeshFilter>()?.sharedMesh;
                            if (mesh != null && mesh.subMeshCount > 1) mr.sharedMaterials = new[] { mr.sharedMaterials[0] };
                        }

                    var cam = host.GetComponentInChildren<Camera>();
                    Assert.IsNotNull(cam, "頭貼相機不見了");
                    cam.Render();

                    var rt = hp.Texture as RenderTexture;
                    Assert.IsNotNull(rt, "頭貼 RT 是 null");
                    var prev = RenderTexture.active; RenderTexture.active = rt;
                    var shot = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                    shot.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); shot.Apply();
                    RenderTexture.active = prev;
                    string path = "H:/65_remake/portrait-lolita-" + variant + ".png";
                    File.WriteAllBytes(path, shot.EncodeToPNG());
                    Debug.Log("[portrait-snap] wrote " + path);
                    Object.DestroyImmediate(shot);
                }
                finally { Object.DestroyImmediate(host); }
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
