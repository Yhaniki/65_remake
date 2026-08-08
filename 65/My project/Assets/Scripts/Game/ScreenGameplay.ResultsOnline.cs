using System;
using UnityEngine;

namespace Sdo.Game
{
    public sealed partial class ScreenGameplay
    {
        private ResultScreen.Row[] PrepareResultRows()
        {
            var rows = BuildResultRows() ?? new ResultScreen.Row[0];
            AttachResultHeadPortraits(rows);
            return rows;
        }

        private void CalculateResultOutcome(ResultScreen.Row[] rows, out bool localWon,
                                            out int expGained, out int coinsGained)
        {
            int place = 0;        // 嚴格名次(同分也分先後)→ 只有它能決定「誰做勝利動作」(場上只能有一個贏家)
            int shownPlace = 0;   // 畫面上寫的名次(同分並列、不跳號)→ 獎勵照它算,面板寫第幾名就領第幾名的
            for (int i = 0; rows != null && i < rows.Length; i++)
            {
                if (!rows[i].IsLocal) continue;
                place = rows[i].Rank > 0 ? rows[i].Rank : i + 1;
                shownPlace = rows[i].DisplayRank > 0 ? rows[i].DisplayRank : place;
                break;
            }

            int players = Mathf.Max(1, rows != null ? rows.Length : 0);
            if (!spectatorMode && place > 0)
            {
                // 場上的勝利定格/短曲只能有一個第一名(嚴格 place,平手照座位序)—— 兩台不能同時演勝利動作。
                _localWon = place == 1;
                // 戰績用的「贏」是**並列第一也算**(使用者指定:同分兩邊都記勝場)。
                // 見 ScreenGameplay.LocalWonForRecord —— 這三件事(定格 / 戰績 / 結算旗)刻意各走各的。
                _localWonForRecord = LocalTiedForTopRow(rows);
            }
            if (shownPlace <= 0) shownPlace = players;   // 找不到本機(旁觀)→ 按最後一名算(獎勵那邊本來就是 0)
            // 結算面板的 YOU WIN 旗:**畫面上的名次**(同分並列)排進前半就算贏(使用者指定)——
            // 兩個人打平就兩個都是第一名、兩台都出 YOU WIN;6 人局前三名都出。規則見 RankingBoard.IsWinningPlace。
            localWon = !spectatorMode && Sdo.Ruleset.RankingBoard.IsWinningPlace(shownPlace, players);

            int bad = _score != null ? _score.BadCount : 0;
            int miss = _score != null ? _score.MissCount : 0;
            // 🔴 自由模式**照給** G幣/EXP(使用者指定)。freeMode 只管「藏名次」——
            // 名次仍然照算(shownPlace 是拿得到的),只是不畫出來,獎勵就照那個名次發。
            // 沒有獎勵的只有旁觀者:它根本沒下場(而且 place 會是 0 = 名單裡找不到本機)。
            bool noReward = spectatorMode;
            expGained = noReward ? 0 : Sdo.Ruleset.Reward.Experience(bad, miss, shownPlace, players);
            coinsGained = noReward ? 0 : Sdo.Ruleset.Reward.Coins(bad, miss, shownPlace, players, playerLevel);
        }

        /// <summary>本機的分數有沒有並列第一(權威結果列版;沒有本機那一列 = false)。
        /// 規則與 <c>RankingBoard.LocalTiedForTop</c> 同一條,只是資料來源是結算列而不是右側名單。</summary>
        private static bool LocalTiedForTopRow(ResultScreen.Row[] rows)
        {
            long top = long.MinValue, local = 0L;
            bool haveLocal = false;
            for (int i = 0; rows != null && i < rows.Length; i++)
            {
                if (rows[i].Score > top) top = rows[i].Score;
                if (rows[i].IsLocal && !haveLocal) { local = rows[i].Score; haveLocal = true; }
            }
            return haveLocal && local >= top;
        }

