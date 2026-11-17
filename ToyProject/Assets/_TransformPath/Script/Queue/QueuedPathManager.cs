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
        }

        [Header("Route")]
        [SerializeField] private PathProviderBehaviour _routeProviderObject;

        [Header("Spacing")]
        [SerializeField, Min(0f)] private float _defaultSpacing = PathConstants.DEFAULT_QUEUE_SPACING;

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
        private readonly List<IPathProvider> _routeStructure = new List<IPathProvider>();

        private PathProviderBehaviour _routeProvider;
        private bool _isInitialized;
        private int _routeRevision = -1;
        private int _nextSequence;
        private PathConfigurationErrorReporter _errors;

        private static readonly Comparison<Entry> CompareOrder = (left, right) =>
        {
            int progress = right.Progress.CompareTo(left.Progress);
            return progress != 0 ? progress : left.Sequence.CompareTo(right.Sequence);
        };

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
            if (provider != null)
                provider.EnsureBuilt();
            PathProviderUtility.ThrowIfNotReady(provider, nameof(provider));
            if (ReferenceEquals(_routeProvider, provider))
                return;
            using var operation = PathExecutionBoundary.Enter();
            LeaveAll(EPathQueueLeaveReason.ManagerReleased);
            _routeProvider = provider;
            _routeRevision = provider.Revision;
            CaptureRouteStructure();
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
            _routeStructure.Clear();
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

        internal override void Tick(EPathTickPhase phase, float deltaTime, int frameId)
        {
            if (phase != EPathTickPhase.Queue || !_isInitialized || _routeProvider == null)
                return;
            using var operation = PathExecutionBoundary.Enter();
            if (!_routeProvider.IsReady)
            {
                LeaveAll(EPathQueueLeaveReason.ProviderInvalid);
                return;
            }
            if (_routeProvider.Revision != _routeRevision)
                RefreshRouteRevision();

            RemoveEndedMembers();
            for (int i = 0; i < _ordered.Count; i++)
            {
                Entry entry = _ordered[i];
                PathFollower follower = entry.Member.Follower;
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
            if (!_isInitialized)
                throw new InvalidOperationException("QueuedPathManager is not initialized.");
            if (_routeProvider == null || !ReferenceEquals(member.Follower.CurrentProvider, _routeProvider))
                throw new InvalidOperationException("Queue member and manager must use the same route provider instance.");
            if (_entries.TryGetValue(member, out Entry joined))
            {
                // Restarting in place keeps the member's place among equals.
                joined.MotionEpoch = member.Follower.MotionEpoch;
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

        private void LeaveAll(EPathQueueLeaveReason reason)
        {
            _leaving.Clear();
            _leaving.AddRange(_joined);
            for (int i = 0; i < _leaving.Count; i++)
            {
                PathQueueMember member = _leaving[i].Member;
                Remove(member);
                member.OnRemovedByManager(reason);
            }
            _leaving.Clear();
        }

        // A member whose playback ended or left the route is no longer on it.
        private void RemoveEndedMembers()
        {
            _leaving.Clear();
            for (int i = 0; i < _joined.Count; i++)
            {
                PathFollower follower = _joined[i].Member.Follower;
                if (follower == null
                    || follower.PlaybackId == 0
                    || !ReferenceEquals(follower.CurrentProvider, _routeProvider))
                    _leaving.Add(_joined[i]);
            }
            for (int i = 0; i < _leaving.Count; i++)
            {
                PathQueueMember member = _leaving[i].Member;
                PathFollower follower = member.Follower;
                EPathQueueLeaveReason reason = follower == null || follower.PlaybackId == 0
                    ? follower != null && follower.State == EPathFollowerState.Completed
                        ? EPathQueueLeaveReason.Completed
                        : EPathQueueLeaveReason.Stopped
                    : EPathQueueLeaveReason.ProviderInvalid;
                Remove(member);
                member.OnRemovedByManager(reason);
            }
            _leaving.Clear();
        }

        #endregion

        #region Calculation

        private void CalculateStatuses()
        {
            float routeLength = Mathf.Max(_routeProvider.PathLength, PathConstants.MIN_MOVE_VALUE);
            Entry ahead = null;
            for (int i = 0; i < _ordered.Count; i++)
            {
                Entry entry = _ordered[i];
                PathQueueMember member = entry.Member;
                float? distance = ahead == null
                    ? (float?)null
                    : Mathf.Max(0f, (ahead.Progress - entry.Progress) * routeLength);
                float spacing = member.UseManagerSpacing ? _defaultSpacing : member.ActorSpacing;
                // A follower that has not taken the new geometry yet waits a frame.
                bool revisionBlocked = member.Follower.RouteRevision != _routeRevision;
                bool spacingBlocked = distance.HasValue && distance.Value <= spacing;
                float multiplier = PathQueueCoordinator.CalculateSpeedMultiplier(
                    _enableGradualSlowdown && member.EnableGradualSlowdown,
                    distance, spacing, _slowdownStartDistance, _minSpeedMultiplier, _slowdownCurve);
                float maxProgress = PathQueueCoordinator.CalculateMaxProgress(
                    member.EnableOvertakeProtection, ahead?.Progress, spacing, routeLength);
                entry.Status = new PathQueueStatus(ahead?.Member, distance, revisionBlocked || spacingBlocked,
                    multiplier, maxProgress);
                ahead = entry;
            }
        }

        private void RefreshRouteRevision()
        {
            _routeRevision = _routeProvider.Revision;
            if (IsSameRouteStructure())
                return;
            LeaveAll(EPathQueueLeaveReason.ManagerReleased);
            CaptureRouteStructure();
        }

        // The geometry may change under the members. Different segments may not.
        private bool IsSameRouteStructure()
        {
            if (!(_routeProvider is MultiPathData sequence))
                return true;
            if (sequence.SegmentCount != _routeStructure.Count)
                return false;
            for (int i = 0; i < _routeStructure.Count; i++)
            {
                if (!ReferenceEquals(sequence.GetSegmentPath(i), _routeStructure[i]))
                    return false;
            }
            return true;
        }

        private void CaptureRouteStructure()
        {
            _routeStructure.Clear();
            if (_routeProvider is MultiPathData sequence)
            {
                for (int i = 0; i < sequence.SegmentCount; i++)
                    _routeStructure.Add(sequence.GetSegmentPath(i));
            }
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
