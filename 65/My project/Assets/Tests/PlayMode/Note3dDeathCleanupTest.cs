using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Sdo.Tests
{
    /// <summary>
    /// 3D note skin：血條用完死掉之後，**畫面上不可以留下任何 3D 音符/命中特效**。
    ///
    /// 使用者回報「3d note 血用完 死掉了殘留在畫面上」。兩個根因都只發生在 3D 皮：
    ///   ① <c>Note3dHighway</c> 的箭頭是 mesh 不是 SpriteRenderer，`SetTrackVisible(false)` 收不到它；而
    ///      `EnterResult` 之後 `Update` 直接 return、不再呼叫 `ScrollNotes`，最後一幀那批箭頭就留在原地。
    ///   ② `HIT_LONG.EFT` 是自我循環的（負 life 的 emitter 永不死），本來只靠放開鍵時 `StopHit3dLong` 收掉；
    ///      按著長條時死掉就沒人收，那團金光會一直燒在受擊線上。
    ///
    /// 測法不碰任何私有欄位：關掉 autoPlay 又不給輸入 → 全 miss → HP 見底 → GAME OVER 字幕出現，
    /// 這時場上不該再找得到 <c>Note3dMeshes_root</c>（<see cref="GameObject.Find"/> 只找 active 物件）或 <c>Hit3d*</c>。
    /// </summary>
    public class Note3dDeathCleanupTest
    {
        const int W = 800, H = 600;

        [UnityTearDown]
        public IEnumerator TearDown() => GameplayBoot.Teardown();

        [UnityTest]
        public IEnumerator Note3dVisuals_AreGone_AfterHpOutDeath()
        {
            Sdo.Game.ScreenGameplay game = null;
            yield return GameplayBoot.Boot(g => game = g, g =>
            {
                g.roomNoteType = 11;    // 3D hiteft3D skin (index == NoteTypeEftSuffix.Length)
                g.autoPlay = false;     // 不打 → 全 miss → 血條見底（-50/miss，1000→-150 = 23 個 miss）
                g.playFullSong = false; // 完奏模式會不死
            });
            game.SetCamModeForTest(0);

            // 等 GAME OVER 字幕亮起來（死亡字幕在結算面板接手前會顯示一段）。
            float t0 = Time.realtimeSinceStartup;
            bool died = false;
            while (!died && Time.realtimeSinceStartup - t0 < 120f)
            {
                var go = GameObject.Find("GameOverText");
                var sr = go != null ? go.GetComponent<SpriteRenderer>() : null;
                died = sr != null && sr.enabled;
                if (!died) yield return null;
            }
            Assert.IsTrue(died, "120s 內沒有死亡 —— 測試前提壞了（autoPlay 沒關掉？血量公式改了？）");

            Cap("H:/65_remake/note3d-death-cleanup.png");   // 供人眼複核

            Assert.IsNull(GameObject.Find("Note3dMeshes_root"),
                "死掉之後 3D 音符的 mesh pool 還留在畫面上（Note3dMeshes_root 仍是 active）");
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
                Assert.IsFalse(t.gameObject.activeInHierarchy && t.name.StartsWith("Hit3d"),
                    $"死掉之後 3D 命中特效還在跑：{t.name}（HIT_LONG 會自我循環，必須明確砍掉）");
        }

        static void Cap(string path)
        {
            var main = Camera.main; if (main == null) return;
            var rt = new RenderTexture(W, H, 24);
            foreach (var c in Camera.allCameras) if (c != main && c.targetTexture != null) c.Render();
            main.targetTexture = rt; main.Render(); main.targetTexture = null;
            RenderTexture.active = rt;
            var t = new Texture2D(W, H, TextureFormat.RGBA32, false);
            t.ReadPixels(new Rect(0, 0, W, H), 0, 0); t.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes(path, t.EncodeToPNG());
            Object.Destroy(t); Object.Destroy(rt);
        }
    }
}
