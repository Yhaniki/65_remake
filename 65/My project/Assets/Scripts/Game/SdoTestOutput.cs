#if UNITY_EDITOR
using System.IO;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 測試／擷取用截圖的統一落點：<c>&lt;repo&gt;/test-output/&lt;分類&gt;/…</c>。
    ///
    /// 以前每個 capture 測試各自寫死 "H:/65_remake/xxx.png"，跑完一輪 repo 根目錄就散一地 PNG，
    /// 而且在 worktree 裡跑會把圖寫回主 repo。這裡改成從 <see cref="Application.dataPath"/>
    /// （= &lt;repo&gt;/65/My project/Assets）往上推出 repo 根，各 worktree 各自收在自己的 test-output/ 下。
    ///
    /// 只在 Editor 下編譯：這些路徑純粹是開發期診斷用，不該進打包版。
    /// </summary>
    public static class SdoTestOutput
    {
        /// <summary>repo 根底下的資料夾名（.gitignore 用同一個名字整包忽略）。</summary>
        public const string FolderName = "test-output";

        /// <summary>&lt;repo&gt;/test-output（不保證存在，取用請走 <see cref="Dir"/>／<see cref="File"/>）。</summary>
        public static string Root
        {
            get
            {
                // Application.dataPath = <repo>/65/My project/Assets → 往上三層就是 repo 根
                string repo = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..", "..", ".."));
                return (repo.Replace('\\', '/').TrimEnd('/')) + "/" + FolderName;
            }
        }

        /// <summary>取得（並建立）某個分類的輸出資料夾，例如 <c>Dir("combo")</c>。</summary>
        public static string Dir(string category)
        {
            string d = string.IsNullOrEmpty(category) ? Root : Root + "/" + category.Trim('/', '\\');
            Directory.CreateDirectory(d);
            return d;
        }

        /// <summary>某分類底下的完整檔案路徑；沿路的資料夾會先建好。</summary>
        public static string File(string category, string fileName) => Dir(category) + "/" + fileName;
    }
}
#endif
