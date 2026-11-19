using System;

namespace Common.TransformPath.Tests
{
    /// <summary>A spacing policy whose rule each test sets.</summary>
    internal sealed class TestSpacingPolicy : PathQueueSpacingPolicy
    {
        public Func<PathQueueMember, PathQueueMember, float, float> Rule;

        public override float GetSpacing(PathQueueMember member, PathQueueMember ahead, float resolvedSpacing) =>
            Rule(member, ahead, resolvedSpacing);
    }
}
