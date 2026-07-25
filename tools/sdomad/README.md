# sdomad — 給 Unity 用的 libmad 包裝

外部歌(osu / StepMania)的 mp3 解碼走這顆 DLL，也就是 **StepMania 用的同一個解碼器 libmad**。
輸出的 PCM 與 StepMania 逐位相同，譜面作者當初在 SM 裡校出來的 `#OFFSET` 因此才對得上。

## 為什麼不用純 C# 的 NLayer

NLayer（`Assets/Plugins/NLayer/NLayer.dll`，備援路徑）在兩件事上和 libmad 不一樣，兩件都會變成聽得出來的偏移：

| | libmad / StepMania | NLayer | 後果 |
|---|---|---|---|
| Huffman 資料壞掉的幀 | `MAD_ERROR_BADHUFFDATA` → `continue` **整幀丟棄** | 不報錯，照樣吐 2304 個垃圾樣本 | `lull~そして僕らは~` 的 OP.mp3 開頭有 25 個這種重複 padding 幀 → 音樂晚 **0.62 秒**（0.088s vs 0.71s） |
| reservoir 指不到資料的幀 | `MAD_ERROR_BADDATAPTR` → `ret = 0` **pretend success**，照樣輸出一幀 | 靜默跳過 | 跳一幀，那之後整首提前 **26ms**（engine[Blue] 37.5s 起、Amanojaku 24s 起） |

附帶好處：libmad 比 NLayer **快 5–8 倍**（engine.mp3 279ms vs 1410ms）。

`sdomad.c` 的解碼迴圈逐條照抄 `RageSoundReader_MP3::do_mad_frame_decode`
（`assets/SM-YHANIKI-master/src/RageSoundReader_MP3.cpp`）。

## 授權

**libmad 是 GPL v2** — 見 `libmad-COPYING.txt`。散布含這顆 DLL 的建置時，整個作品要以 GPL v2 授權並提供原始碼。
（StepMania 本身也是 GPL v2。）libmad 原始碼取自 `D:/repo/SM-YHANIKI/ThirdParty/mad-0.15.1b`（0.15.1b 原版，未修改）。

## 重建 DLL

需要 Visual Studio 的 VC++ 工具鏈。x64、`FPM_64BIT`（x64 的 MSVC 不支援 `FPM_INTEL` 的 inline asm）：

```powershell
$vs  = "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community"
$vc  = "$vs\VC\Auxiliary\Build\vcvars64.bat"
$mad = "D:\repo\SM-YHANIKI\ThirdParty\mad-0.15.1b"     # libmad 0.15.1b 原始碼
$srcs = @("version.c","fixed.c","bit.c","timer.c","stream.c","frame.c",
          "synth.c","decoder.c","layer12.c","layer3.c","huffman.c") |
        ForEach-Object { "`"$mad\$_`"" }
cmd /c "call `"$vc`" >nul 2>&1 && cl /nologo /O2 /LD /I `"$mad`" /DFPM_64BIT ^
        /DSIZEOF_INT=4 /DSIZEOF_LONG=4 /DSIZEOF_LONG_LONG=8 ^
        sdomad.c $($srcs -join ' ') /Fe:sdomad.dll"
```

產出的 `sdomad.dll` 放到 `65/My project/Assets/Plugins/x86_64/`。
Unity 端的 P/Invoke 在 `Assets/Scripts/Game/MadDecoder.cs`；DLL 載不到時會自動退回 NLayer。

## 匯出的 API

```c
float *SdoMadDecode(const unsigned char *mp3, int mp3Len,
                    int *outSamples, int *outChannels, int *outSampleRate,
                    int *outFrames, int *outSkipped, int *outPretend);
void   SdoMadFree(float *p);
int    SdoMadVersion(void);
```

`outSkipped` = 被丟棄的壞幀數，`outPretend` = BADDATAPTR「假裝成功」的幀數 —— 兩個都會進 Unity 的 log，
方便對照某首歌到底發生了什麼。
