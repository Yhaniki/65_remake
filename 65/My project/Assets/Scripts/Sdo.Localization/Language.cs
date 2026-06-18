namespace Sdo.Localization
{
    /// <summary>Supported UI languages. Codes map to StreamingAssets/Localization/&lt;code&gt;.json.</summary>
    public enum Language
    {
        English,
        TraditionalChinese,
        SimplifiedChinese,
        Japanese,
    }

    public static class LanguageInfo
    {
        public static readonly Language[] All =
        {
            Language.TraditionalChinese,
            Language.SimplifiedChinese,
            Language.English,
            Language.Japanese,
        };

        /// <summary>BCP-47-ish code used for the json filename.</summary>
        public static string Code(Language l) => l switch
        {
            Language.English => "en",
            Language.TraditionalChinese => "zh-TW",
            Language.SimplifiedChinese => "zh-Hans",
            Language.Japanese => "ja",
            _ => "en",
        };

        /// <summary>.NET culture used for number/date formatting.</summary>
        public static string Culture(Language l) => l switch
        {
            Language.English => "en-US",
            Language.TraditionalChinese => "zh-TW",
            Language.SimplifiedChinese => "zh-CN",
            Language.Japanese => "ja-JP",
            _ => "en-US",
        };

        public static Language FromCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return Language.TraditionalChinese;
            switch (code.Trim().ToLowerInvariant())
            {
                case "en": case "en-us": return Language.English;
                case "zh-tw": case "zh-hant": case "zh": return Language.TraditionalChinese;
                case "zh-hans": case "zh-cn": return Language.SimplifiedChinese;
                case "ja": case "ja-jp": return Language.Japanese;
                default: return Language.TraditionalChinese;
            }
        }
    }
}
