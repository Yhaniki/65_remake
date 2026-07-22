using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Sdo.Shop;

namespace Sdo.Game
{
    /// <summary>
    /// 商品資源解析 (非衣服商品專用)：把 <c>DRESS.TXT</c> + <c>PETDRESS.TXT</c> 這張官方對照表載進來 (見
    /// <see cref="DressTable"/> 的逆向出處)，再把「檔名」對到 data root 底下**實際的檔案路徑**。
    ///
    /// 官方資料夾分工 (exe 字串 0x144040/0x144734，離線 exe 只認這四個)：
    ///   UI/ITEM2D_PACK_IN_SHOP  商城限定圖 (背景卡 2xxxxx 在商城畫這套)
    ///   UI/ITEM2D_PACK          一般道具/藥水/背包圖 (覆蓋率最高)
    ///   UI/ITEM2D_PACKUSE       使用中/他人身上
    ///   UI/MATCHITEMS           寵物圖 (DRESS: 1000001 → 1000001.an) —— 寵物不上架，這裡用不到
    /// 3D 的部分：道具/禮盒在 <c>DAOJU/</c>(寵物與寵物頭飾在 <c>PETAVATAR/</c>，同樣因不上架而未用)。
    /// (⚠️ <c>Datas/ITEM2D</c> 不是商品圖來源 —— exe 裡根本沒有這個路徑字串，它是信紙/星座卡。)
    /// </summary>
    public static class DressCatalog
    {
        private static Dictionary<int, string> _dress;   // modelId → 資源檔名 (含拼音,如 100000_xiaolaba.an)

        // 2D 圖示資料夾 (優先序:商城限定 → 一般 → 使用中)。UI/MATCHITEMS 是寵物圖 —— 寵物不上架 → 不掃。
        private static readonly string[] IconDirs =
        {
            "UI/ITEM2D_PACK_IN_SHOP", "UI/ITEM2D_PACK", "UI/ITEM2D_PACKUSE",
        };
        // 3D 模型資料夾 (道具/禮盒)
        private static readonly string[] MeshDirs = { "DAOJU" };

        public static void Reset() { _dress = null; _dirFiles.Clear(); }

        /// <summary>載入到的對照筆數 (診斷用)。</summary>
        public static int Count => Table().Count;

        private static Dictionary<int, string> Table()
        {
            if (_dress != null) return _dress;
            _dress = new Dictionary<int, string>();
            foreach (var f in new[] { "DRESS.TXT", "PETDRESS.TXT" })   // PETDRESS 疊在 DRESS 之上 (寵物部件)
            {
                try
                {
                    var p = Path.Combine(SdoExtracted.Root, f);
                    if (File.Exists(p)) DressTable.Parse(File.ReadAllText(p), _dress);
                }
                catch (Exception e) { Debug.LogWarning("[shop] " + f + " 讀取失敗: " + e.Message); }
            }
            if (_dress.Count == 0) Debug.LogWarning("[shop] DRESS.TXT 找不到 → 非衣服商品都沒有圖 (data root: " + SdoExtracted.Root + ")");
            return _dress;
        }

        /// <summary>該 modelId 的官方資源檔名 (禮包會自動借用寫死的禮盒道具)。沒有 → null。</summary>
        public static string Resource(int modelId)
        {
            var t = Table();
            if (t.TryGetValue(modelId, out var r)) return r;
            int proxy = DressTable.GiftPackProxyModelId(modelId);       // 禮包:官方按區間借禮盒模型
            return proxy >= 0 && t.TryGetValue(proxy, out var pr) ? pr : null;
        }

