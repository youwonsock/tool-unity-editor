using System;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Common.FlowField.Editor;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// Runs a Manager's bake in Edit Mode through the same bake sessions the
    /// Editor uses, and turns the result into the asset's serialized text,
    /// without writing an asset. The per-save revision, the owner and the asset name
    /// are left out, so a bake compares with the committed asset of the same scene.
    /// </summary>
    internal static class FlowFieldBakeExport
    {
        private static readonly Regex Volatile = new Regex("\"(_revision|_ownerId|m_Name)\":(\\d+|\"[^\"]*\"),?");

        internal static string Export(FlowFieldManager manager)
            => manager.SpaceMode == FlowFieldSpaceMode.Volume3D ? ExportVolume(manager) : ExportSurface(manager);

        /// <summary>The serialized text of an existing asset, in the same form.</summary>
        internal static string Describe(ScriptableObject asset) => Strip(EditorJsonUtility.ToJson(asset));

        internal static string Hash(string text)
        {
            ulong hash = 1469598103934665603UL;
            foreach (byte value in Encoding.UTF8.GetBytes(text))
            {
                hash ^= value;
                hash *= 1099511628211UL;
            }
            return hash.ToString("X16");
        }

        private static string ExportSurface(FlowFieldManager manager)
        {
            FlowFieldSurfaceSignature signature = manager.CreateSurfaceSignature();
            var scan = new FlowFieldSurfaceGroundScan(signature.Surface);
            while (!scan.IsComplete)
                scan.Step(double.MaxValue);
            Assert.That(scan.IsValid, Is.True, scan.Error);

            FlowFieldSession session = FlowFieldBakeService.CreateSurfaceBakeSession(
                manager, in signature, scan.Result, out FlowFieldSurfaceBuilder builder);
            var asset = ScriptableObject.CreateInstance<FlowFieldSurfaceBakeData>();
            try
            {
                PumpToEnd(session);
                Assert.That(builder.TryExportBakeData(out FlowFieldSurfaceData surface, out FlowFieldSurfaceWorkspace workspace), Is.True);
                bool hasGoal = workspace.HasActiveGoal && workspace.ResolvedGoalIndex >= 0;
                FlowFieldSurfaceGoalResolution goal = manager.ResolveConfiguredGoal(signature.Surface.Grid);
                asset.Apply(signature, surface, hasGoal, goal.RequestedWorld, goal.InfluenceRadius,
                    hasGoal ? workspace.ResolvedGoalIndex : -1, workspace);
                return Describe(asset);
            }
            finally
            {
                session.Dispose();
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        private static string ExportVolume(FlowFieldManager manager)
        {
            FlowFieldSession session = FlowFieldBakeService.CreateVolumeBakeSession(manager, out FlowFieldVolumeBuilder builder);
            var asset = ScriptableObject.CreateInstance<FlowFieldVolumeBakeData>();
            try
            {
                PumpToEnd(session);
                Assert.That(builder.TryExportBakeData(out FlowFieldVolumeBakePayload payload), Is.True);
                asset.Apply(in payload);
                return Describe(asset);
            }
            finally
            {
                session.Dispose();
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        private static void PumpToEnd(FlowFieldSession session)
        {
            for (int pump = 0; pump < 10000 && session.IsBuilding; pump++)
                session.Pump();
            Assert.That(session.IsFaulted, Is.False, session.LastError);
            Assert.That(session.IsBuilding, Is.False, "The bake session never finished.");
            Assert.That(session.IsReady, Is.True);
        }

        private static string Strip(string json) => Volatile.Replace(json, string.Empty);
    }
}
