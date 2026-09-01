using System.IO;
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Common.FlowField.Editor
{
    internal static class FlowFieldSurfaceBakeEditor
    {
        private static bool _isBaking;
        private static bool _asyncSessionPending;
        private static int _callbackGeneration;

        static FlowFieldSurfaceBakeEditor()
        {
            AssemblyReloadEvents.beforeAssemblyReload += InvalidateCallbacks;
            EditorApplication.quitting += InvalidateCallbacks;
        }

        public static bool IsBaking => _isBaking;

        private static void InvalidateCallbacks()
        {
            unchecked
            {
                _callbackGeneration++;
            }

            _isBaking = false;
            _asyncSessionPending = false;
        }

        [MenuItem("Tools/FlowField/Bake All Managers In Open Scenes")]
        private static void BakeAllManagersInOpenScenes()
        {
            if (_isBaking)
                throw new System.InvalidOperationException("A FlowField bake is already in progress.");

            _isBaking = true;
            try
            {
                FlowFieldManager[] managers = Resources.FindObjectsOfTypeAll<FlowFieldManager>();
                int bakedCount = 0;
                for (int i = 0; i < managers.Length; i++)
                {
                    FlowFieldManager manager = managers[i];
                    if (manager == null
                        || EditorUtility.IsPersistent(manager)
                        || !manager.gameObject.scene.IsValid()
                        || string.IsNullOrEmpty(manager.gameObject.scene.path))
                    {
                        continue;
                    }

                    BakeAndAssign(manager);
                    bakedCount++;
                }

                Debug.Log(
                    $"[FlowField Surface Bake] 열린 Scene 처리 완료: 성공 {bakedCount}.");
            }
            finally
            {
                if (!_asyncSessionPending)
                    _isBaking = false;
            }
        }

        public static void ScheduleBake(FlowFieldManager manager)
        {
            if (manager == null)
                throw new System.ArgumentNullException(nameof(manager));
            if (_isBaking)
                throw new System.InvalidOperationException("A FlowField bake is already in progress.");

            _isBaking = true;
            int managerId = manager.GetInstanceID();
            EditorApplication.delayCall += () =>
            {
                try
                {
                    var resolved = EditorUtility.InstanceIDToObject(managerId) as FlowFieldManager;
                    if (resolved == null)
                        throw new System.InvalidOperationException("The scheduled FlowField manager no longer exists.");

                    BakeAndAssign(resolved);
                }
                finally
                {
                    if (!_asyncSessionPending)
                        _isBaking = false;
                }
            };
        }

        public static void BakeAndAssign(FlowFieldManager manager)
        {
            if (manager == null)
                throw new System.ArgumentNullException(nameof(manager));

            string assetPath = FlowFieldBakeAssetUtility.ResolveAssetPath(manager);

            FlowFieldSurfaceBakeSettings settings = manager.CreateSurfaceBakeSettings();
            FlowFieldSurfaceBakeResult result = FlowFieldSurfaceBaker.Bake(settings);
            FlowFieldBakeAssetUtility.CreateBakeFolder();

            if (manager.BakeMode == FlowFieldBakeMode.StaticBaked)
            {
                BakeStaticSnapshot(manager, settings, result, assetPath);
                return;
            }

            FlowFieldSurfaceBakeData data = manager.SurfaceBakeData;
            bool assignedNewData = false;
            if (data == null)
            {
                data = AssetDatabase.LoadAssetAtPath<FlowFieldSurfaceBakeData>(assetPath);
                if (data == null)
                {
                    data = ScriptableObject.CreateInstance<FlowFieldSurfaceBakeData>();
                    data.name = Path.GetFileNameWithoutExtension(assetPath);
                    AssetDatabase.CreateAsset(data, assetPath);
                    Undo.RegisterCreatedObjectUndo(data, "Create FlowField Surface Bake");
                }

                Undo.RecordObject(manager, "Assign FlowField Surface Bake");
                manager.AssignSurfaceBakeData(data);
                assignedNewData = true;
            }

            Undo.RecordObject(data, "Bake FlowField Surface");
            data.Apply(settings, result);
            EditorUtility.SetDirty(data);

            BakeStaticObstacles(manager, settings.Grid, assetPath);

            manager.NotifySurfaceBakeChanged();
            EditorUtility.SetDirty(manager);
            if (manager.gameObject.scene.IsValid())
                EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);
            AssetDatabase.SaveAssetIfDirty(data);
            if (manager.StaticObstacleBakeData != null)
                AssetDatabase.SaveAssetIfDirty(manager.StaticObstacleBakeData);
            if (assignedNewData)
                AssetDatabase.SaveAssetIfDirty(manager);
            SceneView.RepaintAll();
            Debug.Log(
                $"[{nameof(FlowFieldManager)}] Surface Bake 완료: "
                + $"{result.ValidCellCount}/{settings.Grid.CellCount} cells → {AssetDatabase.GetAssetPath(data)}",
                manager);
        }

        private static void BakeStaticSnapshot(
            FlowFieldManager manager,
            FlowFieldSurfaceBakeSettings settings,
            FlowFieldSurfaceBakeResult surfaceResult,
            string surfaceAssetPath)
        {
            // Keep all calculation data transient until the common BFS has
            // completed. Existing assets therefore remain untouched if either
            // GPU or Managed fallback fails.
            var surface = ScriptableObject.CreateInstance<FlowFieldSurfaceBakeData>();
            surface.hideFlags = HideFlags.HideAndDontSave;
            surface.Apply(settings, surfaceResult);

            var workspace = new FlowFieldWorkspace();
            workspace.Resize(settings.Grid.CellCount);
            FlowFieldGoalResolution goal = manager.ResolveConfiguredGoal(settings.Grid);
            FlowFieldBuildRequest buildRequest = new FlowFieldBuildRequest(
                settings.Grid,
                FlowFieldSurfaceData.From(surface),
                new FlowFieldObstacleRequest(
                    settings.Grid,
                    surface,
                    null,
                    workspace,
                    manager.ObstacleLayer,
                    manager.ObstacleCheckHeight,
                    manager.ObstacleCheckCenterOffset,
                    manager.ObstacleClearance,
                    useUnregisteredSweep: false,
                    FlowFieldCellRect.Full(settings.Grid)),
                goal,
                FlowFieldDirtyFlags.All,
                Mathf.Min(settings.Grid.CellCount, Mathf.Max(64, manager.MaxGpuWaves)),
                surface.Revision);
            FlowFieldBuildResult prepared = FlowFieldBuildPipeline.PrepareBase(
                buildRequest,
                new FlowFieldObstaclePipeline(),
                new FlowFieldGoalTracker(),
                rebuildStaticObstacles: true,
                rebuildDynamicObstacles: false,
                rebuildGoal: true);
            if (prepared.ExcludedColliderCount > 0)
            {
                Debug.LogWarning(
                    $"[{nameof(FlowFieldManager)}] Static Bake에서 "
                    + $"{prepared.ExcludedColliderCount}개의 비정적 또는 Rigidbody Collider를 제외했습니다.",
                    manager);
            }
            bool hasGoal = prepared.Workspace.HasActiveGoal
                && prepared.ResolvedGoalIndex >= 0;
            int resolvedGoalIndex = hasGoal ? prepared.ResolvedGoalIndex : -1;

            var pipeline = new FlowFieldBuildPipeline(LoadFrontierShader(manager));
            int version = surface.Revision;
            int callbackGeneration = _callbackGeneration;
            int managerInstanceId = manager.GetInstanceID();
            bool capturedGoalActive = goal.HasActiveGoal;
            Vector3 capturedGoalWorld = goal.RequestedWorld;
            float capturedGoalRadius = goal.InfluenceRadius;
            LayerMask capturedObstacleLayer = manager.ObstacleLayer;
            float capturedObstacleCheckHeight = manager.ObstacleCheckHeight;
            float capturedObstacleCenterOffset = manager.ObstacleCheckCenterOffset;
            float capturedObstacleClearance = manager.ObstacleClearance;
            int maxWaves = Mathf.Min(settings.Grid.CellCount, Mathf.Max(64, manager.MaxGpuWaves));
            var request = new FlowFieldBfsRequest(
                settings.Grid,
                surface,
                workspace,
                hasGoal,
                goal.LocalX,
                goal.LocalZ,
                goal.InfluenceRadius,
                resolvedGoalIndex,
                maxWaves,
                version);

            void Complete(FlowFieldBfsRequest completedRequest)
            {
                int undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("Bake FlowField Static Snapshot");
                try
                {
                    if (!IsCurrentBakeInput(
                            manager,
                            managerInstanceId,
                            callbackGeneration,
                            settings,
                            capturedGoalActive,
                            capturedGoalWorld,
                            capturedGoalRadius,
                            capturedObstacleLayer,
                            capturedObstacleCheckHeight,
                            capturedObstacleCenterOffset,
                            capturedObstacleClearance))
                    {
                        Debug.LogWarning(
                            $"[{nameof(FlowFieldManager)}] Static Flow Bake 입력이 변경되어 이전 Asset을 유지합니다.",
                            manager);
                        return;
                    }

                    if (!hasGoal)
                    {
                        for (int index = 0; index < settings.Grid.CellCount; index++)
                        {
                            if (!surface.IsSurfaceValid(index) || workspace.Blocked[index])
                                completedRequest.Workspace.NextCells[index] = -2;
                        }
                    }

                    FlowFieldFinalFieldComposer.Compose(
                        settings.Grid,
                        surface,
                        workspace,
                        Vector3.forward,
                        Array.Empty<FlowFieldModifierLayer>());

                    FlowFieldSurfaceBakeData persistentSurface =
                        AssetDatabase.LoadAssetAtPath<FlowFieldSurfaceBakeData>(surfaceAssetPath);
                    if (persistentSurface == null)
                    {
                        persistentSurface = ScriptableObject.CreateInstance<FlowFieldSurfaceBakeData>();
                        persistentSurface.name = Path.GetFileNameWithoutExtension(surfaceAssetPath);
                        AssetDatabase.CreateAsset(persistentSurface, surfaceAssetPath);
                        Undo.RegisterCreatedObjectUndo(persistentSurface, "Create FlowField Surface Bake");
                    }
                    Undo.RecordObject(persistentSurface, "Bake FlowField Surface");
                    persistentSurface.Apply(settings, surfaceResult);
                    EditorUtility.SetDirty(persistentSurface);

                    string legacyStaticPath = FlowFieldBakeAssetUtility.DeriveSiblingAssetPath(
                        surfaceAssetPath,
                        "_StaticObstacleBake.asset");
                    FlowFieldStaticObstacleBakeData legacyObstacle =
                        AssetDatabase.LoadAssetAtPath<FlowFieldStaticObstacleBakeData>(legacyStaticPath);
                    if (legacyObstacle == null)
                    {
                        legacyObstacle = ScriptableObject.CreateInstance<FlowFieldStaticObstacleBakeData>();
                        legacyObstacle.name = Path.GetFileNameWithoutExtension(legacyStaticPath);
                        AssetDatabase.CreateAsset(legacyObstacle, legacyStaticPath);
                        Undo.RegisterCreatedObjectUndo(legacyObstacle, "Create FlowField Static Obstacles");
                    }
                    Undo.RecordObject(legacyObstacle, "Bake FlowField Static Obstacles");
                    legacyObstacle.Apply(
                        settings.Grid,
                        manager.ObstacleLayer,
                        manager.ObstacleCheckHeight,
                        manager.ObstacleCheckCenterOffset,
                        manager.ObstacleClearance,
                        workspace.StaticBlocked);
                    EditorUtility.SetDirty(legacyObstacle);

                    string staticPath = FlowFieldBakeAssetUtility.ResolveStaticAssetPath(manager);
                    FlowFieldStaticBakeData staticData = AssetDatabase.LoadAssetAtPath<FlowFieldStaticBakeData>(staticPath);
                    if (staticData == null)
                    {
                        staticData = ScriptableObject.CreateInstance<FlowFieldStaticBakeData>();
                        staticData.name = Path.GetFileNameWithoutExtension(staticPath);
                        AssetDatabase.CreateAsset(staticData, staticPath);
                        Undo.RegisterCreatedObjectUndo(staticData, "Create FlowField Static Snapshot");
                    }
                    Undo.RecordObject(staticData, "Bake FlowField Static Snapshot");
                    staticData.Apply(
                        settings,
                        persistentSurface,
                        manager.ObstacleLayer,
                        manager.ObstacleCheckHeight,
                        manager.ObstacleCheckCenterOffset,
                        manager.ObstacleClearance,
                        hasGoal,
                        goal.RequestedWorld,
                        goal.InfluenceRadius,
                        resolvedGoalIndex,
                        workspace);
                    EditorUtility.SetDirty(staticData);

                    Undo.RecordObject(manager, "Assign FlowField Static Bake Assets");
                    manager.AssignSurfaceBakeData(persistentSurface);
                    manager.AssignStaticObstacleBakeData(legacyObstacle);
                    manager.AssignStaticBakeData(staticData);
                    EditorUtility.SetDirty(manager);
                    if (manager.gameObject.scene.IsValid())
                        EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);
                    AssetDatabase.SaveAssets();
                    SceneView.RepaintAll();
                    Debug.Log(
                        $"[{nameof(FlowFieldManager)}] Static Flow Bake 완료: "
                        + $"{settings.Grid.CellCount:N0} cells → {staticPath}",
                        manager);
                    Undo.CollapseUndoOperations(undoGroup);
                }
                finally
                {
                    pipeline.Dispose();
                    workspace.Release();
                    UnityEngine.Object.DestroyImmediate(surface);
                    _asyncSessionPending = false;
                    _isBaking = false;
                }
            }

            void Failed(FlowFieldBfsRequest failedRequest, Exception exception)
            {
                try
                {
                    if (callbackGeneration != _callbackGeneration)
                        return;
                    Debug.LogError(
                        $"[{nameof(FlowFieldManager)}] Static Flow Bake failed: {exception?.Message}",
                        manager);
                }
                finally
                {
                    pipeline.Dispose();
                    workspace.Release();
                    UnityEngine.Object.DestroyImmediate(surface);
                    _asyncSessionPending = false;
                    _isBaking = false;
                }
            }

            _isBaking = true;
            _asyncSessionPending = true;
            if (!pipeline.StartBfs(request, Complete, Failed))
            {
                pipeline.Dispose();
                workspace.Release();
                UnityEngine.Object.DestroyImmediate(surface);
                _asyncSessionPending = false;
                _isBaking = false;
                throw new InvalidOperationException("Static Flow Bake BFS session could not be started.");
            }
        }

        private static bool IsCurrentBakeInput(
            FlowFieldManager manager,
            int managerInstanceId,
            int callbackGeneration,
            FlowFieldSurfaceBakeSettings bakedSettings,
            bool bakedGoalActive,
            Vector3 bakedGoalWorld,
            float bakedGoalRadius,
            LayerMask bakedObstacleLayer,
            float bakedObstacleCheckHeight,
            float bakedObstacleCenterOffset,
            float bakedObstacleClearance)
        {
            if (callbackGeneration != _callbackGeneration
                || manager == null
                || manager.GetInstanceID() != managerInstanceId
                || manager.BakeMode != FlowFieldBakeMode.StaticBaked)
                return false;

            FlowFieldSurfaceBakeSettings currentSettings;
            try
            {
                currentSettings = manager.CreateSurfaceBakeSettings();
            }
            catch
            {
                return false;
            }

            if (!currentSettings.IsValid
                || !currentSettings.Grid.MatchesBounds(bakedSettings.Grid)
                || !FlowFieldBakeBoundsUtility.Approximately(
                    currentSettings.BakeBounds,
                    bakedSettings.BakeBounds)
                || currentSettings.GroundLayer.value != bakedSettings.GroundLayer.value
                || Mathf.Abs(currentSettings.MaxSurfaceSlope - bakedSettings.MaxSurfaceSlope) > 0.0001f
                || Mathf.Abs(currentSettings.MaxStepHeight - bakedSettings.MaxStepHeight) > 0.0001f
                || manager.ObstacleLayer.value != bakedObstacleLayer.value
                || Mathf.Abs(manager.ObstacleCheckHeight - bakedObstacleCheckHeight) > 0.0001f
                || Mathf.Abs(manager.ObstacleCheckCenterOffset - bakedObstacleCenterOffset) > 0.0001f
                || Mathf.Abs(manager.ObstacleClearance - bakedObstacleClearance) > 0.0001f)
                return false;

            bool currentGoalActive = manager.HasConfiguredGoal;
            if (currentGoalActive != bakedGoalActive)
                return false;
            if (!currentGoalActive)
                return true;

            Vector3 currentGoalWorld = manager.ConfiguredGoalWorld;
            return (currentGoalWorld - bakedGoalWorld).sqrMagnitude <= 0.00000001f
                && Mathf.Abs(manager.ConfiguredGoalInfluenceRadius - bakedGoalRadius) <= 0.0001f;
        }

        private static ComputeShader LoadFrontierShader(FlowFieldManager manager)
        {
            ComputeShader shader = manager.FrontierComputeShader;
            return shader != null ? shader : Resources.Load<ComputeShader>("FlowFieldFrontier");
        }

        private static void BakeStaticObstacles(
            FlowFieldManager manager,
            FlowFieldGridSpace grid,
            string surfaceAssetPath)
        {
            string assetPath = FlowFieldBakeAssetUtility.DeriveSiblingAssetPath(surfaceAssetPath, "_StaticObstacleBake.asset");
            FlowFieldStaticObstacleBakeData data = manager.StaticObstacleBakeData;
            if (data == null)
            {
                data = AssetDatabase.LoadAssetAtPath<FlowFieldStaticObstacleBakeData>(assetPath);
                if (data == null)
                {
                    data = ScriptableObject.CreateInstance<FlowFieldStaticObstacleBakeData>();
                    data.name = Path.GetFileNameWithoutExtension(assetPath);
                    AssetDatabase.CreateAsset(data, assetPath);
                    Undo.RegisterCreatedObjectUndo(data, "Create FlowField Static Obstacles");
                }

                Undo.RecordObject(manager, "Assign FlowField Static Obstacle Bake");
                manager.AssignStaticObstacleBakeData(data);
            }

            var blocked = new bool[grid.CellCount];
            Collider[] buffer = null;
            Collider[] targetOverlapBuffer = null;
            if (!FlowFieldObstacleMaskBuilder.BuildStatic(
                grid,
                    manager.SurfaceBakeData,
                    manager.ObstacleLayer,
                    manager.ObstacleCheckHeight,
                    manager.ObstacleCheckCenterOffset,
                    manager.ObstacleClearance,
                    blocked,
                    ref buffer,
                ref targetOverlapBuffer,
                out int excludedColliderCount,
                syncTransformsBeforeQuery: true))
                throw new System.InvalidOperationException("Static Obstacle mask 생성에 실패했습니다.");

            if (excludedColliderCount > 0)
            {
                Debug.LogWarning(
                    $"[{nameof(FlowFieldManager)}] Static Bake에서 "
                    + $"{excludedColliderCount}개의 비정적 또는 Rigidbody Collider를 제외했습니다. "
                    + "이 Collider는 RegisterDynamicObstacle 또는 Unregistered Sweep으로 처리해야 합니다.",
                    manager);
            }

            Undo.RecordObject(data, "Bake FlowField Static Obstacles");
            data.Apply(
                grid,
                manager.ObstacleLayer,
                manager.ObstacleCheckHeight,
                manager.ObstacleCheckCenterOffset,
                manager.ObstacleClearance,
                blocked);
            EditorUtility.SetDirty(data);
        }

        public static void ClearReference(FlowFieldManager manager)
        {
            if (manager == null
                || (manager.SurfaceBakeData == null
                    && manager.StaticObstacleBakeData == null
                    && manager.StaticBakeData == null))
                return;

            Undo.RecordObject(manager, "Clear FlowField Surface Bake Reference");
            manager.AssignSurfaceBakeData(null);
            manager.AssignStaticObstacleBakeData(null);
            manager.AssignStaticBakeData(null);
            EditorUtility.SetDirty(manager);
            if (manager.gameObject.scene.IsValid())
                EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);
        }

    }

    internal static class FlowFieldBakeAssetUtility
    {
        private const string BAKE_DIRECTORY = "Assets/_FlowField/Settings";

        internal static string DeriveSiblingAssetPath(string surfaceAssetPath, string suffix)
        {
            string directory = Path.GetDirectoryName(surfaceAssetPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(directory))
                throw new System.ArgumentException("Surface asset path must include an Assets directory.", nameof(surfaceAssetPath));
            string fileName = Path.GetFileNameWithoutExtension(surfaceAssetPath);
            if (fileName.EndsWith("_SurfaceBake"))
                fileName = fileName.Substring(0, fileName.Length - "_SurfaceBake".Length);
            return $"{directory}/{fileName}{suffix}";
        }

        internal static string ResolveAssetPath(FlowFieldManager manager)
        {
            if (manager == null)
                throw new System.ArgumentNullException(nameof(manager));
            if (!manager.gameObject.scene.IsValid() || string.IsNullOrEmpty(manager.gameObject.scene.path))
                throw new System.InvalidOperationException("Scene을 먼저 저장해야 합니다.");

            ValidateFileName(manager.name);
            GlobalObjectId globalId = GlobalObjectId.GetGlobalObjectIdSlow(manager);
            string id = globalId.targetObjectId != 0
                ? globalId.targetObjectId.ToString()
                : manager.GetInstanceID().ToString();
            return $"{BAKE_DIRECTORY}/{manager.name}_{id}_SurfaceBake.asset";
        }

        internal static string ResolveStaticAssetPath(FlowFieldManager manager)
        {
            string surfacePath = ResolveAssetPath(manager);
            return DeriveSiblingAssetPath(surfacePath, "_StaticBake.asset");
        }

        internal static void CreateBakeFolder()
            => CreateFolderHierarchy(BAKE_DIRECTORY);

        private static void CreateFolderHierarchy(string path)
        {
            string[] parts = path.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(current, parts[i])))
                        throw new System.InvalidOperationException($"Unable to create FlowField bake folder '{next}'.");
                }
                current = next;
            }

            if (!AssetDatabase.IsValidFolder(path))
                throw new System.InvalidOperationException($"FlowField bake folder is not available: {path}");
        }

        private static void ValidateFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)
                || value == "."
                || value == ".."
                || value.IndexOfAny(new[] { '/', '\\' }) >= 0
                || value.IndexOf("..", System.StringComparison.Ordinal) >= 0)
                throw new System.ArgumentException("FlowField manager name must be a simple asset file name.", nameof(value));

            char[] invalidCharacters = Path.GetInvalidFileNameChars();
            for (int i = 0; i < invalidCharacters.Length; i++)
                if (value.IndexOf(invalidCharacters[i]) >= 0)
                    throw new System.ArgumentException("FlowField manager name contains an invalid character.", nameof(value));
        }
    }
}
