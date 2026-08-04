using System.IO;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Where the cloth-validation harness reads its trigger file and writes its recordings
    /// (<c>tools/mmd_cloth_validate/</c> — <c>compare.py</c> reads the same folder).
    ///
    /// This exists because both probes (<see cref="MmdPhysicsProbe"/> in-game, <c>MmdClothProbe</c> in PlayMode) were
    /// written inside the <c>feat/mmd-avatar</c> worktree and hardcoded its absolute path. That path stopped being the
    /// right one the moment the branch merged, and a hardcoded drive letter never survives a second checkout anyway —
    /// so derive it: in the editor from the project (which IS in the repo), in a built player from beside the exe
    /// (there is no repo there, and dropping the recordings next to the game is the only thing that can work).
    /// </summary>
    public static class MmdProbePaths
    {
        private const string HarnessRel = "tools/mmd_cloth_validate";

        /// <summary>The harness folder for this checkout / this build. Never null (falls back to the working dir).</summary>
        public static string HarnessDir
        {
            get
            {
                if (Application.isEditor)
                {
                    // <repo>/65/My project/Assets → up 3 = <repo>
                    var repo = Directory.GetParent(Application.dataPath)?.Parent?.Parent;
                    if (repo != null) return Path.Combine(repo.FullName, HarnessRel.Replace('/', Path.DirectorySeparatorChar));
                }
                // Built player: <exe>_Data → the folder holding the exe.
                var beside = Directory.GetParent(Application.dataPath);
                return beside != null ? Path.Combine(beside.FullName, "mmd_cloth_validate") : "mmd_cloth_validate";
            }
        }
    }
}
