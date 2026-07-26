using System.Collections.Generic;

namespace Sdo.Game
{
    /// <summary>
    /// 場景本體(SCENE.MSH)裡「逐字閃爍的霓虹招牌」。每個字是 MSH 的一個獨立材質/range,執行期另外載一張
    /// 同名少一條底線的 DDS,閃爍就是兩張在材質槽上對調。
    ///
    /// 資料出自官方 StageScene_InitLightPlacements_004b1f90 呼叫的兩張 (材質名, 替換貼圖) 指標表:
    ///   0x00588240 共 8 對 → 招牌 A「LA MAISON」(第二個 A 因檔名撞號叫 aa)
    ///   0x00588280 共 9 對 → 招牌 B「SN❄WFLAKE」(bt1 不是字母,渲染出來是一顆雪花星)
    /// 表序 = 招牌上的閱讀順序,逐字掃就照這個順序跑,不能重排。
    ///
    /// ★ 每一對的第一個字串是 **MSH 材質名,也就是「亮」的那張**;第二個才是換上去的「暗」版。
    ///   (實測 17/17 皆如此:L_.DDS alpha 加權亮度 17.66 vs L.DDS 2.12。)
    /// </summary>
    public static class SceneNeonSignCatalog
    {
        /// <summary>一面招牌:照閱讀順序排的 (亮貼圖/材質名, 暗貼圖) 對。</summary>
        public sealed class Sign
        {
            public readonly string[] LitDds;    // = SCENE.MSH 的材質名
            public readonly string[] DarkDds;
            public Sign(string[] lit, string[] dark) { LitDds = lit; DarkDds = dark; }
            public int Length => LitDds.Length;
        }

        private static Sign Pair(params string[] litNames)
        {
            var dark = new string[litNames.Length];
            for (int i = 0; i < litNames.Length; i++)
            {
                // 官方的第二張就是同名去掉底線:"l_.dds" → "l.dds"。逐對核對過 17/17 都成立,
                // 所以用規則產生而不是再抄一份 17 個字串(少一份會抄錯的地方)。
                int dot = litNames[i].LastIndexOf('.');
                string stem = dot > 0 ? litNames[i].Substring(0, dot) : litNames[i];
                string ext = dot > 0 ? litNames[i].Substring(dot) : ".dds";
                dark[i] = (stem.EndsWith("_") ? stem.Substring(0, stem.Length - 1) : stem) + ext;
            }
            return new Sign(litNames, dark);
        }

        private static readonly Dictionary<string, Sign[]> ByFolder = new Dictionary<string, Sign[]>
        {
            ["SCN0001"] = new[]
            {
                // 招牌 A「LA MAISON」— 8 塊共面四邊形,沿 +X 由左到右
                Pair("l_.dds", "aa_.dds", "m_.dds", "a_.dds", "i_.dds", "s_.dds", "o_.dds", "n_.dds"),
                // 招牌 B「SN❄WFLAKE」— 9 塊傾斜四邊形,沿 −Z 排列;bt1 是雪花不是字母
                Pair("s1_.dds", "n1_.dds", "bt1_.dds", "w1_.dds", "f1_.dds", "l1_.dds", "a1_.dds", "k1_.dds", "e1_.dds"),
            },
        };

        private static readonly Sign[] Empty = new Sign[0];

        public static IReadOnlyList<Sign> ForFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return Empty;
            return ByFolder.TryGetValue(folder.ToUpperInvariant(), out var s) ? s : Empty;
        }
    }
}
