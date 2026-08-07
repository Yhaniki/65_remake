using System.Collections.Generic;
using NUnit.Framework;
using Sdo.UI.Catalog;
using Sdo.UI.Screens;

namespace Sdo.Tests
{
    /// <summary>
    /// 選歌面板底下那排「房主設定」widget 與 session 的對齊。
    ///
    /// 🔴 為什麼會錯:這個面板**只 BuildUI 一次**,四個 widget(模式/隊形/旁觀/場景)的值是建版面當下
    /// 從 session 讀的。平常它們自己就是 session 的寫入者,但 session 會被別的地方改掉 ——
    /// 線上非房主每份房間快照都把房間設定收進 session(<c>NetRoomSettingsPublisher.AdoptIfNotHost</c>)。
    /// 使用者回報的症狀:房主設 ShowTime 再把房主讓給我,我打開選歌選單那格還是寫「自由模式」。
    /// 修法是每次 OnShow 都把 widget 拉回 session(<c>SongSelectScreen.SyncRoomSettingWidgets</c>)。
    ///
    /// 這裡測的是場景那一格能不能從 session 反推回選擇器位置 —— 三個下拉走
    /// <c>SdoComboBox.SetIndexWithoutNotify</c>,見 SdoComboBoxSetIndexPlayTests。
    /// </summary>
    public class SongSelectRoomSettingWidgetsTests
    {
        /// <summary>選擇器只放 id 0..30(見 SongSelectScreen.BuildUI 的過濾)。</summary>
        private static IList<StageInfo> Stages()
        {
            var list = new List<StageInfo>();
            foreach (var s in StageCatalog.Stages)
                if (s.Id >= 0 && s.Id <= StageCatalog.MaxSelectableId) list.Add(s);
            return list;
        }

        [Test]
        public void Random_Scene_Is_Slot_Zero()
        {
            Assert.AreEqual(0, SongSelectScreen.SceneSelectorIndex(Stages(), true, 12),
                "隨機 → 第 0 格,不管 StageId 佔位值是什麼");
        }

        [Test]
        public void A_Concrete_Scene_Maps_To_Its_List_Position_Plus_One()
        {
            var stages = Stages();
            for (int i = 0; i < stages.Count; i++)
                Assert.AreEqual(i + 1, SongSelectScreen.SceneSelectorIndex(stages, false, stages[i].Id),
                    "第 0 格是「隨機」,所以清單位置要 +1");
        }

        [Test]
        public void An_Unknown_Scene_Falls_Back_To_Random()
        {
            // 34 是場景表裡的空號;婚禮房(31+)不在選擇器裡。認不得就退回隨機那格,不要指到別的場景。
            Assert.AreEqual(0, SongSelectScreen.SceneSelectorIndex(Stages(), false, 34));
            Assert.AreEqual(0, SongSelectScreen.SceneSelectorIndex(Stages(), false, 38));
            Assert.AreEqual(0, SongSelectScreen.SceneSelectorIndex(null, false, 3));
        }
    }
}
