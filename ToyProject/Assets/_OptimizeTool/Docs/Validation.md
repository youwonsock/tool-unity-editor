# Mesh optimization validation — 2026-10-23

Environment: Unity 2023.2.20f1, macOS Editor, URP 16.0.6.

## Observed results

- `MeshOptimizationTests`: **19/19 EditMode tests passed**.
- `OptimizeToolShowcaseTests`: **2/2 PlayMode tests passed**.
- The actual showcase builder regenerated three distinct meshes through `MeshCombiner.BuildMeshes()`, preserving their existing asset GUIDs.
- The PlayMode tests switched all four modes and verified Renderer/vertex/triangle counts, distinct mesh references, and the BAKE VIEW button.
- A second PlayMode test rendered the original and each optimized mode at the same camera pose into a 512 × 256 RenderTexture. Mean normalized RGB error remained below the 0.005 acceptance threshold for every mode.
- SHA-256 fingerprints of all files under `Settings/Generated`, `Prefab`, and `Scene` were unchanged across the final PlayMode test run.
- Native Game View screenshots were inspected for geometry framing, the four mode buttons, metrics, and reduction text. They are linked from the README.
- The sample scene was left open in Edit Mode.

| Mode | Renderers | Vertices | Triangles |
| --- | ---: | ---: | ---: |
| Original | 9 | 216 | 108 |
| Combined | 1 | 216 | 108 |
| Backface | 1 | 60 | 30 |
| Occlusion | 1 | 72 | 36 |

## Regression coverage

The EditMode tests cover raw vertex-channel preservation (tangents, colors, UV0–UV7), empty submeshes after face removal, unreadable source copying without importer changes, explicit normal recalculation, mixed channel layouts, perspective/orthographic directions, mirrored and small-scale transforms, self-collider exclusion, layer/trigger filtering, hit-buffer growth, 66,000-vertex input, material-mapped scene output, repeated child collection, full-batch staging, injected save failure rollback, name/GUID preservation, successful overwrite Undo/Redo, source-reference Undo, output collisions, custom vectors without a camera, and CustomEditor discovery.

The exact test results are recorded in [ValidationResults.json](ValidationResults.json).

## Reproduction

1. Run `Tools > OptimizeTool > Build Showcase Assets` in Edit Mode. No selection is required.
2. Open `Assets/_OptimizeTool/Scene/OptimizeToolSample.unity` and enter Play Mode.
3. Use `1`–`4` or the mode buttons. Use `F` / BAKE VIEW to restore the bake pose; FOCUS CAMERA and the free camera expose removed geometry from other angles.
4. In Test Runner, run the `MeshOptimizationTests` fixture in EditMode and the `OptimizeToolShowcaseTests` fixture in PlayMode.

## Scope and limits

These results validate mesh processing and the showcase. The initial assembly-wide run also included separately changing `PhysicsRecorderTests` and encountered failures there; those tests are excluded from the mesh fixture results above. FlowField, TransformPath, PhysicsRecorder, the complete project suite, and a standalone Player build are outside this validation report.

Geometry counts and image equivalence do not establish a frame-time or draw-call improvement. Camera culling remains a view-dependent bake based on Collider sampling; it is unsuitable as a general replacement for moving-camera geometry. Combining removes individual Renderer culling granularity. Existing baked-lightmap Renderer settings are not reconstructed, so lighting needs rebaking after scene combination. Undo restores scene references and overwritten meshes; newly created asset files remain on disk. UInt32 index support depends on the target graphics device.


## 2026-10-24 recovery and capture-integrity refactoring

The current implementation was verified in a separate Unity 2023.2.20f1 project. EditMode **92/92**, PlayMode **36/36**, and Python capture-integrity **8/8** tests passed. These are OptimizeTool assembly results, not the entire repository suite.

Regression coverage includes removed-source restoration during rebuilds, original disabled-state preservation, Undo/Redo ownership, Prefab instance reference remapping, save-failure rollback, and exact workload/camera/settings restoration after cancellation, disable, destruction and output failure. Scale case/mode restoration and single completion delivery are covered separately.

A macOS Development Player built with zero errors/warnings and produced a v2 capture of **960 samples**. The launcher and offline analyzer both validated the capture identity and SHA-256 fingerprints. Reusing the completed directory was rejected with exit code 1 while preserving every existing result file. This run verifies the capture path; it does not establish new performance improvement claims.

All **454** protected original asset/metadata/settings files retained their hashes. Comparison against the previous compiled build found no missing public API members (**353 checked**) or Unity-serialized fields (**159 checked**). Existing component GUIDs, scenes and Prefabs were retained. Prior capture data remains readable; 24 archived timing captures passed the shared reader. Three image-only archives intentionally contain no timing CSV.

Evidence: [validation summary](RecoveryRefactoring/validation.json), [EditMode XML](RecoveryRefactoring/EditMode.xml), [PlayMode XML](RecoveryRefactoring/PlayMode.xml), [protected hashes](RecoveryRefactoring/preserved-sha256.json), [tested source hashes](RecoveryRefactoring/source-sha256.json), [Player capture](RecoveryRefactoring/Player/complete.json).
