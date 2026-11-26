using System;
using UnityEngine;
using BuildPhase = Common.FlowField.FlowFieldSurfaceBuildPhase;

namespace Common.FlowField
{
    /// <summary>The stages of one Surface2D rebuild, from a pending request to the published field.</summary>
    internal sealed partial class FlowFieldSurfaceBuilder
    {
        private void BuildPending(FlowFieldSurfaceBuild build)
        {
            if (!_inputs.Requests.Pending)
                return;

            FlowFieldSurfaceRequest request = _inputs.Requests.Latest;
            _inputs.Requests.Pending = false;
            FlowFieldSurfaceDirtyFlags pending = _inputs.DirtyFlags;
            _inputs.DirtyFlags = FlowFieldSurfaceDirtyFlags.None;

            // A failure leaves through StepBuild and the session faults.
            bool surfaceChanged = _baseStage.PrepareSurface(request, pending, _modifierComposition);
            // Surface preparation may invalidate every downstream stage
            // (for example after a grid change). Include those flags in
            // this build rather than leaving them for a later no-op tick.
            if (surfaceChanged)
                pending |= _inputs.DirtyFlags;
            else
                pending &= ~(FlowFieldSurfaceDirtyFlags.Grid | FlowFieldSurfaceDirtyFlags.Ground);
            _inputs.DirtyFlags = FlowFieldSurfaceDirtyFlags.None;
            if (request.SourceKind == FlowFieldSurfaceSourceKind.StaticSnapshot)
            {
                BuildStaticSnapshot(build, request, pending);
                return;
            }

            // The sweep decides what the masks hold, so starting or stopping
            // it rebuilds every obstacle.
            bool sweepsObstacles = _host.SweepsObstacles;
            if (sweepsObstacles != _masksSweep)
            {
                _masksSweep = sweepsObstacles;
                pending |= FlowFieldSurfaceDirtyFlags.StaticObstacles
                    | FlowFieldSurfaceDirtyFlags.DynamicObstacles
                    | FlowFieldSurfaceDirtyFlags.Escape
                    | FlowFieldSurfaceDirtyFlags.FinalRegion;
                _inputs.ExpandObstacleDirty(FlowFieldCellRect.Full(_state.Grid));
            }
            _baseStage.BuildSceneBase(request, ref pending, surfaceChanged, sweepsObstacles);
            if ((pending & FlowFieldSurfaceDirtyFlags.ModifierArea) != 0)
            {
                FlowFieldCellRect modifierRegion = RebuildModifierAreaData(request);
                bool modifierAreaChanged = modifierRegion.IsValid;
                if (modifierAreaChanged)
                {
                    pending |= FlowFieldSurfaceDirtyFlags.FinalRegion;
                    _inputs.ExpandFinalDirty(modifierRegion);
                }
                if (!modifierAreaChanged
                    && _state.BaseComposed && _fieldStore.IsValid
                    && !IsBaseDirty(pending)
                    && (pending & (FlowFieldSurfaceDirtyFlags.ModifierStack
                        | FlowFieldSurfaceDirtyFlags.DefaultDirection)) == 0)
                {
                    pending &= ~(FlowFieldSurfaceDirtyFlags.ModifierArea | FlowFieldSurfaceDirtyFlags.FinalRegion);
                    _inputs.DirtyFinalRegion = FlowFieldCellRect.Invalid;
                }
            }
            bool topologyChanged = IsBaseDirty(pending);
            // A pending BFS recomposes the whole field when it completes
            // (OnBfsCompleted), and a rejected solve restores the published
            // field, so composing before the solve would never be visible.
            if (_state.Workspace.HasActiveGoal && topologyChanged)
            {
                StartBfs(build, request);
                return;
            }

            if ((pending & (FlowFieldSurfaceDirtyFlags.ModifierStack | FlowFieldSurfaceDirtyFlags.FinalRegion)) != 0)
            {
                ComposeThenCommit(
                    build,
                    request,
                    rebuildBase: IsBaseDirty(pending)
                        || (pending & FlowFieldSurfaceDirtyFlags.DefaultDirection) != 0
                        || !_state.BaseComposed,
                    resultChanged: (pending & FlowFieldSurfaceDirtyFlags.FinalRegion) != 0);
                return;
            }

            // Nothing to compose here: the branch above takes every final-region
            // change, so the published cells stay as they are and Commit copies
            // nothing.
            QueueCommit(
                build,
                request,
                resultChanged: false,
                includeBase: false,
                finalRegion: _state.LastComposedRegion);
        }

