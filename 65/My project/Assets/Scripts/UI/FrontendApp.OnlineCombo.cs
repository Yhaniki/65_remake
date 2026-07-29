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
    }
}
