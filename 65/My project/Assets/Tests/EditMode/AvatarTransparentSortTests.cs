using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Game;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>
    /// **跨角色**的半透明排序:站在後面的人的衣服不可以畫到站在前面的人的翅膀前面。
    ///
    /// 使用者回報(遊戲舞台截圖):「後面有一個人穿了金姬兰,但是衣服卻畫在前面的人的翅膀再更前面」。
    ///
    /// 機制:<see cref="SdoAvatarBuilder.TransparentGarmentQueue"/> 給每個角色的半透明**身體衣物**一組
    /// 遞增的 renderQueue(3000+載入順序) —— 那是為了釘住**同一個角色內部**的疊色順序(否則 Unity 的
    /// 逐幀距離排序會讓兩件重疊的紗互相翻面 = 使用者當年的「褲子會一直閃爍」)。但那個計數器是
    /// **每個角色各自從 0 開始**的,而翅膀(CHIBANG,不是 body garment)根本不進這條 → 停在 shader 的
    /// Queue=Transparent(3000)。於是:
    ///   前面那個人的翅膀      queue 3000、ZWrite OFF(alpha-blend,不寫深度)
    ///   後面那個人的金姬兰紗  queue 3000..3017、ZWrite ON
    /// 紗的 queue 比較大 → **後畫**;而翅膀沒寫深度 → 沒有東西可以擋住它 → 遠處的裙子直接蓋在近處的
    /// 翅膀上面。renderQueue 是硬性的先後,它壓過了「誰離相機比較近」。
    /// </summary>
    public class AvatarTransparentSortTests
    {
        private const int Layer = 4;
        private const int W = 96, H = 96;

        private readonly List<Object> _trash = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _trash.Count; i++) if (_trash[i] != null) Object.DestroyImmediate(_trash[i]);
            _trash.Clear();
            AvatarTranslucentDepth.SweepOrphans();
            AvatarTransparentRank.CameraProvider = null;
        }

        // ---- 純邏輯 ----

        /// <summary>遠的先畫 → rank 小。</summary>
        [Test]
        public void RanksByDistance_PutsTheFarthestFirst()
        {
            var ranks = AvatarTransparentRank.RanksByDistance(new[] { 100f, 400f, 25f });
            Assert.AreEqual(new[] { 1, 0, 2 }, ranks);
        }

        /// <summary>同距離要**穩定**:兩個站在同一排的人不可以每幀互換 rank(那就是新的閃爍)。</summary>
        [Test]
        public void RanksByDistance_IsStableForTies()
        {
            var ranks = AvatarTransparentRank.RanksByDistance(new[] { 100f, 100f, 100f });
            Assert.AreEqual(new[] { 0, 1, 2 }, ranks);
        }

        /// <summary>角色內部的順序原封不動地放在低位;不同 rank 的區段不重疊。</summary>
        [Test]
        public void QueueFor_KeepsWithinCharacterOrder_AndSeparatesCharacters()
        {
            Assert.AreEqual(3000, AvatarTransparentRank.QueueFor(0, 0));
            Assert.AreEqual(3017, AvatarTransparentRank.QueueFor(0, 17));
            Assert.Greater(AvatarTransparentRank.QueueFor(1, 0), AvatarTransparentRank.QueueFor(0, AvatarTransparentRank.Band - 1));
        }

        /// <summary>🔴 最高的 queue 一定要 &lt; 深度分身的 3500,否則分身會跑到衣物前面寫深度、把它自己的層裁掉。</summary>
        [Test]
        public void HighestQueue_StaysBelowTheDepthTwin()
            => Assert.Less(AvatarTransparentRank.QueueFor(AvatarTransparentRank.MaxRank, AvatarTransparentRank.Band - 1),
                           AvatarTranslucentDepth.DepthQueue);

        /// <summary>溢出 Band 的件夾在最後一格 —— 不會跨進下一個角色的區段。</summary>
        [Test]
        public void QueueFor_ClampsOverflowIntoItsOwnBand()
        {
            int last = AvatarTransparentRank.QueueFor(0, AvatarTransparentRank.Band - 1);
            Assert.AreEqual(last, AvatarTransparentRank.QueueFor(0, 999));
            Assert.Less(last, AvatarTransparentRank.QueueFor(1, 0));
        }

        /// <summary>每個「多個角色會同框」的畫面都要掛上去。商城/儲物櫃**刻意不掛**(8 張卡各自渲染到自己的 RT,
        /// 排進同一個名冊只會讓 queue 每幀亂跳) —— 所以 <c>SdoAvatarBuilder</c> 不在這張清單裡。</summary>
        [TestCase("Game/ScreenGameplay.cs")]          // 舞台:本機舞者
        [TestCase("Game/ScreenGameplay.Dancers.cs")]  // 舞台:其他舞者
        [TestCase("Game/SdoRoomAvatar.cs")]           // 房間/選性別
        public void ScreenWithMultipleCharacters_AttachesTheRank(string relPath)
        {
            string path = System.IO.Path.Combine(Application.dataPath, "Scripts",
                                                 relPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (!System.IO.File.Exists(path)) Assert.Ignore(relPath + " 不在這份簽出裡");
            StringAssert.Contains("AvatarTransparentRank.Attach", System.IO.File.ReadAllText(path),
                relPath + " 會同時放好幾個角色,卻沒有掛跨角色的半透明排序 → 後面的人的紗會蓋到前面的人的翅膀上");
        }

        private T Track<T>(T o) where T : Object { _trash.Add(o); return o; }

        private Texture2D Solid(Color c, float a)
        {
            var t = Track(new Texture2D(4, 4, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point });
            var px = new Color[16];
            for (int i = 0; i < 16; i++) px[i] = new Color(c.r, c.g, c.b, a);
            t.SetPixels(px); t.Apply();
            return t;
        }

        /// <summary>一片掛在自己 root 底下的半透明衣物(= 一個角色身上的一件)。</summary>
        private GameObject Character(string name, Shader sh, Texture2D tex, float z, float scale, int queue, bool sheerState)
        {
            var root = Track(new GameObject(name));
            root.transform.position = new Vector3(0f, 0f, z);   // 角色站的位置 = rank 依它算距離
            var quad = Track(GameObject.CreatePrimitive(PrimitiveType.Quad));
            quad.layer = Layer;
            quad.transform.SetParent(root.transform, false);
            quad.transform.localPosition = Vector3.zero;
            quad.transform.localScale = Vector3.one * scale;
            var m = Track(new Material(sh) { mainTexture = tex, name = name + "_cloth" });
            if (sheerState) SdoAvatarBuilder.ApplySheerMaterialState(m, 0.5f);   // 紗:ZWrite ON + 雙面 (與真實衣物同一支)
            m.renderQueue = queue;
            quad.GetComponent<MeshRenderer>().sharedMaterial = m;
            return root;
        }

        private Color Shoot(Camera cam, RenderTexture rt)
        {
            cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = Track(new Texture2D(W, H, TextureFormat.RGBA32, false));
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            return tex.GetPixel(W / 2, H / 2);
        }

        private Camera Probe(out RenderTexture rt)
        {
            var camGo = Track(new GameObject("sortProbeCam"));
            var cam = camGo.AddComponent<Camera>();
            cam.enabled = false;
            cam.fieldOfView = 45f; cam.aspect = 1f;
            cam.nearClipPlane = 5f; cam.farClipPlane = 500f;
            cam.cullingMask = 1 << Layer;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            rt = Track(new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32));
            cam.targetTexture = rt;
            camGo.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);   // 看 +Z
            return cam;
        }

        /// <summary>
        /// 重現使用者的畫面:近處一對半透明翅膀(queue 3000, ZWrite OFF)、遠處一件不透明的紗裙
        /// (queue 3017, ZWrite ON)。翅膀在前面,所以它的紅色**一定**要出現在合成結果裡。
        /// 排序壞掉時遠處的裙子後畫、又不透明 → 中央像素變成純綠色 = 裙子畫到翅膀前面。
        /// </summary>
        [Test]
        public void FarCharactersGarment_DoesNotPaintOverANearWing()
        {
            bool asyncWas = UnityEditor.EditorSettings.asyncShaderCompilation;
            UnityEditor.EditorSettings.asyncShaderCompilation = false;   // 冷 shader cache 首跑會全黑
            try
            {
                var alpha = Shader.Find("Sdo/UnlitAvatarAlpha");
                var sheer = Shader.Find("Sdo/UnlitAvatarSheer");
                Assert.IsNotNull(alpha, "Sdo/UnlitAvatarAlpha 不見了");
                Assert.IsNotNull(sheer, "Sdo/UnlitAvatarSheer 不見了");

                var cam = Probe(out var rt);
                // 遠(z=200)的角色先建,拿到大一點的 queue —— 正是真實情況:金姬兰 18 段紗吃掉 3000..3017。
                var far = Character("farDress", sheer, Solid(Color.green, 1f), 200f, 90f,
                                    SdoAvatarBuilder.TransparentGarmentQueue(17), sheerState: true);
                var near = Character("nearWing", alpha, Solid(Color.red, 0.6f), 100f, 60f,
                                     SdoAvatarBuilder.TransparentGarmentQueue(0), sheerState: false);
                AvatarTranslucentDepth.Attach(near);   // 翅膀那條真的會補深度分身(ZWrite OFF)
                AvatarTranslucentDepth.Attach(far);
                Assert.AreEqual(1, AvatarTransparentRank.Attach(far));
                Assert.AreEqual(1, AvatarTransparentRank.Attach(near));
                AvatarTransparentRank.CameraProvider = () => cam;
                AvatarTransparentRank.ReorderNowForTest();

                var px = Shoot(cam, rt);
                Assert.Greater(px.r, 0.15f,
                    $"中央像素 {px} 幾乎沒有翅膀的紅色 → 站在後面的人的裙子畫到前面的人的翅膀上面了 " +
                    "(遠處衣物的 renderQueue 比近處翅膀大 → 後畫,而翅膀 ZWrite OFF 擋不住它)");
                Assert.AreEqual(0, far.GetComponent<AvatarTransparentRank>().RankForTest, "遠的人要先畫");
                Assert.AreEqual(1, near.GetComponent<AvatarTransparentRank>().RankForTest);
            }
            finally { UnityEditor.EditorSettings.asyncShaderCompilation = asyncWas; }
        }
    }
}