        /// <summary>
        /// 右側名單換成 server 的權威結果(false = 權威結果還沒到,名單不動)。
        /// 平手序直接用**列的順序**當 key —— 那一份已經是 server 照 (score, seat, userId) 排好的,
        /// 照抄就與面板、與另一台完全一致。
        /// </summary>
        private bool RosterFromNetRows()
        {
            var rows = NetResultRows != null ? NetResultRows() : null;
            if (rows == null || rows.Length == 0) return false;
            _roster.Clear();
            for (int i = 0; i < rows.Length && _roster.Count < RosterRows; i++)
                _roster.Add(new Sdo.Ruleset.PlayerEntry(rows[i].Name ?? "", rows[i].Score, rows[i].IsLocal, i));
            return true;
        }

        /// <summary>本機在 server 權威結果裡的名次(1-based);0 = 權威結果還沒到 / 名單裡沒有本機(旁觀)。</summary>
        private int NetLocalPlace()
        {
            var rows = NetResultRows != null ? NetResultRows() : null;
            if (rows == null) return 0;
            for (int i = 0; i < rows.Length; i++)
                if (rows[i].IsLocal) return rows[i].Rank > 0 ? rows[i].Rank : i + 1;
            return 0;
        }

        /// <summary>server 權威結果裡的第一名 userId;0 = 還沒到。旁觀者靠這條決定誰做勝利動作。</summary>
        private int NetWinnerUserId()
        {
            var rows = NetResultRows != null ? NetResultRows() : null;
            if (rows == null || rows.Length == 0) return 0;
            int best = -1;
            for (int i = 0; i < rows.Length; i++)
            {
                int rank = rows[i].Rank > 0 ? rows[i].Rank : i + 1;
                if (best < 0 || rank < (rows[best].Rank > 0 ? rows[best].Rank : best + 1)) best = i;
            }
            return best >= 0 ? rows[best].UserId : 0;
        }

        /// <summary>權威第一名在舞者陣列裡的索引;-1 = 沒有權威結果 / 那個人不在場上名單裡。</summary>
        private int NetWinnerDancerIndex()
        {
            int userId = NetWinnerUserId();
            if (userId == 0 || netDancers == null) return -1;
            for (int i = 0; i < netDancers.Length; i++)
                if (netDancers[i].UserId == userId) return i;
            return -1;
        }

        /// <summary>
        /// Refreshes an already-visible result panel when the server's final rows arrive after
        /// the local finish animation. Before the panel opens, the backing delegate is enough.
        /// </summary>
        public void RefreshNetResultRows()
        {
            if (_result == null || !_result.Visible) return;
            var rows = PrepareResultRows();
            CalculateResultOutcome(rows, out bool localWon, out int expGained, out int coinsGained);
            Texture localHead = spectatorMode ? null : BuildLocalHeadPortrait();
            _result.ReplaceRows(rows, localWon, expGained, coinsGained, localHead);
        }

