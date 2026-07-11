using System;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// Central runtime audio mix (0..1 per category), driven by the OPTION「音效」three sliders. A plain static holder so
    /// every audio source reads its category volume here with no assembly coupling — the gameplay (Sdo.Game) can't see
    /// Sdo.Settings, so the persisted <c>VolumeSettings</c> is pushed in via <see cref="Set"/> by the cross-layer callers
    /// (FrontendApp on boot, OptionDlgModal on slider-drag / 保存 / 默認 / 退出-revert).
    ///
    /// Categories:
    ///   • <see cref="Bgm"/>   背景音樂 → the lobby/room BgmPlayer.
    ///   • <see cref="Music"/> 遊戲音樂 → the gameplay song (sdomxxxx.ogg) + the song-select exper preview.
    ///   • <see cref="Sfx"/>   遊戲音效 → every SE/*.wav (UI clicks via UiSfx + gameplay SE + scene ambient).
    ///
    /// Continuous sources (BGM) subscribe to <see cref="Changed"/> for a live update while a slider is dragged; one-shot
    /// / freshly-started sources (SE, song, preview) just read the field at play time.
    /// </summary>
    public static class AudioMix
    {
        public static float Bgm = 0.5f;
        public static float Music = 0.5f;
        public static float Sfx = 0.5f;

        /// <summary>Fired after any volume changes so continuous sources can re-read their level live.</summary>
        public static event Action Changed;

        public static void Set(float bgm, float music, float sfx)
        {
            Bgm = Mathf.Clamp01(bgm);
            Music = Mathf.Clamp01(music);
            Sfx = Mathf.Clamp01(sfx);
            Changed?.Invoke();
        }
    }
}