        private void BuildStaticSnapshot(
            FlowFieldSurfaceBuild build,
            in FlowFieldSurfaceRequest request,
            FlowFieldSurfaceDirtyFlags pending)
        {
            if (request.BakeSnapshot == null)
                throw new InvalidOperationException(MissingBakeMessage);

            bool reloadSnapshot = !_fieldStore.IsValid
                || !_state.SurfaceReady
                || !_state.Grid.MatchesBounds(request.SurfaceSettings.Grid)
                || !ReferenceEquals(_baseStage.LoadedSnapshot, request.BakeSnapshot);
            if (reloadSnapshot)
            {
                if (!request.BakeSnapshot.HasValidData)
                    throw new InvalidOperationException(MissingBakeMessage);
                // Static runtime never runs a Physics obstacle query or a BFS
                // backend. Loading the asset is the base-stage equivalent and
                // is only needed for the first build or when the referenced
                // snapshot/signature changed.
                request.BakeSnapshot.CopyToWorkspace(_state.Grid, _state.Workspace);
                FlowFieldSurfaceGraphTraversal.BuildGroundTopologyMasks(_state.Grid, _state.Surface, _state.Workspace);
                _baseStage.LoadedSnapshot = request.BakeSnapshot;

                _state.HasObstacleMask = true;
                _state.BaseComposed = false;
                _state.ResolvedDefaultDirection =
                    FlowFieldVectorUtility.NormalizeDefaultDirection(request.DefaultDirection);
            }
            else if ((pending & FlowFieldSurfaceDirtyFlags.DefaultDirection) != 0)
            {
                _state.ResolvedDefaultDirection =
                    FlowFieldVectorUtility.NormalizeDefaultDirection(request.DefaultDirection);
                _inputs.ExpandFinalDirty(FlowFieldCellRect.Full(_state.Grid));
            }

            if ((pending & FlowFieldSurfaceDirtyFlags.ModifierArea) != 0 || reloadSnapshot)
            {
                FlowFieldCellRect modifierRegion = RebuildModifierAreaData(request);
                if (modifierRegion.IsValid)
                {
                    pending |= FlowFieldSurfaceDirtyFlags.FinalRegion;
                    _inputs.ExpandFinalDirty(modifierRegion);
                }
            }

            // Default Direction is not serialized in a static snapshot. It
            // therefore changes the cached BaseDirection/Speed arrays even
            // though it never requires Physics or BFS; rebuild the base
            // vectors before applying the final modifier stack.
            bool rebuildBase = reloadSnapshot
                || !_state.BaseComposed
                || (pending & FlowFieldSurfaceDirtyFlags.DefaultDirection) != 0;
            bool finalDirty = rebuildBase
                || (pending & (FlowFieldSurfaceDirtyFlags.FinalRegion
                    | FlowFieldSurfaceDirtyFlags.ModifierArea
                    | FlowFieldSurfaceDirtyFlags.ModifierStack
                    | FlowFieldSurfaceDirtyFlags.DefaultDirection)) != 0;
            if (finalDirty)
            {
                ComposeThenCommit(build, request, rebuildBase, resultChanged: true);
                return;
            }

            QueueCommit(
                build,
                request,
                resultChanged: false,
                includeBase: false,
                finalRegion: _state.LastComposedRegion);
        }

        private FlowFieldCellRect RebuildModifierAreaData(in FlowFieldSurfaceRequest request)
        {
            using FlowFieldExecutionBoundary.OperationScope userCode = _host.EnterUserCode();
            return _modifierComposition.RebuildAreaData(
                new FlowFieldSurfaceModifierBuildRequest(
                    _state.Grid,
                    _state.Surface,
                    _state.Workspace,
                    request.ObstacleCheckHeight,
                    request.ObstacleCheckCenterOffset));
        }

