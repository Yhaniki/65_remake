using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 從**記憶體**把 ogg 解成交錯 float PCM(<c>Assets/Plugins/x86_64/sdovorbis.dll</c>,stb_vorbis 包裝)。
    ///
    /// 存在的理由:Unity 沒有記憶體 ogg 解碼器 —— <c>UnityWebRequestMultimedia</c> 只吃 <c>file://</c>。
    /// DATA 打包成 pak 之後官方歌沒有實體檔案,沒有這顆就得先解出來落地才播得了(慢,而且會在
    /// CACHE 留下明碼音訊)。有了它就能直接把 pak 裡的位元組交給 <c>AudioClip</c>。
    ///
    /// <para>🔴 <b>為什麼 ogg 可以換解碼器而 mp3 不行</b>:mp3 沒有精確的樣本位置 —— 編碼器延遲要靠
    /// Xing/LAME 表頭猜、壞幀處理各家不同,所以 <see cref="MadDecoder"/> 才要逐條照抄 StepMania。
    /// Vorbis 不一樣:格式本身帶 granule position,天生 gapless,任何合規解碼器輸出的 PCM 都一致。
    /// StepMania 的 <c>RageSoundReader_Vorbisfile.cpp</c> 也沒有任何偏移補償(只用 <c>ov_pcm_tell</c>
    /// 追位置)—— 那就是證據。所以換成 stb_vorbis 不會動到對拍。</para>
    ///
    /// 純 CPU、不碰 Unity API,可以在背景執行緒跑。DLL 載不到 → <see cref="Available"/> 為 false,
    /// 呼叫端要退回原本的 <c>file://</c> 路徑。
    /// </summary>
    public static class VorbisDecoder
    {
        private const string Dll = "sdovorbis";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SdoVorbisDecode(byte[] ogg, int oggLen,
            out int outSamples, out int outChannels, out int outSampleRate);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SdoVorbisFree(IntPtr p);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SdoVorbisVersion();

        private static int _available = -1;   // -1 未測 / 0 不可用 / 1 可用

        /// <summary>DLL 在不在(第一次呼叫會實際 P/Invoke 一次探針)。</summary>
        public static bool Available
        {
            get
            {
                if (_available < 0)
                {
                    try { _available = SdoVorbisVersion() > 0 ? 1 : 0; }
                    catch (DllNotFoundException) { _available = 0; }
                    catch (EntryPointNotFoundException) { _available = 0; }
                    catch (Exception) { _available = 0; }
                    if (_available == 0)
                        Debug.LogWarning("[ogg] sdovorbis.dll 載不到 → ogg 只能走 file://(pak 內的歌會播不出來)");
                }
                return _available == 1;
            }
        }

        /// <summary>解一整個 ogg 成交錯 float PCM。回 null = 這條路走不通(DLL 不在或檔案解不開)。</summary>
        public static Mp3Pcm Decode(byte[] ogg)
        {
            if (ogg == null || ogg.Length == 0 || !Available) return null;

            IntPtr p = IntPtr.Zero;
            try
            {
                p = SdoVorbisDecode(ogg, ogg.Length, out int samples, out int channels, out int rate);
                if (p == IntPtr.Zero || samples <= 0 || channels <= 0 || rate <= 0) return null;

                var data = new float[samples];
                Marshal.Copy(p, data, 0, samples);
                return new Mp3Pcm { Samples = data, Channels = channels, SampleRate = rate };
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ogg] sdovorbis 解碼失敗: " + e.Message);
                return null;
            }
            finally
            {
                if (p != IntPtr.Zero) SdoVorbisFree(p);
            }
        }
    }
}
