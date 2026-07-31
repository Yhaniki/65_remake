using System;
using System.Collections.Generic;

namespace Sdo.UI.Core
{
    /// <summary>Front-end screens. Settings is a modal overlay, not a screen state.
    /// GenderSel is the standalone opening 男/女 select screen (entry state). Shop is the 商城 screen.
    /// New members appended to keep values stable.</summary>
    public enum ScreenId { Lobby, Room, SongSelect, Gameplay, GenderSel, Shop }

    /// <summary>
    /// Pure-logic screen state machine. Validates transitions against an allowed-edges table and
    /// raises <see cref="ScreenChanged"/>. No Unity dependency, so it is unit-testable.
    /// </summary>
    public sealed class FlowManager
    {
        private static readonly Dictionary<ScreenId, HashSet<ScreenId>> Allowed =
            new Dictionary<ScreenId, HashSet<ScreenId>>
            {
                // 選完男女按「登入」:連上 → 大廳(線上);連不上/沒設伺服器 → 照原本直接進自己的房間(單機)。
                { ScreenId.GenderSel, new HashSet<ScreenId> { ScreenId.Room, ScreenId.Lobby } },
                // 大廳 → 房間(建房或點列表加入);大廳 → 選角色是「登出」(斷線退回單機)。
                { ScreenId.Lobby, new HashSet<ScreenId> { ScreenId.Room, ScreenId.GenderSel } },
                { ScreenId.Room, new HashSet<ScreenId> { ScreenId.GenderSel, ScreenId.Lobby, ScreenId.SongSelect, ScreenId.Gameplay, ScreenId.Shop } },
                { ScreenId.SongSelect, new HashSet<ScreenId> { ScreenId.Room } },
                // Gameplay → GenderSel 是旁觀的 Ctrl+Q(需求 10):旁觀者直接離開房間回選角色,不繞回房間畫面。
                { ScreenId.Gameplay, new HashSet<ScreenId> { ScreenId.Room, ScreenId.GenderSel } },
                { ScreenId.Shop, new HashSet<ScreenId> { ScreenId.Room } },
            };

        // 單機開場落在男/女選擇畫面（GenderSel）。選完 → Room。
        public ScreenId Current { get; private set; } = ScreenId.GenderSel;

        /// <summary>(from, to)</summary>
        public event Action<ScreenId, ScreenId> ScreenChanged;

        public bool CanGoTo(ScreenId target)
            => target == Current
               || (Allowed.TryGetValue(Current, out var set) && set.Contains(target));

        public bool GoTo(ScreenId target)
        {
            if (target == Current) return true;
            if (!CanGoTo(target)) return false;
            var from = Current;
            Current = target;
            ScreenChanged?.Invoke(from, target);
            return true;
        }
    }
}
