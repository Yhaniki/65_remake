using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 房間裡的一個「人」。掛在每個 3D avatar 的 root 上，附一個 BoxCollider 讓滑鼠 raycast 打得到
    /// —— 右鍵點他就開他的個人資訊/戰績面板（<c>RoomScreen.HandleRightClickPerson</c>）。
    ///
    /// 身分欄位（Id/DisplayName/Level）由 UI 層填：本機房主取自 profile.json，其他人是假名單
    /// （<c>Sdo.UI.Services.RoomOccupants</c>）。Sdo.Game 不認識 profile/假名單，所以這裡只是個資料袋。
    /// </summary>
    public sealed class RoomOccupant : MonoBehaviour
    {
        public int Slot;              // 0 = 本機房主；1-5 = 舞者；6-15 = 旁觀者 (RoomLayout)
        public bool IsLocal;
        public string Id = "";
        public string DisplayName = "";
        public int Level = 1;
        public bool Male;

        /// <summary>幫這個 avatar root 裝上可被 raycast 打到的人形碰撞盒（SDO 角色高約 62 單位）。</summary>
        public static RoomOccupant Attach(GameObject root, int layer, int slot, bool isLocal, bool male)
        {
            root.layer = layer;   // picking 用 layerMask 過濾，root 必須在場景層上
            var box = root.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, 32f, 0f);
            box.size = new Vector3(34f, 64f, 34f);

            var occ = root.AddComponent<RoomOccupant>();
            occ.Slot = slot;
            occ.IsLocal = isLocal;
            occ.Male = male;
            return occ;
        }
    }
}
