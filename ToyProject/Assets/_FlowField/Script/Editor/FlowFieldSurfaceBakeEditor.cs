using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Common.FlowField.Editor
{
    internal static class FlowFieldSurfaceBakeEditor
    {
        private static bool _isBaking;

        public static bool IsBaking => _isBaking;

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

            BakeStaticObstacles(manager, settings.Grid, assetPath);

            BakeCoarseTopology(manager, settings.Grid, data, assetPath);

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

        private static void BakeCoarseTopology(
            FlowFieldManager manager,
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            string surfaceAssetPath)
        {
            FlowFieldCoarseTopologyBaker.Bake(
                    grid,
                    surface,
                    manager.CoarseCellMultiplier,
                    manager.CoarseWalkableRatio,
                    out byte[] walkable,
                    out byte[] neighborMasks);

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
