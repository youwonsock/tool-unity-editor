using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The Manager as an area of a <see cref="FlowFieldWorld"/>: the checks
    /// that its grid fits the world, joining at Init or when it is enabled,
    /// leaving when it is disabled, released or destroyed, and moving its
    /// area when its grid changes.
    /// </summary>
    public partial class FlowFieldManager : IFlowFieldWorldMember
    {
        [SerializeField, Tooltip("The world this Manager is an area of, stitched to the areas its grid touches. It can change only after Release.")]
        private FlowFieldWorld _world;

        // The world Init bound, until Release. The Manager is an area of it
        // while it is enabled: disabling leaves the world as the area of an
        // unloaded scene does, and enabling joins it again.
        private FlowFieldWorld _boundWorld;

        /// <summary>The world this Manager joins at Init, or null.</summary>
        public FlowFieldWorld World => _world;

        // The area this Manager is in its bound world, or null.
        internal FlowFieldWorldArea WorldArea => _boundWorld != null ? _boundWorld.Areas.Find(this) : null;

        /// <summary>
        /// The checks a world adds: the world stays from Init to Release, and
        /// the grid uses its Space Mode, Cell Size and lattice and overlaps no
        /// other area. A grid the settings cannot make is the other checks' to
        /// report.
        /// </summary>
        private void ValidateWorld(FlowFieldSpaceMode spaceMode)
        {
            if (_boundWorld != null && _world != _boundWorld)
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

        // Init has checked the world. A disabled Manager joins it when it is
        // enabled, so one that is never enabled leaves no area behind.
        private void BindWorld()
        {
            _boundWorld = _world;
            EnterWorld();
        }

        // Joins the bound world where the grid is now. The checks that refuse
        // an overlap ran before: Init's, or enabling's.
        private void EnterWorld()
        {
            if (_boundWorld == null || !isActiveAndEnabled || WorldArea != null
                || !TryWorldLayout(SpaceMode, out FlowFieldGridSpace grid)
                || !FlowFieldWorldAreas.TryLatticeBox(_boundWorld, grid, out FlowFieldLatticeBox box, out _))
                return;
            _boundWorld.Areas.Join(this, grid, in box);
        }

        // Disabling: the area leaves and gives back the world's Goals and
        // registrations until it is enabled.
        private void SuspendWorldArea()
        {
            if (_boundWorld != null)
                _boundWorld.Areas.Suspend(this);
        }

        // A settings change that moved the grid moves the area; the checks
        // that refuse an overlap ran before the submit.
        private void RefreshWorldArea()
        {
            FlowFieldWorldArea area = WorldArea;
            if (area == null || !TryWorldLayout(SpaceMode, out FlowFieldGridSpace grid)
                || !FlowFieldWorldAreas.TryLatticeBox(_boundWorld, grid, out FlowFieldLatticeBox box, out _))
                return;
            if (area.Box.Equals(box))
                return;
            _boundWorld.Areas.Join(this, grid, in box);
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
            if (_boundWorld == null)
                return;
            _boundWorld.Areas.Leave(this);
            _boundWorld = null;
        }
    }
}
