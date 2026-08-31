#if UNITY_EDITOR
using System;
using UnityEngine;

namespace Common.FlowField
{
    internal sealed class FlowFieldEditorPreview
    {
        private const float MIN_INTERVAL = 0.2f;
        private readonly FlowFieldWorkspace _workspace = new FlowFieldWorkspace();
        private FlowFieldGridSpace _grid;
        private int _bakeRevision = -1;
        private double _lastBuildTime;
        private bool _valid;
        private bool _initialized;

        internal FlowFieldWorkspace Workspace => _workspace;

        internal void Init()
        {
            if (_initialized)
                throw new InvalidOperationException("Editor preview is already initialized.");
            _initialized = true;
        }

        internal void Refresh(
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
                throw new ArgumentException("Editor preview requires a valid grid and surface bake.");

            double now = UnityEditor.EditorApplication.timeSinceStartup;
            if (refreshRate < MIN_INTERVAL || float.IsNaN(refreshRate) || float.IsInfinity(refreshRate))
                throw new ArgumentOutOfRangeException(nameof(refreshRate));
            double minInterval = refreshRate;
            if (_valid
                && _grid.MatchesBounds(grid)
                && _bakeRevision == surface.Revision
                && now - _lastBuildTime < minInterval)
                return;

            _workspace.Resize(grid.CellCount);
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

        public void Release()
        {
            if (!_initialized)
                return;
            _workspace.Release();
            _initialized = false;
            _valid = false;
        }
    }
}
#endif
