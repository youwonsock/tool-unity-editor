using System;
using System.Collections.Generic;
using UnityEngine;
using NUnit.Framework;

namespace Common.FlowField.Tests
{
    internal static class FlowFieldRegressionFixture
    {
        internal const int Layer = 29;
        internal static readonly FlowFieldGridSpace Grid = FlowFieldGridSpace.FromCellGrid(Vector3.zero, 3, 3, 1f);
        internal static readonly FlowFieldSurfaceBakeSettings Settings = new FlowFieldSurfaceBakeSettings(
            Grid, new Bounds(new Vector3(1.5f, 0f, 1.5f), new Vector3(3f, 4f, 3f)), 1 << Layer, 45f, 0.5f);

        internal static CountingSurfaceSource Surface()
        {
            var result = new FlowFieldSurfaceBakeResult(Grid.CellCount);
            for (int i = 0; i < Grid.CellCount; i++) result.SetSurface(i, 0f, Vector3.up);
            for (int i = 0; i < Grid.CellCount; i++)
            {
                Grid.FromFlatIndex(i, out int x, out int z);
                FlowFieldSurfaceBaker.BuildNeighborMask(Settings, result, i, x, z);
            }
            return new CountingSurfaceSource(FlowFieldSurfaceData.FromRuntime(Settings, result, 1));
        }

        internal sealed class CountingSurfaceSource : IFlowFieldSurfaceSource
        {
            private readonly FlowFieldFixedSurfaceSource _source;
            internal int BuildCount { get; private set; }
            internal CountingSurfaceSource(FlowFieldSurfaceData surface)
                => _source = new FlowFieldFixedSurfaceSource(surface);
            public FlowFieldSurfaceData Build(in FlowFieldSurfaceBakeSettings settings)
            {
                BuildCount++;
                return _source.Build(settings);
            }
        }

        internal static FlowFieldSessionRequest Request(Vector3? goal = null, Vector3? direction = null)
            => FlowFieldSessionRequest.ForSceneBuild(Settings, 1 << Layer, 1f, 0.5f, 0f, false,
                goal.HasValue ? FlowFieldGoalPipeline.Resolve(Grid, null, true, goal.Value, 0f) : FlowFieldGoalResolution.None,
                direction ?? Vector3.forward, FlowFieldDirtyFlags.None, FlowFieldCellRect.Invalid,
                FlowFieldCellRect.Invalid);

        // Production event boundaries isolate subscriber exceptions. Assert
        // from the test owner after pumping/disposal, never from EventFailed.
        internal sealed class EventFailures
        {
            private readonly List<Exception> _failures = new List<Exception>();

            internal EventFailures(FlowFieldSessionBase session)
                => session.EventFailed += _failures.Add;

            internal void AssertNone()
                => Assert.That(_failures, Is.Empty,
                    "A session event callback failed: " + string.Join("\n", _failures));
        }

        // A solve runs on a worker thread, so the scheduler can need real time.
        internal static void PumpSchedulerUntil(Func<bool> done)
        {
            double deadline = UnityEditor.EditorApplication.timeSinceStartup + 10d;
            while (!done() && UnityEditor.EditorApplication.timeSinceStartup < deadline)
            {
                FlowFieldBuildScheduler.PumpAll(10d);
                System.Threading.Thread.Sleep(1);
            }
            Assert.That(done(), Is.True, "The scheduler did not finish in time.");
        }

        internal sealed class DeferredBackend : IFlowFieldSolveBackend
        {
            internal sealed class Pending
            {
                internal FlowFieldBfsRequest Request;
                internal Action<FlowFieldBfsRequest> Completed;
                internal Action<FlowFieldBfsRequest, Exception> Failed;
                internal bool Cancelled;
            }

            internal readonly List<Pending> Calls = new List<Pending>();
            public bool StartBfs(in FlowFieldBfsRequest request, Action<FlowFieldBfsRequest> completed,
                Action<FlowFieldBfsRequest, Exception> failed)
            {
                Calls.Add(new Pending { Request = request, Completed = completed, Failed = failed });
                return true;
            }

            public void Poll(bool wait) { }

            public void Cancel()
            {
                foreach (Pending call in Calls)
                    call.Cancelled = true;
            }

            // A cancelled solve writes nothing, but a late callback may still arrive.
            internal void Complete(int index, bool solve = true)
            {
                Pending call = Calls[index];
                if (solve && !call.Cancelled) FlowFieldSurfaceBuildPipeline.SolveNow(call.Request);
                call.Completed(call.Request);
            }

            internal void Fail(int index) => Calls[index].Failed(Calls[index].Request, new InvalidOperationException("Simulated solve failure"));
            public void Dispose() { } // Retain callbacks to exercise late completion after release.
        }

        internal sealed class Modifier : IFlowFieldVectorModifier
        {
            public Collider InfluenceCollider { get; set; }
            public int Priority { get; set; }
            public int Revision { get; set; }
            public FlowFieldModifierObservation Observe() => new FlowFieldModifierObservation(InfluenceCollider, Priority, Revision);
            internal float Speed = 2f;
            public IFlowFieldModifierSnapshot CaptureSnapshot() => new Snapshot(Speed);
            private sealed class Snapshot : IFlowFieldModifierSnapshot
            {
                private readonly float _speed;
                internal Snapshot(float speed) { _speed = speed; }
                public FlowFieldVectorState Modify(in FlowFieldVectorState current, in FlowFieldVectorModifierContext context)
                    => new FlowFieldVectorState(current.Direction, current.SpeedMultiplier * _speed);
            }
        }
    }
}
