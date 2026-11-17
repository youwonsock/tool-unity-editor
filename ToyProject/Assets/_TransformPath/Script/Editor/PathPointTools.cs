using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>The PathData Inspector's point authoring tools, each with Undo.</summary>
    internal static class PathPointTools
    {
        private const float MAX_RAYCAST_DISTANCE = 1000f;
        private const string PATH_POINT_NAME_PREFIX = "PathPoint";

        public static void SnapToGround(IReadOnlyList<PathData> paths)
        {
            for (int i = 0; i < paths.Count; i++)
            {
                PathData pathData = paths[i];
                if (pathData == null)
                    continue;

                List<Transform> pathPoints = PathEditorSerializationUtility.GetPathPoints(pathData);
                if (pathPoints.Count == 0)
                {
                    Debug.LogWarning("PathData: 스냅할 경로 포인트가 없습니다.", pathData);
                    continue;
                }

                PathEditorUndoUtility.RecordObjects(
                    pathPoints.ToArray(),
                    "Snap Path Points to Ground");
                int snappedCount = 0;
                int failedCount = 0;
                for (int pointIndex = 0; pointIndex < pathPoints.Count; pointIndex++)
                {
                    Transform point = pathPoints[pointIndex];
                    if (point == null)
                        continue;
                    if (Physics.Raycast(
                            point.position,
                            Vector3.down,
                            out RaycastHit hit,
                            MAX_RAYCAST_DISTANCE))
                    {
                        point.position = hit.point;
                        PathEditorUndoUtility.RecordPrefabOverride(point);
                        snappedCount++;
                    }
                    else
                        failedCount++;
                }

                if (snappedCount > 0)
                {
                    PathEditorUndoUtility.MarkDirty(pathData);
                    Debug.Log(
                        $"PathData: {snappedCount}개의 포인트를 지면에 스냅했습니다. (실패: {failedCount}개)",
                        pathData);
                }
                else
                    Debug.LogWarning(
                        $"PathData: 스냅된 포인트가 없습니다. (실패: {failedCount}개)",
                        pathData);
            }
            PathEditorUndoUtility.RepaintScene();
        }

        /// <summary>Adds a start, a middle and an end point under each path and uses them.</summary>
        public static void CreateDefaultPoints(IReadOnlyList<PathData> paths)
        {
            for (int i = 0; i < paths.Count; i++)
            {
                PathData pathData = paths[i];
                if (pathData == null)
                    continue;

                int undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("Create Path Points");
                PathEditorUndoUtility.Record(pathData, "Create Path Points");
                Transform start = CreatePathPoint(
                    pathData.transform,
                    "PathPointStart",
                    Vector3.zero);
                Transform middle = CreatePathPoint(
                    pathData.transform,
                    "PathPoint",
                    Vector3.forward * 5f);
                Transform end = CreatePathPoint(
                    pathData.transform,
                    "PathPointEnd",
                    Vector3.forward * 10f);
                SetPathPoints(pathData, new List<Transform> { start, middle, end });
                PathEditorUndoUtility.MarkDirty(pathData);
                Undo.CollapseUndoOperations(undoGroup);
                Debug.Log(
                    "PathData: PathPointStart, PathPoint, PathPointEnd 생성 완료",
                    pathData);
            }
            PathEditorUndoUtility.RepaintScene();
        }

        /// <summary>Uses the descendants whose names start with PathPoint, in hierarchy order.</summary>
        public static void RefreshFromChildren(IReadOnlyList<PathData> paths)
        {
            for (int i = 0; i < paths.Count; i++)
            {
                PathData pathData = paths[i];
                if (pathData == null)
                    continue;

                List<Transform> collected = new List<Transform>();
                PathEditorSerializationUtility.CollectTransformsWithPathPointPrefix(
                    pathData.transform,
                    collected,
                    PATH_POINT_NAME_PREFIX);
                SetPathPoints(pathData, collected);
                if (collected.Count < 2)
                    Debug.LogWarning(
                        $"PathData: '{PATH_POINT_NAME_PREFIX}'로 시작하는 자손이 {collected.Count}개입니다. 유효 경로는 최소 2개가 필요합니다.",
                        pathData);
                else
                    Debug.Log(
                        $"PathData: _pathPoints를 자손에서 {collected.Count}개 갱신했습니다.",
                        pathData);
            }
            PathEditorUndoUtility.RepaintScene();
        }

        private static Transform CreatePathPoint(
            Transform parent,
            string pointName,
            Vector3 localPosition)
        {
            GameObject pointObject = new GameObject(pointName);
            Transform point = pointObject.transform;
            point.SetParent(parent, false);
            point.localPosition = localPosition;
            Undo.RegisterCreatedObjectUndo(pointObject, $"Create {pointName}");
            return point;
        }

        private static void SetPathPoints(
            PathData pathData,
            List<Transform> points)
        {
            if (pathData == null)
                return;

            PathEditorUndoUtility.Record(pathData, "Set Path Points");
            SerializedObject serializedPathData = new SerializedObject(pathData);
            SerializedProperty pathPointsProperty = serializedPathData.FindProperty("_pathPoints");
            if (pathPointsProperty == null)
                return;

            serializedPathData.Update();
            pathPointsProperty.ClearArray();
            if (points != null)
            {
                for (int i = 0; i < points.Count; i++)
                {
                    pathPointsProperty.InsertArrayElementAtIndex(i);
                    pathPointsProperty.GetArrayElementAtIndex(i).objectReferenceValue = points[i];
                }
            }
            serializedPathData.ApplyModifiedProperties();
            PathEditorUndoUtility.MarkDirty(pathData);
        }
    }
}
