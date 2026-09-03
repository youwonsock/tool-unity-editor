# FlowField

Bake된 표면·장애물·Goal·Modifier로 방향과 속도 배율을 제공하는 런타임 모듈입니다. `RuntimeDynamic`과 `StaticBaked`를 선택할 수 있으며, 이동은 소비자가 자신의 `FixedUpdate`에서 `TrySample` 또는 엄격한 `Sample`을 호출해 적용합니다.

## 요구사항

- Unity `2023.2.20f1` 이상
- Collections `1.4.0`, Mathematics `1.2.6`
- GPU 경로는 Compute Shader, raw-buffer atomic, indirect dispatch와 AsyncGPUReadback을 지원하는 플랫폼에서 사용합니다. 지원하지 않는 플랫폼은 동일한 Managed FIFO BFS로 자동 전환합니다.

`Script/Core/Common.FlowField.Core.asmdef`가 저수준 계산과 패키지 참조를 소유합니다. Core EditMode 테스트용 asmdef는 `Script/Core/EditModeTests`에 있습니다.

## 베이크 모드

`FlowFieldManager.BakeMode`에서 두 모드를 선택할 수 있습니다.

- `RuntimeDynamic`(기본값)은 초기화와 `NotifySurfaceDirty()`에서 하향 Raycast Surface를 다시 만들고, 정적 Collider와 등록된 동적 장애물을 합성합니다. Goal/장애물 변경은 Surface를 재사용해 BFS만 갱신합니다. 실제 장애물 마스크가 같으면 요청·Revision·이벤트를 만들지 않습니다.
- `StaticBaked`는 Editor의 `Bake / ReBake Static Flow Field`로 생성한 v4 `FlowFieldStaticBakeData` 하나를 런타임에 로드합니다. 런타임 Goal·장애물·Surface API와 Collider 이동은 검증 후 무시되며, Default Direction과 Modifier만 Final Field를 다시 합성합니다. Asset이 없거나 v4 signature가 다르면 `Init()`이 실패합니다.

두 모드는 동일한 Surface·Obstacle·Goal·Topology·Modifier 함수와 GPU Frontier BFS를 사용하고, Compute Shader가 지원되지 않거나 오류/overflow가 발생하면 동일 입력의 Managed FIFO BFS로 전환합니다. 정적 런타임은 Physics query와 BFS를 실행하지 않습니다.

샘플 기능 검증용 `FlowFieldSampleAgent`와 `FlowFieldSampleController`는 시스템 런타임 코드와 분리해 `Script/Temp`에 보관합니다.

## 수명 주기

`FlowFieldManager`는 Play Mode의 `Awake`에서 `Init`하고 `OnDestroy`에서 `Release`합니다. `Init`은 공통 Session을 만들고 첫 Field를 `RequestRebuild()`합니다. 상태는 `Uninitialized → Building → Ready`로 관찰할 수 있고, 이후 재빌드에서는 이전 committed field를 유지하며 `IsRebuilding`으로 진행 상태를 확인할 수 있습니다. GPU 오류·overflow·미지원 플랫폼은 같은 8방향 FIFO BFS를 Managed backend로 실행합니다. `Faulted`에서도 `RequestRebuild()`로 현재 입력을 재시도할 수 있습니다.

`OnDisable`은 readback을 정리하고 Ready를 해제한 뒤 전체 영역을 Dirty로 표시합니다. 다시 활성화되면 최신 입력으로 재빌드하며, Release는 해당 Manager가 소유한 GPU 요청을 완료한 뒤 리소스를 해제합니다.

## Provider 사용

```csharp
using Common.FlowField;
using UnityEngine;

public sealed class FlowConsumer : MonoBehaviour
{
    [SerializeField] private FlowFieldManager _manager;

    private void FixedUpdate()
    {
        if (!_manager.TrySample(transform.position, out FlowFieldSample sample))
            return; // 아직 결과가 없거나 Grid 밖이면 정지합니다.
        if (sample.HasSurface)
            transform.position += sample.Direction * sample.SpeedMultiplier * Time.fixedDeltaTime;
    }
}
```

`TrySample`은 미준비·비정상 좌표·Grid 밖에서 `false`와 정지 샘플을 반환합니다. Grid 안의 Surface가 없는 Cell은 `true`와 `HasSurface == false`를 반환하며 방향과 속도는 0입니다. 엄격한 `Sample`은 잘못된 사용 시 예외를 유지합니다. 샘플은 개체가 속한 한 셀의 committed 방향만 반환합니다. 영향권 밖 셀은 기본 방향을 사용하고, 영향권 안에서 Goal까지 도달할 수 없는 셀은 정지합니다.

Grid 경계에 넣을 좌표가 필요할 때만 명시적 `ClampPositionToGrid(Vector3)`를 사용합니다. 이 API는 `FlowFieldClampResult.Position`, `ClampedX`, `ClampedZ`를 반환하며 일반 `Sample`이 자동으로 Clamp하지는 않습니다.

## Controller와 Modifier

`IFlowFieldController`의 Goal·동적 장애물·Modifier 등록 API는 초기화되고 등록된 대상만 받습니다. null 인자는 `ArgumentNullException`, 수치·범위 오류는 `ArgumentOutOfRangeException`, 구조 불일치는 `ArgumentException`, 생명주기·등록 계약 위반은 `InvalidOperationException`입니다. Modifier Priority 중복, 미등록 대상 해제/Dirty 통지, Compose 중 등록 변경은 즉시 실패합니다. `StateChanged`는 실제 상태 전이마다, `FieldChanged`와 `Revision`은 실제 commit마다 한 번만 발생합니다.

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

Goal은 맵 안쪽 후보 8개 중 하나를 고정 난수로 15초마다 변경하며 기본 영향 반경은 0m(Global)입니다. Space 키로 즉시 다음 후보를 선택하고 G 키로 Goal을 명시적으로 삭제할 수 있습니다. 화면에는 Ready/Revision, 생성 수, Goal 활성 상태, 변경 횟수, 공간 해시로 계산한 깊은 관통 쌍 수가 표시됩니다.

`FlowFieldShowcaseOverviewController`는 `Baseline → SpeedModifier → NoiseModifier → DynamicObstacle → SampleAndClamp` 모드를 8초마다 순환합니다. `1/2/3`으로 기본·속도·노이즈 모드를 선택하고 `M`으로 동적 장애물, `O`로 Sample/Clamp, `R`로 명시적 Rebuild, `C`로 진단을 실행할 수 있습니다. Space는 Goal 변경, G는 Goal 삭제입니다. Overview Board에는 Modifier/Obstacle 등록 상태, 현재 Sample 방향·속도, Clamp 결과와 Bounds 진단이 함께 표시됩니다. 동적 장애물은 이동할 때마다 즉시 필드를 재계산하지 않고 Dirty 통지 후 명시적인 Rebuild 경계에서만 반영됩니다.

## 정상적인 `Try*` API

Core의 `FlowFieldCellSampler.TrySample`과 Bake/Overlap 헬퍼의 `Try*`는 데이터가 아직 준비되지 않았거나 후보가 없는 정상 결과를 `false`로 표현합니다. 공개 Provider 계약에도 동일한 `TrySample(Vector3, out FlowFieldSample)`을 제공합니다.
