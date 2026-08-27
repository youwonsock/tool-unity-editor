using System.Collections.Generic;
using UnityEngine;

namespace Supercent.Common.TransformPath
{
    /// <summary>
    /// 같은 경로 위의 QueuedPathFollower들을 관리하고 충돌 감지를 수행하는 매니저
    /// </summary>
    public class QueuedPathManager : MonoBehaviour, IPathQueue
    {
        #region Constants

        private const float MIN_SPACING = 0f;
        private const float MIN_SLOWDOWN_START_DISTANCE = 0f;
        private const float MIN_SPEED_MULTIPLIER = 0f;
        private const float MAX_SPEED_MULTIPLIER = 1f;
        private const float DEFAULT_SPACING = 1.5f;
        private const float DEFAULT_SLOWDOWN_START_DISTANCE = 3f;
        private const float DEFAULT_MIN_SPEED_MULTIPLIER = 0.1f;

        #endregion


        #region Queue Manager State

        [Header("간격 설정")]
        [SerializeField] private float _defaultSpacing = DEFAULT_SPACING;

        [Header("감속 설정")]
        [SerializeField] private bool _enableGradualSlowdown = true;
        [SerializeField] private float _slowdownStartDistance = DEFAULT_SLOWDOWN_START_DISTANCE;
        [SerializeField] private float _minSpeedMultiplier = DEFAULT_MIN_SPEED_MULTIPLIER;
        [SerializeField] private AnimationCurve _slowdownCurve = AnimationCurve.Linear(0, 0, 1, 1);

        [Header("경로 데이터")]
        [SerializeField] private MultiPathData _multiPathData;

        // GlobalNormalizedTime 기준 정렬된 리스트 (값이 클수록 앞에 있음)
        private readonly List<QueuedPathFollower> _followers = new List<QueuedPathFollower>();
        private readonly HashSet<QueuedPathFollower> _followerSet = new HashSet<QueuedPathFollower>();
        private readonly Dictionary<QueuedPathFollower, int> _followerIndexCache = new Dictionary<QueuedPathFollower, int>();
        private bool _needsSort = false;

        #endregion


        #region Queue Manager Properties

        public float DefaultSpacing { get => _defaultSpacing; set => _defaultSpacing = value; }
        public bool EnableGradualSlowdown { get => _enableGradualSlowdown; set => _enableGradualSlowdown = value; }
        public float SlowdownStartDistance { get => _slowdownStartDistance; set => _slowdownStartDistance = value; }
        public float MinSpeedMultiplier { get => _minSpeedMultiplier; set => _minSpeedMultiplier = value; }
        public int FollowerCount => _followers.Count;

        public IReadOnlyList<QueuedPathFollower> Followers
        {
            get
            {
                EnsureSortedForQueryIfNeeded();
                return _followers;
            }
        }

        public MultiPathData MultiPathData { get => _multiPathData; set => _multiPathData = value; }

        /// <summary>
        /// 전체 경로 길이
        /// </summary>
        public float TotalPathLength => _multiPathData != null ? _multiPathData.PathLength : 0f;

        #endregion


        #region Unity Events

        private void OnValidate()
        {
            _defaultSpacing = Mathf.Max(MIN_SPACING, _defaultSpacing);
            _slowdownStartDistance = Mathf.Max(MIN_SLOWDOWN_START_DISTANCE, _slowdownStartDistance);
            _minSpeedMultiplier = Mathf.Clamp(_minSpeedMultiplier, MIN_SPEED_MULTIPLIER, MAX_SPEED_MULTIPLIER);
        }

        private void Update()
        {
            if (_needsSort)
            {
                SortFollowers();
                _needsSort = false;
            }
        }

        #endregion


        #region Queue Manager API

        /// <summary>
        /// Follower를 매니저에 등록합니다.
        /// </summary>
        public void Register(QueuedPathFollower follower)
        {
            if (follower == null)
                return;

            if (!_followerSet.Add(follower))
                return;

            int index = _followers.Count;
            _followers.Add(follower);
            _followerIndexCache[follower] = index;
            _queueRegistry.Register(follower);
            _needsSort = true;
        }

        /// <summary>
        /// Follower를 매니저에서 해제합니다.
        /// </summary>
        public void Unregister(QueuedPathFollower follower)
        {
            if (follower == null)
                return;

            if (!_followerIndexCache.TryGetValue(follower, out int index))
            {
                _followerSet.Remove(follower);
                return;
            }

            int lastIndex = _followers.Count - 1;
            if (index < lastIndex)
            {
                _followers[index] = _followers[lastIndex];
                _followerIndexCache[_followers[index]] = index;
            }
            _followers.RemoveAt(lastIndex);

            _followerSet.Remove(follower);
            _followerIndexCache.Remove(follower);
            _queueRegistry.Unregister(follower);
            _needsSort = true;
        }

        /// <summary>
        /// 지정한 Follower의 바로 앞에 있는 Follower를 반환합니다.
        /// </summary>
        public QueuedPathFollower GetFollowerAhead(QueuedPathFollower follower)
        {
            if (follower == null)
                return null;

            EnsureSortedForQueryIfNeeded();

            if (!_followerIndexCache.TryGetValue(follower, out int index))
                return null;

            // 리스트는 GlobalNormalizedTime 내림차순 정렬
            // index가 작을수록 앞에 있음
            if (index == 0)
                return null;

            return _followers[index - 1];
        }

