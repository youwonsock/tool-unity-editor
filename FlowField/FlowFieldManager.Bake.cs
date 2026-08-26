using UnityEngine;

namespace Supercent.Common.FlowField
{
    public partial class FlowFieldManager
    {
        private FlowFieldSurfaceRequest CreateSurfaceRequest()
            => new FlowFieldSurfaceRequest(
                _context,
                CreateSurfaceBakeSettings(),
                _surfaceBakeData,
                _staticObstacleBakeData,
                _coarseTopologyData,
                _obstacleLayer,
                _obstacleCheckHeight,
                _obstacleCheckCenterOffset,
                _obstacleClearance,
                _coarseCellMultiplier,
                _coarseWalkableRatio);

        internal FlowFieldSurfaceBakeSettings CreateSurfaceBakeSettings()
        {
            TryGetBakeLayout(out Bounds worldBounds, out FlowFieldGridSpace grid);
            return new FlowFieldSurfaceBakeSettings(
                grid,
                worldBounds,
                _groundBakeLayer,
                _maxSurfaceSlope,
                _maxStepHeight);
        }

        internal bool TryGetBakeLayout(out Bounds worldBounds, out FlowFieldGridSpace grid)
            => FlowFieldBakeBoundsUtility.TryCreateWorldLayout(
                transform.position,
                _bakeBoundsLocal,
                _cellSize,
                out worldBounds,
                out grid);

        internal void SetBakeBoundsLocal(Bounds localBounds)
        {
            Bounds snapped = FlowFieldBakeBoundsUtility.SnapCenterAnchored(localBounds, _cellSize);
            if (FlowFieldBakeBoundsUtility.Approximately(_bakeBoundsLocal, snapped))
                return;

            _bakeBoundsLocal = snapped;
            _invalidBakeWarningIssued = false;
            _context.DirtyFlags = FlowFieldDirtyFlags.All;
#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif
        }

        internal bool TryValidateSurfaceBake(out string reason)
            => FlowFieldSurfacePipeline.TryValidate(CreateSurfaceRequest(), out reason);

        internal void AssignSurfaceBakeData(FlowFieldSurfaceBakeData bakeData)
        {
            if (_surfaceBakeData == bakeData)
                return;

            _surfaceBakeData = bakeData;
            _invalidBakeWarningIssued = false;
            _context.DirtyFlags = FlowFieldDirtyFlags.All;
#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif
        }

        internal void AssignStaticObstacleBakeData(FlowFieldStaticObstacleBakeData bakeData)
        {
            if (_staticObstacleBakeData == bakeData)
                return;

            _staticObstacleBakeData = bakeData;
            _invalidBakeWarningIssued = false;
            _context.DirtyFlags = FlowFieldDirtyFlags.All;
#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif
        }

        internal void AssignCoarseTopologyData(FlowFieldCoarseTopologyData bakeData)
        {
            if (_coarseTopologyData == bakeData)
                return;

            _coarseTopologyData = bakeData;
            _invalidBakeWarningIssued = false;
            _context.DirtyFlags = FlowFieldDirtyFlags.All;
#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif
        }

        internal void NotifySurfaceBakeChanged()
        {
            _invalidBakeWarningIssued = false;
            _context.DirtyFlags = FlowFieldDirtyFlags.All;
#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif
        }
    }
}
