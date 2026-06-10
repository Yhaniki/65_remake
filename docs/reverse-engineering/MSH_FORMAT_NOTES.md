# SDO `.MSH` 格式逆向研究筆記（已完成）

> 來源：`sdo_standalone.exe.c`（Ghidra 反編譯）追蹤到 mesh class vtable[1] = `FUN_0041a7e0`，
> 並以 10 個樣本檔驗證解析正確（vertex / index / UV / 材質 dds 名稱皆合理）。

## 反編譯呼叫鏈

| 位址 | 角色 |
|------|------|
| `FUN_0041fb40(name)` | MSH loader 入口（檔名 → mesh 物件） |
| `FUN_0041ed50` | `IResource::LoadFromFile`（讀檔到 buffer，再 dispatch `vtable[1]`） |
| `FUN_0041a110` | Mesh class 建構式（物件 0x2c bytes，vtable=`PTR_FUN_00543144`） |
| **`FUN_0041a7e0`** | **Mesh class 真正的 binary parser**（vtable[1]） |
| `FUN_0041a160` | Mesh deinit（清空 sub-arrays） |
| `FUN_0041fa10` / `FUN_00409110` | HRC loader / HRC class 建構式 |

確認遊戲使用 D3DX9 mesh 函式庫（看到 `D3DXFERR_INVALIDMESH` 等錯誤訊息）。

## File layout（最終版）

```
struct MshFile {
  char    magic[12];          // "Mesh00000030"
  uint32  submesh_count;      // 多數檔案 = 1，COAT 系列 = 2

  Submesh submeshes[submesh_count];
};

struct Submesh {
  // ── sub-header ───────────────────────────────
  uint32  fvf;                // D3DFVF: 0x1158 / 0x115A / 0x115C
  uint32  idx_size_bytes;     // index buffer 位元組數
  uint32  options;            // 固定 = 101 (0x65)

  // ── index buffer ─────────────────────────────
  uint16  indices[idx_size_bytes / 2];   // triangle list

  // ── vertex sub-header ────────────────────────
  uint32  vert_size_bytes;
  uint32  vert_stride;        // 每頂點位元組數（與 fvf 對應）

  // ── vertex buffer (每頂點 = vert_stride bytes) ──
  // FVF 0x1158 (44 bytes): pos(12) + 1 weight(4) + UBYTE4 bone idx(4)
  //                       + normal(12) + diffuse(4) + uv(8)
  // FVF 0x115A (48 bytes): + 1 個 weight (= 24 bytes 蒙皮區塊)
  // FVF 0x115C (52 bytes): + 1 個 weight (= 28 bytes 蒙皮區塊)
  byte    vert_data[vert_size_bytes];

  // ── material / texture entries ───────────────
  uint32  reserved[6];        // 推測為 bounding box 等 metadata
  uint32  num_materials;
  Material materials[num_materials];

  // ── footer (材質 D3D 物件 metadata，本 reader 略過) ──
  // FUN_0041a7e0 26265 之後依 *param_2 ∈ {0,1,2} 各有不同處理。
};

struct Material {
  uint32  data[17];           // transform / 各種 flags
  char    dds_name[];         // null-terminated，e.g. "boydogcloth001.dds"
  byte    padding[];          // 對齊到 408 bytes (0x66 dwords)
};
// 總大小固定 = 408 bytes = 0x66 dwords
```

## 已驗證樣本

| 檔名 | size | submesh | verts | tri | DDS materials |
|------|------|---------|-------|-----|---------------|
| `900006_MAN_SHOES.MSH` | 9340  | 1 | 162  | 200 | `900006_man_shoes.dds` |
| `DOG.MSH`             | 27276 | 1 | 431  | 586 | `boydogleg001.dds`、`boydogcloth001.dds`、`boydoghead001.dds` |
| `000001_FOX.MSH`      | 65754 | 1 | 1128 | 789 | `000001_fox.dds` |
| `900001_MAN_FACE.MSH` | 22324 | 1 | 369  | 600 | `900001_man_face.dds`（Y 範圍 55~65＝頭部高度）|
| `900008_WOMAN_HAIR.MSH` | 22378 | 1 | 355 | 473 | `900008_woman_hair.dds`（Y 47~61）|
| `002304_WOMAN_COAT.MSH` | 63796 | 2 | 1st=756 | 868 | `002304_woman_coat.dds` |

UV 全部落在 [0, 1]，max_idx == verts-1 ✓。

## 已知未盡事項

1. **多 submesh 檔（COAT 系列）的 footer 解析**：
   `FUN_0041a7e0` 26265 行之後依 material class id（0/1/2）讀取不同大小的物件，
   完整解析需要還原 D3D 材質結構，本 reader 遇到第二個 submesh 會自動停下。
2. **bone index 對應**：vertex buffer 內的 UBYTE4 bone indices 需配合 `.HRC` 才能還原 skin pose。

## 對應的 reader 程式碼

`tools/msh_reader.py` 完整實作此格式（含 self-test）。
