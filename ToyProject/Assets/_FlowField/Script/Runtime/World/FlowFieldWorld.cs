using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// A world of areas: Managers that reference this asset and stitch their fields where their
    /// grids touch, across scenes loaded additively. Every area uses this Space Mode and Cell
    /// Size, puts its grid on this lattice, and overlaps no other area; these settings cannot
    /// change while an area is joined. An enabled Manager joins its world at Init, a disabled one
    /// when it is enabled. Disabling it leaves the world as the area of an unloaded scene does
    /// until it is enabled again, and Release or destroying it removes it. The world bake, run in
    /// the Editor with the world's scenes open, searches the Bake Goals across every area, so a
    /// world Goal in one of their cells leads through scenes that are not loaded.
    /// </summary>
    [CreateAssetMenu(fileName = "FlowFieldWorld", menuName = "FlowField/World")]
    public sealed class FlowFieldWorld : ScriptableObject
    {
        [SerializeField, Tooltip("The Space Mode of every area.")]
        private FlowFieldSpaceMode _spaceMode = FlowFieldSpaceMode.Surface2D;
        [SerializeField, Tooltip("The Cell Size of every area.")]
        private float _cellSize = 0.5f;
        [SerializeField, Tooltip("A corner of the lattice every area's grid starts on. Surface2D aligns X and Z, Volume3D all three axes.")]
        private Vector3 _latticeOrigin;
        [SerializeField, Tooltip("Positions the world bake searches across the areas of the open scenes. A world Goal in the cell of one leads through areas that are not loaded the way the bake found, instead of by a straight-line estimate. Bake again after a scene of the world changes.")]
        private Vector3[] _bakeGoals = Array.Empty<Vector3>();
        [SerializeField, HideInInspector]
        private FlowFieldWorldBake _bake;

        /// <summary>The Space Mode every area uses.</summary>
        public FlowFieldSpaceMode SpaceMode => _spaceMode;
        /// <summary>The Cell Size every area uses.</summary>
        public float CellSize => _cellSize;
        /// <summary>A lattice corner every area's grid origin lies on.</summary>
        public Vector3 LatticeOrigin => _latticeOrigin;

        internal FlowFieldWorldAreas Areas => FlowFieldWorldAreas.For(this);

        internal IReadOnlyList<Vector3> BakeGoals => _bakeGoals ?? Array.Empty<Vector3>();

        // The last world bake, or null; the Editor bake writes it.
        internal FlowFieldWorldBake Bake
        {
            get => _bake;
            set => _bake = value;
        }

        /// <summary>
        /// The baked steps toward a Goal at the position for the agent type,
        /// or null when the bake searched no Goal in its lattice cell or ran
        /// with other world settings.
        /// </summary>
        internal FlowFieldWorldBakedGoal FindBakedGoal(Vector3 position, string agentType)
        {
            if (_bake == null || !_bake.Matches(this))
                return null;
            FlowFieldWorldAreas.LatticeCell(this, position, out int x, out int y, out int z);
            return _bake.Find(x, y, z, agentType);
        }

        // Whether an area of the bake holds the lattice cell, under this world's settings.
        internal bool BakeCovers(int x, int y, int z)
            => _bake != null && _bake.Matches(this) && _bake.Covers(x, y, z);

        // Whether the bake holds an area at exactly this box.
        internal bool BakeHasArea(in FlowFieldLatticeBox box)
            => _bake != null && _bake.Matches(this) && _bake.HasArea(in box);

        /// <summary>The Managers that joined this world, in the order they joined.</summary>
        public IReadOnlyList<FlowFieldManager> Managers => Areas.Managers;

        /// <summary>The area whose grid holds the position, or false when no joined area does.</summary>
        public bool TryGetArea(Vector3 worldPosition, out FlowFieldManager area)
        {
            area = null;
            if (!FlowFieldGridSpace.IsFinite(worldPosition))
                return false;
            FlowFieldWorldArea found = Areas.Find(worldPosition);
            area = found?.Manager;
            return area != null;
        }

        /// <summary>
        /// Adds a Goal of the whole world. Every area builds a field toward it, the area holding it
        /// from its cell and the others from the steps their touching areas need, so a mover follows
        /// it across the areas. While no loaded area holds it, each area leads to its outer border
        /// and stops there: the way the world bake found when it searched a Bake Goal in the Goal's
        /// cell, else by a straight-line estimate toward the Goal. Areas that join later build
        /// it too. A StaticBaked area searches it on its baked cells, with no Physics. Read it with
        /// <see cref="TrySample"/> or each area's Manager, which knows the same handle.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="request"/> has no Goal or a positive Influence Radius: a world Goal reaches every area.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a defined value.</exception>
        /// <exception cref="InvalidOperationException">Called outside Play Mode.</exception>
        public FlowFieldGoalHandle AddGoal(FlowFieldGoalKind kind, in FlowFieldGoalRequest request)
        {
            ThrowIfNotPlaying();
            ThrowIfInUserCode(nameof(AddGoal));
            if (!System.Enum.IsDefined(typeof(FlowFieldGoalKind), kind))
                throw new System.ArgumentOutOfRangeException(nameof(kind));
            ValidateRequest(in request);
            return new FlowFieldGoalHandle(Areas.Goals.Add(kind, request));
        }

        /// <summary>Moves a world Goal; every area builds toward the new position.</summary>
        /// <returns>False when the handle names no Goal of this world.</returns>
        /// <exception cref="ArgumentException"><paramref name="request"/> has no Goal or a positive Influence Radius.</exception>
        /// <exception cref="InvalidOperationException">Called outside Play Mode.</exception>
        public bool SetGoal(FlowFieldGoalHandle goal, in FlowFieldGoalRequest request)
        {
            ThrowIfNotPlaying();
            ThrowIfInUserCode(nameof(SetGoal));
            ValidateRequest(in request);
            return goal.IsValid && Areas.Goals.TrySet(goal.Id, request);
        }

        /// <summary>Removes a world Goal from every area.</summary>
        /// <returns>False when the handle names no Goal of this world.</returns>
        public bool RemoveGoal(FlowFieldGoalHandle goal)
        {
            ThrowIfInUserCode(nameof(RemoveGoal));
            return goal.IsValid && Areas.Goals.Remove(goal.Id);
        }

        /// <summary>
        /// Whether every joined area has a field of the world Goal for its Default agent type. An area
        /// the Goal cannot reach yet, or at all, has none.
        /// </summary>
        public bool IsGoalReady(FlowFieldGoalHandle goal)
        {
            if (!goal.IsValid || !Areas.Goals.Contains(goal.Id))
                return false;
            IReadOnlyList<FlowFieldManager> managers = Managers;
            for (int i = 0; i < managers.Count; i++)
            {
                if (!managers[i].IsGoalReady(goal))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Reads the world Goal's field of the area holding the position, as that Manager's
        /// <see cref="FlowFieldManager.TrySample(Vector3, FlowFieldGoalHandle, out FlowFieldSample)"/>
        /// does. It never throws.
        /// </summary>
        /// <returns>False with <see cref="FlowFieldSample.Stopped"/> when no area holds the position or its field is not ready.</returns>
        public bool TrySample(Vector3 worldPosition, FlowFieldGoalHandle goal, out FlowFieldSample sample)
        {
            sample = FlowFieldSample.Stopped;
            return TryGetArea(worldPosition, out FlowFieldManager area) && area.TrySample(worldPosition, goal, out sample);
        }

        /// <summary>
        /// Registers an obstacle with every area, for one that may move from one area into another:
        /// each area rebuilds the cells it reaches, and areas that join later take it too. Report
        /// moves with <see cref="NotifyDynamicObstacleMoved"/>. StaticBaked areas keep their bake.
        /// </summary>
        /// <returns>False when the world already holds it.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="collider"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Called outside Play Mode or with a destroyed Collider.</exception>
        public bool RegisterDynamicObstacle(Collider collider)
        {
            ThrowIfNotPlaying();
            if (ReferenceEquals(collider, null))
                throw new System.ArgumentNullException(nameof(collider));
            if (collider == null)
                throw FlowFieldFailures.DestroyedCollider();
            return Areas.Registrations.RegisterObstacle(collider);
        }

        /// <summary>Removes a world obstacle from every area.</summary>
        /// <returns>False when the world does not hold it.</returns>
        public bool UnregisterDynamicObstacle(Collider collider)
            => !ReferenceEquals(collider, null) && Areas.Registrations.UnregisterObstacle(collider);

        /// <summary>A world obstacle moved; every area rebuilds where it was and where it is.</summary>
        /// <returns>False when the world does not hold it.</returns>
        public bool NotifyDynamicObstacleMoved(Collider collider)
            => !ReferenceEquals(collider, null) && Areas.Registrations.NotifyObstacleMoved(collider);

        /// <summary>Rebuilds the cells the Bounds reach in every area, as each Manager's own call does.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="worldBounds"/> is not finite.</exception>
        public void NotifyObstacleRegionDirty(Bounds worldBounds)
        {
            if (!FlowFieldGridSpace.IsFinite(worldBounds.center) || !FlowFieldGridSpace.IsFinite(worldBounds.size))
                throw FlowFieldFailures.BoundsNotFinite(nameof(worldBounds));
            Areas.Registrations.NotifyRegionDirty(worldBounds);
        }

        /// <summary>
        /// Registers a Modifier with every area, for one whose influence may reach more than one:
        /// each area composes it on its own cells, and areas that join later take it too.
        /// </summary>
        /// <returns>False when the world already holds it.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="modifier"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Called outside Play Mode.</exception>
        public bool RegisterVectorModifier(IFlowFieldVectorModifier modifier)
        {
            ThrowIfNotPlaying();
            if (modifier == null)
                throw new System.ArgumentNullException(nameof(modifier));
            return Areas.Registrations.RegisterModifier(modifier);
        }

        /// <summary>Removes a world Modifier from every area.</summary>
        /// <returns>False when the world does not hold it.</returns>
        public bool UnregisterVectorModifier(IFlowFieldVectorModifier modifier)
            => modifier != null && Areas.Registrations.UnregisterModifier(modifier);

        /// <summary>A world Modifier changed; every area that holds it composes again.</summary>
        /// <returns>False when the world does not hold it.</returns>
        public bool NotifyModifierChanged(IFlowFieldVectorModifier modifier, FlowFieldModifierChange change)
            => modifier != null && Areas.Registrations.NotifyModifierChanged(modifier, change);

        // The joined areas stand on the lattice they joined, so an edit of it
        // while any area is joined is undone, as a Manager's Space Mode edit
        // waits for Release.
        private void OnValidate()
        {
            if (!Application.isPlaying
                || !FlowFieldWorldAreas.TryGetJoinedLattice(this, out FlowFieldSpaceMode spaceMode, out float cellSize, out Vector3 latticeOrigin)
                || _spaceMode == spaceMode && _cellSize == cellSize && _latticeOrigin == latticeOrigin)
                return;
            _spaceMode = spaceMode;
            _cellSize = cellSize;
            _latticeOrigin = latticeOrigin;
            Debug.LogWarning(
                $"[FlowField] The Space Mode, Cell Size and Lattice Origin of the FlowField World {name} can change only while no area is joined.",
                this);
        }

        private static void ValidateRequest(in FlowFieldGoalRequest request)
        {
            if (!request.HasGoal)
                throw new System.ArgumentException("A world Goal needs a position; FlowFieldGoalRequest.None names no Goal.", nameof(request));
            if (request.InfluenceRadius > 0f)
                throw new System.ArgumentException("A world Goal reaches every area; use an Influence Radius of 0.", nameof(request));
        }

        // Modifier code runs inside an area's operation, where a world command
        // would change what that operation reads.
        private void ThrowIfInUserCode(string command)
        {
            if (Areas.IsInUserCode)
                throw new System.InvalidOperationException(
                    $"{command} cannot be called from a Modifier's Observe, CaptureSnapshot or Modify.");
        }

        private static void ThrowIfNotPlaying()
        {
            if (!Application.isPlaying)
                throw new System.InvalidOperationException("FlowField World Goals run only in Play Mode.");
        }

    }
}
