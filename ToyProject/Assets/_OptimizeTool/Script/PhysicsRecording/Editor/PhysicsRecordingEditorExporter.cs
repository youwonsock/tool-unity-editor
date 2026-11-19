using Common.OptimizeTool.Editor;
using UnityEditor;
using UnityEngine;

namespace Common.OptimizeTool.PhysicsRecording.Editor
{
    /// <summary>Registers the editor's asset exporter with <see cref="PhysicsRecorder"/> on every script load.</summary>
    [InitializeOnLoad]
    internal sealed class PhysicsRecordingEditorExporter : IPhysicsRecordingExporter
    {
        static PhysicsRecordingEditorExporter() => PhysicsRecorder.Exporter = new PhysicsRecordingEditorExporter();

        public PhysicsTakeSettings Validate(in PhysicsTakeSettings settings)
        {
            PhysicsTakeSettings validated = settings.WithFolder(AssetFileTransaction.ValidateFolder(settings.Folder));
            AssetFileTransaction.ValidateFileName(validated.ClipName);
            PhysicsRecordingAssetWriter.ValidateTargets(validated);
            return validated;
        }

        public void BeginTake() => EditorApplication.LockReloadAssemblies();

        public void EndTake() => EditorApplication.UnlockReloadAssemblies();

        public PhysicsRecordingAssets Export(Transform root, PhysicsTake take, in PhysicsTakeSettings settings) =>
            PhysicsRecordingAssetWriter.Write(root, take, settings);
    }
}
