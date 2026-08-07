using System;
using System.Collections.Generic;
using Sdo.Ruleset;
using UnityEngine;

namespace Sdo.Game
{
    public sealed partial class ScreenGameplay
    {
        /// <summary>Raised at every exact 50-combo boundary for reliable network relay.</summary>
        public Action<int> LocalComboMilestone;

        private readonly Dictionary<int, PlayingEmoji> _remoteComboEmojis =
            new Dictionary<int, PlayingEmoji>();
        private readonly Dictionary<int, Transform> _remoteComboAnchors =
            new Dictionary<int, Transform>();

        private void NotifyLocalComboMilestone(Judgment judgment)
        {
            if (judgment != Judgment.Perfect && judgment != Judgment.Cool) return;
            int combo = _score != null ? _score.Combo : 0;
            if (combo >= 50 && combo % 50 == 0)
                LocalComboMilestone?.Invoke(combo);
        }

        /// <summary>Plays the remote dancer's exact 50/100-combo one-shot visuals.</summary>
        public void PlayRemoteComboMilestone(int userId, int combo)
        {
            if (userId == 0 || combo < 50 || combo % 50 != 0) return;
            // 🔴 擋的只有**已經離場**的人(人都回房間了,那則 milestone 是他走之前送出、路上還在飛的)。
            //
            // **死掉的人照冒表情與火** —— 完奏模式血用完歌不切斷,他還在打、combo 還在累積,
            // 那些 combo 是真的。死亡只讓他**停舞**(見 TickRemotePresence),不是把他從場上抹掉。
            var opp = NetOpponents != null ? NetOpponents() : null;
            if (LeftRemote(opp, userId)) return;
            if (!TryGetDancerRoot(userId, out var root, out var avatar)) return;

            EmojiKind kind = EmojiTriggers.ComboMilestone(combo);
            if (kind != EmojiKind.None)
            {
                var emoji = GetRemoteComboEmoji(userId, root, avatar);
                var frames = FramesFor(kind);
                if (emoji != null && frames != null && frames.Length > 0)
                    emoji.Play(frames, EmojiLoops(kind));
            }

            if (effectCharacter && combo % 100 == 0)
            {
                var anchor = GetRemoteComboAnchor(userId, root);
                SpawnComboBurst(Mathf.Clamp(combo / 100 - 1, 0, 4), anchor);
            }
        }

        /// <summary><paramref name="avatar"/> ＝ 那一格的 SDO 驅動器(本機那一格是 null) ——
        /// 表情要問它「這個人身上的 MMD 模型比 SDO 高多少」(<see cref="MmdAvatarSwap.HeadroomFor"/>)。</summary>
        private bool TryGetDancerRoot(int userId, out Transform root, out SdoAvatar avatar)
        {
            root = null; avatar = null;
            if (netDancers == null) return false;
            for (int i = 0; i < netDancers.Length; i++)
            {
                if (netDancers[i].UserId != userId) continue;
                if (i >= 0 && i < _extraRoots.Count) root = _extraRoots[i];
                if (i >= 0 && i < _extraDancers.Count) avatar = _extraDancers[i];
                return root != null;
            }
            return false;
        }

        private PlayingEmoji GetRemoteComboEmoji(int userId, Transform root, SdoAvatar avatar)
        {
            if (_remoteComboEmojis.TryGetValue(userId, out var existing) && existing != null)
                return existing;

            var go = new GameObject("RemoteHeadEmoji_" + userId);
            go.transform.SetParent(transform, false);
            if (use3dCamera) go.layer = SceneLayer;
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sortingOrder = 50;
            sr.enabled = false;
            var emoji = go.AddComponent<PlayingEmoji>();
            emoji.sr = sr;
            emoji.SlotGetter = () => root != null ? root.position : Vector3.zero;
            // 他把模型放大了 → 他的表情也要跟著往上(yOff 是照 SDO 舞者的頭高訂的,見 MmdHeadroom)。
            var av = avatar;
            emoji.RiseGetter = () => MmdAvatarSwap.HeadroomFor(av);
            emoji.CamGetter = () => _sceneCam != null ? _sceneCam : _cam;
            _remoteComboEmojis[userId] = emoji;
            return emoji;
        }

        private Transform GetRemoteComboAnchor(int userId, Transform root)
        {
            if (_remoteComboAnchors.TryGetValue(userId, out var existing) && existing != null)
                return existing;

            var go = new GameObject("RemoteComboAnchor_" + userId);
            go.transform.position = new Vector3(root.position.x, 0.6f, root.position.z);
            go.transform.SetParent(root, true);
            _remoteComboAnchors[userId] = go.transform;
            return go.transform;
        }

    }
}
