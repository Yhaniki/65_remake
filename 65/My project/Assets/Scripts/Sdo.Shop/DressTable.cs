using System;
using System.Collections.Generic;

namespace Sdo.Shop
{
    /// <summary>一筆商品資源的種類 (由 DRESS.TXT 的副檔名決定)。</summary>
    public enum DressResourceKind { Unknown, Icon, Mesh, Texture }

    /// <summary>
    /// <c>DRESS.TXT</c> / <c>PETDRESS.TXT</c> 的解析 —— 官方「modelId → 資源檔名」的**唯一**對照表。純字串邏輯 (無 I/O)。
    ///
    /// 逆向依據 (離線 exe <c>sdo_stand_alone.exe.c</c>): 讀檔迴圈 :122092 用 <c>sscanf(line,"%d %s")</c> 把 dress.txt
    /// 建成 <c>map&lt;int,{檔名,category,…}&gt;</c>；查表 :44360 <c>FUN_004312e0(&amp;id)</c> 回傳 value，<c>+0x0c</c> 就是檔名。
    /// 商品格 (SHOP.XML 的 <c>AvtShow avtnormal_N</c>) 依這個檔名的副檔名決定畫 2D 還是 3D (:44412 FUN_004313c0)：
    ///   .an  → 2D 圖示 (UI/ITEM2D_PACK*、寵物在 UI/MATCHITEMS)
    ///   .msh → 3D 模型 (道具 DAOJU/、寵物 PETAVATAR/)
    ///   .dds → 只有貼圖 (寵物衣服:全部共用同一件 coat mesh、只換貼圖 —— PETDRESS 的 1041000 那筆就是那個共用 mesh)
    ///
    /// ⚠️ 檔名**不是** {modelId} 加零補齊，帶著漢語拼音 (<c>100000 → 100000_xiaolaba.an</c>)，客戶端也是查表才知道 →
    /// 不查這張表就找不到圖 (這是「商城道具沒有圖」的真正原因，不是素材缺)。
    /// 藥水也在表裡就解決了：<c>100031(标准药水) → 100202_bianxingshui.an</c> (借變形水的美術，官方就這樣)。
    /// </summary>
    public static class DressTable
    {
        /// <summary>解析 <c>DRESS.TXT</c>/<c>PETDRESS.TXT</c>：每行 <c>{id}{空白}{檔名}</c>；空行/壞行略過。
        /// 後出現的同 id 覆蓋前面的 (讓 PETDRESS 疊在 DRESS 上)。</summary>
        public static Dictionary<int, string> Parse(string text, Dictionary<int, string> into = null)
        {
            var map = into ?? new Dictionary<int, string>();
            if (string.IsNullOrEmpty(text)) return map;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                int i = 0;
                while (i < line.Length && line[i] >= '0' && line[i] <= '9') i++;
                if (i == 0) continue;                                   // 不是數字開頭
                if (!int.TryParse(line.Substring(0, i), out var id)) continue;
                var rest = line.Substring(i).Trim();
                if (rest.Length == 0) continue;
                int sp = rest.IndexOfAny(new[] { ' ', '\t' });          // 檔名後面若還有欄位就切掉
                if (sp > 0) rest = rest.Substring(0, sp);
                map[id] = rest;
            }
            return map;
        }

        /// <summary>資源種類 (由副檔名判)。</summary>
        public static DressResourceKind KindOf(string resource)
        {
            if (string.IsNullOrEmpty(resource)) return DressResourceKind.Unknown;
            if (resource.EndsWith(".an", StringComparison.OrdinalIgnoreCase)) return DressResourceKind.Icon;
            if (resource.EndsWith(".msh", StringComparison.OrdinalIgnoreCase)) return DressResourceKind.Mesh;
            if (resource.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) return DressResourceKind.Texture;
            return DressResourceKind.Unknown;
        }

        /// <summary>禮包 (cat 14000, modelId 6xxxxx) 在 DRESS.TXT **沒有自己的資源** —— 官方是按 modelId 區間**寫死**
        /// 借一個「禮盒」道具的模型來畫 (sdo_stand_alone.exe.c :44586-44637)。回傳那個借用的道具 modelId，沒有 → -1。</summary>
        public static int GiftPackProxyModelId(int modelId)
        {
            if (modelId >= 600000 && modelId <= 619999) return 100400;   // 100400_lihe.msh      禮盒
            if (modelId >= 620000 && modelId <= 629999) return 100796;   // 100796_hongbao       紅包
            if (modelId >= 630000 && modelId <= 639999) return 100798;   // 100798_jiazukuochongka150
            if (modelId == 640001 || modelId == 640002) return 100881;   // 100881_guoniandalibao.msh 過年大禮包
            return -1;
        }

        /// <summary>寵物衣服 (cat 43000) 共用的那件 coat mesh 在 PETDRESS 的 key (1041000 → 1040000_all_coat_.msh)。
        /// 各件衣服自己只有一張 <c>1040xxx_all_coat.dds</c> 貼圖 → 畫的時候是「同一件 mesh + 換貼圖」。</summary>
        public const int PetCoatMeshKey = 1041000;

        /// <summary>這個 modelId 是寵物衣服嗎 (只有貼圖、要借共用 mesh)。</summary>
        public static bool IsPetClothes(int modelId) => modelId >= 1040000 && modelId <= 1040999;
    }
}
