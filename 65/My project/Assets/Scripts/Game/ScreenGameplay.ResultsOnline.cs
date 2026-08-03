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
            int place = 0;
            for (int i = 0; rows != null && i < rows.Length; i++)
            {
                if (!rows[i].IsLocal) continue;
                place = rows[i].Rank > 0 ? rows[i].Rank : i + 1;
                break;
            }

            int players = Mathf.Max(1, rows != null ? rows.Length : 0);
            localWon = !spectatorMode && place == 1;
            if (!spectatorMode && place > 0) _localWon = localWon;
            if (place <= 0) place = players;

            int bad = _score != null ? _score.BadCount : 0;
            int miss = _score != null ? _score.MissCount : 0;
            bool noReward = freeMode || spectatorMode;
            expGained = noReward ? 0 : Sdo.Ruleset.Reward.Experience(bad, miss, place, players);
            coinsGained = noReward ? 0 : Sdo.Ruleset.Reward.Coins(bad, miss, place, players, playerLevel);
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
                    portrait.headFrameDist = 1.9f;
                    portrait.headAimUp = 0.25f;
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