        // Snapshots are taken here. The composition itself can continue over the
        // next frames, while input arriving meanwhile waits for the next build.
        // A composition that rebuilds the base publishes the base arrays too:
        // the sample safety rule, a restore after a rejected build and the bake
        // export all read them.
        private void ComposeThenCommit(
            FlowFieldSurfaceBuild build,
            in FlowFieldSurfaceRequest request,
            bool rebuildBase,
            bool resultChanged)
        {
            FlowFieldCellRect region = _inputs.DirtyFinalRegion.IsValid
                ? _inputs.DirtyFinalRegion
                : FlowFieldCellRect.Full(_state.Grid);
            using (_host.EnterUserCode())
            {
                _modifierComposition.BeginFinalField(
                    new FlowFieldSurfaceModifierBuildRequest(
                        _state.Grid,
                        _state.Surface,
                        _state.Workspace,
                        request.ObstacleCheckHeight,
                        request.ObstacleCheckCenterOffset),
                    _state.ResolvedDefaultDirection,
                    region,
                    rebuildBase);
            }
            _state.LastComposedRegion = rebuildBase ? FlowFieldCellRect.Full(_state.Grid) : region;
            _inputs.DirtyFinalRegion = FlowFieldCellRect.Invalid;
            build.BeginCompose(request, rebuildBase, resultChanged);
            ContinueCompose(build);
        }

        private void ContinueCompose(FlowFieldSurfaceBuild build)
        {
            bool finished;
            using (_host.EnterUserCode())
                finished = _modifierComposition.StepFinalField(in build.FrameBudget);
            // StepBuild yields until the composition finishes.
            if (!finished)
                return;

            _state.BaseComposed |= build.ComposeRebuildBase;
            QueueCommit(
                build,
                build.ComposeRequest,
                build.ComposeResultChanged,
                build.ComposeRebuildBase,
                _state.LastComposedRegion);
        }

        // Only a build with an active Goal solves; the others compose directly.
        private void StartBfs(FlowFieldSurfaceBuild build, in FlowFieldSurfaceRequest request)
        {
            FlowFieldSurfaceBfsRequest bfsRequest = new FlowFieldSurfaceBfsRequest(
                _state.Grid,
                _state.Surface,
                _state.Workspace,
                request.Goal.LocalX,
                request.Goal.LocalZ,
                request.Goal.InfluenceRadius,
                _state.Workspace.ResolvedGoalIndex);

            // Set first: the callback that reports the solve moves the phase on.
            build.Phase = BuildPhase.WaitingSolve;
            PrepareSolveBackend();
            bool accepted = _solveBackend.StartBfs(bfsRequest, _ => RunBfsCallback(build));
            // Only a disposed backend refuses a solve.
            if (!accepted && build.IsWaitingForWorker)
                throw new InvalidOperationException("FlowField BFS session could not be started.");
        }

        // The backend calls back from Poll, inside StepBuild: a failure leaves
        // through StepBuild and the session faults. Stop, a fault and Dispose
        // drop the build, so a late callback finds it no longer waiting.
        private void RunBfsCallback(FlowFieldSurfaceBuild build)
        {
            if (build.IsWaitingForWorker)
                OnBfsCompleted(build);
        }

        /// <summary>
        /// A solve or composition is published even when newer input has arrived:
        /// a Goal or obstacle that keeps moving must not starve movers of a field.
        /// The exceptions are a replaced Grid or stale configuration, whose cells
        /// no longer match, and a removed Goal, which movers must stop following.
        /// NotifyCellsDirty keeps the grid: the ground is scanned again by the
        /// next build.
        /// A request without a Goal keeps GoalRemoved set and has nothing to stop.
        /// A StaticBaked field loads its baked Goal and never sets GoalRemoved,
        /// so a composition that spans several pumps publishes.
        /// </summary>
        private bool RejectOutdatedWork()
        {
            bool gridChanged = (_inputs.DirtyFlags & FlowFieldSurfaceDirtyFlags.Grid) != 0;
            bool goalRemoved = _inputs.GoalRemoved && _state.Workspace.HasActiveGoal;
            if (!gridChanged && !goalRemoved)
                return false;
            RestoreStagingAfterStaleResult();
            RequeueRejectedGoal();
            return true;
        }

