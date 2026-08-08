using System.Collections.Generic;

namespace Sdo.Ruleset
{
    /// <summary>
    /// 名次要怎麼影響站位(config.ini <c>[Room] rankBasedFormation</c>)。
    /// </summary>
    public enum FormationRankMode
    {
        /// <summary>不看分數:每個人整場站在座位序(組隊時是隊內序)的格子。</summary>
        Off = 0,

        /// <summary>
        /// 官方行為(**預設**):只把當下第一名滑進領隊格,跟他對調的是**當下站在那一格的人**,其餘不動。
        /// 每幀的路徑見 <see cref="FormationAssignment.StableLeaderSlots"/> —— 要接上一幀的站位,
        /// 不然被擠掉的會恆是座位序第 0 位那位。
        /// </summary>
        Leader = 1,

        /// <summary>
        /// 完整名次:第 k 名站 slot k —— 連中段名次也跟著排(偏離官方,官方只動領隊格那一格)。
        /// 見 <see cref="FormationAssignment.StableRankSlots"/>。
        /// </summary>
        Full = 2,
    }

    /// <summary>
    /// 「誰站哪一格」——**分數 → 隊形 slot** 的純規則。
    ///
    /// 官方行為(逐字對照 EXE:setup 在 <c>FUN_00471f20</c>、每幀在 <c>FUN_0046dd20</c>):
    ///   • setup 時 slot 0 = 房主,其餘依房間座位序填 1,2,3…
    ///   • **每幀**掃每位舞者的分數,把當下第 1 名滑進 slot 0 —— 那是鏡頭錨定的中央前排位置;
    ///     被擠掉的那位回到自己原本的格子。移動是 <c>*0.9 + target*0.1</c> 的 LERP,不是瞬移。
    ///
    /// 所以這裡只回答「目標格子是誰」,LERP 與實際搬 transform 是呼叫端的事
    /// (🔴 而且**一定要搬既有的 transform,不能重建角色** —— avatar 的 Mesh/Texture/Material 是
    ///  per-instance 而且沒有人 Destroy,重建一次就洩一份。見 docs/systems/multi-avatar-perf.md)。
    ///
    /// 為什麼要是純函式:每一台 client 都得算出**一樣**的站位,否則同一個人在不同人的畫面上站不同格。
    /// 而分數是 server 推來的權威資料 → 只要規則一致,結果就一致。有測試守著比對「同分怎麼辦」。
    /// </summary>
    public static class FormationAssignment
    {
        /// <summary>鏡頭錨定的那一格(領隊)。官方是 0。</summary>
        public const int LeaderSlot = 0;

        /// <summary>
        /// 挑戰者至少要領先目前領隊這麼多分才換位 —— 純粹的**視覺**防抖:分數咬得很緊時,
        /// 每個判定都換一次中央那格看起來像在抽動。
        ///
        /// ⚠️ 這**不是**同步機制,而且只走本機 fallback 那條路(見 <see cref="ResolveLeader"/>)。
        /// 線上的領隊是 server 權威的:它把每個人的 (歌曲時間, 分數) 存成序列,在同一個歌曲
        /// 時刻取樣後才比大小,再加上換人節流 —— 見 server 的 <c>LiveLeaderTracker</c>。
        /// 分數門檻治不了「兩台看到的分數在時間上錯位」那種震盪,因為雜訊振幅 = 時間落差 ×
        /// 當下得分率,會跟著 combo 一起長,門檻永遠追不上。所以那件事不在這裡處理。
        /// </summary>
        public const long LeaderSwitchLead = 300L;

        /// <summary>
        /// 算出每位舞者的目標 slot。<paramref name="scores"/> 的索引 = 舞者(依房間座位序),
        /// 回傳陣列同索引 = 那位舞者要站的 slot。
        ///
        /// 規則:基礎是「舞者 i → slot i」;然後把當下第 1 名與 <see cref="LeaderSlot"/> 的占用者互換。
        /// **同分時取索引小的**(= 座位序在前的)—— 必須是決定性的,不然兩台會各挑一個人當第一名,
        /// 那個人就會在別人畫面上站在中央、在自己畫面上站在旁邊。
        /// </summary>
        public static int[] SlotForDancer(IReadOnlyList<long> scores)
            => SlotForDancer(scores, TopScorer(scores));

