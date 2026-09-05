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
            if (manager.BakeMode != FlowFieldBakeMode.StaticBaked)
                return;

            FlowFieldSurfaceBakeSettings settings = manager.CreateSurfaceBakeSettings();
            LayerMask obstacleLayer = manager.ObstacleLayer;
            float obstacleCheckHeight = manager.ObstacleCheckHeight;
            float obstacleCheckCenterOffset = manager.ObstacleCheckCenterOffset;
            float obstacleClearance = manager.ObstacleClearance;
            bool hasConfiguredGoal = manager.HasConfiguredGoal;
            Vector3 configuredGoalWorld = manager.ConfiguredGoalWorld;
            float configuredGoalRadius = manager.ConfiguredGoalInfluenceRadius;
            FlowFieldSurfaceBakeResult result = FlowFieldSurfaceBaker.Bake(
                settings,
                ReportSurfaceProgress);
            FlowFieldSurfaceData surface = FlowFieldSurfaceData.FromRuntime(settings, result, 1);
            FlowFieldSession session = new FlowFieldSession(new FlowFieldFixedSurfaceSource(surface));
            int generation = _callbackGeneration;
            bool cleaned = false;
            void Cleanup()
            {
                if (cleaned) return;
                cleaned = true;
                _cancel = null;
                _asyncPending = false;
                session.FieldCommitted -= Complete;
                session.Failed -= Failed;
                session.DisposePermanently();
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
                            configuredGoalRadius))
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
                    AssetDatabase.SaveAssets();
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
                    AssetDatabase.SaveAssets();
                    Debug.LogException(exception, manager);
                }
                finally { Cleanup(); }
            }

            session.FieldCommitted += Complete;
            session.Failed += Failed;
            _cancel = Cleanup;
            _asyncPending = true;
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
            }
            catch
            {
                Cleanup();
                throw;
            }
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

        private static bool IsCurrentInput(
            FlowFieldManager manager,
            in FlowFieldSurfaceBakeSettings settings,
            LayerMask obstacleLayer,
            float obstacleCheckHeight,
            float obstacleCheckCenterOffset,
            float obstacleClearance,
            bool hasConfiguredGoal,
            Vector3 configuredGoalWorld,
            float configuredGoalRadius)
        {
            if (manager == null || manager.BakeMode != FlowFieldBakeMode.StaticBaked)
                return false;
            try
            {
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
            ValidateFileName(manager.name);
            GlobalObjectId id = GlobalObjectId.GetGlobalObjectIdSlow(manager);
            string suffix = id.targetObjectId != 0 ? id.targetObjectId.ToString() : manager.GetInstanceID().ToString();
            return $"{BAKE_DIRECTORY}/{manager.name}_{suffix}_StaticBake.asset";
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
