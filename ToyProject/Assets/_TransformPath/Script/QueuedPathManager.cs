using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// 같은 경로 위의 QueuedPathFollower들을 관리하고 충돌 감지를 수행하는 매니저
    /// </summary>
    // MultiPathData (-200) must complete its Init before the queue validates it.
    [DefaultExecutionOrder(-180)]
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
        private bool _isInitialized;
        private bool _isFaulted;
        private System.Exception _fault;

        #endregion


        #region Queue Manager Properties

        public float DefaultSpacing
        {
            get
            {
                ThrowIfUsable();
                return _defaultSpacing;
            }
            set
            {
                ThrowIfFaulted();
                if (!IsFinite(value) || value < MIN_SPACING)
                    throw new ArgumentOutOfRangeException(nameof(value));
                _defaultSpacing = value;
            }
        }
        public bool EnableGradualSlowdown
        {
            get
            {
                ThrowIfFaulted();
                return _enableGradualSlowdown;
            }
            set
            {
                ThrowIfFaulted();
                _enableGradualSlowdown = value;
            }
        }
        public float SlowdownStartDistance
        {
            get
            {
                ThrowIfUsable();
                return _slowdownStartDistance;
            }
            set
            {
                ThrowIfFaulted();
                if (!IsFinite(value) || value < MIN_SLOWDOWN_START_DISTANCE)
                    throw new ArgumentOutOfRangeException(nameof(value));
                _slowdownStartDistance = value;
            }
        }
        public float MinSpeedMultiplier
        {
            get
            {
                ThrowIfUsable();
                return _minSpeedMultiplier;
            }
            set
            {
                ThrowIfFaulted();
                if (!IsFinite(value) || value < MIN_SPEED_MULTIPLIER || value > MAX_SPEED_MULTIPLIER)
                    throw new ArgumentOutOfRangeException(nameof(value));
                _minSpeedMultiplier = value;
            }
        }
        public int FollowerCount
        {
            get
            {
                ThrowIfUsable();
                return _followers.Count;
            }
        }

        public IReadOnlyList<QueuedPathFollower> Followers
        {
            get
            {
                ThrowIfUsable();
                return _followers;
            }
        }

        public MultiPathData MultiPathData
        {
            get
            {
                ThrowIfFaulted();
                return _multiPathData;
            }
            set
            {
                ThrowIfFaulted();
                _multiPathData = value;
            }
        }

        /// <summary>
        /// 전체 경로 길이
        /// </summary>
        public float TotalPathLength
        {
            get
            {
                ThrowIfUsable();
                if (_multiPathData == null)
                    throw new InvalidOperationException("QueuedPathManager requires a MultiPathData reference.");
                return _multiPathData.PathLength;
            }
        }

        #endregion


        #region Unity Events

        private void OnValidate()
        {
            // Invalid values are reported by Init; serialized values are never rewritten here.
        }

        private void Awake()
        {
            if (Application.isPlaying)
                Init();
        }

        private void OnDestroy()
        {
            if (_isInitialized || _isFaulted)
                Release();
        }

        private void Update()
        {
            ThrowIfUsable();
            if (!_isInitialized)
                throw new InvalidOperationException("QueuedPathManager is not initialized.");
            _queueRegistry.Sort();
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
            ThrowIfUsable();
            if (follower == null)
                throw new ArgumentNullException(nameof(follower));

            if (!_followerSet.Add(follower))
                throw new InvalidOperationException("Follower is already registered.");

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
            ThrowIfUsable();
            if (follower == null)
                throw new ArgumentNullException(nameof(follower));

            if (!_followerIndexCache.TryGetValue(follower, out int index))
                throw new InvalidOperationException("Follower is not registered.");

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
            ThrowIfUsable();
            if (follower == null)
                throw new ArgumentNullException(nameof(follower));

            if (!_followerIndexCache.TryGetValue(follower, out int index))
                throw new InvalidOperationException("Follower is not registered.");

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
            ThrowIfUsable();
            if (follower == null)
                throw new ArgumentNullException(nameof(follower));

            float? distance = GetDistanceToAhead(follower);
            if (!distance.HasValue)
                return false;

            float spacing = GetEffectiveSpacing(follower);
            return ShouldStartBlocking(distance.Value, spacing);
        }

        /// <summary>
        /// 지정한 Follower와 앞 객체 사이의 경로 상 거리를 반환합니다.
        /// 앞 객체가 없으면 null을 반환합니다.
        /// </summary>
        public float? GetDistanceToAhead(QueuedPathFollower follower)
        {
            ThrowIfUsable();
            if (follower == null)
                throw new ArgumentNullException(nameof(follower));
            QueuedPathFollower ahead = GetFollowerAhead(follower);
            if (ahead == null)
                return null;

            return CalculatePathDistance(ahead, follower);
        }

        /// <summary>
        /// 앞 객체와의 거리에 따른 속도 배율을 계산합니다.
        /// </summary>
        public float GetSpeedMultiplier(QueuedPathFollower follower)
        {
            ThrowIfUsable();
            if (follower == null)
                throw new ArgumentNullException(nameof(follower));
            if (!_enableGradualSlowdown)
                return 1f;

            float? distance = GetDistanceToAhead(follower);
            if (!distance.HasValue)
                return 1f;

            float spacing = GetEffectiveSpacing(follower);

            if (distance.Value >= _slowdownStartDistance)
                return 1f;

            if (distance.Value <= spacing)
                return 0f;

            // spacing ~ slowdownStartDistance 사이에서 점진적 감속
            float range = _slowdownStartDistance - spacing;
            if (range <= 0f)
                return 1f;
            if (_slowdownCurve == null || _slowdownCurve.length == 0)
                throw new InvalidOperationException("QueuedPathManager slowdown curve is not configured.");
            float normalizedDistance = (distance.Value - spacing) / range;
            float curveValue = _slowdownCurve.Evaluate(normalizedDistance);

            return Mathf.Lerp(_minSpeedMultiplier, 1f, curveValue);
        }

        /// <summary>
        /// 지정한 Follower가 앞 객체를 추월하지 않도록 클램핑된 NormalizedTime을 반환합니다.
        /// </summary>
        public float GetClampedNormalizedTime(QueuedPathFollower follower, float targetNormalizedTime)
        {
            ThrowIfUsable();
            if (follower == null)
                throw new ArgumentNullException(nameof(follower));
            if (!IsFinite(targetNormalizedTime) || targetNormalizedTime < 0f || targetNormalizedTime > 1f)
                throw new ArgumentOutOfRangeException(nameof(targetNormalizedTime));
            QueuedPathFollower ahead = GetFollowerAhead(follower);
            if (ahead == null)
                return targetNormalizedTime;

            float spacing = GetEffectiveSpacing(follower);
            float spacingNormalized = spacing / TotalPathLength;

            float maxNormalizedTime = ahead.GlobalNormalizedTime - spacingNormalized;
            return Mathf.Clamp01(Mathf.Min(targetNormalizedTime, maxNormalizedTime));
        }

        /// <summary>
        /// 정렬이 필요함을 알립니다. (Follower의 위치가 변경된 경우 호출)
        /// </summary>
        public void NotifySortNeeded()
        {
            ThrowIfUsable();
            _needsSort = true;
        }

        /// <summary>
        /// 모든 Follower를 해제합니다.
        /// </summary>
        public void ClearAllFollowers()
        {
            ThrowIfUsable();
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
        private float? CalculatePathDistance(QueuedPathFollower ahead, QueuedPathFollower behind)
        {
            if (ahead == null || behind == null)
                throw new ArgumentNullException(ahead == null ? nameof(ahead) : nameof(behind));

            if (TotalPathLength <= 0f)
                throw new InvalidOperationException("QueuedPathManager requires a measurable path.");

            float normalizedDiff = ahead.GlobalNormalizedTime - behind.GlobalNormalizedTime;

            if (normalizedDiff < 0f)
                throw new InvalidOperationException("Queue ordering is stale: the ahead follower is behind the requested follower.");

            return normalizedDiff * TotalPathLength;
        }

        private float GetEffectiveSpacing(QueuedPathFollower follower)
        {
            if (follower == null)
                throw new ArgumentNullException(nameof(follower));
            if (follower.UseManagerSpacing)
                return _defaultSpacing;

            return follower.ActorSpacing;
        }

        private static bool ShouldStartBlocking(float distance, float spacing)
        {
            if (!IsFinite(distance) || !IsFinite(spacing) || distance < 0f || spacing < 0f)
                throw new ArgumentOutOfRangeException(nameof(distance));
            return distance <= spacing;
        }

        #endregion

        #region IPathQueue Contract State

        private readonly PathQueueRegistry _queueRegistry = new PathQueueRegistry();

        #endregion

        public bool IsInitialized => _isInitialized;
        public bool IsFaulted => _isFaulted;

        public void Init()
        {
            if (_isInitialized)
                throw new InvalidOperationException("QueuedPathManager is already initialized.");
            if (_isFaulted)
                throw new InvalidOperationException("QueuedPathManager is faulted; call Release before Init.", _fault);

            try
            {
                if (!IsFinite(_defaultSpacing) || _defaultSpacing < MIN_SPACING)
                    throw new ArgumentOutOfRangeException(nameof(_defaultSpacing));
                if (!IsFinite(_slowdownStartDistance) || _slowdownStartDistance < MIN_SLOWDOWN_START_DISTANCE)
                    throw new ArgumentOutOfRangeException(nameof(_slowdownStartDistance));
                if (!IsFinite(_minSpeedMultiplier) || _minSpeedMultiplier < MIN_SPEED_MULTIPLIER || _minSpeedMultiplier > MAX_SPEED_MULTIPLIER)
                    throw new ArgumentOutOfRangeException(nameof(_minSpeedMultiplier));
                if (_slowdownCurve == null || _slowdownCurve.length == 0)
                    throw new ArgumentException("Slowdown curve is required.", nameof(_slowdownCurve));
                if (_multiPathData == null)
                    throw new InvalidOperationException("QueuedPathManager requires a MultiPathData reference.");
                if (!_multiPathData.IsInitialized || !_multiPathData.IsReady)
                    throw new InvalidOperationException("QueuedPathManager requires an initialized and ready MultiPathData.");
                if (_multiPathData.PathLength <= 0f)
                    throw new InvalidOperationException("QueuedPathManager requires a measurable MultiPathData.");
                _isInitialized = true;
            }
            catch (System.Exception exception)
            {
                _isInitialized = false;
                _isFaulted = true;
                if (_fault == null)
                    _fault = exception;
                throw;
            }
        }

        public void Release()
        {
            if (!_isInitialized && !_isFaulted)
                throw new InvalidOperationException("QueuedPathManager has not been initialized.");
            _followers.Clear();
            _followerSet.Clear();
            _followerIndexCache.Clear();
            _queueRegistry.Clear();
            _needsSort = false;
            _isInitialized = false;
            _isFaulted = false;
            _fault = null;
        }


        #region IPathQueue Contract

        int IPathQueue.AgentCount
        {
            get
            {
                ThrowIfUsable();
                return _queueRegistry.Count;
            }
        }

        #endregion


        #region IPathQueue Implementation

        void IPathQueue.Register(IQueuedPathAgent agent)
        {
            ThrowIfUsable();
            if (agent == null)
                throw new ArgumentNullException(nameof(agent));
            if (agent is QueuedPathFollower concreteFollower)
            {
                Register(concreteFollower);
                return;
            }

            _queueRegistry.Register(agent);
        }

        void IPathQueue.Unregister(IQueuedPathAgent agent)
        {
            ThrowIfUsable();
            if (agent == null)
                throw new ArgumentNullException(nameof(agent));
            if (agent is QueuedPathFollower concreteFollower)
            {
                Unregister(concreteFollower);
                return;
            }

            _queueRegistry.Unregister(agent);
        }

        bool IPathQueue.ShouldBlock(IQueuedPathAgent agent)
        {
            ThrowIfUsable();
            if (agent == null)
                throw new ArgumentNullException(nameof(agent));
            float? distance = ((IPathQueue)this).GetDistanceToAhead(agent);
            return distance.HasValue && ShouldStartBlocking(distance.Value, GetEffectiveSpacing(agent));
        }

        float? IPathQueue.GetDistanceToAhead(IQueuedPathAgent agent)
        {
            ThrowIfUsable();
            if (agent == null)
                throw new ArgumentNullException(nameof(agent));
            IQueuedPathAgent ahead = _queueRegistry.GetAhead(agent);
            if (ahead == null)
                return null;

            float normalizedDifference = ahead.GlobalNormalizedTime - agent.GlobalNormalizedTime;
            if (normalizedDifference < 0f)
                throw new InvalidOperationException("Queue ordering is stale: the ahead agent is behind the requested agent.");
            return normalizedDifference * TotalPathLength;
        }

        float IPathQueue.GetSpeedMultiplier(IQueuedPathAgent agent)
        {
            ThrowIfUsable();
            if (agent == null)
                throw new ArgumentNullException(nameof(agent));
            if (!_enableGradualSlowdown || !agent.EnableGradualSlowdown)
                return 1f;

            float? distance = ((IPathQueue)this).GetDistanceToAhead(agent);
            if (!distance.HasValue)
                return 1f;

            float spacing = GetEffectiveSpacing(agent);
            if (distance.Value >= _slowdownStartDistance)
                return 1f;
            if (distance.Value <= spacing)
                return 0f;

            float range = _slowdownStartDistance - spacing;
            if (range <= 0f)
                return 1f;
            if (_slowdownCurve == null || _slowdownCurve.length == 0)
                throw new InvalidOperationException("QueuedPathManager slowdown curve is not configured.");

            float normalizedDistance = (distance.Value - spacing) / range;
            float curveValue = _slowdownCurve.Evaluate(normalizedDistance);
            return UnityEngine.Mathf.Lerp(_minSpeedMultiplier, 1f, curveValue);
        }

        float IPathQueue.GetClampedNormalizedTime(IQueuedPathAgent agent, float targetNormalizedTime)
        {
            ThrowIfUsable();
            if (agent == null)
                throw new ArgumentNullException(nameof(agent));
            if (!IsFinite(targetNormalizedTime) || targetNormalizedTime < 0f || targetNormalizedTime > 1f)
                throw new ArgumentOutOfRangeException(nameof(targetNormalizedTime));
            IQueuedPathAgent ahead = _queueRegistry.GetAhead(agent);
            if (ahead == null)
                return targetNormalizedTime;

            float spacingNormalized = GetEffectiveSpacing(agent) / TotalPathLength;
            float maxNormalizedTime = ahead.GlobalNormalizedTime - spacingNormalized;
            return UnityEngine.Mathf.Clamp01(UnityEngine.Mathf.Min(targetNormalizedTime, maxNormalizedTime));
        }

        void IPathQueue.NotifySortNeeded()
        {
            ThrowIfUsable();
            _queueRegistry.NotifySortNeeded();
            NotifySortNeeded();
        }

        #endregion


        #region IPathQueue Helpers

        private float GetEffectiveSpacing(IQueuedPathAgent agent)
            => agent.UseManagerSpacing ? _defaultSpacing : agent.ActorSpacing;

        private static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);

        private void ThrowIfUsable()
        {
            if (_isFaulted)
                throw new InvalidOperationException("QueuedPathManager is faulted; call Release before use.", _fault);
            if (!_isInitialized)
                throw new InvalidOperationException("QueuedPathManager is not initialized.");
        }

        private void ThrowIfFaulted()
        {
            if (_isFaulted)
                throw new InvalidOperationException("QueuedPathManager is faulted; call Release before use.", _fault);
        }

        #endregion
    }
}