        /// <summary>
        /// 依已選定的 <paramref name="leader"/> 指派 slot。分數只決定人數,領隊由 <see cref="SelectLeader"/>
        /// 決定,不能在這裡又重算一次最高分。leader 不合法時才退回當下最高分。
        ///
        /// ⚠️ **無狀態**版本 —— 每幀都從座位序重來,所以被擠掉的恆是座位序第 0 位那位(沒在爭名次的人也會
        /// 一直被搬)。這是給一次性計算/測試用的;每幀跑的路徑要用 <see cref="StableLeaderSlots"/>。
        /// </summary>
        public static int[] SlotForDancer(IReadOnlyList<long> scores, int leader)
        {
            int n = scores != null ? scores.Count : 0;
            var slot = new int[n];
            for (int i = 0; i < n; i++) slot[i] = i;      // 基礎:各就各位
            if (n <= 1) return slot;

            int top = leader >= 0 && leader < n ? leader : TopScorer(scores);
            if (top == LeaderSlot) return slot;            // 第一名本來就在領隊格

            // 找出「現在占著領隊格」的那位,與第一名互換。
            int holder = -1;
            for (int i = 0; i < n; i++) if (slot[i] == LeaderSlot) { holder = i; break; }
            if (holder < 0) return slot;                   // 理論上不會(基礎指派保證有人在 slot 0)

            slot[holder] = slot[top];
            slot[top] = LeaderSlot;
            return slot;
        }

        /// <summary>
        /// 依「名次怎麼影響站位」指派 slot(config.ini 的 <c>rankBasedFormation</c>,見
        /// <c>StartupConfigSchema</c>)。
        ///
        /// <list type="bullet">
        /// <item><see cref="FormationRankMode.Off"/> —— **完全不看分數**:每個人整場站自己座位序的格子
        /// (＝<see cref="SeatOrderSlots"/>)。</item>
        /// <item><see cref="FormationRankMode.Leader"/> —— 官方行為,見上面那個 overload。</item>
        /// <item><see cref="FormationRankMode.Full"/> —— 第 k 名站 slot k。這個 overload 走的是**無防抖**的
        /// <see cref="RankSlots"/>(給測試/一次性計算用);每幀跑的路徑要用 <see cref="StableRankSlots"/>,
        /// 否則分數咬很緊時整排人會每幀抽動。</item>
        /// </list>
        ///
        /// 這是純視覺偏好,每台各自生效 —— 它不進網路協定,也不影響名次/分數的計算。
        /// (兩台設定不同時,只是「誰站哪格」在各自畫面上不同,分數與判定仍然一致。)
        /// </summary>
        public static int[] SlotForDancer(IReadOnlyList<long> scores, int leader, FormationRankMode mode)
        {
            switch (mode)
            {
                case FormationRankMode.Off: return SeatOrderSlots(scores != null ? scores.Count : 0);
                case FormationRankMode.Full: return RankSlots(scores);
                default: return SlotForDancer(scores, leader);
            }
        }

        /// <summary>
        /// **完整名次**站位:分數由高到低,第 k 名站 slot k(同分取索引小 —— 決定性,理由同
        /// <see cref="SlotForDancer(IReadOnlyList{long}, int)"/>)。
        ///
        /// 與官方的 <see cref="FormationRankMode.Leader"/> 差在哪:官方只做「第一名 ↔ 領隊格占用者」一次互換,
        /// 而領隊格的占用者恆是**座位序第 0 位**那位舞者 —— 所以第 2、3 名爭第一名的時候,被擠來擠去的是
        /// 座位序第 0 位那位(就算他是最後一名),看起來像「沒在爭名次的人也一直在動」。
        /// 這裡改成完整排序,每個名次固定一格:名次沒變的人就完全不動。
        /// </summary>
        public static int[] RankSlots(IReadOnlyList<long> scores)
        {
            int n = scores != null ? scores.Count : 0;
            var slot = new int[n];
            if (n == 0) return slot;

            // 依分數降序、同分依索引升序排出名次順序(選擇排序 —— n ≤ 6,不值得配置額外容器)。
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            for (int a = 0; a < n - 1; a++)
                for (int b = a + 1; b < n; b++)
                    if (scores[order[b]] > scores[order[a]]) { int t = order[a]; order[a] = order[b]; order[b] = t; }

            for (int r = 0; r < n; r++) slot[order[r]] = r;
            return slot;
        }

