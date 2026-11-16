namespace Common.FlowField.Editor
{
    /// <summary>
    /// The display options of a Manager, with the defaults used when it has no
    /// <see cref="FlowFieldDisplaySettings"/> component.
    /// </summary>
    internal readonly struct FlowFieldDisplay
    {
        internal bool ShowField { get; }
        internal FlowFieldVolumeGizmoMode VolumeGizmoMode { get; }
        internal bool ShowVolumeCells { get; }
        internal bool ShowVolumeVectors { get; }
        internal FlowFieldVolumeSliceAxis VolumeGizmoSliceAxis { get; }
        internal int VolumeGizmoSlice { get; }

        private FlowFieldDisplay(FlowFieldDisplaySettings settings)
        {
            ShowField = settings != null && settings.ShowField;
            VolumeGizmoMode = settings != null ? settings.VolumeGizmoMode : FlowFieldVolumeGizmoMode.FullVolume;
            ShowVolumeCells = settings != null && settings.ShowVolumeCells;
            ShowVolumeVectors = settings == null || settings.ShowVolumeVectors;
            VolumeGizmoSliceAxis = settings != null ? settings.VolumeGizmoSliceAxis : FlowFieldVolumeSliceAxis.Y;
            VolumeGizmoSlice = settings != null ? settings.VolumeGizmoSlice : -1;
        }

        internal static FlowFieldDisplay Of(FlowFieldManager manager)
            => new FlowFieldDisplay(manager != null ? manager.GetComponent<FlowFieldDisplaySettings>() : null);
    }
}
