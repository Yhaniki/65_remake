using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using MagicaCloth2;

namespace Sdo.Tests
{
    /// <summary>
    /// A/B diagnostic for the headless cloth-probe freeze: a MINIMAL vanilla BoneCloth (3-bone chain, unit scale, all
    /// Magica defaults, nothing from our MMD conversion) must sag under default gravity within 2 s. If even this stays
    /// frozen in batchmode, the freeze is ENVIRONMENTAL (batch player-loop / Burst / jobs); if this moves while the MMD
    /// probe stays frozen, the freeze is caused by OUR conversion's settings. Logs [probe-diag] lines either way.
    /// </summary>
    public class MmdClothDiagTest
    {
        [UnityTest]
        public IEnumerator VanillaBoneCloth_SagsUnderGravity()
        {
            LogAssert.ignoreFailingMessages = true;
            // NOTE: deliberately NOT setting Time.captureDeltaTime — it is the one factor common to every frozen run
            // (UTF batch, UTF GUI, real game probe all froze WITH it; the user's real game runs WITHOUT it and works).
            MagicaManager.InitCustomGameLoop();

            // 3-bone horizontal chain at unit scale: root fixed, the rest must fall under default gravity.
            var root = new GameObject("DiagRoot").transform;
            var b0 = new GameObject("d0").transform; b0.SetParent(root, false); b0.localPosition = Vector3.up;   // hangs from y=1
            var b1 = new GameObject("d1").transform; b1.SetParent(b0, false); b1.localPosition = Vector3.right * 0.3f;
            var b2 = new GameObject("d2").transform; b2.SetParent(b1, false); b2.localPosition = Vector3.right * 0.3f;

            var go = new GameObject("DiagCloth");
            go.transform.SetParent(root, false);
            var cloth = go.AddComponent<MagicaCloth>();
            var sd = cloth.SerializeData;
            sd.clothType = ClothProcess.ClothType.BoneCloth;
            sd.rootBones.Add(b0);
            cloth.BuildAndRun();

            int build = 0;
            while (build < 600 && !cloth.Process.IsRunning()) { build++; yield return null; }
            Debug.Log($"[probe-diag] vanilla build frames={build} IsRunning={cloth.Process.IsRunning()} enabled={cloth.enabled}");
            Assert.Less(build, 600, "vanilla cloth never started running");

            // Split the freeze: IsPlaying gate? team registration? sim dispatched but output dead?
            int preSim = 0;
            MagicaManager.OnPreSimulation += CountPreSim;
            void CountPreSim() => preSim++;

            Vector3 tip0 = b2.position;
            for (int f = 0; f < 120; f++)
            {
                yield return null;
                if (f % 30 == 29)
                    Debug.Log($"[probe-diag] f={f + 1} tip={b2.position:F4} moved={(b2.position - tip0).magnitude:F5} dt={Time.deltaTime:F4} " +
                              $"IsPlaying={MagicaManager.IsPlaying()} teams={MagicaManager.Team?.TrueTeamCount ?? -1} active={MagicaManager.Team?.ActiveTeamCount ?? -1} preSim={preSim}");
                if (f == 60)   // full internal dump: team time/updateCount/flags + time manager state
                {
                    var sb = new System.Text.StringBuilder();
                    MagicaManager.Time?.InformationLog(sb);
                    MagicaManager.Team?.InformationLog(sb);
                    Debug.Log("[probe-dump]\n" + sb);
                }
            }
            MagicaManager.OnPreSimulation -= CountPreSim;
            float moved = (b2.position - tip0).magnitude;
            Debug.Log($"[probe-diag] VERDICT vanilla moved={moved:F5} over 2s (expect >0.1 if simulating)");

            Time.captureDeltaTime = 0f;
            Object.Destroy(root.gameObject);
            // Assert on movement LAST so the verdict logs even on failure.
            Assert.Greater(moved, 0.05f, "vanilla default BoneCloth did not move headless -> ENVIRONMENTAL freeze (batchmode)");
            yield return null;
        }
    }
}
