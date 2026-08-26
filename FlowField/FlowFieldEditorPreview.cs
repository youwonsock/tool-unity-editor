#if UNITY_EDITOR
using System;
using UnityEngine;

namespace Supercent.Common.FlowField
{
    internal sealed class FlowFieldEditorPreview : IDisposable
    {
        private const float MIN_INTERVAL = 0.2f;
        private readonly FlowFieldWorkspace _workspace = new FlowFieldWorkspace();
        private FlowFieldGridSpace _grid;
        private int _bakeRevision = -1;
        private double _lastBuildTime;
        private bool _valid;

        internal FlowFieldWorkspace Workspace => _workspace;

        internal void Ensure(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            FlowFieldCoarseTopologyData coarseTopology,
            int fineRingCoarseRadius,
            Vector3 defaultDirection,
            LayerMask obstacleLayer,
            float obstacleCheckHeight,
            float obstacleCheckCenterOffset,
            float obstacleClearance,
            FlowFieldObstaclePipeline obstaclePipeline,
            FlowFieldModifierPipeline modifierPipeline,
            bool hasGoal,
            int goalX,
            int goalZ,
            float goalRadius,
            float refreshRate)
        {
            if (!grid.IsValid || surface == null || !surface.HasValidData)
                return;

            double now = UnityEditor.EditorApplication.timeSinceStartup;
            double minInterval = Mathf.Max(MIN_INTERVAL, refreshRate);
            if (_valid
                && _grid.MatchesBounds(grid)
                && _bakeRevision == surface.Revision
                && now - _lastBuildTime < minInterval)
                return;

            _workspace.EnsureCapacity(grid.CellCount);
            if (obstaclePipeline.BuildFullLayerScratch(
                    grid,
                    surface,
                    obstacleLayer,
                    obstacleCheckHeight,
                    obstacleCheckCenterOffset,
                    obstacleClearance,
                    _workspace.ObstacleScratch,
                    syncTransforms: true))
            {
                obstaclePipeline.CommitPreviewAndBuildEscape(
                    grid,
                    surface,
                    _workspace);
            }

            if (hasGoal)
            {
                FlowFieldHierarchicalGoalSolver.BuildHierarchicalGoal(
                    grid,
                    surface,
                    coarseTopology,
                    _workspace,
                    goalX,
                    goalZ,
                    goalRadius,
                    fineRingCoarseRadius,
                    out _);
            }
            else
            {
                _workspace.ClearGoal();
            }

            modifierPipeline.BuildEditorFinalField(
                grid,
                surface,
                _workspace,
                defaultDirection,
                obstacleCheckHeight,
                obstacleCheckCenterOffset);
            _grid = grid;
            _bakeRevision = surface.Revision;
            _lastBuildTime = now;
            _valid = true;
        }

        internal void Invalidate()
        {
            _valid = false;
            _grid = default;
            _bakeRevision = -1;
        }

        public void Dispose()
            => _workspace.Dispose();
    }
}
#endif
