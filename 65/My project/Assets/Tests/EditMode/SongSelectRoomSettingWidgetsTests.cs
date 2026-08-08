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

        /// <summary>
        /// ◄ ► 只是在選擇器上挪一格(隨機那格也在環裡),兩端環繞。
        ///
        /// 🔴 配套的行為是:翻場景**不寫 session** —— 換場景要按「確定」才生效
        /// (<c>SongSelectScreen.ApplySceneToSession</c> 只有 <c>OnConfirm</c> 呼叫)。
        /// 曾經是一按 ◄ ► 就套用,症狀是房主還在翻場景、外面房間 win2 的場景縮圖就跟著一格一格跳,
        /// 而且沒按確定就關掉也回不去原本的場景。
        /// </summary>
        [Test]
        public void Scene_Step_Wraps_Through_The_Random_Slot()
        {
            int n = Stages().Count;
            Assert.AreEqual(1, SongSelectScreen.SceneStepIndex(0, +1, n), "隨機 → 第一個場景");
            Assert.AreEqual(n, SongSelectScreen.SceneStepIndex(0, -1, n), "隨機往回 → 最後一個場景");
            Assert.AreEqual(0, SongSelectScreen.SceneStepIndex(n, +1, n), "最後一個場景 → 繞回隨機");
            Assert.AreEqual(0, SongSelectScreen.SceneStepIndex(1, -1, n), "第一個場景往回 → 隨機");
            // 走完一整圈回到原點:每一格都走得到,沒有卡住或跳過的位置。
            int pos = 0;
            for (int i = 0; i < n + 1; i++) pos = SongSelectScreen.SceneStepIndex(pos, +1, n);
            Assert.AreEqual(0, pos);
            // 沒有任何場景時只剩隨機那格,怎麼按都待在 0(不會除以零)。
            Assert.AreEqual(0, SongSelectScreen.SceneStepIndex(0, +1, 0));
            Assert.AreEqual(0, SongSelectScreen.SceneStepIndex(0, -1, 0));
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
