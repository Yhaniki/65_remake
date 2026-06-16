# SDO 場景 → MapObj 載入表 (Unity 用)

原始遊戲 (`sdo_stand_alone.exe`) 的 3D 舞台背景載入機制，整理成 Unity remake 可直接消費的資料表。

- **機器可讀資料**：[SDO_SCENE_MAPOBJ_TABLE.json](SDO_SCENE_MAPOBJ_TABLE.json)（40 場景、245 mapobj 群組、575 實例、35 個特效/光板）
- **產生腳本（可重跑）**：[scene_mapobj_build.py](scene_mapobj_build.py)（座標直接從 EXE `.data` 解出）

## 機制（一句話）

每個場景 id 進入 `FUN_004b43c0` = `Scene_LoadBackground`（[030_scene_004b43c0.c](../../assets/sdox_offline/src/modules/scene/030_scene_004b43c0.c)），三步驟：

1. `scenePaths[id]` → 掛載 `Datas\Scene\scnNNNN.bin`（或具名的 `MerryRoomA.bin` / `ScnRoom.bin` …）
2. 從該封包載入基底舞台 `scene.msh` + `scene.hrc`
3. `switch(id)` → 載入該場景 hardcode 的 mapobj 群組（`.bin` + `.msh`/`.hrc`/`.mot`），並各自擺位

**SCN0009 ↔ GUATAN** 就是 `case 9`：載入 `guatan.bin`，同一份模型實例化 4 份（第 3、4 份縮放 0.65）。

## JSON Schema

```jsonc
{
  "id": 9, "hex": "0x09",
  "archive": "Datas/Scene/scn0009.bin",   // 掛載的場景封包
  "folder":  "SCN0009",                    // Extracted/SCENE 下的資料夾
  "base":    { "msh": "scene.msh", "hrc": "scene.hrc" }, // 基底舞台（所有場景都先載）
  "instancePositions": "explicit",         // 見下方「座標來源」
  "mapobjs": [
    { "name": "guatan",
      "archive": "Datas/scene/Mapobj/guatan.bin",
      "msh": "guatan.msh", "hrc": "guatan.hrc", "mot": "guatan.mot", // 缺的 key = 該檔不載
      "instances": [ { "pos": [-45.79,0,0], "scale": [1,1,1] }, ... ] }
  ],
  "effects": [                              // 非 mapobj 的額外物件（見下）；無則不出現此 key
    { "kind": "particle", "effect": "0x6c", "pos": [66,193,213], "scale": [20,20,20] }
  ],
  "notes": "..."
}
```

### `effects` 陣列（非 mesh 物件，已解出座標）

舞台上不是 msh/hrc 的東西放在 `effects`，全部是程式碼字面值座標（`explicit`）：

| `kind` | 意義 | 欄位 | 出現於 |
|--------|------|------|--------|
| `billboard` | 2D 貼圖光板（面向相機的 quad）| `texture`, `pos`, `scale`(光板尺寸) | id 1, 5, 22, 24 |
| `light` | 點光源 | `pos` | id 28 |
| `particle` | 粒子特效發射器（`Effect_Play`）| `effect`(特效 id), `pos`, `scale`(均勻) | id 31, 32, 33 |

例：婚禮房 id 31 撒 6 朵花瓣粒子（`0x6c`），id 33 撒 9 朵。雪景 id 24 有 3 塊 `guang_.tga` 光板（200×200）。

## ⚠️ 座標來源 `instancePositions`（Unity 匯入關鍵）

| 值 | 意義 | Unity 作法 |
|----|------|-----------|
| `explicit` | `pos` 是程式碼字面值或已初始化的 `.data`，**真實座標** | 直接套用 transform |
| `origin` | 引擎的位置表是**全 0 的 BSS**；每個實例都擺在原點，**模型幾何本身已內含世界座標** | 匯入這些 mesh 時**不要 recenter**（保留模型空間原點），或從 `.bin`/`.msh` 取座標 |

`explicit` 的場景：0, 4, 5, 6, 7, 9, 10, 15, 16（其餘多為 `origin`）。
例如 `case 6` 的 `deng`（72 顆燈沿弧線排列）座標是真的；`case 17` 的地鐵物件則全是 `origin`，靠 mesh 自帶座標。

## id → 場景對照（速查）

