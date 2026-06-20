using System;

namespace Sdo.UI.Core
{
    /// <summary>Hooks set by FrontendApp so screens can open overlays without referencing it directly.</summary>
    public static class Nav
    {
        public static Action OpenSettings;
        public static Action OpenNoteSkinPicker;
        public static Action StartGame;   // host pressed Start -> hand off to gameplay (Step1Game) with the session selection
    }
}
