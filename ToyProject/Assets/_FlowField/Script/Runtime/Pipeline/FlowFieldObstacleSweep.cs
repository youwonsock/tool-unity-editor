using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Watches the Obstacle Layer for Colliders that are not registered, for a
    /// session whose settings enable the sweep. A pass lists the Colliders in
    /// the sweep bounds once and compares each one's bounds with the previous
    /// pass, a few at a time; a Collider that appeared, moved or disappeared
    /// reports where it was and where it is. Disappearing is judged at the end
    /// of a pass. The first pass only records what is there, because the build
    /// that follows it reads the whole scene anyway.
    /// </summary>
    internal sealed class FlowFieldObstacleSweep
    {
        // More Colliders than this would make every pass report the whole
        // bounds, and the field would never become certain.
        internal const int MaxColliders = 65536;
        private const int InitialCapacity = 256;
        private const int CollidersPerStep = 1024;
        private const float MoveEpsilonSqr = 0.00000001f;

        internal delegate void ChangeReport(bool hasPrevious, Bounds previous, bool hasCurrent, Bounds current);

        private readonly ChangeReport _report;
        private readonly Dictionary<Collider, Bounds> _observed = new Dictionary<Collider, Bounds>(64);
        private readonly HashSet<Collider> _seen = new HashSet<Collider>();
        private readonly List<Collider> _removed = new List<Collider>(8);
        private Collider[] _hits = new Collider[InitialCapacity];
        private int _hitCount;
        private int _cursor;
        private bool _passOpen;
        // The first pass has finished, so later passes report what changed.
        private bool _recorded;
        private bool _overflowed;
        private bool _enabled;
        private Bounds _bounds;
        private int _layer;
        private float _interval;
        private double _nextPassTime;
        private int _lastFrame = -1;

        internal FlowFieldObstacleSweep(ChangeReport report)
            => _report = report ?? throw new ArgumentNullException(nameof(report));

        /// <summary>Enabled and not stopped by too many Colliders.</summary>
        internal bool IsRunning => _enabled && !_overflowed;

        internal Bounds SweepBounds => _bounds;

        /// <summary>
        /// Takes the settings of each submitted configuration. Only a new
        /// switch, bounds or layer forgets what earlier passes saw.
        /// </summary>
        internal void Configure(bool enabled, Bounds bounds, LayerMask layer, float interval)
        {
            if (enabled != _enabled
                || layer.value != _layer
                || !FlowFieldLayoutUtility.Approximately(bounds, _bounds))
                Reset();
            _enabled = enabled;
            _bounds = bounds;
            _layer = layer.value;
            _interval = interval;
        }

        /// <summary>Forgets what earlier passes saw; the next pass only records again.</summary>
        internal void Clear()
        {
            _observed.Clear();
            _seen.Clear();
            _removed.Clear();
            Array.Clear(_hits, 0, _hitCount);
            _hitCount = 0;
            _cursor = 0;
            _passOpen = false;
            _recorded = false;
            _nextPassTime = 0d;
            _lastFrame = -1;
        }

        /// <summary>Clears and lets a stopped sweep run again.</summary>
        internal void Reset()
        {
            Clear();
            _overflowed = false;
        }

        /// <summary>
        /// One step of the sweep, at most once a frame: a pass begins when the
        /// interval since the last one has passed (0 is every frame), and an
        /// open pass goes on. Registered Colliders are left to their own
        /// notifications. False when this step stopped the sweep.
        /// </summary>
        internal bool Step(int frame, double now, FlowFieldObstacleRegistry registry)
        {
            if (!IsRunning || frame == _lastFrame)
                return true;
            if (!_passOpen)
            {
                if (now < _nextPassTime)
                    return true;
                if (!BeginPass(now))
                    return false;
            }
            _lastFrame = frame;

            // The first pass records every Collider in the frame it lists them.
            // Spread over frames, a Collider moved after the build scanned it
            // but before this pass reached it would be recorded where it moved
            // to, and that move would never be reported.
            int end = _recorded ? Math.Min(_hitCount, _cursor + CollidersPerStep) : _hitCount;
            for (; _cursor < end; _cursor++)
            {
                Collider collider = _hits[_cursor];
                if (collider == null)
                    continue;
                if (registry.Set.Contains(collider))
                {
                    _observed.Remove(collider);
                    continue;
                }
                _seen.Add(collider);
                Bounds current = collider.bounds;
                if (_observed.TryGetValue(collider, out Bounds previous))
                {
                    if ((current.center - previous.center).sqrMagnitude <= MoveEpsilonSqr
                        && (current.size - previous.size).sqrMagnitude <= MoveEpsilonSqr)
                        continue;
                    _observed[collider] = current;
                    if (_recorded)
                        _report(true, previous, true, current);
                }
                else
                {
                    _observed.Add(collider, current);
                    if (_recorded)
                        _report(false, default, true, current);
                }
            }
            if (_cursor >= _hitCount)
                EndPass();
            return true;
        }

        private bool BeginPass(double now)
        {
            _nextPassTime = now + _interval;
            Physics.SyncTransforms();
            while (true)
            {
                int count = Physics.OverlapBoxNonAlloc(
                    _bounds.center,
                    _bounds.extents,
                    _hits,
                    Quaternion.identity,
                    _layer,
                    QueryTriggerInteraction.Ignore);
                if (count < _hits.Length)
                {
                    _hitCount = count;
                    break;
                }
                if (_hits.Length >= MaxColliders)
                {
                    _overflowed = true;
                    Clear();
                    Debug.LogWarning(
                        $"[FlowField] The unregistered obstacle sweep found {MaxColliders} or more Colliders, so it stopped for this session.");
                    return false;
                }
                _hits = new Collider[Math.Min(MaxColliders, _hits.Length * 2)];
            }
            _cursor = 0;
            _seen.Clear();
            _passOpen = true;
            return true;
        }

        // A Collider no pass step saw is gone: destroyed, disabled, a trigger,
        // on another layer or out of the bounds.
        private void EndPass()
        {
            _passOpen = false;
            foreach (KeyValuePair<Collider, Bounds> pair in _observed)
            {
                if (!_seen.Contains(pair.Key))
                    _removed.Add(pair.Key);
            }
            for (int i = 0; i < _removed.Count; i++)
            {
                Bounds previous = _observed[_removed[i]];
                _observed.Remove(_removed[i]);
                if (_recorded)
                    _report(true, previous, false, default);
            }
            _removed.Clear();
            _seen.Clear();
            Array.Clear(_hits, 0, _hitCount);
            _hitCount = 0;
            _recorded = true;
        }
    }
}
