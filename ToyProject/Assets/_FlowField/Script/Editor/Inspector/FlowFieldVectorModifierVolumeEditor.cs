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
                DrawWarning("FlowField Manager를 지정해야 Modifier가 등록됩니다.", MessageType.Error);
                return;
            }

            Collider influenceCollider = modifier.InfluenceCollider;
            if (influenceCollider == null)
            {
                DrawWarning("Influence Collider를 지정해야 합니다.", MessageType.Error);
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
