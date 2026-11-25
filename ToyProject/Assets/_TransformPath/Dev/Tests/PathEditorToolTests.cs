using System.Collections.Generic;
using System.Text.RegularExpressions;
using Common.TransformPath.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Common.TransformPath.Tests
{
    /// <summary>The Inspector tools' rules, called the way the Inspector calls them.</summary>
    public sealed class PathEditorToolTests : PathBoundaryIsolatedTest
    {
        private const string TEMP_FOLDER = "Assets/_TransformPathEditorTestTemp";
        // Far from anything a scene open during the tests may hold.
        private static readonly Vector3 Away = new Vector3(10000f, 0f, 10000f);

        private PathTestScene _scene;
        private readonly List<Object> _objects = new List<Object>();

        [SetUp]
        public void SetUp() => _scene = new PathTestScene();

        [TearDown]
        public void TearDown()
        {
            for (int i = _objects.Count - 1; i >= 0; i--)
                if (_objects[i] != null)
                    Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
            _scene.Dispose();
            if (AssetDatabase.IsValidFolder(TEMP_FOLDER))
                AssetDatabase.DeleteAsset(TEMP_FOLDER);
        }

        private GameObject Track(GameObject gameObject)
        {
            _objects.Add(gameObject);
            return gameObject;
        }

        private PathData PathAt(Vector3 from, Vector3 to)
        {
            PathData path = _scene.CreatePath(PathMovementSettings.Speed(3f), EPathCurveType.Linear, from, to);
            path.Rebuild();
            return path;
        }

        [Test]
        public void CreatePathPoints_RefusesAPrefabAssetThatIsNotOpen()
        {
            var host = new GameObject("Prefab Path");
            host.AddComponent<PathData>();
            AssetDatabase.CreateFolder("Assets", TEMP_FOLDER.Substring("Assets/".Length));
            GameObject asset = PrefabUtility.SaveAsPrefabAsset(host, TEMP_FOLDER + "/Path.prefab");
            Object.DestroyImmediate(host);
            var assetPath = asset.GetComponent<PathData>();
            int roots = SceneManager.GetActiveScene().rootCount;

            Assert.IsFalse(PathPointTools.CanCreatePoints(assetPath));
            LogAssert.Expect(LogType.Warning, new Regex("open the prefab"));
            PathPointTools.CreateDefaultPoints(new List<PathData> { assetPath });

            Assert.AreEqual(roots, SceneManager.GetActiveScene().rootCount);
            Assert.AreEqual(0, asset.transform.childCount);

            var scenePath = Track(new GameObject("Scene Path")).AddComponent<PathData>();
            Assert.IsTrue(PathPointTools.CanCreatePoints(scenePath));
            PathPointTools.CreateDefaultPoints(new List<PathData> { scenePath });
            Assert.AreEqual(3, scenePath.transform.childCount);
            Assert.AreEqual(3, scenePath.PathPoints.Count);
        }

        [Test]
        public void SnapToGround_PassesThroughTriggers()
        {
            GameObject ground = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            ground.transform.position = Away + new Vector3(0f, -0.5f, 0f);
            ground.transform.localScale = new Vector3(20f, 1f, 20f);
            GameObject trigger = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            trigger.transform.position = Away + new Vector3(0f, 1.5f, 0f);
            trigger.transform.localScale = new Vector3(20f, 1f, 20f);
            trigger.GetComponent<BoxCollider>().isTrigger = true;
            PathData path = PathAt(Away + new Vector3(0f, 5f, 0f), Away + new Vector3(5f, 5f, 0f));
            Physics.SyncTransforms();

            PathPointTools.SnapToGround(new List<PathData> { path });

            foreach (Transform point in path.PathPoints)
                Assert.AreEqual(0f, point.position.y, 1e-4f);
        }

        [Test]
        public void FirstWaypointGroupAddedToAnEmptyList_StartsEnabled()
        {
            var registry = Track(new GameObject("Registry")).AddComponent<PathWaypointSyncRegistry>();
            var serialized = new SerializedObject(registry);
            SerializedProperty groups = serialized.FindProperty("_groups");
            var list = new ReorderableList(serialized, groups);

            // What the Inspector's "+" does, then what the Inspector does after it.
            ReorderableList.defaultBehaviours.DoAddButton(list);
            PathWaypointSyncRegistryEditor.EnableFirstGroups(groups, 0);
            serialized.ApplyModifiedPropertiesWithoutUndo();

            Assert.IsTrue(registry.Groups[0].Enabled);
        }

        [Test]
        public void SeveralSequencesEditedTogether_StartSegmentsAndOverridesAsOneDoes()
        {
            MultiPathData first = Track(new GameObject("Sequence A")).AddComponent<MultiPathData>();
            MultiPathData second = Track(new GameObject("Sequence B")).AddComponent<MultiPathData>();
            PathData path = PathAt(Vector3.zero, new Vector3(5f, 0f, 0f));
            var serialized = new SerializedObject(new Object[] { first, second });
            var drawer = new PathSegmentListDrawer(serialized, false);

            // The plain field's "+" on empty lists.
            drawer.CaptureTargets();
            serialized.FindProperty("_segments").arraySize = 1;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            drawer.MarkTargetsEdited();
            drawer.ApplyListDefaultsToTargets();
            foreach (MultiPathData sequence in new[] { first, second })
                Assert.AreEqual(PathSegmentAuthoring.DEFAULT_OVERRIDE_VALUE, Segment(sequence).FindPropertyRelative("_moveValue").floatValue);

            // A path for both, then Override chosen for both.
            serialized.Update();
            serialized.FindProperty("_segments").GetArrayElementAtIndex(0).FindPropertyRelative("_path").objectReferenceValue = path;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            serialized.Update();
            drawer.CaptureTargets();
            serialized.FindProperty("_segments").GetArrayElementAtIndex(0).FindPropertyRelative(PathSegmentListDrawer.MOVEMENT_SOURCE_PROPERTY)
                .enumValueIndex = (int)EPathSegmentMovementSource.Override;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            drawer.MarkTargetsEdited();
            drawer.ApplyListDefaultsToTargets();

            foreach (MultiPathData sequence in new[] { first, second })
            {
                SerializedProperty segment = Segment(sequence);
                Assert.AreEqual((int)EPathMoveType.SpeedBased, segment.FindPropertyRelative("_moveType").enumValueIndex);
                Assert.AreEqual(3f, segment.FindPropertyRelative("_moveValue").floatValue);
                sequence.Rebuild();
                Assert.IsTrue(sequence.IsReady);
            }
        }

        [Test]
        public void WaypointGroupSync_DoesNotBuildAnUnfinishedSequence()
        {
            GameObject host = Track(new GameObject("Unfinished Sequence"));
            MultiPathData sequence = host.AddComponent<MultiPathData>();
            PathData path = PathAt(Vector3.zero, new Vector3(5f, 0f, 0f));
            var segments = new SerializedObject(sequence);
            SerializedProperty list = segments.FindProperty("_segments");
            list.arraySize = 2;
            list.GetArrayElementAtIndex(0).FindPropertyRelative("_path").objectReferenceValue = path;
            list.GetArrayElementAtIndex(1).FindPropertyRelative("_path").objectReferenceValue = null;
            segments.ApplyModifiedPropertiesWithoutUndo();
            var registry = host.AddComponent<PathWaypointSyncRegistry>();
            Transform outside = Track(new GameObject("Outside")).transform;
            outside.position = new Vector3(0f, 3f, 0f);
            var groups = new SerializedObject(registry);
            SerializedProperty group = groups.FindProperty("_groups");
            group.arraySize = 1;
            SerializedProperty first = group.GetArrayElementAtIndex(0);
            first.FindPropertyRelative("_enabled").boolValue = true;
            SerializedProperty members = first.FindPropertyRelative("_members");
            members.arraySize = 2;
            members.GetArrayElementAtIndex(0).objectReferenceValue = path.PathPoints[0];
            members.GetArrayElementAtIndex(1).objectReferenceValue = outside;
            groups.ApplyModifiedPropertiesWithoutUndo();

            // A build of this sequence would log that segment 1 has no PathData; the test fails on any error.
            Assert.AreEqual(1, PathWaypointSyncEditorUtility.AlignAllGroups(registry, 1e-6f));
            Assert.AreEqual(path.PathPoints[0].position, outside.position);
        }

        private static SerializedProperty Segment(MultiPathData sequence) =>
            new SerializedObject(sequence).FindProperty("_segments").GetArrayElementAtIndex(0);
    }
}
