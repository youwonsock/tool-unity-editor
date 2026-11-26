using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// A world of areas: Managers that reference this asset and stitch their fields where their
    /// grids touch, across scenes loaded additively. Every area uses this Space Mode and Cell
    /// Size, puts its grid on this lattice, and overlaps no other area. Init joins a Manager to
    /// its world and Release, disabling the scene or destroying it removes it again.
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

        /// <summary>The Space Mode every area uses.</summary>
        public FlowFieldSpaceMode SpaceMode => _spaceMode;
        /// <summary>The Cell Size every area uses.</summary>
        public float CellSize => _cellSize;
        /// <summary>A lattice corner every area's grid origin lies on.</summary>
        public Vector3 LatticeOrigin => _latticeOrigin;

        internal FlowFieldWorldAreas Areas => FlowFieldWorldAreas.For(this);

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
        /// nearest the Goal by a straight-line estimate and stops there. Areas that join later build
        /// it too; StaticBaked areas do not. Read it with <see cref="TrySample"/> or each area's
        /// Manager, which knows the same handle.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="request"/> has no Goal or a positive Influence Radius: a world Goal reaches every area.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a defined value.</exception>
        /// <exception cref="InvalidOperationException">Called outside Play Mode.</exception>
        public FlowFieldGoalHandle AddGoal(FlowFieldGoalKind kind, in FlowFieldGoalRequest request)
        {
            ThrowIfNotPlaying();
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
            ValidateRequest(in request);
            return goal.IsValid && Areas.Goals.TrySet(goal.Id, request);
        }

        /// <summary>Removes a world Goal from every area.</summary>
        /// <returns>False when the handle names no Goal of this world.</returns>
        public bool RemoveGoal(FlowFieldGoalHandle goal) => goal.IsValid && Areas.Goals.Remove(goal.Id);

        /// <summary>Whether every joined area that builds Goals has a field of the world Goal.</summary>
        public bool IsGoalReady(FlowFieldGoalHandle goal)
        {
            if (!goal.IsValid || !Areas.Goals.Contains(goal.Id))
                return false;
            IReadOnlyList<FlowFieldManager> managers = Managers;
            for (int i = 0; i < managers.Count; i++)
            {
                if (managers[i].BakeMode != FlowFieldBakeMode.StaticBaked && !managers[i].IsGoalReady(goal))
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

        private static void ValidateRequest(in FlowFieldGoalRequest request)
        {
            if (!request.HasGoal)
                throw new System.ArgumentException("A world Goal needs a position; FlowFieldGoalRequest.None names no Goal.", nameof(request));
            if (request.InfluenceRadius > 0f)
                throw new System.ArgumentException("A world Goal reaches every area; use an Influence Radius of 0.", nameof(request));
        }

        private static void ThrowIfNotPlaying()
        {
            if (!Application.isPlaying)
                throw new System.InvalidOperationException("FlowField World Goals run only in Play Mode.");
        }

        internal static string NameOf(Object owner) => owner != null ? owner.name : "(destroyed)";
    }
}
