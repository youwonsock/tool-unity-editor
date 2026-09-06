# FlowField

Bake된 표면 또는 3D 셀 볼륨·장애물·Goal·Modifier로 방향과 속도 배율을 제공하는 런타임 모듈입니다. `Surface2D`와 `Volume3D` 공간 모드, `RuntimeDynamic`과 `StaticBaked` 베이크 모드를 선택할 수 있으며, 이동은 소비자가 자신의 `FixedUpdate`에서 `TrySample` 또는 엄격한 `Sample`을 호출해 적용합니다.

## 요구사항

- Unity `2023.2.20f1` 이상
- Collections `1.4.0`, Mathematics `1.2.6`
- GPU 경로는 Compute Shader, raw-buffer atomic, indirect dispatch와 AsyncGPUReadback을 지원하는 플랫폼에서 사용합니다. 지원하지 않는 플랫폼은 동일한 Managed FIFO BFS로 자동 전환합니다.

`Script/Runtime/Core/Common.FlowField.Core.asmdef`가 저수준 계산과 패키지 참조를 소유합니다.

## 내부 구조와 실행 경계

FlowField는 다음 책임 경계를 사용합니다.

- `Common.FlowField.Core`는 계약, Grid, 공통 Session, Surface/Volume 계산 단계, 결과 View와 베이크 데이터를 소유합니다.
- `Common.FlowField.Runtime`은 `FlowFieldManager`, Modifier 컴포넌트와 Runtime PlayerLoop Driver만 소유합니다.
- `Common.FlowField.Editor`는 Inspector, 베이크 작업, 구조 검증 캐시와 Gizmo 표시를 소유합니다.
- `Common.FlowField.Samples`는 Provider/Controller 공개 계약만 사용하고 Agent 생성·HUD를 소유합니다.

Surface와 Volume Session은 공통 수명·요청 수락·게시 이벤트를 `FlowFieldSessionBase`에서 공유하고, 셀 상태·Raycast·BFS·Volume 안전성 같은 계산 정책은 각 Session에 남깁니다. Manager는 설정을 읽어 요청을 만들고 Session에 전달하며, 계산 단계나 결과 배열을 직접 소유하지 않습니다. Volume Runtime 작업은 공통 `FlowFieldBuildScheduler`에 등록되어 Manager 사이에서 프레임당 2ms deadline을 협력적으로 나눕니다. 큰 배열은 작업 Context가 소유하고 완성 후에만 게시 View로 교체합니다.

GPU 경계는 `IFlowFieldGpuBackend` 하나로 통일합니다. Compute Shader 콜백은 해당 작업의 완료·실패 신호만 기록하고, 결과 검증·Managed fallback·합성·게시의 실행은 Session의 정상 작업 단계에서 수행합니다. 오래된 작업의 콜백은 작업 세대가 맞지 않으면 무시합니다.

## 베이크 모드

`FlowFieldManager.BakeMode`에서 두 모드를 선택할 수 있습니다.

- `RuntimeDynamic`(기본값)은 초기화와 `NotifyCellsDirty()`에서 하향 Raycast Surface를 다시 만들고, 정적 Collider와 등록된 동적 장애물을 합성합니다. Goal/장애물 변경은 Surface를 재사용해 BFS만 갱신합니다. 실제 장애물 마스크가 같으면 요청·Revision·이벤트를 만들지 않습니다.
- `StaticBaked`는 Editor의 `Bake / ReBake Static Flow Field`로 생성한 v5 snapshot을 런타임에 로드합니다. `Surface2D`는 `FlowFieldStaticBakeData`, `Volume3D`는 `FlowFieldVolumeBakeData`를 사용합니다. v4 asset은 자동 변환하지 않고 명시적 ReBake를 요구합니다.

`Surface2D`는 기존 바닥 Raycast·경사·단차 규칙과 8방향 동작을 유지합니다. `Volume3D`는 Manager Bounds를 CellSize 정수배의 월드 축 정렬 격자로 만들고, 각 셀을 물리 큐브로 검사합니다. 높이·법선 배열이나 셀별 GameObject는 만들지 않으며, 셀은 `x + Width * (z + Depth * y)` 순서로 저장합니다. Volume의 방향은 표면에 투영하지 않고 26방향 동일 비용 BFS를 사용하며 대각선의 중간 셀이 모두 비어 있어야 합니다.

