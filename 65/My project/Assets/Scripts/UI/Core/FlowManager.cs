using System;
using System.Collections.Generic;

namespace Sdo.UI.Core
{
    /// <summary>Front-end screens. Settings is a modal overlay, not a screen state.</summary>
    public enum ScreenId { Lobby, Room, SongSelect, Gameplay, Shop }

    /// <summary>
    /// Pure-logic screen state machine. Validates transitions against an allowed-edges table and
    /// raises <see cref="ScreenChanged"/>. No Unity dependency, so it is unit-testable.
    /// </summary>
    public sealed class FlowManager
    {
        private static readonly Dictionary<ScreenId, HashSet<ScreenId>> Allowed =
            new Dictionary<ScreenId, HashSet<ScreenId>>
            {
                { ScreenId.Lobby, new HashSet<ScreenId> { ScreenId.Room } },
                { ScreenId.Room, new HashSet<ScreenId> { ScreenId.Lobby, ScreenId.SongSelect, ScreenId.Gameplay, ScreenId.Shop } },
                { ScreenId.SongSelect, new HashSet<ScreenId> { ScreenId.Room } },
                { ScreenId.Gameplay, new HashSet<ScreenId> { ScreenId.Room } },
                { ScreenId.Shop, new HashSet<ScreenId> { ScreenId.Room } },
            };

        public ScreenId Current { get; private set; } = ScreenId.Lobby;

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
