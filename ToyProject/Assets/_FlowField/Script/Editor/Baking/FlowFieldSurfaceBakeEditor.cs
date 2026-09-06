using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Common.FlowField.Editor
{
    /// <summary>
    /// Editor adapter for the shared build session.  It owns only queueing,
    /// Undo and AssetDatabase side effects; surface, obstacle and BFS logic is
    /// the same code used by runtime sessions.
    /// </summary>
    internal static class FlowFieldSurfaceBakeEditor
    {
        private static bool _isBaking;
        private static bool _asyncPending;
        private static bool _processingQueue;
        private static bool _shuttingDown;
        private static int _callbackGeneration;
        private static readonly Queue<int> _queue = new Queue<int>();
        private static Action _cancel;
        private static string _progressLabel = string.Empty;

        static FlowFieldSurfaceBakeEditor()
        {
            AssemblyReloadEvents.beforeAssemblyReload += InvalidateCallbacks;
            EditorApplication.quitting += InvalidateCallbacks;
        }

        public static bool IsBaking => _isBaking;
        public static string ProgressLabel => _progressLabel;

        /// <summary>
        /// Cancels the active bake at the next safe callback/readback
        /// boundary. The generation is invalidated before cleanup so a late
        /// GPU callback cannot write an Asset after the user cancels.
        /// </summary>
        public static void CancelBake()
        {
            if (!_isBaking && !_asyncPending)
                return;

            _shuttingDown = true;
            unchecked { _callbackGeneration++; }
            _cancel?.Invoke();
            _cancel = null;
            _queue.Clear();
            _asyncPending = false;
            _isBaking = false;
            _progressLabel = string.Empty;
            EditorUtility.ClearProgressBar();
            _shuttingDown = false;
        }

        private static void InvalidateCallbacks()
        {
            _shuttingDown = true;
            unchecked { _callbackGeneration++; }
            _cancel?.Invoke();
            _cancel = null;
            _queue.Clear();
            _asyncPending = false;
            _isBaking = false;
            _processingQueue = false;
            _progressLabel = string.Empty;
            EditorUtility.ClearProgressBar();
            _shuttingDown = false;
        }

        [MenuItem("Tools/FlowField/Bake All Managers In Open Scenes")]
        private static void BakeAllManagersInOpenScenes()
        {
            if (_isBaking)
                throw new InvalidOperationException("A FlowField bake is already in progress.");
            FlowFieldManager[] managers = Resources.FindObjectsOfTypeAll<FlowFieldManager>();
            for (int i = 0; i < managers.Length; i++)
            {
                FlowFieldManager manager = managers[i];
                if (manager != null && !EditorUtility.IsPersistent(manager)
                    && manager.gameObject.scene.IsValid()
                    && !string.IsNullOrEmpty(manager.gameObject.scene.path)
                    && manager.BakeMode == FlowFieldBakeMode.StaticBaked)
                    _queue.Enqueue(manager.GetInstanceID());
            }
            _isBaking = true;
            EditorApplication.delayCall += ProcessQueue;
        }

        public static void ScheduleBake(FlowFieldManager manager)
        {
            if (manager == null)
                throw new ArgumentNullException(nameof(manager));
            if (manager.BakeMode != FlowFieldBakeMode.StaticBaked)
            {
                Debug.Log("RuntimeDynamic does not persist a bake asset; Surface is rebuilt by the runtime session.", manager);
                return;
            }
            if (_isBaking)
            {
                // latest-wins for a repeated request of the same Manager
                int instanceId = manager.GetInstanceID();
                if (!_queue.Contains(instanceId))
                    _queue.Enqueue(instanceId);
                return;
            }
            _queue.Enqueue(manager.GetInstanceID());
            _isBaking = true;
            EditorApplication.delayCall += ProcessQueue;
        }

        private static void ProcessQueue()
        {
            if (_processingQueue || _asyncPending)
                return;
            _processingQueue = true;
            if (_queue.Count == 0)
            {
                _isBaking = false;
                _processingQueue = false;
                return;
            }

            int id = _queue.Dequeue();
            FlowFieldManager manager = EditorUtility.InstanceIDToObject(id) as FlowFieldManager;
            _progressLabel = manager == null
                ? "Resolving FlowField manager"
                : $"Baking {manager.name}";
            EditorUtility.DisplayProgressBar("FlowField Static Bake", _progressLabel, 0f);
            try
            {
                if (manager == null)
                    throw new InvalidOperationException("The scheduled FlowField manager no longer exists.");
                BakeAndAssign(manager);
            }
            catch (OperationCanceledException)
            {
                Debug.Log("FlowField Static Bake cancelled.");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, manager);
            }
            _processingQueue = false;
            if (!_asyncPending)
            {
                ScheduleNextOrStop();
            }
        }

        private static void ScheduleNextOrStop()
        {
            if (_shuttingDown)
                return;
            if (_queue.Count > 0)
            {
                _isBaking = true;
                EditorApplication.delayCall += ProcessQueue;
            }
            else
            {
                _isBaking = false;
                _progressLabel = string.Empty;
                EditorUtility.ClearProgressBar();
            }
        }

        public static void BakeAndAssign(FlowFieldManager manager)
        {
            if (manager == null)
                throw new ArgumentNullException(nameof(manager));
            if (Application.isPlaying)
                throw new InvalidOperationException("Static Flow Bake is unavailable while the Editor is in Play mode.");
            if (manager.BakeMode != FlowFieldBakeMode.StaticBaked)
                return;

            if (manager.SpaceMode == FlowFieldSpaceMode.Volume3D)
            {
                BakeVolumeAndAssign(manager);
                return;
            }

            FlowFieldSurfaceBakeSettings settings = manager.CreateSurfaceBakeSettings();
            int bakeInputGeneration = manager.CaptureBakeInputGeneration();
            LayerMask obstacleLayer = manager.ObstacleLayer;
            float obstacleCheckHeight = manager.ObstacleCheckHeight;
            float obstacleCheckCenterOffset = manager.ObstacleCheckCenterOffset;
            float obstacleClearance = manager.ObstacleClearance;
            bool hasConfiguredGoal = manager.HasConfiguredGoal;
            Vector3 configuredGoalWorld = manager.ConfiguredGoalWorld;
            float configuredGoalRadius = manager.ConfiguredGoalInfluenceRadius;
            FlowFieldSurfaceBakeJob bakeJob = new FlowFieldSurfaceBakeJob(
                settings,
                ReportSurfaceProgress);
            FlowFieldSession session = null;
            int generation = _callbackGeneration;
            bool cleaned = false;
            void Cleanup()
            {
                if (cleaned) return;
                cleaned = true;
                EditorApplication.update -= Pump;
                _cancel = null;
                _asyncPending = false;
                if (session != null)
                {
                    session.FieldCommitted -= Complete;
                    session.Failed -= Failed;
                    session.DisposePermanently();
                }
                bakeJob.Dispose();
                EditorUtility.ClearProgressBar();
                _progressLabel = string.Empty;
                if (!_processingQueue)
                    ScheduleNextOrStop();
            }

            void Failed(Exception exception)
            {
                try
                {
                    if (generation == _callbackGeneration)
                        Debug.LogError($"[{nameof(FlowFieldManager)}] Static Flow Bake failed: {exception?.Message}", manager);
                }
                finally { Cleanup(); }
            }

            void StartSession(FlowFieldSurfaceBakeResult result)
            {
                if (cleaned)
                    return;
                if (generation != _callbackGeneration)
                {
                    Cleanup();
                    return;
                }
                if (!IsCurrentInput(
                        manager,
                        settings,
                        obstacleLayer,
                        obstacleCheckHeight,
                        obstacleCheckCenterOffset,
                        obstacleClearance,
                        hasConfiguredGoal,
                        configuredGoalWorld,
                        configuredGoalRadius,
                        bakeInputGeneration))
                {
                    Debug.LogWarning("Static Flow Bake input changed; existing asset was preserved.", manager);
                    Cleanup();
                    return;
                }

                FlowFieldSurfaceData surface = FlowFieldSurfaceData.FromRuntime(settings, result, 1);
                session = new FlowFieldSession(new FlowFieldFixedSurfaceSource(surface));
                session.FieldCommitted += Complete;
                session.Failed += Failed;
                try
                {
                    session.Initialize(
                        FlowFieldBakeMode.RuntimeDynamic,
                        FlowFieldSessionSourceKind.SceneBuild,
                        FlowFieldBfsBackendPolicy.PreferGpu,
                        manager.FrontierComputeShader);

                    FlowFieldGoalResolution goalSnapshot = manager.ResolveConfiguredGoal(settings.Grid);
                    bool accepted = session.Submit(FlowFieldSessionRequest.ForSceneBuild(
                        settings,
                        obstacleLayer,
                        obstacleCheckHeight,
                        obstacleCheckCenterOffset,
                        obstacleClearance,
                        false,
                        goalSnapshot,
                        manager.DefaultFlowDirection,
                        FlowFieldDirtyFlags.All,
                        FlowFieldCellRect.Full(settings.Grid),
                        FlowFieldCellRect.Full(settings.Grid),
                        Mathf.Min(settings.Grid.CellCount, Mathf.Max(64, manager.MaxGpuWaves)),
                        $"{manager.name}_StaticBakeSurface"));
                    if (!accepted && !session.IsFaulted)
                        throw new InvalidOperationException("Static Flow Bake session could not be started.");
                    if (session.IsFaulted)
                        throw session.Fault ?? new InvalidOperationException("Static Flow Bake failed.");
                    session.Pump();
                }
                catch
                {
                    Cleanup();
                    throw;
                }
            }

            void Pump()
            {
                if (cleaned)
                    return;
                try
                {
                    if (generation != _callbackGeneration)
                    {
                        Cleanup();
                        return;
                    }

                    if (session != null)
                    {
                        if (!IsCurrentInput(
                                manager,
                                settings,
                                obstacleLayer,
                                obstacleCheckHeight,
                                obstacleCheckCenterOffset,
                                obstacleClearance,
                                hasConfiguredGoal,
                                configuredGoalWorld,
                                configuredGoalRadius,
                                bakeInputGeneration))
                        {
                            Debug.LogWarning("Static Flow Bake input changed; existing asset was preserved.", manager);
                            Cleanup();
                            return;
                        }
                        session.Pump();
                        if (session.IsFaulted)
                            Failed(session.Fault ?? new InvalidOperationException("Static Flow Bake failed."));
                        return;
                    }
                    if (!IsCurrentInput(
                            manager,
                            settings,
                            obstacleLayer,
                            obstacleCheckHeight,
                            obstacleCheckCenterOffset,
                            obstacleClearance,
                            hasConfiguredGoal,
                            configuredGoalWorld,
                            configuredGoalRadius,
                            bakeInputGeneration))
                    {
                        Debug.LogWarning("Static Flow Bake input changed; existing asset was preserved.", manager);
                        Cleanup();
                        return;
                    }

                    bakeJob.Step(2.0);
                    if (_shuttingDown)
                    {
                        Cleanup();
                        return;
                    }
                    _progressLabel = $"Baking Surface {bakeJob.Progress:P0}";
                    if (EditorUtility.DisplayCancelableProgressBar(
                            "FlowField Static Bake",
                            _progressLabel,
                            bakeJob.Progress))
                    {
                        bakeJob.Cancel();
                    }
                    if (!bakeJob.IsComplete)
                        return;
                    if (bakeJob.Status == FlowFieldSurfaceBakeJobStatus.Cancelled)
                    {
                        Debug.Log("Static Flow Bake cancelled.");
                        Cleanup();
                        return;
                    }
                    if (!bakeJob.IsValid)
                        throw new InvalidOperationException(bakeJob.Error);
                    StartSession(bakeJob.Result);
                }
                catch (OperationCanceledException)
                {
                    Debug.Log("Static Flow Bake cancelled.");
                    Cleanup();
                }
                catch (Exception exception)
                {
                    Failed(exception);
                }
            }

            void Complete(bool changed)
            {
                int undoGroup = -1;
                string assetPath = null;
                FlowFieldStaticBakeData asset = null;
                bool createdAsset = false;
                try
                {
                    if (generation != _callbackGeneration)
                        return;
                    if (!IsCurrentInput(
                            manager,
                            settings,
                            obstacleLayer,
                            obstacleCheckHeight,
                            obstacleCheckCenterOffset,
                            obstacleClearance,
                            hasConfiguredGoal,
                            configuredGoalWorld,
                            configuredGoalRadius,
                            bakeInputGeneration))
                    {
                        Debug.LogWarning("Static Flow Bake input changed; existing asset was preserved.", manager);
                        return;
                    }
                    FlowFieldWorkspace workspace = session.CommittedWorkspace;
                    if (workspace == null || session.CommittedSurface == null)
                        throw new InvalidOperationException("Static Flow Bake produced no committed field.");

                    assetPath = FlowFieldBakeAssetUtility.ResolveStaticAssetPath(manager);
                    FlowFieldBakeAssetUtility.CreateBakeFolder();
                    undoGroup = Undo.GetCurrentGroup();
                    Undo.SetCurrentGroupName("Bake FlowField Static Snapshot");
                    asset = AssetDatabase.LoadAssetAtPath<FlowFieldStaticBakeData>(assetPath);
                    FlowFieldBakeAssetUtility.ValidateTargetOwnership(
                        manager.StaticBakeData,
                        asset,
                        AssetDatabase.LoadMainAssetAtPath(assetPath),
                        assetPath,
                        "Surface2D");
                    if (asset == null)
                    {
                        asset = ScriptableObject.CreateInstance<FlowFieldStaticBakeData>();
                        createdAsset = true;
                        asset.name = Path.GetFileNameWithoutExtension(assetPath);
                        AssetDatabase.CreateAsset(asset, assetPath);
                        Undo.RegisterCreatedObjectUndo(asset, "Create FlowField Static Snapshot");
                    }
                    Undo.RecordObject(asset, "Bake FlowField Static Snapshot");
                    bool hasGoal = workspace.HasActiveGoal && workspace.ResolvedGoalIndex >= 0;
                    FlowFieldGoalResolution goal = manager.ResolveConfiguredGoal(settings.Grid);
                    asset.Apply(settings, session.CommittedSurface, manager.ObstacleLayer,
                        manager.ObstacleCheckHeight, manager.ObstacleCheckCenterOffset,
                        manager.ObstacleClearance, hasGoal, goal.RequestedWorld,
                        goal.InfluenceRadius, hasGoal ? workspace.ResolvedGoalIndex : -1, workspace);
                    EditorUtility.SetDirty(asset);
                    Undo.RecordObject(manager, "Assign FlowField Static Snapshot");
                    manager.AssignStaticBakeData(asset);
                    EditorUtility.SetDirty(manager);
                    if (manager.gameObject.scene.IsValid())
                        EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);
                    AssetDatabase.SaveAssetIfDirty(asset);
                    Undo.CollapseUndoOperations(undoGroup);
                    Debug.Log($"[{nameof(FlowFieldManager)}] Static Flow Bake complete: {assetPath}", manager);
                }
                catch (Exception exception)
                {
                    if (undoGroup >= 0)
                    {
                        try { Undo.RevertAllDownToGroup(undoGroup); }
                        catch (Exception undoException) { Debug.LogException(undoException, manager); }
                    }
                    if (createdAsset && !string.IsNullOrEmpty(assetPath)
                        && AssetDatabase.LoadAssetAtPath<FlowFieldStaticBakeData>(assetPath) != null)
                    {
                        AssetDatabase.DeleteAsset(assetPath);
                    }
                    Debug.LogException(exception, manager);
                }
                finally { Cleanup(); }
            }

            _cancel = Cleanup;
            _asyncPending = true;
            EditorApplication.update += Pump;
            EditorUtility.DisplayProgressBar(
                "FlowField Static Bake",
                "Baking Surface",
                0f);
        }

        private static void BakeVolumeAndAssign(FlowFieldManager manager)
        {
            if (manager == null)
                throw new ArgumentNullException(nameof(manager));

            FlowFieldVolumeSession session = manager.CreateVolumeBakeSessionForEditor(
                out FlowFieldVolumeRequest request);
            int bakeInputGeneration = manager.CaptureBakeInputGeneration();
            int generation = _callbackGeneration;
            bool cleaned = false;

            void Cleanup()
            {
                if (cleaned)
                    return;
                cleaned = true;
                EditorApplication.update -= Pump;
                _cancel = null;
                _asyncPending = false;
                session.FieldCommitted -= Complete;
                session.Failed -= Failed;
                session.Dispose();
                EditorUtility.ClearProgressBar();
                _progressLabel = string.Empty;
                if (!_processingQueue)
                    ScheduleNextOrStop();
            }

            void Failed(Exception exception)
            {
                try
                {
                    if (generation == _callbackGeneration && !_shuttingDown)
                        Debug.LogError(
                            $"[{nameof(FlowFieldManager)}] Volume3D Static Flow Bake failed: {exception?.Message}",
                            manager);
                }
                finally
                {
                    Cleanup();
                }
            }

            void Complete(bool changed)
            {
                string assetPath = null;
                FlowFieldVolumeBakeData asset = null;
                bool created = false;
                int undoGroup = -1;
                try
                {
                    if (generation != _callbackGeneration)
                    {
                        Cleanup();
                        return;
                    }
                    if (!IsCurrentVolumeInput(manager, request, bakeInputGeneration))
                    {
                        Debug.LogWarning(
                            "Volume3D Static Flow Bake input changed; existing asset was preserved.",
                            manager);
                        return;
                    }

                    if (!session.TryExport(
                            out FlowFieldVolumeRequest committedRequest,
                            out bool[] blocked,
                            out uint[] topology,
                            out FlowFieldGoalFlags[] goalFlags,
                            out int[] next,
                            out Vector3[] directions,
                            out float[] speeds,
                            out Vector3[] escapeDirections,
                            out int resolvedGoalIndex))
                        throw new InvalidOperationException("Volume3D bake produced no committed field.");

                    assetPath = FlowFieldBakeAssetUtility.ResolveVolumeAssetPath(manager);
                    FlowFieldBakeAssetUtility.CreateBakeFolder();
                    undoGroup = Undo.GetCurrentGroup();
                    Undo.SetCurrentGroupName("Bake FlowField Volume Snapshot");
                    asset = AssetDatabase.LoadAssetAtPath<FlowFieldVolumeBakeData>(assetPath);
                    FlowFieldBakeAssetUtility.ValidateTargetOwnership(
                        manager.VolumeStaticBakeData,
                        asset,
                        AssetDatabase.LoadMainAssetAtPath(assetPath),
                        assetPath,
                        "Volume3D");
                    if (asset == null)
                    {
                        asset = ScriptableObject.CreateInstance<FlowFieldVolumeBakeData>();
                        asset.name = Path.GetFileNameWithoutExtension(assetPath);
                        AssetDatabase.CreateAsset(asset, assetPath);
                        Undo.RegisterCreatedObjectUndo(asset, "Create FlowField Volume Snapshot");
                        created = true;
                    }
                    Undo.RecordObject(asset, "Bake FlowField Volume Snapshot");
                    asset.Apply(
                        committedRequest.Grid,
                        committedRequest.WorldBounds,
                        manager.ObstacleLayer,
                        manager.ObstacleClearance,
                        committedRequest.HasGoal,
                        committedRequest.GoalWorld,
                        committedRequest.GoalInfluenceRadius,
                        resolvedGoalIndex,
                        blocked,
                        topology,
                        goalFlags,
                        next,
                        directions,
                        speeds,
                        escapeDirections);
                    EditorUtility.SetDirty(asset);
                    Undo.RecordObject(manager, "Assign FlowField Volume Snapshot");
                    manager.AssignVolumeBakeData(asset);
                    EditorUtility.SetDirty(manager);
                    if (manager.gameObject.scene.IsValid())
                        EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);
                    AssetDatabase.SaveAssetIfDirty(asset);
                    Undo.CollapseUndoOperations(undoGroup);
                    Debug.Log(
                        $"[{nameof(FlowFieldManager)}] Volume3D Static Bake complete: {assetPath}",
                        manager);
                }
                catch (Exception exception)
                {
                    if (undoGroup >= 0)
                    {
                        try { Undo.RevertAllDownToGroup(undoGroup); }
                        catch (Exception undoException) { Debug.LogException(undoException, manager); }
                    }
                    if (created && !string.IsNullOrEmpty(assetPath))
                        AssetDatabase.DeleteAsset(assetPath);
                    Debug.LogException(exception, manager);
                }
                finally
                {
                    Cleanup();
                }
            }

            void Pump()
            {
                if (cleaned)
                    return;
                try
                {
                    if (generation != _callbackGeneration)
                    {
                        Cleanup();
                        return;
                    }
                    if (!ReportVolumeProgress(session.BuildProgress))
                    {
                        Debug.Log("Volume3D Static Flow Bake cancelled.");
                        Cleanup();
                        return;
                    }
                    session.Pump(2.0);
                    if (session.IsFaulted)
                        Failed(new InvalidOperationException(
                            session.LastError ?? "Volume3D Static Flow Bake failed."));
                }
                catch (OperationCanceledException)
                {
                    Debug.Log("Volume3D Static Flow Bake cancelled.");
                    Cleanup();
                }
                catch (Exception exception)
                {
                    Failed(exception);
                }
            }

            session.FieldCommitted += Complete;
            session.Failed += Failed;
            _cancel = Cleanup;
            _asyncPending = true;
            EditorApplication.update += Pump;
        }

        private static bool ReportSurfaceProgress(int row, int rowCount)
        {
            if (_shuttingDown)
                return false;
            _progressLabel = $"Baking Surface row {row + 1}/{rowCount}";
            float progress = rowCount <= 0 ? 0f : (float)row / rowCount;
            return !EditorUtility.DisplayCancelableProgressBar(
                "FlowField Static Bake",
                _progressLabel,
                progress);
        }

        private static bool ReportVolumeProgress(float progress)
        {
            if (_shuttingDown)
                return false;
            progress = Mathf.Clamp01(progress);
            _progressLabel = $"Baking Volume3D {progress:P0}";
            return !EditorUtility.DisplayCancelableProgressBar(
                "FlowField Static Bake",
                _progressLabel,
                progress);
        }

        private static bool IsCurrentVolumeInput(
            FlowFieldManager manager,
            in FlowFieldVolumeRequest request,
            int bakeInputGeneration)
        {
            if (manager == null
                || manager.BakeMode != FlowFieldBakeMode.StaticBaked
                || manager.SpaceMode != FlowFieldSpaceMode.Volume3D)
                return false;
            try
            {
                // Capture changes observed by the editor even when a
                // serialized property was changed and then changed back
                // between two OnValidate callbacks. The generation is
                // monotonic, so A -> B -> A is still rejected.
                if (manager.CaptureBakeInputGeneration() != bakeInputGeneration
                    || request.BakeInputGeneration != bakeInputGeneration)
                    return false;
                if (!manager.TryGetVolumeLayout(out Bounds bounds, out FlowFieldGridSpace grid)
                    || !grid.MatchesBounds(request.Grid)
                    || !FlowFieldBakeBoundsUtility.Approximately(bounds, request.WorldBounds))
                    return false;
                return manager.ObstacleLayer.value == request.ObstacleLayer.value
                    && Mathf.Abs(manager.ObstacleClearance - request.ObstacleClearance) <= 0.0001f
                    && FlowFieldGridSpace.Approximately(
                        manager.DefaultFlowDirection,
                        request.DefaultDirection,
                        0.00000001d)
                    && manager.HasConfiguredGoal == request.HasGoal
                    && (!request.HasGoal
                        || FlowFieldGridSpace.Approximately(
                            manager.ConfiguredGoalWorld,
                            request.GoalWorld,
                            0.00000001d)
                            && Mathf.Abs(manager.ConfiguredGoalInfluenceRadius - request.GoalInfluenceRadius) <= 0.0001f);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsCurrentInput(
            FlowFieldManager manager,
            in FlowFieldSurfaceBakeSettings settings,
            LayerMask obstacleLayer,
            float obstacleCheckHeight,
            float obstacleCheckCenterOffset,
            float obstacleClearance,
            bool hasConfiguredGoal,
            Vector3 configuredGoalWorld,
            float configuredGoalRadius,
            int bakeInputGeneration)
        {
            if (manager == null || manager.BakeMode != FlowFieldBakeMode.StaticBaked)
                return false;
            try
            {
                if (manager.CaptureBakeInputGeneration() != bakeInputGeneration)
                    return false;
                FlowFieldSurfaceBakeSettings current = manager.CreateSurfaceBakeSettings();
                return current.IsValid && current.Grid.MatchesBounds(settings.Grid)
                    && FlowFieldBakeBoundsUtility.Approximately(current.BakeBounds, settings.BakeBounds)
                    && current.GroundLayer.value == settings.GroundLayer.value
                    && Mathf.Abs(current.MaxSurfaceSlope - settings.MaxSurfaceSlope) <= 0.0001f
                    && Mathf.Abs(current.MaxStepHeight - settings.MaxStepHeight) <= 0.0001f
                    && manager.ObstacleLayer.value == obstacleLayer.value
                    && Mathf.Abs(manager.ObstacleCheckHeight - obstacleCheckHeight) <= 0.0001f
                    && Mathf.Abs(manager.ObstacleCheckCenterOffset - obstacleCheckCenterOffset) <= 0.0001f
                    && Mathf.Abs(manager.ObstacleClearance - obstacleClearance) <= 0.0001f
                    && manager.HasConfiguredGoal == hasConfiguredGoal
                    && (!hasConfiguredGoal
                        || (manager.ConfiguredGoalWorld - configuredGoalWorld).sqrMagnitude <= 0.00000001f
                            && Mathf.Abs(manager.ConfiguredGoalInfluenceRadius - configuredGoalRadius) <= 0.0001f);
            }
            catch { return false; }
        }

        public static void ClearReference(FlowFieldManager manager)
        {
            if (manager != null && manager.SpaceMode == FlowFieldSpaceMode.Volume3D)
            {
                if (manager.VolumeStaticBakeData == null)
                    return;
                Undo.RecordObject(manager, "Clear FlowField Volume Bake Reference");
                manager.AssignVolumeBakeData(null);
                EditorUtility.SetDirty(manager);
                if (manager.gameObject.scene.IsValid())
                    EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);
                return;
            }
            if (manager == null || manager.StaticBakeData == null)
                return;
            Undo.RecordObject(manager, "Clear FlowField Static Bake Reference");
            manager.AssignStaticBakeData(null);
            EditorUtility.SetDirty(manager);
            if (manager.gameObject.scene.IsValid())
                EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);
        }
    }

    internal static class FlowFieldBakeAssetUtility
    {
        private const string BAKE_DIRECTORY = "Assets/_FlowField/Settings";

        internal static string ResolveStaticAssetPath(FlowFieldManager manager)
        {
            if (manager == null)
                throw new ArgumentNullException(nameof(manager));
            if (!manager.gameObject.scene.IsValid() || string.IsNullOrEmpty(manager.gameObject.scene.path))
                throw new InvalidOperationException("Scene을 먼저 저장해야 합니다.");
            if (manager.StaticBakeData != null)
            {
                string assignedPath = AssetDatabase.GetAssetPath(manager.StaticBakeData);
                if (!string.IsNullOrEmpty(assignedPath))
                    return assignedPath;
            }
            return ResolveStablePath(manager, "StaticBake");
        }

        internal static string ResolveVolumeAssetPath(FlowFieldManager manager)
        {
            if (manager == null)
                throw new ArgumentNullException(nameof(manager));
            if (!manager.gameObject.scene.IsValid() || string.IsNullOrEmpty(manager.gameObject.scene.path))
                throw new InvalidOperationException("Scene을 먼저 저장해야 합니다.");
            if (manager.VolumeStaticBakeData != null)
            {
                string assignedPath = AssetDatabase.GetAssetPath(manager.VolumeStaticBakeData);
                if (!string.IsNullOrEmpty(assignedPath))
                    return assignedPath;
            }
            return ResolveStablePath(manager, "VolumeBake");
        }

        private static string ResolveStablePath(FlowFieldManager manager, string suffix)
        {
            ValidateFileName(manager.name);
            GlobalObjectId id = GlobalObjectId.GetGlobalObjectIdSlow(manager);
            string managerId = id.targetObjectId != 0
                ? id.targetObjectId.ToString()
                : manager.GetInstanceID().ToString();
            string sceneGuid = AssetDatabase.AssetPathToGUID(manager.gameObject.scene.path);
            if (string.IsNullOrEmpty(sceneGuid))
                throw new InvalidOperationException("씬 GUID를 확인할 수 없습니다. 씬을 저장한 뒤 다시 시도하세요.");
            return $"{BAKE_DIRECTORY}/{sceneGuid}_{managerId}_{manager.name}_{suffix}.asset";
        }

        internal static void CreateBakeFolder()
        {
            string[] parts = BAKE_DIRECTORY.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next) && string.IsNullOrEmpty(AssetDatabase.CreateFolder(current, parts[i])))
                    throw new InvalidOperationException($"Unable to create bake folder '{next}'.");
                current = next;
            }
        }

        internal static void ValidateTargetOwnership(
            UnityEngine.Object assignedAsset,
            UnityEngine.Object typedAsset,
            UnityEngine.Object existingAsset,
            string assetPath,
            string modeName)
        {
            if (existingAsset != null && typedAsset == null)
                throw new InvalidOperationException(
                    $"{modeName} Bake target '{assetPath}' already exists and is not assigned to this Manager. "
                    + "Choose a new asset or explicitly assign the existing asset before baking.");
            if (typedAsset != null && assignedAsset != typedAsset)
                throw new InvalidOperationException(
                    $"{modeName} Bake target '{assetPath}' is shared by another reference. "
                    + "Create a separate Bake Asset before overwriting it.");
        }

        private static void ValidateFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "." || value == ".."
                || value.IndexOfAny(new[] { '/', '\\' }) >= 0
                || value.IndexOf("..", StringComparison.Ordinal) >= 0)
                throw new ArgumentException("FlowField manager name must be a simple asset file name.", nameof(value));
            char[] invalid = Path.GetInvalidFileNameChars();
            for (int i = 0; i < invalid.Length; i++)
                if (value.IndexOf(invalid[i]) >= 0)
                    throw new ArgumentException("FlowField manager name contains an invalid character.", nameof(value));
        }
    }
}
