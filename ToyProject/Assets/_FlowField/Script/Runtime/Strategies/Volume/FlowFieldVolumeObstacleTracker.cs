using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    // Owns the registered colliders and their last bounds. Reports dirty bounds
    // synchronously when told about a change; safety margins, requests and
    // lifecycle decisions remain with the builder that receives them.
    internal sealed class FlowFieldVolumeObstacleTracker
    {
        private const int MaxObstacleSweepColliders = 65536;

        private readonly List<Collider> _dynamicObstacles = new List<Collider>(16);
        // Unity overloads Collider equality after destruction. Keep the last
        // bounds beside the reference so a destroyed registered collider can
        // still invalidate its former cells.
        private readonly List<Bounds> _dynamicObstacleBounds = new List<Bounds>(16);

        private readonly Action<Bounds> _markDirty;

        internal FlowFieldVolumeObstacleTracker(Action<Bounds> markDirty)
            => _markDirty = markDirty ?? throw new ArgumentNullException(nameof(markDirty));

        internal int RegisteredCount => _dynamicObstacles.Count;
        internal Collider GetRegisteredObstacle(int index) => _dynamicObstacles[index];

        internal void Clear()
        {
            _dynamicObstacles.Clear();
            _dynamicObstacleBounds.Clear();
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
            _dynamicObstacles.RemoveAt(obstacleIndex);
            _dynamicObstacleBounds.RemoveAt(obstacleIndex);
            if (FlowFieldGridSpace.IsFinite(previous.center)
                && FlowFieldGridSpace.IsFinite(previous.size))
                _markDirty(previous);
            return true;
        }

        /// <summary>
        /// A registered collider moved: its last bounds and its current bounds
        /// become dirty. A destroyed one leaves its last bounds dirty and stops
        /// being registered. False when the collider is not registered.
        /// </summary>
        internal bool NotifyMoved(Collider collider)
        {
            if (ReferenceEquals(collider, null))
                throw new ArgumentNullException(nameof(collider));
            int index = FindRegisteredIndex(collider);
            if (index < 0)
                return false;
            Bounds previous = _dynamicObstacleBounds[index];
            bool previousValid = FlowFieldGridSpace.IsFinite(previous.center)
                && FlowFieldGridSpace.IsFinite(previous.size);
            if (collider == null)
            {
                if (previousValid)
                    _markDirty(previous);
                _dynamicObstacles.RemoveAt(index);
                _dynamicObstacleBounds.RemoveAt(index);
                return true;
            }
            Bounds current = collider.bounds;
            if ((current.center - previous.center).sqrMagnitude <= 0.00000001f
                && (current.size - previous.size).sqrMagnitude <= 0.00000001f)
                return true;
            if (previousValid)
                _markDirty(previous);
            _dynamicObstacleBounds[index] = current;
            _markDirty(current);
            return true;
        }

        private int FindRegisteredIndex(Collider collider)
        {
            for (int index = 0; index < _dynamicObstacles.Count; index++)
                if (ReferenceEquals(_dynamicObstacles[index], collider))
                    return index;
            return -1;
        }
    }
}
