using System;

namespace Common.OptimizeTool.PhysicsRecording
{
    /// <summary>
    /// The output and key reduction settings of one take, fixed when it starts. Tolerances are checked here;
    /// asset path rules belong to the editor exporter.
    /// </summary>
    internal readonly struct PhysicsTakeSettings
    {
        public readonly string Folder, ClipName;
        public readonly bool Overwrite, SaveReference;
        public readonly float PositionTolerance, RotationTolerance;

        public PhysicsTakeSettings(string folder, string clipName, bool overwrite, bool saveReference,
            float positionTolerance, float rotationTolerance)
        {
            if (!PhysicsPoseMath.IsFinite(positionTolerance) || positionTolerance < 0f)
                throw new ArgumentOutOfRangeException(nameof(positionTolerance));
            if (!PhysicsPoseMath.IsFinite(rotationTolerance) || rotationTolerance < 0f)
                throw new ArgumentOutOfRangeException(nameof(rotationTolerance));
            Folder = folder;
            ClipName = clipName;
            Overwrite = overwrite;
            SaveReference = saveReference;
            PositionTolerance = positionTolerance;
            RotationTolerance = rotationTolerance;
        }

        public string ClipPath => $"{Folder}/{ClipName}.anim";
        public string PrefabPath => $"{Folder}/{ClipName}.prefab";
        public string ReferencePath => PhysicsRecordingReference.AssetPathForClip(ClipPath);

        /// <summary>The same settings writing to <paramref name="folder"/>, such as the exporter's normalized path.</summary>
        public PhysicsTakeSettings WithFolder(string folder) =>
            new PhysicsTakeSettings(folder, ClipName, Overwrite, SaveReference, PositionTolerance, RotationTolerance);
    }
}
