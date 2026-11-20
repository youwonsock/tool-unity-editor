using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Per-step candidate filter for the Volume3D cell queries. One OverlapBox
    /// per build step collects the layer colliders near the step's cells, and
    /// modifier bounds are read once per step. The registered obstacles are
    /// read once per build (<see cref="CaptureRegistered"/>), and a step only
    /// keeps the ones whose bounds reach it. A cell then runs its exact
    /// CheckBox / OverlapsTarget only when a candidate's AABB reaches it. The
    /// result equals querying every cell: a collider that overlaps a cell box
    /// also overlaps the enclosing step box, and its (margin-expanded) AABB
    /// intersects that cell box.
    /// </summary>
    internal sealed class FlowFieldVolumeCellQuery
    {
        private const int InitialCapacity = 32;
        private const int MaxLayerCandidates = 4096;
        private const float BoundsMargin = 0.001f;

        private Collider[] _layerHits;
        private Bounds[] _layerBounds = new Bounds[InitialCapacity];
        private int _layerCount;
        private bool _layerComplete;

        // The registered obstacles as the build's synced pose found them.
        private Collider[] _registered = new Collider[8];
        private Bounds[] _registeredBounds = new Bounds[8];
        // Each Bounds' min and max, read once, for the per-step filter.
        private Vector3[] _registeredMin = new Vector3[8];
        private Vector3[] _registeredMax = new Vector3[8];
        private int[] _registeredLayers = new int[8];
        private int _registeredCount;
        // The captured entries whose bounds reach the current step.
        private int[] _stepRegistered = new int[8];
        private int _stepRegisteredCount;

        private Bounds[] _modifierBounds = new Bounds[4];
        private bool[] _modifierNear = new bool[4];

        /// <summary>World box enclosing the query boxes of the listed cells.</summary>
        internal static Bounds StepBounds(FlowFieldGridSpace grid, int[] cells, int count, Vector3 halfExtents)
        {
            Vector3 min = grid.CellCenter(cells[0]);
            Vector3 max = min;
            for (int i = 1; i < count; i++)
            {
                Vector3 center = grid.CellCenter(cells[i]);
                min = Vector3.Min(min, center);
                max = Vector3.Max(max, center);
            }
            var bounds = new Bounds();
            bounds.SetMinMax(min - halfExtents, max + halfExtents);
            return bounds;
        }

        /// <summary>
        /// Reads every registered obstacle once for the build, right after its
        /// Physics.SyncTransforms: the Collider, its expanded bounds and its
        /// layer. A registered Collider that moves or leaves later in the build
        /// is notified, and its uncertain region outlives this build.
        /// </summary>
        internal void CaptureRegistered(FlowFieldObstacleRegistry registry)
        {
            Array.Clear(_registered, 0, _registeredCount);
            _registeredCount = 0;
            for (int index = 0; index < registry.Count; index++)
            {
                Collider collider = registry.GetCollider(index);
                if (collider == null || collider.isTrigger)
                    continue;
                if (_registeredCount == _registered.Length)
                {
                    Array.Resize(ref _registered, _registeredCount * 2);
                    Array.Resize(ref _registeredBounds, _registeredCount * 2);
                    Array.Resize(ref _registeredMin, _registeredCount * 2);
                    Array.Resize(ref _registeredMax, _registeredCount * 2);
                    Array.Resize(ref _registeredLayers, _registeredCount * 2);
                    Array.Resize(ref _stepRegistered, _registeredCount * 2);
                }
                Bounds bounds = ExpandedBounds(collider);
                _registered[_registeredCount] = collider;
                _registeredBounds[_registeredCount] = bounds;
                _registeredMin[_registeredCount] = bounds.min;
                _registeredMax[_registeredCount] = bounds.max;
                _registeredLayers[_registeredCount] = 1 << collider.gameObject.layer;
                _registeredCount++;
            }
        }

        internal void BeginObstacleStep(Bounds stepBox, LayerMask obstacleLayer)
        {
            CollectLayerCandidates(stepBox, obstacleLayer);
            _stepRegisteredCount = 0;
            // Bounds.Intersects with every min and max read once: a build at a
            // million cells runs this for each registered obstacle 15,625 times.
            Vector3 stepMin = stepBox.min;
            Vector3 stepMax = stepBox.max;
            for (int index = 0; index < _registeredCount; index++)
            {
                Vector3 min = _registeredMin[index];
                Vector3 max = _registeredMax[index];
                if (min.x <= stepMax.x && max.x >= stepMin.x
                    && min.y <= stepMax.y && max.y >= stepMin.y
                    && min.z <= stepMax.z && max.z >= stepMin.z)
                    _stepRegistered[_stepRegisteredCount++] = index;
            }
        }

        /// <summary>False only when no layer collider can overlap the cell box.</summary>
        internal bool MayHitLayer(in Bounds cellBox)
        {
            if (!_layerComplete)
                return true;
            for (int index = 0; index < _layerCount; index++)
            {
                if (_layerBounds[index].Intersects(cellBox))
                    return true;
            }
            return false;
        }

        internal bool IntersectsRegistered(
            Vector3 center,
            Vector3 halfExtents,
            in Bounds cellBox,
            ref Collider[] overlapBuffer)
        {
            for (int step = 0; step < _stepRegisteredCount; step++)
            {
                int index = _stepRegistered[step];
                if (!_registeredBounds[index].Intersects(cellBox))
                    continue;
                int layerMask = _registeredLayers[index];
                if (!Physics.CheckBox(
                        center,
                        halfExtents,
                        Quaternion.identity,
                        layerMask,
                        QueryTriggerInteraction.Ignore))
                    continue;
                if (FlowFieldOverlapUtility.OverlapsTarget(
                        center,
                        halfExtents,
                        layerMask,
                        _registered[index],
                        QueryTriggerInteraction.Ignore,
                        ref overlapBuffer))
                    return true;
            }
            return false;
        }

        internal void BeginModifierStep(Bounds stepBox, FlowFieldModifierWorkItem[] modifiers)
        {
            if (_modifierBounds.Length < modifiers.Length)
            {
                _modifierBounds = new Bounds[modifiers.Length];
                _modifierNear = new bool[modifiers.Length];
            }
            for (int index = 0; index < modifiers.Length; index++)
            {
                Collider collider = modifiers[index].InfluenceCollider;
                if (collider == null)
                {
                    _modifierNear[index] = false;
                    continue;
                }
                _modifierBounds[index] = ExpandedBounds(collider);
                _modifierNear[index] = _modifierBounds[index].Intersects(stepBox);
            }
        }

        /// <summary>False only when the modifier's influence collider cannot overlap the cell box.</summary>
        internal bool MayTouchModifier(int modifierIndex, in Bounds cellBox)
            => _modifierNear[modifierIndex] && _modifierBounds[modifierIndex].Intersects(cellBox);

        /// <summary>Drops collider references held between steps.</summary>
        internal void Clear()
        {
            if (_layerHits != null)
                Array.Clear(_layerHits, 0, _layerHits.Length);
            Array.Clear(_registered, 0, _registered.Length);
            _layerCount = 0;
            _registeredCount = 0;
            _stepRegisteredCount = 0;
        }

        private void CollectLayerCandidates(Bounds stepBox, LayerMask obstacleLayer)
        {
            _layerCount = 0;
            _layerComplete = true;
            if (obstacleLayer.value == 0)
                return;
            FlowFieldOverlapUtility.EnsureCapacity(ref _layerHits, InitialCapacity, InitialCapacity, MaxLayerCandidates);
            while (true)
            {
                int count = Physics.OverlapBoxNonAlloc(
                    stepBox.center,
                    stepBox.extents,
                    _layerHits,
                    Quaternion.identity,
                    obstacleLayer,
                    QueryTriggerInteraction.Ignore);
                if (count < _layerHits.Length)
                {
                    _layerCount = count;
                    break;
                }
                if (_layerHits.Length >= MaxLayerCandidates)
                {
                    // Too many candidates to list: every cell of this step
                    // falls back to its own CheckBox.
                    _layerComplete = false;
                    return;
                }
                FlowFieldOverlapUtility.EnsureCapacity(
                    ref _layerHits, _layerHits.Length * 2, InitialCapacity, MaxLayerCandidates);
            }
            if (_layerBounds.Length < _layerCount)
                _layerBounds = new Bounds[_layerHits.Length];
            for (int index = 0; index < _layerCount; index++)
                _layerBounds[index] = ExpandedBounds(_layerHits[index]);
        }

        // A query may count colliders within their contact offset, so the
        // AABB filter keeps that distance plus a small numerical margin.
        private static Bounds ExpandedBounds(Collider collider)
        {
            Bounds bounds = collider.bounds;
            bounds.Expand(2f * (Mathf.Max(0f, collider.contactOffset) + BoundsMargin));
            return bounds;
        }
    }
}
