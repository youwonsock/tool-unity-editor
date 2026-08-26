# FlowField

Unity 월드에서 Bake한 표면·장애물·Goal·Modifier 데이터를 기반으로 방향과 속도 배율을 제공하는 재사용 모듈입니다. 모듈은 이동 구현을 소유하지 않으며, 사용하는 시스템이 자신의 `FixedUpdate`에서 현재 위치를 Pull 샘플링합니다.

## 요구사항

- Unity `2023.2.20f1` 이상
- Burst `1.8.13`
- Collections `1.4.0`
- Jobs `0.70.0-preview.7`
- Mathematics `1.2.6`

`Assets/Supercent/Common/FlowField` 폴더를 대상 프로젝트의 같은 경로에 복사하면 됩니다. 별도의 runtime assembly를 추가할 필요는 없습니다. `Core` 폴더의 기존 asmdef가 패키지 참조와 저수준 계산을 담당하고, Manager와 Editor는 기본 프로젝트 assembly에서 함께 컴파일됩니다.

Grid는 최대 `100,000 Cell`까지 지원합니다. Manager와 Editor는 `width * depth`를 `long`으로 검증하므로 제한을 넘는 Grid는 Workspace를 할당하지 않고 미준비 상태로 남습니다. 제한 초과로 Ready 상태가 해제되면 `Revision` 증가와 `FieldChanged` 1회가 게시됩니다.

## 시작하기

1. 빈 GameObject에 `FlowFieldManager`를 추가합니다.
2. Manager의 Bake Bounds, Cell Size, Ground Layer를 설정합니다. Ground Layer의 기본값은 `Physics.DefaultRaycastLayers`입니다. 프로젝트가 별도 레이어를 사용한다면 Inspector에서 명시적으로 지정합니다.
3. Manager Inspector의 `Bake Surface`를 누르거나 `Tools/Supercent/FlowField/Bake All Managers In Open Scenes` 메뉴를 실행합니다. Bake가 완료되면 생성된 Surface/Obstacle/Coarse 자산을 Manager에 연결합니다.
4. `IFlowFieldController`로 Goal을 지정하고, 필요한 경우 동적 장애물과 Vector Modifier를 등록합니다.

Manager는 실행 순서 `-200`에서 Dirty 상태를 다음 `FixedUpdate`에 반영합니다. 외부 시스템은 기본 실행 순서의 `FixedUpdate`에서 샘플을 읽으므로, 샘플 호출만으로 즉시 재빌드되지 않습니다.

## 샘플 의미와 장애물 정책

`TrySample`은 Manager가 미준비이거나 위치가 Grid 밖이면 `false`와 정지 샘플을 반환합니다. Grid 안의 Surface 없는 Cell은 `true`이지만 `HasSurface == false`, `Direction == Vector3.zero`, `SpeedMultiplier == 0`입니다. Base Cell이 Blocked이면 이웃과 보간하지 않고 해당 Cell의 Escape/Modifier 결과를 사용합니다. 일반 Cell은 Surface가 유효하고 Blocked되지 않은 이웃만 Bilinear 보간합니다.

`Direction`은 `HasSurface == true`일 때 Surface Normal과 직교하는 접선 방향입니다. Default 방향이 경사면에서 0이 되면 Forward, Right 순으로 fallback하며, Goal·Escape·Modifier가 0이 되는 경우는 정지로 유지됩니다.

Static Obstacle Bake는 `isStatic == true`, Rigidbody 없음, 비Trigger Collider만 포함합니다. 동적 Collider는 `RegisterDynamicObstacle`로 등록하거나 `Enable Unregistered Obstacle Sweep`을 사용해 Dynamic Mask로 처리합니다. Sweep은 Obstacle Layer 전체 NonAlloc 물리 검색을 주기적으로 수행하므로 비용이 있으며, 새 Manager의 기본값은 OFF입니다. 기존 직렬화 값은 유지됩니다. Static Bake가 없으면 Static Mask는 비워지고 Runtime Sweep/등록 장애물만 Dynamic Mask에 기록됩니다.

Combined Mask에 하나라도 Blocked Cell이 있으면 Goal 계산은 전체 Fine Solver를 사용해 장애물을 우회합니다. Blocked Cell이 없을 때만 기존 Coarse/Hierarchical Solver를 사용합니다.

## Provider 사용

Inspector 인터페이스 필드는 Unity 직렬화 대상이 아니므로 `MonoBehaviour`를 직렬화하고 런타임에 계약으로 변환합니다.

