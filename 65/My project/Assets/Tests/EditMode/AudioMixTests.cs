using NUnit.Framework;
using Sdo.Game;

namespace Sdo.Tests
{
    /// <summary>
    /// OPTION 音效頁三個滑桿 → 實際交給 AudioSource 的增益。
    /// 重點:滑桿是「感知刻度」不是振幅 —— 中點必須明顯比一半還小聲(官方 dB 音量的行為),
    /// 但兩端要維持全靜音 / 原音,否則「MIN 還聽得到」或「MAX 被壓掉」。
    /// </summary>
    public class AudioMixTests
    {
        private const float Eps = 1e-4f;

        [Test]
        public void Gain_EndsAreExact()
        {
            Assert.AreEqual(0f, AudioMix.Gain(0f), Eps);   // MIN = 真的靜音
            Assert.AreEqual(1f, AudioMix.Gain(1f), Eps);   // MAX = 原音,不衰減
        }

        [Test]
        public void Gain_MidpointIsQuieterThanLinear()
        {
            // 平方律:0.5 → 0.25 振幅(≈ −12 dB)。舊行為(直接用 0.5 = −6 dB)才是「50% 還是超大聲」的原因。
            float mid = AudioMix.Gain(0.5f);
            Assert.Less(mid, 0.5f);
            Assert.AreEqual(0.25f, mid, 0.02f);
        }

        [Test]
        public void Gain_IsMonotonicAndClamped()
        {
            float prev = -1f;
            for (int i = 0; i <= 10; i++)
            {
                float g = AudioMix.Gain(i / 10f);
                Assert.Greater(g, prev);
                prev = g;
            }
            Assert.AreEqual(0f, AudioMix.Gain(-3f), Eps);   // 超出範圍先 clamp
            Assert.AreEqual(1f, AudioMix.Gain(9f), Eps);
        }

        [Test]
        public void Set_StoresSliderValuesAndExposesCurvedGain()
        {
            AudioMix.Set(0.5f, 1f, 0f);
            Assert.AreEqual(0.5f, AudioMix.BgmSlider, Eps);       // 存檔/UI 拿到的仍是滑桿刻度
            Assert.AreEqual(AudioMix.Gain(0.5f), AudioMix.Bgm, Eps);   // 播放端拿到的是曲線後的增益
            Assert.AreEqual(1f, AudioMix.Music, Eps);
            Assert.AreEqual(0f, AudioMix.Sfx, Eps);
            AudioMix.Set(0.5f, 0.5f, 0.5f);   // 回到預設,避免污染其它測試
        }

        [Test]
        public void Set_ClampsOutOfRangeSliders()
        {
            AudioMix.Set(-1f, 2f, 0.25f);
            Assert.AreEqual(0f, AudioMix.BgmSlider, Eps);
            Assert.AreEqual(1f, AudioMix.MusicSlider, Eps);
            Assert.AreEqual(0.25f, AudioMix.SfxSlider, Eps);
            AudioMix.Set(0.5f, 0.5f, 0.5f);
        }

        [Test]
        public void Set_FiresChanged()
        {
            int hits = 0;
            System.Action h = () => hits++;
            AudioMix.Changed += h;
            try { AudioMix.Set(0.3f, 0.3f, 0.3f); }
            finally { AudioMix.Changed -= h; AudioMix.Set(0.5f, 0.5f, 0.5f); }
            Assert.AreEqual(1, hits);
        }

        [Test]
        public void SceneSfx_IsAlwaysHalfOfGameplaySfx()
        {
            try
            {
                AudioMix.Set(0.5f, 0.5f, 1f);
                Assert.AreEqual(1f, AudioMix.Sfx, Eps);
                Assert.AreEqual(0.5f, AudioMix.SceneSfx, Eps);
                AudioMix.Set(0.5f, 0.5f, 0.5f);
                Assert.AreEqual(0.25f, AudioMix.Sfx, Eps);
                Assert.AreEqual(0.125f, AudioMix.SceneSfx, Eps);
                AudioMix.Set(0.5f, 0.5f, 0f);
                Assert.AreEqual(0f, AudioMix.SceneSfx, Eps);
            }
            finally
            {
                AudioMix.Set(0.5f, 0.5f, 0.5f);
            }
        }
    }
}
