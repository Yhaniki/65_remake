using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// mp3 解碼 —— 走 <b>libmad</b>(Assets/Plugins/x86_64/sdomad.dll),也就是 <b>StepMania 用的同一個解碼器</b>。
    ///
    /// 為什麼非它不可:外部歌的音訊要跟譜面對得起來,唯一可靠的辦法是用譜面作者當初校 offset 時用的解碼器。
    /// NLayer(純 C# 的備援)在兩件事上跟 libmad 不一樣,而且都會直接變成聽得出來的偏移:
    ///   1. <b>壞幀不丟</b>:libmad 對 Huffman 資料壞掉的幀回 MAD_ERROR_BADHUFFDATA,StepMania 的
    ///      <c>do_mad_frame_decode</c> 對這種 recoverable error 是 <c>continue</c> —— 整幀丟棄、不佔時間軸。
    ///      NLayer 不報錯,照樣吐 2304 個垃圾樣本。lull~そして僕らは~ 的 OP.mp3 開頭有 25 個 Huffman 壞掉的
    ///      重複 padding 幀,libmad 丟掉它們 → 音樂落在 0.088s(與 StepMania 一致);NLayer 全留 →
    ///      音樂落在 0.71s,整整晚 0.62 秒。
    ///   2. <b>好幀不漏</b>:NLayer 的高階 MpegFile 會靜默跳過 bit reservoir 指不到資料的幀,跳一幀那之後
    ///      整首提前 26ms(engine[Blue] 37.5 秒起、Amanojaku 24 秒起)。libmad 對這種幀是
    ///      <c>MAD_ERROR_BADDATAPTR → pretend success</c>,照樣輸出一幀、時間軸一格不動。
    /// 附帶好處:libmad 比 NLayer 快 5–8 倍(engine 279ms vs 1410ms)。
    ///
    /// 原生層的解碼流程逐條照抄 <c>RageSoundReader_MP3::do_mad_frame_decode</c> —— 原始碼與建置腳本在
    /// <c>tools/sdomad/</c>。<b>libmad 是 GPL v2</b>(tools/sdomad/libmad-COPYING.txt)。
    ///
    /// DLL 載不到(非 Windows、缺檔、平台不符)時 <see cref="Decode"/> 回 null,呼叫端退回 NLayer。
    /// </summary>
    public static class MadDecoder
    {
        private const string Dll = "sdomad";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SdoMadDecode(byte[] mp3, int mp3Len,
            out int outSamples, out int outChannels, out int outSampleRate,
            out int outFrames, out int outSkipped, out int outPretend);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SdoMadFree(IntPtr p);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SdoMadVersion();

        private static int _available = -1;   // -1 未測 / 0 不可用 / 1 可用

        /// <summary>DLL 在不在(第一次呼叫會實際 P/Invoke 一次探針)。</summary>
        public static bool Available
        {
            get
            {
                if (_available < 0)
                {
                    try { _available = SdoMadVersion() > 0 ? 1 : 0; }
                    catch (DllNotFoundException) { _available = 0; }
                    catch (EntryPointNotFoundException) { _available = 0; }
                    catch (Exception) { _available = 0; }
                    if (_available == 0)
                        Debug.LogWarning("[mp3] sdomad.dll 載不到 → 退回 NLayer(外部歌可能有 ~26ms 級的偏移)");
                }
                return _available == 1;
            }
        }

        /// <summary>
        /// 解一整個 mp3 成交錯 float PCM。回 null = 這條路走不通(DLL 不在或檔案解不開),呼叫端要退回 NLayer。
        /// 純 CPU、不碰 Unity API,可以在背景執行緒跑。
        /// </summary>
        public static Mp3Pcm Decode(string path, out int droppedFrames, out int pretendFrames)
        {
            droppedFrames = 0; pretendFrames = 0;
            if (!Available || string.IsNullOrEmpty(path)) return null;
            byte[] bytes;
            try { bytes = System.IO.File.ReadAllBytes(path); }
            catch { return null; }
            return Decode(bytes, out droppedFrames, out pretendFrames, path);
        }

        /// <summary>從**記憶體**解 —— DATA 打包成 pak 之後歌沒有實體檔案,這是唯一走得通的路。
        ///
        /// 原生函式本來就吃 <c>const unsigned char *</c>,所以這只是把 <see cref="Decode(string,out int,out int)"/>
        /// 裡「先 ReadAllBytes」那一步讓給呼叫端 —— **解碼行為完全相同**,對拍不受影響。
        /// <paramref name="label"/> 只用於 log。</summary>
        public static Mp3Pcm Decode(byte[] bytes, out int droppedFrames, out int pretendFrames, string label = null)
        {
            droppedFrames = 0; pretendFrames = 0;
            if (!Available || bytes == null || bytes.Length <= 4) return null;
            string path = label ?? "(memory)";

            IntPtr p = IntPtr.Zero;
            try
            {
                p = SdoMadDecode(bytes, bytes.Length, out int n, out int ch, out int sr,
                                 out int frames, out int skipped, out int pretend);
                if (p == IntPtr.Zero || n <= 0 || ch <= 0 || sr <= 0) return null;
                var data = new float[n];
                Marshal.Copy(p, data, 0, n);
                droppedFrames = skipped; pretendFrames = pretend;
                return new Mp3Pcm { Samples = data, Channels = ch, SampleRate = sr };
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[mp3] libmad 解碼失敗({e.GetType().Name})→ 退回 NLayer:{path}");
                return null;
            }
            finally { if (p != IntPtr.Zero) SdoMadFree(p); }
        }
    }
}
