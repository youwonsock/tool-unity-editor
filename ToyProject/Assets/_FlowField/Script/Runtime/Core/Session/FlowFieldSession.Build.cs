using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>The stages of one Surface2D rebuild, from a pending request to the published field.</summary>
    internal sealed partial class FlowFieldSession
    {
        private bool BuildPending()
        {
            if (!_requests.Pending)
                return true;

            FlowFieldSessionRequest request = _requests.Latest;
            _requests.Pending = false;
            _submittedForScheduler = false;
            FlowFieldDirtyFlags pending = _state.DirtyFlags;
            _state.DirtyFlags = FlowFieldDirtyFlags.None;

            try
            {
                bool surfaceChanged = _baseStage.PrepareSurface(request, pending, _modifierPipeline?.Registry);
                // Surface preparation may invalidate every downstream stage
                // (for example after a grid change). Include those flags in
                // this build rather than leaving them for a later no-op tick.
                if (surfaceChanged)
                    pending |= _state.DirtyFlags;
                else
                    pending &= ~FlowFieldDirtyFlags.Grid;
                _state.DirtyFlags = FlowFieldDirtyFlags.None;
                if (request.SourceKind == FlowFieldSessionSourceKind.StaticSnapshot)
                {
                    BuildStaticSnapshot(request, pending);
                    return true;
                }

                _baseStage.BuildSceneBase(request, ref pending, surfaceChanged);
                if ((pending & FlowFieldDirtyFlags.ModifierArea) != 0)
                {
                    FlowFieldCellRect modifierRegion = RebuildModifierAreaData(request);
                    bool modifierAreaChanged = modifierRegion.IsValid;
                    if (modifierAreaChanged)
                    {
                        pending |= FlowFieldDirtyFlags.FinalRegion;
                        _state.ExpandFinalDirty(modifierRegion);
                    }
                    if (!modifierAreaChanged
                        && _state.BaseComposed && _fieldStore.IsValid
                        && (pending & (FlowFieldDirtyFlags.ModifierValue
                            | FlowFieldDirtyFlags.DefaultDirection
                            | FlowFieldDirtyFlags.Grid
                            | FlowFieldDirtyFlags.StaticObstacles
                            | FlowFieldDirtyFlags.DynamicObstacles
                            | FlowFieldDirtyFlags.Escape
                            | FlowFieldDirtyFlags.Goal)) == 0)
                    {
                        pending &= ~(FlowFieldDirtyFlags.ModifierArea | FlowFieldDirtyFlags.FinalRegion);
                        _state.DirtyFinalRegion = FlowFieldCellRect.Invalid;
                    }
                }
                bool topologyChanged = (pending & (FlowFieldDirtyFlags.Grid
                    | FlowFieldDirtyFlags.StaticObstacles
                    | FlowFieldDirtyFlags.DynamicObstacles
                    | FlowFieldDirtyFlags.Escape
                    | FlowFieldDirtyFlags.Goal)) != 0;
                // A pending BFS recomposes the whole field when it completes
                // (OnBfsCompleted), and a rejected solve restores the published
                // field, so composing before the solve would never be visible.
                if (_state.Workspace.HasActiveGoal && topologyChanged)
                    return StartBfs(request);

                if ((pending & (FlowFieldDirtyFlags.ModifierValue | FlowFieldDirtyFlags.FinalRegion)) != 0)
                {
                    ComposeThenCommit(
                        request,
                        rebuildBase: IsBaseDirty(pending)
                            || (pending & FlowFieldDirtyFlags.DefaultDirection) != 0
                            || !_state.BaseComposed,
                        resultChanged: (pending & FlowFieldDirtyFlags.FinalRegion) != 0,
                        includeBase: IsBaseDirty(pending) || !_fieldStore.IsValid);
                    return true;
                }

                Commit(
                    request,
                    resultChanged: (pending & FlowFieldDirtyFlags.FinalRegion) != 0,
                    includeBase: IsBaseDirty(pending) || !_fieldStore.IsValid,
                    finalRegion: _state.LastComposedRegion);
                return true;
            }
            catch (Exception exception)
            {
                EnterFault(exception);
                return false;
            }
        }

        private void BuildStaticSnapshot(in FlowFieldSessionRequest request, FlowFieldDirtyFlags pending)
        {
            if (request.StaticBakeSnapshot == null)
                throw new InvalidOperationException("Static Flow Bake Asset is missing or invalid.");

            bool reloadSnapshot = !_fieldStore.IsValid
                || !_state.SurfaceReady
                || !_state.Grid.MatchesBounds(request.SurfaceSettings.Grid)
                || !ReferenceEquals(_baseStage.LoadedSnapshot, request.StaticBakeSnapshot);
            if (reloadSnapshot)
            {
                if (!request.StaticBakeSnapshot.HasValidData)
                    throw new InvalidOperationException("Static Flow Bake Asset is missing or invalid.");
                // Static runtime never runs a Physics obstacle query or a BFS
                // backend. Loading the asset is the base-stage equivalent and
                // is only needed for the first build or when the referenced
                // snapshot/signature changed.
                request.StaticBakeSnapshot.CopyToWorkspace(_state.Grid, _state.Workspace);
                _baseStage.LoadedSnapshot = request.StaticBakeSnapshot;

                _state.HasObstacleMask = true;
                _state.BaseComposed = false;
                _state.ResolvedDefaultDirection =
                    FlowFieldVectorUtility.NormalizeDefaultDirection(request.DefaultDirection);
            }
            else if ((pending & FlowFieldDirtyFlags.DefaultDirection) != 0)
            {
                _state.ResolvedDefaultDirection =
                    FlowFieldVectorUtility.NormalizeDefaultDirection(request.DefaultDirection);
                _state.ExpandFinalDirty(FlowFieldCellRect.Full(_state.Grid));
            }

            if ((pending & FlowFieldDirtyFlags.ModifierArea) != 0 || reloadSnapshot)
            {
                FlowFieldCellRect modifierRegion = RebuildModifierAreaData(request);
                if (modifierRegion.IsValid)
                {
                    pending |= FlowFieldDirtyFlags.FinalRegion;
                    _state.ExpandFinalDirty(modifierRegion);
                }
            }

            // Default Direction is not serialized in a static snapshot. It
            // therefore changes the cached BaseDirection/Speed arrays even
            // though it never requires Physics or BFS; rebuild the base
            // vectors before applying the final modifier stack.
            bool rebuildBase = reloadSnapshot
                || !_state.BaseComposed
                || (pending & FlowFieldDirtyFlags.DefaultDirection) != 0;
            bool finalDirty = rebuildBase
                || (pending & (FlowFieldDirtyFlags.FinalRegion
                    | FlowFieldDirtyFlags.ModifierArea
                    | FlowFieldDirtyFlags.ModifierValue
                    | FlowFieldDirtyFlags.DefaultDirection)) != 0;
            if (finalDirty)
            {
                ComposeThenCommit(request, rebuildBase, resultChanged: true, includeBase: rebuildBase);
                return;
            }

            Commit(
                request,
                resultChanged: false,
                includeBase: rebuildBase,
                finalRegion: _state.LastComposedRegion);
        }

        private FlowFieldCellRect RebuildModifierAreaData(in FlowFieldSessionRequest request)
        {
            using FlowFieldExecutionBoundary.OperationScope userCode = Boundary.EnterUserCode();
            return _modifierPipeline.RebuildAreaData(
                new FlowFieldModifierBuildRequest(
                    _state.Grid,
                    _state.Surface,
                    _state.Workspace,
                    request.ObstacleCheckHeight,
                    request.ObstacleCheckCenterOffset));
        }

        // Snapshots are taken here. The composition itself can continue over the
        // next frames, while input arriving meanwhile waits for the next build.
        private void ComposeThenCommit(
            in FlowFieldSessionRequest request,
            bool rebuildBase,
            bool resultChanged,
            bool includeBase)
        {
            FlowFieldCellRect region = _state.DirtyFinalRegion.IsValid
                ? _state.DirtyFinalRegion
                : FlowFieldCellRect.Full(_state.Grid);
            using (Boundary.EnterUserCode())
            {
                _modifierPipeline.BeginFinalField(
                    new FlowFieldModifierBuildRequest(
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
            ContinueCompose();
        }

        private void ContinueCompose()
        {
            bool finished;
            using (Boundary.EnterUserCode())
                finished = _modifierPipeline.StepFinalField(in _composeStage.FrameBudget);
            if (!finished)
            {
                SetLifecycle(FlowFieldSessionLifecycle.Building);
                return;
            }

            _state.BaseComposed |= _composeStage.RebuildBase;
            Commit(
                _composeStage.Request,
                _composeStage.ResultChanged,
                _composeStage.IncludeBase,
                _state.LastComposedRegion);
            _composeStage.Clear();
            SetLifecycle(FlowFieldSessionLifecycle.Active);
        }

        private bool StartBfs(in FlowFieldSessionRequest request)
        {
            // A solve callback can arrive after this lifetime has ended.
            int generation = LifetimeGeneration;
            if (!_state.Workspace.HasActiveGoal)
            {
                ComposeThenCommit(request, rebuildBase: true, resultChanged: true, includeBase: true);
                return true;
            }

            FlowFieldBfsRequest bfsRequest = new FlowFieldBfsRequest(
                _state.Grid,
                _state.Surface,
                _state.Workspace,
                true,
                request.Goal.LocalX,
                request.Goal.LocalZ,
                request.Goal.InfluenceRadius,
                _state.Workspace.ResolvedGoalIndex);

            SetLifecycle(FlowFieldSessionLifecycle.Building);
            if (_solveStage.Policy == FlowFieldSolvePolicy.Inline)
            {
                FlowFieldSurfaceBuildPipeline.SolveNow(bfsRequest);
                OnBfsCompleted();
                return true;
            }

            bool accepted = _solveStage.Start(bfsRequest, _ => RunBfsCallback(generation));
            // A synchronous completion has already left the Building state.
            if (!accepted && IsCurrentBuild(generation))
                throw new InvalidOperationException("FlowField BFS session could not be started.");
            return accepted;
        }

        private bool IsCurrentBuild(int generation)
            => !Disposed
                && generation == LifetimeGeneration
                && Lifecycle == FlowFieldSessionLifecycle.Building;

        private void RunBfsCallback(int generation)
        {
            if (!IsCurrentBuild(generation))
                return;
            using FlowFieldExecutionBoundary.OperationScope operation = Boundary.EnterOperation();
            try { OnBfsCompleted(); }
            catch (Exception exception) { EnterFault(exception); }
        }

        /// <summary>
        /// A solve or composition is published even when newer input has arrived:
        /// a Goal or obstacle that keeps moving must not starve movers of a field.
        /// The exceptions are a replaced Grid or stale configuration, whose cells
        /// no longer match, and a removed Goal, which movers must stop following.
        /// A request without a Goal keeps GoalRemoved set and has nothing to stop.
        /// </summary>
        private bool RejectOutdatedWork()
        {
            bool gridChanged = (_state.DirtyFlags & FlowFieldDirtyFlags.Grid) != 0;
            bool goalRemoved = _state.GoalRemoved && _state.Workspace.HasActiveGoal;
            if (!gridChanged && !goalRemoved)
                return false;
            RestoreStagingAfterStaleResult();
            RequeueRejectedGoal();
            SetLifecycle(FlowFieldSessionLifecycle.Active);
            return true;
        }

        /// <summary>
        /// Restored staging holds the published Goal, while the tracker still
        /// records the rejected one. Forget it so the next Goal stage rebuilds.
        /// </summary>
        private void RequeueRejectedGoal()
        {
            _goalTracker.ForgetLastBuild();
            _state.DirtyFlags |= FlowFieldDirtyFlags.Goal | FlowFieldDirtyFlags.FinalRegion;
            _state.ExpandFinalDirty(FlowFieldCellRect.Full(_state.Grid));
            if (_requests.HasLatest)
                _requests.Pending = true;
        }

        private void OnBfsCompleted()
        {
            if (Lifecycle != FlowFieldSessionLifecycle.Building)
                return;
            if (RejectOutdatedWork())
                return;

            FlowFieldSessionRequest finalRequest = _requests.Latest;
            FlowFieldDirtyFlags pending = _state.DirtyFlags;
            // Obstacle or Goal input observed during this solve keeps its request
            // and dirty flags through Commit; the next owner boundary handles it.
            _requests.Pending = IsBaseDirty(_state.DirtyFlags);

            // Default Direction is final-only: apply its newest value to the freshly
            // solved base rather than solving again.
            Vector3 latestDefaultDirection = _latestDefaultDirection ?? finalRequest.DefaultDirection;
            if (FlowFieldGridSpace.IsFinite(latestDefaultDirection)
                && latestDefaultDirection.sqrMagnitude
                    > FlowFieldVectorUtility.DIRECTION_EPSILON_SQR)
            {
                Vector3 resolvedDefault = FlowFieldVectorUtility.NormalizeDefaultDirection(
                    latestDefaultDirection);
                if ((_state.ResolvedDefaultDirection - resolvedDefault).sqrMagnitude
                    > 0.0001f * 0.0001f)
                    _state.ResolvedDefaultDirection = resolvedDefault;
            }
            if ((pending & FlowFieldDirtyFlags.ModifierArea) != 0)
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
                _state.DirtyFlags |= FlowFieldDirtyFlags.All;
                _state.DirtyFinalRegion = FlowFieldCellRect.Full(_state.Grid);
                _state.DirtyObstacleRegion = FlowFieldCellRect.Full(_state.Grid);
            }
        }

        private void Commit(
            in FlowFieldSessionRequest request,
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
            _state.ConfigurationStale = false;
            if (!_requests.Pending)
            {
                _state.DirtyFlags = FlowFieldDirtyFlags.None;
                _state.DirtyFinalRegion = FlowFieldCellRect.Invalid;
                // A pending probe still needs the region it observed.
                if (!_state.ObstacleProbePending)
                    _state.DirtyObstacleRegion = FlowFieldCellRect.Invalid;
            }
            PublishRuntimeState();
            if (actualChanged)
            {
                unchecked
                {
                    Revision++;
                }
                NotifyFieldCommitted();
            }
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
