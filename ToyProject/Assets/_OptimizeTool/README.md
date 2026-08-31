# OptimizeTool

Unity Editor에서 메시 결합, Backface Culling 메시 베이크, Physics Recorder를 제공하는 도구 모음입니다.

## 구성

- `Script/MeshCombiner.cs`: 자식 MeshFilter를 재질별로 결합하고 에셋으로 저장합니다.
- `Script/BackfaceCullMeshBaker.cs`: 카메라 방향 기준으로 보이지 않는 삼각형을 제거한 메시를 저장합니다.
- `Script/PhysicsRecorder.cs`: Rigidbody 움직임을 AnimationClip과 Prefab으로 기록합니다.

샘플 비교 검증용 `OptimizeToolComparisonController`는 시스템 도구와 분리해 `Script/Temp`에 보관합니다.

## 샘플

`Scene/OptimizeToolSample.unity`는 동일 재질의 3×3 Cube 원본과 실제 `MeshCombiner`가 만든 단일 메시를 나란히 보여줍니다. Play Mode에서는 원본과 최적화 결과를 3초 간격으로 전환하며, 에셋을 생성하거나 덮어쓰지 않습니다.

비교용 계층은 `Prefab/OptimizeToolShowcase.prefab`에 보관하고, 결합 Mesh 결과는 `Settings/Generated`에 둡니다.

도구는 `Init → Combine/Bake/Record → Release` 수명 주기를 사용합니다. 입력 Mesh, Readable/Normal 정책, 기존 Asset 충돌 정책과 `Assets/` 하위 출력 경로를 먼저 검증하며 계약 위반은 예외입니다. 출력 경로와 파일명은 기본값 없이 각 컴포넌트에 직렬화하여 지정해야 합니다. 샘플 `MeshCombiner`의 결과 경로는 `Assets/_OptimizeTool/Settings/Generated`입니다.

수동 재생성은 편집 모드에서 `MeshCombiner`의 명시적 Save 옵션을 실행한 뒤 결과를 `Settings/Generated`로 이동하는 방식으로 수행합니다. 일반 Play Mode에는 AssetDatabase 변경이 없습니다.