두 공간 모드는 GPU Frontier BFS를 사용하고, Compute Shader 미지원·오류·overflow·readback 검증 실패 시 동일 입력의 Managed BFS로 전환합니다. Volume3D 빌드는 모든 Manager가 공유하는 프레임당 2ms 협력 예산으로 진행하며 결과는 완성된 배열을 한 번에 게시합니다. 일반 재빌드에서는 이전 committed field가 계속 샘플됩니다.

샘플 기능 검증용 `FlowFieldSampleAgent`와 `FlowFieldSampleController`는 시스템 런타임 코드와 분리해 `Script/Samples`에 보관합니다.

## 수명 주기

`FlowFieldManager`는 Play Mode의 `Awake`에서 `Init`하고 `OnDestroy`에서 `Release`합니다. `Init`은 공통 Session을 만들고 첫 Field를 `RequestRebuild()`합니다. 상태는 `Uninitialized → Building → Ready`로 관찰할 수 있고, 이후 재빌드에서는 이전 committed field를 유지하며 `IsRebuilding`으로 진행 상태를 확인할 수 있습니다. GPU 오류·overflow·미지원 플랫폼은 동일 입력을 Managed backend로 실행하며, `Surface2D`는 8방향, `Volume3D`는 26방향 FIFO BFS를 사용합니다. `Faulted`에서도 `RequestRebuild()`로 현재 입력을 재시도할 수 있습니다.

`OnDisable`은 진행 중인 Volume 계산을 Suspend하고, 다시 활성화되면 최신 입력으로 재개합니다. Release는 해당 Manager가 소유한 GPU 요청을 완료한 뒤 리소스를 해제합니다. Volume 셀 수는 최대 1,000,000개, Surface 셀 수는 최대 100,000개이며 Volume Manager의 추적 메모리 예산은 기본 512MiB입니다.

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

`TrySample`은 미준비·비정상 좌표·Grid 밖에서 `false`와 정지 샘플을 반환합니다. `Volume3D` 영역 안의 셀은 장애물·Goal 도달 여부와 관계없이 `HasCell == true`, `HasSurface == false`, `SurfaceNormal == Vector3.zero`입니다. 엄격한 `Sample`은 잘못된 사용 시 예외를 유지합니다. 샘플은 개체가 속한 한 셀의 committed 방향만 반환합니다. 영향권 밖 셀은 기본 방향을 사용하고, 영향권 안에서 Goal까지 도달할 수 없는 셀은 정지합니다.

Grid 경계에 넣을 좌표가 필요할 때만 명시적 `ClampPositionToGrid(Vector3)`를 사용합니다. 이 API는 `FlowFieldClampResult.Position`, `ClampedX`, `ClampedY`, `ClampedZ`를 반환하며 일반 `Sample`이 자동으로 Clamp하지는 않습니다. 기존 XZ 전용 변환 API는 Surface2D에서만 사용하고, Volume3D에서는 XYZ 변환 API를 사용합니다.

## Controller와 Modifier

`IFlowFieldController`의 Goal·동적 장애물·Modifier 등록 API는 초기화되고 등록된 대상만 받습니다. null 인자는 `ArgumentNullException`, 수치·범위 오류는 `ArgumentOutOfRangeException`, 구조 불일치는 `ArgumentException`, 생명주기·등록 계약 위반은 `InvalidOperationException`입니다. Modifier Priority 중복, 미등록 대상 해제/Dirty 통지, Compose 중 등록 변경은 즉시 실패합니다. `StateChanged`는 실제 상태 전이마다, `FieldChanged`와 `Revision`은 실제 commit마다 한 번만 발생합니다.