        /// <summary>
        /// 지정한 Follower가 앞 객체에 의해 블로킹되어야 하는지 확인합니다.
        /// </summary>
        public bool ShouldBlock(QueuedPathFollower follower)
        {
            if (follower == null)
                return false;

            float distance = GetDistanceToAhead(follower);
            if (distance < 0f)
                return false;

            float spacing = GetEffectiveSpacing(follower);
            return ShouldStartBlocking(distance, spacing);
        }

        /// <summary>
        /// 지정한 Follower와 앞 객체 사이의 경로 상 거리를 반환합니다.
        /// 앞 객체가 없으면 -1을 반환합니다.
        /// </summary>
        public float GetDistanceToAhead(QueuedPathFollower follower)
        {
            QueuedPathFollower ahead = GetFollowerAhead(follower);
            if (ahead == null)
                return -1f;

            return CalculatePathDistance(ahead, follower);
        }

        /// <summary>
        /// 앞 객체와의 거리에 따른 속도 배율을 계산합니다.
        /// </summary>
        public float GetSpeedMultiplier(QueuedPathFollower follower)
        {
            if (!_enableGradualSlowdown)
                return 1f;

            float distance = GetDistanceToAhead(follower);
            if (distance < 0f)
                return 1f;

            float spacing = GetEffectiveSpacing(follower);

            if (distance >= _slowdownStartDistance)
                return 1f;

            if (distance <= spacing)
                return 0f;

            // spacing ~ slowdownStartDistance 사이에서 점진적 감속
            float range = _slowdownStartDistance - spacing;
            if (range <= 0f)
                return 1f;
            float normalizedDistance = (distance - spacing) / range;
            float curveValue = _slowdownCurve.Evaluate(normalizedDistance);

            return Mathf.Lerp(_minSpeedMultiplier, 1f, curveValue);
        }

        /// <summary>
        /// 지정한 Follower가 앞 객체를 추월하지 않도록 클램핑된 NormalizedTime을 반환합니다.
        /// </summary>
        public float GetClampedNormalizedTime(QueuedPathFollower follower, float targetNormalizedTime)
        {
            QueuedPathFollower ahead = GetFollowerAhead(follower);
            if (ahead == null)
                return Mathf.Clamp01(targetNormalizedTime);

            float spacing = GetEffectiveSpacing(follower);
            float spacingNormalized = TotalPathLength > 0f ? spacing / TotalPathLength : 0f;

            float maxNormalizedTime = ahead.GlobalNormalizedTime - spacingNormalized;
            return Mathf.Clamp01(Mathf.Min(targetNormalizedTime, maxNormalizedTime));
        }

        /// <summary>
        /// 정렬이 필요함을 알립니다. (Follower의 위치가 변경된 경우 호출)
        /// </summary>
        public void NotifySortNeeded()
        {
            _needsSort = true;
        }

        /// <summary>
        /// 모든 Follower를 해제합니다.
        /// </summary>
        public void ClearAllFollowers()
        {
            _followers.Clear();
            _followerSet.Clear();
            _followerIndexCache.Clear();
            _queueRegistry.Clear();
        }

        #endregion


        #region Queue Manager Helpers

        /// <summary>
        /// Follower 리스트를 GlobalNormalizedTime 기준 내림차순으로 정렬합니다.
        /// </summary>
        private void SortFollowers()
        {
            _followers.Sort(CompareFollowersByGlobalTimeDesc);
            RebuildIndexCache();
        }

        private static int CompareFollowersByGlobalTimeDesc(QueuedPathFollower a, QueuedPathFollower b)
            => b.GlobalNormalizedTime.CompareTo(a.GlobalNormalizedTime);

        /// <summary>
        /// 인덱스 캐시를 재구축합니다.
        /// </summary>
        private void RebuildIndexCache()
        {
            _followerIndexCache.Clear();

            for (int i = 0; i < _followers.Count; i++)
                _followerIndexCache[_followers[i]] = i;
        }

        /// <summary>
        /// 두 Follower 간의 경로 상 거리를 계산합니다.
        /// </summary>
        private float CalculatePathDistance(QueuedPathFollower ahead, QueuedPathFollower behind)
        {
            if (ahead == null || behind == null)
                return -1f;

            if (TotalPathLength <= 0f)
                return -1f;

            float normalizedDiff = ahead.GlobalNormalizedTime - behind.GlobalNormalizedTime;

            if (normalizedDiff < 0f)
                return -1f;

            return normalizedDiff * TotalPathLength;
        }

        private void EnsureSortedForQueryIfNeeded()
        {
            if (!_needsSort)
                return;

            SortFollowers();
            _needsSort = false;
        }

        private float GetEffectiveSpacing(QueuedPathFollower follower)
        {
            if (follower != null && follower.UseManagerSpacing)
                return _defaultSpacing;

            return follower != null ? follower.ActorSpacing : _defaultSpacing;
        }

        private static bool ShouldStartBlocking(float distance, float spacing)
            => distance >= 0f && distance <= spacing;

        #endregion

        #region IPathQueue Contract State

        private readonly PathQueueRegistry _queueRegistry = new PathQueueRegistry();

        #endregion


        #region IPathQueue Contract

        int IPathQueue.AgentCount => _queueRegistry.Count;

        #endregion


        #region IPathQueue Implementation

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
            return distance >= 0f && ShouldStartBlocking(distance, GetEffectiveSpacing(agent));
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


        #region IPathQueue Helpers

        private float GetEffectiveSpacing(IQueuedPathAgent agent)
            => agent.UseManagerSpacing ? _defaultSpacing : UnityEngine.Mathf.Max(0f, agent.ActorSpacing);

        #endregion
    }
}
