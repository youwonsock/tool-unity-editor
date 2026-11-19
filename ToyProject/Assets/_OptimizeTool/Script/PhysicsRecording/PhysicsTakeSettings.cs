using System;

namespace Common.OptimizeTool.PhysicsRecording
{
    /// <summary>
    /// The output, sampling and key reduction settings of one take, fixed when it starts. Values are checked here;
    /// asset path rules belong to the editor exporter.
    /// </summary>
    internal readonly struct PhysicsTakeSettings
    {
        public readonly string Folder, ClipName;
        public readonly bool Overwrite, SaveReference, OmitStillTracks;
        public readonly float PositionTolerance, RotationTolerance, SampleInterval;
        public readonly PhysicsClipFormat ClipFormat;

        public PhysicsTakeSettings(string folder, string clipName, bool overwrite, bool saveReference,
            float positionTolerance, float rotationTolerance, float sampleInterval, bool omitStillTracks,
            PhysicsClipFormat clipFormat)
        {
            if (!float.IsFinite(positionTolerance) || positionTolerance < 0f)
                throw new ArgumentOutOfRangeException(nameof(positionTolerance));
            if (!float.IsFinite(rotationTolerance) || rotationTolerance < 0f)
                throw new ArgumentOutOfRangeException(nameof(rotationTolerance));
            if (!float.IsFinite(sampleInterval) || sampleInterval < 0f)
                throw new ArgumentOutOfRangeException(nameof(sampleInterval));
            if (!Enum.IsDefined(typeof(PhysicsClipFormat), clipFormat))
                throw new ArgumentOutOfRangeException(nameof(clipFormat));
            Folder = folder;
            ClipName = clipName;
            Overwrite = overwrite;
            SaveReference = saveReference;
            PositionTolerance = positionTolerance;
            RotationTolerance = rotationTolerance;
            SampleInterval = sampleInterval;
            OmitStillTracks = omitStillTracks;
            ClipFormat = clipFormat;
        }

        public string ClipPath => $"{Folder}/{ClipName}.anim";
        public string PrefabPath => $"{Folder}/{ClipName}.prefab";
        public string ReferencePath => PhysicsRecordingReference.AssetPathForClip(ClipPath);

        /// <summary>The same settings writing to <paramref name="folder"/>, such as the exporter's normalized path.</summary>
        public PhysicsTakeSettings WithFolder(string folder) => new PhysicsTakeSettings(folder, ClipName, Overwrite,
            SaveReference, PositionTolerance, RotationTolerance, SampleInterval, OmitStillTracks, ClipFormat);
    }
}
