#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Common.TransformPath
{
    [CustomEditor(typeof(PathData))]
    internal sealed class PathDataEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.Space();
            if (GUILayout.Button("Rebuild Runtime Path"))
            {
                foreach (Object targetObject in targets)
                {
                    PathData pathData = targetObject as PathData;
                    if (pathData == null)
                        continue;
                    Undo.RecordObject(pathData, "Rebuild Runtime Path");
                    pathData.Rebuild();
                    EditorUtility.SetDirty(pathData);
                }
            }
            if (GUILayout.Button("Sort Path Events"))
            {
                foreach (Object targetObject in targets)
                {
                    PathData pathData = targetObject as PathData;
                    if (pathData == null)
                        continue;
                    Undo.RecordObject(pathData, "Sort Path Events");
                    if (pathData.SortPathEventsByNormalizedTime())
                        EditorUtility.SetDirty(pathData);
                }
            }
        }
    }
}
#endif
