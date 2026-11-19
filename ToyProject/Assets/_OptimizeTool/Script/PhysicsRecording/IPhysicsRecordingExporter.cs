using UnityEngine;

namespace Common.OptimizeTool.PhysicsRecording
{
    /// <summary>
    /// The editor side of a take. Only the editor assembly registers one, through <see cref="PhysicsRecorder.Exporter"/>,
    /// so the recorder can start a take only in the Unity Editor.
    /// </summary>
    internal interface IPhysicsRecordingExporter
    {
        /// <summary>
        /// Returns the settings with the normalized output folder. Throws when the folder, the clip name or an
        /// output path cannot be used.
        /// </summary>
        PhysicsTakeSettings Validate(in PhysicsTakeSettings settings);

        /// <summary>Called when a take starts; prefabs cannot be saved during a script reload, so reloads wait.</summary>
        void BeginTake();

        /// <summary>Called once for every <see cref="BeginTake"/>, however the take ends, after any save.</summary>
        void EndTake();

        /// <summary>
        /// Saves the clip, the playback prefab for a Legacy take and, when enabled, the reference. If any of them fails,
        /// the previous files are restored and the error is thrown.
        /// </summary>
        PhysicsRecordingAssets Export(Transform root, PhysicsTake take, in PhysicsTakeSettings settings);
    }

    /// <summary>
    /// The persistent assets of one saved take and its metrics. Prefab is null for a Generic clip, and Reference when
    /// it was not saved.
    /// </summary>
    internal readonly struct PhysicsRecordingAssets
    {
        public AnimationClip Clip { get; }
        public GameObject Prefab { get; }
        public PhysicsRecordingReference Reference { get; }
        public PhysicsRecordingMetrics Metrics { get; }

        public PhysicsRecordingAssets(AnimationClip clip, GameObject prefab, PhysicsRecordingReference reference,
            PhysicsRecordingMetrics metrics)
        {
            Clip = clip;
            Prefab = prefab;
            Reference = reference;
            Metrics = metrics;
        }
    }
}
