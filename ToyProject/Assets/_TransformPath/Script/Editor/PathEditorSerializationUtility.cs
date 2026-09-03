#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>Shared SerializedProperty lookups used by TransformPath editors.</summary>
    internal static class PathEditorSerializationUtility
    {
        public const string SEGMENTS_PROPERTY = "_segments";
        public const string PATH_DATA_PROPERTY = "_pathData";

        public static List<PathData> GetPathDatas(MultiPathData multiPathData)
        {
            List<PathData> result = new List<PathData>();
            if (multiPathData == null)
                return result;

            SerializedObject serializedMultiPath = new SerializedObject(multiPathData);
            SerializedProperty segments = serializedMultiPath.FindProperty(SEGMENTS_PROPERTY);
            if (segments == null)
                return result;

            for (int i = 0; i < segments.arraySize; i++)
                result.Add(GetPathData(segments, i));
            return result;
        }

        public static List<PathData> CollectUniquePathDatas(MultiPathData multiPathData)
        {
            List<PathData> result = new List<PathData>();
            HashSet<PathData> seen = new HashSet<PathData>();
            List<PathData> pathDatas = GetPathDatas(multiPathData);
            for (int i = 0; i < pathDatas.Count; i++)
            {
                PathData pathData = pathDatas[i];
                if (pathData != null && seen.Add(pathData))
                    result.Add(pathData);
            }

            return result;
        }

        public static PathData GetPathData(SerializedProperty segments, int index)
        {
            if (segments == null || index < 0 || index >= segments.arraySize)
                return null;

            SerializedProperty segment = segments.GetArrayElementAtIndex(index);
            SerializedProperty pathDataProperty = segment.FindPropertyRelative(PATH_DATA_PROPERTY);
            return pathDataProperty == null
                ? null
                : pathDataProperty.objectReferenceValue as PathData;
        }

        public static List<Transform> GetPathPoints(PathData pathData)
        {
            List<Transform> result = new List<Transform>();
            if (pathData == null)
                return result;

            SerializedObject serializedPathData = new SerializedObject(pathData);
            SerializedProperty points = serializedPathData.FindProperty("_pathPoints");
            if (points == null)
                return result;

            for (int i = 0; i < points.arraySize; i++)
                result.Add(GetPathPoint(points, i));
            return result;
        }

        public static Transform GetPathPoint(SerializedProperty points, int index)
        {
            if (points == null || index < 0 || index >= points.arraySize)
                return null;
            return points.GetArrayElementAtIndex(index).objectReferenceValue as Transform;
        }

        public static void CollectTransformsWithPathPointPrefix(
            Transform parent,
            List<Transform> results,
            string prefix)
        {
            if (parent == null || results == null)
                return;

            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.name.StartsWith(prefix, StringComparison.Ordinal))
                    results.Add(child);
                CollectTransformsWithPathPointPrefix(child, results, prefix);
            }
        }

        public static string BuildPathConfigsSignature(MultiPathData multiPathData)
        {
            if (multiPathData == null)
                return "null";

            SerializedObject serializedMultiPath = new SerializedObject(multiPathData);
            SerializedProperty segments = serializedMultiPath.FindProperty(SEGMENTS_PROPERTY);
            if (segments == null)
                return "null";

            StringBuilder signature = new StringBuilder();
            signature.Append(segments.arraySize);
            signature.Append('|');
            for (int i = 0; i < segments.arraySize; i++)
            {
                PathData pathData = GetPathData(segments, i);
                signature.Append(pathData == null ? 0 : pathData.GetInstanceID());
                signature.Append(':');
                AppendPathPointSignature(signature, pathData);
                signature.Append(',');
            }

            return signature.ToString();
        }

        public static void AppendPathPointSignature(
            StringBuilder signature,
            PathData pathData)
        {
            List<Transform> points = GetPathPoints(pathData);
            signature.Append(points.Count);
            signature.Append('[');
            for (int i = 0; i < points.Count; i++)
            {
                signature.Append(points[i] == null ? 0 : points[i].GetInstanceID());
                signature.Append(';');
            }
            signature.Append(']');
        }
    }
}
#endif
