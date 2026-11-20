using Common.FlowField;
using UnityEditor;
using UnityEngine;

namespace Common.FlowField.Editor
{
    [CustomEditor(typeof(FlowFieldVectorModifierVolume), true)]
    internal sealed class FlowFieldVectorModifierVolumeEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            // An edited Priority or Manager shows in this repaint, before the
            // editor's change events drop the cached duplicates.
            if (DrawDefaultInspector())
                FlowFieldModifierPriorities.ClearCache();

            var modifier = (FlowFieldVectorModifierVolume)target;
            if (modifier.FlowFieldManager == null)
            {
                DrawWarning("Assign a FlowField Manager to register the Modifier.", MessageType.Error);
                return;
            }

            if (!FlowFieldModifierMaskBuilder.TryValidateInfluenceCollider(modifier.InfluenceCollider, out string reason))
                DrawWarning(reason, MessageType.Error);

            DrawDuplicatePriority(modifier);
        }

        // Registration rejects a second Modifier with the same Priority.
        private static void DrawDuplicatePriority(FlowFieldVectorModifierVolume modifier)
        {
            foreach (var group in FlowFieldModifierPriorities.FindDuplicatePriorities(modifier.FlowFieldManager))
            {
                if (group.Value.Contains(modifier))
                    DrawWarning(FlowFieldModifierPriorities.Describe(group.Key, group.Value), MessageType.Warning);
            }
        }

        private static void DrawWarning(string message, MessageType messageType)
        {
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(message, messageType);
        }
    }
}
