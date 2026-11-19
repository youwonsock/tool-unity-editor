using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Common.FlowField.Editor
{
    internal enum FlowFieldVolumeVisualizationStatusCode
    {
        Unavailable = 0,
        RuntimeOnly = 1,
        Validating = 2,
        Valid = 3,
        Rebuilding = 4,
        Invalid = 5,
    }

    internal readonly struct FlowFieldVolumeVisualizationStatus
    {
        internal FlowFieldVolumeVisualizationStatusCode Code { get; }
        internal string Message { get; }
        internal bool CanDraw { get; }
        internal bool HasView { get; }
        internal FlowFieldReadView View { get; }
        internal FlowFieldVolumeDisplaySelection Selection { get; }
        internal int SelectedCellCount { get; }
        internal int MovingCellCount { get; }
        internal int StoppedCellCount { get; }
        internal bool IsSampled { get; }
        internal float ValidationProgress { get; }

        internal FlowFieldVolumeVisualizationStatus(
            FlowFieldVolumeVisualizationStatusCode code,
            string message,
            bool canDraw,
            bool hasView,
            FlowFieldReadView view,
            FlowFieldVolumeDisplaySelection selection,
            int selectedCellCount,
            int movingCellCount,
            int stoppedCellCount,
            bool isSampled,
            float validationProgress)
        {
            Code = code;
            Message = message;
            CanDraw = canDraw;
            HasView = hasView;
            View = view;
            Selection = selection;
            SelectedCellCount = selectedCellCount;
            MovingCellCount = movingCellCount;
            StoppedCellCount = stoppedCellCount;
            IsSampled = isSampled;
            ValidationProgress = validationProgress;
        }
    }

    /// <summary>
    /// Editor-only Volume3D display service.  It owns no navigation data and
    /// never starts Physics, BFS, or a bake.  Asset validation is shared by
    /// all managers that reference the same asset and advances from the
    /// editor update loop in bounded chunks.
    /// </summary>
    internal static class FlowFieldVolumeVisualizationEditor
    {
        private const double EditorBudgetMilliseconds = 2.0;
        private const int CellValidationBudget = 4096;
        private const int GraphValidationBudget = 64;

        private sealed class AssetState : IDisposable
        {
            internal FlowFieldVolumeBakeData Asset;
            internal int Revision;
            internal int ContentGeneration;
            internal int InvalidationGeneration;
            internal FlowFieldVolumeBakeValidator Validator;
            internal bool ValidationComplete;
            internal bool ValidationValid;
            internal string ValidationError;
            internal long ResultId;

            public void Dispose()
            {
                Validator?.Dispose();
                Validator = null;
                Asset = null;
                ValidationComplete = false;
                ValidationValid = false;
                ValidationError = string.Empty;
                ResultId = 0L;
            }
        }

        private sealed class ManagerState : IDisposable
        {
            internal FlowFieldManager Manager;
            internal bool RefreshRequested;
            internal FlowFieldVolumeVisualizationStatusCode Code;
            internal string Message;
            internal FlowFieldVolumeDisplaySelection Selection;
            internal bool HasSelection;
            internal int SelectionHash;
            internal long CountedResultId;
            internal int CountedSelectionHash;
            internal int SelectedCellCount;
            internal int MovingCellCount;
            internal int StoppedCellCount;
            internal float ValidationProgress;

            public void Dispose()
            {
                Manager = null;
                HasSelection = false;
                Message = string.Empty;
            }
        }

        private static readonly Dictionary<int, ManagerState> Managers
            = new Dictionary<int, ManagerState>(16);
        private static readonly Dictionary<FlowFieldVolumeBakeData, AssetState> Assets
            = new Dictionary<FlowFieldVolumeBakeData, AssetState>(8);
        private static int _contentGeneration;
        private static int _managerCursor;
        private static long _nextAssetResultId;

        internal static void Register(FlowFieldManager manager)
        {
            if (manager == null || !manager.isActiveAndEnabled)
                return;
            int id = manager.GetInstanceID();
            if (!Managers.ContainsKey(id))
            {
                Managers.Add(id, new ManagerState
                {
                    Manager = manager,
                    RefreshRequested = true,
                    Code = FlowFieldVolumeVisualizationStatusCode.Unavailable,
                    Message = "Preparing the Volume3D display.",
                    CountedResultId = long.MinValue,
                    CountedSelectionHash = int.MinValue,
                });
            }
            else
            {
                Managers[id].Manager = manager;
            }
        }

        internal static void Unregister(FlowFieldManager manager)
        {
            if (manager == null)
                return;
            int id = manager.GetInstanceID();
            if (!Managers.TryGetValue(id, out ManagerState state))
                return;
            state.Dispose();
            Managers.Remove(id);
            _managerCursor = Math.Min(_managerCursor, Managers.Count);
            RemoveUnusedAssets();
        }

        /// <summary>Drops the state of Managers that were destroyed or disabled.</summary>
        internal static void PruneInactive()
        {
            var stale = new List<FlowFieldManager>();
            var missing = new List<int>();
            foreach (KeyValuePair<int, ManagerState> entry in Managers)
            {
                if (entry.Value.Manager == null)
                    missing.Add(entry.Key);
                else if (!entry.Value.Manager.isActiveAndEnabled)
                    stale.Add(entry.Value.Manager);
            }
            foreach (int id in missing)
            {
                Managers[id].Dispose();
                Managers.Remove(id);
            }
            foreach (FlowFieldManager manager in stale)
                Unregister(manager);
            _managerCursor = Math.Min(_managerCursor, Managers.Count);
            if (missing.Count > 0)
                RemoveUnusedAssets();
        }

        internal static void RequestRefresh(FlowFieldManager manager)
        {
            if (manager == null || !manager.isActiveAndEnabled)
                return;
            Register(manager);
            Managers[manager.GetInstanceID()].RefreshRequested = true;
        }

        internal static bool TryGetStatus(
            FlowFieldManager manager,
            out FlowFieldVolumeVisualizationStatus status)
        {
            status = default;
            if (manager == null || !manager.isActiveAndEnabled)
                return false;
            Register(manager);
            ManagerState state = Managers[manager.GetInstanceID()];
            bool hasView = TryAcquireCurrentView(manager, state, out FlowFieldReadView view);
            status = new FlowFieldVolumeVisualizationStatus(
                state.Code,
                state.Message,
                hasView && state.Code != FlowFieldVolumeVisualizationStatusCode.Invalid,
                hasView,
                view,
                state.Selection,
                state.SelectedCellCount,
                state.MovingCellCount,
                state.StoppedCellCount,
                state.HasSelection,
                state.ValidationProgress);
            return true;
        }

        internal static bool PumpAll(double budgetMilliseconds = EditorBudgetMilliseconds)
        {
            if (Managers.Count == 0 || budgetMilliseconds <= 0d)
                return false;

            Stopwatch timer = Stopwatch.StartNew();
            bool changed = false;
            int withoutWork = 0;
            while (Managers.Count > 0 && timer.Elapsed.TotalMilliseconds < budgetMilliseconds)
            {
                if (_managerCursor >= Managers.Count)
                    _managerCursor = 0;
                ManagerState state = GetManagerAtCursor();
                _managerCursor = (_managerCursor + 1) % Math.Max(1, Managers.Count);
                if (state == null || state.Manager == null)
                {
                    withoutWork++;
                    if (withoutWork >= Math.Max(1, Managers.Count))
                        break;
                    continue;
                }

                bool didWork = PumpManager(state);
                changed |= didWork;
                if (didWork)
                    withoutWork = 0;
                else
                    withoutWork++;
                if (withoutWork >= Math.Max(1, Managers.Count))
                    break;
            }

            if (changed)
            {
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
                SceneView.RepaintAll();
            }
            return changed;
        }

        internal static void Draw(FlowFieldManager manager)
        {
            if (manager == null || manager.SpaceMode != FlowFieldSpaceMode.Volume3D)
                return;
            if (!TryGetStatus(manager, out FlowFieldVolumeVisualizationStatus status)
                || !status.CanDraw
                || !status.HasView
                || !status.IsSampled)
                return;

            Color previousColor = Gizmos.color;
            Matrix4x4 previousMatrix = Gizmos.matrix;
            Gizmos.matrix = Matrix4x4.identity;
            try
            {
                FlowFieldGridSpace grid = status.View.Grid;
                for (int ordinal = 0; ordinal < status.Selection.DisplayCount; ordinal++)
                {
                    if (!status.Selection.TryGetCoordinate(
                            grid,
                            ordinal,
                            out int x,
                            out int y,
                            out int z,
                            out int index)
                        || !status.View.TryGetCell(
                            index,
                            out bool blocked,
                            out Vector3 direction,
                            out float speed))
                        continue;

                    Vector3 center = grid.LocalToWorldCenter(x, y, z);
                    if (FlowFieldDisplay.Of(manager).ShowVolumeCells)
                    {
                        Gizmos.color = blocked
                            ? new Color(0.92f, 0.18f, 0.18f, 0.72f)
                            : new Color(0.12f, 0.86f, 0.9f, 0.48f);
                        Gizmos.DrawWireCube(center, Vector3.one * grid.CellSize);
                    }

                    if (!FlowFieldDisplay.Of(manager).ShowVolumeVectors)
                        continue;
                    if (speed <= 0f || direction.sqrMagnitude <= 0.00000001f)
                        DrawStopMarker(center, grid.CellSize);
                    else
                        DrawArrow(center, direction, grid.CellSize * 0.4f, grid.CellSize);
                }
            }
            finally
            {
                Gizmos.matrix = previousMatrix;
                Gizmos.color = previousColor;
            }
        }

        internal static bool TryGetBoundsValidity(
            FlowFieldManager manager,
            out bool valid)
        {
            valid = false;
            if (manager == null || manager.SpaceMode != FlowFieldSpaceMode.Volume3D)
                return false;
            if (!TryGetStatus(manager, out FlowFieldVolumeVisualizationStatus status))
                return false;
            valid = status.Code == FlowFieldVolumeVisualizationStatusCode.Valid
                || status.Code == FlowFieldVolumeVisualizationStatusCode.Rebuilding;
            return true;
        }

        internal static void DrawBounds(FlowFieldManager manager, Bounds worldBounds)
        {
            if (manager == null)
                return;
            TryGetBoundsValidity(manager, out bool valid);
            Color previousColor = Gizmos.color;
            Matrix4x4 previousMatrix = Gizmos.matrix;
            try
            {
                Gizmos.matrix = Matrix4x4.identity;
                FlowFieldGizmoDrawer.DrawBakeBounds(worldBounds, valid);
            }
            finally
            {
                Gizmos.matrix = previousMatrix;
                Gizmos.color = previousColor;
            }
        }

        internal static void InvalidateAll()
        {
            _contentGeneration++;
            foreach (AssetState assetState in Assets.Values)
            {
                assetState.Validator?.Cancel();
                assetState.Validator = null;
            }
            foreach (ManagerState managerState in Managers.Values)
                managerState.RefreshRequested = true;
        }

        internal static void InvalidateAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return;
            foreach (KeyValuePair<FlowFieldVolumeBakeData, AssetState> pair in Assets)
            {
                if (pair.Key == null || AssetDatabase.GetAssetPath(pair.Key) != path)
                    continue;
                pair.Value.Validator?.Cancel();
                pair.Value.Validator = null;
                pair.Value.InvalidationGeneration = _contentGeneration - 1;
            }
            foreach (ManagerState managerState in Managers.Values)
                managerState.RefreshRequested = true;
        }

        internal static void Clear()
        {
            foreach (ManagerState managerState in Managers.Values)
                managerState.Dispose();
            foreach (AssetState assetState in Assets.Values)
                assetState.Dispose();
            Managers.Clear();
            Assets.Clear();
            _managerCursor = 0;
        }

        private static bool PumpManager(ManagerState state)
        {
            FlowFieldManager manager = state.Manager;
            if (manager == null)
                return false;
            // Setting a status clears the request, so read it first.
            bool refreshRequested = state.RefreshRequested;

            if (manager.SpaceMode != FlowFieldSpaceMode.Volume3D)
            {
                SetUnavailable(state, "This is a Surface2D Manager.");
                return false;
            }

            if (!manager.TryGetLayout(out Bounds worldBounds, out FlowFieldGridSpace grid)
                || !grid.IsValid)
            {
                SetUnavailable(state, "Volume Bounds or Cell Size is invalid.");
                return false;
            }

            if (Application.isPlaying)
                return PumpRuntime(manager, state);
            // The settings as Init checks them; the asset follows in chunks below.
            FlowFieldManagerConfig settings = manager.CaptureConfig(FlowFieldBakeMode.RuntimeDynamic);
            if (!FlowFieldSpaceStrategy.For(FlowFieldSpaceMode.Volume3D)
                    .TryValidate(in settings, bakeModeLocked: false, validateStaticGoal: false, out string settingsReason))
            {
                SetUnavailable(state, settingsReason, FlowFieldVolumeVisualizationStatusCode.Invalid);
                return refreshRequested;
            }
            if (manager.BakeMode != FlowFieldBakeMode.StaticBaked)
            {
                SetUnavailable(state, "RuntimeDynamic shows Volume3D vectors once it runs.",
                    FlowFieldVolumeVisualizationStatusCode.RuntimeOnly);
                return refreshRequested;
            }

            FlowFieldVolumeBakeData asset = manager.CaptureConfig().BakeAssets.Volume;
            if (asset == null)
            {
                SetUnavailable(state, "StaticBaked Volume3D requires a Bake Asset.");
                return false;
            }

            FlowFieldManagerConfig config = manager.CaptureConfig();
            string metadataReason;
            string goalReason;
            bool metadataMatches = asset.MatchesMetadata(
                grid,
                worldBounds,
                config.Obstacles.Layer,
                config.Obstacles.Clearance,
                out metadataReason);
            bool goalMatches = asset.MatchesGoal(
                config.HasGoal,
                config.GoalWorld,
                config.Flow.GoalInfluenceRadius,
                out goalReason);
            if (!metadataMatches || !goalMatches)
            {
                SetUnavailable(
                    state,
                    string.IsNullOrEmpty(metadataReason) ? goalReason : metadataReason,
                    FlowFieldVolumeVisualizationStatusCode.Invalid);
                return refreshRequested;
            }

            AssetState assetState = GetAssetState(asset, grid);
            if (!assetState.ValidationComplete && assetState.Validator == null)
            {
                assetState.Validator = new FlowFieldVolumeBakeValidator(asset, grid);
                state.RefreshRequested = true;
            }
            if (!assetState.ValidationComplete && !assetState.Validator.IsComplete)
            {
                state.Code = FlowFieldVolumeVisualizationStatusCode.Validating;
                state.Message = $"Validating the Volume3D bake... {assetState.Validator.Progress:P0}";
                state.ValidationProgress = assetState.Validator.Progress;
                assetState.Validator.Step(CellValidationBudget, GraphValidationBudget);
                state.RefreshRequested = false;
                return true;
            }
            bool validationFinished = false;
            if (!assetState.ValidationComplete)
            {
                validationFinished = true;
                assetState.ValidationComplete = true;
                assetState.ValidationValid = assetState.Validator.IsValid;
                assetState.ValidationError = assetState.Validator.Error;
                assetState.Validator.Dispose();
                assetState.Validator = null;
            }
            if (!assetState.ValidationValid)
            {
                SetUnavailable(
                    state,
                    assetState.ValidationError,
                    FlowFieldVolumeVisualizationStatusCode.Invalid);
                state.ValidationProgress = 1f;
                // The Inspector still shows the progress until the next repaint.
                return validationFinished || refreshRequested;
            }

            if (!asset.TryGetView(grid, out bool[] blocked, out Vector3[] directions, out float[] speeds))
            {
                SetUnavailable(state, "Cannot read the Static Volume3D Bake view.",
                    FlowFieldVolumeVisualizationStatusCode.Invalid);
                return false;
            }

            if (assetState.ResultId == 0L)
                assetState.ResultId = Interlocked.Increment(ref _nextAssetResultId);
            long resultId = assetState.ResultId;
            FlowFieldReadView view = FlowFieldReadView.CreateStaticVolume(
                grid,
                worldBounds,
                asset,
                resultId,
                blocked,
                directions,
                speeds);
            SetView(state, manager, view, "Showing the baked base field.",
                FlowFieldVolumeVisualizationStatusCode.Valid);
            return validationFinished || refreshRequested;
        }

        private static bool PumpRuntime(
            FlowFieldManager manager,
            ManagerState state)
        {
            bool refreshRequested = state.RefreshRequested;
            FlowFieldVolumeVisualizationStatusCode previousCode = state.Code;
            bool hasRequestedSource = TryGetConfiguredSource(
                manager,
                out FlowFieldVolumeVisualizationSource requestedSource);
            bool hasView = TryGetRuntimeView(manager, out FlowFieldReadView view);
            if (hasView && hasRequestedSource
                && view.Source.HasSameDisplaySource(requestedSource))
            {
                SetView(
                    state,
                    manager,
                    view,
                    manager.IsRebuilding
                        ? "Computing a new result for the same source; showing the published field."
                        : "Showing the runtime final field.",
                    manager.IsRebuilding
                        ? FlowFieldVolumeVisualizationStatusCode.Rebuilding
                        : FlowFieldVolumeVisualizationStatusCode.Valid);
                return refreshRequested || state.Code != previousCode;
            }

            state.HasSelection = false;
            state.Code = manager.IsRebuilding
                ? FlowFieldVolumeVisualizationStatusCode.Rebuilding
                : FlowFieldVolumeVisualizationStatusCode.Unavailable;
            state.Message = hasRequestedSource && hasView
                ? "A new Volume3D grid is being prepared, so the previous vectors are hidden."
                : "No Volume3D result is published.";
            state.ValidationProgress = 0f;
            state.RefreshRequested = false;
            return refreshRequested || state.Code != previousCode;
        }

        private static void SetView(
            ManagerState state,
            FlowFieldManager manager,
            FlowFieldReadView view,
            string message,
            FlowFieldVolumeVisualizationStatusCode code)
        {
            if (!view.IsValid)
            {
                SetUnavailable(state, "The Volume3D view arrays do not match the grid.",
                    FlowFieldVolumeVisualizationStatusCode.Invalid);
                return;
            }

            state.Code = code;
            state.Message = message;
            state.ValidationProgress = 1f;
            int selectionHash = ComputeSelectionHash(manager, view.Grid);
            if (!state.HasSelection || state.SelectionHash != selectionHash)
            {
                int max = FlowFieldDisplay.Of(manager).VolumeGizmoMode == FlowFieldVolumeGizmoMode.Slice
                    ? FlowFieldVolumeDisplaySelection.DefaultSliceLimit
                    : FlowFieldVolumeDisplaySelection.DefaultFullVolumeLimit;
                state.HasSelection = FlowFieldVolumeDisplaySelection.TryCreate(
                    view.Grid,
                    FlowFieldDisplay.Of(manager).VolumeGizmoMode,
                    FlowFieldDisplay.Of(manager).VolumeGizmoSliceAxis,
                    FlowFieldDisplay.Of(manager).VolumeGizmoSlice,
                    max,
                    out state.Selection);
                state.SelectionHash = selectionHash;
                state.CountedResultId = long.MinValue;
            }
            if (state.HasSelection
                && (state.CountedResultId != view.ResultId
                    || state.CountedSelectionHash != state.SelectionHash))
            {
                CountSelectedCells(state, view);
                state.CountedResultId = view.ResultId;
                state.CountedSelectionHash = state.SelectionHash;
            }
            state.RefreshRequested = false;
        }

        private static void CountSelectedCells(
            ManagerState state,
            in FlowFieldReadView view)
        {
            state.SelectedCellCount = 0;
            state.MovingCellCount = 0;
            state.StoppedCellCount = 0;
            FlowFieldGridSpace grid = view.Grid;
            for (int ordinal = 0; ordinal < state.Selection.DisplayCount; ordinal++)
            {
                if (!state.Selection.TryGetCoordinate(
                        grid,
                        ordinal,
                        out _,
                        out _,
                        out _,
                        out int index)
                    || !view.TryGetCell(index, out _, out Vector3 direction, out float speed))
                    continue;
                state.SelectedCellCount++;
                if (speed > 0f && direction.sqrMagnitude > 0.00000001f)
                    state.MovingCellCount++;
                else
                    state.StoppedCellCount++;
            }
        }

        private static AssetState GetAssetState(
            FlowFieldVolumeBakeData asset,
            FlowFieldGridSpace grid)
        {
            if (!Assets.TryGetValue(asset, out AssetState state))
            {
                state = new AssetState();
                Assets.Add(asset, state);
            }
            int contentGeneration = asset.ContentGeneration;
            if (state.Asset != asset
                || state.Revision != asset.Revision
                || state.ContentGeneration != contentGeneration
                || state.InvalidationGeneration != _contentGeneration)
            {
                state.Validator?.Dispose();
                state.Asset = asset;
                state.Revision = asset.Revision;
                state.ContentGeneration = contentGeneration;
                state.InvalidationGeneration = _contentGeneration;
                state.Validator = null;
                state.ValidationComplete = false;
                state.ValidationValid = false;
                state.ValidationError = string.Empty;
                state.ResultId = 0L;
            }
            return state;
        }

        private static int ComputeSelectionHash(
            FlowFieldManager manager,
            FlowFieldGridSpace grid)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + grid.Width;
                hash = hash * 31 + grid.Height;
                hash = hash * 31 + grid.Depth;
                hash = hash * 31 + grid.CellSize.GetHashCode();
                hash = hash * 31 + (int)FlowFieldDisplay.Of(manager).VolumeGizmoMode;
                hash = hash * 31 + (int)FlowFieldDisplay.Of(manager).VolumeGizmoSliceAxis;
                hash = hash * 31 + FlowFieldDisplay.Of(manager).VolumeGizmoSlice;
                return hash;
            }
        }

        private static ManagerState GetManagerAtCursor()
        {
            int index = 0;
            foreach (ManagerState state in Managers.Values)
            {
                if (index++ == _managerCursor)
                    return state;
            }
            return null;
        }

        private static void SetUnavailable(
            ManagerState state,
            string message,
            FlowFieldVolumeVisualizationStatusCode code = FlowFieldVolumeVisualizationStatusCode.Unavailable)
        {
            state.Code = code;
            state.Message = string.IsNullOrEmpty(message)
                ? "The Volume3D field cannot be shown."
                : message;
            state.HasSelection = false;
            state.SelectedCellCount = 0;
            state.MovingCellCount = 0;
            state.StoppedCellCount = 0;
            state.CountedResultId = long.MinValue;
            state.CountedSelectionHash = int.MinValue;
            state.ValidationProgress = 0f;
            state.RefreshRequested = false;
        }

        private static bool TryAcquireCurrentView(
            FlowFieldManager manager,
            ManagerState state,
            out FlowFieldReadView view)
        {
            view = default;
            if (manager == null || manager.SpaceMode != FlowFieldSpaceMode.Volume3D)
                return false;

            if (Application.isPlaying)
                return TryGetRuntimeView(manager, out view) && view.IsValid;

            if (state.Code != FlowFieldVolumeVisualizationStatusCode.Valid)
                return false;
            FlowFieldVolumeBakeData asset = manager.CaptureConfig().BakeAssets.Volume;
            if (asset == null
                || !manager.TryGetLayout(
                    out Bounds worldBounds,
                    out FlowFieldGridSpace grid)
                || !asset.TryGetView(
                    grid,
                    out bool[] blocked,
                    out Vector3[] directions,
                    out float[] speeds))
                return false;

            long resultId = 0L;
            if (Assets.TryGetValue(asset, out AssetState assetState))
                resultId = assetState.ResultId;
            view = FlowFieldReadView.CreateStaticVolume(
                grid,
                worldBounds,
                asset,
                resultId,
                blocked,
                directions,
                speeds);
            return view.IsValid;
        }

        // The published field, valid for this repaint. Each publication raises
        // the Manager's Revision, which tells counted cells when to recount.
        private static bool TryGetRuntimeView(FlowFieldManager manager, out FlowFieldReadView view)
        {
            view = default;
            if (!manager.TryGetPublishedField(out FlowFieldField field)
                || !(field is FlowFieldField<FlowFieldVolumeRequest> volume))
                return false;
            view = FlowFieldReadView.FromVolumeField(volume, manager.Revision);
            return true;
        }

        // What the Manager is configured to show. The Manager submits its
        // settings every frame, so this matches the latest accepted request.
        private static bool TryGetConfiguredSource(
            FlowFieldManager manager,
            out FlowFieldVolumeVisualizationSource source)
        {
            source = default;
            if (!manager.TryGetLayout(out Bounds worldBounds, out FlowFieldGridSpace grid))
                return false;
            FlowFieldVolumeBakeData asset = manager.BakeMode == FlowFieldBakeMode.StaticBaked
                ? manager.CaptureConfig().BakeAssets.Volume
                : null;
            source = new FlowFieldVolumeVisualizationSource(
                grid,
                worldBounds,
                manager.BakeMode,
                asset,
                asset != null ? asset.Revision : -1,
                0L);
            return source.IsValid;
        }

        private static void RemoveUnusedAssets()
        {
            if (Assets.Count == 0)
                return;
            var used = new HashSet<FlowFieldVolumeBakeData>();
            foreach (ManagerState state in Managers.Values)
            {
                if (state.Manager != null && state.Manager.CaptureConfig().BakeAssets.Volume != null)
                    used.Add(state.Manager.CaptureConfig().BakeAssets.Volume);
            }
            var remove = new List<FlowFieldVolumeBakeData>();
            foreach (KeyValuePair<FlowFieldVolumeBakeData, AssetState> pair in Assets)
                if (pair.Key == null || !used.Contains(pair.Key))
                    remove.Add(pair.Key);
            foreach (FlowFieldVolumeBakeData asset in remove)
            {
                if (Assets.TryGetValue(asset, out AssetState state))
                    state.Dispose();
                Assets.Remove(asset);
            }
        }

        private static void DrawArrow(
            Vector3 center,
            Vector3 direction,
            float length,
            float cellSize)
        {
            Vector3 body = direction.normalized;
            if (body.sqrMagnitude <= 0.00000001f)
                return;
            float headLength = Mathf.Min(cellSize * 0.18f, length * 0.5f);
            float headWidth = Mathf.Min(cellSize * 0.1f, length * 0.25f);
            Vector3 tip = center + body * length;
            Vector3 basePoint = tip - body * headLength;
            Vector3 basis = Mathf.Abs(Vector3.Dot(body, Vector3.up)) > 0.98f
                ? Vector3.right
                : Vector3.Cross(body, Vector3.up).normalized;
            Vector3 otherBasis = Vector3.Cross(body, basis).normalized;
            Gizmos.color = new Color(0.15f, 1f, 0.3f, 0.95f);
            Gizmos.DrawLine(center, tip);
            Gizmos.DrawLine(tip, basePoint + basis * headWidth);
            Gizmos.DrawLine(tip, basePoint - basis * headWidth);
            Gizmos.DrawLine(tip, basePoint + otherBasis * headWidth);
            Gizmos.DrawLine(tip, basePoint - otherBasis * headWidth);
        }

        private static void DrawStopMarker(Vector3 center, float cellSize)
        {
            float radius = Mathf.Max(0.025f, cellSize * 0.08f);
            Gizmos.color = new Color(1f, 0.8f, 0.15f, 0.95f);
            Gizmos.DrawWireSphere(center, radius);
            float arm = radius * 1.5f;
            Gizmos.DrawLine(center - Vector3.right * arm, center + Vector3.right * arm);
            Gizmos.DrawLine(center - Vector3.up * arm, center + Vector3.up * arm);
            Gizmos.DrawLine(center - Vector3.forward * arm, center + Vector3.forward * arm);
        }
    }
}
