# 熱舞 Online (Dance! Online) SDM 轉 OGG 解碼指南

本指南說明如何將《熱舞 Online》遊戲中的 `.sdm` 音樂加密檔無損解密為一般播放器可讀取的標準 `.ogg` 格式。

## 工具說明
核心解碼工具為專案內的 `sdm_batch_decoder.py`。該腳本經過重構，具備「智慧序列號映射」功能，能夠 100% 完美還原音訊，不會出現破音、無法播放或 OGG 結構損壞的問題。

### 為什麼需要這支腳本？
遊戲的 `.sdm` 模型具有非常特殊的加密結構：
1. **前綴剝離存存**：每首歌曲真正的 OGG 標頭（前 512 位元組，內含極關鍵的 Vorbis Codebook 解析參數）被從音訊檔中剝離，經過 DES 加密後集中存放在 `songencode.dat` 之中。
2. **音訊主體加密**：而 `.sdm` 檔案內保留的音訊主體（從 513 位元組開始）則採用簡單的 `(505 - byte) % 256` 算法進行混淆。

早期依靠「檔名擷取數字」去尋找密鑰的方法經常發生「A 歌配到 B 歌的標頭」的嚴重錯配。即使聲音資料是對的，播放器也因為少了正確的 Codebook 而報錯罷工。

**新版腳本的優勢：**
新版 `sdm_batch_decoder.py` 會先解密檔案主體提取出**原生的 OGG 序列號**，隨後在 `songencode.dat` 中反向精確尋找擁有相同序列號的標頭。只要配對成功，就能達到毫無剪接痕跡的**無損還原**。

---

## 執行環境要求
- Python 3.6 或以上版本
- （可選）如果您需要測試輸出檔案的有效性，可以使用 `ffprobe` 或任何常見的音樂播放器 (如 VLC, Windows Media Player 等)。

## 使用方法

這支腳本支援單一檔案與整個資料夾的批次轉換。

### 基本指令格式
```powershell
python sdm_batch_decoder.py <輸入路徑> <songencode.dat路徑> [-o <輸出資料夾>]
```

### 1. 單一檔案轉換
如果您只想轉換一首歌曲（例如 `sdom0001.sdm`）：
```powershell
python sdm_batch_decoder.py music\sdom0001.sdm music\songencode.dat -o decrypted_music
```
轉換成功後，您會在 `decrypted_music\sdom0001.ogg` 找到能完美播放的音樂檔。

### 2. 批次資料夾轉換
如果想一次把 `music` 資料夾下幾百首 `.sdm` 全部轉換出來：
```powershell
python sdm_batch_decoder.py music music\songencode.dat -o decrypted_music
```
腳本將會全自動掃描並匹配真正的加密金鑰，輸出到指定的目錄。

---

## 常見問題 (FAQ)

**Q: 終端機顯示「Skipping sdomXXXX.sdm: No valid key found」？**
A: 這表示該 `.sdm` 在 `songencode.dat` 中找不到對應的序列號密鑰。這可能是因為該 `songencode.dat` 版本較舊，未包含後續更新的歌曲標頭，或是該檔案本身已損毀。

**Q: 需要使用 `fix_ogg_serial` 去修復 OGG 序列嗎？**
A: **絕對不需要**。舊式解密思維因為代入了錯誤的標頭才產生序列號斷檔。新腳本由於找到的是 100% 同源的標頭，產出的檔案自然符合標準 OGG Vorbis 規格，不該進行任何人為的序列號竄改。

**Q: 轉出來的 OGG 可以丟進一般剪輯軟體嗎？**
A: 可以。這些檔案已經是合法的原生 Vorbis 音訊，可被 Premiere、Audacity 等直接讀取。
