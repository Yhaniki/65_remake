using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 官方的**隊伍色表**,全案唯一的一份。
    ///
    /// 來源不是猜的,是 EXE 裡那張四筆的表 <c>DAT_00586274</c>(檔案位移 0x186274),官方拿舞者/座位上的
    /// 隊伍 byte 直接當索引去查,再寫進名字物件的文字色:
    ///
    ///     官方 byte 0 = 沒隊伍 → 0xFFFFFECF 乳白
    ///     官方 byte 1 = A 隊   → 0xFFFFA500 橘
    ///     官方 byte 2 = B 隊   → 0xFF4FE400 綠
    ///     官方 byte 3 = C 隊   → 0xFF53C8FF 青藍
    ///
    /// ⚠️ 官方的隊伍編號(0=無 1=A 2=B 3=C)與 remake 的 <see cref="Sdo.Net.TeamTag"/>(0=A 1=B 2=C 3=自由)
    /// **不一樣**,所以這裡用 TeamTag 收參數、自己對到正確的那一格。同一個換算也出現在房間的
    /// <c>RoomBadgeFrames</c>(Team.an / Room66.an / master.an 的幀索引就是官方那個 byte)。
    ///
    /// 「沒隊伍」刻意**不**放進來:那一格的官方值(255,254,207)與 remake 既有的
    /// <see cref="TextStyles.FaceCream"/>(250,252,214,從官方截圖取樣)是同一個乳白,而 FaceCream
    /// 同時是房間頭上名字的顏色 —— 為了一個看不出來的差別去動它,只會讓沒組隊的畫面無謂地變一次。
    /// 所以 <see cref="TryFor"/> 對「自由」回 false,呼叫端維持原本的中性外觀。
    ///
    /// 放在 Sdo.Game 而不是 Sdo.UI:遊戲端(頭上名字)也要用,而 Sdo.Game 不能反過來參照 Sdo.UI。
    /// </summary>
    public static class TeamColors
    {
        /// <summary>A 隊 —— 橘。官方 0xFFFFA500。</summary>
        public static readonly Color A = new Color32(0xFF, 0xA5, 0x00, 0xFF);

        /// <summary>B 隊 —— 綠。官方 0xFF4FE400。</summary>
        public static readonly Color B = new Color32(0x4F, 0xE4, 0x00, 0xFF);

        /// <summary>C 隊 —— 青藍。官方 0xFF53C8FF。</summary>
        public static readonly Color C = new Color32(0x53, 0xC8, 0xFF, 0xFF);

        /// <summary>隊伍數(A/B/C)。「自由」不是一隊,所以不算在裡面。</summary>
        public const int TeamCount = 3;

        /// <summary>「自由」的隊伍值(<see cref="Sdo.Net.TeamTag.Free"/>)。</summary>
        public const int Free = 3;

        /// <summary>
        /// <paramref name="team"/>(<see cref="Sdo.Net.TeamTag"/>:0=A 1=B 2=C 3=自由)那一隊的顏色。
        ///
        /// 回 false = **沒有隊伍色**(自由,或值壞掉)—— 呼叫端要維持原本的中性外觀
        /// (名牌不畫、名字用乳白、腳下不加彩色光暈),不要自己挑一個顏色頂替:
        /// 那會讓沒組隊的人看起來像屬於某一隊。
        /// </summary>
        public static bool TryFor(int team, out Color color)
        {
            switch (team)
            {
                case 0: color = A; return true;
                case 1: color = B; return true;
                case 2: color = C; return true;
                default: color = Color.white; return false;
            }
        }

        /// <summary>這個隊伍值算「有選隊」嗎(0..2 = A/B/C)。</summary>
        public static bool IsTeam(int team) => team >= 0 && team < TeamCount;
    }
}
