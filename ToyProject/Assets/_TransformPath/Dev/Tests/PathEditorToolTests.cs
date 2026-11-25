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
        public void GroupsPastedIntoAnEmptyList_KeepTheirState()
        {
            var registry = Track(new GameObject("Registry")).AddComponent<PathWaypointSyncRegistry>();
            Transform member = Track(new GameObject("Member")).transform;
            var serialized = new SerializedObject(registry);
            SerializedProperty groups = serialized.FindProperty("_groups");

            // What a paste writes: a whole group, switched off on purpose.
            groups.arraySize = 1;
            SerializedProperty pasted = groups.GetArrayElementAtIndex(0);
            pasted.FindPropertyRelative("_displayName").stringValue = "Pasted";
            pasted.FindPropertyRelative("_members").arraySize = 1;
            pasted.FindPropertyRelative("_members").GetArrayElementAtIndex(0).objectReferenceValue = member;
            PathWaypointSyncRegistryEditor.EnableFirstGroups(groups, 0);
            serialized.ApplyModifiedPropertiesWithoutUndo();

            Assert.IsFalse(registry.Groups[0].Enabled);
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
        public void SeveralSequencesEditedTogether_ADeleteKeepsAnOverridesValues()
        {
            PathData lap = PathAt(Vector3.zero, new Vector3(5f, 0f, 0f));
            var sequences = new[]
            {
                Track(new GameObject("Sequence A")).AddComponent<MultiPathData>(),
                Track(new GameObject("Sequence B")).AddComponent<MultiPathData>(),
            };
            // One lap at its own speed, then the same lap faster.
            foreach (MultiPathData sequence in sequences)
                sequence.SetSegments(new[]
                {
                    new PathSegmentAuthoring(lap),
                    new PathSegmentAuthoring(lap, PathMovementSettings.Speed(10f)),
                });
            var serialized = new SerializedObject(sequences);
            var drawer = new PathSegmentListDrawer(serialized, false);

            drawer.CaptureTargets();
            serialized.FindProperty("_segments").DeleteArrayElementAtIndex(0);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            drawer.MarkTargetsEdited();
            drawer.ApplyListDefaultsToTargets();

            foreach (MultiPathData sequence in sequences)
            {
                Assert.AreEqual(1, sequence.AuthoringSegmentCount);
                Assert.AreEqual(10f, Segment(sequence).FindPropertyRelative("_moveValue").floatValue);
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

        [Test]
        public void SnapToGround_LiftsAPointJustUnderTheGround()
        {
            GameObject ground = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            ground.transform.position = Away + new Vector3(0f, -0.5f, 0f);
            ground.transform.localScale = new Vector3(20f, 1f, 20f);
            PathData path = PathAt(Away + new Vector3(0f, -0.05f, 0f), Away + new Vector3(5f, 3f, 0f));
            Physics.SyncTransforms();

            PathPointTools.SnapToGround(new List<PathData> { path });

            foreach (Transform point in path.PathPoints)
                Assert.AreEqual(0f, point.position.y, 1e-4f);
        }

        [Test]
        public void SnapToGround_MovesAPointListedTwiceOnce()
        {
            GameObject upper = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            upper.transform.position = Away + new Vector3(0f, -0.5f, 0f);
            upper.transform.localScale = new Vector3(20f, 1f, 20f);
            GameObject lower = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            lower.transform.position = Away + new Vector3(0f, -10.5f, 0f);
            lower.transform.localScale = new Vector3(20f, 1f, 20f);
            PathData path = PathAt(Away + new Vector3(0f, 2f, 0f), Away + new Vector3(5f, 2f, 0f));
            // A closed outline lists its first point again at the end.
            var points = new List<Transform>(path.PathPoints) { path.PathPoints[0] };
            path.SetPathPoints(points);
            Physics.SyncTransforms();

            LogAssert.Expect(LogType.Log, new Regex(@"snapped 2 point\(s\) to the ground \(0 failed\)"));
            PathPointTools.SnapToGround(new List<PathData> { path });

            foreach (Transform point in path.PathPoints)
                Assert.AreEqual(0f, point.position.y, 1e-4f);
        }

        [Test]
        public void SeveralSequencesEditedTogether_ADuplicateInTheMiddleKeepsTheOverride()
        {
            PathData lap = PathAt(Vector3.zero, new Vector3(5f, 0f, 0f));
            var sequences = new[]
            {
                Track(new GameObject("Sequence A")).AddComponent<MultiPathData>(),
                Track(new GameObject("Sequence B")).AddComponent<MultiPathData>(),
            };
            foreach (MultiPathData sequence in sequences)
                sequence.SetSegments(new[]
                {
                    new PathSegmentAuthoring(lap),
                    new PathSegmentAuthoring(lap, PathMovementSettings.Speed(10f)),
                });
            var serialized = new SerializedObject(sequences);
            var drawer = new PathSegmentListDrawer(serialized, false);

            // A copy of the first segment inserted after it shifts the Override down.
            drawer.CaptureTargets();
            serialized.FindProperty("_segments").InsertArrayElementAtIndex(0);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            drawer.MarkTargetsEdited();
            drawer.ApplyListDefaultsToTargets();

            foreach (MultiPathData sequence in sequences)
            {
                Assert.AreEqual(3, sequence.AuthoringSegmentCount);
                PathSegmentAuthoring moved = sequence.GetAuthoringSegment(2);
                Assert.AreEqual(EPathSegmentMovementSource.Override, moved.MovementSource);
            }
        }

        [Test]
        public void SnapToGround_IgnoresThePointsOwnCollider()
        {
            GameObject ground = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            ground.transform.position = Away + new Vector3(0f, -0.5f, 0f);
            ground.transform.localScale = new Vector3(20f, 1f, 20f);
            PathData path = PathAt(Away + new Vector3(0f, 1f, 0f), Away + new Vector3(5f, 1f, 0f));
            // A marker collider whose top is closer above the point than the ray's start.
            foreach (Transform point in path.PathPoints)
                point.gameObject.AddComponent<SphereCollider>().radius = 0.15f;
            Physics.SyncTransforms();

            PathPointTools.SnapToGround(new List<PathData> { path });

            foreach (Transform point in path.PathPoints)
                Assert.AreEqual(0f, point.position.y, 1e-4f);
        }

        [Test]
        public void SeveralSequencesEditedTogether_ACopyInsertedBeforeADifferentSegmentKeepsIt()
        {
            PathData lap = PathAt(Vector3.zero, new Vector3(5f, 0f, 0f));
            var sequences = new[]
            {
                Track(new GameObject("Sequence A")).AddComponent<MultiPathData>(),
                Track(new GameObject("Sequence B")).AddComponent<MultiPathData>(),
            };
            var layouts = new[]
            {
                // Same source, different values: only the values tell the segments apart.
                new[] { new PathSegmentAuthoring(lap, PathMovementSettings.Speed(5f)), new PathSegmentAuthoring(lap, PathMovementSettings.Speed(10f)) },
                new[] { new PathSegmentAuthoring(lap), new PathSegmentAuthoring(lap, true) },
            };
            foreach (PathSegmentAuthoring[] layout in layouts)
            {
                foreach (MultiPathData sequence in sequences)
                    sequence.SetSegments(layout);
                var serialized = new SerializedObject(sequences);
                var drawer = new PathSegmentListDrawer(serialized, false);

                drawer.CaptureTargets();
                serialized.FindProperty("_segments").InsertArrayElementAtIndex(0);
                serialized.ApplyModifiedPropertiesWithoutUndo();
                drawer.MarkTargetsEdited();
                drawer.ApplyListDefaultsToTargets();

                foreach (MultiPathData sequence in sequences)
                {
                    PathSegmentAuthoring kept = sequence.GetAuthoringSegment(2);
                    Assert.AreEqual(layout[1].MovementSource, kept.MovementSource);
                    Assert.AreEqual(layout[1].PreservePreviousSpeed, kept.PreservePreviousSpeed);
                    float value = new SerializedObject(sequence).FindProperty("_segments")
                        .GetArrayElementAtIndex(2).FindPropertyRelative("_moveValue").floatValue;
                    Assert.AreEqual(
                        layout[1].MovementSource == EPathSegmentMovementSource.Override ? 10f : PathSegmentAuthoring.DEFAULT_OVERRIDE_VALUE,
                        value);
                }
            }
        }
    }
}
