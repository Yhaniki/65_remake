namespace Sdo.UI.Services
{
    /// <summary>
    /// 頭貼下緣那條「READY / HOST」徽章要畫 .an 的**哪一幀**。
    ///
    /// 官方把兩種徽章各做了四種顏色,分別放在兩個四幀的 .an 裡:
    ///   • <c>Room66.an</c>(charready0..5 的背景)= a06/a07/a08/a09 —— READY
    ///   • <c>master.an</c> (master0..5 的背景) = b06/b07/b08/b09 —— HOST
    /// 兩組的幀序**完全一樣**:白 → 橘 → 綠 → 藍,正好對上房間右側「組隊」四格的
    /// 自由(灰白) / A(橘) / B(綠) / C(藍)。所以選了不同隊的人,頭貼上的字就是自己那一隊的顏色;
    /// 沒選隊(自由)的人是白色。
    ///
    /// 官方就是這樣查的:反編譯的 <c>023_gameplay_00482340</c> 直接拿玩家記錄 +0x48 那個隊伍 byte
    /// 當 Room66/master 的幀索引,中間**沒有任何轉換**。官方 byte 的定義是 0=自由 1=A 2=B 3=C
    /// (<c>StateRoom_SetTeam_0047d450</c> 的四個 case),與 remake 的 <see cref="Sdo.Net.TeamTag"/>
    /// (0=A 1=B 2=C 3=自由)差一位 —— 這個檔做的就是那一位的換算。同一組幀序也用在名字底下的
    /// <c>Team.an</c> 名牌(第 0 幀是 1×1 空白 = 沒選隊)。
    ///
    /// 這裡只做「隊伍 → 幀」這一步,與 Unity 無關 → 可單元測試(見 RoomBadgeFramesTests)。
    /// </summary>
    public static class RoomBadgeFrames
    {
        /// <summary>Room66.an / master.an 各有幾幀。</summary>
        public const int FrameCount = 4;

        /// <summary>
        /// <paramref name="team"/> = <c>Sdo.Net.TeamTag</c>(0=A 1=B 2=C 3=自由)→ 幀索引 0..3。
        ///
        /// 自由(3)與任何超出範圍的值都回 0(白色)—— 「沒選隊」與「值壞掉」在畫面上都應該是中性的白,
        /// 而不是隨便挑一隊的顏色去誤導別人。
        /// </summary>
        public static int ForTeam(int team)
            => team >= 0 && team <= 2 ? team + 1 : 0;
    }
}
