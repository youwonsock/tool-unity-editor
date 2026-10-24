#if UNITY_EDITOR
namespace Common.OptimizeTool
{
    internal sealed class PhysicsRecordingSettings
    {
        public readonly float Interval, PositionThreshold, RotationThreshold;
        public readonly bool OnlyIfMoved, Overwrite;
        public readonly string Folder, Name;

        public PhysicsRecordingSettings(float interval, float positionThreshold, float rotationThreshold,
            bool onlyIfMoved, bool overwrite, string folder, string name)
        {
            Interval = interval;
            PositionThreshold = positionThreshold;
            RotationThreshold = rotationThreshold;
            OnlyIfMoved = onlyIfMoved;
            Overwrite = overwrite;
            Folder = folder;
            Name = name;
        }
        public string ClipPath => $"{Folder}/{Name}.anim";
        public string PrefabPath => $"{Folder}/{Name}.prefab";
        public string ReferencePath => PhysicsRecordingReference.AssetPathForClip(ClipPath);
    }
}
#endif
