using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 同場多舞者(M8)。
    ///
    /// 目前這一步只做**共用資產的生成與擺位** —— 也就是「六隻角色同時在場上跳」這件事本身。
    /// 計畫把它排在最前面是刻意的:六隻 CPU 蒙皮的 avatar 是全案最大的效能未知,
    /// 先量得出數字才知道要不要做 LOD/隔幀蒙皮,而不是先寫一套優化再回頭發現不需要。
    /// 量測用 <c>SDO_DANCERS=&lt;n&gt;</c> 打開(見 <see cref="TickDancerPerf"/>)。
    ///
    /// 接遠端真人(誰站哪、名字牌、每個人自己的跳/停)是下一步;這裡先把「多隻在場」的地基打好,
    /// 兩者用的是同一條生成路徑。
    /// </summary>
    public sealed partial class ScreenGameplay
    {
        /// <summary>本機以外的舞者。索引 0 = formation slot 1(slot 0 是本機/領隊)。</summary>
        private readonly List<SdoAvatar> _extraDancers = new List<SdoAvatar>();
        private readonly List<Transform> _extraRoots = new List<Transform>();

        // 共用的解析結果(TryLoadAvatar 存進來)。SdoAvatar 對它們只讀 → 六隻共用安全,
        // 而且省掉五次重讀重解(LoadAsset 沒有快取)。
        private HrcLoader _sharedHrc;
        private MotLoader _sharedDanceMot, _sharedRestMot;
        private DpsLoader _sharedDps;

        /// <summary>
        /// 這一局用哪一種個人隊形(0..2)。由 FrontendApp 從 <c>matchStarting.resolved.formationType</c> 灌進來
        /// (<c>GameSession.Formation==3</c> 的「隨機」由房主抽、server 驗過再 echo,所以每台一定一樣)。
        /// </summary>
        public int formationType;

        /// <summary>同場的一位舞者(真人)。由 FrontendApp 從 <c>matchStarting.participants</c> 灌進來,依座位序。</summary>
        public struct DancerInfo
        {
            public int UserId;
            public string Name;
            public bool Male;
            public string[] Parts;     // null → 預設整套
            public int BodyIndex;
            /// <summary>0=A 1=B 2=C 3=自由。組隊模式的站位是照這個分的。</summary>
            public int Team;
        }

        /// <summary>
        /// 這一場的舞者名單(依座位序,**含本機**)。null/空 = 離線或量測模式 → 退回「複製本機外觀」。
        /// 依座位序是必要的:隊形的 slot 指派看的是這個順序,每台都要一樣。
        /// </summary>
        public DancerInfo[] netDancers;

        /// <summary>本機是名單裡的第幾位。-1 = 我不在場上(旁觀者)。</summary>
        public int localDancerIndex = 0;

        /// <summary>
        /// 組隊站位版型:-1 = 不組隊(走個人隊形)。由房主算好、server 驗過再 echo
        /// (<c>matchStarting.resolved.teamLayout</c>)—— 各台自己算的話會用不同時刻的人數快照
        /// 算出不同版型。
        /// </summary>
        public int teamLayout = -1;

        /// <summary>每位舞者「自己的」格子(組隊模式下由隊伍決定;個人隊形就是座位序)。</summary>
        private int[] _dancerBaseSlot;

        private bool TeamMode => teamLayout >= 0;

        /// <summary>
        /// 第 <paramref name="dancerIndex"/> 位舞者的隊伍(0=A 1=B 2=C),**只在組隊局有效** ——
        /// 其餘一律回「沒組隊」。頭上名字的顏色與腳下星環的顏色都吃這個值。
        ///
        /// 為什麼閘門是 <see cref="TeamMode"/>(server echo 的 teamLayout)而不是直接看 team 值:
        /// teamLayout 是 server 用**它自己**的參與者名單重算出來的結論,是「這一局到底算不算組隊局」的
        /// 唯一權威;座位上的 team 則可能是剛切模式那一瞬間還沒清乾淨的殘值。
        /// </summary>
        private int TeamOf(int dancerIndex)
        {
            if (!TeamMode || netDancers == null) return TeamColors.Free;
            return dancerIndex >= 0 && dancerIndex < netDancers.Length ? netDancers[dancerIndex].Team : TeamColors.Free;
        }

        /// <summary>每位舞者現在在跳嗎(遠端的由分數流推導,見 TickRemoteGates)。索引 = 舞者序。</summary>
        private bool[] _dancerDancing;
        /// <summary>上一個結算點時每位遠端舞者的判定計數(推導跳/停用)。</summary>
        private Sdo.Ruleset.DanceJudgeCounts[] _dancerPrevCounts;
        /// <summary>下一次遠端 gate 結算的譜面時間(與本機同一個 8 拍節奏)。</summary>
        private double _nextRemoteGateMs;

        /// <summary>這一場總共有幾位舞者(含本機)。量測時由 <c>SDO_DANCERS</c> 覆寫。</summary>
        private int TotalDancers
        {
            get
            {
                var v = DevVar("SDO_DANCERS");
                int n;
                if (!string.IsNullOrEmpty(v) && int.TryParse(v, out n))
                    return Mathf.Clamp(n, 1, FormationCatalog.MaxDancers);
                if (netDancers != null && netDancers.Length > 0)
                    return Mathf.Clamp(netDancers.Length, 1, FormationCatalog.MaxDancers);
                return Mathf.Clamp(playerCount, 1, FormationCatalog.MaxDancers);
            }
        }

        /// <summary>
        /// 生出本機以外的舞者,擺在官方隊形座標上,並與本機**共用同一份編舞與同一個時鐘**。
        ///
        /// 共用時鐘很重要:同一首歌大家跳一樣(這也是分數流不必傳按鍵記錄的原因)。
        /// 這裡把本機那兩個 delegate 直接指過去,而不是各自算一份時間 —— 各算一份就會慢慢漂開。
        /// </summary>
        private void SpawnExtraDancers()
        {
            // 旁觀者自己沒有舞者(TryLoadAvatar 被跳過),但**別人的還是要出** —— 那正是它要看的東西。
            // 所以這裡不看 spectatorMode,只看有沒有共用資產。
            int total = TotalDancers;
            if (total <= 1 || _sharedHrc == null) return;

            var slots = BuildSlotSpots(total);
            _slotSpots = slots;
            bool haveNames = netDancers != null && netDancers.Length > 0;
            int localIdx = haveNames ? Mathf.Clamp(localDancerIndex, -1, total - 1) : 0;

            for (int i = 0; i < total && i < slots.Length; i++)
            {
                if (i == localIdx) { _extraDancers.Add(null); _extraRoots.Add(_avatarRoot); continue; }   // 本機那格

                // 這一位的外觀:有真人名單就用**他自己的**(性別/穿搭/體型),否則複製本機的
                // (離線與效能量測用 —— 那時場上是同一個人的複製品,成本一樣但外觀不重要)。
                bool male = localPlayerMale;
                string[] parts = avatarParts;
                int bodyIdx = bodyShapeIndex;
                string label = null;
                if (haveNames && i < netDancers.Length)
                {
                    var d = netDancers[i];
                    male = d.Male;
                    parts = d.Parts;
                    bodyIdx = d.BodyIndex;
                    label = d.Name;
                    // 🔴 穿搭還沒傳到(null)就退回預設整套 —— **不能把 null 丟給 builder**:LoadParts 是
                    // `foreach (var rel0 in parts)`,null 會 NRE,而那條 NRE 會把整個 SpawnExtraDancers 打斷 ——
                    // 後面的舞者連生都不會生,連 _dancerCur/_dancerDancing 都沒配起來。少一件衣服總比整場沒人好。
                    if (parts == null) parts = SdoRoomAvatar.DefaultParts(male);
                }

                var go = new GameObject("Dancer" + i + (label != null ? "_" + label : ""));
                var av = go.AddComponent<SdoAvatar>();
                // 🔴 骨架要用**這一位的性別**那一份 —— 男女骨架不同,拿錯會整隻扭曲。
                var hrc = male == localPlayerMale
                    ? _sharedHrc
                    : AvatarAssetCache.Hrc(male ? SdoRoomAvatar.MaleHrc : SdoRoomAvatar.FemaleHrc);
                if (hrc == null) hrc = _sharedHrc;
                av.Setup(hrc, _sharedDanceMot);
                av.SetBodyShape(SdoBodyShape.WeightFromIndex(bodyIdx, male));
                av.RestMot = _sharedRestMot;
                if (_sharedDps != null)
                {
                    av.Dps = _sharedDps;
                    av.MotResolver = ResolveMot;
                    // 時鐘沿用本機那一個 delegate —— 同一首歌大家跳一樣,各算一份會慢慢漂開。
                    av.DanceTimeSec = _avatar != null ? _avatar.DanceTimeSec : null;
                    // 但**跳/停要各自判斷**:那是每個人自己打得好不好的結果(他斷連了他就站著),
                    // 由分數流推導(TickRemoteGates → Sdo.Ruleset.DanceGate,與本機同一個函式)。
                    int me = i;
                    av.DanceEnabled = () => !_failed && _dancerDancing != null
                                            && me < _dancerDancing.Length && _dancerDancing[me];
                }

                // 用與本機舞者**同一個** builder 與 skin style —— 換成房間那套(SdoRoomAvatar)的話
                // shader/材質不同,場上會出現「其中一隻看起來不一樣」。
                var built = SdoAvatarBuilder.LoadParts(go, av, parts, SdoAvatarBuilder.SkinStyle.Gameplay);
                if (!built.Any) { Destroy(go); _extraDancers.Add(null); _extraRoots.Add(null); continue; }

                // 🔴 FeetYAt 會把角色 pose 到第 0 幀 → **量完一定要擺回迴圈當下**,
                // 否則這一隻會定在 T-pose 之後的第一幀(房間那邊踩過同一個坑)。
                float feetY = av.FeetYAt(0f);
                var spot = slots[Mathf.Clamp(i, 0, slots.Length - 1)];
                go.transform.position = new Vector3(spot.x, spot.y - feetY, spot.z);
                av.PhaseOffsetSec = i * 0.37f;   // 待機時不要整齊得像複製人
                av.PoseInitialIdle();

                // 遠端舞者刻意**不掛**手光與頭上表情:那兩個是本機專屬的表演元素,而且手光每一隻都掛的話
                // 成本會直接翻倍。
                //
                // 但**名字牌與地面星環要掛**:
                //   • 名字 —— 六隻角色在場上,沒有名字就分不出哪一隻是誰。
                //   • 星環 —— 官方的組隊實機畫面裡**每一位**腳下都有一圈(而且是自己那一隊的顏色),
                //     那正是場上分辨敵我的方式。星環只是 14 個 quad 的環帶 + 一份材質,成本與手光不是同一個量級。
                int team = TeamOf(i);
                if (!string.IsNullOrEmpty(label)) CreateRemoteNameplate(av, go.transform, label, team);
                CreateGroundStarRing(spot.x, spot.z, 0.6f, av, go.transform, team, local: false);

                // 🔴 一定要跟本機舞者同一層。3D 舞台是**另一台相機**在畫的(SceneCam 的 cullingMask 只有 SceneLayer,
                // 主相機反過來把那層剔掉),留在 Default 層的話這一隻改由主正交相機畫 —— 那台的座標系是 800×600
                // design px,模型單位直接當像素 → 60 單位高的人變成貼在畫面上的 60px 小人,而且不受場景遮擋。
                // (回報:「進遊戲後其他玩家變超小」。條件與本機那條 3D 擺位路徑一致 —— 退回 2D 時兩邊都留在 Default。)
                if (use3dCamera && _camReady) SetLayerRecursive(go, SceneLayer);

                _extraDancers.Add(av);
                _extraRoots.Add(go.transform);
            }

            // 名次換位用的 per-dancer 狀態。初值 = 各就各位(開場大家都 0 分 → 照座位序)。
            int n = total;
            _dancerCur = new Vector3[n];
            _dancerScores = new long[n];
            _dancerDancing = new bool[n];
            _dancerPrevCounts = new Sdo.Ruleset.DanceJudgeCounts[n];
            BuildBaseSlots(n);
            for (int i = 0; i < n; i++)
            {
                int bs = Mathf.Clamp(_dancerBaseSlot[i], 0, _slotSpots.Length - 1);
                _dancerCur[i] = _slotSpots[bs];
                _dancerDancing[i] = true;   // 開場都在跳(與本機的 _dancing 初值一致)
            }
            _camAnchorSpot = _slotSpots.Length > 0 ? _slotSpots[0] : Vector3.zero;

            // 🔴 數**真的建出來的那幾隻**:_extraDancers 在本機那一格放的是 null 佔位
            // (索引要與舞者序對齊),直接印 Count 會多算一隻 —— 而這行字是驗證多人同場時唯一的證據。
            int builtCount = 0;
            for (int i = 0; i < _extraDancers.Count; i++) if (_extraDancers[i] != null) builtCount++;
            Debug.Log("[dancers] 生出 " + builtCount + " 位額外舞者(總共 " + total + " 位,隊形 "
                      + ClampedFormationType + ")");
        }

        /// <summary>
        /// 這一局的站位座標(索引 = slot)。
        ///
        /// 組隊模式走 <see cref="TeamFormationCatalog"/> 的三張官方座標表(2v2/3v3/2v2v2),
        /// 把每隊的位置攤平成一排 slot;非組隊走個人隊形表。
        /// 湊不出合法組隊版型時 server 根本不會讓這一場開始(R10c),所以這裡收到的 layout 一定是合法的 ——
        /// 但還是夾一次值,因為「相信上游」是最容易在重構時失效的假設。
        /// </summary>
        private Vector3[] BuildSlotSpots(int total)
        {
            if (!TeamMode) return FormationCatalog.GetSlots(ClampedFormationType, total);

            int li = Mathf.Clamp(teamLayout, 0, TeamFormationCatalog.All.Length - 1);
            var layout = TeamFormationCatalog.All[li];
            var teams = TeamFormationCatalog.GetTeams(layout);
            var flat = new System.Collections.Generic.List<Vector3>();
            _teamSlotStart = new int[teams.Length];
            for (int t = 0; t < teams.Length; t++)
            {
                _teamSlotStart[t] = flat.Count;
                for (int m = 0; m < teams[t].Length; m++) flat.Add(teams[t][m]);
            }
            _teamSlotSize = new int[teams.Length];
            for (int t = 0; t < teams.Length; t++) _teamSlotSize[t] = teams[t].Length;
            return flat.ToArray();
        }

        private int[] _teamSlotStart, _teamSlotSize;

        /// <summary>
        /// 每位舞者「自己的」格子。
        ///
        /// 個人隊形 = 座位序(舞者 i → slot i)。
        /// 組隊模式 = 依隊伍分:A 隊的人依序填第 0 隊的位置、B 隊填第 1 隊…
        /// 🔴 分配只看**座位序**(名單本身已排過),不看分數 —— 每台都要算出一樣的結果。
        /// </summary>
        private void BuildBaseSlots(int total)
        {
            _dancerBaseSlot = new int[total];
            if (!TeamMode || _teamSlotStart == null || netDancers == null)
            {
                for (int i = 0; i < total; i++) _dancerBaseSlot[i] = i;
                return;
            }
            var used = new int[_teamSlotStart.Length];
            for (int i = 0; i < total; i++)
            {
                int team = i < netDancers.Length ? netDancers[i].Team : 0;
                if (team < 0 || team >= _teamSlotStart.Length) team = 0;   // 自由/超範圍 → 併到第一隊(server 已擋,這是防呆)
                int within = used[team] < _teamSlotSize[team] ? used[team]++ : _teamSlotSize[team] - 1;
                _dancerBaseSlot[i] = Mathf.Clamp(_teamSlotStart[team] + within, 0, _slotSpots.Length - 1);
            }
        }

        /// <summary>夾進合法範圍的隊形編號(官方只有三張個人隊形座標表)。</summary>
        private int ClampedFormationType => Mathf.Clamp(formationType, 0, FormationCatalog.TypeCount - 1);

        // ================= 名次換位(G4)=================

        /// <summary>
        /// 這一局的隊形座標(索引 = slot)。單人時就是 [原點]。
        /// </summary>
        private Vector3[] _slotSpots = { Vector3.zero };

        /// <summary>
        /// **相機錨點** = 現在占著 slot 0 的那位舞者的位置。
        ///
        /// 🔴 與 <c>_danceSpot</c> 分開是必要的:<c>_danceSpot</c> 有 6 個 read site 的語意是
        /// 「本機舞者站哪」(擺 avatar、飛行翅膀的基準 Y、胸口位置、頭上表情…),
        /// 只有導播鏡頭那一處的語意是「鏡頭錨定在哪」。官方的鏡頭跟**第一名**,不跟本機 ——
        /// 把 _danceSpot 改成跟第一名會一起把那 6 處弄歪(本機舞者會被搬到別人的位置上)。
        /// 單人時兩者相同(都是原點),所以離線行為完全不變。
        /// </summary>
        private Vector3 _camAnchorSpot;

        /// <summary>每位舞者目前的位置(LERP 中的值)。索引 = 舞者序(與 netDancers 同序)。</summary>
        private Vector3[] _dancerCur;

        /// <summary>每位舞者的分數(給 FormationAssignment 用)。索引同上。</summary>
        private long[] _dancerScores;

        /// <summary>
        /// 每幀把每位舞者往它該站的格子收斂一步,並把相機錨點設成 slot 0 的占用者。
        ///
        /// 🔴 **搬既有的 transform,不重建角色。** avatar 的 Mesh/Texture/Material 是 per-instance
        /// 而且沒有人 Destroy(見 docs/systems/multi-avatar-perf.md)—— 用「重建」來換位置的話
        /// 每次名次變動都會洩一份 native 記憶體。官方本來也是 LERP 滑過去,不是瞬移。
        /// </summary>
        private void TickDancerSlots()
        {
            if (_dancerCur == null || _dancerCur.Length <= 1) return;   // 單人:沒有換位這件事
            int n = _dancerCur.Length;

            FillDancerScores();
            // 🔴 組隊模式**不做名次換位**:把第一名滑進全場 slot 0 會讓他跨隊跑到別隊的位置上。
            // 官方的組隊座標表是「每隊自己的前後排」,member 0 是該隊的前排 —— 那是隊內的概念,
            // 不是全場的。跨隊搬人在官方也不會發生。所以組隊時每個人待在自己隊的格子裡。
            // (「隊內也依分數換前後排」有可能是官方行為,但我沒有證據,所以不猜。)
            var slots = TeamMode ? _dancerBaseSlot
                                 : Sdo.Ruleset.FormationAssignment.SlotForDancer(_dancerScores);

            for (int i = 0; i < n; i++)
            {
                int s = Mathf.Clamp(slots[i], 0, _slotSpots.Length - 1);
                Vector3 target = _slotSpots[s];
                _dancerCur[i] = new Vector3(
                    Sdo.Ruleset.FormationAssignment.SlideStep(_dancerCur[i].x, target.x),
                    target.y,
                    Sdo.Ruleset.FormationAssignment.SlideStep(_dancerCur[i].z, target.z));

                if (s == Sdo.Ruleset.FormationAssignment.LeaderSlot) _camAnchorSpot = _dancerCur[i];

                var t = i < _extraRoots.Count ? _extraRoots[i] : null;
                if (t == null) continue;
                // 只改 XZ。Y 由各自的擺位(腳底貼地)與飛行翅膀的浮空每幀負責 —— 兩邊都寫 Y 會打架。
                var q = t.position;
                t.position = new Vector3(_dancerCur[i].x, q.y, _dancerCur[i].z);

                // 本機那格順便同步 _danceSpot(它的語意是「本機舞者站哪」,被擠位時要跟著走)。
                if (i == LocalDancerSlotIndex) _danceSpot = new Vector3(_dancerCur[i].x, _danceSpot.y, _dancerCur[i].z);
            }
        }

        /// <summary>
        /// 一位遠端舞者頭上的名字牌。做法與本機那個一樣(把頭骨每幀投影到螢幕、在固定像素距離上畫),
        /// 但**不畫箭頭** —— 那個箭頭在官方是「這是你」的指示物,每個人頭上都有一個就沒有意義了。
        /// </summary>
        private void CreateRemoteNameplate(SdoAvatar av, Transform root, string label, int team)
        {
            int headIdx = av.BoneIndex("Bip01_Head");
            if (headIdx < 0) headIdx = av.BoneIndex("Bip01_Neck");
            Transform anchor = null;
            if (headIdx >= 0)
            {
                var ag = new GameObject("HeadAnchor");
                if (use3dCamera) ag.layer = SceneLayer;
                ag.transform.SetParent(root, false);
                av.AddAnchor(headIdx, ag.transform);
                anchor = ag.transform;
            }
            var go = new GameObject("RemoteNameplate_" + label);
            var hm = go.AddComponent<HeadMarker>();
            hm.Init(null, label);   // null = 不要箭頭(見上面的理由)
            hm.SetTeamColor(team);  // 組隊局:名字染成他那一隊的顏色(與腳下星環同一個色)
            Transform a = anchor;
            Transform r = root;
            hm.AnchorGetter = () => a != null ? a.position
                : ((r != null ? r.position : Vector3.zero) + new Vector3(0f, 59f, 0f));
            hm.CamGetter = () => _sceneCam != null ? _sceneCam : _cam;
        }

        /// <summary>測試用:第 i 位舞者的 root(本機那一格 = <c>_avatarRoot</c>;還沒生出來 = null)。</summary>
        public Transform DancerRootForTest(int i) => i >= 0 && i < _extraRoots.Count ? _extraRoots[i] : null;

        /// <summary>本機在舞者陣列裡的索引(離線/量測時是 0;旁觀者是 -1)。</summary>
        private int LocalDancerSlotIndex
            => (netDancers != null && netDancers.Length > 0) ? localDancerIndex : 0;

        private void FillDancerScores()
        {
            int local = LocalDancerSlotIndex;
            var opp = NetOpponents != null ? NetOpponents() : null;
            bool haveNames = netDancers != null && netDancers.Length > 0;

            for (int i = 0; i < _dancerScores.Length; i++)
            {
                if (i == local) { _dancerScores[i] = _score != null ? _score.Score : 0L; continue; }
                _dancerScores[i] = haveNames ? ScoreOf(opp, netDancers[i].UserId) : ScoreFallback(opp, i, local);
            }
        }

        /// <summary>依 userId 找那個人的最新分數(找不到 = 還沒收到他的第一筆 → 0)。</summary>
        private static long ScoreOf(NetPlayerScore[] opp, int userId)
        {
            if (opp == null) return 0L;
            for (int i = 0; i < opp.Length; i++) if (opp[i].UserId == userId) return opp[i].Score;
            return 0L;
        }

        /// <summary>沒有真人名單時(離線/量測)的退化路徑:照順序取。</summary>
        private static long ScoreFallback(NetPlayerScore[] opp, int i, int local)
        {
            if (opp == null) return 0L;
            int k = i > local ? i - 1 : i;
            return k >= 0 && k < opp.Length ? opp[k].Score : 0L;
        }

        // ================= 遠端舞者的跳/停(G2)=================

        /// <summary>
        /// 每位遠端舞者自己的「跳/停」。與本機**同一個規則函式**(<see cref="Sdo.Ruleset.DanceGate"/>),
        /// 只是旗標的來源不同:本機直接知道「這個 8 拍有沒有斷」,遠端從相鄰兩筆分數流的差推出來。
        ///
        /// 這就是分數流不必傳按鍵記錄的原因(計畫的 D4):舞蹈由編舞驅動(同一首歌大家跳一樣),
        /// 只有「跳還是站」需要同步,而那從判定計數的差就還原得出來。
        /// 已知界線寫在 DanceGate.NextFromSamples 的註解裡(取樣跨結算點時可能差一個 block)。
        /// </summary>
        private void TickRemoteGates(double nowMs)
        {
            if (_dancerDancing == null || _dancerDancing.Length <= 1) return;
            if (netDancers == null || netDancers.Length == 0) return;   // 離線/量測:遠端就跟著本機
            if (_map == null) return;

            double settle = Sdo.Ruleset.DanceGate.SettleMs(_map.Bpm);
            if (_nextRemoteGateMs <= 0) _nextRemoteGateMs = settle;
            if (nowMs < _nextRemoteGateMs) return;
            while (nowMs >= _nextRemoteGateMs) _nextRemoteGateMs += settle;

            var opp = NetOpponents != null ? NetOpponents() : null;
            int local = LocalDancerSlotIndex;
            for (int i = 0; i < _dancerDancing.Length; i++)
            {
                if (i == local) { _dancerDancing[i] = _dancing; continue; }   // 本機用真值
                if (i >= netDancers.Length) continue;
                int uid = netDancers[i].UserId;
                var cur = CountsOf(opp, uid, out int combo);
                _dancerDancing[i] = Sdo.Ruleset.DanceGate.NextFromSamples(
                    _dancerDancing[i], _dancerPrevCounts[i], cur, combo);
                _dancerPrevCounts[i] = cur;
            }
        }

        private static Sdo.Ruleset.DanceJudgeCounts CountsOf(NetPlayerScore[] opp, int userId, out int combo)
        {
            combo = 0;
            if (opp == null) return default(Sdo.Ruleset.DanceJudgeCounts);
            for (int i = 0; i < opp.Length; i++)
                if (opp[i].UserId == userId) { combo = opp[i].Combo; return opp[i].Counts; }
            return default(Sdo.Ruleset.DanceJudgeCounts);
        }

        // ================= 效能量測 =================
        // 計畫的 G6:「先量測六個角色同時渲染的效能,再決定要不要優化」。統計本身在 FrameStats
        // (房間那邊也用同一份 —— 房間的最壞情況是 6 座位 + 10 旁觀 = 16 隻,比這裡更重)。

        private FrameStats _perf;

        private void TickDancerPerf()
        {
            if (string.IsNullOrEmpty(DevVar("SDO_DANCERS"))) return;   // 只有量測時才做
            if (_perf == null) _perf = new FrameStats("gameplay");
            int n = _dancerCur != null ? _dancerCur.Length : (spectatorMode ? 0 : 1);
            _perf.Tick(n);
        }
    }
}
