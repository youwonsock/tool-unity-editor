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

        private readonly List<Collider> _colliders = new List<Collider>(16);
        // Kept beside each reference, so a destroyed Collider can still clear
        // the cells it used to block.
        private readonly List<Bounds> _bounds = new List<Bounds>(16);
        private readonly HashSet<Collider> _set = new HashSet<Collider>();

        internal int Count => _colliders.Count;
        internal Collider GetCollider(int index) => _colliders[index];
        // Where each Collider was last reported: registration or a notified move.
        internal Bounds GetBounds(int index) => _bounds[index];
        // Static masks leave these Colliders to the dynamic mask.
        internal HashSet<Collider> Set => _set;

        private void RemoveAt(int index)
        {
            _set.Remove(_colliders[index]);
            _colliders.RemoveAt(index);
            _bounds.RemoveAt(index);
        }

        internal void Clear()
        {
            _colliders.Clear();
            _bounds.Clear();
            _set.Clear();
        }

        /// <summary>False when it is already registered.</summary>
        internal bool Register(Collider collider, out FlowFieldObstacleChange change)
        {
            change = default;
            if (ReferenceEquals(collider, null))
                throw new ArgumentNullException(nameof(collider));
            if (collider == null)
                throw new InvalidOperationException("Cannot register a destroyed Collider.");
            if (IndexOf(collider) >= 0)
                return false;
            Bounds current = SyncedBounds(collider);
            _colliders.Add(collider);
            _bounds.Add(current);
            _set.Add(collider);
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
            int index = IndexOf(collider);
            if (index < 0)
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
            int index = IndexOf(collider);
            if (index < 0)
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
            Bounds current = SyncedBounds(collider);
            if ((current.center - previous.center).sqrMagnitude <= MoveEpsilonSqr
                && (current.size - previous.size).sqrMagnitude <= MoveEpsilonSqr)
                return true;
            _bounds[index] = current;
            change = new FlowFieldObstacleChange(
                FlowFieldObstacleChangeKind.Moved, collider, true, previous, true, current);
            changed = true;
            return true;
        }

        // Collider.bounds lags a Transform move until the physics scene syncs,
        // and Auto Sync Transforms is off unless a project turns it on. A
        // Collider moved and notified in the same frame would otherwise report
        // where it was, and the move would be lost.
        private static Bounds SyncedBounds(Collider collider)
        {
            Physics.SyncTransforms();
            return collider.bounds;
        }

        private int IndexOf(Collider collider)
        {
            for (int index = 0; index < _colliders.Count; index++)
            {
                if (ReferenceEquals(_colliders[index], collider))
                    return index;
            }
            return -1;
        }
    }
}
