using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The Manager as an area of a <see cref="FlowFieldWorld"/>: the checks
    /// that its grid fits the world, joining at Init and leaving at Release
    /// and destruction, and moving its area when its grid changes.
    /// </summary>
    public partial class FlowFieldManager : IFlowFieldWorldMember
    {
        [SerializeField, Tooltip("The world this Manager is an area of, stitched to the areas its grid touches. It can change only after Release.")]
        private FlowFieldWorld _world;

        // The world Init joined, until Release.
        private FlowFieldWorld _joinedWorld;

        /// <summary>The world this Manager joins at Init, or null.</summary>
        public FlowFieldWorld World => _world;

        // The area this Manager is in its joined world, or null.
        internal FlowFieldWorldArea WorldArea => _joinedWorld != null ? _joinedWorld.Areas.Find(this) : null;

        /// <summary>
        /// The checks a world adds: the world stays from Init to Release, and
        /// the grid uses its Space Mode, Cell Size and lattice and overlaps no
        /// other area. A grid the settings cannot make is the other checks' to
        /// report.
        /// </summary>
        private void ValidateWorld(FlowFieldSpaceMode spaceMode)
        {
            if (_joinedWorld != null && _world != _joinedWorld)
                throw new InvalidOperationException("The FlowField World can change only after Release.");
            if (_world == null || !TryWorldLayout(spaceMode, out FlowFieldGridSpace grid))
                return;
            if (!FlowFieldWorldAreas.TryLatticeBox(_world, grid, out FlowFieldLatticeBox box, out string reason))
                throw new InvalidOperationException(reason);
            FlowFieldWorldArea other = _world.Areas.Overlapping(in box, this);
            if (other != null)
                throw new InvalidOperationException(
                    $"This area overlaps the area of {other.Member.Name} in the FlowField World {_world.name}.");
        }

        /// <summary>
        /// Whether the grid's settings fit the world, for the Inspector; true
        /// without a world. The overlap with other areas is checked at Init.
        /// </summary>
        internal bool TryValidateWorldLattice(out string reason)
        {
            reason = null;
            return _world == null
                || !TryWorldLayout(SpaceMode, out FlowFieldGridSpace grid)
                || FlowFieldWorldAreas.TryLatticeBox(_world, grid, out _, out reason);
        }

        private bool TryWorldLayout(FlowFieldSpaceMode spaceMode, out FlowFieldGridSpace grid)
        {
            FlowFieldManagerConfig config = CaptureConfig(spaceMode, _bakeMode);
            return FlowFieldSpaceStrategy.For(spaceMode).TryCreateLayout(in config, out _, out grid);
        }

        // Init has checked the world.
        private void JoinWorld()
        {
            if (_world == null || !TryWorldLayout(SpaceMode, out FlowFieldGridSpace grid)
                || !FlowFieldWorldAreas.TryLatticeBox(_world, grid, out FlowFieldLatticeBox box, out _))
                return;
            _world.Areas.Join(this, grid, in box);
            _joinedWorld = _world;
        }

        // A settings change that moved the grid moves the area; the checks
        // that refuse an overlap ran before the submit.
        private void RefreshWorldArea()
        {
            FlowFieldWorldArea area = WorldArea;
            if (area == null || !TryWorldLayout(SpaceMode, out FlowFieldGridSpace grid)
                || !FlowFieldWorldAreas.TryLatticeBox(_joinedWorld, grid, out FlowFieldLatticeBox box, out _))
                return;
            if (area.Box.Equals(box))
                return;
            _joinedWorld.Areas.Join(this, grid, in box);
        }

        // A world's commands run as this Manager's operation, so handlers run
        // after the world has finished, as after the Manager's own commands.
        internal FlowFieldExecutionBoundary.OperationScope EnterOperation() => _boundary.EnterOperation();

        internal bool IsInUserCode => _boundary.IsInUserCode;

        string IFlowFieldWorldMember.Name => this != null ? name : "(destroyed)";
        int IFlowFieldWorldMember.WorldSessionCount => WorldSessionCount;
        FlowFieldSession IFlowFieldWorldMember.WorldSession(int index) => WorldSession(index);
        string IFlowFieldWorldMember.WorldSessionName(int index) => WorldSessionName(index);
        FlowFieldExecutionBoundary.OperationScope IFlowFieldWorldMember.EnterOperation() => EnterOperation();
        bool IFlowFieldWorldMember.IsInUserCode => IsInUserCode;

        private void LeaveWorld()
        {
            if (_joinedWorld == null)
                return;
            _joinedWorld.Areas.Leave(this);
            _joinedWorld = null;
        }
    }
}
