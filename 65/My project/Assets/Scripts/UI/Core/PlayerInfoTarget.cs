using Sdo.Settings;

namespace Sdo.UI.Core
{
    /// <summary>
    /// 要在「個人資訊」面板 (PlayerInformationDlg) 上顯示的那個人。房間右鍵點到誰就填誰。
    ///
    /// 官方的這些欄位是伺服器下發的；離線版本機玩家取自 profile.json，房間裡的其他人取自
    /// <see cref="Sdo.UI.Services.MockPlayerService"/> 的假名單 + 由 id 決定性生成的戰績
    /// （<see cref="MockPlayerStats"/>）。將來接上線就把這個結構改成從封包填。
    /// </summary>
    public sealed class PlayerInfoTarget
    {
        public string Name = "";
        public int Level = 1;
        public bool Male;
        public string[] AvatarParts;     // 3D 預覽要穿的部位（null → 預設裸裝）
        public PlayerStats Stats = new PlayerStats();
        public bool IsSelf;              // 看自己 vs 看別人（官方據此決定底部按鈕；離線只有「確定」）
    }
}
