using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Common.FlowField.Tests
{
    public sealed class FlowFieldVolumeVisualizationEditorTests
    {
        private GameObject _object;

        [TearDown]
        public void TearDown()
        {
            if (_object != null)
                UnityEngine.Object.DestroyImmediate(_object);
        }

        [Test]
        public void ManagerStoresFullVolumeDisplayDefaultsWithoutChangingRuntimeContract()
        {
            Type managerType = Type.GetType("Common.FlowField.FlowFieldManager, Common.FlowField.Runtime");
            Assert.That(managerType, Is.Not.Null);

            _object = new GameObject("FlowFieldVisualizationEditorTest");
            Component manager = _object.AddComponent(managerType);
            var serialized = new SerializedObject(manager);
            Assert.That(serialized.FindProperty("_volumeGizmoMode").intValue, Is.EqualTo(0));
            Assert.That(serialized.FindProperty("_showVolumeCells").boolValue, Is.False);
            Assert.That(serialized.FindProperty("_showVolumeVectors").boolValue, Is.True);
            Assert.That(serialized.FindProperty("_volumeGizmoSliceAxis").intValue, Is.EqualTo(1));
            Assert.That(serialized.FindProperty("_volumeGizmoSlice").intValue, Is.EqualTo(-1));

            PropertyInfo revision = managerType.GetProperty("Revision");
            Assert.That(revision, Is.Not.Null);
            Assert.That((int)revision.GetValue(manager, null), Is.EqualTo(0));
        }

        [Test]
        public void VisualizationServiceExposesLifecycleEntryPointsAndDoesNotRequireBakeOnRefresh()
        {
            Type serviceType = Type.GetType(
                "Common.FlowField.FlowFieldVolumeVisualizationEditor, Common.FlowField.Editor");
            Assert.That(serviceType, Is.Not.Null);
            Assert.That(
                serviceType.GetMethod("RequestRefresh", BindingFlags.Static | BindingFlags.NonPublic),
                Is.Not.Null);
            Assert.That(
                serviceType.GetMethod("PumpAll", BindingFlags.Static | BindingFlags.NonPublic),
                Is.Not.Null);
            Assert.That(
                serviceType.GetMethod("Clear", BindingFlags.Static | BindingFlags.NonPublic),
                Is.Not.Null);

            _object = new GameObject("FlowFieldVisualizationRefreshTest");
            Type managerType = Type.GetType("Common.FlowField.FlowFieldManager, Common.FlowField.Runtime");
            Assert.That(managerType, Is.Not.Null);
            Component manager = _object.AddComponent(managerType);
            int before = (int)manager.GetType().GetProperty("Revision").GetValue(manager, null);
            serviceType.GetMethod("RequestRefresh", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { manager });
            serviceType.GetMethod("PumpAll", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { 2.0d });
            int after = (int)manager.GetType().GetProperty("Revision").GetValue(manager, null);
            Assert.That(after, Is.EqualTo(before));
        }
    }
}