공간을 런타임 중에 바꾸지 않습니다. `IFlowFieldController.SetSpaceMode`는 `Release → SetSpaceMode → Init` 순서에서만 호출할 수 있고, `IFlowFieldProvider.SpaceMode`로 소비자가 방향 적용 방식을 선택합니다. 기존 Modifier의 등록 API는 유지하되, `IFlowFieldVectorModifier.CaptureSnapshot()`은 모든 Modifier가 빌드별 설정을 고정하기 위해 구현해야 합니다. 반환된 `IFlowFieldModifierSnapshot`은 이후 계산에서 live 컴포넌트 상태를 읽지 않습니다. null Snapshot, 유한하지 않은 방향·속도, 음수 속도는 즉시 오류로 처리하며 live `Modify` fallback은 사용하지 않습니다. Snapshot은 Collider 형상·Transform을 복제하는 물리 프록시 계약은 아니므로, 외부 코드가 Revision 통지 없이 상태를 바꾸는 경우까지 자동 일관성을 보장하지 않습니다.

```csharp
IFlowFieldController controller = _manager;
controller.SetGoal(FlowFieldGoalRequest.Position(goalPosition, 10f));
controller.RegisterDynamicObstacle(obstacleCollider);
controller.NotifyObstacleRegionDirty(obstacleCollider.bounds);
```

## ToyProject 샘플

`Scene/FlowFieldSample.unity`는 100×100 Ground, 100×6×100 Bake Bounds, Cell Size 0.5(200×200=40,000 Cell), Obstacle Clearance 0.3의 고정 시드 미로입니다. 외곽·내부 벽은 `FlowFieldObstacle` 레이어 BoxCollider이고, 남서쪽 Spawn 광장은 비워 두었습니다.

재사용 가능한 계층은 `Prefab/FlowFieldShowcase.prefab`에, 런타임 Agent 원본은 `Prefab/FlowFieldSampleAgent.prefab`에 보관합니다. Bake 결과와 공유 재질은 `Settings`에 둡니다.

Play Mode에서 `FlowFieldSampleController`가 40×25 배열의 1,000개 공유 Prefab을 생성한 후에만 시뮬레이션을 시작합니다. Agent는 비키네마틱 Rigidbody, CapsuleCollider(반경 0.25m/높이 0.8m), Continuous Speculative 충돌과 0 마찰·0 반발 PhysicMaterial을 사용합니다. 중앙 `FixedUpdate`가 Flow 방향을 목표 속도 3m/s, 최대 가속도 8m/s²로 `Rigidbody.AddForce`에 전달합니다.

Goal은 맵 안쪽 후보 8개 중 하나를 고정 난수로 15초마다 변경하며 기본 영향 반경은 0m(Global)입니다. Space 키로 즉시 다음 후보를 선택하고 G 키로 Goal을 명시적으로 삭제할 수 있습니다. 화면에는 Ready/Revision, 생성 수, Goal 활성 상태, 변경 횟수, 공간 해시로 계산한 깊은 관통 쌍 수가 표시됩니다.

`FlowFieldShowcaseOverviewController`는 `Baseline → SpeedModifier → NoiseModifier → DynamicObstacle → SampleAndClamp` 모드를 8초마다 순환합니다. `1/2/3`으로 기본·속도·노이즈 모드를 선택하고 `M`으로 동적 장애물, `O`로 Sample/Clamp, `R`로 명시적 Rebuild, `C`로 진단을 실행할 수 있습니다. Space는 Goal 변경, G는 Goal 삭제입니다. Overview Board에는 Modifier/Obstacle 등록 상태, 현재 Sample 방향·속도, Clamp 결과와 Bounds 진단이 함께 표시됩니다. 동적 장애물은 이동할 때마다 즉시 필드를 재계산하지 않고 Dirty 통지 후 명시적인 Rebuild 경계에서만 반영됩니다.

`FlowFieldVolume3DSampleController`는 `20×12×20`, CellSize 1 Volume3D Manager에서 64개 비행 Agent를 생성합니다. 서로 다른 Y의 Goal을 순서대로 선택해 상하 이동과 장애물 우회를 확인할 수 있습니다. Compute Shader는 계속 `Script/Runtime/Core` 아래의 스크립트 계층에 두며 `Resources`로 이동하지 않습니다.

