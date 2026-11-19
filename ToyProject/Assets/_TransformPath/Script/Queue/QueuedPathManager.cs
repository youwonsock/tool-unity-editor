using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// Keeps followers on one route apart. Every frame, before anyone moves, it
    /// orders its members by progress and gives each one a limit from the
    /// member ahead. It is the only place the queue's order and limits change.
    /// </summary>
    public sealed class QueuedPathManager : PathRuntimeBehaviour
    {
        private sealed class Entry
        {
            public PathQueueMember Member;
            public int Sequence;
            public int MotionEpoch;
            public float Progress;
            public PathQueueStatus? Status;
            public EPathQueueLeaveReason LeaveReason;
        }

        [Header("Route")]
        [SerializeField] private PathProviderBehaviour _routeProviderObject;

        [Header("Spacing")]
        [SerializeField, Min(0f)] private float _defaultSpacing = PathConstants.DEFAULT_QUEUE_SPACING;
        [Tooltip("Optional. Decides each member's gap from the default or the member's own spacing. Several managers may share one.")]
        [SerializeField] private PathQueueSpacingPolicy _spacingPolicy;

        [Header("Slowdown")]
        [SerializeField] private bool _enableGradualSlowdown = true;
        [SerializeField, Min(0f)] private float _slowdownStartDistance = PathConstants.DEFAULT_SLOWDOWN_START_DISTANCE;
        [SerializeField, Range(0f, 1f)] private float _minSpeedMultiplier = PathConstants.DEFAULT_MIN_SPEED_MULTIPLIER;
        [SerializeField] private AnimationCurve _slowdownCurve = null;

        // _ordered is sorted by progress, leaders first; _joined keeps join order,
        // which is the order members hear their results in.
        private readonly List<Entry> _ordered = new List<Entry>(100);
        private readonly List<Entry> _joined = new List<Entry>(100);
        private readonly List<Entry> _leaving = new List<Entry>(100);
        private readonly List<Entry> _pool = new List<Entry>(100);
        private readonly Dictionary<PathQueueMember, Entry> _entries = new Dictionary<PathQueueMember, Entry>(100);

        private PathProviderBehaviour _routeProvider;
        private bool _isInitialized;
        private int _routeRevision = -1;
        private int _nextSequence;
        private bool _calculating;
        private PathConfigurationErrorReporter _errors;

        private static readonly Comparison<Entry> CompareOrder = (left, right) =>
        {
            int progress = right.Progress.CompareTo(left.Progress);
            return progress != 0 ? progress : left.Sequence.CompareTo(right.Sequence);
        };

        /// <summary>Whether the manager validated its settings and can take members.</summary>
        public bool IsInitialized => _isInitialized;
        public PathProviderBehaviour RouteProvider => _routeProvider;
        /// <summary>The route revision the last results were computed for.</summary>
        public int RouteRevision => _routeRevision;
        public int MemberCount => _ordered.Count;

        public float DefaultSpacing
        {
            get => _defaultSpacing;
            set
            {
                PathValueUtility.ThrowIfNegative(value, nameof(value));
                _defaultSpacing = value;
            }
        }

        /// <summary>Decides each member's gap every frame, or null for the resolved spacing.</summary>
        public PathQueueSpacingPolicy SpacingPolicy
        {
            get => _spacingPolicy;
            set => _spacingPolicy = value;
        }

        public bool EnableGradualSlowdown
        {
            get => _enableGradualSlowdown;
            set => _enableGradualSlowdown = value;
        }

        public float SlowdownStartDistance
        {
            get => _slowdownStartDistance;
            set
            {
                PathValueUtility.ThrowIfNegative(value, nameof(value));
                _slowdownStartDistance = value;
            }
        }

        public float MinSpeedMultiplier
        {
            get => _minSpeedMultiplier;
            set
            {
                PathValueUtility.ThrowIfOutOfRange(value, 0f, 1f, nameof(value));
                _minSpeedMultiplier = value;
            }
        }

        /// <summary>The member at a position in the queue, leaders first.</summary>
        public PathQueueMember GetMember(int orderedIndex)
        {
            if (orderedIndex < 0 || orderedIndex >= _ordered.Count)
                throw new ArgumentOutOfRangeException(nameof(orderedIndex));
            return _ordered[orderedIndex].Member;
        }

        public bool TryGetMemberStatus(PathQueueMember member, out PathQueueStatus status)
        {
            PathQueueStatus? current = member != null && _entries.TryGetValue(member, out Entry entry)
                ? entry.Status
                : null;
            status = current.GetValueOrDefault();
            return current.HasValue;
        }

        /// <summary>Sets the route every member must play. Changing it removes the current members.</summary>
        public void ConfigureRoute(PathProviderBehaviour provider)
        {
            ThrowIfCalculating();
            if (provider != null)
                provider.EnsureBuilt();
            PathProviderUtility.ThrowIfNotReady(provider, nameof(provider));
            if (ReferenceEquals(_routeProvider, provider))
                return;
            using var operation = PathExecutionBoundary.Enter();
            LeaveAll(EPathQueueLeaveReason.ManagerReleased);
            _routeProvider = provider;
            _routeRevision = provider.Revision;
        }

        #region Runtime

        internal override void Initialize()
        {
            if (_isInitialized)
                return;
            if (!TryValidateSettings(out string error))
            {
                _errors.Report(this, $"QueuedPathManager '{name}': {error}");
                return;
            }
            if (_slowdownCurve == null)
                _slowdownCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
            _isInitialized = true;
            _errors.Reset();
            if (_routeProviderObject == null)
                return;
            _routeProviderObject.EnsureBuilt();
            if (_routeProviderObject.IsReady)
                ConfigureRoute(_routeProviderObject);
        }

        internal override void Release()
        {
            using var operation = PathExecutionBoundary.Enter();
            LeaveAll(EPathQueueLeaveReason.ManagerReleased);
            _routeProvider = null;
            _routeRevision = -1;
            _isInitialized = false;
        }

        protected override void OnEnable()
        {
            if (Application.isPlaying && !_isInitialized)
                Initialize();
            base.OnEnable();
        }

        protected override void OnDisabled() => Release();

        internal override EPathTickPhases TickPhases => EPathTickPhases.Queue;

        internal override void Tick(EPathTickPhase phase, float deltaTime, int frameId)
        {
            if (!_isInitialized || _routeProvider == null)
                return;
            using var operation = PathExecutionBoundary.Enter();
            if (!_routeProvider.IsReady)
            {
                LeaveAll(EPathQueueLeaveReason.ProviderInvalid);
                return;
            }
            _routeRevision = _routeProvider.Revision;

            RemoveEndedMembers();
            for (int i = 0; i < _ordered.Count; i++)
            {
                Entry entry = _ordered[i];
                PathFollower follower = entry.Member.Follower;
                // The member takes a new geometry before its progress is read,
                // so a path that changes every frame still lets the queue move.
                follower.RefreshRouteGeometry();
                entry.Progress = Mathf.Clamp01(follower.Progress);
                // A jump (Seek, loop seam) joins the back of the line at its new
                // progress before this frame's order, as a new start would.
                if (follower.MotionEpoch != entry.MotionEpoch)
                {
                    entry.MotionEpoch = follower.MotionEpoch;
                    entry.Sequence = _nextSequence++;
                }
            }
            PathSortUtility.StableInsertionSort(_ordered, CompareOrder);
            CalculateStatuses();
            for (int i = 0; i < _joined.Count; i++)
                _joined[i].Member.ApplyQueueStatus(_joined[i].Status.GetValueOrDefault(), _joined[i].Status.HasValue, deltaTime);
        }

        internal override void OnTickFault(Exception exception)
        {
            using var operation = PathExecutionBoundary.Enter();
            LeaveAll(EPathQueueLeaveReason.ManagerReleased);
        }

        #endregion

        #region Membership

        internal void Join(PathQueueMember member)
        {
            ThrowIfCalculating();
            if (!_isInitialized)
                throw new InvalidOperationException("QueuedPathManager is not initialized.");
            if (_routeProvider == null || !ReferenceEquals(member.Follower.CurrentProvider, _routeProvider))
                throw new InvalidOperationException("Queue member and manager must use the same route provider instance.");
            if (_entries.TryGetValue(member, out Entry joined))
            {
                // Restarting in place keeps the member's place among equals and waits
                // for this frame's result, as a new start does.
                joined.MotionEpoch = member.Follower.MotionEpoch;
                joined.Status = null;
                return;
            }
            Entry entry = _pool.Count > 0 ? _pool[_pool.Count - 1] : new Entry();
            if (_pool.Count > 0)
                _pool.RemoveAt(_pool.Count - 1);
            entry.Member = member;
            entry.Sequence = _nextSequence++;
            entry.MotionEpoch = member.Follower.MotionEpoch;
            entry.Progress = Mathf.Clamp01(member.Follower.Progress);
            entry.Status = null;
            _entries.Add(member, entry);
            _ordered.Add(entry);
            _joined.Add(entry);
        }

        /// <summary>Removes a member without telling it. The member calls this when it leaves.</summary>
        internal void Remove(PathQueueMember member)
        {
            ThrowIfCalculating();
            if (member == null || !_entries.TryGetValue(member, out Entry entry))
                return;
            _entries.Remove(member);
            _ordered.Remove(entry);
            _joined.Remove(entry);
            entry.Member = null;
            entry.Status = null;
            _pool.Add(entry);
        }

        internal bool Contains(PathQueueMember member) => member != null && _entries.ContainsKey(member);

        /// <summary>A spacing policy runs while the line is walked, so nothing may change the line then.</summary>
        internal void ThrowIfCalculating()
        {
            if (_calculating)
                throw new InvalidOperationException(
                    $"QueuedPathManager '{name}' cannot change its members while its spacing policy runs.");
        }

        private void LeaveAll(EPathQueueLeaveReason reason)
        {
            ThrowIfCalculating();
            _leaving.Clear();
            _leaving.AddRange(_joined);
            for (int i = 0; i < _leaving.Count; i++)
            {
                PathQueueMember member = _leaving[i].Member;
                Remove(member);
                member.OnRemovedByManager(reason, _routeProvider);
            }
            _leaving.Clear();
        }

        // A member whose playback ended, left the route or still plays the
        // route's old definition is no longer on it.
        private void RemoveEndedMembers()
        {
            _leaving.Clear();
            for (int i = 0; i < _joined.Count; i++)
            {
                Entry entry = _joined[i];
                if (TryGetLeaveReason(entry.Member.Follower, out entry.LeaveReason))
                    _leaving.Add(entry);
            }
            for (int i = 0; i < _leaving.Count; i++)
            {
                PathQueueMember member = _leaving[i].Member;
                EPathQueueLeaveReason reason = _leaving[i].LeaveReason;
                Remove(member);
                member.OnRemovedByManager(reason, _routeProvider);
            }
            _leaving.Clear();
        }

        private bool TryGetLeaveReason(PathFollower follower, out EPathQueueLeaveReason reason)
        {
            if (follower == null || follower.PlaybackId == 0)
                reason = follower != null && follower.State == EPathFollowerState.Completed
                    ? EPathQueueLeaveReason.Completed
                    : EPathQueueLeaveReason.Stopped;
            else if (!ReferenceEquals(follower.CurrentProvider, _routeProvider))
                reason = EPathQueueLeaveReason.ProviderInvalid;
            // A moving playback would stop at its next move, a paused one only
            // when it resumes. Either way it no longer plays this route.
            else if (follower.RouteDefinitionRevision != _routeProvider.DefinitionRevision)
                reason = EPathQueueLeaveReason.ManagerReleased;
            else
            {
                reason = default(EPathQueueLeaveReason);
                return false;
            }
            return true;
        }

        #endregion

        #region Calculation

        private void CalculateStatuses()
        {
            float routeLength = Mathf.Max(_routeProvider.PathLength, PathConstants.MIN_MOVE_VALUE);
            // Read once a frame: a Unity null check is not free.
            PathQueueSpacingPolicy policy = _spacingPolicy;
            bool hasPolicy = policy != null;
            _calculating = true;
            try
            {
                Entry ahead = null;
                for (int i = 0; i < _ordered.Count; i++)
                {
                    Entry entry = _ordered[i];
                    PathQueueMember member = entry.Member;
                    float? distance = ahead == null
                        ? (float?)null
                        : Mathf.Max(0f, (ahead.Progress - entry.Progress) * routeLength);
                    float spacing = SpacingFor(member);
                    if (hasPolicy)
                        spacing = AskPolicy(policy, member, ahead?.Member, spacing);
                    bool blocked = distance.HasValue && distance.Value <= spacing;
                    float multiplier = PathQueueCoordinator.CalculateSpeedMultiplier(
                        _enableGradualSlowdown && member.EnableGradualSlowdown,
                        distance, spacing, _slowdownStartDistance, _minSpeedMultiplier, _slowdownCurve);
                    float maxProgress = PathQueueCoordinator.CalculateMaxProgress(
                        member.EnableOvertakeProtection, ahead?.Progress, spacing, routeLength);
                    entry.Status = new PathQueueStatus(ahead?.Member, distance, spacing, blocked, multiplier, maxProgress);
                    ahead = entry;
                }
            }
            finally
            {
                _calculating = false;
            }
        }

        // The manager's spacing, or the member's own when it does not use the manager's.
        private float SpacingFor(PathQueueMember member) => member.UseManagerSpacing ? _defaultSpacing : member.ActorSpacing;

        private static float AskPolicy(PathQueueSpacingPolicy policy, PathQueueMember member, PathQueueMember ahead,
            float resolvedSpacing)
        {
            float spacing = policy.GetSpacing(member, ahead, resolvedSpacing);
            if (!PathValueUtility.IsNonNegativeFinite(spacing))
                throw new InvalidOperationException(
                    $"Spacing policy '{policy.name}' returned {spacing} for '{member.name}'. Spacing must be finite and non-negative.");
            return spacing;
        }

        private bool TryValidateSettings(out string error)
        {
            if (!PathValueUtility.IsNonNegativeFinite(_defaultSpacing))
                error = "Default spacing must be finite and non-negative.";
            else if (!PathValueUtility.IsNonNegativeFinite(_slowdownStartDistance))
                error = "Slowdown start distance must be finite and non-negative.";
            else if (!PathValueUtility.IsInRange(_minSpeedMultiplier, 0f, 1f))
                error = "Minimum speed multiplier must be within 0..1.";
            else
                error = null;
            return error == null;
        }

        #endregion
    }
}
