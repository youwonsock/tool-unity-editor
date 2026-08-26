using Supercent.Common.FlowField;
using UnityEditor;
using UnityEngine;

namespace Supercent.Common.FlowField.Editor
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

            FlowFieldManager manager = modifier.FlowFieldManager;
            if (manager != null && manager.HasDuplicateVectorModifierPriority(modifier))
            {
                DrawWarning(
                    $"같은 Manager에 Priority {modifier.Priority}인 Modifier가 있습니다. "
                    + "결정적 순서가 필요하면 고유 Priority를 지정하세요.",
                    MessageType.Warning);
            }
        }

        private static void DrawWarning(string message, MessageType messageType)
        {
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(message, messageType);
        }
    }
}
