using System.Collections.Generic;

namespace Sdo.Ruleset
{
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
        /// 依已選定的 <paramref name="leader"/> 指派 slot。這個 overload 給有防抖狀態的即時多人站位用:
        /// 分數只決定人數,領隊由 <see cref="SelectLeader"/> 決定,不能在這裡又重算一次最高分。
        /// leader 不合法時才退回當下最高分。
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
