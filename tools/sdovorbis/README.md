# sdovorbis — 給 Unity 用的 stb_vorbis 包裝

DATA 打包成 SDOPAK 之後，官方歌（`MUSIC/*.ogg`）沒有實體檔案。這顆 DLL 讓遊戲能直接把
pak 裡的位元組解成 PCM，**不必先解出來落地**。

沒有它的話只剩兩條路，兩條都不好：把 ogg 解到 `DATA/CACHE/AUDIO/` 再用 `file://` 播
（每首歌一次磁碟寫入、最多 512 MB 重複佔用，而且那些檔是明碼），或者乾脆不打包 MUSIC。

Unity 端的 P/Invoke 在 `Assets/Scripts/Game/VorbisDecoder.cs`；入口是
`Assets/Scripts/Game/MemoryAudio.cs`。DLL 載不到時 `VorbisDecoder.Available` 為 false，
呼叫端會退回原本的 `file://` 路徑（散裝樹照樣能跑）。

## 為什麼 ogg 可以隨便換解碼器，mp3 不行

這是這顆 DLL 與 `tools/sdomad` 最重要的差別，也是為什麼這裡敢用 stb_vorbis 而不是
StepMania 用的 libvorbisfile：

| | mp3 | ogg (Vorbis) |
|---|---|---|
| 樣本位置 | **沒有** —— 編碼器延遲要靠 Xing/LAME 表頭猜 | 格式自帶 granule position |
| 壞幀處理 | 各家不同（libmad 丟整幀、NLayer 吐垃圾樣本 → 差 0.62 秒） | 規格明確 |
| 換解碼器 | **會動到時間軸**（`sdomad` 因此逐條照抄 StepMania 的錯誤處理） | 輸出一致 |

證據：StepMania 自己的 `RageSoundReader_Vorbisfile.cpp`（`assets/SM-YHANIKI-master/src/`）
**沒有任何偏移補償**，只用 `ov_pcm_tell` 追位置。mp3 那邊的 `RageSoundReader_MP3.cpp`
則有一整套 `MAD_ERROR_BADDATAPTR` / `BADCRC` / `BADHUFFDATA` 的分支。

順帶：**mp3 永遠不會從 pak 讀** —— 官方 `MUSIC` 是 100% ogg（4,356 ogg + 2,180 `.gn`），
mp3 只出現在 `ADDON/SONG`（外部歌，reserved 目錄、永不打包）。所以 `sdomad` 那條路
一行都沒動。

## 授權

**stb_vorbis 是 public domain**（檔尾有 MIT / Unlicense 雙授權宣告），不像 libmad 的 GPL v2 會傳染。

⚠️ 但專案整體仍受 `sdomad`（libmad, GPL v2）約束 —— 只要出貨包含 `sdomad.dll`，
整個散布的作品就要以 GPL v2 授權並附原始碼。這顆 DLL 沒有改變那件事，只是沒有再增加義務。

`stb_vorbis.c` 取自 https://github.com/nothings/stb（v1.22，未修改）。

## 重建 DLL

需要 Visual Studio 的 VC++ 工具鏈，x64：

```powershell
$vc = "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\VC\Auxiliary\Build\vcvars64.bat"
cd tools\sdovorbis
cmd /c "call `"$vc`" >nul 2>&1 && cl /nologo /O2 /LD sdovorbis.c /Fe:sdovorbis.dll"
```

產出的 `sdovorbis.dll` 放到 `65/My project/Assets/Plugins/x86_64/`。
（會出現 `warning C4819`（字碼頁）—— 那是中文註解在 CP950 下的提示，不影響產出。）

## 離線自測

接進 Unity 之前先確認解碼器是對的 —— 錯的解碼器接進去只會變成很難查的雜音：

```powershell
cmd /c "call `"$vc`" >nul 2>&1 && cl /nologo /O2 selftest.c sdovorbis.lib /Fe:selftest.exe"
.\selftest.exe H:\65_remake_clean\DATA\MUSIC\sdom5085.ogg
```

預期（`sdom5085.ogg`）：

```
channels  = 2
rate      = 44100 Hz
samples   = 8362272 interleaved (4181136 per channel)
duration  = 94.810 s
peak      = 1.045644          ← Vorbis 可以超過 1.0，Unity 播放時會夾
rms       = 0.245979
OK
```

`4181136 / 94.810s` 這組數字在 `MemoryAudioTests.Ogg_DecodesRealOfficialSong` 裡也釘死了
—— 換解碼器如果動到長度，對拍就會整首偏掉，那個測試會先紅。

## 匯出的 API

```c
float *SdoVorbisDecode(const unsigned char *ogg, int oggLen,
                       int *outSamples, int *outChannels, int *outSampleRate);
void   SdoVorbisFree(float *p);
int    SdoVorbisVersion(void);
```

`outSamples` 是**交錯**樣本總數（= 每聲道樣本數 × 聲道數），與 `SdoMadDecode` 的語意一致。
輸出是 float 而不是 stb_vorbis 預設的 16-bit：Vorbis 內部就是 float，量化會白丟精度，
而 `AudioClip.SetData` 吃的本來就是 float。
