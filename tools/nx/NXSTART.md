# NXStart — 繞過 NXPatch 啟動握手

`NXPatch.exe`（私服客戶端本體）**不能直接雙擊啟動**：它會檢查自己是不是由官方 launcher
（`NXPatchLauncher.exe`）帶起來的。官方 launcher 同時還做授權 / manifest 檢查。

`NXStart.cs` 是一支**最小替代啟動器**：只重現 NXPatch 要的那組「啟動握手」，
**不做**任何授權 / manifest 檢查，所以遊戲能直接跑起來。

> **出處說明**：`NXStart.cs` 本來就在 `H:\sdo\Super Dance Online\` 底下（原始碼註解寫明常數是
> *"recovered from the original launcher's own code"*，即從官方 launcher 反出來的）。
> 本次工作**不是**重新破解它，而是把它的握手流程**在 Python 重寫**，用來讓 Frida 能
> spawn NXPatch 做記憶體 dump / hook（見 `dump_chart.py`、`dump_nxpatch_image.py`）。

---

## 握手內容

NXPatch 啟動時要同時滿足**三件事**：

1. **argv 帶固定啟動戳（LaunchStamp）**
   ```
   --nx=_ry8Sfdm40N9wv7tg2dI1eiodUU9WxWkduk0Ib-o
   ```
   （固定字串，不參與計算。）

2. **argv 帶一個 seal（隨機 nonce + 校驗值）**
   ```
   --nxs=<nonce 8位大寫HEX><Compute(nonce) 8位大寫HEX>
   ```

3. **同一個 seal 鏡射進具名共享記憶體**
   - 名稱：`Local\NXPatchV25Session`
   - 大小：128 bytes，先清零
   - 內容：seal 的 ASCII + `\0`（最多寫 127 bytes）
   - **必須在遊戲讀取前保持存活** → 啟動後 sleep 幾秒再釋放（NXStart 用 8 秒）

## Compute()：seal 的校驗算法

一個 FNV-1a 變體（每輪多一個 rotate），最後再 XOR + rotate：

```csharp
const uint Gs0 = 0x7AE373C3;   // 初始值
const uint Gs1 = 0x3BA8EB43;   // 收尾 XOR

uint Compute(string nonce)      // nonce = 8 個字元
{
    uint num = Gs0;
    for (int i = 0; i < 8; i++)
    {
        num ^= (byte)nonce[i];
        num *= 16777619u;               // FNV prime
        num = (num << 5) | (num >> 27); // rotl 5
    }
    num ^= Gs1;
    return (num << 7) | (num >> 25);    // rotl 7
}
```

nonce 本身是隨機 uint32 印成 `X8`（8 位大寫 HEX）；校驗值同樣印成 `X8`。
因為 nonce 是自己產的，**不需要跟伺服器互動**——這就是能離線繞過的原因。

## Python 版（本資料夾工具用的）

```python
Gs0, Gs1 = 0x7AE373C3, 0x3BA8EB43
rotl = lambda x, n: ((x << n) | (x >> (32 - n))) & 0xFFFFFFFF

def compute(nonce: str) -> int:
    v = Gs0
    for i in range(8):
        v ^= ord(nonce[i]); v = (v * 16777619) & 0xFFFFFFFF; v = rotl(v, 5)
    return rotl(v ^ Gs1, 7)

nonce = "DEADBEEF"                                    # 固定值也可以，反正是自己產的
seal  = "--nxs=" + nonce + "%08X" % compute(nonce)
stamp = "--nx=_ry8Sfdm40N9wv7tg2dI1eiodUU9WxWkduk0Ib-o"
# 1) CreateFileMappingW(INVALID_HANDLE_VALUE, None, PAGE_READWRITE, 0, 128, r"Local\NXPatchV25Session")
#    → 清零後寫入 seal + b"\0"，並讓這個 handle 保持存活
# 2) 以 argv = [NXPatch.exe, stamp, seal] 啟動
```

完整可跑的版本見 `dump_chart.py` / `dump_nxpatch_image.py` 開頭那段。

---

## 這樣繞過會/不會得到什麼

- ✅ 遊戲**能啟動**（跳過官方 launcher 的授權 / manifest 檢查）。
- ❌ **不會**讓你離線解開 `.nx` 譜面。譜面金鑰是進歌時由連線提供的（見 `NX_FORMAT.md` §2），
  跟啟動握手是**兩回事**。實測：只用這個握手起遊戲，`[ctx+0x90]`（金鑰）全程為 null。

## 順帶：NXPatch.exe 並沒有真的加殼

進入點在 `.nxd` 段、開頭是 `pushfd/pushal` 的保護 stub，看起來像加殼，**但主 `.text` 是明文可讀的**。
（一開始反組譯不出來只是起點對到 mid-instruction。）
所以 `.nx` 的讀取/解密流程可以**純靜態**分析出來 —— `dump_nxpatch_image.py` 只是為了拿到
無 ASLR 的完整映像方便逐位址對照。
