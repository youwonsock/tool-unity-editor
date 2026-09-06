#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Common.FlowField
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
                    Message = "Volume3D 표시 상태를 준비하는 중입니다.",
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

        internal static void PumpAll(double budgetMilliseconds = EditorBudgetMilliseconds)
        {
            if (Managers.Count == 0 || budgetMilliseconds <= 0d)
                return;

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
                    if (manager.ShowVolumeCells)
                    {
                        Gizmos.color = blocked
                            ? new Color(0.92f, 0.18f, 0.18f, 0.72f)
                            : new Color(0.12f, 0.86f, 0.9f, 0.48f);
                        Gizmos.DrawWireCube(center, Vector3.one * grid.CellSize);
                    }

                    if (!manager.ShowVolumeVectors)
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

            if (manager.SpaceMode != FlowFieldSpaceMode.Volume3D)
            {
                SetUnavailable(state, "Surface2D Manager입니다.");
                return false;
            }

            if (!manager.TryGetVolumeLayout(out Bounds worldBounds, out FlowFieldGridSpace grid)
                || !grid.IsValid)
            {
                SetUnavailable(state, "Volume Bounds 또는 Cell Size가 유효하지 않습니다.");
                return false;
            }

            if (Application.isPlaying)
                return PumpRuntime(manager, state);
            if (manager.BakeMode != FlowFieldBakeMode.StaticBaked)
            {
                SetUnavailable(state, "RuntimeDynamic은 실행 후 Volume3D 벡터를 표시합니다.",
                    FlowFieldVolumeVisualizationStatusCode.RuntimeOnly);
                return state.RefreshRequested;
            }

            FlowFieldVolumeBakeData asset = manager.VolumeStaticBakeData;
            if (asset == null)
            {
                SetUnavailable(state, "StaticBaked Volume3D에는 Bake Asset이 필요합니다.");
                return false;
            }

            string metadataReason;
            string goalReason;
            bool metadataMatches = asset.MatchesMetadata(
                grid,
                worldBounds,
                manager.ObstacleLayer,
                manager.ObstacleClearance,
                out metadataReason);
            bool goalMatches = asset.MatchesGoal(
                manager.HasConfiguredGoal,
                manager.ConfiguredGoalWorld,
                manager.ConfiguredGoalInfluenceRadius,
                out goalReason);
            if (!metadataMatches || !goalMatches)
            {
                SetUnavailable(
                    state,
                    string.IsNullOrEmpty(metadataReason) ? goalReason : metadataReason,
                    FlowFieldVolumeVisualizationStatusCode.Invalid);
                return state.RefreshRequested;
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
                state.Message = $"Volume3D Bake 구조 검증 중... {assetState.Validator.Progress:P0}";
                state.ValidationProgress = assetState.Validator.Progress;
                assetState.Validator.Step(CellValidationBudget, GraphValidationBudget);
                state.RefreshRequested = false;
                return true;
            }
            if (!assetState.ValidationComplete)
            {
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
                return state.RefreshRequested;
            }

            if (!asset.TryGetView(grid, out bool[] blocked, out Vector3[] directions, out float[] speeds))
            {
                SetUnavailable(state, "Static Volume3D Bake View를 읽을 수 없습니다.",
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
                asset.Revision,
                resultId,
                blocked,
                directions,
                speeds);
            SetView(state, manager, view, "베이크 기본 필드가 표시 중입니다.",
                FlowFieldVolumeVisualizationStatusCode.Valid);
            return state.RefreshRequested;
        }

        private static bool PumpRuntime(
            FlowFieldManager manager,
            ManagerState state)
        {
            bool hasRequestedSource = manager.TryGetVolumeVisualizationSource(
                out FlowFieldVolumeVisualizationSource requestedSource);
            bool hasView = manager.TryGetVolumeVisualizationView(out FlowFieldReadView view);
            if (hasView && hasRequestedSource
                && view.Source.HasSameDisplaySource(requestedSource))
            {
                SetView(
                    state,
                    manager,
                    view,
                    manager.IsRebuilding
                        ? "같은 소스의 새 결과를 계산 중이며 이전 게시 필드를 표시합니다."
                        : "런타임 최종 필드가 표시 중입니다.",
                    manager.IsRebuilding
                        ? FlowFieldVolumeVisualizationStatusCode.Rebuilding
                        : FlowFieldVolumeVisualizationStatusCode.Valid);
                return state.RefreshRequested;
            }

            state.HasSelection = false;
            state.Code = manager.IsRebuilding
                ? FlowFieldVolumeVisualizationStatusCode.Rebuilding
                : FlowFieldVolumeVisualizationStatusCode.Unavailable;
            state.Message = hasRequestedSource && hasView
                ? "새 Volume3D 격자를 준비 중이므로 이전 벡터를 숨겼습니다."
                : "게시된 Volume3D 결과가 없습니다.";
            state.ValidationProgress = 0f;
            return state.RefreshRequested;
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
                SetUnavailable(state, "Volume3D 표시 View의 배열 길이가 격자와 다릅니다.",
                    FlowFieldVolumeVisualizationStatusCode.Invalid);
                return;
            }

            state.Code = code;
            state.Message = message;
            state.ValidationProgress = 1f;
            int selectionHash = ComputeSelectionHash(manager, view.Grid);
            if (!state.HasSelection || state.SelectionHash != selectionHash)
            {
                int max = manager.VolumeGizmoMode == FlowFieldVolumeGizmoMode.Slice
                    ? FlowFieldVolumeDisplaySelection.DefaultSliceLimit
                    : FlowFieldVolumeDisplaySelection.DefaultFullVolumeLimit;
                state.HasSelection = FlowFieldVolumeDisplaySelection.TryCreate(
                    view.Grid,
                    manager.VolumeGizmoMode,
                    manager.VolumeGizmoSliceAxis,
                    manager.VolumeGizmoSlice,
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
                hash = hash * 31 + (int)manager.VolumeGizmoMode;
                hash = hash * 31 + (int)manager.VolumeGizmoSliceAxis;
                hash = hash * 31 + manager.VolumeGizmoSlice;
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
                ? "Volume3D 필드를 표시할 수 없습니다."
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
            {
                return manager.TryGetVolumeVisualizationView(out view)
                    && view.IsValid;
            }

            if (state.Code != FlowFieldVolumeVisualizationStatusCode.Valid)
                return false;
            FlowFieldVolumeBakeData asset = manager.VolumeStaticBakeData;
            if (asset == null
                || !manager.TryGetVolumeLayout(
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
                asset.Revision,
                resultId,
                blocked,
                directions,
                speeds);
            return view.IsValid;
        }

        private static void RemoveUnusedAssets()
        {
            if (Assets.Count == 0)
                return;
            var used = new HashSet<FlowFieldVolumeBakeData>();
            foreach (ManagerState state in Managers.Values)
            {
                if (state.Manager != null && state.Manager.VolumeStaticBakeData != null)
                    used.Add(state.Manager.VolumeStaticBakeData);
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
#endif
