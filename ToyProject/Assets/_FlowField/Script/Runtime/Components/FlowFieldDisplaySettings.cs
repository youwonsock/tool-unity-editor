using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Scene View display options for the <see cref="FlowFieldManager"/> on the
    /// same GameObject. Only Editor code reads them, and they never change what
    /// the Manager computes. Without this component the field is not drawn.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(FlowFieldManager))]
    [AddComponentMenu("FlowField/FlowField Display Settings")]
    public sealed class FlowFieldDisplaySettings : MonoBehaviour
    {
        [SerializeField, Tooltip("Shows the baked surface, obstacles, the Goal, Modifier cells and the final 3D vectors.")]
        private bool _showField = true;
        [SerializeField] private FlowFieldVolumeGizmoMode _volumeGizmoMode = FlowFieldVolumeGizmoMode.FullVolume;
        [SerializeField] private bool _showVolumeCells;
        [SerializeField] private bool _showVolumeVectors = true;
        [SerializeField] private FlowFieldVolumeSliceAxis _volumeGizmoSliceAxis = FlowFieldVolumeSliceAxis.Y;
        [SerializeField, Tooltip("-1 uses the middle slice.")] private int _volumeGizmoSlice = -1;

        /// <summary>Serialized property paths, for SerializedObject access.</summary>
        internal static class PropertyNames
        {
            internal const string ShowField = nameof(_showField);
            internal const string VolumeGizmoMode = nameof(_volumeGizmoMode);
            internal const string ShowVolumeCells = nameof(_showVolumeCells);
            internal const string ShowVolumeVectors = nameof(_showVolumeVectors);
            internal const string VolumeGizmoSliceAxis = nameof(_volumeGizmoSliceAxis);
            internal const string VolumeGizmoSlice = nameof(_volumeGizmoSlice);
        }

        public bool ShowField => _showField;
        public FlowFieldVolumeGizmoMode VolumeGizmoMode => _volumeGizmoMode;
        public bool ShowVolumeCells => _showVolumeCells;
        public bool ShowVolumeVectors => _showVolumeVectors;
        public FlowFieldVolumeSliceAxis VolumeGizmoSliceAxis => _volumeGizmoSliceAxis;
        public int VolumeGizmoSlice => _volumeGizmoSlice;
    }
}
