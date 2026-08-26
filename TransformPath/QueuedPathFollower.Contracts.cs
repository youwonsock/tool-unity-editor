using System;

namespace Supercent.Common.TransformPath
{
    public partial class QueuedPathFollower : IQueuedPathAgent
    {
        #region Properties

        IPathFollower IQueuedPathAgent.PathFollower => _pathFollower;
        UnityEngine.Object IQueuedPathAgent.UnityOwner => this;

        #endregion


        #region Public Methods

        public bool TryStartMove(IPathProvider provider, PathMoveSettings settings, Action onComplete = null)
        {
            if (!EnsurePathFollowerReady())
                return false;

            _onComplete = onComplete;
            PrepareMoveStart();

            if (!(_pathFollower is IPathFollower follower) || !follower.TryStartMove(provider, settings))
            {
                ResetMoveState();
                _onComplete = null;
                return false;
            }

            _isMoving = true;
            return true;
        }

        #endregion
    }
}
