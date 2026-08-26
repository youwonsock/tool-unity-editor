#if UNITY_EDITOR
using UnityEngine;

namespace Supercent.Common.FlowField
{
    public partial class FlowFieldManager
    {
        private FlowFieldEditorPreview _editorPreview;

        private void EnsureEditorPreview(FlowFieldGridSpace grid)
        {
            EnsureServices();
            FlowFieldGoalResolution goal = FlowFieldGoalPipeline.Resolve(
                grid,
                _goalTransform,
                _hasExplicitGoal,
                _explicitGoalWorld,
                _goalInfluenceRadius);
            _editorPreview.Ensure(
                grid,
                _surfaceBakeData,
                _coarseTopologyData,
                _fineRingCoarseRadius,
                FlowFieldVectorUtility.SanitizeDefaultDirection(_defaultFlowDirection),
                _obstacleLayer,
                _obstacleCheckHeight,
                _obstacleCheckCenterOffset,
                _obstacleClearance,
                _obstaclePipeline,
                _modifierPipeline,
                goal.IsValid,
                goal.LocalX,
                goal.LocalZ,
                goal.InfluenceRadius,
                _refreshRate);
        }

        private FlowFieldWorkspace ResolveGizmoWorkspace()
            => Application.isPlaying ? _context.Workspace : _editorPreview.Workspace;

        private void InvalidateEditorPreview()
            => _editorPreview?.Invalidate();

        private void DisposeEditorPreview()
        {
            _editorPreview?.Dispose();
            _editorPreview = null;
        }
    }
}
#endif