        /// <summary>
        /// Restored staging holds the published Goal, while the tracker still
        /// records the rejected one. Forget it so the next Goal stage rebuilds.
        /// </summary>
        private void RequeueRejectedGoal()
        {
            _goalTracker.ForgetLastBuild();
            _inputs.DirtyFlags |= FlowFieldSurfaceDirtyFlags.Goal | FlowFieldSurfaceDirtyFlags.FinalRegion;
            _inputs.ExpandFinalDirty(FlowFieldCellRect.Full(_state.Grid));
            if (_inputs.Requests.HasLatest)
                _inputs.Requests.Pending = true;
        }

        private void OnBfsCompleted(FlowFieldSurfaceBuild build)
        {
            if (RejectOutdatedWork())
            {
                build.Phase = BuildPhase.Rejected;
                return;
            }

            FlowFieldSurfaceRequest finalRequest = _inputs.Requests.Latest;
            FlowFieldSurfaceDirtyFlags pending = _inputs.DirtyFlags;
            // Obstacle or Goal input observed during this solve keeps its request
            // and dirty flags through Commit; the next owner boundary handles it.
            _inputs.Requests.Pending = IsBaseDirty(_inputs.DirtyFlags);

            // Default Direction is final-only: apply its newest value, owed
            // settings included, to the freshly solved base rather than solving
            // again. The owed Submit then finds it composed.
            if (FlowFieldVectorUtility.TryNormalizeDefaultDirection(
                    _inputs.ConfigOwed ? _inputs.OwedConfig.Flow.DefaultDirection : finalRequest.DefaultDirection,
                    out Vector3 resolvedDefault)
                && !FlowFieldVectorUtility.SameDefaultDirection(_state.ResolvedDefaultDirection, resolvedDefault))
                _state.ResolvedDefaultDirection = resolvedDefault;
            if ((pending & FlowFieldSurfaceDirtyFlags.ModifierArea) != 0)
                RebuildModifierAreaData(finalRequest);
            ComposeThenCommit(build, finalRequest, rebuildBase: true, resultChanged: true);
        }

        private void RestoreStagingAfterStaleResult()
        {
            bool sameSignature = _fieldStore.IsValid
                && _state.Workspace.Capacity == _fieldStore.Capacity
                && _fieldStore.Grid.MatchesBounds(_state.Grid)
                && _fieldStore.Surface != null
                && _state.Surface != null
                && SurfacesEqual(_fieldStore.Surface, _state.Surface);
            if (sameSignature)
            {
                _fieldStore.CopyGoalAndFieldToWorkspace(_state.Workspace);
            }
            else if (_state.Workspace.Capacity > 0)
            {
                _state.Workspace.ClearAll();
                _inputs.DirtyFlags |= FlowFieldSurfaceDirtyFlags.All;
                _inputs.DirtyFinalRegion = FlowFieldCellRect.Full(_state.Grid);
                _inputs.DirtyObstacleRegion = FlowFieldCellRect.Full(_state.Grid);
            }
        }

        // True when the published field now reads differently.
        private bool Commit(
            in FlowFieldSurfaceRequest request,
            bool resultChanged,
            bool includeBase,
            FlowFieldCellRect finalRegion)
        {
            if (!_state.SurfaceReady || !_state.Grid.IsValid)
                throw new InvalidOperationException("Cannot commit a FlowField without a valid staging surface.");
            bool actualChanged = false;

            if (resultChanged)
            {
                actualChanged = _fieldStore.CommitFromWorkspace(
                    _state.Grid,
                    _state.Surface,
                    _state.Workspace,
                    includeBase,
                    finalRegion.IsValid ? finalRegion : FlowFieldCellRect.Full(_state.Grid));
            }
            _fieldStore.CommittedRequest = request;
            if (!_inputs.Requests.Pending)
            {
                _inputs.DirtyFlags = FlowFieldSurfaceDirtyFlags.None;
                _inputs.DirtyFinalRegion = FlowFieldCellRect.Invalid;
                _inputs.DirtyObstacleRegion = FlowFieldCellRect.Invalid;
            }
            return actualChanged;
        }

