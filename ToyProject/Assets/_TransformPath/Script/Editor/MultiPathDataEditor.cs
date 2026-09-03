#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Common.TransformPath
{
    [CustomEditor(typeof(MultiPathData))]
    internal sealed class MultiPathDataEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Sequence segments are length-indexed at runtime. Rebuild after changing child geometry or segment settings.",
                MessageType.Info);
            if (GUILayout.Button("Rebuild Sequence"))
            {
                foreach (Object targetObject in targets)
                {
                    MultiPathData multiPathData = targetObject as MultiPathData;
                    if (multiPathData == null)
                        continue;
                    Undo.RecordObject(multiPathData, "Rebuild Sequence");
                    multiPathData.Rebuild();
                    EditorUtility.SetDirty(multiPathData);
                }
            }
        }
    }
}
#endif
