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
            int place = 0;        // 嚴格名次(同分也分先後)→ 只有它能決定「誰做勝利動作 / 出 WIN 旗」
            int shownPlace = 0;   // 畫面上寫的名次(同分並列、不跳號)→ 獎勵照它算,面板寫第幾名就領第幾名的
            for (int i = 0; rows != null && i < rows.Length; i++)
            {
                if (!rows[i].IsLocal) continue;
                place = rows[i].Rank > 0 ? rows[i].Rank : i + 1;
                shownPlace = rows[i].DisplayRank > 0 ? rows[i].DisplayRank : place;
                break;
            }

            int players = Mathf.Max(1, rows != null ? rows.Length : 0);
            localWon = !spectatorMode && place == 1;
            if (!spectatorMode && place > 0)
            {
                _localWon = localWon;
                // 戰績用的「贏」是**並列第一也算**(使用者指定:同分兩邊都記勝場)。輸贏定格與 WIN/LOSE 旗
                // 仍然只有一個第一名(嚴格 place)—— 這幾件事刻意不同,見 ScreenGameplay.LocalWonForRecord。
                _localWonForRecord = LocalTiedForTopRow(rows);
            }
            if (shownPlace <= 0) shownPlace = players;   // 找不到本機(旁觀)→ 按最後一名算(獎勵那邊本來就是 0)

            int bad = _score != null ? _score.BadCount : 0;
            int miss = _score != null ? _score.MissCount : 0;
            bool noReward = freeMode || spectatorMode;
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
                    // 待機也要跟本機那一列同一支(**舞台**待機,不是大廳待機)—— 不然同一排頭像裡
                    // 只有自己的動作不一樣,而且取景基準(頭骨第 0 幀的位置)也差一截。
                    portrait.idleMotOverride = dancer.Male ? MaleGameplayRestMot : FemaleGameplayRestMot;
                    portrait.fitHairTop = false;
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
