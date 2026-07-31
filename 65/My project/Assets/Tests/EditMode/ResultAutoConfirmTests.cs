using System.IO;
using System.Reflection;
using NUnit.Framework;
using Sdo.Game;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>
    /// 線上結算:沒人按確定,面板開著 30 秒後要自己按下去(回房間)。
    /// 走的必須是按確定同一條路(<c>OnConfirm</c>),而且一局只送一次 —— 逾時之後每一幀都還在 Tick。
    /// </summary>
    public class ResultAutoConfirmTests
    {
        [Test]
        public void OpenPanel_AutoConfirmsOnceAfterTimeout()
        {
            RunOnPanel(30f, (result, confirms) =>
            {
                result.Tick();
                Assert.AreEqual(0, confirms(), "面板才剛開,不該自動確定");

                SetShowStart(result, Time.time - 29.5f);
                result.Tick();
                Assert.AreEqual(0, confirms(), "還沒滿 30 秒就自動確定了");

                SetShowStart(result, Time.time - 30.1f);
                result.Tick();
                Assert.AreEqual(1, confirms(), "滿 30 秒沒人按 → 要自動確定");

                result.Tick();
                result.Tick();
                Assert.AreEqual(1, confirms(), "確定只能送一次(逾時後每幀都還在 Tick)");
            });
        }

        [Test]
        public void OfflinePanel_NeverAutoConfirms()
        {
            RunOnPanel(0f, (result, confirms) =>
            {
                SetShowStart(result, Time.time - 600f);
                result.Tick();
                Assert.AreEqual(0, confirms(), "單機沒設逾時 → 永遠等玩家按確定");
            });
        }

        // Build + Show 一個結算面板,把 OnConfirm 的呼叫次數交給 body 檢查,結束後清乾淨。
        private static void RunOnPanel(float autoConfirmSec, System.Action<ResultScreen, System.Func<int>> body)
        {
            if (!File.Exists(Path.Combine(SdoExtracted.ResultStatisDir, "Statis25.an")))
                Assert.Ignore("STATISTIC art not present in this environment.");

            GameObject hudGo = null, root = null;
            try
            {
                hudGo = new GameObject("HudCamResultAutoConfirm");
                var hud = hudGo.AddComponent<Camera>();
                hud.enabled = false;

                int confirms = 0;
                var result = new ResultScreen();
                result.Build(hud);
                result.OnConfirm = () => confirms++;
                result.autoConfirmSec = autoConfirmSec;
                result.Show("song", "Lv 1", new[]
                {
                    new ResultScreen.Row { Rank = 1, Name = "me", IsLocal = true, Score = 10 },
                }, localWon: true, expGained: 1, coinsGained: 0);
                root = (GameObject)typeof(ResultScreen)
                    .GetField("_root", BindingFlags.NonPublic | BindingFlags.Instance)
                    .GetValue(result);

                body(result, () => confirms);
            }
            finally
            {
                if (root != null) Object.DestroyImmediate(root);
                if (hudGo != null) Object.DestroyImmediate(hudGo);
            }
        }

        // 面板出現的時刻(Tick 用它算經過秒數)—— 往回撥就等於「已經過了那麼久」。
        private static void SetShowStart(ResultScreen result, float t) =>
            typeof(ResultScreen).GetField("_showStart", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(result, t);
    }
}
