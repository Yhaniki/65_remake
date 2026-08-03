using System;
using Sdo.Net;

namespace Sdo.UI.Services
{
    /// <summary>
    /// 「這一局站哪一種個人隊形」—— 房間設定(0=基本 1=扇形 2=環線 **3=隨機**)→ 官方三張座標表之一(0..2)。
    ///
    /// 🔴 與 <see cref="RoundStageChoice"/> 完全同型的一件事,理由也一樣:抽出來的值**不能寫回**
    /// <c>GameSession.Formation</c>。那是房間設定 —— 選歌對話框底下的隊形下拉讀它、線上也會被
    /// <c>NetRoomSettingsPublisher</c> 推給 server。寫回去的話「隨機隊形」只隨機一次,打完那一局就
    /// 定死成抽到的那一種。落點是 <c>GameSession.RoundFormationType</c>。
    ///
    /// 與 Unity 無關 → 可單元測試(見 RoundFormationChoiceTests)。
    /// </summary>
    public static class RoundFormationChoice
    {
        /// <param name="settingFormation">房間設定的隊形 0..3(3 = 隨機)。</param>
        /// <param name="rangeExclusive">[min, max) 取一個整數,對映 UnityEngine.Random.Range(int,int)。</param>
        public static int Pick(int settingFormation, Func<int, int, int> rangeExclusive)
        {
            const int count = NetResolvedRound.FormationTypeCount;   // 官方只有三張個人隊形表
            if (settingFormation >= count && rangeExclusive != null) return Clamp(rangeExclusive(0, count), 0, count - 1);
            return Clamp(settingFormation, 0, count - 1);
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