        private static bool SurfacesEqual(
            FlowFieldSurfaceData left,
            FlowFieldSurfaceData right)
            => ReferenceEquals(left, right)
                || left != null
                && right != null
                && left.ContentEquals(right);


        private bool HasActiveBuild => _build is { IsLive: true };

        // The base stages run in one frame so their Physics reads form one
        // snapshot, so one build begins per pump whatever the budget says.
        public FlowFieldBuildPolicy BuildPolicy => new FlowFieldBuildPolicy(beginsAfterBudget: true, maxBuildsBegunPerPump: 1);

        public bool HasOwedInput => _inputs.Requests.Pending || _inputs.ConfigOwed;

        public bool TryBeginBuild(out FlowFieldBuildStart start, out IFlowFieldBuild build)
        {
            build = null;
            Bounds? invalidated = null;
            if (_inputs.ConfigOwed)
            {
                _inputs.ConfigOwed = false;
                invalidated = Submit(CreateCurrentRequest(in _inputs.OwedConfig));
            }
            // A build with a new grid or obstacle mask queries every obstacle.
            start = new FlowFieldBuildStart((_inputs.DirtyFlags & ObstacleQueryMask) != 0, invalidated);
            if (!_inputs.Requests.Pending)
                return false;
            build = _build = new FlowFieldSurfaceBuild(this);
            return true;
        }

        // One step of the build the session runs; the build forwards here.
        internal FlowFieldBuildStep StepBuild(FlowFieldSurfaceBuild build, in FlowFieldBuildBudget budget, bool waitForWorker)
        {
            build.FrameBudget = budget;
            try
            {
                switch (build.Phase)
                {
                    case BuildPhase.Begin:
                        build.Phase = BuildPhase.None;
                        BuildPending(build);
                        break;
                    case BuildPhase.WaitingSolve:
                        // A finished solve reports through its callback, which
                        // moves the build on.
                        _solveBackend?.Poll(waitForWorker);
                        break;
                    case BuildPhase.Composing:
                        // Input can only change between frames, so checking here
                        // covers everything that arrived since the last piece.
                        if (RejectOutdatedWork())
                            build.Phase = BuildPhase.Rejected;
                        else
                            ContinueCompose(build);
                        break;
                }

                switch (build.Phase)
                {
                    case BuildPhase.WaitingSolve:
                        return FlowFieldBuildStep.Waiting;
                    case BuildPhase.Composing:
                        return FlowFieldBuildStep.Yield;
                    case BuildPhase.Publish:
                        return FlowFieldBuildStep.Completed;
                    default:
                        build.Phase = BuildPhase.None;
                        return FlowFieldBuildStep.Rejected;
                }
            }
            finally
            {
                build.FrameBudget = FlowFieldBuildBudget.Unlimited;
            }
        }

        internal bool PublishBuild(FlowFieldSurfaceBuild build)
        {
            build.Phase = BuildPhase.None;
            FlowFieldSurfaceCommit commit = build.Commit;
            return Commit(commit.Request, commit.ResultChanged, commit.IncludeBase, commit.Region);
        }

        /// <summary>Marks the build ready to publish with what Commit needs.</summary>
        private static void QueueCommit(
            FlowFieldSurfaceBuild build,
            in FlowFieldSurfaceRequest request,
            bool resultChanged,
            bool includeBase,
            FlowFieldCellRect finalRegion)
        {
            build.Commit = new FlowFieldSurfaceCommit(request, resultChanged, includeBase, finalRegion);
            build.Phase = BuildPhase.Publish;
        }
    }
}
