using System;

namespace Sdo.UI.Services
{
    /// <summary>
    /// 「這一局跑哪個場景」—— 房間設定(隨機 / 指定某個場景)→ 一個具體的 scene id。
    ///
    /// 🔴 這一步的結果**不能寫回房間設定**(GameSession.StageId/StageFolder/StageRandom)。
    /// 房間設定是房主選的東西,會畫在房間 win2 的場景縮圖上、也會被推給 server;抽出來的場景只屬於這一局。
    /// 兩者混在一起的症狀就是「選了隨機場景,一進遊戲房間那張縮圖就變成抽到的那張,而且下一局不再隨機」。
    /// 落點見 <c>GameSession.RoundStageFolder</c>。
    ///
    /// 線上與離線共用同一段:線上由房主抽好交給 server 驗+echo(RoomScreen.BuildResolvedRound),
    /// 離線在按下「開始」時自己抽(RoomScreen.ResolveLocalRoundStage)。兩邊都得到「每一局重抽一次」。
    ///
    /// 與 Unity 無關 → 可單元測試(見 RoundStageChoiceTests)。
    /// </summary>
    public static class RoundStageChoice
    {
        /// <param name="stageRandom">房間設定是「隨機場景」嗎。</param>
        /// <param name="settingSceneId">
        /// 房間設定指定的場景 id。<paramref name="stageRandom"/> 為 true 時**完全不看**它 ——
        /// 隨機時那個欄位裡放的是上一次抽的佔位值,拿它當結果等於「只抽一次之後永遠是同一個場景」。
        /// </param>
        /// <param name="maxSceneId">可選場景的最大 id(含)。線上是 NetLimits.MaxSceneId,兩邊相等有測試守著。</param>
        /// <param name="rangeExclusive">[min, max) 取一個整數,對映 UnityEngine.Random.Range(int,int)。</param>
        public static int Pick(bool stageRandom, int settingSceneId, int maxSceneId, Func<int, int, int> rangeExclusive)
        {
            if (maxSceneId < 0) return 0;
            if (!stageRandom) return Clamp(settingSceneId, 0, maxSceneId);
            if (rangeExclusive == null) return Clamp(settingSceneId, 0, maxSceneId);
            return Clamp(rangeExclusive(0, maxSceneId + 1), 0, maxSceneId);
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
