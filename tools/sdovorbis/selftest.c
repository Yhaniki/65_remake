/* sdovorbis 的離線自測:解一個真實的 .ogg，把關鍵數字印出來。
 *
 * 目的是「接進 Unity 之前先確認解碼器是對的」—— 錯的解碼器接進去只會變成很難查的雜音。
 * 用法:  selftest.exe <檔案.ogg>
 * 輸出:  聲道數 / 取樣率 / 每聲道樣本數 / 秒數 / 峰值 / RMS / 前後幾個樣本
 */
#include <stdio.h>
#include <stdlib.h>
#include <math.h>

extern float *SdoVorbisDecode(const unsigned char *ogg, int oggLen,
                              int *outSamples, int *outChannels, int *outSampleRate);
extern void SdoVorbisFree(float *p);

int main(int argc, char **argv)
{
    FILE *f;
    long len;
    unsigned char *buf;
    float *pcm;
    int samples = 0, ch = 0, rate = 0;
    double sum = 0.0, peak = 0.0;
    int i;

    if (argc < 2) { printf("usage: selftest <file.ogg>\n"); return 2; }

    f = fopen(argv[1], "rb");
    if (!f) { printf("FAIL open %s\n", argv[1]); return 1; }
    fseek(f, 0, SEEK_END); len = ftell(f); fseek(f, 0, SEEK_SET);
    buf = (unsigned char *)malloc(len);
    if (!buf || fread(buf, 1, len, f) != (size_t)len) { printf("FAIL read\n"); return 1; }
    fclose(f);

    pcm = SdoVorbisDecode(buf, (int)len, &samples, &ch, &rate);
    free(buf);
    if (!pcm) { printf("FAIL decode\n"); return 1; }

    for (i = 0; i < samples; i++) {
        double a = pcm[i] < 0 ? -pcm[i] : pcm[i];
        if (a > peak) peak = a;
        sum += (double)pcm[i] * (double)pcm[i];
    }

    printf("file      = %s (%ld bytes)\n", argv[1], len);
    printf("channels  = %d\n", ch);
    printf("rate      = %d Hz\n", rate);
    printf("samples   = %d interleaved (%d per channel)\n", samples, ch ? samples / ch : 0);
    printf("duration  = %.3f s\n", (ch && rate) ? (double)(samples / ch) / rate : 0.0);
    printf("peak      = %.6f\n", peak);
    printf("rms       = %.6f\n", samples ? sqrt(sum / samples) : 0.0);
    printf("first8    =");
    for (i = 0; i < 8 && i < samples; i++) printf(" %+.6f", pcm[i]);
    printf("\nlast8     =");
    for (i = samples - 8; i < samples; i++) if (i >= 0) printf(" %+.6f", pcm[i]);
    printf("\n");

    /* 全靜音 = 解碼其實失敗了但沒回 NULL —— 那種「成功」最難查，明確擋掉。 */
    if (peak < 1e-6) { printf("FAIL: decoded to silence\n"); SdoVorbisFree(pcm); return 1; }

    SdoVorbisFree(pcm);
    printf("OK\n");
    return 0;
}
