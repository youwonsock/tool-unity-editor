using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Supercent.Common.FlowField.Editor
{
    internal static class FlowFieldSurfaceBakeEditor
    {
        private static bool _isBaking;

        public static bool IsBaking => _isBaking;

        [MenuItem("Tools/Supercent/FlowField/Bake All Managers In Open Scenes")]
        private static void BakeAllManagersInOpenScenes()
        {
            if (_isBaking)
                return;

            _isBaking = true;
            try
            {
                FlowFieldManager[] managers = Resources.FindObjectsOfTypeAll<FlowFieldManager>();
                int bakedCount = 0;
                int failedCount = 0;
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

                    if (TryBakeAndAssign(manager))
                        bakedCount++;
                    else
                        failedCount++;
                }

                Debug.Log(
                    $"[FlowField Surface Bake] 열린 Scene 처리 완료: 성공 {bakedCount}, 실패 {failedCount}.");
            }
            finally
            {
                _isBaking = false;
            }
        }

        public static void ScheduleBake(FlowFieldManager manager)
        {
            if (manager == null || _isBaking)
                return;

            _isBaking = true;
            int managerId = manager.GetInstanceID();
            EditorApplication.delayCall += () =>
            {
                try
                {
                    var resolved = EditorUtility.InstanceIDToObject(managerId) as FlowFieldManager;
                    if (resolved == null)
                        return;

                    TryBakeAndAssign(resolved);
                }
                finally
                {
                    _isBaking = false;
                }
            };
        }

        public static bool TryBakeAndAssign(FlowFieldManager manager)
        {
            if (manager == null)
                return false;

            if (!FlowFieldBakeAssetUtility.TryResolveAssetPath(manager, out string assetPath, out string error))
            {
                Debug.LogError($"[{nameof(FlowFieldManager)}] Surface Bake 실패: {error}", manager);
                return false;
            }

            FlowFieldSurfaceBakeSettings settings = manager.CreateSurfaceBakeSettings();
            if (!FlowFieldSurfaceBaker.TryBake(settings, out FlowFieldSurfaceBakeResult result, out error))
            {
                Debug.LogError($"[{nameof(FlowFieldManager)}] Surface Bake 실패: {error}", manager);
                return false;
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
                }

                Undo.RecordObject(manager, "Assign FlowField Surface Bake");
                manager.AssignSurfaceBakeData(data);
                assignedNewData = true;
            }

            Undo.RecordObject(data, "Bake FlowField Surface");
            data.Apply(settings, result);
            EditorUtility.SetDirty(data);

            if (!TryBakeStaticObstacles(manager, settings.Grid, assetPath, out error))
            {
                Debug.LogError($"[{nameof(FlowFieldManager)}] Static Obstacle Bake 실패: {error}", manager);
                return false;
            }

            if (!TryBakeCoarseTopology(manager, settings.Grid, data, assetPath, out error))
            {
                Debug.LogError($"[{nameof(FlowFieldManager)}] Coarse Topology Bake 실패: {error}", manager);
                return false;
            }

            manager.NotifySurfaceBakeChanged();
            EditorUtility.SetDirty(manager);
            if (manager.gameObject.scene.IsValid())
                EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);
            AssetDatabase.SaveAssetIfDirty(data);
            if (manager.StaticObstacleBakeData != null)
                AssetDatabase.SaveAssetIfDirty(manager.StaticObstacleBakeData);
            if (manager.CoarseTopologyData != null)
                AssetDatabase.SaveAssetIfDirty(manager.CoarseTopologyData);
            if (assignedNewData)
                AssetDatabase.SaveAssetIfDirty(manager);
            SceneView.RepaintAll();
            Debug.Log(
                $"[{nameof(FlowFieldManager)}] Surface Bake 완료: "
                + $"{result.ValidCellCount}/{settings.Grid.CellCount} cells → {AssetDatabase.GetAssetPath(data)}",
                manager);
            return true;
        }

        private static bool TryBakeStaticObstacles(
            FlowFieldManager manager,
            FlowFieldGridSpace grid,
            string surfaceAssetPath,
            out string error)
        {
            error = string.Empty;
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
            {
                error = "Static Obstacle mask 생성에 실패했습니다.";
                return false;
            }

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
            return true;
        }

        private static bool TryBakeCoarseTopology(
            FlowFieldManager manager,
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            string surfaceAssetPath,
            out string error)
        {
            if (!FlowFieldCoarseTopologyBaker.TryBake(
                    grid,
                    surface,
                    manager.CoarseCellMultiplier,
                    manager.CoarseWalkableRatio,
                    out byte[] walkable,
                    out byte[] neighborMasks,
                    out error))
                return false;

            string assetPath = FlowFieldBakeAssetUtility.DeriveSiblingAssetPath(surfaceAssetPath, "_CoarseTopology.asset");
            FlowFieldCoarseTopologyData data = manager.CoarseTopologyData;
            if (data == null)
            {
                data = AssetDatabase.LoadAssetAtPath<FlowFieldCoarseTopologyData>(assetPath);
                if (data == null)
                {
                    data = ScriptableObject.CreateInstance<FlowFieldCoarseTopologyData>();
                    data.name = Path.GetFileNameWithoutExtension(assetPath);
                    AssetDatabase.CreateAsset(data, assetPath);
                }

                Undo.RecordObject(manager, "Assign FlowField Coarse Topology");
                manager.AssignCoarseTopologyData(data);
            }

            Undo.RecordObject(data, "Bake FlowField Coarse Topology");
            data.Apply(grid, manager.CoarseCellMultiplier, manager.CoarseWalkableRatio, walkable, neighborMasks);
            EditorUtility.SetDirty(data);
            return true;
        }

        public static void ClearReference(FlowFieldManager manager)
        {
            if (manager == null || manager.SurfaceBakeData == null)
                return;

            Undo.RecordObject(manager, "Clear FlowField Surface Bake Reference");
            manager.AssignSurfaceBakeData(null);
            manager.AssignStaticObstacleBakeData(null);
            manager.AssignCoarseTopologyData(null);
            EditorUtility.SetDirty(manager);
            if (manager.gameObject.scene.IsValid())
                EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);
        }

    }
}
