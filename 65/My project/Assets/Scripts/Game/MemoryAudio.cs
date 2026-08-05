using System;
using Sdo.Osu;
using Sdo.Settings.Vfs;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 「一條路徑 → <see cref="AudioClip"/>」的唯一入口,**不需要檔案真的存在於磁碟上**。
    ///
    /// 為什麼要它:<c>UnityWebRequestMultimedia</c> 只吃 <c>file://</c>,所以 DATA 打包成 pak 之後
    /// 官方歌與音效都播不出來(而且是**靜默無聲、不報錯**那種壞法)。這裡把位元組從 VFS 拿出來,
    /// 用三顆自己的解碼器轉成 PCM,再 <c>AudioClip.Create</c>。
    ///
    /// <list type="bullet">
    /// <item>ogg → <see cref="VorbisDecoder"/>(sdovorbis.dll / stb_vorbis)</item>
    /// <item>mp3 → <see cref="MadDecoder"/>(sdomad.dll / libmad,與 StepMania 逐位相同)</item>
    /// <item>wav → <see cref="WavDecoder"/>(自己 parse)</item>
    /// </list>
    ///
    /// <para>🔴 <b>格式看內容不看副檔名</b> —— 外面撿來的歌曲庫常有名不符實的檔([NX] 那包就有 4 個
    /// Ogg 取名叫 .mp3)。餵錯解碼器不會報錯,只會解出 0 個取樣 → 整首沒聲音。見
    /// <see cref="AudioFileType"/>。</para>
    ///
    /// <para><b>不負責對拍。</b> 遊玩用的音訊有一整套 gapless/priming 的修正(<see cref="Mp3Decoder"/>),
    /// 那條路仍然自己走。這裡是給「載進來就播」的場合用的:UI 音效、環境音、大廳 BGM、選歌試聽。</para>
    /// </summary>
    public static class MemoryAudio
    {
        /// <summary>把 <paramref name="path"/> 的音訊載成 AudioClip;失敗 → null。
        ///
        /// <paramref name="path"/> 是 VFS 路徑(絕對或相對 DATA 根都行)。散裝時讀真實檔案,
        /// 打包後從 pak 讀 —— 呼叫端不必知道差別。</summary>
        public static AudioClip Load(string path, string clipName = null, bool stream = false)
        {
            var bytes = VfsFile.ReadAllBytes(path);
            if (bytes == null || bytes.Length == 0) return null;
            return FromBytes(bytes, clipName ?? System.IO.Path.GetFileNameWithoutExtension(path) ?? "clip", stream);
        }

        /// <summary>位元組 → AudioClip;解不出來 → null。</summary>
        public static AudioClip FromBytes(byte[] bytes, string clipName, bool stream = false)
        {
            var pcm = Decode(bytes);
            if (pcm == null || pcm.Samples == null || pcm.Samples.Length == 0) return null;
            return ToClip(pcm, clipName, stream);
        }

        /// <summary>位元組 → PCM。格式**看內容**判斷,不看副檔名。</summary>
        public static Mp3Pcm Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 12) return null;

            switch (AudioFileType.Sniff(bytes))
            {
                case AudioKind.Ogg:
                    return VorbisDecoder.Decode(bytes);
                case AudioKind.Wav:
                    return WavDecoder.Decode(bytes);
                case AudioKind.Mp3:
                    return MadDecoder.Decode(bytes, out _, out _);
                default:
                    return null;
            }
        }

        /// <summary>交錯 float PCM → AudioClip。</summary>
        public static AudioClip ToClip(Mp3Pcm pcm, string clipName, bool stream = false)
        {
            if (pcm == null || pcm.Samples == null || pcm.Channels <= 0 || pcm.SampleRate <= 0) return null;

            int perChannel = pcm.Samples.Length / pcm.Channels;
            if (perChannel <= 0) return null;

            // stream:false —— 資料已經全在記憶體裡了，再叫 Unity 用 PCMReaderCallback 拉一次沒有意義。
            var clip = AudioClip.Create(clipName ?? "clip", perChannel, pcm.Channels, pcm.SampleRate, false);
            if (clip == null) return null;
            clip.SetData(pcm.Samples, 0);
            return clip;
        }
    }
}
