namespace Sdo.Net
{
    /// <summary>
    /// 這一局所有「隨機」項目**拍板定案後**的值。由房主在按下開始的那一刻抽好送上來，
    /// server 驗範圍後原樣 echo 給所有參與者 —— **client 一律只信 server echo 回來的這份,
    /// 絕不用自己算的值**。
    ///
    /// 為什麼是「host 抽、server 驗、server echo」這個組合:
    ///
    ///   • **不能讓各 client 自己抽** —— 那是使用者明確要求要解決的問題(「就算是 Room 裡面
    ///     隨機場景，也要隨機到一樣的」)。各自抽就各自看到不同場景。
    ///
    ///   • **不能讓 server 抽** —— server 編不到 <c>StageCatalog</c>(它在 Sdo.UI，有 UnityEngine
    ///     依賴),也沒有歌曲目錄。硬要做就得把整份場景表與歌單複製到 server，那是兩份真相。
    ///
    ///   • **所以 host 抽,但 server 是最終權威** —— server 驗每個欄位在合法範圍內才 echo。
    ///     一個改過的 host 不能靠這個把別人送進不存在的場景。
    ///
    /// 現有的兩處「各自抽」邏輯在線上都必須關掉:
    ///   <c>FrontendApp.StartGameplay</c> 的每局重抽隨機難度、
    ///   <c>SongSelectScreen.OnConfirm</c> 的按確認時抽場景。
    /// </summary>
    public sealed class NetResolvedRound
    {
        /// <summary>
        /// 這一局實際要用的場景 id。房間設定是「隨機場景」時由 host 抽;指定場景時就是那個值。
        /// server 驗 <c>0 &lt;= sceneId &lt;= NetLimits.MaxSceneId</c>。
        /// </summary>
        public int SceneId;

        /// <summary>
        /// 個人隊形的型別(0..2,對應 <c>FormationCatalog</c> 的三張表)。
        /// 房間設定的「隨機隊形」(<c>GameSession.Formation == 3</c>)由 host 抽成 0..2。
        /// </summary>
        public int FormationType;

        /// <summary>
        /// 組隊站位版型。<see cref="TeamLayout.None"/>(-1)= 不組隊,走個人隊形。
        /// 由 host 依參與者的隊伍分佈算好(<see cref="TeamLayoutRules.TryLayoutFor"/>),
        /// server 會用自己手上的參與者名單**重新驗一次** —— 不能只信 host 算的。
        /// </summary>
        public TeamLayout TeamLayout = TeamLayout.None;

        /// <summary>
        /// 隨機難度抽出的實際歌曲。**只有房間選的是「隨機難度」時才有值**;
        /// 一般指定歌曲時是 null,參與者直接用 <c>roomState.song</c>。
        ///
        /// (隨機難度的候選只在官方歌範圍內，所以這條路徑不會有缺歌問題。)
        /// </summary>
        public NetSongRef RandomSong;

        /// <summary>是隨機難度局嗎?</summary>
        public bool IsRandomSong => RandomSong != null;

        /// <summary>組隊模式嗎?(協定上就是用 teamLayout 的正負號判斷)</summary>
        public bool IsTeamMode => (int)TeamLayout >= 0;

        // ---- codec ----

        public JObj Encode()
        {
            var o = JObj.New()
                .Int("sceneId", SceneId)
                .Int("formationType", FormationType)
                .Int("teamLayout", (int)TeamLayout);
            if (RandomSong != null) o.Put("randomSong", RandomSong.Encode());
            return o;
        }

        /// <summary>
        /// 解析 + **範圍驗證**。回 false = 值不合法,server 應該回 error 而不是照著跑。
        /// </summary>
        public static bool TryDecode(object node, out NetResolvedRound r)
        {
            r = null;
            if (node == null) return false;

            var x = new NetResolvedRound();
            x.SceneId = NetJson.Int(node, "sceneId", -1);
            x.FormationType = NetJson.Int(node, "formationType", -1);

            int rawLayout = NetJson.Int(node, "teamLayout", (int)TeamLayout.None);

            // 🔴 範圍驗證 —— 這是「server 是最終權威」的具體體現。
            if (x.SceneId < 0 || x.SceneId > NetLimits.MaxSceneId) return false;
            if (x.FormationType < 0 || x.FormationType >= FormationTypeCount) return false;
            if (rawLayout < (int)TeamLayout.None || rawLayout > (int)TeamLayout.V2v2v2) return false;
            x.TeamLayout = (TeamLayout)rawLayout;

            // randomSong 是可選的;有帶就必須是合法的歌曲參照。
            var songNode = NetJson.Sub(node, "randomSong");
            if (songNode != null)
            {
                NetSongRef song;
                if (!NetSongRef.TryDecode(songNode, out song)) return false;
                x.RandomSong = song;
            }

            r = x;
            return true;
        }

        /// <summary>
        /// 個人隊形的型別數量。**必須等於 <c>Sdo.Game.FormationCatalog.TypeCount</c>** ——
        /// 官方的房間對話框只建 3 顆 FORMATION 按鈕。server 編不到那個類別(它用 Vector3),
        /// 所以在這裡複製一份,並用 EditMode 測試斷言兩邊相等。
        ///
        /// 注意 <c>GameSession.Formation</c> 有 **4** 個選項(0=基本 1=扇形 2=環線 **3=隨機**)——
        /// 3 不是第四種隊形,而是「隨機」的意思,由 host 抽成 0..2 之後才進到這裡。
        /// </summary>
        public const int FormationTypeCount = 3;
    }
}
