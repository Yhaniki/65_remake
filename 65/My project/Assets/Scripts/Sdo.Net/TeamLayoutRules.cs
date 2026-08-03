namespace Sdo.Net
{
    /// <summary>
    /// 組隊站位的版型。數值**必須與 <c>Sdo.Game.TeamFormationCatalog.Layout</c> 一致** ——
    /// 那邊才是座標表本體(逐字重製自 EXE 的 <c>0x582be8</c> / <c>0x582c78</c> / <c>0x582c30</c>),
    /// 但它帶 <c>Vector3</c> 所以 server 編不到。有一個 EditMode 測試斷言兩邊的數值對得上，防止漂移。
    /// </summary>
    public enum TeamLayout
    {
        /// <summary>不組隊 —— 走個人隊形(<c>FormationCatalog</c>)。</summary>
        None = -1,
        /// <summary>2 隊 × 2 人。</summary>
        V2v2 = 0,
        /// <summary>2 隊 × 3 人。</summary>
        V3v3 = 1,
        /// <summary>3 隊 × 2 人。</summary>
        V2v2v2 = 2,
    }

    /// <summary>
    /// 「這組人數湊得出合法的組隊站位嗎」的純規則。
    ///
    /// 🔴 **官方只有三張組隊座標表**:2v2、3v3、2v2v2。沒有 3v2、4v1、5 人 的站位資料。
    /// 所以湊不出來時**擋住不准開始遊戲**(<c>error{badTeams}</c>),而不是退回個人隊形 ——
    /// 退回會讓玩家以為分隊生效了卻看到單人站位，那是靜默的錯誤行為。
    ///
    /// 放在 <c>Sdo.Net</c> 而不是 <c>Sdo.Game</c>:server 也必須獨立驗這一條
    /// (client 端的預檢只是為了 UX —— 提早把「開始」鈕灰掉並說明原因)。
    /// </summary>
    public static class TeamLayoutRules
    {
        /// <summary>隊伍數量上限(A/B/C)。</summary>
        public const int MaxTeams = 3;

        /// <summary>
        /// **只有普通模式可以組隊**(<c>NetRoomSettings.GameMode</c>:0=自由 1=普通 2=ShowTime)。
        ///
        /// 自由模式是「各玩各的」——連難度都各挑各的(見 <c>FreeModeDifficulty</c>),更沒有隊伍的意義;
        /// ShowTime 是集氣表演,同樣不分隊。所以組隊只在普通模式成立。
        ///
        /// 這條規則 client 與 server **兩邊都要擋**:client 擋是為了說得出原因(按下去有話講),
        /// server 擋才是真的 —— 否則改過的 client 可以在自由模式下把大家分隊,開場就會走到組隊站位。
        /// </summary>
        public const int TeamGameMode = 1;

        /// <summary>這個模式可以組隊嗎。</summary>
        public static bool TeamsAllowedIn(int gameMode) => gameMode == TeamGameMode;

        /// <summary>
        /// 依每隊人數決定版型。
        ///
        /// 判定來源是反編譯的 <c>023_gameplay_00482340</c>:它就是數每隊人數然後選 mode
        /// (例如 team1==2 &amp;&amp; team2==2 &amp;&amp; team3==2 → 2v2v2)。
        ///
        /// 人數為 0 的隊伍視為不存在(6 人房只有 4 個人準備好時，C 隊就是空的)。
        /// </summary>
        /// <param name="a">A 隊人數。</param>
        /// <param name="b">B 隊人數。</param>
        /// <param name="c">C 隊人數。</param>
        /// <param name="layout">湊得出來時填版型;否則 <see cref="TeamLayout.None"/>。</param>
        /// <returns>湊得出合法版型才回 true。</returns>
        public static bool TryLayoutFor(int a, int b, int c, out TeamLayout layout)
        {
            layout = TeamLayout.None;
            if (a < 0 || b < 0 || c < 0) return false;

            // 有人的隊伍數,以及那些隊各有幾人。
            int teams = 0;
            int first = 0, second = 0, third = 0;
            if (a > 0) { teams++; first = a; }
            if (b > 0) { teams++; if (first == 0) first = b; else second = b; }
            if (c > 0) { teams++; if (first == 0) first = c; else if (second == 0) second = c; else third = c; }

            if (teams == 2)
            {
                if (first == 2 && second == 2) { layout = TeamLayout.V2v2; return true; }
                if (first == 3 && second == 3) { layout = TeamLayout.V3v3; return true; }
                return false;   // 3v2、4v1、1v1… 都沒有座標表
            }

            if (teams == 3)
            {
                if (first == 2 && second == 2 && third == 2) { layout = TeamLayout.V2v2v2; return true; }
                return false;   // 3v2v1 之類沒有座標表
            }

            // 0 隊(沒人)或 1 隊(全部同隊 = 不是對戰)都不算組隊。
            return false;
        }

        /// <summary>陣列版(索引 0..2 = A/B/C)。長度不是 3 一律回 false。</summary>
        public static bool TryLayoutFor(int[] teamCounts, out TeamLayout layout)
        {
            layout = TeamLayout.None;
            if (teamCounts == null || teamCounts.Length != MaxTeams) return false;
            return TryLayoutFor(teamCounts[0], teamCounts[1], teamCounts[2], out layout);
        }

        /// <summary>這個版型總共要幾個舞者?(2v2 → 4、3v3 → 6、2v2v2 → 6)</summary>
        public static int TotalDancers(TeamLayout layout)
        {
            switch (layout)
            {
                case TeamLayout.V2v2: return 4;
                case TeamLayout.V3v3: return 6;
                case TeamLayout.V2v2v2: return 6;
                default: return 0;
            }
        }

        /// <summary>這個版型有幾隊?(2v2 → 2、3v3 → 2、2v2v2 → 3)</summary>
        public static int TeamCount(TeamLayout layout)
        {
            switch (layout)
            {
                case TeamLayout.V2v2: return 2;
                case TeamLayout.V3v3: return 2;
                case TeamLayout.V2v2v2: return 3;
                default: return 0;
            }
        }

        /// <summary>
        /// 房主「一鍵分隊」時,這個人數能不能套用指定版型。
        /// (<c>assignTeams</c> 的 server 端驗證 —— 4 個人選 3v3 是不行的。)
        /// </summary>
        public static bool CanAssign(TeamLayout layout, int seatedPlayers)
            => layout != TeamLayout.None && TotalDancers(layout) == seatedPlayers;

        /// <summary>wire 字串 ↔ 版型。</summary>
        public static string ToWire(TeamLayout layout)
        {
            switch (layout)
            {
                case TeamLayout.V2v2: return "2v2";
                case TeamLayout.V3v3: return "3v3";
                case TeamLayout.V2v2v2: return "2v2v2";
                default: return "none";
            }
        }

        public static bool TryParseLayout(string w, out TeamLayout layout)
        {
            switch (w)
            {
                case "2v2": layout = TeamLayout.V2v2; return true;
                case "3v3": layout = TeamLayout.V3v3; return true;
                case "2v2v2": layout = TeamLayout.V2v2v2; return true;
                case "none": layout = TeamLayout.None; return true;
                default: layout = TeamLayout.None; return false;
            }
        }

        /// <summary>
        /// 房主一鍵分隊:依座位順序把人平均分配到各隊。
        /// 回傳長度 = <paramref name="seatedPlayers"/> 的陣列，元素是該位玩家的隊伍編號(0/1/2)。
        /// 人數與版型不符時回 null。
        ///
        /// 分配方式是**依座位順序輪流發牌**(0,1,0,1… / 0,1,2,0,1,2…),而不是「前半 A 後半 B」——
        /// 這樣房間裡相鄰的人會被分到不同隊，比較符合「隨機分隊」的直覺。
        /// </summary>
        public static int[] AssignTeams(TeamLayout layout, int seatedPlayers)
        {
            if (!CanAssign(layout, seatedPlayers)) return null;
            int teams = TeamCount(layout);
            var result = new int[seatedPlayers];
            for (int i = 0; i < seatedPlayers; i++) result[i] = i % teams;
            return result;
        }
    }
}
