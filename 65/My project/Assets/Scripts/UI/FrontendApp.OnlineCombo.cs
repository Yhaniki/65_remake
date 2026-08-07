using Sdo.Net;

namespace Sdo.UI
{
    public sealed partial class FrontendApp
    {
        private void OnNetComboMilestone(NetComboMilestone milestone)
        {
            if (_activeGame != null)
                _activeGame.PlayRemoteComboMilestone(milestone.UserId, milestone.Combo);
        }

        /// <summary>房裡有人按 SPACE 放 ShowTime → 在場上那隻舞者身上重現光環 + 街舞。</summary>
        private void OnNetShowtimeRelease(NetShowtimeRelease release)
        {
            if (_activeGame != null)
                _activeGame.PlayRemoteShowtime(release.UserId, release.Level, release.Variant, release.WindowMs);
        }
    }
}
