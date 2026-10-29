using NUnit.Framework;
using UnityEngine;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// v6 bake storage round trips: Volume vectors rebuilt from NextCells and
    /// escape faces, Surface vectors packed as bytes.
    /// </summary>
    public sealed class FlowFieldBakeFormatTests
    {
        [Test]
        public void VolumeBakeRoundTripRebuildsDefaultDirectedAndEscapeVectors()
        {
            using var scenario = new FlowFieldScenarioFixture.VolumeScenario(9, 6, 8)
            {
                Goal = new Vector3(7.5f, 1.5f, 6.5f),
                GoalInfluenceRadius = 4f,
                DefaultDirection = new Vector3(1f, 0f, 1f),
            };
            scenario.LayerObstacle(new Vector3(4f, 2f, 4f), new Vector3(2f, 4f, 2f));
            scenario.Build();
            Assert.That(scenario.Session.TryExport(out FlowFieldVolumeRequest request, out bool[] blocked,
                out uint[] topology, out FlowFieldGoalFlags[] goalFlags, out int[] next, out Vector3[] directions,
                out float[] speeds, out Vector3[] escape), Is.True);
            int goalIndex = -1;
            for (int index = 0; index < next.Length; index++)
                if (next[index] == index) goalIndex = index;
            Assert.That(System.Array.IndexOf(next, -1), Is.GreaterThanOrEqualTo(0), "Scene must contain default-direction cells.");
            Assert.That(System.Array.Exists(escape, value => value != Vector3.zero), Is.True, "Scene must contain escape directions.");

            var data = ScriptableObject.CreateInstance<FlowFieldVolumeBakeData>();
            var copy = ScriptableObject.CreateInstance<FlowFieldVolumeBakeData>();
            try
            {
                data.Apply(request.Grid, request.WorldBounds, request.ObstacleLayer, request.ObstacleClearance,
                    true, request.GoalWorld, request.GoalInfluenceRadius, goalIndex,
                    blocked, topology, goalFlags, next, directions, speeds, escape);
                JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(data), copy);

                Assert.That(copy.FormatVersion, Is.EqualTo(FlowFieldVolumeBakeData.CURRENT_FORMAT_VERSION));
                Assert.That(copy.TryGetValidationView(out FlowFieldVolumeBakeValidationView view), Is.True);
                Assert.That(view.NextCells, Is.EqualTo(next));
                for (int index = 0; index < next.Length; index++)
                {
                    Assert.That((view.Directions[index] - directions[index]).sqrMagnitude, Is.Zero, $"direction {index}");
                    Assert.That(view.Speeds[index], Is.EqualTo(speeds[index]), $"speed {index}");
                    Assert.That((view.EscapeDirections[index] - escape[index]).sqrMagnitude, Is.Zero, $"escape {index}");
                }
            }
            finally
            {
                Object.DestroyImmediate(data);
                Object.DestroyImmediate(copy);
            }
        }

        [Test]
        public void BlobRoundTripsBitsAndRejectsPartialElements()
        {
            var vectors = new[] { new Vector3(0.1f, -2.5f, 1e-7f), new Vector3(float.MaxValue, 0f, -0f) };
            Assert.That(FlowFieldBakeBlob.Unpack<Vector3>(FlowFieldBakeBlob.Pack(vectors)), Is.EqualTo(vectors));
            var floats = new[] { 0.3f, -1f, 123.456f };
            Assert.That(FlowFieldBakeBlob.Unpack<float>(FlowFieldBakeBlob.Pack(floats)), Is.EqualTo(floats));
            Assert.That(FlowFieldBakeBlob.Unpack<Vector3>(new byte[13]), Is.Null);
            Assert.That(FlowFieldBakeBlob.Unpack<float>(System.Array.Empty<byte>()), Is.Empty);
        }
    }
}
