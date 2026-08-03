using NUnit.Framework;
using UnityEngine;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 角色的「隱形深度分身」(頭上聊天泡只被人擋的那一半)的結構合約。
    ///
    /// 這些性質沒有一條是「畫面上看得出來」的:分身本來就看不見,錯了只會表現成
    /// 「泡有時候被擋、有時候不被擋」或「某些程式突然多看到一倍 renderer」。
    /// </summary>
    public class RoomPeopleDepthProxyTests
    {
        private GameObject _avatar, _proxyRoot;

        [SetUp]
        public void SetUp()
        {
            _avatar = new GameObject("FakeAvatar");
            AddPart("Body", Shader.Find("Unlit/Texture"), 0f);
            AddPart("Hair", Shader.Find("Sdo/UnlitDoubleSided"), 0.3f);
            _proxyRoot = new GameObject("PeopleDepthRoot") { layer = RoomScene3D.PeopleDepthLayer };
        }

        [TearDown]
        public void TearDown()
        {
            if (_avatar != null) Object.DestroyImmediate(_avatar);
            if (_proxyRoot != null) Object.DestroyImmediate(_proxyRoot);
        }

        private MeshRenderer AddPart(string name, Shader shader, float cutoff)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            go.transform.SetParent(_avatar.transform, false);
            var mat = new Material(shader) { name = name + "Mat", mainTexture = Texture2D.whiteTexture };
            if (mat.HasProperty("_Cutoff")) mat.SetFloat("_Cutoff", cutoff);
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            return mr;
        }

        [Test]
        public void Every_Renderer_Gets_A_Twin_On_The_Depth_Layer_Below_The_Bubbles()
        {
            var proxy = RoomPeopleDepthProxy.Attach(_avatar, _proxyRoot.transform, RoomScene3D.PeopleDepthLayer);
            Assert.IsNotNull(proxy, "分身元件建不起來(shader 不在?)");
            Assert.AreEqual(2, proxy.ProxyCount, "身上兩個 renderer 應該各有一個分身");

            var twins = _proxyRoot.GetComponentsInChildren<Renderer>();
            Assert.AreEqual(2, twins.Length);
            foreach (var t in twins)
            {
                Assert.AreEqual(RoomScene3D.PeopleDepthLayer, t.gameObject.layer, "分身不在深度那一層 → 房間相機看不到它");
                Assert.AreEqual(RoomPeopleDepthProxy.ProxySortingOrder, t.sortingOrder,
                    "分身的 sortingOrder 必須夾在「深度重置片」與「泡」之間,否則它寫的深度會被重置片洗掉");
                Assert.AreEqual(RoomPeopleDepthProxy.ShaderName, t.sharedMaterial.shader.name,
                    "分身必須用只寫深度的 shader —— 用到本尊的材質就會多畫一層(紗質衣物會變濃)");
            }
        }

        [Test]
        public void Proxies_Live_Outside_The_Avatar_Hierarchy()
        {
            int before = _avatar.GetComponentsInChildren<Renderer>(true).Length;
            RoomPeopleDepthProxy.Attach(_avatar, _proxyRoot.transform, RoomScene3D.PeopleDepthLayer);
            int after = _avatar.GetComponentsInChildren<Renderer>(true).Length;

            Assert.AreEqual(before, after,
                "分身變成角色的後代了 —— 專案裡到處都有人對角色做 GetComponentsInChildren<Renderer>()"
                + "(量厚度、拍頭貼時關掉別人、衣物檢查),它們會突然多看到一倍的 renderer");
        }

        [Test]
        public void Twin_Material_Copies_The_Cutout_Settings()
        {
            RoomPeopleDepthProxy.Attach(_avatar, _proxyRoot.transform, RoomScene3D.PeopleDepthLayer);
            var hairTwin = FindTwin("Hair");
            var bodyTwin = FindTwin("Body");

            // 頭髮是 alpha cutout:分身不裁的話,泡會被「一張看不見的頭髮方片」咬掉一塊。
            Assert.AreEqual(0.3f, hairTwin.sharedMaterial.GetFloat("_Cutoff"), 1e-4f, "沒抄到頭髮的 _Cutoff");
            Assert.IsNotNull(hairTwin.sharedMaterial.GetTexture("_MainTex"), "沒抄到貼圖 → 裁切門檻沒有依據");

            // 實心材質沒有 _Cutoff → 一律 0(= 完全不裁)。貼圖 alpha 整片 0 的 DXT1 布料很常見,
            // 給非 0 的門檻會把人整片挖空(見 [[unity-dxt1-alpha-cutout-trap]])。
            Assert.AreEqual(0f, bodyTwin.sharedMaterial.GetFloat("_Cutoff"), 1e-4f);
        }

        [Test]
        public void Rescan_Picks_Up_Parts_Added_Later()
        {
            var proxy = RoomPeopleDepthProxy.Attach(_avatar, _proxyRoot.transform, RoomScene3D.PeopleDepthLayer);
            Assert.AreEqual(2, proxy.ProxyCount);

            // 翅膀之類的部件是後來才掛上去的(AvatarWingRig)—— 只掃一次的話它們永遠擋不住泡。
            AddPart("Wing", Shader.Find("Sdo/UnlitDoubleSided"), 0.3f);
            proxy.Rescan();
            Assert.AreEqual(3, proxy.ProxyCount, "後來才掛上去的部件沒有被補上分身");
            Assert.AreEqual(3, _proxyRoot.GetComponentsInChildren<Renderer>().Length);

            proxy.Rescan();
            Assert.AreEqual(3, proxy.ProxyCount, "重掃不該重複建分身");
        }

        private Renderer FindTwin(string srcName)
        {
            foreach (var r in _proxyRoot.GetComponentsInChildren<Renderer>())
                if (r.name.StartsWith(srcName)) return r;
            Assert.Fail("找不到 " + srcName + " 的分身");
            return null;
        }
    }
}
