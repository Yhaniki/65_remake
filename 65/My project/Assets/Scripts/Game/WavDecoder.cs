using System;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 從**記憶體**把 RIFF/WAVE 解成交錯 float PCM。
    ///
    /// 存在的理由跟 <see cref="VorbisDecoder"/> 一樣:<c>UnityWebRequestMultimedia</c> 只吃 <c>file://</c>,
    /// 而 DATA 打包成 pak 之後 <c>SE/*.wav</c> 沒有實體檔案。wav 不像 ogg 需要外部解碼器 ——
    /// 它就是表頭加原始取樣,自己 parse 幾十行就好,不必為它多拉一顆 DLL。
    ///
    /// 支援官方 SE 用得到的格式:PCM 8/16/24/32-bit 整數 與 IEEE float 32-bit。
    /// 壓縮過的 wav(ADPCM 之類)回 null —— 官方那批沒有,真的遇到就讓呼叫端退回 <c>file://</c>。
    /// 純 CPU、不碰 Unity API,可以在背景執行緒跑。
    /// </summary>
    public static class WavDecoder
    {
        private const ushort FormatPcm = 1;
        private const ushort FormatFloat = 3;
        private const ushort FormatExtensible = 0xFFFE;

        /// <summary>解一整個 wav;不是合法的 RIFF/WAVE 或格式不支援 → null。</summary>
        public static Mp3Pcm Decode(byte[] wav)
        {
            if (wav == null || wav.Length < 44) return null;
            if (!Tag(wav, 0, 'R', 'I', 'F', 'F') || !Tag(wav, 8, 'W', 'A', 'V', 'E')) return null;

            ushort format = 0, channels = 0, bits = 0;
            int rate = 0, dataAt = -1, dataLen = 0;

            // chunk 走訪:fmt 與 data 的順序不保證，中間也可能夾 LIST/fact 之類的東西。
            int pos = 12;
            while (pos + 8 <= wav.Length)
            {
                int size = ReadI32(wav, pos + 4);
                if (size < 0 || pos + 8 + (long)size > wav.Length) size = wav.Length - pos - 8;   // 表頭寫壞 → 收到檔尾
                int body = pos + 8;

                if (Tag(wav, pos, 'f', 'm', 't', ' ') && size >= 16)
                {
                    format = ReadU16(wav, body);
                    channels = ReadU16(wav, body + 2);
                    rate = ReadI32(wav, body + 4);
                    bits = ReadU16(wav, body + 14);
                    // WAVE_FORMAT_EXTENSIBLE：真正的格式藏在 SubFormat 的前兩個 byte。
                    if (format == FormatExtensible && size >= 40) format = ReadU16(wav, body + 24);
                }
                else if (Tag(wav, pos, 'd', 'a', 't', 'a'))
                {
                    dataAt = body;
                    dataLen = size;
                }

                pos = body + size + (size & 1);   // chunk 都對齊到偶數位元組
            }

            if (dataAt < 0 || channels == 0 || rate <= 0) return null;
            if (format != FormatPcm && format != FormatFloat) return null;   // 壓縮過的 → 交給呼叫端退回

            int bytesPerSample = bits / 8;
            if (bytesPerSample <= 0) return null;
            int count = dataLen / bytesPerSample;
            if (count <= 0) return null;

            var data = new float[count];
            try
            {
                if (format == FormatFloat && bits == 32)
                {
                    for (int i = 0; i < count; i++) data[i] = BitConverter.ToSingle(wav, dataAt + i * 4);
                }
                else if (bits == 16)
                {
                    for (int i = 0; i < count; i++) data[i] = (short)(wav[dataAt + i * 2] | (wav[dataAt + i * 2 + 1] << 8)) / 32768f;
                }
                else if (bits == 24)
                {
                    for (int i = 0; i < count; i++)
                    {
                        int o = dataAt + i * 3;
                        int v = (wav[o] << 8) | (wav[o + 1] << 16) | (wav[o + 2] << 24);   // 左移到高位再算術右移 = 正確的 sign extend
                        data[i] = (v >> 8) / 8388608f;
                    }
                }
                else if (bits == 32)
                {
                    for (int i = 0; i < count; i++) data[i] = ReadI32(wav, dataAt + i * 4) / 2147483648f;
                }
                else if (bits == 8)
                {
                    // 8-bit wav 是**無號**的（128 = 靜音），跟其它位寬不一樣。
                    for (int i = 0; i < count; i++) data[i] = (wav[dataAt + i] - 128) / 128f;
                }
                else return null;
            }
            catch { return null; }

            return new Mp3Pcm { Samples = data, Channels = channels, SampleRate = rate };
        }

        private static bool Tag(byte[] b, int at, char a, char c, char d, char e)
        {
            return at + 4 <= b.Length && b[at] == a && b[at + 1] == c && b[at + 2] == d && b[at + 3] == e;
        }

        private static ushort ReadU16(byte[] b, int at) { return (ushort)(b[at] | (b[at + 1] << 8)); }

        private static int ReadI32(byte[] b, int at)
        {
            return b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24);
        }
    }
}
