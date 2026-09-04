#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>Editor-only Undo, dirty, and prefab override operations.</summary>
    internal static class PathEditorUndoUtility
    {
        #region Public Methods

        public static void Record(Object targetObject, string undoName)
        {
            if (targetObject != null)
                Undo.RecordObject(targetObject, undoName);
        }

        public static void RecordObjects(Object[] targetObjects, string undoName)
        {
            if (targetObjects != null && targetObjects.Length > 0)
                Undo.RecordObjects(targetObjects, undoName);
        }

        public static void MarkDirty(Object targetObject)
        {
            if (targetObject == null)
                return;
            EditorUtility.SetDirty(targetObject);
            RecordPrefabOverride(targetObject);
        }

        public static void RecordPrefabOverride(Object targetObject)
        {
            if (targetObject != null && PrefabUtility.IsPartOfPrefabInstance(targetObject))
                PrefabUtility.RecordPrefabInstancePropertyModifications(targetObject);
        }

        public static void RepaintScene()
        {
            SceneView.RepaintAll();
        }

        #endregion
    }
}
#endif