        /// <summary>
        /// 每幀用的完整名次站位:以**上一幀的站位**為基礎,只在後面那位領先前面那位達
        /// <see cref="LeaderSwitchLead"/> 分時交換相鄰兩格。
        ///
        /// 為什麼不直接每幀跑 <see cref="RankSlots"/>:完整排序讓每個名次都是一格,於是**每一組相鄰名次**
        /// 都會在分數咬緊時來回互換(官方模式只有領隊格那一格會抖)。所以防抖從「只保護領隊格」擴到全排:
        /// 一次只做相鄰交換 → 一定還是排列(不會有人重複/漏格),而且反向換回也要再跨過同一條門檻。
        /// 每幀一次 pass 就夠 —— 站位本來就是 <see cref="SlideStep"/> 慢慢滑過去的,差幾幀看不出來。
        ///
        /// <paramref name="prevSlots"/> 傳 null(或長度不符/不是排列 —— 例如中途有人離開)就從座位序重新開始。
        /// <paramref name="leader"/> 有效時**釘死 slot 0**:線上的第一名是 server 權威的
        /// (見 <see cref="ResolveLeader"/>),中段名次各台可能因分數的時間落差而不同,但中央前排那格
        /// (導播鏡頭的錨點)必須每台一致。
        /// </summary>
        public static int[] StableRankSlots(IReadOnlyList<long> scores, IReadOnlyList<int> prevSlots, int leader)
        {
            int n = scores != null ? scores.Count : 0;
            if (n == 0) return new int[0];

            var order = OrderFromSlots(prevSlots, n);       // order[名次] = 舞者索引

            // 相鄰交換:後面那位要領先前面那位達門檻才上去。
            for (int r = 0; r + 1 < n; r++)
            {
                long ahead = scores[order[r]], behind = scores[order[r + 1]];
                if (ahead > long.MaxValue - LeaderSwitchLead) continue;
                if (behind >= ahead + LeaderSwitchLead) { int t = order[r]; order[r] = order[r + 1]; order[r + 1] = t; }
            }

            // 權威第一名釘在 slot 0,其餘保持相對順序。
            if (leader >= 0 && leader < n && order[0] != leader)
            {
                int at = 0;
                for (int r = 1; r < n; r++) if (order[r] == leader) { at = r; break; }
                for (int r = at; r > 0; r--) order[r] = order[r - 1];
                order[0] = leader;
            }

            var slot = new int[n];
            for (int r = 0; r < n; r++) slot[order[r]] = r;
            return slot;
        }

        /// <summary>
        /// 每幀用的**領隊換位**站位:以**上一幀的站位**為基礎,把 <paramref name="leader"/> 與**當下站在
        /// 領隊格的那位**互換。其餘的人一格都不動。
        ///
        /// 🔴 為什麼一定要吃 <paramref name="prevSlots"/>:無狀態的
        /// <see cref="SlotForDancer(IReadOnlyList{long}, int)"/> 每幀都先把所有人放回座位序再交換一次,
        /// 於是「被交換掉的那位」恆是**座位序第 0 位**。三個人時第 2、3 名互爭第一名,每換一次手就把座位序
        /// 第 0 位那位在 slot1／slot2 之間丟來丟去 —— 就算他分數墊底、名次從頭到尾沒變過。
        /// 接上一幀之後,交換的對象變成「上一次被擠上領隊格的那位」,爭第一名的兩個人自己換,
        /// 沒在爭的人完全不動。座位序只在**開局**(大家都 0 分)決定一次初始位置,之後就只認格子。
        ///
        /// <paramref name="prevSlots"/> 傳 null(或長度不符/不是排列 —— 例如中途有人離開)就從座位序重新開始。
        /// <paramref name="leader"/> 不合法(-1、超出範圍)時維持上一幀的站位不動。
        /// </summary>
        public static int[] StableLeaderSlots(IReadOnlyList<int> prevSlots, int leader, int count)
        {
            if (count <= 0) return new int[0];

            var order = OrderFromSlots(prevSlots, count);   // order[slot] = 站在那一格的舞者
            var slot = new int[count];
            for (int s = 0; s < count; s++) slot[order[s]] = s;

            if (count <= 1 || leader < 0 || leader >= count) return slot;
            if (slot[leader] == LeaderSlot) return slot;    // 第一名已經在領隊格 → 全場不動

            int holder = order[LeaderSlot];                 // 現在站在領隊格的那位,跟第一名對調
            slot[holder] = slot[leader];
            slot[leader] = LeaderSlot;
            return slot;
        }

        /// <summary>
        /// 把「舞者 → slot」翻回「名次 → 舞者」。<paramref name="slots"/> 不是 0..n-1 的排列(null、長度變了、
        /// 有人重複)就回座位序 —— 壞掉的狀態要能自己走回一個合法的排列,不能讓兩個人疊在同一格。
        /// </summary>
        private static int[] OrderFromSlots(IReadOnlyList<int> slots, int n)
        {
            var order = new int[n];
            for (int r = 0; r < n; r++) order[r] = -1;
            if (slots == null || slots.Count != n) return SeatOrderSlots(n);

            for (int i = 0; i < n; i++)
            {
                int s = slots[i];
                if (s < 0 || s >= n || order[s] >= 0) return SeatOrderSlots(n);
                order[s] = i;
            }
            return order;
        }

        /// <summary>座位序站位:舞者 i → slot i。關掉名次連動時的目標格子。</summary>
        public static int[] SeatOrderSlots(int count)
        {
            if (count < 0) count = 0;
            var slot = new int[count];
            for (int i = 0; i < count; i++) slot[i] = i;
            return slot;
        }

