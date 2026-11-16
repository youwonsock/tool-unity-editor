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
        [SerializeField, Tooltip("Bake 표면, Obstacle, Goal, Modifier 영향 셀과 최종 3D 벡터를 표시합니다.")]
        private bool _showField = true;
        [SerializeField] private FlowFieldVolumeGizmoMode _volumeGizmoMode = FlowFieldVolumeGizmoMode.FullVolume;
        [SerializeField] private bool _showVolumeCells;
        [SerializeField] private bool _showVolumeVectors = true;
        [SerializeField] private FlowFieldVolumeSliceAxis _volumeGizmoSliceAxis = FlowFieldVolumeSliceAxis.Y;
        [SerializeField, Tooltip("-1이면 단면 중앙을 사용합니다.")] private int _volumeGizmoSlice = -1;

        public bool ShowField => _showField;
        public FlowFieldVolumeGizmoMode VolumeGizmoMode => _volumeGizmoMode;
        public bool ShowVolumeCells => _showVolumeCells;
        public bool ShowVolumeVectors => _showVolumeVectors;
        public FlowFieldVolumeSliceAxis VolumeGizmoSliceAxis => _volumeGizmoSliceAxis;
        public int VolumeGizmoSlice => _volumeGizmoSlice;
    }
}
