# Skin 系統

> osu mania + SM NoteSkin 雙支援。Phase 1 只用預設皮。

## 目錄結構

```
Skins/
└── {skinName}/
    ├── mania/              # osu 風
    │   ├── key1.png
    │   ├── key2.png
    │   └── ...
    └── notes/              # SM 風 alias
        ├── tap.png
        ├── hold head.png
        └── hold tail.png
```

SM 參考 [SM-YHANIKI](https://github.com/Yhaniki/SM-YHANIKI)：`note/`、`note(RV)/` 逆流用。

## 可換元素

| 元素 | osu | SM |
|------|-----|-----|
| Tap | mania key | tap |
| Hold head/body/tail | hold 系列 | hold 系列 |
| Combo | combo 數字 | combo 圖層 |
| 判定 | hit 特效 | — |

## Hold release 視覺

- SM 風：**Marvelous 白光**（YHANIKI hold 結尾）
- 或 osu skin 自訂 tail 圖

## 設定

- 玩家選 skin → PlayFab / PlayerPrefs
- UI skin 與 gameplay skin 可分離

## Phase 1

固定內建 1 套，無切換 UI。

## 相關

- [systems/skin.md](../systems/skin.md)
- [sm-yhaniki-notes.md](../reference/sm-yhaniki-notes.md)
