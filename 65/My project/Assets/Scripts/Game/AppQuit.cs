using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Instant, reliable application exit. Unity's normal shutdown — whether from Application.Quit, the logout
    /// button, Alt+F4, the window close button, or an OS sign-out — stalls for several seconds on PCs with many HID
    /// devices: the new Input System churns device (re)registration while shutting down, so the borderless-fullscreen
    /// frame stays frozen until Windows shows "not responding" and the user force-closes it. Hard-killing the process
    /// skips that stall entirely. Safe here: settings persist on change (Sdo.Settings) and there is no unsaved state.
    /// </summary>
    public static class AppQuit
    {
#if !UNITY_EDITOR
        // Intercept EVERY quit path (not just the in-app logout button): Alt+F4, the window close button, OS sign-out,
        // and any Application.Quit. wantsToQuit fires BEFORE the hanging shutdown begins, so we terminate first.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install() => Application.wantsToQuit += () => { Kill(); return true; };

        private static void Kill() => System.Diagnostics.Process.GetCurrentProcess().Kill();
#endif

        /// <summary>Exit now. In the editor this stops Play mode; in a build it terminates the process immediately.</summary>
        public static void Now()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Kill();
#endif
        }
    }
}
