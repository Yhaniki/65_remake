# 商城 avatar 實錄 probe（線上 sdo.bin）

錄下官方**線上版**（`sdo.bin`）商城預覽實際載入的骨架 / 動作 / 網格檔，用來一槌定音：
商城裡的人形到底是 **wshop/mshop 假人骨架的 bind pose（不套 .mot）**，還是 **wrest/mrest idle .mot**；
每張卡片載哪個 `.MSH`。

## 為什麼是這個做法
線上版把 avatar **打包進 `Datas\Avatar.bin`**（不是個別 `.MSH` 檔），所以光掛 Windows 檔案開啟 API 只會看到
`Avatar.bin` 被開一次，看不到個別骨架/網格。因此**主力 hook 用線上反編譯 `H:\sdo_cn\sdo.bin.c` 的位址**：

- `FUN_00438f70(this, char* name)` @ **VA 0x00438f70**（`__thiscall`，`args[0]`=資源名）= 線上「**依名載入資源**」函式，
  被 `"wshop0001.hrc"`、`"mshop0001.hrc"`、`"101013_hua10.hrc"`、item 網格… 呼叫（`sdo.bin.c:54681` 定義、
  `:512956/:560599` 商城 init 呼叫）。掛它 → 錄下商城從封包載入的**每一個資源名**（骨架/動作/網格）。
  線上 imageBase 0x400000、無 ASLR，VA == runtime 位址（跟 ShowTime hook 同慣例）。

另外**補掛**檔案開啟 API（`CreateFileW/A`、`fopen`）當備援，抓 `Avatar.bin` 本身或任何沒打包的散檔。

## 前置
```
pip install frida frida-tools
```
（Frida 17.x。若 `frida` CLI 不在 PATH，bat 會自動改用 `python probe.py`。）

## 怎麼跑
1. 先開遊戲，過了 launcher、進到大廳/房間。
2. **以系統管理員身分**執行 `run_shop_probe.bat`（右鍵 → 以系統管理員身分執行）。
   sdo.bin 是提權程序，非 admin 會被拒絕 attach。
3. 遊戲裡：開**商城** → 點 `发型` / `表情` / `项链` / `下装` 分頁 → hover 卡片 → 按**試穿**。
   視窗會即時印出載入的檔名，同時寫進 `shop_avatar_online_log.txt`。
4. 完成後在視窗按 `Ctrl+C` 結束。

## 怎麼看結果
log 每行是 `[秒數] API  路徑`。重點看：
- 出現 `WSHOP0001.HRC` / `MSHOP0001.HRC`（或 0002/0003）→ 商城用**假人骨架**。
- 若**同時**出現某個 `WREST####.MOT` / `MREST####.MOT` → 商城有套 idle 動作（哪一支就照抄）。
- 若**只**有 `.HRC` 沒有相隨的 `.MOT` → 確認官方就是 bind pose（不套動作）。
- 每張卡片會載對應的 `######_WOMAN_XXXX.MSH`（COAT/PANT/HAIR/FACE_HUAN/LINGDANG…）。

把 log 貼回來，我就能把 remake 的骨架/動作/取景對到官方實測值。

## log 每行前綴
- `RES  xxx` = 經 `FUN_00438f70` 依名載入的資源（**主力**，看這個就對了；含封包內的骨架/動作/網格）。
- `FILE xxx` = 經檔案 API 開的散檔（通常只有 `Avatar.bin` / `UI.bin` 之類封包）。

## 注意
- 若 attach 後 `RES` 一直沒東西：確認 base 印出來是 `0x400000`（無 ASLR）。若線上更新過、位址位移了，
  把 log 開頭那行 base 貼回來，我用 `sdo.bin.c` 重新對位址。
- 檔案：`hook_online_avatar_files.js`（hook 本體，含線上 VA）、`probe.py`（attach 用）、`run_shop_probe.bat`（雙擊入口）。
