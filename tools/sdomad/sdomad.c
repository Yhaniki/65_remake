/* sdomad — libmad 的最小包裝,給 C# P/Invoke 用。
 *
 * 解碼流程與錯誤處理**逐條照抄** StepMania 的 RageSoundReader_MP3::do_mad_frame_decode
 * (assets/SM-YHANIKI-master/src/RageSoundReader_MP3.cpp),所以輸出的 PCM 與 StepMania
 * 逐位相同 —— 這正是重點:
 *   • MAD_ERROR_BADDATAPTR  → ret = 0「pretend success」,那一幀照樣 synth 輸出(內容是靜音/前一幀殘留)。
 *   • MAD_ERROR_BADCRC      → mad_frame_mute + mad_synth_mute + continue(整幀丟棄)。
 *   • 其他 MAD_RECOVERABLE  → continue(整幀丟棄,**不佔時間軸**)。
 *   • 不可回復的錯誤        → 停止。
 * 「其他 recoverable 整幀丟棄」就是 NLayer 做不到的那件事:lull~そして僕らは~ 的 OP.mp3 開頭有 23 個
 * Huffman 資料壞掉的重複 padding 幀,libmad 回 MAD_ERROR_BADHUFFDATA(0x0238)把它們丟掉,音樂
 * 落在 0.078s;NLayer 不報錯、照樣吐 2304 個垃圾樣本,時間軸就多了 0.6 秒的無聲。
 *
 * libmad 是 GPL v2 —— 見 ThirdParty/mad-0.15.1b/COPYING。
 */
#include <stdlib.h>
#include <string.h>
#include "mad.h"

#ifdef _WIN32
#define SDOMAD_API __declspec(dllexport)
#else
#define SDOMAD_API
#endif

/* 解一整個 mp3。成功回傳 malloc 出來的交錯 float 緩衝(呼叫端要用 SdoMadFree 釋放),失敗回 NULL。
 * outSamples = 交錯樣本總數;outSkipped = 被丟棄的壞幀數;outPretend = BADDATAPTR「假裝成功」的幀數。 */
SDOMAD_API float *SdoMadDecode(const unsigned char *mp3, int mp3Len,
                               int *outSamples, int *outChannels, int *outSampleRate,
                               int *outFrames, int *outSkipped, int *outPretend)
{
    if (outSamples) *outSamples = 0;
    if (outChannels) *outChannels = 0;
    if (outSampleRate) *outSampleRate = 0;
    if (outFrames) *outFrames = 0;
    if (outSkipped) *outSkipped = 0;
    if (outPretend) *outPretend = 0;
    if (!mp3 || mp3Len <= 4) return NULL;

    /* 跳過 ID3v2(syncsafe 28-bit)。StepMania 是在 MAD_ERROR_LOSTSYNC 時用 id3_tag_query 跳,
     * 一開始就跳掉等效,而且省掉一次重新同步。 */
    long off = 0;
    if (mp3Len > 10 && mp3[0] == 'I' && mp3[1] == 'D' && mp3[2] == '3')
    {
        off = 10 + (((mp3[6] & 0x7f) << 21) | ((mp3[7] & 0x7f) << 14) |
                    ((mp3[8] & 0x7f) << 7) | (mp3[9] & 0x7f));
        if (off < 0 || off >= mp3Len) off = 0;
    }

    struct mad_stream stream;
    struct mad_frame frame;
    struct mad_synth synth;
    mad_stream_init(&stream);
    mad_frame_init(&frame);
    mad_synth_init(&synth);
    mad_frame_mute(&frame);     /* 第一幀若解不出來,輸出的就是這份靜音(和 MAD 一樣) */
    mad_synth_mute(&synth);
    mad_stream_buffer(&stream, mp3 + off, (unsigned long)(mp3Len - off));

    int cap = 1 << 20;
    float *out = (float *)malloc(sizeof(float) * cap);
    if (!out) goto fail;

    int len = 0, frames = 0, skipped = 0, pretend = 0, channels = 0, rate = 0;
    for (;;)
    {
        int ret = mad_frame_decode(&frame, &stream);
        if (ret == -1)
        {
            if (stream.error == MAD_ERROR_BUFLEN || stream.error == MAD_ERROR_BUFPTR)
                break;                                  /* 資料用完 = EOF */
            if (stream.error == MAD_ERROR_BADDATAPTR)
            {
                ret = 0;                                /* pretend success —— 這一幀照樣輸出 */
                pretend++;
            }
            else if (stream.error == MAD_ERROR_BADCRC)
            {
                mad_frame_mute(&frame);
                mad_synth_mute(&synth);
                skipped++;
                continue;                               /* 整幀丟棄 */
            }
            else if (MAD_RECOVERABLE(stream.error))
            {
                skipped++;
                continue;                               /* 整幀丟棄(BADHUFFDATA 走這裡)*/
            }
            else break;                                 /* 不可回復 */
        }

        mad_synth_frame(&synth, &frame);
        int nch = MAD_NCHANNELS(&frame.header);
        if (!channels) { channels = nch; rate = frame.header.samplerate; }
        if (nch != channels) continue;                  /* 聲道數變了 → 跳過(同 synth_output)*/

        int need = len + synth.pcm.length * nch;
        if (need > cap)
        {
            while (cap < need) cap <<= 1;
            float *bigger = (float *)realloc(out, sizeof(float) * cap);
            if (!bigger) goto fail;
            out = bigger;
        }
        for (unsigned int i = 0; i < synth.pcm.length; i++)
            for (int c = 0; c < nch; c++)
                out[len++] = (float)mad_f_todouble(synth.pcm.samples[c][i]);
        frames++;
    }

    mad_synth_finish(&synth);
    mad_frame_finish(&frame);
    mad_stream_finish(&stream);

    if (len == 0 || channels == 0) { free(out); return NULL; }
    if (outSamples) *outSamples = len;
    if (outChannels) *outChannels = channels;
    if (outSampleRate) *outSampleRate = rate;
    if (outFrames) *outFrames = frames;
    if (outSkipped) *outSkipped = skipped;
    if (outPretend) *outPretend = pretend;
    return out;

fail:
    mad_synth_finish(&synth);
    mad_frame_finish(&frame);
    mad_stream_finish(&stream);
    free(out);
    return NULL;
}

SDOMAD_API void SdoMadFree(float *p) { free(p); }

/* 版本探針 —— C# 端用它確認 DLL 真的載到了。 */
SDOMAD_API int SdoMadVersion(void) { return 1; }
