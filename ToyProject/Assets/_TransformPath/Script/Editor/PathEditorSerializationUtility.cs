using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Common.TransformPath.Editor
{
    /// <summary>Shared SerializedProperty lookups used by TransformPath editors.</summary>
    internal static class PathEditorSerializationUtility
    {
        #region Constants

        public const string SEGMENTS_PROPERTY = "_segments";
        public const string PATH_PROPERTY = "_path";

        #endregion


        #region Public Methods

        public static List<PathData> GetPathDatas(MultiPathData multiPathData)
        {
            List<PathData> result = new List<PathData>();
            if (multiPathData == null)
                return result;
            for (int i = 0; i < multiPathData.AuthoringSegmentCount; i++)
                result.Add(multiPathData.GetAuthoringSegment(i).Path);
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
            SerializedProperty pathProperty = segment.FindPropertyRelative(PATH_PROPERTY);
            return pathProperty == null
                ? null
                : pathProperty.objectReferenceValue as PathData;
        }

        public static List<Transform> GetPathPoints(PathData pathData)
        {
            return pathData == null || pathData.PathPoints == null
                ? new List<Transform>()
                : new List<Transform>(pathData.PathPoints);
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

            StringBuilder signature = new StringBuilder();
            signature.Append(multiPathData.AuthoringSegmentCount);
            signature.Append('|');
            for (int i = 0; i < multiPathData.AuthoringSegmentCount; i++)
            {
                PathData pathData = multiPathData.GetAuthoringSegment(i).Path;
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
            IReadOnlyList<Transform> points = pathData == null || pathData.PathPoints == null
                ? (IReadOnlyList<Transform>)Array.Empty<Transform>()
                : pathData.PathPoints;
            signature.Append(points.Count);
            signature.Append('[');
            for (int i = 0; i < points.Count; i++)
            {
                signature.Append(points[i] == null ? 0 : points[i].GetInstanceID());
                signature.Append(';');
            }
            signature.Append(']');
        }

        public static void SetFloat(SerializedObject serializedObject, string propertyName, float value)
        {
            SerializedProperty property = serializedObject.FindProperty(propertyName);
            if (property != null)
                property.floatValue = value;
        }

        public static void SetEnumIndex(SerializedObject serializedObject, string propertyName, int index)
        {
            SerializedProperty property = serializedObject.FindProperty(propertyName);
            if (property != null)
                property.enumValueIndex = index;
        }

        public static void SetCurve(SerializedObject serializedObject, string propertyName, AnimationCurve value)
        {
            SerializedProperty property = serializedObject.FindProperty(propertyName);
            if (property != null)
                property.animationCurveValue = PathValueUtility.CloneCurve(value);
        }

        #endregion
    }
}
