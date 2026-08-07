using NUnit.Framework;
using Sdo.Game;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>
    /// 房間上排頭貼:**遠端那幾格的取景必須跟本機那格同一個行為**。
    ///
    /// 使用者回報:「遠端載了模組後,上面大頭貼的頭會嚴重晃動,跟本機的行為模式不一樣 —— 本機的不太會晃。」
    /// 病灶是 <see cref="RoomRemoteHeadSet"/> 把 SDO 那條的死區濾波(0.3 個頭高)套到 MMD 取景上,而本機那顆
    /// (<see cref="RoomHeadPortrait"/> 的 MMD 分支)是**每幀直接對準** <see cref="MmdAvatar.TryHeadBounds"/>,
    /// 中間沒有任何濾波。死區半徑 0.3 頭高 ≈ 框高的 16%(頭本身才佔 55%)→ 頭在格子裡大幅晃。
    ///
    /// 兩邊的框不是同一種東西,所以不能共用同一組常數:SDO 的中心是每幀從**臉的 mesh bounds** 算的(姿勢一變
    /// 框就形變 → 追它會抖 → 需要死區),MMD 的是錨在頭骨上、大小取自 rest 姿勢的直立盒子(轉頭/甩馬尾都不變
    /// → 追它是乾淨的)。
    /// </summary>
    public class RemoteHeadPortraitAimParityTests
    {
        private static T Make<T>() where T : Component
        {
            var go = new GameObject("aim-parity-" + typeof(T).Name);
            return go.AddComponent<T>();
        }

        [Test]
        public void RemoteMmdAim_HasNoFilter_LikeTheLocalPortrait()
        {
            var remote = Make<RoomRemoteHeadSet>();
            try
            {
                Assert.AreEqual(0f, remote.aimDeadZoneFaces,
                    "遠端頭貼的 MMD 取景又套上死區了 —— 本機那顆是每幀直接追頭框,頭會在格子裡晃");
                Assert.AreEqual(0f, remote.aimSmoothSec,
                    "遠端頭貼的 MMD 取景加了平滑 —— 相機會落後頭,一樣是晃");
            }
            finally { Object.DestroyImmediate(remote.gameObject); }
        }

        /// <summary>取景**構圖**的參數本來就要與本機同步(這條先前已經踩過:遠端頭貼比本機高 14% 框高)。
        /// 這裡只釘「預設值不准各自漂移」,實際餵值的是 RoomScreen(同一組常數餵兩顆)。</summary>
        [Test]
        public void RemoteFramingDefaults_MatchTheLocalPortrait()
        {
            var remote = Make<RoomRemoteHeadSet>();
            var local = Make<RoomHeadPortrait>();
            try
            {
                Assert.AreEqual(local.fov, remote.fov, 1e-4f, "fov 不一致 → 頭的大小不一樣");
                Assert.AreEqual(local.headFrameDist, remote.frameDist, 1e-4f, "相機距離不一致 → 頭忽大忽小");
                Assert.AreEqual(local.headAimUp, remote.aimUp, 1e-4f, "aimUp 不一致 → 頭的高低不一樣");
                Assert.AreEqual(local.zoom, remote.zoom, 1e-4f);
                Assert.AreEqual(local.fitHairTop, remote.fitHairTop, "髮頂處理不一致");
                Assert.AreEqual(local.rtWidth, remote.rtWidth);
                Assert.AreEqual(local.rtHeight, remote.rtHeight);
            }
            finally
            {
                Object.DestroyImmediate(remote.gameObject);
                Object.DestroyImmediate(local.gameObject);
            }
        }
    }
}
