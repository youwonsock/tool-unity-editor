using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    // Owns registration, cached collider observations and sweep storage. Reports
    // dirty bounds synchronously; safety margins, requests and lifecycle
    // decisions remain with the session that receives them.
    internal sealed class FlowFieldVolumeObstacleTracker
    {
        private const int MaxObstacleSweepColliders = 65536;

        private readonly List<Collider> _dynamicObstacles = new List<Collider>(16);
        // Unity overloads Collider equality after destruction. Keep the last
        // bounds beside the reference so a destroyed registered collider can
        // still invalidate its former cells.
        private readonly List<Bounds> _dynamicObstacleBounds = new List<Bounds>(16);
        private readonly Dictionary<Collider, Bounds> _observedObstacleBounds = new Dictionary<Collider, Bounds>(64);
        private readonly Dictionary<Collider, ObstacleObservation> _observedObstacleStates
            = new Dictionary<Collider, ObstacleObservation>(64);
        private readonly HashSet<Collider> _seenObstacleColliders = new HashSet<Collider>();
        private Collider[] _obstacleSweepBuffer = new Collider[512];

        private readonly Action<Bounds> _markDirty;
        private int _observationVersion;

        internal FlowFieldVolumeObstacleTracker(Action<Bounds> markDirty)
            => _markDirty = markDirty ?? throw new ArgumentNullException(nameof(markDirty));

        internal int RegisteredCount => _dynamicObstacles.Count;
        internal Collider GetRegisteredObstacle(int index) => _dynamicObstacles[index];
        internal long SweepBufferBytes => 8L * _obstacleSweepBuffer.Length;

        // Lifecycle transitions also invalidate passes that retain their
        // collections (Suspend/Fault). Structural changes and nested passes
        // invalidate the same token before an outer callback can resume.
        internal void InvalidateObservations()
        {
            unchecked { _observationVersion++; }
        }

        private int BeginObservation()
        {
            InvalidateObservations();
            return _observationVersion;
        }

        private bool ReportDirty(Bounds bounds, int version)
        {
            if (version != _observationVersion)
                return false;
            _markDirty(bounds);
            return version == _observationVersion;
        }

        internal void Clear()
        {
            InvalidateObservations();
            _dynamicObstacles.Clear();
            _dynamicObstacleBounds.Clear();
            _observedObstacleBounds.Clear();
            _observedObstacleStates.Clear();
            _seenObstacleColliders.Clear();
            // Retain the grown query capacity, matching Release/Dispose policy.
        }

        internal bool Register(Collider collider)
        {
            if (ReferenceEquals(collider, null))
                throw new ArgumentNullException(nameof(collider));
            if (collider == null)
                throw new InvalidOperationException("Cannot register a destroyed Collider.");
            if (FindRegisteredIndex(collider) >= 0)
                return false;
            Bounds current = collider.bounds;
            InvalidateObservations();
            _observedObstacleBounds.Remove(collider);
            _observedObstacleStates.Remove(collider);
            _dynamicObstacles.Add(collider);
            _dynamicObstacleBounds.Add(current);
            _markDirty(current);
            return true;
        }

        internal bool Unregister(Collider collider)
        {
            if (ReferenceEquals(collider, null))
                throw new ArgumentNullException(nameof(collider));
            int obstacleIndex = FindRegisteredIndex(collider);
            if (obstacleIndex < 0)
                return false;
            Bounds previous = _dynamicObstacleBounds[obstacleIndex];
            InvalidateObservations();
            _observedObstacleBounds.Remove(collider);
            _observedObstacleStates.Remove(collider);
            _dynamicObstacles.RemoveAt(obstacleIndex);
            _dynamicObstacleBounds.RemoveAt(obstacleIndex);
            if (FlowFieldGridSpace.IsFinite(previous.center)
                && FlowFieldGridSpace.IsFinite(previous.size))
                _markDirty(previous);
            return true;
        }

        internal bool DetectRegisteredChanges()
        {
            int version = BeginObservation();
            for (int i = _dynamicObstacles.Count - 1; i >= 0; i--)
            {
                Collider collider = _dynamicObstacles[i];
                if (collider == null)
                {
                    Bounds removedBounds = _dynamicObstacleBounds[i];
                    if (FlowFieldGridSpace.IsFinite(removedBounds.center)
                        && FlowFieldGridSpace.IsFinite(removedBounds.size)
                        && !ReportDirty(removedBounds, version))
                        return false;
                    _dynamicObstacles.RemoveAt(i);
                    _dynamicObstacleBounds.RemoveAt(i);
                    _observedObstacleBounds.Remove(collider);
                    _observedObstacleStates.Remove(collider);
                    continue;
                }
                Bounds current = collider.bounds;
                Bounds previous = _dynamicObstacleBounds[i];
                if ((current.center - previous.center).sqrMagnitude > 0.00000001f
                    || (current.size - previous.size).sqrMagnitude > 0.00000001f)
                {
                    if (FlowFieldGridSpace.IsFinite(previous.center)
                        && FlowFieldGridSpace.IsFinite(previous.size)
                        && !ReportDirty(previous, version))
                        return false;
                    _dynamicObstacleBounds[i] = current;
                    if (!ReportDirty(current, version))
                        return false;
                }
            }
            return true;
        }

        private int FindRegisteredIndex(Collider collider)
        {
            for (int index = 0; index < _dynamicObstacles.Count; index++)
                if (ReferenceEquals(_dynamicObstacles[index], collider))
                    return index;
            return -1;
        }

        internal bool DetectUnregisteredChanges(in FlowFieldVolumeRequest request)
        {
            Physics.SyncTransforms();
            Bounds sweepBounds = request.WorldBounds;
            float sweepMargin = request.Grid.CellSize + request.ObstacleClearance;
            sweepBounds.Expand(Vector3.one * (sweepMargin * 2f));
            int count = SweepObstacles(sweepBounds, request.ObstacleLayer, out bool complete);
            return ApplySweep(sweepBounds, _obstacleSweepBuffer, count, complete);
        }

        internal bool ApplySweep(Bounds sweepBounds, Collider[] colliders, int count, bool complete)
        {
            int version = BeginObservation();
            if (!complete)
            {
                // A saturated query cannot prove removal. Retain previous
                // observations while conservatively invalidating the swept area.
                return ReportDirty(sweepBounds, version);
            }

            _seenObstacleColliders.Clear();
            for (int index = 0; index < count; index++)
            {
                Collider collider = colliders[index];
                if (collider == null || FindRegisteredIndex(collider) >= 0)
                    continue;
                _seenObstacleColliders.Add(collider);
                Bounds current = collider.bounds;
                ObstacleObservation currentState = ObstacleObservation.From(collider);
                if (!_observedObstacleBounds.TryGetValue(collider, out Bounds previous))
                {
                    _observedObstacleBounds[collider] = current;
                    _observedObstacleStates[collider] = currentState;
                    if (!ReportDirty(current, version))
                        return false;
                    continue;
                }
                if (!_observedObstacleStates.TryGetValue(collider, out ObstacleObservation previousState)
                    || !previousState.Equals(currentState))
                {
                    Bounds previousBounds = previousState.Bounds.size.sqrMagnitude > 0f
                        ? previousState.Bounds
                        : previous;
                    if (!ReportDirty(previousBounds, version) || !ReportDirty(current, version))
                        return false;
                    _observedObstacleBounds[collider] = current;
                    _observedObstacleStates[collider] = currentState;
                }
            }

            if (_observedObstacleBounds.Count == 0)
                return true;
            List<Collider> removed = null;
            foreach (KeyValuePair<Collider, Bounds> pair in _observedObstacleBounds)
            {
                if (_seenObstacleColliders.Contains(pair.Key))
                    continue;
                if (removed == null)
                    removed = new List<Collider>();
                removed.Add(pair.Key);
                Bounds previous = _observedObstacleStates.TryGetValue(pair.Key, out ObstacleObservation removedState)
                    ? removedState.Bounds
                    : pair.Value;
                if (!ReportDirty(previous, version))
                    return false;
            }
            if (removed == null)
                return true;
            for (int index = 0; index < removed.Count; index++)
            {
                _observedObstacleBounds.Remove(removed[index]);
                _observedObstacleStates.Remove(removed[index]);
            }
            return true;
        }

        private int SweepObstacles(Bounds bounds, LayerMask obstacleLayer, out bool complete)
        {
            while (true)
            {
                int count = Physics.OverlapBoxNonAlloc(
                    bounds.center,
                    bounds.extents,
                    _obstacleSweepBuffer,
                    Quaternion.identity,
                    obstacleLayer,
                    QueryTriggerInteraction.Ignore);
                if (count < _obstacleSweepBuffer.Length)
                {
                    complete = true;
                    return count;
                }
                if (_obstacleSweepBuffer.Length >= MaxObstacleSweepColliders)
                {
                    complete = false;
                    return count;
                }
                int nextCapacity = Math.Min(
                    MaxObstacleSweepColliders,
                    _obstacleSweepBuffer.Length * 2);
                _obstacleSweepBuffer = new Collider[nextCapacity];
            }
        }

        private readonly struct ObstacleObservation : IEquatable<ObstacleObservation>
        {
            internal readonly Bounds Bounds;
            private readonly bool _enabled;
            private readonly bool _active;
            private readonly bool _trigger;
            private readonly int _layer;

            private ObstacleObservation(
                Bounds bounds,
                bool enabled,
                bool active,
                bool trigger,
                int layer)
            {
                Bounds = bounds;
                _enabled = enabled;
                _active = active;
                _trigger = trigger;
                _layer = layer;
            }

            internal static ObstacleObservation From(Collider collider)
                => new ObstacleObservation(
                    collider != null ? collider.bounds : default,
                    collider != null && collider.enabled,
                    collider != null && collider.gameObject.activeInHierarchy,
                    collider != null && collider.isTrigger,
                    collider != null ? collider.gameObject.layer : -1);

            public bool Equals(ObstacleObservation other)
                => _enabled == other._enabled
                    && _active == other._active
                    && _trigger == other._trigger
                    && _layer == other._layer
                    && (Bounds.center - other.Bounds.center).sqrMagnitude <= 0.00000001f
                    && (Bounds.size - other.Bounds.size).sqrMagnitude <= 0.00000001f;

            public override bool Equals(object obj)
                => obj is ObstacleObservation other && Equals(other);

            public override int GetHashCode() => 0;
        }
    }
}
