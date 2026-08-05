/* sdovorbis — stb_vorbis 的最小包裝,給 C# P/Invoke 用。
 *
 * 為什麼要它:Unity 沒有「從記憶體解 ogg」的路 —— UnityWebRequestMultimedia 只吃 file://。
 * DATA 打包成 pak 之後官方歌(.ogg)沒有實體檔案,得先解出來落地才播得了,那既慢又會在
 * CACHE 裡留下明碼音訊。有了這顆就能直接把 pak 裡的位元組解成 PCM 交給 AudioClip。
 * 形狀刻意跟 tools/sdomad 一致(那是 mp3 那顆)。
 *
 * 🔴 為什麼 ogg 可以隨便換解碼器,mp3 不行:
 *   mp3 沒有精確的樣本位置 —— 編碼器延遲要靠 Xing/LAME 表頭猜,壞幀怎麼處理各家不同,
 *   所以 sdomad 才要逐條照抄 StepMania 的錯誤處理(見那邊的 README)。
 *   Vorbis 不一樣:格式本身帶 granule position,天生 gapless,任何合規解碼器輸出的 PCM 都一致。
 *   StepMania 自己的 RageSoundReader_Vorbisfile.cpp 也沒有任何偏移補償,只用 ov_pcm_tell
 *   追蹤位置 —— 那就是證據。所以這裡用 stb_vorbis 而不是 libvorbisfile,結果一樣而依賴少得多。
 *
 * stb_vorbis 是 public domain(檔尾有授權),不像 libmad 的 GPL v2 會傳染。
 */
#include <stdlib.h>
#include <string.h>

/* stb_vorbis 的 pushdata API 用不到，關掉可以少編一大塊。 */
#define STB_VORBIS_NO_PUSHDATA_API
/* 我們只從記憶體解，不需要 stdio 那條路（順便避開 fopen_s 的警告）。 */
#define STB_VORBIS_NO_STDIO
#include "stb_vorbis.c"

#ifdef _WIN32
#define SDOVORBIS_API __declspec(dllexport)
#else
#define SDOVORBIS_API
#endif

/* 解一整個 ogg。成功回傳 malloc 出來的**交錯 float** 緩衝(呼叫端要用 SdoVorbisFree 釋放),失敗回 NULL。
 *
 * outSamples = 交錯樣本總數(= 每聲道樣本數 × 聲道數),與 SdoMadDecode 的語意一致。
 *
 * 用 float 而不是 stb_vorbis_decode_memory 的 16-bit:Vorbis 內部就是 float,量化到 16-bit
 * 會白白丟掉精度,而 Unity 的 AudioClip.SetData 吃的本來就是 float。 */
SDOVORBIS_API float *SdoVorbisDecode(const unsigned char *ogg, int oggLen,
                                     int *outSamples, int *outChannels, int *outSampleRate)
{
    int err = 0;
    stb_vorbis *v;
    stb_vorbis_info info;
    unsigned int perChannel;
    long long total;
    float *buf;
    int got;

    if (outSamples) *outSamples = 0;
    if (outChannels) *outChannels = 0;
    if (outSampleRate) *outSampleRate = 0;
    if (!ogg || oggLen <= 0) return NULL;

    v = stb_vorbis_open_memory(ogg, oggLen, &err, NULL);
    if (!v) return NULL;

    info = stb_vorbis_get_info(v);
    perChannel = stb_vorbis_stream_length_in_samples(v);
    if (info.channels <= 0 || perChannel == 0) { stb_vorbis_close(v); return NULL; }

    /* 32-bit 溢位防線:一首歌大到讓 samples×channels 爆掉 int 的話寧可失敗，
       也不要配置一個比要求小的緩衝然後被寫爆。 */
    total = (long long)perChannel * (long long)info.channels;
    if (total <= 0 || total > 0x7FFFFFF0LL) { stb_vorbis_close(v); return NULL; }

    buf = (float *)malloc((size_t)total * sizeof(float));
    if (!buf) { stb_vorbis_close(v); return NULL; }

    got = stb_vorbis_get_samples_float_interleaved(v, info.channels, buf, (int)total);
    stb_vorbis_close(v);

    if (got <= 0) { free(buf); return NULL; }

    /* got 是「每聲道實際解出的樣本數」。比宣告的少是可能的(檔案被截斷)——
       回報實際值，別讓呼叫端拿到一段尾巴是未初始化記憶體的緩衝。 */
    if (outSamples) *outSamples = got * info.channels;
    if (outChannels) *outChannels = info.channels;
    if (outSampleRate) *outSampleRate = info.sample_rate;
    return buf;
}

SDOVORBIS_API void SdoVorbisFree(float *p)
{
    if (p) free(p);
}

SDOVORBIS_API int SdoVorbisVersion(void)
{
    return 1;
}