| id | 封包 | 主要 mapobj |
|----|------|------------|
| 0 | scn0000 | huishou, zhaopai |
| 1 | scn0001 | （只有 2D 光暈特效）|
| 2 | scn0002 | （無，與 27 共用空 body）|
| 3 | scn0003 | box(16×16 菱形格, 256 格已展開), ball |
| 4 | scn0004 | sea_up/down, chuan, beach(lang), sea |
| 5 | scn0005 | christmas, merrychristmas |
| 6 | scn0006 | deng(×72), zhuanpan |
| 7 | scn0007 | sky |
| 8 | scn0008 | jinzita, zimu |
| **9** | **scn0009** | **guatan ×4** |
| 10 | scn0010 | house ×2, mao, qiqiu, mao1 |
| 11 | scn0011 | screen, caidai, dengguang, dideng, dingdeng, ding, jiguang, laba |
| 12 | scn0012 | fifa_guanggao/renqun/shanguang/qiubei |
| 13 | scn0013 | fifanight_*（夜間，沿用日間 .msh）|
| 14 | scn0014 | 14_haidi: guang, shanhu*, sea_screen, tv |
| 15 | scn0015 | UV, hua, shu1-4 |
| 16 | scn0016 | di1-21 + fangzi* + sky + jiguang1-3（38 個）|
| 17 | scn0017 | 17_ditie: dianshi, die1-2, fushou1-2, laba1-2, sky, zhan, zhuzi … |
| 18 | scn0018 | 18_Boat: beijing, guajian1-2, qiao, water … |
| 19 | scn0019 | pk: guang1-4, laba1-11, shan, zhuan |
| 20 | scn0020 | 19_subway: tv1, tv6, SUBWAY04 |
| 21 | scn0021 | saloon: laba1-12, deng1-12, zhumen |
| 22 | scn0022 | fenmu: dong1-2, gui, gui2, lanhuo, sheguang1-3 (+3 光板) |
| 23 | scn0023 | jiaoshi: chuanghu(fanse), laba1-10 |
| 24 | scn0024 | xuejing: donghua, biaodonghua (+3 光板 +雪特效) |
| 25 | scn0025 | chuntian: donghua + hudie 蝴蝶動畫 ×5 |
| 26 | scn0026 | lanqiuchang: che1-4, deng1-3, laba1-4, huo, xiaodeng |
| 27 | scn0027 | （無，case 0x1b 空 body）|
| 28 | scn0028 | niaochao: chuan, deng1-4, feichuan, pengshui (+4 燈) |
| 29 | scn0029 | jiku: jiuba, pingmu |
| 30 | scn0030 | katonggonglu: kongtongfengche(風車) |
| 31 | MerryRoomA | hunliA (+6 特效) |
| 32 | MerryRoomB | hunliB, hunliBhua (+7 特效) |
| 33 | MerryRoomC | hunliC, hunliChua (+9 特效) |
| 34 | ScnCommunityHall | （無 switch case，只載基底）|
| 35 | ScnMyHouse | 3dhouse: computer |
| 36 | ScnCommunity | （無 switch case，只載基底）|
| 37 | ScnRoom | Room_obj: dianshi, laba1-4, guang1-8, taizi（14 個）|
| 38 | ScnMerryRoom | 同 37 但無 taizi（13 個）|
| 39 | ScnRoom_Night | Room_night_obj: lang, tang1-2, xin, deng |

## id 重映射（進 switch 前）

- `0x0c → 0x0d`（旗標 `DAT_00674f04+0xad9 != 1` 時）
- `0x25 → 0x26`（旗標 `DAT_00674f04+0x82 == 1` 時）
- `0x1e → 0x1f`（永遠）
- `0x1c → 0x1d`（永遠）

屬日/夜或變體切換。JSON 中 0x1c / 0x1e 的 case body 仍保留供參考。

## 已全部補入

- **box (id 3)**：256 格菱形網格已展開成顯式座標（`x = -154.523 + (r+c)·10.301131`，`z = (c-r)·10.301131`）。
- **光板 / 點光源 / 粒子特效**：已解出座標放入各場景的 `effects` 陣列（id 1, 5, 22, 24, 28, 31, 32, 33，共 35 個）。

唯一未列舉的是 mapobj 材質用的 `.dds`/`.tga` 貼圖序列——它們屬於各自的 `.bin` 封包，由 mesh 材質自行引用。
