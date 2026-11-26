using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// With no Goal, a Default Direction edit in Play Mode must update the base
    /// and the final direction of every Default Direction cell, and editing it
    /// back must restore the first field (kit f3).
    /// </summary>
    /// <remarks>
    /// The kit's note said both counts equal the open cells. That holds for
    /// -X; for +Z, cells whose surface tilts along Z get the Default Direction
    /// projected onto the surface, which is not exactly +Z. So the +Z counts
    /// are the recorded ones, and the rule asserted is that base and final
    /// agree and that the edit back restores them.
    /// </remarks>
    [TestFixture, Category("Protocol")]
    internal sealed class DefaultDirectionEditTests : FlowFieldPlayTestBase
    {
        private const string SceneBuildCounts
            = "before open=8316 base=8188 final=8188 | -X open=8316 base=8316 final=8316 | +Z open=8316 base=8188 final=8188";
        // The baked Goal reaches every open cell, so none follows the Default Direction.
        private const string StaticBakedCounts
            = "before open=0 base=0 final=0 | -X open=0 base=0 final=0 | +Z open=0 base=0 final=0";

        [UnityTest, Timeout(300000)]
        public IEnumerator RuntimeDynamic() => Run(FlowFieldBakeMode.RuntimeDynamic, SceneBuildCounts);

        [UnityTest, Timeout(300000)]
        public IEnumerator StaticBaked() => Run(FlowFieldBakeMode.StaticBaked, StaticBakedCounts);

        [UnityTest, Timeout(300000)]
        public IEnumerator BakedBase() => Run(FlowFieldBakeMode.BakedBase, SceneBuildCounts);

        private static IEnumerator Run(FlowFieldBakeMode bakeMode, string recorded)
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, bakeMode);
            yield return FlowFieldPlayScene.WaitUntilSettled();
            FlowFieldManager manager = Manager;
            // A StaticBaked field keeps its baked Goal.
            if (bakeMode != FlowFieldBakeMode.StaticBaked)
            {
                manager.SetGoal(FlowFieldGoalRequest.None);
                yield return FlowFieldPlayScene.WaitUntilSettled(0.5f);
            }

            Counts before = Count(manager);
            FlowFieldProtocolScenes.SetVector(manager, FlowFieldProtocolScenes.DefaultDirectionPath, FlowFieldProtocolScenes.MinusX);
            yield return FlowFieldPlayScene.WaitUntilSettled(0.5f);
            Counts minusX = Count(manager);
            FlowFieldProtocolScenes.SetVector(manager, FlowFieldProtocolScenes.DefaultDirectionPath, FlowFieldProtocolScenes.PlusZ);
            yield return FlowFieldPlayScene.WaitUntilSettled(0.5f);
            Counts plusZ = Count(manager);

            Assert.That(before.Base, Is.EqualTo(before.Final), "before: base and final disagree: " + before);
            Assert.That(minusX.Base, Is.EqualTo(minusX.Open), "-X: base missed Default Direction cells: " + minusX);
            Assert.That(minusX.Final, Is.EqualTo(minusX.Open), "-X: final missed Default Direction cells: " + minusX);
            Assert.That(plusZ.ToString(), Is.EqualTo(before.ToString()), "The edit back to +Z did not restore the field.");
            FlowFieldRecorded.AreEqual(recorded, "before " + before + " | -X " + minusX + " | +Z " + plusZ, "f3 " + bakeMode);
        }

        // The kit's count: open cells without a next cell, and how many of them
        // point along the serialized Default Direction in each array.
        private static Counts Count(FlowFieldManager manager)
        {
            Vector3 direction = FlowFieldProtocolScenes.GetVector(manager, FlowFieldProtocolScenes.DefaultDirectionPath).normalized;
            Assert.That(manager.TryGetPublishedField(out FlowFieldField field), Is.True, FlowFieldPlayScene.Describe());
            int open = 0, baseMatches = 0, finalMatches = 0;
            for (int i = 0; i < field.Goal.NextCells.Length; i++)
            {
                if (field.Agent.Blocked[i] || field.Goal.NextCells[i] != FlowFieldNextCell.DefaultDirection)
                    continue;
                open++;
                if (Vector3.Distance(field.Goal.BaseDirections[i], direction) < 1e-3f)
                    baseMatches++;
                if (Vector3.Distance(field.Goal.FinalDirections[i], direction) < 1e-3f)
                    finalMatches++;
            }
            return new Counts(open, baseMatches, finalMatches);
        }

        private readonly struct Counts
        {
            internal readonly int Open;
            internal readonly int Base;
            internal readonly int Final;

            internal Counts(int open, int baseMatches, int finalMatches)
            {
                Open = open;
                Base = baseMatches;
                Final = finalMatches;
            }

            public override string ToString() => "open=" + Open + " base=" + Base + " final=" + Final;
        }
    }
}
