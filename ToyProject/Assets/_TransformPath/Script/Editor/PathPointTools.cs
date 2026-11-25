using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Common.TransformPath.Editor
{
    /// <summary>The PathData Inspector's point authoring tools, each with Undo.</summary>
    internal static class PathPointTools
    {
        private const float MAX_RAYCAST_DISTANCE = 1000f;
        // The ray starts this far above each point, so a point a little under the
        // ground, or exactly on it, still finds it. A surface closer above a point
        // than this counts as its ground.
        private const float GROUND_SEARCH_LIFT = 0.5f;
        private const string PATH_POINT_NAME_PREFIX = "PathPoint";

        private static RaycastHit[] _hits = new RaycastHit[16];

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
                    Debug.LogWarning("PathData: there are no path points to snap.", pathData);
                    continue;
                }

                // A closed outline lists its first point again; it moves once.
                var unique = new HashSet<Transform>();
                pathPoints.RemoveAll(point => point != null && !unique.Add(point));
                PathEditorUndoUtility.RecordObjects(
                    pathPoints.ToArray(),
                    "Snap Path Points to Ground");
                // The path's own scene: in Prefab Mode that is the prefab's preview
                // scene, which has its own physics apart from the main scene. In Context
                // the level around the prefab is searched when the prefab has no ground there.
                // A prefab asset that is not open has no scene and searches the main one.
                Scene scene = pathData.gameObject.scene;
                PhysicsScene physics = scene.IsValid() ? scene.GetPhysicsScene() : Physics.defaultPhysicsScene;
                PrefabStage stage = PrefabStageUtility.GetPrefabStage(pathData.gameObject);
                bool searchContext = stage != null && stage.mode == PrefabStage.Mode.InContext;
                int snappedCount = 0;
                int failedCount = 0;
                for (int pointIndex = 0; pointIndex < pathPoints.Count; pointIndex++)
                {
                    Transform point = pathPoints[pointIndex];
                    if (point == null)
                        continue;
                    if (TryFindGround(physics, point, out Vector3 ground)
                        || (searchContext && TryFindGround(Physics.defaultPhysicsScene, point, out ground)))
                    {
                        point.position = ground;
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
                        $"PathData: snapped {snappedCount} point(s) to the ground ({failedCount} failed).",
                        pathData);
                }
                else
                    Debug.LogWarning(
                        $"PathData: no point was snapped ({failedCount} failed).",
                        pathData);
            }
            PathEditorUndoUtility.RepaintScene();
        }

        // The nearest surface below the start of the ray. The ray starts above the
        // point, so it can meet a collider on the point itself, which is not ground.
        // Triggers are volumes, not ground.
        private static bool TryFindGround(PhysicsScene physics, Transform point, out Vector3 ground)
        {
            Vector3 origin = point.position + Vector3.up * GROUND_SEARCH_LIFT;
            float distance = MAX_RAYCAST_DISTANCE + GROUND_SEARCH_LIFT;
            int count;
            // A full buffer may have left out the nearest hit.
            while ((count = physics.Raycast(origin, Vector3.down, _hits, distance,
                       Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) == _hits.Length)
                _hits = new RaycastHit[_hits.Length * 2];
            ground = default;
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = _hits[i];
                if (hit.distance >= nearest || hit.collider.transform.IsChildOf(point))
                    continue;
                nearest = hit.distance;
                ground = hit.point;
            }
            return nearest < float.PositiveInfinity;
        }

        /// <summary>Adds a start, a middle and an end point under each path and uses them.</summary>
        public static void CreateDefaultPoints(IReadOnlyList<PathData> paths)
        {
            for (int i = 0; i < paths.Count; i++)
            {
                PathData pathData = paths[i];
                if (pathData == null)
                    continue;
                if (!CanCreatePoints(pathData))
                {
                    Debug.LogWarning(
                        "PathData: open the prefab to create path points; a prefab asset cannot take new children here.",
                        pathData);
                    continue;
                }

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
                    "PathData: created PathPointStart, PathPoint and PathPointEnd.",
                    pathData);
            }
            PathEditorUndoUtility.RepaintScene();
        }

        /// <summary>
        /// New points are scene objects parented under the path. A prefab asset that
        /// is not open lives in no scene, so Unity refuses the parent and the points
        /// would stay behind in the open scene.
        /// </summary>
        public static bool CanCreatePoints(PathData pathData) =>
            pathData != null && !EditorUtility.IsPersistent(pathData);

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
                        $"PathData: {collected.Count} descendant(s) start with '{PATH_POINT_NAME_PREFIX}'; a path needs at least 2.",
                        pathData);
                else
                    Debug.Log(
                        $"PathData: set _pathPoints to {collected.Count} descendant(s).",
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
