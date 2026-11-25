using NUnit.Framework;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// Values a scenario observed on the code it was written against, kept so
    /// a later change that alters them fails. A value not recorded yet
    /// ("UNSET") fails with the observed text, ready to be copied in.
    /// </summary>
    internal static class FlowFieldRecorded
    {
        internal const string Unset = "UNSET";

        internal static void AreEqual(string recorded, string observed, string what)
        {
            if (recorded == Unset)
                Assert.Fail("[record] " + what + " = " + observed);
            Assert.That(observed, Is.EqualTo(recorded), what);
        }
    }
}
