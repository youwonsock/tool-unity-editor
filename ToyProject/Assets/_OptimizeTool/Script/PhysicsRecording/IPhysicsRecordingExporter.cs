using UnityEngine;

namespace Common.OptimizeTool.PhysicsRecording
{
    /// <summary>
    /// Saves takes as assets. Only the editor assembly registers one, through <see cref="PhysicsRecorder.Exporter"/>,
    /// so the recorder captures in any build but can start a take only in the Unity Editor.
    /// </summary>
    internal interface IPhysicsRecordingExporter
    {
        /// <summary>
        /// Returns the settings with the normalized output folder. Throws when the folder, the clip name or an
        /// existing output cannot be used.
        /// </summary>
        PhysicsTakeSettings Validate(in PhysicsTakeSettings settings);

        /// <summary>Called when a take starts; prefabs cannot be saved during a script reload, so reloads wait.</summary>
        void BeginTake();

        /// <summary>Called once for every <see cref="BeginTake"/>, however the take ends.</summary>
        void EndTake();

        /// <summary>Saves the clip, the playback prefab and, when enabled, the reference, or restores the previous files.</summary>
        PhysicsRecordingAssets Export(Transform root, PhysicsTake take, in PhysicsTakeSettings settings);
    }

    /// <summary>The persistent assets of one saved take; Reference is null when it was not saved.</summary>
    internal readonly struct PhysicsRecordingAssets
    {
        public AnimationClip Clip { get; }
        public GameObject Prefab { get; }
        public PhysicsRecordingReference Reference { get; }

        public PhysicsRecordingAssets(AnimationClip clip, GameObject prefab, PhysicsRecordingReference reference)
        {
            Clip = clip;
            Prefab = prefab;
            Reference = reference;
        }
    }
}
