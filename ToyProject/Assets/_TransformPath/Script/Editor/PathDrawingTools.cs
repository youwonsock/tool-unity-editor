using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Common.TransformPath.Editor
{
    /// <summary>The MultiPathData drawing tools: shared preview sizes and distinct colors for its PathData.</summary>
    internal static class PathDrawingTools
    {
        private const float GOLDEN_RATIO_CONJUGATE = 0.618033988749895f;
        private const float PATH_COLOR_SATURATION = 0.72f;
        private const float PATH_COLOR_VALUE = 1f;
        // PathData preview fields the tools write.
        private const string PATH_COLOR_PROPERTY = "_pathColor";
        private const string LINE_WIDTH_PROPERTY = "_lineWidth";
        private const string POINT_SIZE_PROPERTY = "_pointSize";
        private const string SAMPLE_POINT_SIZE_PROPERTY = "_samplePointSize";
        private const string EVENT_POINT_SIZE_PROPERTY = "_eventPointSize";

        /// <summary>Copies this MultiPath's drawing sizes to every PathData it plays.</summary>
        public static void ApplySharedSizes(MultiPathData multiPathData)
        {
            PathPreviewStyle template = multiPathData.PreviewStyleTemplate;
            List<PathData> paths = PathEditorSerializationUtility.CollectUniquePathDatas(multiPathData);
            PathEditorUndoUtility.RecordObjects(paths.ToArray(), "Apply MultiPath drawing settings");
            for (int i = 0; i < paths.Count; i++)
            {
                var serializedPath = new SerializedObject(paths[i]);
                serializedPath.Update();
                PathEditorSerializationUtility.SetFloat(serializedPath, LINE_WIDTH_PROPERTY, template.LineWidth);
                PathEditorSerializationUtility.SetFloat(serializedPath, POINT_SIZE_PROPERTY, template.PointSize);
                PathEditorSerializationUtility.SetFloat(serializedPath, SAMPLE_POINT_SIZE_PROPERTY, template.SamplePointSize);
                PathEditorSerializationUtility.SetFloat(serializedPath, EVENT_POINT_SIZE_PROPERTY, template.EventPointSize);
                serializedPath.ApplyModifiedProperties();
                PathEditorUndoUtility.MarkDirty(paths[i]);
            }
            PathEditorUndoUtility.RepaintScene();
        }

        /// <summary>Gives each unique PathData its own hue, spread by the golden ratio.</summary>
        public static void AssignDistinctColors(MultiPathData multiPathData)
        {
            List<PathData> slots = PathEditorSerializationUtility.GetPathDatas(multiPathData);
            List<PathData> paths = PathEditorSerializationUtility.CollectUniquePathDatas(multiPathData);
            if (paths.Count == 0)
                return;

            PathEditorUndoUtility.RecordObjects(paths.ToArray(), "Assign distinct path colors");
            for (int i = 0; i < paths.Count; i++)
            {
                float hue = (i * GOLDEN_RATIO_CONJUGATE) % 1f;
                var serializedPath = new SerializedObject(paths[i]);
                serializedPath.Update();
                SerializedProperty colorProperty = serializedPath.FindProperty(PATH_COLOR_PROPERTY);
                if (colorProperty != null)
                    colorProperty.colorValue = Color.HSVToRGB(hue, PATH_COLOR_SATURATION, PATH_COLOR_VALUE);
                serializedPath.ApplyModifiedProperties();
                PathEditorUndoUtility.MarkDirty(paths[i]);
            }

            if (slots.Count != paths.Count)
                Debug.LogWarning("MultiPathData: the same PathData is in several slots, so they share the first slot's color.", multiPathData);
        }
    }
}
