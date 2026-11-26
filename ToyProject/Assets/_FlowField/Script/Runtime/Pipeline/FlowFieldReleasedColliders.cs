using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Colliders unregistered while still in the scene, where they were and
    /// in which frame. Such a Collider can count as a static obstacle, and the
    /// build that starts next scans it so. Destroy removes it only at the end
    /// of a frame, after that scan, and nothing would report its spot again.
    /// So for the next two frames its spot is reported once more as soon as
    /// it has left the scene.
    /// </summary>
    internal sealed class FlowFieldReleasedColliders
    {
        private readonly List<Released> _released = new List<Released>(4);

        internal int Count => _released.Count;

        // A Collider released again replaces its earlier entry.
        internal void Remember(Collider collider, Bounds bounds, int frame)
        {
            var released = new Released(collider, bounds, frame);
            for (int index = 0; index < _released.Count; index++)
            {
                if (ReferenceEquals(_released[index].Collider, collider))
                {
                    _released[index] = released;
                    return;
                }
            }
            _released.Add(released);
        }

        // Reports the spot of each Collider that has left the scene, and
        // forgets those two frames old.
        internal void Recheck(int frame, Action<Bounds> reportGone)
        {
            for (int index = _released.Count - 1; index >= 0; index--)
            {
                Released released = _released[index];
                int age = frame - released.Frame;
                if (age <= 0)
                    continue;
                Collider collider = released.Collider;
                bool inScene = collider != null && collider.enabled && collider.gameObject.activeInHierarchy;
                if (inScene && age < 2)
                    continue;
                _released.RemoveAt(index);
                if (!inScene)
                    reportGone(released.Bounds);
            }
        }

        internal void Clear() => _released.Clear();

        private readonly struct Released
        {
            internal readonly Collider Collider;
            internal readonly Bounds Bounds;
            internal readonly int Frame;

            internal Released(Collider collider, Bounds bounds, int frame)
            {
                Collider = collider;
                Bounds = bounds;
                Frame = frame;
            }
        }
    }
}
