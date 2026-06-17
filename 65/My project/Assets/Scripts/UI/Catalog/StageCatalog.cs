using System.Collections.Generic;

namespace Sdo.UI.Catalog
{
    public sealed class StageInfo
    {
        public int Id;
        public string Folder;     // -> Step1Game scenePath = "SCENE/" + Folder
        public string NameZh;     // fallback display name (v1; no name data in the original table)

        public StageInfo(int id, string folder, string nameZh) { Id = id; Folder = folder; NameZh = nameZh; }
    }

    /// <summary>
    /// The selectable 3D stages. Ids/folders mirror docs/reverse-engineering/SDO_SCENE_MAPOBJ_TABLE.json
    /// (40 scenes; ids 34/36 have no folder and are skipped). Names are placeholders for v1.
    /// </summary>
    public static class StageCatalog
    {
        public static readonly IReadOnlyList<StageInfo> Stages = Build();

        public const int DefaultId = 9;   // SCN0009, matches Step1Game's default scene

        public static StageInfo Get(int id)
        {
            foreach (var s in Stages) if (s.Id == id) return s;
            return Default;
        }

        public static StageInfo Default => Get(DefaultId) ?? Stages[0];

        private static List<StageInfo> Build()
        {
            var list = new List<StageInfo>();
            for (int i = 0; i <= 30; i++)
                list.Add(new StageInfo(i, $"SCN{i:0000}", $"舞台 {i:00}"));
            list.Add(new StageInfo(31, "MERRYROOMA", "婚禮房 A"));
            list.Add(new StageInfo(32, "MERRYROOMB", "婚禮房 B"));
            list.Add(new StageInfo(33, "MERRYROOMC", "婚禮房 C"));
            list.Add(new StageInfo(35, "SCNMYHOUSE", "我的家"));
            list.Add(new StageInfo(37, "SCNROOM", "個人房"));
            list.Add(new StageInfo(38, "SCNMERRYROOM", "婚禮大廳"));
            list.Add(new StageInfo(39, "SCNROOM_NIGHT", "個人房（夜）"));
            return list;
        }
    }
}
