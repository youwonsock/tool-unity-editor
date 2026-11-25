using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>The stages of one Surface2D rebuild, from a pending request to the published field.</summary>
    internal sealed partial class FlowFieldSurfaceBuilder
    {
        private void BuildPending()
        {
            if (!_requests.Pending)
                return;

            FlowFieldSurfaceRequest request = _requests.Latest;
            _requests.Pending = false;
            FlowFieldSurfaceDirtyFlags pending = _state.DirtyFlags;
            _state.DirtyFlags = FlowFieldSurfaceDirtyFlags.None;

            // A failure leaves through StepBuild and the session faults.
            bool surfaceChanged = _baseStage.PrepareSurface(request, pending, _modifierComposition);
            // Surface preparation may invalidate every downstream stage
            // (for example after a grid change). Include those flags in
            // this build rather than leaving them for a later no-op tick.
            if (surfaceChanged)
                pending |= _state.DirtyFlags;
            else
                pending &= ~(FlowFieldSurfaceDirtyFlags.Grid | FlowFieldSurfaceDirtyFlags.Ground);
            _state.DirtyFlags = FlowFieldSurfaceDirtyFlags.None;
            if (request.SourceKind == FlowFieldSurfaceSourceKind.StaticSnapshot)
            {
                BuildStaticSnapshot(request, pending);
                return;
            }

            // The sweep decides what the masks hold, so starting or stopping
            // it rebuilds every obstacle.
            bool sweepsObstacles = _session.SweepsObstacles;
            if (sweepsObstacles != _masksSweep)
            {
                _masksSweep = sweepsObstacles;
                pending |= FlowFieldSurfaceDirtyFlags.StaticObstacles
                    | FlowFieldSurfaceDirtyFlags.DynamicObstacles
                    | FlowFieldSurfaceDirtyFlags.Escape
                    | FlowFieldSurfaceDirtyFlags.FinalRegion;
                _state.ExpandObstacleDirty(FlowFieldCellRect.Full(_state.Grid));
            }
            _baseStage.BuildSceneBase(request, ref pending, surfaceChanged, sweepsObstacles);
            if ((pending & FlowFieldSurfaceDirtyFlags.ModifierArea) != 0)
            {
                FlowFieldCellRect modifierRegion = RebuildModifierAreaData(request);
                bool modifierAreaChanged = modifierRegion.IsValid;
                if (modifierAreaChanged)
                {
                    pending |= FlowFieldSurfaceDirtyFlags.FinalRegion;
                    _state.ExpandFinalDirty(modifierRegion);
                }
                if (!modifierAreaChanged
                    && _state.BaseComposed && _fieldStore.IsValid
                    && !IsBaseDirty(pending)
                    && (pending & (FlowFieldSurfaceDirtyFlags.ModifierStack
                        | FlowFieldSurfaceDirtyFlags.DefaultDirection)) == 0)
                {
                    pending &= ~(FlowFieldSurfaceDirtyFlags.ModifierArea | FlowFieldSurfaceDirtyFlags.FinalRegion);
                    _state.DirtyFinalRegion = FlowFieldCellRect.Invalid;
                }
            }
            bool topologyChanged = IsBaseDirty(pending);
            // A pending BFS recomposes the whole field when it completes
            // (OnBfsCompleted), and a rejected solve restores the published
            // field, so composing before the solve would never be visible.
            if (_state.Workspace.HasActiveGoal && topologyChanged)
            {
                StartBfs(request);
                return;
            }

            if ((pending & (FlowFieldSurfaceDirtyFlags.ModifierStack | FlowFieldSurfaceDirtyFlags.FinalRegion)) != 0)
            {
                ComposeThenCommit(
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
                request,
                resultChanged: false,
                includeBase: false,
                finalRegion: _state.LastComposedRegion);
        }

        private void BuildStaticSnapshot(in FlowFieldSurfaceRequest request, FlowFieldSurfaceDirtyFlags pending)
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
                _state.ExpandFinalDirty(FlowFieldCellRect.Full(_state.Grid));
            }

            if ((pending & FlowFieldSurfaceDirtyFlags.ModifierArea) != 0 || reloadSnapshot)
            {
                FlowFieldCellRect modifierRegion = RebuildModifierAreaData(request);
                if (modifierRegion.IsValid)
                {
                    pending |= FlowFieldSurfaceDirtyFlags.FinalRegion;
                    _state.ExpandFinalDirty(modifierRegion);
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
                ComposeThenCommit(request, rebuildBase, resultChanged: true);
                return;
            }

            QueueCommit(
                request,
                resultChanged: false,
                includeBase: false,
                finalRegion: _state.LastComposedRegion);
        }

        private FlowFieldCellRect RebuildModifierAreaData(in FlowFieldSurfaceRequest request)
        {
            using FlowFieldExecutionBoundary.OperationScope userCode = _session.Boundary.EnterUserCode();
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
            in FlowFieldSurfaceRequest request,
            bool rebuildBase,
            bool resultChanged)
        {
            FlowFieldCellRect region = _state.DirtyFinalRegion.IsValid
                ? _state.DirtyFinalRegion
                : FlowFieldCellRect.Full(_state.Grid);
            using (_session.Boundary.EnterUserCode())
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
            _state.DirtyFinalRegion = FlowFieldCellRect.Invalid;
            _composeStage.Begin(request, rebuildBase, resultChanged);
            _phase = BuildPhase.Composing;
            ContinueCompose();
        }

        private void ContinueCompose()
        {
            bool finished;
            using (_session.Boundary.EnterUserCode())
                finished = _modifierComposition.StepFinalField(in _composeStage.FrameBudget);
            // StepBuild yields until the composition finishes.
            if (!finished)
                return;

            _state.BaseComposed |= _composeStage.RebuildBase;
            QueueCommit(
                _composeStage.Request,
                _composeStage.ResultChanged,
                _composeStage.RebuildBase,
                _state.LastComposedRegion);
            _composeStage.Clear();
        }

        // Only a build with an active Goal solves; the others compose directly.
        private void StartBfs(in FlowFieldSurfaceRequest request)
        {
            // A solve callback can arrive after this lifetime has ended.
            int generation = _session.LifetimeGeneration;
            FlowFieldSurfaceBfsRequest bfsRequest = new FlowFieldSurfaceBfsRequest(
                _state.Grid,
                _state.Surface,
                _state.Workspace,
                request.Goal.LocalX,
                request.Goal.LocalZ,
                request.Goal.InfluenceRadius,
                _state.Workspace.ResolvedGoalIndex);

            // Set first: the callback that reports the solve moves the phase on.
            _phase = BuildPhase.WaitingSolve;
            PrepareSolveBackend();
            bool accepted = _solveBackend.StartBfs(bfsRequest, _ => RunBfsCallback(generation));
            // Only a disposed backend refuses a solve.
            if (!accepted && IsCurrentBuild(generation))
                throw new InvalidOperationException("FlowField BFS session could not be started.");
        }

        // The solve this build waits for. Stop and a fault cancel the build and
        // advance the generation, so a late callback finds neither.
        private bool IsCurrentBuild(int generation)
            => !_session.Disposed
                && generation == _session.LifetimeGeneration
                && _phase == BuildPhase.WaitingSolve;

        // The backend calls back from Poll, inside StepBuild: a failure leaves
        // through StepBuild and the session faults.
        private void RunBfsCallback(int generation)
        {
            if (IsCurrentBuild(generation))
                OnBfsCompleted();
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
            bool gridChanged = (_state.DirtyFlags & FlowFieldSurfaceDirtyFlags.Grid) != 0;
            bool goalRemoved = _state.GoalRemoved && _state.Workspace.HasActiveGoal;
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
            _state.DirtyFlags |= FlowFieldSurfaceDirtyFlags.Goal | FlowFieldSurfaceDirtyFlags.FinalRegion;
            _state.ExpandFinalDirty(FlowFieldCellRect.Full(_state.Grid));
            if (_requests.HasLatest)
                _requests.Pending = true;
        }

        private void OnBfsCompleted()
        {
            if (RejectOutdatedWork())
            {
                _phase = BuildPhase.Rejected;
                return;
            }

            FlowFieldSurfaceRequest finalRequest = _requests.Latest;
            FlowFieldSurfaceDirtyFlags pending = _state.DirtyFlags;
            // Obstacle or Goal input observed during this solve keeps its request
            // and dirty flags through Commit; the next owner boundary handles it.
            _requests.Pending = IsBaseDirty(_state.DirtyFlags);

            // Default Direction is final-only: apply its newest value, owed
            // settings included, to the freshly solved base rather than solving
            // again. The owed Submit then finds it composed.
            if (FlowFieldVectorUtility.TryNormalizeDefaultDirection(
                    _configOwed ? _owedConfig.Flow.DefaultDirection : finalRequest.DefaultDirection,
                    out Vector3 resolvedDefault)
                && !FlowFieldVectorUtility.SameDefaultDirection(_state.ResolvedDefaultDirection, resolvedDefault))
                _state.ResolvedDefaultDirection = resolvedDefault;
            if ((pending & FlowFieldSurfaceDirtyFlags.ModifierArea) != 0)
                RebuildModifierAreaData(finalRequest);
            ComposeThenCommit(finalRequest, rebuildBase: true, resultChanged: true);
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
                _state.DirtyFlags |= FlowFieldSurfaceDirtyFlags.All;
                _state.DirtyFinalRegion = FlowFieldCellRect.Full(_state.Grid);
                _state.DirtyObstacleRegion = FlowFieldCellRect.Full(_state.Grid);
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
            _committedRequest = request;
            if (!_requests.Pending)
            {
                _state.DirtyFlags = FlowFieldSurfaceDirtyFlags.None;
                _state.DirtyFinalRegion = FlowFieldCellRect.Invalid;
                _state.DirtyObstacleRegion = FlowFieldCellRect.Invalid;
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
    

        // A solve is running on a worker thread.
        public bool IsWaitingForExternalCompletion => _phase == BuildPhase.WaitingSolve;

        public bool HasActiveBuild => _phase != BuildPhase.None;

        // The base stages run in one frame so their Physics reads form one
        // snapshot, so one build begins per pump whatever the budget says.
        public FlowFieldBuildPolicy BuildPolicy => new FlowFieldBuildPolicy(beginsAfterBudget: true, maxBuildsBegunPerPump: 1);

        public bool HasOwedInput => _requests.Pending || _configOwed;

        public bool TryBeginBuild(out FlowFieldBuildStart start)
        {
            Bounds? invalidated = null;
            if (_configOwed)
            {
                _configOwed = false;
                invalidated = Submit(CreateCurrentRequest(in _owedConfig));
            }
            // A build with a new grid or obstacle mask queries every obstacle.
            start = new FlowFieldBuildStart((_state.DirtyFlags & ObstacleQueryMask) != 0, invalidated);
            if (!_requests.Pending)
                return false;
            _phase = BuildPhase.Begin;
            return true;
        }

        public FlowFieldBuildStep StepBuild(in FlowFieldBuildBudget budget, bool waitForWorker)
        {
            _composeStage.FrameBudget = budget;
            try
            {
                switch (_phase)
                {
                    case BuildPhase.Begin:
                        _phase = BuildPhase.None;
                        BuildPending();
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
                        {
                            _composeStage.Clear();
                            _phase = BuildPhase.Rejected;
                        }
                        else
                        {
                            ContinueCompose();
                        }
                        break;
                }

                switch (_phase)
                {
                    case BuildPhase.WaitingSolve:
                        return FlowFieldBuildStep.Waiting;
                    case BuildPhase.Composing:
                        return FlowFieldBuildStep.Yield;
                    case BuildPhase.Publish:
                        return FlowFieldBuildStep.Completed;
                    default:
                        _phase = BuildPhase.None;
                        return FlowFieldBuildStep.Rejected;
                }
            }
            finally
            {
                _composeStage.FrameBudget = FlowFieldBuildBudget.Unlimited;
            }
        }

        public bool PublishBuild()
        {
            _phase = BuildPhase.None;
            return Commit(_commitRequest, _commitResultChanged, _commitIncludeBase, _commitRegion);
        }

        /// <summary>Marks the build ready to publish with what Commit needs.</summary>
        private void QueueCommit(
            in FlowFieldSurfaceRequest request,
            bool resultChanged,
            bool includeBase,
            FlowFieldCellRect finalRegion)
        {
            _commitRequest = request;
            _commitResultChanged = resultChanged;
            _commitIncludeBase = includeBase;
            _commitRegion = finalRegion;
            _phase = BuildPhase.Publish;
        }
}
}
