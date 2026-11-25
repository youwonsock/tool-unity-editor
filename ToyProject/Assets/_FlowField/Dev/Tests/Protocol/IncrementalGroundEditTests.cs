using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// A ground-only setting edited in Play Mode rebuilds incrementally; the
    /// result must equal a fresh build of the same settings in every published
    /// array, escape directions on blocked cells included (kit s1).
    /// </summary>
    [TestFixture, Category("Protocol")]
    internal sealed class IncrementalGroundEditTests : FlowFieldPlayTestBase
    {
        [UnityTest, Timeout(300000)]
        public IEnumerator MaxStepHeight() => Run(FlowFieldProtocolScenes.MaxStepHeightPath, 0.05f, "1684/10000");

        [UnityTest, Timeout(300000)]
        public IEnumerator MaxSlope() => Run(FlowFieldProtocolScenes.MaxSlopePath, 20f, "1684/10000");

        private static IEnumerator Run(string path, float value, string recordedBlocked)
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            yield return FlowFieldPlayScene.WaitUntilSettled();
            FlowFieldManager manager = Manager;

            FlowFieldProtocolScenes.SetFloat(manager, path, value);
            yield return FlowFieldPlayScene.WaitSeconds(1f);
            yield return FlowFieldPlayScene.WaitUntilSettled(1f);
            FieldSnapshot incremental = FieldSnapshot.Take(manager);

            // Disabling stops the session; enabling builds everything again.
            manager.enabled = false;
            manager.enabled = true;
            yield return FlowFieldPlayScene.WaitSeconds(1f);
            yield return FlowFieldPlayScene.WaitUntilSettled(1f);
            FieldSnapshot fresh = FieldSnapshot.Take(manager);

            Assert.That(incremental.Goal, Is.EqualTo(fresh.Goal), "The resolved Goal moved.");
            string difference = FieldSnapshot.Difference(incremental, fresh);
            Assert.That(difference, Is.Empty, "The incremental field differs from a fresh build: " + difference);
            FlowFieldRecorded.AreEqual(recordedBlocked, fresh.BlockedSummary(), "s1 " + path + " blocked cells");
        }

        /// <summary>Copies of every published array, which a later pump may reuse.</summary>
        private sealed class FieldSnapshot
        {
            private readonly Dictionary<string, Array> _arrays = new Dictionary<string, Array>();
            private bool[] _blocked;

            internal Vector3 Goal { get; private set; }

            internal static FieldSnapshot Take(FlowFieldManager manager)
            {
                Assert.That(manager.TryGetPublishedField(out FlowFieldField field), Is.True, FlowFieldPlayScene.Describe());
                Assert.That(manager.TryGetFieldInfo(out FlowFieldFieldInfo info), Is.True, FlowFieldPlayScene.Describe());
                var snapshot = new FieldSnapshot { Goal = info.ResolvedGoalWorld };
                snapshot.Add(nameof(FlowFieldField.Blocked), field.Blocked);
                snapshot.Add(nameof(FlowFieldField.Topology), field.Topology);
                snapshot.Add(nameof(FlowFieldField.GoalFlags), field.GoalFlags);
                snapshot.Add(nameof(FlowFieldField.NextCells), field.NextCells);
                snapshot.Add(nameof(FlowFieldField.BaseDirections), field.BaseDirections);
                snapshot.Add(nameof(FlowFieldField.BaseSpeeds), field.BaseSpeeds);
                snapshot.Add(nameof(FlowFieldField.FinalDirections), field.FinalDirections);
                snapshot.Add(nameof(FlowFieldField.FinalSpeeds), field.FinalSpeeds);
                snapshot.Add(nameof(FlowFieldField.EscapeDirections), field.EscapeDirections);
                snapshot.Add(nameof(FlowFieldField.ModifierInfluence), field.ModifierInfluence);
                snapshot._blocked = (bool[])field.Blocked.Clone();
                return snapshot;
            }

            private void Add(string name, Array values)
            {
                if (values != null)
                    _arrays[name] = (Array)values.Clone();
            }

            /// <summary>Per array, how many elements differ on blocked and on open cells; empty when none do.</summary>
            internal static string Difference(FieldSnapshot left, FieldSnapshot right)
            {
                var text = new StringBuilder();
                foreach (KeyValuePair<string, Array> entry in left._arrays)
                {
                    if (!right._arrays.TryGetValue(entry.Key, out Array other))
                    {
                        text.Append(entry.Key + ": missing; ");
                        continue;
                    }
                    if (other.Length != entry.Value.Length)
                    {
                        text.Append(entry.Key + ": length " + entry.Value.Length + " vs " + other.Length + "; ");
                        continue;
                    }
                    int onBlocked = 0, onOpen = 0;
                    for (int i = 0; i < entry.Value.Length; i++)
                    {
                        if (Equals(entry.Value.GetValue(i), other.GetValue(i)))
                            continue;
                        if (i < right._blocked.Length && right._blocked[i])
                            onBlocked++;
                        else
                            onOpen++;
                    }
                    if (onBlocked + onOpen > 0)
                        text.Append(entry.Key + ": blocked=" + onBlocked + " open=" + onOpen + "; ");
                }
                foreach (string name in right._arrays.Keys)
                {
                    if (!left._arrays.ContainsKey(name))
                        text.Append(name + ": only in the fresh build; ");
                }
                return text.ToString();
            }

            internal string BlockedSummary()
            {
                int blocked = 0;
                foreach (bool cell in _blocked)
                {
                    if (cell)
                        blocked++;
                }
                return blocked + "/" + _blocked.Length;
            }
        }
    }
}
