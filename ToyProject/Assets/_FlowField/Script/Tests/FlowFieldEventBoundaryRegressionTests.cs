using NUnit.Framework;
using UnityEngine;

namespace Common.FlowField.Tests
{
    public sealed class FlowFieldEventBoundaryRegressionTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void CallbackAssertionIsReportedToTheTestOwnerWithoutFaultingTheSession(bool volume)
        {
            const string message = "Intentional callback assertion regression";
            var backend = new FlowFieldRegressionFixture.DeferredBackend();
            FlowFieldSessionBase session = volume
                ? (FlowFieldSessionBase)new FlowFieldVolumeSession()
                : new FlowFieldSession(FlowFieldRegressionFixture.Surface(), backend);
            try
            {
                var failures = new FlowFieldRegressionFixture.EventFailures(session);
                session.FieldCommitted += () => Assert.Fail(message);
                if (volume)
                {
                    var concrete = (FlowFieldVolumeSession)session;
                    concrete.Initialize(FlowFieldBakeMode.RuntimeDynamic);
                    concrete.Submit(FlowFieldVolumeSafetyRegressionTests.Request(FlowFieldVolumeSafetyRegressionTests.Grid));
                    FlowFieldVolumeSafetyRegressionTests.Finish(concrete);
                }
                else
                {
                    var concrete = (FlowFieldSession)session;
                    concrete.Initialize(FlowFieldBakeMode.RuntimeDynamic, FlowFieldSessionSourceKind.SceneBuild,
                        FlowFieldSolvePolicy.Worker);
                    concrete.Submit(FlowFieldRegressionFixture.Request(new Vector3(2.5f, 0f, 2.5f)));
                    concrete.Pump();
                    backend.Complete(0);
                }

                Assert.That(session.IsReady, Is.True, session.LastError);
                Assert.That(session.IsFaulted, Is.False);
                // This assertion runs outside the production callback boundary.
                var assertion = Assert.Throws<AssertionException>(() => failures.AssertNone());
                Assert.That(assertion.Message, Does.Contain(message));
            }
            finally { session.Dispose(); }
        }
    }
}
