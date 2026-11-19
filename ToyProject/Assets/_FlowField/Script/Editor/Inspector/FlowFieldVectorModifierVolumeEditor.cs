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
            DrawDefaultInspector();

            var modifier = (FlowFieldVectorModifierVolume)target;
            if (modifier.FlowFieldManager == null)
            {
                DrawWarning("Assign a FlowField Manager to register the Modifier.", MessageType.Error);
                return;
            }

            Collider influenceCollider = modifier.InfluenceCollider;
            if (influenceCollider == null)
            {
                DrawWarning("Assign an Influence Collider.", MessageType.Error);
                return;
            }

            if (!influenceCollider.isTrigger)
                DrawWarning(FlowFieldModifierMaskBuilder.TriggerRequiredMessage, MessageType.Error);

            if (influenceCollider is MeshCollider meshCollider && !meshCollider.convex)
                DrawWarning(FlowFieldModifierMaskBuilder.ConvexMeshRequiredMessage, MessageType.Error);

        }

        private static void DrawWarning(string message, MessageType messageType)
        {
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(message, messageType);
        }
    }
}