        private void AttachResultHeadPortraits(ResultScreen.Row[] rows)
        {
            if (!resultHeadPortrait || rows == null) return;
            for (int i = 0; i < rows.Length; i++)
            {
                var row = rows[i];
                if (row.IsLocal) continue;
                if (!TryFindResultDancer(row, out var dancer, out int dancerIndex)) continue;

                if (row.UserId == 0) row.UserId = dancer.UserId;
                int key = dancer.UserId != 0 ? dancer.UserId : -(dancerIndex + 1);
                if (!_resultHeadPortraits.TryGetValue(key, out var portrait) || portrait == null)
                {
                    var go = new GameObject("ResultHeadPortrait" + key);
                    go.transform.SetParent(transform, false);
                    portrait = go.AddComponent<RoomHeadPortrait>();
                    portrait.layer = headPortraitLayer;
                    portrait.parkSpot = HeadAvatarSpot + new Vector3((dancerIndex + 1) * 1000f, 0f, 0f);
                    portrait.fov = headPortraitFov;
                    portrait.pitchDeg = headPitchDeg;
                    portrait.yaw = headAvatarYaw;
                    portrait.avatarScale = headAvatarScale;
                    portrait.zoom = headZoom;
                    // 結算列的每一格頭貼都要**同一個取景**:只對頭骨(不量任何 mesh)、而且不演飛行 idle。
                    // 這兩條就是本機那一列的作法(UpdateHeadPortraitCam);少了它們,穿飛行翅膀的人會用
                    // flystay(浮空前傾)的姿勢去量臉框 → 相機高度/距離跟別人不一樣(使用者回報的頭貼高低差)。
                    portrait.boneFraming = true;
                    portrait.boneAimOffset = headAimOffset;
                    portrait.boneDistModel = headPortraitDist;
                    portrait.groundClipsOnly = true;
                    // 待機:整排同一支(**舞台**待機,不是大廳待機),見 ResultRowRestMot。
                    // 不照 dancer.Male 挑 —— 一男一女就會變成兩套動作,那正是「頭貼沒有同步擺動」。
                    // 順帶一提取景基準(頭骨第 0 幀的位置)也因此在每一格都一樣。
                    portrait.idleMotOverride = ResultRowRestMot;
                    portrait.fitHairTop = false;
                    // 這是**別人**的頭貼 → MMD 走他自己宣告的模型(空＝他沒穿,就照他的 SDO 穿搭)。
                    // 不設的話這一格會走「本機」那條,整排結算頭貼都會戴上我選的模型。
                    portrait.remoteMmdRef = dancer.MmdRef ?? "";
                    // 布料不開(clothSim 預設 false):MMD 的頭髮在這一格是剛性跟著頭骨走的 —— 使用者決定
                    // 頭貼一律不付布料的成本,那是建一具 rig 最貴的一段(見 RoomHeadPortrait.clothSim)。
                    portrait.rtWidth = 192;
                    portrait.rtHeight = 216;
                    if (!portrait.Init(dancer.Male, dancer.Parts, dancer.BodyIndex))
                    {
                        go.SetActive(false);
                        Destroy(go);
                        portrait = null;
                    }
                    else
                    {
                        _resultHeadPortraits[key] = portrait;
                    }
                }

                if (portrait != null) row.Head = portrait.Texture;
                rows[i] = row;
            }
        }

        /// <summary>把結算頭貼的取景參數推回每一格(F4 滑桿是活的,而 RoomHeadPortrait 讀的是自己那份複本)。
        /// 「每一列頭貼共用同一組取景」這個不變量,要在調整當下也成立 —— 否則一拉滑桿就只有本機那一列會動。
        /// 每幀跑一次,幾格而已(見 <see cref="ResultTick"/>)。</summary>
        private void SyncResultHeadPortraitTuning()
        {
            if (_resultHeadPortraits.Count == 0) return;
            foreach (var kv in _resultHeadPortraits)
            {
                var p = kv.Value;
                if (p == null) continue;
                p.fov = headPortraitFov;
                p.pitchDeg = headPitchDeg;
                p.yaw = headAvatarYaw;
                p.avatarScale = headAvatarScale;
                p.zoom = headZoom;
                p.boneAimOffset = headAimOffset;
                p.boneDistModel = headPortraitDist;
                // 待機不必每幀比:整排是同一個常數(ResultRowRestMot),建立時就定案,之後沒有任何事
                // (換 MMD、模型下載完、F8 開關)會讓它需要換 clip —— 換 clip 正是不同步的來源。
            }
        }

        private bool TryFindResultDancer(ResultScreen.Row row, out DancerInfo dancer, out int index)
        {
            dancer = default(DancerInfo);
            index = -1;
            if (netDancers == null) return false;

            if (row.UserId != 0)
            {
                for (int i = 0; i < netDancers.Length; i++)
                {
                    if (netDancers[i].UserId != row.UserId) continue;
                    dancer = netDancers[i];
                    index = i;
                    return true;
                }
            }

            if (!string.IsNullOrEmpty(row.Name))
            {
                for (int i = 0; i < netDancers.Length; i++)
                {
                    if (!string.Equals(netDancers[i].Name, row.Name, StringComparison.Ordinal)) continue;
                    dancer = netDancers[i];
                    index = i;
                    return true;
                }
            }

            return false;
        }
    }
}
