using UnityEngine;

namespace Common.OptimizeTool.Samples
{
    internal static class OptimizeToolPhysicsStatusFormatter
    {
        public static string Format(OptimizeToolPhysicsDemo demo)
        {
            bool comparable = demo.SavedPrefab != null && demo.HasRecordingReference;
            string saved = demo.SavedPrefab == null ? "No recording yet"
                : demo.HasRecordingReference ? "Saved recording ready" : "Recording reference missing: RECORD a new take.";
            bool recording = demo.State == OptimizeToolPhysicsDemo.DemoState.Recording;
            bool failed = demo.State == OptimizeToolPhysicsDemo.DemoState.Error;
            bool recordingFailed = failed && demo.FailedDuringRecording;
            string source = recording
                ? $"LEFT   LIVE RECORDING    {demo.SourceBodyCount} Rigidbodies"
                : failed ? (recordingFailed ? "LEFT   RECORDING FAILED  physics held" : "LEFT   PLAYBACK FAILED   physics held")
                : demo.HasRecordingReference ? "LEFT   RECORDED ORIGINAL  physics held" : "LEFT   REFERENCE MISSING  physics held";
            string comparison = recording ? "Capturing original physics observations...\n"
                : failed ? (demo.SavedPrefab == null ? "No saved take available: RECORD to retry.\n"
                    : comparable
                    ? "Previous saved take available: COMPARE or RECORD again.\n"
                    : "RECORD a new take before comparing.\n")
                : comparable
                ? $"Peak recorded-pose delta: {demo.MaxPositionError:0.000000} m / {demo.MaxRotationError:0.000} deg\n"
                    + $"Checked {demo.ComparisonSampleCount} / {demo.RecordingReference.ObservationCount} original physics observations.\n"
                : "RECORD a take to create its required comparison reference.\n";
            AnimationClip clip = demo.SavedPrefab != null ? demo.SavedPrefab.GetComponent<Animation>()?.clip : null;
            float duration = recording || recordingFailed || clip == null ? demo.Duration : clip.length;
            return $"{demo.State.ToString().ToUpperInvariant()}   {demo.Elapsed:0.00} / {duration:0.00} s\n"
                + source + "\nRIGHT  RECORDED PREFAB    0 Rigidbodies\n\n"
                + (!string.IsNullOrEmpty(demo.LastError) ? (recordingFailed ? "Recording failed: " : "Playback failed: ") + demo.LastError : saved) + "\n"
                + $"RECORD: still for {demo.ReleaseDelay:0.0} s, then real physics.\n"
                + (comparable
                    ? "COMPARE: original observations and Prefab at the same time.\n"
                    : "COMPARE requires the saved take and its recording reference.\n")
                + comparison
                + (Application.isEditor
                    ? failed ? (recordingFailed ? "The failed attempt was not saved." : "Playback did not change the saved take.") : "Saved Prefabs replay without physics calculations."
                    : "Recording requires the Unity Editor.");
        }
    }
}