        /// <summary>2D 圖示的 .an 完整路徑 (該商品官方指定畫 3D/貼圖 → null)。
        ///
        /// DRESS.TXT 的檔名對不上磁碟的兩種情形 (實測 37 筆商品「沒有圖」都是這兩種)：
        ///   ① 表寫 <c>101241_fantuan41.an</c>,磁碟上是 <c>101241_FANTUAN41_.AN</c> (多一個底線的變體;
        ///      同資料夾另有 101241_FANTUAN41.DDS/.PNG,只有 .AN 是底線版)。
        ///   ② 表裡**根本沒這筆** —— 1120000 神奇大力丸 / 1120001-2 宝宝培育药水 / 1120003 大草莓蛋糕 /
        ///      1120004 超级棒棒糖 / 1120005 奇妙冰激凌 (7 碼 modelId,DRESS.TXT 只收到 6 碼那批),
        ///      但 UI/ITEM2D_PACK 裡有 <c>1120000_SHENQIDALIWAN.AN</c> 等一整組。
        /// → 官方檔名查不到就退而用「<c>{modelId}_*.an</c>」在同一批資料夾撿 (id 後面一定接底線,不會跨 id 誤撿)。</summary>
        public static string IconPath(int modelId)
        {
            var res = Resource(modelId);
            var kind = DressTable.KindOf(res);
            if (kind == DressResourceKind.Mesh || kind == DressResourceKind.Texture) return null;   // 官方說它畫 3D → 不撿 2D 圖
            return (kind == DressResourceKind.Icon ? FindIn(IconDirs, res, DressResourceKind.Icon) : null)
                   ?? FindByPrefix(IconDirs, modelId, ".an");
        }

        /// <summary>3D 模型的 data-root 相對路徑 (如 <c>DAOJU/100400_LIHE.MSH</c> 禮盒)。不是 .msh → null。</summary>
        public static string MeshRel(int modelId)
        {
            var full = FindIn(MeshDirs, Resource(modelId), DressResourceKind.Mesh);
            return full == null ? null : RelOf(full);
        }

        private static string FindIn(string[] dirs, string resource, DressResourceKind want)
        {
            if (DressTable.KindOf(resource) != want) return null;
            foreach (var d in dirs)
            {
                try
                {
                    var p = Path.Combine(SdoExtracted.Root, d.Replace('/', Path.DirectorySeparatorChar), resource);
                    if (File.Exists(p)) return p;   // NTFS 不分大小寫 → 表裡的小寫名直接對得上磁碟的大寫檔名
                }
                catch { }
            }
            return null;
        }

        // 「{modelId}_*.ext」撿檔 (官方檔名查不到時的後備,見 IconPath)。資料夾清單快取起來:ITEM2D_PACK 有 2000+ 檔,
        // 每格商品都掃一次目錄會拖慢捲動 (見 [[sdo-avatar-asset-io-cost]] 那條:商城的成本在冷讀檔)。
        private static readonly Dictionary<string, string[]> _dirFiles = new Dictionary<string, string[]>();

        private static string[] FilesIn(string relDir)
        {
            if (_dirFiles.TryGetValue(relDir, out var f)) return f;
            try
            {
                var d = Path.Combine(SdoExtracted.Root, relDir.Replace('/', Path.DirectorySeparatorChar));
                f = Directory.Exists(d) ? Directory.GetFiles(d) : Array.Empty<string>();
            }
            catch { f = Array.Empty<string>(); }
            _dirFiles[relDir] = f;
            return f;
        }

        private static string FindByPrefix(string[] dirs, int modelId, string ext)
        {
            string prefix = modelId.ToString() + "_";
            foreach (var d in dirs)
            {
                string best = null;
                foreach (var full in FilesIn(d))
                {
                    var name = Path.GetFileName(full);
                    if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) continue;
                    if (best == null || string.CompareOrdinal(name, Path.GetFileName(best)) < 0) best = full;   // 同 id 多檔 → 取名字最小的,結果穩定
                }
                if (best != null) return best;
            }
            return null;
        }

        private static string RelOf(string fullPath)
        {
            try
            {
                var root = SdoExtracted.Root;
                if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return fullPath.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
            }
            catch { }
            return fullPath;
        }
    }
}
