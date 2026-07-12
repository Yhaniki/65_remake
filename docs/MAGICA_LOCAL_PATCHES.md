# Magica Cloth 2 本地修改清單(必讀:重裝/升級 MC2 後要重新套用)

`Assets/MagicaCloth2/` 是付費資產、已列入 `.gitignore` **不進版控**。但本專案的 MMD 布料物理
依賴以下**本地修改**;若重新匯入或升級 Magica Cloth 2,必須重新套用,否則布料會回到
「慢動作/甩不動」狀態(症狀:頭髮飄、掉落慢 4 倍、跟不上身體)。

## 為什麼需要:尺度不匹配

Magica 假設「1 世界單位 = 1 公尺」的真人尺度,所有上限都是 SI 數值。本專案的 SDO 角色
渲染高度 ~51 世界單位 ≈ 1.6 m 少女 ⇒ **~32 單位 = 1 公尺**,物理正確值是 SI 的 32 倍:

| 物理量 | 正確值(×32) | MC2 原廠 clamp | 後果(未修) |
|---|---|---|---|
| 重力 | 9.8×32 ≈ 314 u/s² | **20** | 4× 慢動作(擺動週期 8.4s vs 2.1s) |
| 粒子速度上限 | 跳舞需 64–160 u/s | **10** | 頭髮物理上不可能跟上身體 |
| MotionConstraint maxDistance | 裙擺行程 ~9 u | **5** | 裙擺被拴在 16 cm 等值內=硬殼 |

`MmdMagicaCloth.cs` 會自動依角色身高推導並設定正確值(`unitsPerMeter = 身高/1.6`),
但被下列 clamp 夾回去 → 必須放寬 clamp。

## Patch 1:重力 clamp

`Assets/MagicaCloth2/Scripts/Core/Cloth/ClothSerializeDataFunction.cs`(DataValidate 內):

```csharp
// 原:gravity = Mathf.Clamp(gravity, 0.0f, 20.0f);
gravity = Mathf.Clamp(gravity, 0.0f, 1000.0f);
```

## Patch 2:粒子速度上限

`Assets/MagicaCloth2/Scripts/Core/Define/SystemDefine.cs`:

```csharp
// 原:public const float MaxParticleSpeedLimit = 10.0f;
public const float MaxParticleSpeedLimit = 1000.0f;
```

## Patch 3:MotionConstraint maxDistance clamp

`Assets/MagicaCloth2/Scripts/Core/Cloth/Constraints/MotionConstraint.cs`(DataValidate 內):

```csharp
// 原:maxDistance.DataValidate(0.0f, 5.0f);
maxDistance.DataValidate(0.0f, 100.0f);
```

## 相關但「不是」MC2 修改的注意事項

- **相機剔除**:`MmdMagicaCloth.BuildCloth` 已明確設 `cullingSettings.cameraCullingMode = Off`
  (我們的布料掛在無 renderer 的空物件上,預設的自動剔除判定不可靠)。這在我們的程式裡,
  不需動 MC2。
- **取樣階段假象(重要教訓)**:MC2 每幀在 EarlyUpdate 把骨頭**還原成原始姿勢**、在
  LateUpdate 後才寫入模擬結果。任何在 Update 階段讀布料骨頭 transform 的程式碼會看到
  「原始姿勢」→ 看起來像沒模擬(渲染是動的)。要讀模擬結果請在 `WaitForEndOfFrame`
  或 PostLateUpdate 之後。曾因此誤診「布料全凍」花了 8 輪診斷。

## 驗證方式(自動化)

`tools/mmd_cloth_validate/` 是完整的量化驗證管線(pybullet 重建 MMD/Bullet 地面真值 vs
遊戲內探針,4 情境 × 10 指標自動比對):

```powershell
# 1) 建 player(含探針)
Unity -batchmode -quit -projectPath "65/My project" -executeMethod BuildScript.BuildWindows -buildOut Build/Probe
# 2) 跑探針(自動跑 rest/turn/walk/spin,寫 magica_*.json,自動關閉)
Build/Probe/dance.exe -mmdprobe
# 3) 比對
cd tools/mmd_cloth_validate && python compute_metrics_magica.py magica && python compare.py
```

套 patch 後跑一輪,`compare.py` 應顯示 ≥22 PASS(2026-07-12 基準)。
