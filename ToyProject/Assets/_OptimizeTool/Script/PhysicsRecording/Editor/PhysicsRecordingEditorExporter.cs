using UnityEditor;
using UnityEngine;

namespace Common.OptimizeTool.PhysicsRecording
{
    /// <summary>Registers the editor's asset exporter with <see cref="PhysicsRecorder"/> on every script load.</summary>
    [InitializeOnLoad]
    internal sealed class PhysicsRecordingEditorExporter : IPhysicsRecordingExporter
    {
        static PhysicsRecordingEditorExporter() => PhysicsRecorder.Exporter = new PhysicsRecordingEditorExporter();

        public void Validate(in PhysicsTakeSettings settings)
        {
            AssetFileTransaction.ValidateFolder(settings.Folder);
            AssetFileTransaction.ValidateFileName(settings.Name);
            PhysicsRecordingAssetWriter.ValidateTargets(settings);
        }

        public void BeginTake() => EditorApplication.LockReloadAssemblies();

        public void EndTake() => EditorApplication.UnlockReloadAssemblies();

        public PhysicsRecordingAssets Export(Transform root, PhysicsTake take, in PhysicsTakeSettings settings) =>
            PhysicsRecordingAssetWriter.Write(root, take, settings);
    }
}
