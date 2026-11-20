using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    internal enum FlowFieldObstacleChangeKind
    {
        Registered,
        Unregistered,
        // A registered Collider moved, or was found destroyed.
        Moved,
        // NotifyObstacleRegionDirty: something in the region changed.
        RegionDirty,
    }

    /// <summary>
    /// One obstacle change and the world regions it touched: where the
    /// obstacle was and where it is now. Either may be absent.
    /// </summary>
    internal readonly struct FlowFieldObstacleChange
    {
        internal readonly FlowFieldObstacleChangeKind Kind;
        internal readonly Collider Collider;
        internal readonly bool HasPrevious;
        internal readonly Bounds Previous;
        internal readonly bool HasCurrent;
        internal readonly Bounds Current;

        internal FlowFieldObstacleChange(
            FlowFieldObstacleChangeKind kind,
            Collider collider,
            bool hasPrevious,
            Bounds previous,
            bool hasCurrent,
            Bounds current)
        {
            Kind = kind;
            Collider = collider;
            HasPrevious = hasPrevious && IsFinite(previous);
            Previous = previous;
            HasCurrent = hasCurrent && IsFinite(current);
            Current = current;
        }

        private static bool IsFinite(Bounds bounds)
            => FlowFieldGridSpace.IsFinite(bounds.center) && FlowFieldGridSpace.IsFinite(bounds.size);
    }

    /// <summary>
    /// The dynamic obstacles of a session, the same in both space modes: the
    /// registered Colliders and the bounds each was last known at. A change
    /// reports both its old and new bounds, so the cells it left and the
    /// cells it reached are rebuilt.
    /// </summary>
    internal sealed class FlowFieldObstacleRegistry
    {
        private const float MoveEpsilonSqr = 0.00000001f;

        // Nothing reads the registry in order, so a removal moves the last
        // entry into the freed slot and the index map finds every Collider
        // without a scan.
        private readonly List<Collider> _colliders = new List<Collider>(16);
        // Kept beside each reference, so a destroyed Collider can still clear
        // the cells it used to block.
        private readonly List<Bounds> _bounds = new List<Bounds>(16);
        private readonly Dictionary<Collider, int> _indices = new Dictionary<Collider, int>();

        internal int Count => _colliders.Count;
        internal Collider GetCollider(int index) => _colliders[index];
        // Where each Collider was last reported: registration or a notified move.
        internal Bounds GetBounds(int index) => _bounds[index];

        /// <summary>Whether the Collider is registered. Static masks leave these Colliders to the dynamic mask.</summary>
        internal bool Contains(Collider collider)
            => !ReferenceEquals(collider, null) && _indices.ContainsKey(collider);

        private void RemoveAt(int index)
        {
            _indices.Remove(_colliders[index]);
            int last = _colliders.Count - 1;
            if (index != last)
            {
                Collider moved = _colliders[last];
                _colliders[index] = moved;
                _bounds[index] = _bounds[last];
                _indices[moved] = index;
            }
            _colliders.RemoveAt(last);
            _bounds.RemoveAt(last);
        }

        internal void Clear()
        {
            _colliders.Clear();
            _bounds.Clear();
            _indices.Clear();
        }

        /// <summary>False when it is already registered.</summary>
        internal bool Register(Collider collider, out FlowFieldObstacleChange change)
        {
            change = default;
            if (ReferenceEquals(collider, null))
                throw new ArgumentNullException(nameof(collider));
            if (collider == null)
                throw new InvalidOperationException("Cannot register a destroyed Collider.");
            if (_indices.ContainsKey(collider))
                return false;
            // Synced, so a Collider moved and registered in one frame reports
            // where it is now.
            Bounds current = FlowFieldPhysicsSync.SyncedBounds(collider);
            _indices.Add(collider, _colliders.Count);
            _colliders.Add(collider);
            _bounds.Add(current);
            change = new FlowFieldObstacleChange(
                FlowFieldObstacleChangeKind.Registered, collider, false, default, true, current);
            return true;
        }

        /// <summary>False when it is not registered. A destroyed Collider can still be removed.</summary>
        internal bool Unregister(Collider collider, out FlowFieldObstacleChange change)
        {
            change = default;
            if (ReferenceEquals(collider, null))
                throw new ArgumentNullException(nameof(collider));
            if (!_indices.TryGetValue(collider, out int index))
                return false;
            Bounds previous = _bounds[index];
            RemoveAt(index);
            change = new FlowFieldObstacleChange(
                FlowFieldObstacleChangeKind.Unregistered, collider, true, previous, false, default);
            return true;
        }

        /// <summary>
        /// A registered Collider says it moved. <paramref name="changed"/> is
        /// false when its bounds stayed where they were. A destroyed Collider
        /// stops being registered and its last bounds become the change.
        /// False when it is not registered.
        /// </summary>
        internal bool NotifyMoved(Collider collider, out FlowFieldObstacleChange change, out bool changed)
        {
            change = default;
            changed = false;
            if (ReferenceEquals(collider, null))
                throw new ArgumentNullException(nameof(collider));
            if (!_indices.TryGetValue(collider, out int index))
                return false;
            Bounds previous = _bounds[index];
            if (collider == null)
            {
                RemoveAt(index);
                change = new FlowFieldObstacleChange(
                    FlowFieldObstacleChangeKind.Moved, collider, true, previous, false, default);
                changed = true;
                return true;
            }
            Bounds current = FlowFieldPhysicsSync.SyncedBounds(collider);
            if ((current.center - previous.center).sqrMagnitude <= MoveEpsilonSqr
                && (current.size - previous.size).sqrMagnitude <= MoveEpsilonSqr)
                return true;
            _bounds[index] = current;
            change = new FlowFieldObstacleChange(
                FlowFieldObstacleChangeKind.Moved, collider, true, previous, true, current);
            changed = true;
            return true;
        }
    }
}
