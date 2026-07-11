#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using Sdo.Game;

namespace Sdo.EditorTools
{
    /// <summary>
    /// Editor-only helper: dump the 商城 avatar catalog (names + prices, decoded from iteminfo.dat) to the Console.
    /// Use it to confirm the whole pipeline — iteminfo.dat located → 156-byte decode → GBK Simplified-Chinese names →
    /// model-file coverage — actually works in THIS Unity runtime, before the ScreenShop UI exists. Menu: Tools/Shop.
    /// </summary>
    public static class ShopCatalogMenu
    {
        [MenuItem("Tools/Shop/Dump Catalog")]
        public static void DumpCatalog()
        {
            var cat = AvatarItemCatalog.Load();
            if (cat.Count == 0)
            {
                Debug.LogWarning("[shop] catalog empty — iteminfo.dat not found under the data tree (see AvatarItemCatalog.ResolveIteminfoPath).");
                return;
            }
            cat.DumpToLog(20);
        }
    }
}
#endif
