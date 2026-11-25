using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// A copy of every published array, so two fields can be compared cell by
    /// cell and a mismatch named by array, count and first differing cell.
    /// </summary>
    internal sealed class FlowFieldFieldSnapshot
    {
        private static readonly string[] Names =
        {
            "Blocked", "Topology", "GoalFlags", "NextCells", "BaseDirections", "BaseSpeeds",
            "FinalDirections", "FinalSpeeds", "EscapeDirections", "ModifierInfluence",
        };

        private readonly Dictionary<string, Array> _arrays = new Dictionary<string, Array>();
        private readonly string _goal;

        private FlowFieldFieldSnapshot(FlowFieldField field)
        {
            Array[] arrays =
            {
                field.Blocked, field.Topology, field.GoalFlags, field.NextCells, field.BaseDirections, field.BaseSpeeds,
                field.FinalDirections, field.FinalSpeeds, field.EscapeDirections, field.ModifierInfluence,
            };
            for (int i = 0; i < Names.Length; i++)
                _arrays[Names[i]] = arrays[i] == null ? null : (Array)arrays[i].Clone();
            _goal = "goal=" + field.HasActiveGoal + "@" + field.GoalIndex;
        }

        internal static FlowFieldFieldSnapshot Capture(FlowFieldManager manager)
        {
            Assert.That(manager.TryGetPublishedField(out FlowFieldField field), Is.True, FlowFieldPlayScene.Describe());
            return new FlowFieldFieldSnapshot(field);
        }

        /// <summary>Empty when both fields read the same everywhere.</summary>
        internal string Difference(FlowFieldFieldSnapshot other)
        {
            var text = new StringBuilder();
            if (_goal != other._goal)
                text.Append(_goal + " vs " + other._goal + "; ");
            foreach (string name in Names)
            {
                Array left = _arrays[name];
                Array right = other._arrays[name];
                if (left == null || right == null)
                {
                    if (left != right)
                        text.Append(name + " present in one only; ");
                    continue;
                }
                if (left.Length != right.Length)
                {
                    text.Append(name + " length " + left.Length + " vs " + right.Length + "; ");
                    continue;
                }
                int count = 0;
                int first = -1;
                for (int i = 0; i < left.Length; i++)
                {
                    if (Equals(left.GetValue(i), right.GetValue(i)))
                        continue;
                    count++;
                    if (first < 0)
                        first = i;
                }
                if (count > 0)
                    text.Append(name + " differs in " + count + " cells (first " + first + ": "
                        + left.GetValue(first) + " vs " + right.GetValue(first) + "); ");
            }
            return text.ToString();
        }
    }
}
