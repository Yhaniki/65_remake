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
    ///
    /// SLIDER 位置 ≠ AudioSource.volume：<see cref="AudioSource.volume"/> 是**線性振幅**，滑桿擺中間(0.5)只有 −6 dB，
    /// 人耳仍聽成八成音量 → 「50% 怎麼還是超大聲」。官方的 MIN…MAX 走 DirectSound/FMOD 的 dB 音量，中點大約 −15 dB。
    /// 所以這裡把滑桿值過一條感知曲線 <see cref="Gain"/> 再交給引擎：0 仍是全靜音、1 仍是原音，中間才變得像音量鈕。
    /// </summary>
    public static class AudioMix
    {
        /// <summary>感知曲線指數(平方律)。0.5 → 0.25 振幅 ≈ −12 dB。想更接近官方的 −15 dB 就調到 2.5f；
        /// 想回到「滑桿=振幅」的舊行為設 1f。</summary>
        public const float CurveExponent = 2f;
        public const float SceneSfxRelativeGain = 0.5f;

        private static float _bgm = 0.5f, _music = 0.5f, _sfx = 0.5f;   // 滑桿原始值 (0..1)，UI/存檔用的就是這個

        /// <summary>滑桿值 → 實際交給 AudioSource 的線性增益。</summary>
        public static float Gain(float slider)
        {
            float v = Mathf.Clamp01(slider);
            return v <= 0f ? 0f : Mathf.Pow(v, CurveExponent);
        }

        // 播放端讀的是「已套曲線」的增益；三個 Slider 值本身另外從 SliderXxx 取(目前沒有呼叫端需要)。
        public static float Bgm => Gain(_bgm);
        public static float Music => Gain(_music);
        public static float Sfx => Gain(_sfx);
        public static float SceneSfx => Sfx * SceneSfxRelativeGain;

        public static float BgmSlider => _bgm;
        public static float MusicSlider => _music;
        public static float SfxSlider => _sfx;

        /// <summary>Fired after any volume changes so continuous sources can re-read their level live.</summary>
        public static event Action Changed;

        /// <summary>三個「滑桿值」(0..1，OPTION 音效頁/設定檔存的就是這個尺度)。</summary>
        public static void Set(float bgm, float music, float sfx)
        {
            _bgm = Mathf.Clamp01(bgm);
            _music = Mathf.Clamp01(music);
            _sfx = Mathf.Clamp01(sfx);
            Changed?.Invoke();
        }
    }
}