        /// <summary><c>rankBasedFormation</c> 寫進 config.ini 的三個值。</summary>
        public const string ModeKeyOff = "off";
        public const string ModeKeyLeader = "leader";
        public const string ModeKeyFull = "full";

        /// <summary>
        /// 讀 config.ini 的 <c>rankBasedFormation</c>。**舊檔的 0/1 要照樣生效** —— 這個鍵在還是布林開關的
        /// 那幾版寫的就是 0/1,玩家手上的 config.ini 不會自己改(0→Off、1→Leader)。認不出來的值(打錯字、空的)
        /// → <paramref name="fallback"/>,預設是 <see cref="FormationRankMode.Leader"/>。
        /// (canonical 值由 <c>RoomConfig.NormalizeRankFormation</c> 產生;兩邊各寫一份同樣的字串,有測試釘住一致。)
        /// </summary>
        public static FormationRankMode ParseMode(string s, FormationRankMode fallback = FormationRankMode.Leader)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            switch (s.Trim().ToLowerInvariant())
            {
                case "0": case "off": case "false": case "no": case "seat": return FormationRankMode.Off;
                case "1": case "on": case "true": case "yes": case "leader": return FormationRankMode.Leader;
                case "2": case "full": case "rank": return FormationRankMode.Full;
                default: return fallback;
            }
        }

        /// <summary>寫回 config.ini 的字串。</summary>
        public static string ModeKey(FormationRankMode mode)
        {
            switch (mode)
            {
                case FormationRankMode.Off: return ModeKeyOff;
                case FormationRankMode.Full: return ModeKeyFull;
                default: return ModeKeyLeader;
            }
        }

        /// <summary>
        /// 當下第 1 名是第幾位舞者。同分取索引小的(決定性 —— 見 <see cref="SlotForDancer"/>)。
        /// 空清單回 -1。
        /// </summary>
        public static int TopScorer(IReadOnlyList<long> scores)
        {
            int n = scores != null ? scores.Count : 0;
            if (n == 0) return -1;
            int best = 0;
            for (int i = 1; i < n; i++)
                if (scores[i] > scores[best]) best = i;    // 嚴格大於 → 同分保留較小的索引
            return best;
        }

        /// <summary>
        /// 依最新分數更新領隊。分數接近時保留 <paramref name="currentLeader"/>,只有挑戰者領先
        /// <see cref="LeaderSwitchLead"/> 分以上才換人;因此反向換回也要跨過同一條門檻。
        ///
        /// 這是**本機**規則(單機多人、或 server 還沒給過權威 leader 的那幾幀)。分數都是同一台
        /// 算出來的,沒有時間錯位的問題,門檻在這裡只是視覺防抖 —— 見 <see cref="LeaderSwitchLead"/>。
        /// </summary>
        public static int SelectLeader(IReadOnlyList<long> scores, int currentLeader)
        {
            int n = scores != null ? scores.Count : 0;
            if (n == 0) return -1;

            int challenger = TopScorer(scores);
            if (currentLeader < 0 || currentLeader >= n) return challenger;
            if (challenger == currentLeader) return currentLeader;

            long incumbentScore = scores[currentLeader];
            if (incumbentScore > long.MaxValue - LeaderSwitchLead) return currentLeader;
            return scores[challenger] >= incumbentScore + LeaderSwitchLead ? challenger : currentLeader;
        }

        /// <summary>
        /// Prefer the server-selected dancer index when it maps into this client's roster. If the authoritative
        /// user is absent (old server, roster transition, spectator-only row), retain the local hysteresis fallback.
        /// </summary>
        public static int ResolveLeader(IReadOnlyList<long> scores, int currentLeader, int authoritativeLeader)
        {
            int n = scores != null ? scores.Count : 0;
            return authoritativeLeader >= 0 && authoritativeLeader < n
                ? authoritativeLeader
                : SelectLeader(scores, currentLeader);
        }

        /// <summary>
        /// 每幀往目標位置收斂一步(官方的 <c>cur * 0.9 + target * 0.1</c>)。
        ///
        /// 用固定係數而不是 <c>Lerp(cur, target, deltaTime * k)</c> 是刻意的:官方就是每幀固定比例,
        /// 而且這樣在不同幀率下的「感覺」與原版一致(改成時間相關反而會與原版不同)。
        /// 代價是高幀率會收斂得比較快 —— 那是原版就有的性質。
        /// </summary>
        public const float SlideKeep = 0.9f;

        /// <summary>一維的收斂步(呼叫端對 x/z 各做一次;y 不動 —— 站位表的 y 恆為 0)。</summary>
        public static float SlideStep(float cur, float target) => cur * SlideKeep + target * (1f - SlideKeep);
    }
}
