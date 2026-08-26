namespace Supercent.Common.TransformPath
{
    public partial class QueuedPathManager : IPathQueue
    {
        #region Member Variables

        private readonly PathQueueRegistry _queueRegistry = new PathQueueRegistry();

        #endregion


        #region Properties

        int IPathQueue.AgentCount => _queueRegistry.Count;

        #endregion


        #region Public Methods

        void IPathQueue.Register(IQueuedPathAgent agent)
        {
            if (agent is QueuedPathFollower concreteFollower)
            {
                Register(concreteFollower);
                return;
            }

            _queueRegistry.Register(agent);
        }

        void IPathQueue.Unregister(IQueuedPathAgent agent)
        {
            if (agent is QueuedPathFollower concreteFollower)
            {
                Unregister(concreteFollower);
                return;
            }

            _queueRegistry.Unregister(agent);
        }

        bool IPathQueue.ShouldBlock(IQueuedPathAgent agent)
        {
            float distance = ((IPathQueue)this).GetDistanceToAhead(agent);
            return distance >= 0f && QueuedPathBlockingHelper.ShouldStartBlocking(distance, GetEffectiveSpacing(agent));
        }

        float IPathQueue.GetDistanceToAhead(IQueuedPathAgent agent)
        {
            IQueuedPathAgent ahead = _queueRegistry.GetAhead(agent);
            if (ahead == null || TotalPathLength <= 0f)
                return -1f;

            float normalizedDifference = ahead.GlobalNormalizedTime - agent.GlobalNormalizedTime;
            return normalizedDifference >= 0f ? normalizedDifference * TotalPathLength : -1f;
        }

        float IPathQueue.GetSpeedMultiplier(IQueuedPathAgent agent)
        {
            if (!_enableGradualSlowdown || !agent.EnableGradualSlowdown)
                return 1f;

            float distance = ((IPathQueue)this).GetDistanceToAhead(agent);
            if (distance < 0f)
                return 1f;

            float spacing = GetEffectiveSpacing(agent);
            if (distance >= _slowdownStartDistance)
                return 1f;
            if (distance <= spacing)
                return 0f;

            float range = _slowdownStartDistance - spacing;
            if (range <= 0f)
                return 1f;

            float normalizedDistance = (distance - spacing) / range;
            float curveValue = _slowdownCurve != null ? _slowdownCurve.Evaluate(normalizedDistance) : normalizedDistance;
            return UnityEngine.Mathf.Lerp(_minSpeedMultiplier, 1f, curveValue);
        }

        float IPathQueue.GetClampedNormalizedTime(IQueuedPathAgent agent, float targetNormalizedTime)
        {
            IQueuedPathAgent ahead = _queueRegistry.GetAhead(agent);
            if (ahead == null)
                return UnityEngine.Mathf.Clamp01(targetNormalizedTime);

            float spacingNormalized = TotalPathLength > 0f ? GetEffectiveSpacing(agent) / TotalPathLength : 0f;
            float maxNormalizedTime = ahead.GlobalNormalizedTime - spacingNormalized;
            return UnityEngine.Mathf.Clamp01(UnityEngine.Mathf.Min(targetNormalizedTime, maxNormalizedTime));
        }

        void IPathQueue.NotifySortNeeded()
        {
            _queueRegistry.NotifySortNeeded();
            NotifySortNeeded();
        }

        #endregion


        #region Private Methods

        private float GetEffectiveSpacing(IQueuedPathAgent agent)
            => agent.UseManagerSpacing ? _defaultSpacing : UnityEngine.Mathf.Max(0f, agent.ActorSpacing);

        #endregion
    }
}