```csharp
using Supercent.Common.FlowField;
using UnityEngine;

// _providerObject에는 FlowFieldManager가 붙은 GameObject의 컴포넌트를 지정합니다.
[SerializeField] private MonoBehaviour _providerObject;
private IFlowFieldProvider _provider;

private void OnEnable()
{
    _provider = _providerObject as IFlowFieldProvider;
    if (_provider != null)
        _provider.FieldChanged += OnFieldChanged;
}

private void FixedUpdate()
{
    if (_provider == null
        || !_provider.TrySample(transform.position, out FlowFieldSample sample))
        return;

    Vector3 direction = sample.Direction;
    float speedMultiplier = sample.SpeedMultiplier;
    Vector3 surfaceNormal = sample.SurfaceNormal;
    bool hasSurface = sample.HasSurface;
    // direction과 speedMultiplier를 사용하는 시스템의 이동 정책을 여기에 적용합니다.
}

private void OnDisable()
{
    if (_provider != null)
        _provider.FieldChanged -= OnFieldChanged;
}

private void OnFieldChanged()
{
    // 캐시를 비우고 다음 FixedUpdate에서 IsReady/Revision을 다시 확인합니다.
}
```

실제 코드에서는 Provider가 교체될 때 이전 Provider의 이벤트를 먼저 해지해야 합니다. `FieldChanged`는 샘플을 전달하지 않는 상태 알림이며, 구독자는 `IsReady`와 `Revision`을 조회한 뒤 자신의 캐시를 무효화합니다.

`TrySample`은 Manager가 준비되지 않았거나 위치가 Grid 밖이면 `false`와 기본 샘플을 반환합니다. 유효한 Grid 셀이지만 표면 데이터가 없는 경우에는 기존 동작과 같이 `true`와 `HasSurface == false`가 반환될 수 있습니다.

Grid 경계 보정이 필요한 경우 다음 계약을 사용합니다.

```csharp
if (_provider.TryClampPositionToGrid(
        worldPosition,
        out Vector3 clampedPosition,
        out bool clampedX,
        out bool clampedZ))
{
    // clampedX/clampedZ로 각 축의 보정 여부를 확인합니다.
}
```

## Controller 사용

Manager를 제어할 때는 구체적인 Manager 타입 대신 `IFlowFieldController`를 보관할 수 있습니다.

```csharp
IFlowFieldController controller = _providerObject as IFlowFieldController;
controller?.SetGoalPosition(goalWorldPosition);
controller?.RegisterDynamicObstacle(obstacleCollider);
controller?.NotifyObstacleRegionDirty(obstacleCollider.bounds);
```

Goal·장애물·Modifier 변경은 Dirty 알림으로 누적되고 다음 Manager `FixedUpdate`에서 한 번에 반영됩니다. 결과가 게시되면 `Revision`이 증가하고 `FieldChanged`가 한 번 발생합니다. 사용이 끝난 동적 장애물은 `UnregisterDynamicObstacle`으로 해제합니다.

Manager의 `Enable Unregistered Obstacle Sweep`가 켜져 있으면 Obstacle Layer 전체를 주기적으로 검색합니다. 특정 Collider를 명시적으로 등록해 Layer와 무관하게 반영하려면 이 옵션을 끄고 `RegisterDynamicObstacle`/`UnregisterDynamicObstacle`을 사용합니다.

Provider가 비활성화되면 `IsReady == false`가 되고 준비 상태 변화가 게시됩니다. 재활성화 후 첫 성공적인 Build에서 다시 Ready와 새 `Revision`이 게시됩니다. 외부 어댑터는 `OnEnable`에서 구독하고 `OnDisable`에서 반드시 해지해야 하며, `FieldChanged` 콜백에서는 샘플을 저장하지 말고 `IsReady`와 `Revision`을 다시 조회한 뒤 다음 `FixedUpdate`에서 Pull해야 합니다. 콜백은 무인자 fail-fast 이벤트이므로 구독자 예외가 뒤의 구독자 호출을 막을 수 있습니다.

## 공개 계약

- `IFlowFieldProvider.IsReady`: 현재 샘플을 제공할 수 있는지 여부
- `IFlowFieldProvider.Revision`: 게시된 필드 세대
- `IFlowFieldProvider.FieldChanged`: Ready 또는 샘플 결과 변경 알림
- `IFlowFieldProvider.TrySample`: 월드 위치의 `FlowFieldSample` Pull
- `IFlowFieldProvider.TryClampPositionToGrid`: Grid 안으로 XZ 위치 보정
- `IFlowFieldController`: Goal, 동적 장애물, Vector Modifier의 등록·해제·Dirty 알림

`FlowFieldSample`은 `Direction`, `SpeedMultiplier`, `SurfaceNormal`, `HasSurface`를 가진 불변 구조체입니다. 이동·충돌·가속·회전 정책은 이 모듈의 책임이 아니며 Provider를 사용하는 쪽에서 결정합니다.