`Scene/FlowFieldVolume3DTest.unity`는 위 구성을 바로 실행할 수 있는 수동 검증 씬입니다. `Space`로 다중 Y Goal을 순환하고, `O`로 DynamicGate를 등록/해제하며, `R`로 재빌드하고, `G`로 Goal을 제거해 기본 방향 흐름을 확인합니다. Goal이 활성화된 상태에서 도달 불가능한 셀은 정지하고, Goal이 없거나 영향권 밖인 셀은 Manager의 Default Direction을 사용합니다. Game View 상태창은 Manager 상태·Revision·Agent 수·Volume 샘플의 `HasCell`/`HasSurface`/방향을 표시하고, Scene View에서는 Manager의 X/Y/Z 단면 Gizmo를 사용할 수 있습니다.

### 샘플 모드와 FlowField 카메라

`StaticBaked` 샘플은 Play Mode에서 Bake Asset을 저장하거나 재베이크하지 않습니다. Bake Asset에 저장된 Goal·장애물 결과를 그대로 사용하므로 Space, G, Gate 토글 같은 런타임 입력은 비활성화되고 HUD에 안내됩니다. Manager 설정과 Asset이 일치하지 않으면 자동으로 덮어쓰지 않고 Editor에서 명시적으로 ReBake해야 합니다. `R`은 기존 Asset을 다시 로드하고 런타임 Modifier를 재합성하는 요청입니다.

`RuntimeDynamic`에서 Goal, 동적 장애물 또는 Modifier를 바꿀 때는 입력을 모두 확정한 뒤 `RequestRebuild()`를 한 번 호출합니다. 샘플은 RuntimeDynamic에서도 Bake Asset 파일을 만들거나 저장하지 않습니다. 2D·3D 샘플은 Manager가 Ready가 되고 유효한 FieldInfo가 게시될 때까지 Agent를 생성하지 않으며, 재빌드 중에는 Agent의 감속을 계속 처리합니다.

FlowField 샘플 카메라는 `FlowFieldFreeCamera`가 소유합니다. TransformPath 카메라에 의존하지 않으며, RMB 드래그로 회전하고 RMB를 누른 상태에서 WASD로 이동, Q/E로 수직 이동, Shift로 가속, 마우스 휠로 이동 속도 조절, F로 FlowField Bounds에 포커스, Escape로 포인터 캡처를 해제합니다.

Volume3D Scene View 표시의 기본 모드는 `FullVolume`입니다. 실제 계산·베이크 격자는 변경하지 않고, 표시 상한인 전체 볼륨 8,192셀 또는 단면 4,096셀 안에서 XYZ 좌표를 균등 추출합니다. 따라서 20×12×20 테스트 격자는 4,800셀과 12개 Y층을 모두 표시하며, 상한을 넘는 큰 격자도 각 축의 양 끝을 포함해 축약 표시합니다. `Slice`에서는 Y/X/Z 중 한 축을 고정해 기존 단면 확인을 할 수 있습니다. 셀 외곽선과 셀 중심 간격은 모두 `CellSize`를 사용합니다.

StaticBaked 편집 화면의 벡터는 베이크에 저장된 기본 필드이고, Play Mode의 벡터는 Speed·Noise Modifier가 합성된 런타임 최종 필드입니다. 표시용 구조 검증은 Editor update의 협력 예산으로 진행되며, 반복 Repaint나 단면 변경은 Physics·BFS·재베이크·공개 Revision을 변경하지 않습니다. 테스트 씬은 방향 화살표가 겹치지 않도록 셀 경계를 숨기고 `Show Vectors`만 활성화합니다. 필요할 때 Inspector의 `Show Cells`로 셀 경계를 별도 확인할 수 있습니다.

## 정상적인 `Try*` API

Core의 `FlowFieldCellSampler.TrySample`과 Bake/Overlap 헬퍼의 `Try*`는 데이터가 아직 준비되지 않았거나 후보가 없는 정상 결과를 `false`로 표현합니다. 공개 Provider 계약에도 동일한 `TrySample(Vector3, out FlowFieldSample)`을 제공합니다.
