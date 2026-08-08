using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// 「別人的模型什麼時候可以當場建起來」的決策 —— 守的是使用者回報的
    /// 「收到遠端的 mmd 檔案會整個凍結一下」。
    ///
    /// 冷的模型在一幀裡建起來 ＝ .pmx 解析 ＋ 整批貼圖解碼(初音實測 1.5 秒),遊戲中途沒有載入畫面
    /// 可以藏,而且主執行緒凍住連帶讓心跳停(見 <see cref="NetKeepAliveTests"/>)。
    /// </summary>
    public class MmdStagingPolicyTests
    {
        [Test]
        public void ColdModel_IsStaged_NotBuiltInOneFrame()
            => Assert.AreEqual(MmdBuildPlan.Stage,
                               MmdStagingPolicy.For(modelReady: true, alreadyStaging: false, sharedAssetsWarm: false),
                               "第一次要用的模型被當場建起來 —— 那一幀會凍住 1.5 秒以上");

        [Test]
        public void WarmModel_IsBuiltImmediately()
            => Assert.AreEqual(MmdBuildPlan.BuildNow,
                               MmdStagingPolicy.For(modelReady: true, alreadyStaging: false, sharedAssetsWarm: true),
                               "共用資產已經在快取裡還走預熱 —— 只剩 11~26 ms 的骨架,卻讓他晚三幀才出現");

        [Test]
        public void ModelNotHereYet_DoesNothing()
        {
            // 「還沒下載完」不是失敗:那一隻就先維持他自己的 SDO 穿搭。
            Assert.AreEqual(MmdBuildPlan.Skip,
                            MmdStagingPolicy.For(modelReady: false, alreadyStaging: false, sharedAssetsWarm: false));
            Assert.AreEqual(MmdBuildPlan.Skip,
                            MmdStagingPolicy.For(modelReady: false, alreadyStaging: false, sharedAssetsWarm: true));
        }

        /// <summary>
        /// 同一包可能有好幾隻在等(同房兩個人穿同一份模型),而補建迴圈每 0.25 秒會回頭問一次 ——
        /// 不擋掉的話會排出好幾趟同樣的預熱,每一趟都把整包貼圖再解一次。
        /// </summary>
        [Test]
        public void AlreadyStaging_NeverStartsASecondPass()
        {
            Assert.AreEqual(MmdBuildPlan.Skip,
                            MmdStagingPolicy.For(modelReady: true, alreadyStaging: true, sharedAssetsWarm: false));
            Assert.AreEqual(MmdBuildPlan.Skip,
                            MmdStagingPolicy.For(modelReady: true, alreadyStaging: true, sharedAssetsWarm: true));
        }

        /// <summary>
        /// 打歌中不開始新的預熱 —— 分幀之後每張仍有 20~30 ms 的 <c>Apply</c>,連續十幾幀在節奏遊戲裡是最糟的。
        /// 收模型這件事只在房間/大廳做。
        /// </summary>
        [Test]
        public void OnStage_ColdModelWaitsUntilTheSongEnds()
            => Assert.AreEqual(MmdBuildPlan.Skip,
                               MmdStagingPolicy.For(modelReady: true, alreadyStaging: false,
                                                    sharedAssetsWarm: false, onStage: true),
                               "打歌中把冷模型拿去預熱 —— 每張貼圖上傳都會讓一幀變長");

        /// <summary>
        /// 但**暖的照建**:那只剩 11~26 ms 的骨架,而遠端舞者一上台就需要它 ——
        /// 一起擋掉的話,房間裡明明已經下載好的模型會整首歌都顯示成 SDO 穿搭。
        /// </summary>
        [Test]
        public void OnStage_WarmModelStillBuilds()
            => Assert.AreEqual(MmdBuildPlan.BuildNow,
                               MmdStagingPolicy.For(modelReady: true, alreadyStaging: false,
                                                    sharedAssetsWarm: true, onStage: true),
                               "已經預熱好的模型在舞台上被擋掉 —— 那個人整首歌都會是 SDO 穿搭");
    }
}
