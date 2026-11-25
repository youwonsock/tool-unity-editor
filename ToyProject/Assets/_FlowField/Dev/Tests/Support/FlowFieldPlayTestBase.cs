using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// A test that runs a sample scene in Play Mode. It fails on unexpected
    /// "[FlowField" log lines and always leaves Play Mode on an empty scene,
    /// so the Bake Mode it set is never saved.
    /// </summary>
    internal abstract class FlowFieldPlayTestBase
    {
        protected readonly FlowFieldTestLog Log = new FlowFieldTestLog();

        [SetUp]
        public void StartLog() => Log.Start();

        [UnityTearDown]
        public IEnumerator LeavePlayMode()
        {
            yield return FlowFieldPlayScene.Exit();
            Log.StopAndAssertClean();
        }

        protected static FlowFieldManager Manager => FlowFieldPlayScene.Manager;
    }
}
