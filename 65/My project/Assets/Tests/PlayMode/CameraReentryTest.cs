using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Sdo.Tests
{
    /// <summary>
    /// Verifies the camera fix: F2 cycles AUTO(-1) -> fixed 0..n-1 -> AUTO, and returning to AUTO must
    /// RESUME the current director shot — never rewind _dirShot to 0 (the intro crane "fly in from outside").
    /// Mirrors decompiled CameraSeq_SetPlaying(0) -> AdvanceA (021_gameplay cmd 0x3c).
    /// </summary>
    public class CameraReentryTest
    {
        [UnityTearDown]
        public IEnumerator TearDown() => GameplayBoot.Teardown();

        [UnityTest]
        public IEnumerator Reentry_To_Auto_Resumes_Current_Shot_Not_Zero()
        {
            Sdo.Game.ScreenGameplay game = null;
            yield return GameplayBoot.Boot(g => game = g);   // 前端接管開機後 gameplay 不再自我 boot — 見 GameplayBoot

            Assert.AreEqual(-1, game.CamModeForTest, "should start in AUTO director mode");

            // CASE A — the bug: frozen on shot 0 (the intro crane) when the player toured fixed cams.
            // Decompiled CameraSeq_SetPlaying(0) advances shot 0 -> shot 1, so re-entry must NOT replay the crane.
            game.DirShotForTest = 0;
            CycleBackToAuto(game);
            Assert.AreEqual(1, game.DirShotForTest,
                "re-entry from shot 0 must ADVANCE to shot 1 (never replay the intro crane / fly-in from outside)");

            // CASE B — frozen on a later shot N>0: re-entry replays that same shot, not 0.
            game.DirShotForTest = 3;
            CycleBackToAuto(game);
            Assert.AreEqual(3, game.DirShotForTest,
                "re-entry from shot 3 must RESUME shot 3, not restart at shot 0");
        }

        // F2 from AUTO all the way around the fixed cams and back to AUTO.
        private static void CycleBackToAuto(Sdo.Game.ScreenGameplay game)
        {
            game.CycleCamModeForTest();                       // AUTO(-1) -> fixed 0
            Assert.AreEqual(0, game.CamModeForTest, "first F2 must leave AUTO for fixed cam 0");
            int guard = 0;
            while (game.CamModeForTest != -1 && guard++ < 32)
                game.CycleCamModeForTest();                   // ... -> fixed n-1 -> AUTO
            Assert.AreEqual(-1, game.CamModeForTest, "cycling past the last fixed cam returns to AUTO");
        }
    }
}
