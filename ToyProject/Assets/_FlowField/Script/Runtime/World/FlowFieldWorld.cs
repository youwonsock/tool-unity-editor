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

        internal static string NameOf(Object owner) => owner != null ? owner.name : "(destroyed)";
    }
}
