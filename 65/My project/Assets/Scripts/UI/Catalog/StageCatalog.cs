using System.Collections.Generic;
using Sdo.Localization;

namespace Sdo.UI.Catalog
{
    public sealed class StageInfo
    {
        public int Id;
        public string Folder;     // -> Step1Game scenePath = "SCENE/" + Folder
        public string NameZh;     // zh-TW fallback (the real EXE name; used if no key or the table lacks it)
        public string NameKey;    // localization key (stage.name.<id>); resolved live so language switches apply

        public StageInfo(int id, string folder, string nameZh, string nameKey = null)
        { Id = id; Folder = folder; NameZh = nameZh; NameKey = nameKey; }

        /// <summary>Localized display name; falls back to the zh-TW name when no key or the key is unresolved.</summary>
        public string DisplayName
        {
            get
            {
                if (string.IsNullOrEmpty(NameKey)) return NameZh;
                var v = LocalizationManager.Get(NameKey);
                return (string.IsNullOrEmpty(v) || v == "[" + NameKey + "]") ? NameZh : v;
            }
        }
    }

    /// <summary>
    /// The selectable 3D stages. Ids/folders mirror docs/reverse-engineering/SDO_SCENE_MAPOBJ_TABLE.json
    /// (40 scenes; ids 34/36 have no folder and are skipped). Scene names for ids 0..30 are the real
    /// ones from the original EXE string table (sdo_stand_alone.exe .data @ VA 0x005861c8, indexed by
    /// scene id; e.g. id 9 = 黑白舞會). The ROOMDLG scene selector shows exactly ids 0..30 (thumbnail
    /// Scene{id+1}.an); the special rooms 31..39 are entered via other flows, not the selector.
    /// </summary>
    public static class StageCatalog
    {
        public static readonly IReadOnlyList<StageInfo> Stages = Build();

        /// <summary>Highest scene id that appears in the ROOMDLG scene selector (ids 0..30).</summary>
        public const int MaxSelectableId = 30;

        public const int DefaultId = 9;   // SCN0009, matches Step1Game's default scene

        public static StageInfo Get(int id)
        {
            foreach (var s in Stages) if (s.Id == id) return s;
            return Default;
        }

        public static StageInfo Default => Get(DefaultId) ?? Stages[0];

        private static List<StageInfo> Build()
        {
            // zh-TW fallback names for ids 0..30 (the real EXE name table). Kept LOCAL (not a static field) so the
            // static init can never read it before it's assigned — that ordering hazard previously threw a
            // TypeInitializationException (NRE) at boot when SongSelectScreen first touched StageCatalog.Stages.
            var sceneNames = new[]
            {
                "步行街", "新天地", "車庫", "舞台", "海灘", "聖誕夜", "遊樂場", "極地花園", "埃及古墓", "黑白舞會",
                "花車", "舞林大會", "足球場 (日)", "足球場 (夜)", "海底", "魔法屋", "繁華街道", "都市地鐵", "豪華郵輪", "舞鬥競技場",
                "地鐵驛站", "激舞酒吧", "墓地", "教室", "雪景", "春天", "籃球場", "NARNIA", "北京之夜", "飛機場", "卡通公路",
            };
            var list = new List<StageInfo>();
            for (int i = 0; i <= 30; i++)
                list.Add(new StageInfo(i, $"SCN{i:0000}", sceneNames[i], $"stage.name.{i}"));
            list.Add(new StageInfo(31, "MERRYROOMA", "婚禮房 A", "stage.name.31"));
            list.Add(new StageInfo(32, "MERRYROOMB", "婚禮房 B", "stage.name.32"));
            list.Add(new StageInfo(33, "MERRYROOMC", "婚禮房 C", "stage.name.33"));
            list.Add(new StageInfo(35, "SCNMYHOUSE", "我的家", "stage.name.35"));
            list.Add(new StageInfo(37, "SCNROOM", "個人房", "stage.name.37"));
            list.Add(new StageInfo(38, "SCNMERRYROOM", "婚禮大廳", "stage.name.38"));
            list.Add(new StageInfo(39, "SCNROOM_NIGHT", "個人房（夜）", "stage.name.39"));
            return list;
        }
    }
}
