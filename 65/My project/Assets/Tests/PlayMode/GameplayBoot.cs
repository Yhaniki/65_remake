using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Sdo.Game;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Sdo.Tests
{
    /// <summary>
    /// 在 PlayMode 測試裡把遊玩畫面（<see cref="ScreenGameplay"/>）開起來、跑完再拆乾淨。
    ///
    /// 為什麼需要這支：**gameplay 已經不會自我 boot 了**。前端才是入口 —— <c>FrontendApp</c> 在 BeforeSceneLoad
    /// 就把 <see cref="ScreenGameplay.AutoBootSuppressed"/> 設起來（否則自動 boot 出來的那份會留下孤兒舞者），
    /// 並且開頭幾幀持續 KillStrayGameplay。所以測試要自己：請走前端 → 自己建畫面 → **等它真的備妥**。
    ///
    /// 「等」用輪詢而不是睡固定秒數：<see cref="ScreenGameplay.FixedCamCountForTest"/> 只有在 _camReady
    /// （CAMERA/* 載完）之後才 > 0，這正是相機測試需要的訊號。原本睡 2.5 秒的寫法在冷開機時本來就會擲骰子。
    ///
    /// 拆除照 FrontendApp.TeardownGameplay 的做法：開場前先快照場景 root，結束時只砍「新長出來的」——
    /// ScreenGameplay 不把 board/avatar/scene 掛在自己底下，每個都是獨立 root。不拆的話會漏到後面的測試
    /// （ChartEditorTest 就會因為看到殘留的 StageScene / Avatar3D 而紅）。
    /// </summary>
    public static class GameplayBoot
    {
        private static HashSet<GameObject> _preRoots;

        /// <summary>
        /// 請走前端、建一份 ScreenGameplay，等固定鏡頭載好後把它交給 <paramref name="onReady"/>。
        /// <paramref name="configure"/> 在 AddComponent 之後、Start() 之前跑 —— 要指定舞台（scenePath）或
        /// scene-only 模式（observeBurstMode）就寫在這裡，等同官方 auto-boot 讀 SDO_SCENE / SDO_SCENE_ONLY 那段。
        /// </summary>
        public static IEnumerator Boot(Action<ScreenGameplay> onReady, Action<ScreenGameplay> configure = null,
            float timeoutSec = 120f)
        {
            DestroyNow("FrontendApp");        // 不請走它，它會在接下來幾幀把我們建的 gameplay 殺掉
            DestroyNow("FrontendCanvas");

            // 場上若有別的測試留下來的 ScreenGameplay，**不能沿用** —— 它的模式/舞台不見得是我們要的
            // （例：編輯器模式那份不載場景也不載相機 → 這裡會一路等到 timeout）。一律砍掉重開。
            foreach (var stray in UnityEngine.Object.FindObjectsByType<ScreenGameplay>(FindObjectsSortMode.None))
                UnityEngine.Object.DestroyImmediate(stray.gameObject);
            yield return null;

            _preRoots = new HashSet<GameObject>(SceneManager.GetActiveScene().GetRootGameObjects());

            var game = new GameObject("ScreenGameplay").AddComponent<ScreenGameplay>();
            configure?.Invoke(game);   // Start() 還沒跑 → 這裡設的欄位才吃得到

            float t0 = Time.realtimeSinceStartup;
            while (game.FixedCamCountForTest == 0 && Time.realtimeSinceStartup - t0 < timeoutSec)
                yield return null;

            Assert.Greater(game.FixedCamCountForTest, 0,
                $"ScreenGameplay 在 {timeoutSec}s 內沒有備妥固定鏡頭（CAMERA/* 載不進來？）");
            onReady(game);
        }

        /// <summary>砍掉 <see cref="Boot"/> 之後長出來的所有場景 root（gameplay 本體 + 它生的 board/avatar/scene）。</summary>
        public static IEnumerator Teardown()
        {
            if (_preRoots == null) yield break;
            foreach (var go in SceneManager.GetActiveScene().GetRootGameObjects())
                if (!_preRoots.Contains(go)) UnityEngine.Object.Destroy(go);
            _preRoots = null;
            Time.timeScale = 1f;
            yield return null;   // Destroy 是延到幀尾才生效 → 讓下一個測試看到的是乾淨場景
        }

        private static void DestroyNow(string name)
        {
            var go = GameObject.Find(name);
            if (go != null) UnityEngine.Object.DestroyImmediate(go);
        }
    }
}
