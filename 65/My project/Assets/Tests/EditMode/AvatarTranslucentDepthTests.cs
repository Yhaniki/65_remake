using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Game;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>
    /// 半透明衣物/翅膀要進深度緩衝(使用者:穿 Ribbon Star M 037939 那類半透明翅膀的人,擋不住後面那個人的名牌)。
    ///
    /// 這裡釘兩件事,第二件才是重點:
    ///   ① 分身**真的擋得住**畫在它後面的東西(名字牌);
    ///   ② 分身**不可以改變衣服自己的樣子** —— 這是選這個做法(而不是把 ZWrite 打開 / 加 depth prepass)
    ///      的唯一理由。原 shader 加第二個 pass 試過兩次、兩次都讓每一件透明衣服整件消失
    ///      (見 UnlitAvatarSheer.shader 檔頭);把 ZWrite 打開則會讓後畫的那層被前面那層裁掉。
    ///      所以「開/關分身,衣服的像素一個都不能變」要有測試守著。
    /// </summary>
    public class AvatarTranslucentDepthTests
    {
        private const int Layer = 4;
        private const int W = 160, H = 160;

        private readonly List<Object> _trash = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _trash.Count; i++) if (_trash[i] != null) Object.DestroyImmediate(_trash[i]);
            _trash.Clear();
            // 🔴 分身住在角色階層外面 → 拆角色**不會**連帶拆掉它們。漏掉這行的代價實測過:
            //    留下來的只寫深度分身在整套 EditMode 跑下去時,污染了另一條完全無關的渲染測試。
            AvatarTranslucentDepth.SweepOrphans();
        }

        private T Track<T>(T o) where T : Object { _trash.Add(o); return o; }

        /// <summary>一張帶 alpha 的貼圖:左半 alpha=1(實)、右半 alpha=0.25(很淡)。</summary>
        private Texture2D HalfAlphaTex(Color rgb)
        {
            var t = Track(new Texture2D(8, 8, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point });
            var px = new Color[64];
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                    px[y * 8 + x] = new Color(rgb.r, rgb.g, rgb.b, x < 4 ? 1f : 0.25f);
            t.SetPixels(px); t.Apply();
            return t;
        }

        /// <summary>一件掛在 <paramref name="root"/> 底下的半透明衣物(quad + Sdo/UnlitAvatarAlpha)。</summary>
        private MeshRenderer Garment(GameObject root, Texture2D tex, Vector3 pos, float scale = 40f)
        {
            var quad = Track(GameObject.CreatePrimitive(PrimitiveType.Quad));
            quad.name = "garment";
            quad.layer = Layer;
            quad.transform.SetParent(root.transform, false);
            quad.transform.position = pos;
            quad.transform.localScale = Vector3.one * scale;
            var mr = quad.GetComponent<MeshRenderer>();
            var sh = Shader.Find("Sdo/UnlitAvatarAlpha");
            Assert.IsNotNull(sh, "Sdo/UnlitAvatarAlpha 不見了");
            mr.sharedMaterial = Track(new Material(sh) { mainTexture = tex, name = "cloth" });
            return mr;
        }

        [Test]
        public void Only_Translucent_Materials_Get_A_Twin()
        {
            var root = Track(new GameObject("avatar"));
            Garment(root, HalfAlphaTex(Color.green), new Vector3(0, 0, 100f));

            var opaque = Track(GameObject.CreatePrimitive(PrimitiveType.Quad));
            opaque.transform.SetParent(root.transform, false);
            opaque.GetComponent<MeshRenderer>().sharedMaterial =
                Track(new Material(Shader.Find("Unlit/Texture")) { mainTexture = HalfAlphaTex(Color.red) });

            Assert.AreEqual(1, AvatarTranslucentDepth.Attach(root), "不透明的布料也被補了分身(白費 draw call)");
        }

        [Test]
        public void An_Avatar_With_No_Translucent_Part_Keeps_No_Component()
        {
            var root = Track(new GameObject("avatar"));
            var opaque = Track(GameObject.CreatePrimitive(PrimitiveType.Quad));
            opaque.transform.SetParent(root.transform, false);
            opaque.GetComponent<MeshRenderer>().sharedMaterial = Track(new Material(Shader.Find("Unlit/Texture")));

            Assert.AreEqual(0, AvatarTranslucentDepth.Attach(root));
            Assert.IsNull(root.GetComponent<AvatarTranslucentDepth>(), "沒有半透明件卻留下每幀跑的元件");
        }

        [Test]
        public void The_Twin_Lives_Outside_The_Avatar_Hierarchy()
        {
            // 🔴 專案到處對角色做 GetComponentsInChildren<Renderer>()(量身高/拍頭貼開關別人/衣物檢查)。
            //    分身掛進角色底下會讓那些全部多看到一倍 renderer,而且頭貼那條會把分身搬進頭貼相機的 layer。
            var root = Track(new GameObject("avatar"));
            var src = Garment(root, HalfAlphaTex(Color.green), new Vector3(0, 0, 100f));
            int before = root.GetComponentsInChildren<Renderer>(true).Length;

            Assert.AreEqual(1, AvatarTranslucentDepth.Attach(root));
            var comp = root.GetComponent<AvatarTranslucentDepth>();
            Assert.IsNotNull(comp);
            Assert.AreEqual(before, root.GetComponentsInChildren<Renderer>(true).Length,
                            "分身被掛進角色階層裡了");
            Assert.IsNotNull(comp.HolderForTest);
            Assert.IsFalse(comp.HolderForTest.transform.IsChildOf(root.transform));

            var twin = comp.HolderForTest.GetComponentInChildren<MeshRenderer>(true);
            Assert.IsNotNull(twin, "沒有生出分身");
            Assert.AreEqual(AvatarTranslucentDepth.ShaderName, twin.sharedMaterial.shader.name);
            Assert.AreEqual(AvatarTranslucentDepth.DepthQueue, twin.sharedMaterial.renderQueue,
                            "分身沒排在所有衣物色彩批之後 → 會改變衣服的外觀");
            Assert.AreSame(src.GetComponent<MeshFilter>().sharedMesh,
                           twin.GetComponent<MeshFilter>().sharedMesh,
                           "分身沒共用同一顆 Mesh(CPU 蒙皮改寫的就是那一顆,不共用就不會跟著動)");
            Assert.AreEqual(src.gameObject.layer, twin.gameObject.layer);
        }

        [Test]
        public void Destroying_The_Avatar_Takes_The_Twin_With_It()
        {
            // 分身是**看不見的**:活過角色只會在畫面上默默切掉別人的名字。
            var root = Track(new GameObject("avatar"));
            Garment(root, HalfAlphaTex(Color.green), new Vector3(0, 0, 100f));
            AvatarTranslucentDepth.Attach(root);
            var holder = root.GetComponent<AvatarTranslucentDepth>().HolderForTest;
            Assert.IsNotNull(holder);

            Object.DestroyImmediate(root);
            // 執行期(play mode)靠 OnDestroy 就收掉了;DestroyImmediate 拆的角色,OnDestroy 裡的立即銷毀
            // 會被 Unity 吃掉 → 由這道清掃補上(它也是任何漏掉 OnDestroy 的路徑的後盾)。
            AvatarTranslucentDepth.SweepOrphans();
            Assert.IsTrue(holder == null, "角色拆了,只寫深度的分身還活著 —— 它看不見,卻會默默切掉別人的名字");
        }

        [Test]
        public void A_Missing_Shader_Degrades_Instead_Of_Throwing()
        {
            // 打包版把 shader strip 掉時要退回舊行為(不擋名字),不是整個角色壞掉。
            var root = Track(new GameObject("avatar"));
            Assert.DoesNotThrow(() => AvatarTranslucentDepth.Attach(null));
            Assert.AreEqual(0, AvatarTranslucentDepth.Attach(root));
        }

        /// <summary>
        /// 真資料:使用者回報的那對翅膀(Ribbon Star M,商品 id 37939 → 037939_MAN_CHIBANG)真的會拿到分身。
        ///
        /// 它的貼圖是 256×256 DXT3,全透明像素只有 0.1%、genuinely-translucent 佔 0.348(&gt; SheerTranslucentBar
        /// 0.21)→ 官方旗標路徑判成真紗質 → sheer 材質 ZWrite Off → 深度緩衝裡沒有它。
        /// 這條把「整條管線(建角色 → 分類 → 補分身)接得起來」釘住:光看 shader 猜不出來。
        /// </summary>
        [Test]
        public void The_Reported_Wing_037939_Gets_A_Depth_Twin()
        {
            const string wing = "AVATAR/037939_MAN_CHIBANG.MSH";
            var probe = SdoAvatarBuilder.ResolveAvatarFile(wing);
            if (string.IsNullOrEmpty(probe) || !System.IO.File.Exists(probe))
                Assert.Ignore("AVATAR data root not found — 這條需要真實遊戲資料(data_root.txt)");

            var root = Track(new GameObject("wingAvatar"));
            var built = SdoAvatarBuilder.LoadParts(root, null, new[] { wing }, SdoAvatarBuilder.SkinStyle.Gameplay);
            Assert.AreEqual(1, built.Parts, "翅膀 mesh 沒載進來");

            var comp = root.GetComponent<AvatarTranslucentDepth>();
            Assert.IsNotNull(comp, "Ribbon Star M 這對半透明翅膀沒拿到只寫深度的分身 —— 它會讓後面那個人的名牌透出來");
            Assert.Greater(comp.TwinCount, 0);
        }

        /// <summary>
        /// 使用者問「金姬兰 這件衣服因為透明度沒有深度…是否是舞台的位置沒有修正深度問題」。
        ///
        /// 遊戲舞台的舞者走的是 <see cref="SdoAvatarBuilder.LoadParts"/>(SkinStyle.Gameplay);房間那條
        /// (SdoRoomAvatar 自帶迴圈)是另一支。真正要保證的**不是**「有沒有分身」,而是「這件衣服有沒有進深度
        /// 緩衝」—— 有兩條路都算數:
        ///   ① 材質自己 ZWrite ON(<see cref="SdoAvatarBuilder.ApplySheerMaterialState"/> 對紗與實心去背布料
        ///      **都**打開,金姬兰 18 段紗走的就是這條);
        ///   ② ZWrite OFF 的(翅膀那種 alpha-blend)→ 補只寫深度的分身。
        /// 所以斷言是「沒有任何一段是『既不寫深度、又沒有分身』」。日後若有人把 ZWrite 關掉又忘了補分身,
        /// 這條就會紅 —— 那正是名牌會從裙子裡透出來的狀態。
        /// </summary>
        [Test]
        public void The_Reported_OnePiece_024976_IsInTheDepthBuffer_OnTheStagePath()
        {
            const string one = "AVATAR/024976_WOMAN_ONE.MSH";
            var probe = SdoAvatarBuilder.ResolveAvatarFile(one);
            if (string.IsNullOrEmpty(probe) || !System.IO.File.Exists(probe))
                Assert.Ignore("AVATAR data root not found — 這條需要真實遊戲資料(data_root.txt)");

            var root = Track(new GameObject("jinjilanAvatar"));
            var built = SdoAvatarBuilder.LoadParts(root, null, new[] { one }, SdoAvatarBuilder.SkinStyle.Gameplay);
            Assert.AreEqual(1, built.Parts, "金姬兰 mesh 沒載進來");

            var comp = root.GetComponent<AvatarTranslucentDepth>();
            int twins = comp != null ? comp.TwinCount : 0;
            var orphan = new System.Text.StringBuilder();
            int translucent = 0, selfDepth = 0;
            foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(true))
                foreach (var m in mr.sharedMaterials)
                {
                    if (m == null || m.mainTexture == null) continue;
                    if (m.renderQueue < 3000 || m.renderQueue >= AvatarTranslucentDepth.DepthQueue) continue;
                    translucent++;
                    if (m.HasProperty("_ZWriteMode") && m.GetFloat("_ZWriteMode") >= 0.5f) { selfDepth++; continue; }
                    if (twins == 0) orphan.Append($"\n  q={m.renderQueue} {m.shader?.name} '{m.name}'");
                }
            Assert.Greater(translucent, 0, "金姬兰一段半透明材質都沒有 → 這件的透明度整個沒生效,測試的前提已經不成立");
            Assert.AreEqual(0, orphan.Length,
                "這些段既不自己寫深度、又沒有深度分身 → 後面那個人的名牌會從裙子裡透出來:" + orphan);
            // 記錄實測結果:這件的深度來自材質自己的 ZWrite,不是分身(所以它本來就沒有 AvatarTranslucentDepth 元件)。
            Assert.AreEqual(translucent, selfDepth,
                "金姬兰改成靠分身提供深度了?那不是壞事,但這條測試的敘述要跟著更新");
        }

        /// <summary>頭貼/商城卡(Portrait)刻意不掛:那是不透明合成到透明 RT,多一份深度只會有害無益。</summary>
        [Test]
        public void The_Portrait_Path_Gets_No_Twin()
        {
            const string wing = "AVATAR/037939_MAN_CHIBANG.MSH";
            var probe = SdoAvatarBuilder.ResolveAvatarFile(wing);
            if (string.IsNullOrEmpty(probe) || !System.IO.File.Exists(probe))
                Assert.Ignore("AVATAR data root not found");

            var root = Track(new GameObject("portraitAvatar"));
            SdoAvatarBuilder.LoadParts(root, null, new[] { wing }, SdoAvatarBuilder.SkinStyle.Portrait);
            Assert.IsNull(root.GetComponent<AvatarTranslucentDepth>());
        }

        // ---- 渲染:擋得住名牌,而且衣服自己一個像素都沒變 ----

        [Test]
        public void The_Twin_Occludes_A_Later_Draw_Without_Changing_The_Garment()
        {
            // 冷 shader cache 的第一次 render 會畫成全黑(非同步編譯還沒完成)—— 無頭跑時一定要關掉,
            // 否則這條測試是「有時綠有時紅」。見 MmdLilToonRenderTests 的同一段。
            bool asyncWas = UnityEditor.EditorSettings.asyncShaderCompilation;
            UnityEditor.EditorSettings.asyncShaderCompilation = false;
            try { RunOcclusionProbe(); }
            finally { UnityEditor.EditorSettings.asyncShaderCompilation = asyncWas; }
        }

        private void RunOcclusionProbe()
        {
            var camGo = Track(new GameObject("depthProbeCam"));
            var cam = camGo.AddComponent<Camera>();
            cam.enabled = false;
            cam.orthographic = false;
            cam.fieldOfView = 45f;
            cam.aspect = 4f / 3f;
            cam.nearClipPlane = 5f; cam.farClipPlane = 500f;
            cam.cullingMask = 1 << Layer;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            var rt = Track(new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32));
            cam.targetTexture = rt;
            camGo.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);   // 看 +Z

            // 半透明衣物在前(z=100),「名字牌」在後(z=140):不寫深度的 quad,sortingOrder 1 = 永遠最後畫,
            // 與房間名字牌同一組狀態(見 RoomNamePlateAnchor.SortingBase)。
            var root = Track(new GameObject("avatar"));
            Garment(root, HalfAlphaTex(Color.green), new Vector3(0f, 0f, 100f), 60f);

            var plate = Track(GameObject.CreatePrimitive(PrimitiveType.Quad));
            plate.layer = Layer;
            plate.transform.position = new Vector3(0f, 0f, 140f);
            plate.transform.localScale = Vector3.one * 20f;
            var plateMr = plate.GetComponent<MeshRenderer>();
            plateMr.sharedMaterial = Track(new Material(Shader.Find("Sdo/DepthText")) { color = Color.white });
            plateMr.sortingOrder = 1;

            var before = Shoot(cam, rt);
            int plateBefore = CountWhite(before);
            Assert.Greater(plateBefore, 200, "「名字牌」本來就沒畫出來,這條測試沒在驗東西");

            Assert.AreEqual(1, AvatarTranslucentDepth.Attach(root));
            var after = Shoot(cam, rt);

            // ① 名字牌被半透明衣物擋掉(貼圖左半 alpha=1 → 高於 cutoff → 寫深度)。
            Assert.Less(CountWhite(after), plateBefore * 0.6f,
                "半透明衣物還是擋不住畫在它後面的名字牌 —— 只寫深度的分身沒生效");

            // ② 🔴 衣服自己的像素一個都不能變。分身是 ColorMask 0 又排在所有色彩批之後,
            //    所以這條只要紅了就是做法本身錯了(不是參數要調)。
            int changed = 0;
            for (int i = 0; i < before.Length; i++)
                if (!NearlyWhite(before[i]) && !NearlyWhite(after[i]) && Differs(before[i], after[i])) changed++;
            Assert.AreEqual(0, changed, "掛上分身之後衣服自己的顏色變了 —— 分身不再是外觀中性的");
        }

        private static bool Differs(Color32 a, Color32 b)
            => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) > 12;

        private static bool NearlyWhite(Color32 p) => p.r > 170 && p.g > 170 && p.b > 170;

        private static int CountWhite(Color32[] px)
        {
            int n = 0;
            foreach (var p in px) if (NearlyWhite(p)) n++;
            return n;
        }

        private static Color32[] Shoot(Camera cam, RenderTexture rt)
        {
            cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            var px = tex.GetPixels32();
            Object.DestroyImmediate(tex);
            return px;
        }
    }
}
