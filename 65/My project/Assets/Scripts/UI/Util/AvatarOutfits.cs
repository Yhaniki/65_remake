using Sdo.Game;
using Sdo.Settings;
using Sdo.UI.Core;

namespace Sdo.UI.Util
{
    /// <summary>
    /// 「本機這個人現在穿什麼、什麼身材」—— 餵給 <see cref="GenderPreview3D"/> 的那兩個參數。
    /// 大廳左側那尊與個人資料視窗那尊都走這裡,兩邊看到的自己才會是同一套穿搭。
    /// (原本是 <c>LobbyScreen</c> 的兩個私有方法,個人資料視窗也要用 → 抽出來共用。)
    /// </summary>
    public static class AvatarOutfits
    {
        /// <summary>
        /// 取某性別對應 profile(女 00000000 / 男 00000001)的「實際穿戴」部位;找不到 → null(用預設整套)。
        /// 從 id-based equippedItems 經 catalog 現算(含合成的翅膀/表情/項鍊),而不是讀可能過時的
        /// equippedParts 快取 —— 與選角色畫面同一條路,兩邊看到的自己才會一樣。
        /// </summary>
        public static string[] PartsForGender(int gender)
        {
            string id = ProfileManager.SeededIdForGender(gender);
            foreach (var p in ProfileManager.List())
                if (p != null && p.id == id)
                    return WardrobeStore.ResolveEquippedParts(p, gender, cid => AvatarItemCatalog.Instance.ById(cid));
            return null;
        }

        /// <summary>取某性別對應 profile 自己的體型(胖瘦)index 0..4;找不到 → 0(瘦)。</summary>
        public static int BodyIndexForGender(int gender)
        {
            string id = ProfileManager.SeededIdForGender(gender);
            foreach (var p in ProfileManager.List())
                if (p != null && p.id == id)
                    return p.bodyShapeIndex;
            return 0;
        }
    }
}
