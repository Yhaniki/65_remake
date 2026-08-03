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

        /// <summary>
        /// 每位舞者的跳/停歷程(譜面時間 ms + 那一刻在不在跳),格式與本機的 <c>_danceTrack</c> 一樣,只記變化。
        ///
        /// 結算的背景回放要用它:回放是「把這一場再跳一遍」,而「這一場」包含每個人各自斷在哪幾段 ——
        /// 少了這份紀錄,遠端在回放裡只能整段跳好跳滿或整段站著,那就不是剛剛那一場了。
        /// 索引 = 舞者序;本機那格不填(本機走自己的 _danceTrack)。
        /// </summary>
        private List<(double tMs, bool on)>[] _dancerTracks;

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
            // 所以這裡不看 spectatorMode,只看有沒有共用資產(LoadSharedDanceAssets 旁觀也會載)。
            int total = TotalDancers;
            if (total <= 0 || _sharedHrc == null) return;
            bool haveNames = netDancers != null && netDancers.Length > 0;
            int localIdx = haveNames ? Mathf.Clamp(localDancerIndex, -1, total - 1) : 0;
            // 🔴 門檻是「場上有沒有**別人**」,不是「總人數 > 1」。旁觀者不占名單裡的任何一格
            // (localDancerIndex = -1),所以場上只有一位參賽者時 total == 1 —— 而那一位正是旁觀者
            // 唯一要看的人。舊的 total<=1 會把這一格擋掉,旁觀就變成一個空場。
            if (total == 1 && localIdx == 0) return;   // 真的只有本機一個人(離線/單人)

            var slots = BuildSlotSpots(total);
            _slotSpots = slots;
            _dancerRingTr = new Transform[total];   // 每位舞者的特效錨點(星環);本機那格在迴圈後補
            // Freeze ONE predicate for the whole remote spawn pass. A loaded SceneCam alone is insufficient:
            // without a valid CV track the dancer stays in the legacy Default-layer path, so its name must too.
            bool sceneWorldMode = use3dCamera && _camReady && _sceneCam != null;

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

                // 飛行翅膀看**這一位自己的**穿搭 —— 浮空高度與待機 clip 都由它決定(見下面兩處)。
                bool flying = SpecialMotionItems.WearsFlyingWing(parts);

                var go = new GameObject("Dancer" + i + (label != null ? "_" + label : ""));
                var av = go.AddComponent<SdoAvatar>();
                // 🔴 骨架要用**這一位的性別**那一份 —— 男女骨架不同,拿錯會整隻扭曲。
                // 路徑一定要先解成絕對的:AvatarAssetCache 底下是 File.ReadAllBytes,吃到 "AVATAR/MALE.HRC" 這種
                // 相對路徑會相對於**行程的工作目錄**(不是資料根)→ 讀不到 → 下面那行靜默退回 _sharedHrc,
                // 異性玩家就一路用著本機性別的骨架。房間/商城那幾個呼叫端都是先過 ResolveAvatarFile 的。
                var hrc = male == localPlayerMale
                    ? _sharedHrc
                    : AvatarAssetCache.Hrc(SdoAvatarBuilder.ResolveAvatarFile(male ? SdoRoomAvatar.MaleHrc : SdoRoomAvatar.FemaleHrc));
                if (hrc == null) hrc = _sharedHrc;
                av.Setup(hrc, _sharedDanceMot);
                // 🔴 混色長度要與本機**同一個**常數。SdoAvatar 自己的預設是 1.0 秒 —— 那是寫給房間 idle↔walk 的,
                // 拿來跳舞會蓋掉整段 slice:DPS 一個 row 常常只有 1~2 秒,混 1 秒等於半首歌都在過場,
                // 而且下一個 row 邊界又從「混到一半的姿勢」重新起混 —— 動作幅度被壓扁、追不上編舞,
                // 看起來就是「切動作的中間卡一下」。DanceBlendSec(0.5s)是反編譯出來的官方值(見那個常數的註解)。
                av.BlendSec = DanceBlendSec;
                av.SetBodyShape(SdoBodyShape.WeightFromIndex(bodyIdx, male));
                av.RestMot = RemoteRestMot(male, flying) ?? _sharedRestMot;
                if (_sharedDps != null)
                {
                    av.Dps = _sharedDps;
                    av.MotResolver = ResolveMot;
                    // 時鐘沿用本機那一個 delegate —— 同一首歌大家跳一樣,各算一份會慢慢漂開。
                    // 🔴 旁觀沒有本機舞者(_avatar == null)→ 直接指向同一顆歌曲時鐘。給 null 的話
                    //    SdoAvatar.LateUpdate 走不進 DPS 那條路,場上每個人都站著不動
                    //    (使用者回報「旁觀的人沒辦法看到玩家跳舞」)。
                    av.DanceTimeSec = _avatar != null ? _avatar.DanceTimeSec : (System.Func<float>)SongDanceTimeSec;
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
                // 飛行翅膀:穿著就整場浮 HoverY —— 與姿勢、是否在跳舞都無關,和本機同一顆 SpecialMotionItems.HoverY
                // (漏掉的話同場會變成「我浮著、別人踩在地上」)。flystay clip 自己只抬 ~2.6(女)/~0.7(男),
                // 不靠這個常數是浮不起來的。
                //
                // 遠端的穿搭整場不變 → 擺位時加一次就夠,不必像本機 UpdateFlyHover 那樣每幀平滑收斂
                // (那是為了 F4 面板即時換穿);TickDancerSlots 每幀只寫 XZ、保留 Y,所以這個高度會留著。
                go.transform.position = new Vector3(spot.x, spot.y - feetY + SpecialMotionItems.HoverY(flying), spot.z);
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
                if (!string.IsNullOrEmpty(label))
                    CreateRemoteNameplate(av, go.transform, label, team, sceneWorldMode);
                var ringTr = CreateGroundStarRing(spot.x, spot.z, 0.6f, av, go.transform, team, local: false);
                if (_dancerRingTr != null && i < _dancerRingTr.Length) _dancerRingTr[i] = ringTr;   // 完奏特效要掛得到他腳下

                // 🔴 一定要跟本機舞者同一層。3D 舞台是**另一台相機**在畫的(SceneCam 的 cullingMask 只有 SceneLayer,
                // 主相機反過來把那層剔掉),留在 Default 層的話這一隻改由主正交相機畫 —— 那台的座標系是 800×600
                // design px,模型單位直接當像素 → 60 單位高的人變成貼在畫面上的 60px 小人,而且不受場景遮擋。
                // (回報:「進遊戲後其他玩家變超小」。條件與本機那條 3D 擺位路徑一致 —— 退回 2D 時兩邊都留在 Default。)
                if (sceneWorldMode) SetLayerRecursive(go, SceneLayer);

                _extraDancers.Add(av);
                _extraRoots.Add(go.transform);
            }

            // 本機那一格的錨點就是本機的星環(TryLoadAvatar 已經建好;旁觀時 localIdx = -1,沒有這一格)。
            if (localIdx >= 0 && localIdx < _dancerRingTr.Length) _dancerRingTr[localIdx] = _ringTr;

            // 名次換位用的 per-dancer 狀態。初值 = 各就各位(開場大家都 0 分 → 照座位序)。
            int n = total;
            _dancerCur = new Vector3[n];
            _dancerScores = new long[n];
            _dancerLeader = Sdo.Ruleset.FormationAssignment.LeaderSlot;
            _dancerDancing = new bool[n];
            _dancerPrevCounts = new Sdo.Ruleset.DanceJudgeCounts[n];
            _dancerTracks = new List<(double tMs, bool on)>[n];
            BuildBaseSlots(n);
            for (int i = 0; i < n; i++)
            {
                int bs = Mathf.Clamp(_dancerBaseSlot[i], 0, _slotSpots.Length - 1);
                _dancerCur[i] = _slotSpots[bs];
                _dancerDancing[i] = true;   // 開場都在跳(與本機的 _dancing 初值一致)
                _dancerTracks[i] = new List<(double tMs, bool on)>();   // 空 = 從頭到尾都在跳(與 RemoteGateAt 的預設一致)
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
        /// 目前占領隊格的舞者索引。分數接近時保留這個人,直到挑戰者跨過 FormationAssignment 的換位門檻。
        /// </summary>
        private int _dancerLeader;

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
            int[] slots;
            if (TeamMode)
            {
                slots = _dancerBaseSlot;
            }
            else
            {
                int authoritativeLeader = NetLeaderDancerIndex();
                _dancerLeader = Sdo.Ruleset.FormationAssignment.ResolveLeader(
                    _dancerScores, _dancerLeader, authoritativeLeader);
                slots = Sdo.Ruleset.FormationAssignment.SlotForDancer(_dancerScores, _dancerLeader);
            }

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
        /// 一位遠端舞者頭上的名字牌。3D 路徑與本機一樣進 SceneCam、保持固定螢幕大小並吃人物深度遮擋,
        /// 但**不畫箭頭** —— 那個箭頭在官方是「這是你」的指示物,每個人頭上都有一個就沒有意義了。
        /// </summary>
        private void CreateRemoteNameplate(SdoAvatar av, Transform root, string label, int team, bool sceneWorldMode)
        {
            int headIdx = av.BoneIndex("Bip01_Head");
            if (headIdx < 0) headIdx = av.BoneIndex("Bip01_Neck");
            Transform anchor = null;
            if (headIdx >= 0)
            {
                var ag = new GameObject("HeadAnchor");
                if (sceneWorldMode) ag.layer = SceneLayer;
                ag.transform.SetParent(root, false);
                av.AddAnchor(headIdx, ag.transform);
                anchor = ag.transform;
            }
            var go = new GameObject("RemoteNameplate_" + label);
            var hm = go.AddComponent<HeadMarker>();
            hm.Init(null, label,
                    depthTestedWorld: sceneWorldMode,
                    worldLayer: SceneLayer);   // null = 不要箭頭(見上面的理由)
            hm.SetTeamColor(team);  // 組隊局:名字染成他那一隊的顏色(與腳下星環同一個色)
            Transform a = anchor;
            Transform r = root;
            hm.AnchorGetter = () => a != null ? a.position
                : ((r != null ? r.position : Vector3.zero) + new Vector3(0f, 59f, 0f));
            hm.CamGetter = () => _sceneCam != null ? _sceneCam : _cam;
        }

        /// <summary>
        /// 一位遠端舞者的待機 clip(舞台 rest cat 0x15;穿飛行翅膀換成 flystay)。
        ///
        /// 🔴 **不能沿用 _sharedRestMot** —— 那是本機那位解析出來的:性別不同就是另一支 clip,而且本機穿翅膀時
        /// 它已經在 ConfigureAvatarGender 被換成 flystay 了。共用的話會兩個方向都錯:本機一穿翅膀,全場的人
        /// 待機都變飛行姿勢;本機沒穿時,真的穿翅膀的遠端玩家浮在空中卻擺站姿。
        ///
        /// 走 AvatarAssetCache(而不是 LoadAsset)是刻意的:它保證「同一路徑永遠是同一個 MotLoader 物件」,
        /// 而 SdoAvatar 判斷「動作換了嗎」是比物件參照 —— 每隻各 parse 一份會被誤判成換動作,多一次 crossfade。
        /// 路徑要絕對:AvatarAssetCache 底下是 File.ReadAllBytes。
        /// </summary>
        private static MotLoader RemoteRestMot(bool male, bool flying)
        {
            string rel = flying ? SpecialMotionItems.FlyIdleMot(male)
                                : (male ? MaleGameplayRestMot : FemaleGameplayRestMot);
            return AvatarAssetCache.Mot(System.IO.Path.Combine(
                SdoExtracted.Root, rel.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        }

        /// <summary>測試用:第 i 位舞者的 root(本機那一格 = <c>_avatarRoot</c>;還沒生出來 = null)。</summary>
        public Transform DancerRootForTest(int i) => i >= 0 && i < _extraRoots.Count ? _extraRoots[i] : null;

        /// <summary>本機在舞者陣列裡的索引(離線/量測時是 0;旁觀者是 -1)。</summary>
        private int LocalDancerSlotIndex
            => (netDancers != null && netDancers.Length > 0) ? localDancerIndex : 0;

        private int NetLeaderDancerIndex()
        {
            int userId = NetLeaderUserId != null ? NetLeaderUserId() : 0;
            if (userId <= 0 || netDancers == null) return -1;
            for (int i = 0; i < netDancers.Length; i++)
                if (netDancers[i].UserId == userId) return i;
            return -1;
        }

        private void FillDancerScores()
        {
            int local = LocalDancerSlotIndex;
            var opp = NetOpponents != null ? NetOpponents() : null;
            bool haveNames = netDancers != null && netDancers.Length > 0;

            for (int i = 0; i < _dancerScores.Length; i++)
            {
                if (i == local) { _dancerScores[i] = TotalScore; continue; }
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
            // 一格就收工的只有「離線/單人」那種場(那一格是本機自己)。旁觀者不占任何一格,
            // 場上只有一位參賽者時長度就是 1 —— 而那一位正是要推導跳/停的人,不能早退。
            if (_dancerDancing == null || (_dancerDancing.Length <= 1 && !spectatorMode)) return;
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
                bool next = Sdo.Ruleset.DanceGate.NextFromSamples(
                    _dancerDancing[i], _dancerPrevCounts[i], cur, combo);
                // 死了 / 人走了就不可能再跳(分數流推不出這兩件事 —— 它們只會表現成一連串空 block,
                // 而空 block 是「維持現況」)。
                _dancerDancing[i] = Sdo.Ruleset.DanceGate.RemoteEnabled(next, DeadRemote(opp, uid), LeftRemote(opp, uid));
                _dancerPrevCounts[i] = cur;
                RecordDancerGate(i, nowMs);   // 記給結算的背景回放用(本機的對應物是 RecordGate)
            }
        }

        /// <summary>
        /// 死亡 / 離場要**當場**停舞,不能等到下一個 8 拍結算點(那可能是好幾秒之後,而且對方已經
        /// 不再送 frame —— 結算點看到的是空 block,規則(3)會維持現況)。
        ///
        /// 只做單向:把還在跳的關掉。這兩件事在同一場裡都不可逆(死了不會復活、走了不會回來),
        /// 所以不需要、也不可以反過來把人叫起來跳。
        /// </summary>
        private void TickRemotePresence(double nowMs)
        {
            // 長度 1 的早退同 TickRemoteGates:旁觀時那一格是別人,照樣要判斷他有沒有離場。
            if (_dancerDancing == null || (_dancerDancing.Length <= 1 && !spectatorMode)) return;
            if (netDancers == null || netDancers.Length == 0) return;   // 離線/量測:遠端跟著本機
            var opp = NetOpponents != null ? NetOpponents() : null;
            if (opp == null) return;

            int local = LocalDancerSlotIndex;
            for (int i = 0; i < _dancerDancing.Length; i++)
            {
                if (i == local || i >= netDancers.Length) continue;
                if (!_dancerDancing[i]) continue;
                int uid = netDancers[i].UserId;
                if (Sdo.Ruleset.DanceGate.RemoteEnabled(true, DeadRemote(opp, uid), LeftRemote(opp, uid))) continue;
                _dancerDancing[i] = false;
                RecordDancerGate(i, nowMs);   // 回放也要看到他從這一刻起站著
            }
        }

        /// <summary>他的 HP 歸零了嗎(名單裡找不到他 = 還沒收到他的第一筆 frame → 當他活著)。</summary>
        private static bool DeadRemote(NetPlayerScore[] opp, int userId)
        {
            if (opp == null) return false;
            for (int i = 0; i < opp.Length; i++) if (opp[i].UserId == userId) return opp[i].Dead;
            return false;
        }

        /// <summary>他人已經不在這一場了嗎(中途 Esc 回房間 / 斷線)。找不到的處理同上。</summary>
        private static bool LeftRemote(NetPlayerScore[] opp, int userId)
        {
            if (opp == null) return false;
            for (int i = 0; i < opp.Length; i++) if (opp[i].UserId == userId) return opp[i].Left;
            return false;
        }

        /// <summary>把第 <paramref name="i"/> 位舞者這一刻的跳/停記進他自己那一軌 —— 只記變化,與本機
        /// <c>RecordGate</c> 同一套。gate 一個結算週期只變一次,所以這條軌整首歌也就幾十筆。</summary>
        private void RecordDancerGate(int i, double nowMs)
        {
            if (_dancerTracks == null || i < 0 || i >= _dancerTracks.Length) return;
            var tr = _dancerTracks[i];
            if (tr == null) return;
            if (tr.Count == 0 || tr[tr.Count - 1].on != _dancerDancing[i]) tr.Add((nowMs, _dancerDancing[i]));
        }

        /// <summary>第 <paramref name="i"/> 位舞者在譜面時間 <paramref name="tMs"/> 當下在不在跳
        /// (第一筆之前預設在跳,與本機 <c>GateAt</c> 一致)。回放迴圈每幀查一次。</summary>
        private bool RemoteGateAt(int i, double tMs)
        {
            if (_dancerTracks == null || i < 0 || i >= _dancerTracks.Length) return true;
            var tr = _dancerTracks[i];
            if (tr == null) return true;
            bool on = true;
            for (int k = 0; k < tr.Count; k++) { if (tr[k].tMs > tMs) break; on = tr[k].on; }
            return on;
        }

        private static Sdo.Ruleset.DanceJudgeCounts CountsOf(NetPlayerScore[] opp, int userId, out int combo)
        {
            combo = 0;
            if (opp == null) return default(Sdo.Ruleset.DanceJudgeCounts);
            for (int i = 0; i < opp.Length; i++)
                if (opp[i].UserId == userId) { combo = opp[i].Combo; return opp[i].Counts; }
            return default(Sdo.Ruleset.DanceJudgeCounts);
        }

        // ================= 結算:場上其他人的輸贏定格與背景回放 =================

        /// <summary>
        /// 分數最高的那一位舞者(-1 = 沒有名單)。<paramref name="skip"/> 那一格不參選。
        /// 平手的先後與 <see cref="Sdo.Ruleset.RankingBoard"/> 完全一致:分數高的先、同分**座位序**小的先
        /// (_dancerScores 的索引就是座位序)—— 名次面板與場上定格必須指向同一個人,而且每台算出來要是同一位。
        /// </summary>
        private int WinnerDancerIndex(int skip = -1)
        {
            if (_dancerScores == null || _dancerScores.Length == 0) return -1;
            int best = -1;
            for (int i = 0; i < _dancerScores.Length; i++)
            {
                if (i == skip) continue;
                if (best < 0 || DancerOutranks(i, best)) best = i;
            }
            return best;
        }

        private bool DancerOutranks(int a, int b)
        {
            if (_dancerScores[a] != _dancerScores[b]) return _dancerScores[a] > _dancerScores[b];
            return a < b;   // 同分照座位序(RankingBoard.Compare / server 的 ResultRowOrder 都是這條)
        }

        /// <summary>
        /// 場上其他人的輸贏定格(第一名 cat5,其餘 cat4),與本機同一個時機、同一套動作。
        ///
        /// 🔴 clip 一定要挑**他自己性別**那一支,而且要走 <c>ResolveMotFor</c>:一般的 ResolveMot 會拿本機性別
        /// 去做 W→M 映射,本機是男生時女生玩家的 WWIN0002 會被換成 MWIN0002 —— 男版動作套在女骨架上就是一團扭曲。
        /// </summary>
        /// <param name="redo">true = 權威名次晚到的重演。贏家沒換就**什麼都不做** —— PlayOneShot 會把
        /// 計時歸零,已經停在最後一幀的定格會倒回第 0 幀再演一次(玩家看得一清二楚)。真的換人時走硬切。</param>
        private void PlayRemoteFinishPoses(bool redo = false)
        {
            if (_dancerScores == null || _extraDancers.Count == 0) return;
            FillDancerScores();               // 用最後一筆分數流定名次(這一幀 TickDancerSlots 也填過,重填不花錢)
            int winner = WinnerDancerIndex();
            // 🔴 場上**恰好一個**贏家,而且要是名次面板上的那一個。_localWon 走的是 _roster(server 的對手清單),
            // 這裡走的是 _dancerScores(照 netDancers 的 userId 去查同一份分數)—— 兩份名單在有人中途離開時
            // 可能對不齊,對不齊就會變成「兩個人一起做勝利動作」或「一個都沒有」。以面板為準:
            int local = LocalDancerSlotIndex;
            int netWinner = NetWinnerDancerIndex();   // server 權威的第一名(有的話,兩台看到的贏家一定同一位)
            if (netWinner >= 0) winner = netWinner;
            else if (_localWon) winner = local;                              // 面板說本機贏 → 其他人一律 cat4
            else if (winner == local) winner = WinnerDancerIndex(skip: local);   // 面板說本機沒贏 → 贏家在別人裡挑
            if (redo && winner == _remoteFinishWinner) return;   // 贏家沒換 → 不要把別人的定格倒回去重演
            _remoteFinishWinner = winner;
            for (int i = 0; i < _extraDancers.Count; i++)
            {
                var av = _extraDancers[i];
                if (av == null) continue;     // 本機那格是 null 佔位(本機的定格由 EnterResult 自己放)
                bool male = netDancers != null && i < netDancers.Length ? netDancers[i].Male : localPlayerMale;
                var mot = ResolveMotFor(FinishMot(male, i == winner), male);
                if (mot == null) continue;
                if (redo) av.SnapNextClip();                 // 定格→定格:硬切(0.5s crossfade 只會糊成一團)
                av.PlayOneShot(mot, true);                   // hold = 停在最後一幀(定格)
            }
        }

        /// <summary>上一次遠端定格用的贏家索引(-1 = 還沒放過)。權威名次晚到時靠它判斷「要不要重演」。</summary>
        private int _remoteFinishWinner = -1;

        /// <summary>每位舞者腳下星環的 transform(= 特效錨點)。索引 = 舞者序;本機那格 = <c>_ringTr</c>。</summary>
        private Transform[] _dancerRingTr;

        /// <summary>
        /// FINISHED(完奏特效)要掛在誰腳下 —— **場上的第一名**,不論那是本機還是別人。
        ///
        /// 🔴 以前的條件是「本機贏才放」,而且錨點寫死本機的 _ringTr。那在只渲染本機一隻舞者的年代講得通,
        /// 現在場上每個人都在 —— 別人贏的時候整場沒有完奏特效,而**旁觀者永遠看不到它**
        /// (它的 _localWon 恆 false;使用者回報「結算動作的 win 的 particle 也沒看到」)。
        ///
        /// 贏家取自 <see cref="PlayRemoteFinishPoses"/> 剛定案的那一位(_remoteFinishWinner) ——
        /// 與場上做勝利動作的必須是同一個人,分兩處各算一次遲早會不一致。
        /// 場上沒有其他舞者(離線/單人)時退回「本機贏就掛本機」。
        /// </summary>
        private Transform FinishedEftAnchor()
        {
            int w = _remoteFinishWinner;
            if (w >= 0 && _dancerRingTr != null && w < _dancerRingTr.Length && _dancerRingTr[w] != null)
                return _dancerRingTr[w];
            return _localWon ? _ringTr : null;
        }

        /// <summary>輸贏定格用哪一支 clip。本機那兩個欄位(winMot/loseMot)是同一組值的「本機性別」版。</summary>
        private static string FinishMot(bool male, bool won)
            => won ? (male ? MaleWinMot : FemaleWinMot) : (male ? MaleLoseMot : FemaleLoseMot);

        /// <summary>
        /// 結算的背景回放:場上其他人跟著本機一起把這一場再跳一遍。
        ///
        /// 🔴 漏掉的原因藏在生成那一步 —— <c>av.DanceTimeSec = _avatar.DanceTimeSec</c> 複製的是**當下那一顆
        /// delegate**,不是「永遠跟著本機」。<c>StartBackgroundReplay</c> 把本機換成迴圈時鐘之後,遠端手上還是舊的
        /// 那顆歌曲時鐘;歌早就結束(時間已走過編舞尾端)→ 他們一律站著待機。所以這裡要重指一次。
        ///
        /// 跳/停用他**自己**那一軌(<see cref="RemoteGateAt"/>),與本機用 _danceTrack 是同一個道理:
        /// 回放要回放的正是「這一場每個人各自斷在哪幾段」,不是「大家從頭跳到尾」。
        /// </summary>
        private void StartRemoteBackgroundReplay(System.Func<float> loopTimeSec)
        {
            for (int i = 0; i < _extraDancers.Count; i++)
            {
                var av = _extraDancers[i];
                if (av == null) continue;
                av.ClearOneShot();     // 收掉輸贏定格,回到 DPS 舞蹈那條路
                av.SnapNextClip();     // 定格 → 回放走硬切,不做平滑過場(與本機同一個處理)
                if (av.Dps == null) continue;   // 沒有編舞可跳(資產缺)→ 留在待機,別掛一組永遠回 true 的閘門
                int me = i;
                av.DanceTimeSec = loopTimeSec;
                av.DanceEnabled = () => RemoteGateAt(me, LoopMs());
            }
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
