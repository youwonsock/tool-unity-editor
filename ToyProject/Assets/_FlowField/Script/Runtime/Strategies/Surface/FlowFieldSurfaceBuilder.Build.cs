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
                pending &= ~FlowFieldSurfaceDirtyFlags.Grid;
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
                    && (pending & (FlowFieldSurfaceDirtyFlags.ModifierValue
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

            if ((pending & (FlowFieldSurfaceDirtyFlags.ModifierValue | FlowFieldSurfaceDirtyFlags.FinalRegion)) != 0)
            {
                ComposeThenCommit(
                    request,
                    rebuildBase: IsBaseDirty(pending)
                        || (pending & FlowFieldSurfaceDirtyFlags.DefaultDirection) != 0
                        || !_state.BaseComposed,
                    resultChanged: (pending & FlowFieldSurfaceDirtyFlags.FinalRegion) != 0,
                    includeBase: IsBaseDirty(pending) || !_fieldStore.IsValid);
                return;
            }

            // Nothing to compose here: the branch above takes every final-region
            // change, so the published cells stay as they are.
            QueueCommit(
                request,
                resultChanged: false,
                includeBase: IsBaseDirty(pending) || !_fieldStore.IsValid,
                finalRegion: _state.LastComposedRegion);
        }

        private void BuildStaticSnapshot(in FlowFieldSurfaceRequest request, FlowFieldSurfaceDirtyFlags pending)
        {
            if (request.BakeSnapshot == null)
                throw new InvalidOperationException("Static Flow Bake Asset is missing or invalid.");

            bool reloadSnapshot = !_fieldStore.IsValid
                || !_state.SurfaceReady
                || !_state.Grid.MatchesBounds(request.SurfaceSettings.Grid)
                || !ReferenceEquals(_baseStage.LoadedSnapshot, request.BakeSnapshot);
            if (reloadSnapshot)
            {
                if (!request.BakeSnapshot.HasValidData)
                    throw new InvalidOperationException("Static Flow Bake Asset is missing or invalid.");
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
                    | FlowFieldSurfaceDirtyFlags.ModifierValue
                    | FlowFieldSurfaceDirtyFlags.DefaultDirection)) != 0;
            if (finalDirty)
            {
                ComposeThenCommit(request, rebuildBase, resultChanged: true, includeBase: rebuildBase);
                return;
            }

            QueueCommit(
                request,
                resultChanged: false,
                includeBase: rebuildBase,
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
        private void ComposeThenCommit(
            in FlowFieldSurfaceRequest request,
            bool rebuildBase,
            bool resultChanged,
            bool includeBase)
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
            _composeStage.Begin(request, rebuildBase, resultChanged, includeBase);
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
                _composeStage.IncludeBase,
                _state.LastComposedRegion);
            _composeStage.Clear();
        }

        private void StartBfs(in FlowFieldSurfaceRequest request)
        {
            // A solve callback can arrive after this lifetime has ended.
            int generation = _session.LifetimeGeneration;
            if (!_state.Workspace.HasActiveGoal)
            {
                ComposeThenCommit(request, rebuildBase: true, resultChanged: true, includeBase: true);
                return;
            }

            FlowFieldSurfaceBfsRequest bfsRequest = new FlowFieldSurfaceBfsRequest(
                _state.Grid,
                _state.Surface,
                _state.Workspace,
                request.Goal.LocalX,
                request.Goal.LocalZ,
                request.Goal.InfluenceRadius,
                _state.Workspace.ResolvedGoalIndex);

            // Set first: a solve that completes synchronously reports through
            // the callback, which moves the phase on.
            _phase = BuildPhase.WaitingSolve;
            // Stop drops the backend; the next solve takes a new one.
            _solveStage.Prepare(_sourceKind);
            bool accepted = _solveStage.Start(bfsRequest, _ => RunBfsCallback(generation));
            // A synchronous completion has already moved the phase on.
            if (!accepted && IsCurrentBuild(generation))
                throw new InvalidOperationException("FlowField BFS session could not be started.");
        }

        // The solve this build waits for. Stop and a fault cancel the build and
        // advance the generation, so a late callback finds neither.
        private bool IsCurrentBuild(int generation)
            => !_session.Disposed
                && generation == _session.LifetimeGeneration
                && _phase == BuildPhase.WaitingSolve;

        // The backend calls back from StartBfs or Poll, both inside StepBuild:
        // a failure leaves through StepBuild and the session faults.
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

            // Default Direction is final-only: apply its newest value to the freshly
            // solved base rather than solving again.
            Vector3 latestDefaultDirection = _latestDefaultDirection ?? finalRequest.DefaultDirection;
            if (FlowFieldGridSpace.IsFinite(latestDefaultDirection)
                && latestDefaultDirection.sqrMagnitude
                    > FlowFieldVectorUtility.DirectionEpsilonSqr)
            {
                Vector3 resolvedDefault = FlowFieldVectorUtility.NormalizeDefaultDirection(
                    latestDefaultDirection);
                if ((_state.ResolvedDefaultDirection - resolvedDefault).sqrMagnitude
                    > 0.0001f * 0.0001f)
                    _state.ResolvedDefaultDirection = resolvedDefault;
            }
            if ((pending & FlowFieldSurfaceDirtyFlags.ModifierArea) != 0)
                RebuildModifierAreaData(finalRequest);
            ComposeThenCommit(finalRequest, rebuildBase: true, resultChanged: true, includeBase: true);
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
    }
}
