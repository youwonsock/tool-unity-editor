using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>The stages of one Surface2D rebuild, from a pending request to the published field.</summary>
    internal sealed partial class FlowFieldSession
    {
        private bool BuildPending()
        {
            if (!_hasPendingRequest)
                return true;

            FlowFieldSessionRequest request = _latestRequestedInput;
            _hasPendingRequest = false;
            FlowFieldDirtyFlags pending = _state.DirtyFlags;
            _state.DirtyFlags = FlowFieldDirtyFlags.None;

            try
            {
                bool surfaceChanged = PrepareSurface(request, pending);
                if (surfaceChanged)
                    _rebuild.PromoteSurface();
                else
                    _rebuild.DropDeferredSurface();
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

                BuildSceneBase(request, ref pending, surfaceChanged);
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
                    ComposeFinal(
                        request,
                        FlowFieldSurfaceRebuildTracker.IsBaseDirty(pending)
                            || (pending & FlowFieldDirtyFlags.DefaultDirection) != 0
                            || !_state.BaseComposed);
                }

                Commit(
                    request,
                    resultChanged: (pending & FlowFieldDirtyFlags.FinalRegion) != 0,
                    includeBase: FlowFieldSurfaceRebuildTracker.IsBaseDirty(pending) || !_fieldStore.IsValid,
                    finalRegion: _state.LastComposedRegion);
                return true;
            }
            catch (Exception exception)
            {
                EnterFault(exception);
                return false;
            }
        }

        private bool PrepareSurface(in FlowFieldSessionRequest request, FlowFieldDirtyFlags pending)
        {
            if ((pending & FlowFieldDirtyFlags.Grid) == 0
                && _state.Grid.IsValid
                && _state.SurfaceReady)
                return false;

            // Any Surface/grid rebuild invalidates an obstacle candidate that
            // was measured against the previous cell heights and bounds.
            // Leave the registered collider set intact; the downstream base
            // stage will issue one fresh dynamic query for the new Surface.
            _obstaclePipeline.DiscardStagedDynamicProbe();

            FlowFieldSurfaceData surface = ResolveSurface(request);
            if (!surface.IsValid)
                throw new InvalidOperationException("FlowField surface source returned invalid data.");

            bool boundsChanged = !_state.Grid.MatchesBounds(request.SurfaceSettings.Grid);
            FlowFieldSurfaceData previousSurface = _state.Surface;
            bool staticSnapshotChanged = request.SourceKind == FlowFieldSessionSourceKind.StaticSnapshot
                && !ReferenceEquals(_loadedStaticBakeSnapshot, request.StaticBakeSnapshot);
            bool surfaceChanged = boundsChanged
                || !_state.SurfaceReady
                || staticSnapshotChanged
                || !ReferenceEquals(previousSurface, surface)
                    && !previousSurface.ContentEquals(surface);

            // A full Surface notification may legitimately produce the same
            // immutable hit/normal arrays. Preserve the previous object in
            // that case so Goal/obstacle-only work does not trigger a
            // committed Surface copy or an unnecessary downstream BFS.
            if (!surfaceChanged)
                return false;

            _state.Grid = request.SurfaceSettings.Grid;
            _lastObservedGrid = _state.Grid;
            _hasObservedGrid = true;
            _state.Workspace.Resize(_state.Grid.CellCount);
            _state.BaseComposed = false;
            _state.LastComposedRegion = FlowFieldCellRect.Invalid;
            _state.Surface = surface;

            if (boundsChanged)
                _state.HasObstacleMask = false;
            _state.DirtyFlags |= FlowFieldDirtyFlags.StaticObstacles
                | FlowFieldDirtyFlags.DynamicObstacles
                | FlowFieldDirtyFlags.Escape
                | FlowFieldDirtyFlags.DefaultDirection
                | FlowFieldDirtyFlags.Goal
                | FlowFieldDirtyFlags.ModifierArea
                | FlowFieldDirtyFlags.FinalRegion;
            _state.DirtyFinalRegion = FlowFieldCellRect.Full(_state.Grid);
            _state.DirtyObstacleRegion = FlowFieldCellRect.Full(_state.Grid);
            _modifierRegistry?.MarkAllAreasDirty();
            return true;
        }

        private FlowFieldSurfaceData ResolveSurface(in FlowFieldSessionRequest request)
        {
            if (request.SourceKind != FlowFieldSessionSourceKind.StaticSnapshot)
            {
                return _surfaceSource.Build(request.SurfaceSettings)
                    ?? throw new InvalidOperationException("FlowField surface source returned no surface data.");
            }

            if (request.StaticBakeSnapshot == null || !request.StaticBakeSnapshot.HasValidData)
                throw new InvalidOperationException("Static Flow Bake Asset is missing or invalid.");
            bool surfaceMatches = request.StaticBakeSnapshot.MatchesSurface(
                request.SurfaceSettings,
                out string surfaceMismatch);
            bool obstaclesMatch = request.StaticBakeSnapshot.MatchesObstacles(
                request.ObstacleLayer,
                request.ObstacleCheckHeight,
                request.ObstacleCheckCenterOffset,
                request.ObstacleClearance,
                out string obstacleMismatch);
            if (!surfaceMatches || !obstaclesMatch)
            {
                throw new InvalidOperationException(
                    string.IsNullOrEmpty(surfaceMismatch) ? obstacleMismatch : surfaceMismatch);
            }
            return request.StaticBakeSnapshot.Surface;
        }

        private void BuildStaticSnapshot(in FlowFieldSessionRequest request, FlowFieldDirtyFlags pending)
        {
            if (request.StaticBakeSnapshot == null)
                throw new InvalidOperationException("Static Flow Bake Asset is missing or invalid.");

            bool reloadSnapshot = !_fieldStore.IsValid
                || !_state.SurfaceReady
                || !_state.Grid.MatchesBounds(request.SurfaceSettings.Grid)
                || !ReferenceEquals(_loadedStaticBakeSnapshot, request.StaticBakeSnapshot);
            if (reloadSnapshot)
            {
                if (!request.StaticBakeSnapshot.HasValidData)
                    throw new InvalidOperationException("Static Flow Bake Asset is missing or invalid.");
                // Static runtime never runs a Physics obstacle query or a BFS
                // backend. Loading the asset is the base-stage equivalent and
                // is only needed for the first build or when the referenced
                // snapshot/signature changed.
                request.StaticBakeSnapshot.CopyToWorkspace(_state.Grid, _state.Workspace);
                _loadedStaticBakeSnapshot = request.StaticBakeSnapshot;

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
                ComposeFinal(request, rebuildBase);

            Commit(
                request,
                resultChanged: finalDirty,
                includeBase: rebuildBase,
                finalRegion: _state.LastComposedRegion);
        }

        private void BuildSceneBase(
            in FlowFieldSessionRequest request,
            ref FlowFieldDirtyFlags pending,
            bool surfaceChanged)
        {
            if ((pending & FlowFieldDirtyFlags.DefaultDirection) != 0)
            {
                _state.ResolvedDefaultDirection = FlowFieldVectorUtility.NormalizeDefaultDirection(request.DefaultDirection);
                pending |= FlowFieldDirtyFlags.FinalRegion;
                _state.ExpandFinalDirty(FlowFieldCellRect.Full(_state.Grid));
            }

            bool rebuildStatic = (pending & FlowFieldDirtyFlags.StaticObstacles) != 0;
            bool rebuildDynamic = (pending & FlowFieldDirtyFlags.DynamicObstacles) != 0;
            bool rebuildGoal = (pending & FlowFieldDirtyFlags.Goal) != 0;
            if (!_state.HasObstacleMask)
            {
                // A direct Goal request is allowed before the first obstacle
                // preparation (notably in injected/test sessions). Ensure the
                // solver never consumes the zero-initialized mask.
                rebuildStatic = true;
                rebuildDynamic = true;
            }
            if (!rebuildStatic && !rebuildDynamic && !rebuildGoal)
                return;

            bool hadObstacleMask = _state.HasObstacleMask;
            FlowFieldGoalResolution goal = request.Goal;
            bool goalChanged = rebuildGoal && _goalTracker.HasChanged(goal);
            FlowFieldBuildResult prepared = FlowFieldSurfaceBuildPipeline.PrepareBase(
                new FlowFieldSurfaceBuildRequest(
                    new FlowFieldObstacleRequest(
                        _state.Grid,
                        _state.Surface,
                        _state.Workspace,
                        request.ObstacleLayer,
                        request.ObstacleCheckHeight,
                        request.ObstacleCheckCenterOffset,
                        request.ObstacleClearance,
                        request.UseUnregisteredObstacleSweep,
                        _state.DirtyObstacleRegion),
                    goal,
                    surfaceChanged),
                _obstaclePipeline,
                _goalTracker,
                rebuildStatic,
                rebuildDynamic,
                rebuildGoal);

            ApplyPreparedBase(prepared, rebuildStatic, rebuildDynamic, rebuildGoal,
                goalChanged, hadObstacleMask, ref pending);
        }

        private void ApplyPreparedBase(
            in FlowFieldBuildResult prepared,
            bool rebuildStatic,
            bool rebuildDynamic,
            bool rebuildGoal,
            bool goalChanged,
            bool hadObstacleMask,
            ref FlowFieldDirtyFlags pending)
        {
            _state.DirtyObstacleRegion = FlowFieldCellRect.Invalid;
            if (rebuildStatic || rebuildDynamic)
                _state.HasObstacleMask = true;
            if ((rebuildStatic || rebuildDynamic)
                && hadObstacleMask
                && !prepared.ObstacleMaskChanged)
            {
                // A probe that produces the same effective mask is not a
                // base change. Drop the request-only obstacle flags so no
                // BFS, Revision or FieldChanged event is generated.
                pending &= ~(FlowFieldDirtyFlags.StaticObstacles
                    | FlowFieldDirtyFlags.DynamicObstacles
                    | FlowFieldDirtyFlags.Escape);
                if ((pending & ~(FlowFieldDirtyFlags.StaticObstacles
                        | FlowFieldDirtyFlags.DynamicObstacles
                        | FlowFieldDirtyFlags.Escape
                        | FlowFieldDirtyFlags.FinalRegion)) == FlowFieldDirtyFlags.None)
                {
                    pending &= ~FlowFieldDirtyFlags.FinalRegion;
                    _state.DirtyFinalRegion = FlowFieldCellRect.Invalid;
                }
            }
            if (rebuildStatic || rebuildDynamic)
            {
                // The obstacle API deliberately does not advance either
                // version until this comparison has established that the
                // effective mask changed.  This keeps a registration,
                // transform notification or probe that produces the same
                // mask completely invisible to BFS and consumers.
                if (prepared.ObstacleMaskChanged)
                    _rebuild.PromoteObstacle();
                else
                    _rebuild.DropDeferredObstacle();
            }
            if (rebuildGoal)
            {
                if (goalChanged)
                    _rebuild.PromoteGoal();
                else
                    _rebuild.DropDeferredGoal();
            }
            if (prepared.ObstacleMaskChanged || !hadObstacleMask)
            {
                if (!prepared.ObstacleMaskChanged)
                    _obstaclePipeline.CommitCombinedAndBuildEscape(
                        _state.Grid,
                        _state.Surface,
                        _state.Workspace,
                        out _);
                _state.ExpandObstacleDirty(prepared.ObstacleDirtyRegion);
                UpdateBlockedWarning(prepared.HasWalkableSurface);
                pending |= FlowFieldDirtyFlags.Goal | FlowFieldDirtyFlags.FinalRegion;
                _state.ExpandFinalDirty(FlowFieldCellRect.Full(_state.Grid));
            }
            if (prepared.GoalStatus == FlowFieldGoalBuildStatus.NoWalkableSurface
                && _goalTracker.TryConsumeMissingWalkableWarning())
            {
                Debug.LogWarning("[FlowField] Goal 범위에 이동 가능한 표면 셀이 없습니다.");
            }
            if (rebuildGoal && prepared.GoalStatus == FlowFieldGoalBuildStatus.Unchanged)
            {
                // A repeated Goal request can be produced by an explicit
                // rebuild call even though the resolved source cell and
                // influence radius are identical. The prepared workspace is
                // already the committed Goal graph, so make this a true
                // no-op instead of launching another BFS.
                pending &= ~FlowFieldDirtyFlags.Goal;
                if ((pending & ~(FlowFieldDirtyFlags.Goal | FlowFieldDirtyFlags.FinalRegion))
                    == FlowFieldDirtyFlags.None)
                {
                    pending &= ~FlowFieldDirtyFlags.FinalRegion;
                    _state.DirtyFinalRegion = FlowFieldCellRect.Invalid;
                }
            }
            if ((rebuildGoal && prepared.GoalChanged)
                || prepared.ObstacleMaskChanged)
            {
                pending |= FlowFieldDirtyFlags.FinalRegion;
                _state.ExpandFinalDirty(FlowFieldCellRect.Full(_state.Grid));
            }
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

        private void ComposeFinal(in FlowFieldSessionRequest request, bool rebuildBase = false)
        {
            FlowFieldCellRect region = _state.DirtyFinalRegion.IsValid
                ? _state.DirtyFinalRegion
                : FlowFieldCellRect.Full(_state.Grid);
            using (Boundary.EnterUserCode())
            {
                _modifierPipeline.RebuildFinalField(
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
            _state.BaseComposed |= rebuildBase;
            _state.DirtyFinalRegion = FlowFieldCellRect.Invalid;
        }

        private bool StartBfs(in FlowFieldSessionRequest request)
        {
            // A GPU callback can arrive after this lifetime has ended.
            int generation = LifetimeGeneration;
            if (!_state.Workspace.HasActiveGoal)
            {
                ComposeFinal(request, rebuildBase: true);
                Commit(request, resultChanged: true, includeBase: true, finalRegion: FlowFieldCellRect.Full(_state.Grid));
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
                _state.Workspace.ResolvedGoalIndex,
                FlowFieldComputeSolver.ClampMaxWaves(_state.Grid.CellCount, request.MaxGpuWaves),
                () => IsCurrentBuild(generation));

            if (_backendPolicy == FlowFieldBfsBackendPolicy.RequireGpu
                && (_buildPipeline == null || !_buildPipeline.SupportsGpu))
                throw new PlatformNotSupportedException("FlowField GPU backend is required but unavailable.");

            _rebuild.BeginSolve();
            _activeRequest = request;
            SetLifecycle(FlowFieldSessionLifecycle.Building);
            bool accepted;
            if (_backendPolicy == FlowFieldBfsBackendPolicy.ManagedOnly
                || _managedBackendForced)
            {
                accepted = FlowFieldSurfaceBuildPipeline.BuildManaged(bfsRequest);
                if (accepted)
                    OnBfsCompleted();
                return accepted;
            }

            if (_buildPipeline == null)
                throw new InvalidOperationException("FlowField BFS backend is not initialized.");

            accepted = _buildPipeline.StartBfs(
                bfsRequest,
                _ => RunBfsCallback(generation, OnBfsCompleted),
                (_, exception) => RunBfsCallback(generation, () => OnBfsFailed(exception)),
                allowManagedFallback: _backendPolicy != FlowFieldBfsBackendPolicy.RequireGpu);
            // A synchronous completion has already left the Building state.
            if (!accepted && IsCurrentBuild(generation))
                throw new InvalidOperationException("FlowField BFS session could not be started.");
            return accepted;
        }

        // A missing shader passes no reason: Managed is then the configured path.
        private void ForceManagedBackend(string reason)
        {
            _managedBackendForced = true;
            if (reason == null || GpuFallbackReason != null)
                return;
            GpuFallbackReason = reason;
            Debug.LogWarning($"[FlowField] Surface2D GPU 계산을 끄고 Managed로 전환합니다: {reason}");
        }

        private bool IsCurrentBuild(int generation)
            => !Disposed
                && generation == LifetimeGeneration
                && _lifecycle == FlowFieldSessionLifecycle.Building;

        private void RunBfsCallback(int generation, Action callback)
        {
            if (!IsCurrentBuild(generation))
                return;
            using FlowFieldExecutionBoundary.OperationScope operation = Boundary.EnterOperation();
            try { callback(); }
            catch (Exception exception) { EnterFault(exception); }
        }

        /// <summary>
        /// Shared rejection policy for successful and failed Surface2D completions.
        /// Final-only inputs remain eligible for recomposition; changed Grid or
        /// Goal inputs restore staging and stay pending for the next owning
        /// update. Obstacle-only staleness does not reject a solve: rejecting
        /// would starve publication while an obstacle keeps moving, and sampling
        /// already re-checks the latest obstacle state against the published field.
        /// The same starvation limits how many Goal solves in a row are rejected.
        /// </summary>
        private bool RejectStaleBfsResult()
        {
            // Grid and Goal requests stay unversioned until their stage proves
            // a change, so the versions below can match while one is pending.
            bool gridStale = (_state.DirtyFlags & FlowFieldDirtyFlags.Grid) != 0;
            bool goalStale = (_state.DirtyFlags & FlowFieldDirtyFlags.Goal) != 0;
            bool rejectGoal = _rebuild.ShouldRejectSupersededGoal(goalStale && !gridStale && !_rebuild.IsBaseStale);
            if (_rebuild.IsBaseStale)
            {
                bool obstacleStale = HasStaleObstacleInput();
                RestoreStagingAfterStaleResult(preserveObstacle: !obstacleStale);
                if (obstacleStale)
                {
                    _state.HasObstacleMask = false;
                    _state.DirtyFlags |= FlowFieldDirtyFlags.StaticObstacles
                        | FlowFieldDirtyFlags.DynamicObstacles
                        | FlowFieldDirtyFlags.Escape;
                    _state.ExpandObstacleDirty(FlowFieldCellRect.Full(_state.Grid));
                }
                RequeueRejectedGoal();
                SetLifecycle(FlowFieldSessionLifecycle.Active);
                return true;
            }

            if (gridStale || rejectGoal)
            {
                RestoreStagingAfterStaleResult(preserveObstacle: true);
                RequeueRejectedGoal();
                SetLifecycle(FlowFieldSessionLifecycle.Active);
                return true;
            }

            return false;
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
            if (_hasLatestRequestedInput)
                _hasPendingRequest = true;
        }

        private void OnBfsCompleted()
        {
            if (_lifecycle != FlowFieldSessionLifecycle.Building)
                return;
            if (_buildPipeline is FlowFieldSurfaceBuildPipeline productionPipeline
                && productionPipeline.GpuDisabled)
                ForceManagedBackend(productionPipeline.GpuFallbackReason);
            if (RejectStaleBfsResult())
                return;

            bool hasNewRequest = _hasPendingRequest;
            FlowFieldSessionRequest finalRequest = hasNewRequest ? _latestRequestedInput : _activeRequest;
            FlowFieldDirtyFlags pending = _state.DirtyFlags;
            // Obstacle or Goal input observed during this solve keeps its request
            // and dirty flags through Commit; the next owner boundary handles it.
            _hasPendingRequest = FlowFieldSurfaceRebuildTracker.IsBaseDirty(_state.DirtyFlags);

            // RequestVersion also advances for Default Direction and Modifier
            // edits. Those changes are final-only and must never invalidate a
            // valid in-flight base solve (a Goal/obstacle result would be
            // lost if we restored the previous committed workspace here).
            // Apply the newest scalar input to the freshly solved base before
            // committing. If the façade has not submitted a newer request
            // yet, the active request is still a valid base result and the
            // next final-only request will recompose it without another BFS.
            Vector3 latestDefaultDirection = _hasLatestDefaultDirection
                ? _latestDefaultDirection
                : finalRequest.DefaultDirection;
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
            ComposeFinal(finalRequest, rebuildBase: true);
            Commit(finalRequest, resultChanged: true, includeBase: true, finalRegion: _state.LastComposedRegion);
            SetLifecycle(FlowFieldSessionLifecycle.Active);
        }

        private void OnBfsFailed(Exception exception)
        {
            if (_lifecycle != FlowFieldSessionLifecycle.Building)
                return;
            if (_backendPolicy == FlowFieldBfsBackendPolicy.PreferGpu)
                ForceManagedBackend(exception?.Message ?? "GPU 계산에 실패했습니다.");
            if (RejectStaleBfsResult())
                return;
            // A failed base solve cannot be replaced by a final-only compose:
            // unlike the successful callback there is no freshly solved Goal
            // graph to preserve. Requeue the Goal stage whenever the request
            // changed while the backend was running (including a pending
            // Default Direction/Modifier request), then retry the same base
            // inputs without exposing a partial result.
            if (_rebuild.IsRequestStale
                || _hasPendingRequest
                || _obstacleProbePending
                || FlowFieldSurfaceRebuildTracker.IsBaseDirty(_state.DirtyFlags))
            {
                RestoreStagingAfterStaleResult(preserveObstacle: true);
                RequeueRejectedGoal();
                SetLifecycle(FlowFieldSessionLifecycle.Active);
                return;
            }
            EnterFault(exception ?? new InvalidOperationException("Managed FlowField BFS failed."));
        }

        private bool HasStaleObstacleInput()
            => (_state.DirtyFlags & (FlowFieldDirtyFlags.Grid
                | FlowFieldDirtyFlags.StaticObstacles
                | FlowFieldDirtyFlags.DynamicObstacles
                | FlowFieldDirtyFlags.Escape)) != 0;

        private void RestoreStagingAfterStaleResult(bool preserveObstacle = false)
        {
            bool sameSignature = _fieldStore.IsValid
                && _state.Workspace.Capacity == _fieldStore.Capacity
                && _fieldStore.Grid.MatchesBounds(_state.Grid)
                && _fieldStore.Surface != null
                && _state.Surface != null
                && SurfacesEqual(_fieldStore.Surface, _state.Surface);
            if (sameSignature)
            {
                if (preserveObstacle)
                    _fieldStore.CopyGoalAndFieldToWorkspace(_state.Workspace);
                else
                    _fieldStore.CopyToWorkspace(_state.Workspace);
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
            _hasCommittedRequest = true;
            _configurationStale = false;
            if (!_hasPendingRequest)
            {
                _state.DirtyFlags = FlowFieldDirtyFlags.None;
                _state.DirtyFinalRegion = FlowFieldCellRect.Invalid;
                // A pending probe still needs the region it observed.
                if (!_obstacleProbePending)
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

        private void UpdateBlockedWarning(bool hasWalkableCell)
        {
            if (!hasWalkableCell && !_obstaclePipeline.AllBlockedWarningIssued)
            {
                _obstaclePipeline.AllBlockedWarningIssued = true;
                Debug.LogWarning("[FlowField] 모든 유효한 Surface 셀이 장애물로 막혔습니다.");
            }
            else if (hasWalkableCell)
            {
                _obstaclePipeline.AllBlockedWarningIssued = false;
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
