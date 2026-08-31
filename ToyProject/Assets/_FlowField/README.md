# FlowField

Bake된 표면·장애물·Goal·Modifier로 방향과 속도 배율을 제공하는 런타임 모듈입니다. 이동은 소비자가 자신의 `FixedUpdate`에서 `IFlowFieldProvider.Sample`을 호출해 적용합니다.

## 요구사항

- Unity `2023.2.20f1` 이상
- Burst `1.8.13`, Collections `1.4.0`, Jobs `0.70.0-preview.7`, Mathematics `1.2.6`

`Script/Core/Common.FlowField.Core.asmdef`가 저수준 계산과 패키지 참조를 소유합니다. Core EditMode 테스트용 asmdef는 `Script/Core/EditModeTests`에 있습니다.

샘플 기능 검증용 `FlowFieldSampleAgent`와 `FlowFieldSampleController`는 시스템 런타임 코드와 분리해 `Script/Temp`에 보관합니다.

## 수명 주기

`FlowFieldManager`는 Play Mode의 `Awake`에서 `Init`하고 `OnDestroy`에서 `Release`합니다. `Init`은 서비스·Grid·Workspace를 만들고 첫 Field를 동기 `Rebuild`합니다. 재빌드는 명시적으로 `Rebuild()`를 호출해야 하며, 초기화 전·중복 호출·Faulted 상태 사용은 예외입니다. 복구는 `Release → Init` 순서만 허용됩니다.

`OnDisable`은 Ready를 해제하고 전체 영역을 Dirty로 표시합니다. 다시 활성화되는 경계에서 명시적으로 `Rebuild()`하여 Ready를 복구하며, 실패하면 Manager가 Faulted 상태가 됩니다.

## Provider 사용

```csharp
using Common.FlowField;
using UnityEngine;

public sealed class FlowConsumer : MonoBehaviour
{
    [SerializeField] private FlowFieldManager _manager;

    private void FixedUpdate()
    {
        if (!_manager.IsReady)
            return; // 아직 결과가 없는 것은 정상적인 상태 확인입니다.

        FlowFieldSample sample = _manager.Sample(transform.position);
        if (sample.HasSurface)
            transform.position += sample.Direction * sample.SpeedMultiplier * Time.fixedDeltaTime;
    }
}
```

`Sample`은 Manager가 준비되지 않았거나 좌표가 유한하지 않거나 Grid 밖이면 예외를 던집니다. Grid 안의 Surface가 없는 Cell은 `HasSurface == false`인 정상 결과이며, `Direction == Vector3.zero`와 `SpeedMultiplier == 0`을 가집니다. 개별 이웃 Surface가 없거나 Raycast가 검출되지 않은 것도 Bake 결과에 반영되는 정상 결과입니다.

Grid 경계에 넣을 좌표가 필요할 때만 명시적 `ClampPositionToGrid(Vector3)`를 사용합니다. 이 API는 `FlowFieldClampResult.Position`, `ClampedX`, `ClampedZ`를 반환하며 일반 `Sample`이 자동으로 Clamp하지는 않습니다.

## Controller와 Modifier

`IFlowFieldController`의 Goal·동적 장애물·Modifier 등록 API는 초기화되고 등록된 대상만 받습니다. null 인자는 `ArgumentNullException`, 수치·범위 오류는 `ArgumentOutOfRangeException`, 구조 불일치는 `ArgumentException`, 생명주기·등록 계약 위반은 `InvalidOperationException`입니다. Modifier Priority 중복, 미등록 대상 해제/Dirty 통지, Compose 중 등록 변경은 즉시 실패합니다.

```csharp
IFlowFieldController controller = _manager;
controller.SetGoalPosition(goalPosition, 10f);
controller.RegisterDynamicObstacle(obstacleCollider);
controller.NotifyObstacleRegionDirty(obstacleCollider.bounds);
```

## ToyProject 샘플

`Scene/FlowFieldSample.unity`는 100×100 Ground, 100×6×100 Bake Bounds, Cell Size 0.5(200×200=40,000 Cell), Obstacle Clearance 0.3의 고정 시드 미로입니다. 외곽·내부 벽은 `FlowFieldObstacle` 레이어 BoxCollider이고, 남서쪽 Spawn 광장은 비워 두었습니다.

재사용 가능한 계층은 `Prefab/FlowFieldShowcase.prefab`에, 런타임 Agent 원본은 `Prefab/FlowFieldSampleAgent.prefab`에 보관합니다. Bake 결과와 공유 재질은 `Settings`에 둡니다.

Play Mode에서 `FlowFieldSampleController`가 40×25 배열의 1,000개 공유 Prefab을 생성한 후에만 시뮬레이션을 시작합니다. Agent는 비키네마틱 Rigidbody, CapsuleCollider(반경 0.25m/높이 0.8m), Continuous Speculative 충돌과 0 마찰·0 반발 PhysicMaterial을 사용합니다. 중앙 `FixedUpdate`가 Flow 방향을 목표 속도 3m/s, 최대 가속도 8m/s²로 `Rigidbody.AddForce`에 전달합니다.

Goal은 맵 안쪽 후보 8개 중 하나를 고정 난수로 15초마다 변경하며 영향 반경은 10m입니다. Space 키로 즉시 변경할 수 있습니다. 화면에는 Ready/Revision, 생성 수, Goal, 변경 횟수, 공간 해시로 계산한 깊은 관통 쌍 수가 표시됩니다.

## 정상적인 `Try*` API

Core의 내부 `FlowFieldBilinearSampler.TrySample`과 Bake/Overlap 헬퍼의 `Try*`는 데이터가 아직 준비되지 않았거나 후보가 없는 정상 결과를 `false`로 표현합니다. 공개 Provider 계약에는 `TrySample`을 노출하지 않습니다.
