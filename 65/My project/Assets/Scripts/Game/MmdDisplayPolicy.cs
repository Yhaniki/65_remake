namespace Sdo.Game
{
    /// <summary>一隻角色身上該畫哪一具身體。</summary>
    public enum MmdSource
    {
        /// <summary>SDO 原本的穿搭（沒有 MMD，或設定不要）。</summary>
        Sdo,
        /// <summary>本機玩家自己選的那個模型。</summary>
        LocalModel,
        /// <summary>那個人自己宣告的模型（packId）。</summary>
        RemoteModel,
    }

    /// <summary>
    /// 「這一隻要顯示哪一具身體」的決策 —— 抽出來當純函式，因為它踩過一個很難從畫面上看出根因的 bug：
    /// <b>本機選的模型被套到了別人身上</b>。當時遠端角色與本機角色共用同一個「沒有 packId ⇒ 用設定裡選的那個」
    /// 回退路徑，於是同房的人沒穿 MMD（packId 空）時，就被畫成了我自己的模型。
    ///
    /// 現在這裡把兩件事分得死死的，而且它們是**兩個獨立的功能**：
    ///   • <b>我要不要用 MMD 模型</b> —— 看本機有沒有選模型（<c>RoomConfig.mmdModel</c>，「(不使用)」＝不用）。
    ///     沒有第二個總開關：選了就是要用。
    ///   • <b>我要不要看到別人的 MMD 模型</b> —— <c>RoomConfig.mmdShowOthers</c>。
    /// 兩者互不影響：可以自己維持 SDO 角色卻看得到別人的 MMD，也可以反過來。
    ///
    /// 遠端角色<b>永遠只可能</b>畫他自己宣告的那個模型；他沒宣告就是他的 SDO 穿搭，絕不回退到本機選的模型。
    /// </summary>
    public static class MmdDisplayPolicy
    {
        /// <param name="remote">這一隻是別人（不是本機玩家）嗎。</param>
        /// <param name="declaredPack">遠端角色的外觀宣告的模型 packId（空＝他沒穿 MMD）。</param>
        /// <param name="localModelSelected">本機設定裡選了一個裝得到的模型嗎。</param>
        /// <param name="showOthers">要顯示別人的 MMD 模型嗎（<c>mmdShowOthers</c>）。</param>
        public static MmdSource SourceFor(bool remote, string declaredPack, bool localModelSelected, bool showOthers)
        {
            if (remote)
                return showOthers && !string.IsNullOrEmpty(declaredPack) ? MmdSource.RemoteModel : MmdSource.Sdo;
            return localModelSelected ? MmdSource.LocalModel : MmdSource.Sdo;
        }
    }
}
