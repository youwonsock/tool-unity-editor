using NUnit.Framework;

namespace Common.TransformPath.Tests
{
    /// <summary>
    /// Gives each test its own notification queue around its SetUp and TearDown,
    /// and fails a test that leaves an operation open or a notification
    /// undelivered. Every test class of this assembly derives from it. (Unity's
    /// test runner ignores assembly-level NUnit test actions.)
    /// </summary>
    public abstract class PathBoundaryIsolatedTest
    {
        private PathExecutionBoundary.Isolation _isolation;

        // NUnit runs a base SetUp before and a base TearDown after the derived ones.
        [SetUp]
        public void IsolateBoundary() => _isolation = PathExecutionBoundary.Isolate();

        [TearDown]
        public void CheckBoundaryIsIdle()
        {
            bool idle = PathExecutionBoundary.IsIdle;
            _isolation?.Dispose();
            _isolation = null;
            if (!idle)
                Assert.Fail("The test left an open operation or undelivered notifications in PathExecutionBoundary.");
        }
    }
}
