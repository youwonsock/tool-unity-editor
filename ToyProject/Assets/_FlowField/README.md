# FlowField

씬의 장애물과 Goal을 기준으로 이동 가능한 공간에 방향과 속도 배율을 제공하는 Unity 방향장 시스템입니다. `Surface2D`는 바닥 표면을, `Volume3D`는 Bounds 내부의 정육면체 셀을 계산하며 `RuntimeDynamic`과 `StaticBaked` 실행 모드를 지원합니다. 이동 주체는 자신의 업데이트 주기에서 게시된 Field를 조회해 이동에 적용합니다.

## 목차

- [프로젝트 개요](#프로젝트-개요)
- [프로젝트 요약](#프로젝트-요약)
- [사용 방법](#사용-방법)
- [동적 장애물 추적 조건](#동적-장애물-추적-조건)
- [재요청과 강제 갱신 API](#재요청과-강제-갱신-api)
- [Modifier Fault 복구](#modifier-fault-복구)
- [격자·메모리 제한](#격자메모리-제한)
- [구조와 실행 흐름](#구조와-실행-흐름)
- [수명과 결과 게시](#수명과-결과-게시)
- [기능 상세](#기능-상세)
- [검증과 지원 범위](#검증과-지원-범위)

## 프로젝트 개요

| 항목 | 내용 |
| --- | --- |
| 개발 인원 | 1명 — 유원석 (You Won Sock) |
| GitHub | [youwonsock](https://github.com/youwonsock) |
| 이메일 | qazwsx233434@gmail.com |
| 프로젝트 목적 | 표면·볼륨 기반 방향장 계산, 정적 베이크, 런타임 샘플링과 Unity 실행 검증 |
| 개발 언어 | C#, Burst Job |
| 개발 도구 | Unity 2023.2.20f1, Visual Studio 또는 Rider |
| 샘플 표시 환경 | Universal Render Pipeline 16.0.6, UGUI (방향장 런타임과 분리) |
| 런타임 어셈블리 | `Common.FlowField.Core`, `Common.FlowField.Runtime` |
| 에디터·샘플 어셈블리 | `Common.FlowField.Editor`, `Common.FlowField.Samples` |
| 측정 어셈블리 | `Common.FlowField.PlayerProbe` (Editor와 측정 빌드 전용) |
| 회귀 테스트 어셈블리 | `Common.FlowField.Tests` (Editor 전용) |

## 프로젝트 요약

FlowField는 Manager가 작성 설정과 현재 입력을 확인한 뒤 Surface2D 또는 Volume3D 전용 계산 요청을 만들고, 모드별 Session이 계산 또는 StaticBaked 데이터 로드를 수행합니다. 완성된 기본 필드에 필요한 Default Direction과 Modifier를 적용한 결과만 게시하며, 이동 주체는 게시된 결과를 읽어 자신의 이동을 결정합니다.

1. `FlowFieldManager`가 Bounds·CellSize·공간 모드·Goal·장애물·Modifier 설정을 수집합니다.
2. Surface2D는 표면 데이터와 8방향 탐색을, Volume3D는 XYZ 셀과 26방향 탐색을 사용하는 모드별 요청을 실행합니다.
3. RuntimeDynamic은 최신 입력을 수락해 처리를 예약하고, StaticBaked는 v6 Bake Asset을 로드합니다. 두 모드 모두 협력 Scheduler가 작업을 진행합니다. 메인 스레드 예산은 프로젝트 전체에 하나이며 `FlowFieldBuildSettings.BudgetMilliseconds`(기본 2ms)로 정합니다.
4. 계산 결과에 Default Direction과 작업별 Modifier Snapshot을 합성합니다.
5. Surface2D는 `FlowFieldFieldStore`, Volume3D는 `FlowFieldVolumeFieldData`에 완성된 필드를 게시합니다.
6. `IFlowFieldProvider`가 방향·속도·상태를 읽기 전용으로 제공하고, 이동 주체가 이를 자신의 업데이트에서 적용합니다.

런타임 흐름

```text
Manager 설정·Goal·Obstacle·Modifier
  → Surface / Volume 전용 계산 요청
  → BuildDriver.LateUpdate (게임 시간이 멈춰도 진행)
  → 모드별 Session의 계산 또는 StaticBaked 로드
  → Default Direction·Modifier 합성
  → Surface FieldStore / Volume 결과 저장소 게시
  → IFlowFieldProvider 조회
  → 이동 주체의 이동·감속 적용
```

모드별 Session이 최신 수락 입력·실행 중 입력·게시 결과를 소유합니다. `FlowFieldSessionBase`는 수명 표·공개 상태·이벤트 전달·Scheduler 등록을 공유하고, 최신 요청과 대기 표시는 `FlowFieldRequestInbox`가 맡습니다. 같은 Grid·Source에서 읽을 수 있는 게시 결과는 다음 계산 중에도 유지합니다. 구체적인 수명·입력·Revision 정책은 아래의 [수명과 결과 게시](#수명과-결과-게시)와 [TechnicalNotes.md](Docs/TechnicalNotes.md)에 정리합니다.

## 사용 방법

1. Unity 2023.2.20f1에서 `ToyProject`를 열고 `Assets/_FlowField/Scene/FlowFieldSample.unity` 또는 `Assets/_FlowField/Scene/FlowFieldVolume3DTest.unity`를 실행합니다.
2. 새 씬에서는 `FlowFieldManager`의 Bounds·CellSize·공간 모드와 LayerMask를 지정합니다. Manager Transform은 회전 없이 단위 Scale을 사용합니다. Surface2D에는 바닥 Layer와 경사·단차 조건도 필요합니다.
3. `RuntimeDynamic`은 초기화 후 런타임 입력을 계산합니다. `StaticBaked`는 Editor에서 Bake/ReBake한 v6 Asset을 연결한 뒤 초기화합니다. Goal·장애물 설정이 바뀌면 Editor에서 다시 베이크합니다.
4. 이동 주체는 `IFlowFieldProvider.TrySample()`의 방향·속도 배율을 자신의 업데이트에서 적용합니다. 조회 실패와 조회에 성공한 정지 샘플을 모두 처리해야 합니다.
5. 공개 입력 명령은 `IFlowFieldController`를 사용합니다. 최신 설정의 재요청과 Manager·Session Fault 재시도에는 `RequestRebuild()`를 사용하며, 바닥·장애물 형상 변경은 아래의 갱신 API를 선택합니다. 실행 모드를 바꿀 때는 `Release()` → `SetSpaceMode()` → `Init()` 순서를 따릅니다.

`RequestRebuild()`는 계산 완료를 보장하는 동기 호출이 아닙니다. Surface의 Modifier 등록·값/영역 변경·자동 관측은 첫 요청 수락 이후 기존 요청에 처리를 예약하므로 매번 추가 호출할 필요가 없습니다. Manager는 Surface 작업 직전 현재 설정을 다시 정규화합니다. Grid·Source 무효화로 명시적 재빌드가 필요한 상태에서는 유효한 새 요청을 제출해야 처리를 재개합니다. Scheduler는 Manager가 제출한 Surface 요청만 빌드하므로, 풀이 중에 바뀐 Goal은 풀이가 끝난 뒤 다음 제출에서 반영됩니다.

### 샘플 반환값과 이동 예제

`TrySample()`의 반환값은 조회 성공 여부입니다. `true`여도 Goal 도착·장애물·안전성 제한 등으로 방향 또는 속도가 0일 수 있습니다. 이동 여부는 반환된 방향과 속도 배율로 결정합니다.

| 반환값·멤버 | 의미와 처리 |
| --- | --- |
| `TrySample() == false` | 미준비, 비정상 좌표, 영역 밖 또는 현재 게시 결과를 사용할 수 없는 상태입니다. 출력은 `FlowFieldSample.Stopped`이며 이번 업데이트에서 정지합니다. |
| `TrySample() == true` | 해당 위치의 샘플을 읽었습니다. 안전성 검사로 정지한 셀에서도 `true`와 속도 0을 반환할 수 있습니다. |
| `HasCell` | 샘플에 격자 셀이 존재합니다. 장애물 여부나 이동 가능 여부를 뜻하지 않습니다. |
| `HasSurface`·`SurfaceNormal` | Surface2D의 표면 정보입니다. Volume3D는 정상 셀도 `HasSurface == false`, 법선은 0이므로 공통 이동 조건으로 사용하지 않습니다. |
| `Direction` | 현재 사용할 이동 방향입니다. 정지 시 0이며, 막힌 셀에서는 탈출 방향이 제공될 수 있습니다. |
| `SpeedMultiplier` | 이동 속도에 곱할 배율입니다. 0이면 정지합니다. 기본 이동 속도와 구분합니다. |

다음은 Surface2D·Volume3D에서 함께 사용할 수 있는 최소 Transform 이동 예제입니다. Inspector에서 Manager를 연결합니다. Manager는 Play 진입 시 `Awake()`에서 초기화하며, 최초 게시 전에는 `TrySample()`이 실패할 수 있습니다.

```csharp
using Common.FlowField;
using UnityEngine;

public sealed class FlowFieldReadmeMover : MonoBehaviour
{
    [SerializeField] private FlowFieldManager _flowField = null;
    [SerializeField, Min(0f)] private float _moveSpeed = 3f;

    private void FixedUpdate()
    {
        IFlowFieldProvider provider = _flowField;
        Vector3 velocity = Vector3.zero;

        if (_flowField != null
            && provider.TrySample(transform.position, out FlowFieldSample sample)
            && sample.HasCell
            && sample.SpeedMultiplier > 0f)
        {
            velocity = sample.Direction * (_moveSpeed * sample.SpeedMultiplier);
        }

        transform.position += velocity * Time.fixedDeltaTime;
    }
}
```

Rigidbody·CharacterController를 사용하는 경우 계산한 속도를 해당 이동 경로에 적용합니다. 충돌 처리·표면 높이 보정·Agent 간 회피는 이동 시스템에서 처리합니다. 기존 속도를 보관하는 이동 시스템에서는 조회 실패나 정지 샘플에도 속도를 0으로 갱신해야 합니다.

### 동적 장애물 추적 조건

`RuntimeDynamic`은 등록·스윕·변경 알림으로 수락한 장애물 입력을 계산합니다. **미등록 장애물의 자동 스윕은 기본적으로 꺼져 있습니다.** 이동하는 문이나 벽을 다음 경로 중 하나로 연결해야 자동 변경 감지가 동작합니다.

| 경로 | 설정과 수명 |
| --- | --- |
| 명시적 등록 | Manager 초기화 후 `RegisterDynamicObstacle(collider)`로 Collider를 등록합니다. 등록 Collider의 이동·제거 등을 관측하며 중복 등록은 `false`를 반환합니다. 명시적 등록은 `UnregisterDynamicObstacle(collider)`로 해제합니다. 스윕 ON 상태의 대상 Layer Collider는 등록 해제 후에도 스윕으로 관측될 수 있습니다. |
| 미등록 스윕 | Inspector에서 **Enable Unregistered Obstacle Sweep**을 켜고 대상 Collider의 Layer를 **Obstacle Layer**에 포함합니다. 스윕은 해당 Layer의 미등록 장애물 변경을 감지합니다. |

장애물 쿼리에 영향을 주려면 GameObject와 Collider가 활성 상태여야 하며, 쿼리는 **Trigger를 무시**합니다. Trigger가 필요한 Modifier 영향 Collider와 구분합니다. 명시적 등록 경로는 지정된 Collider 자체를 검사하므로 미등록 스윕의 Layer 필터와 구분합니다. Layer에 넣는 것만으로 스윕 OFF 상태의 미등록 장애물이 계속 추적되지는 않습니다.

Surface2D의 정적 장애물 마스크에는 **Obstacle Layer의 Collider 중 Rigidbody가 없고 동적 장애물로 등록하지 않은 것**이 들어갑니다. GameObject의 Static 플래그는 Player에서 항상 꺼져 있으므로 보지 않습니다. 이동용 장애물은 등록하거나 Rigidbody를 붙여 베이크에서도 빼고, 등록·해제하면 정적 마스크를 자동으로 다시 만듭니다. 미등록 스윕을 켜면 스윕이 Layer 전체를 추적하므로 정적 마스크는 비워 둡니다. Volume3D의 RuntimeDynamic 장애물 계산은 Obstacle Layer의 Collider와 명시적 등록 Collider를 검사합니다.

등록·변경 알림은 초기화된 `RuntimeDynamic` Manager가 입력을 수락할 수 있을 때 호출합니다. `StaticBaked`의 동적 등록·변경 알림은 거부됩니다. `Release()`는 등록 집합을 정리하므로 이후 `Init()`한 새 Session에는 필요한 동적 장애물을 다시 등록합니다.

### 재요청과 강제 갱신 API

| API | Surface2D | Volume3D | 모드 조건 |
| --- | --- | --- | --- |
| `RequestRebuild()` | 최신 작성 설정을 다시 수락합니다. 기본 입력이 같으면 기존 표면·내비게이션을 재사용할 수 있습니다. | 같은 설정이라도 새 작업을 예약합니다. | 두 모드 모두 사용 가능하며 Manager·Session Fault의 명시적 재시도 경로입니다. |
| `NotifyCellsDirty()` | Grid dirty를 표시해 바닥 Raycast·표면과 후속 계산을 다시 준비합니다. 바닥 Collider의 위치·형상만 바뀐 경우 사용합니다. | 현재 설정으로 새 전체 작업을 예약합니다. | `RuntimeDynamic` 전용입니다. |
| `NotifyObstacleRegionDirty(bounds)` | 해당 영역의 동적 장애물·탈출·최종 결과를 dirty로 표시합니다. | 영역의 안전성 상태를 갱신하고 새 계산을 예약합니다. | `RuntimeDynamic` 전용이며 등록·스윕 경로로 관리하는 장애물의 수동 변경 알림입니다. |

Surface2D에서 Bounds·CellSize 등 설정은 그대로이고 **바닥 형상만 변경됐다면 `RequestRebuild()`만으로 Raycast가 다시 실행된다고 가정하면 안 됩니다.** `NotifyCellsDirty()`로 표면 갱신을 요청합니다. 반대로 Modifier 값만 바뀌었다면 해당 setter·변경 알림으로 최종 합성을 예약하면 됩니다.

장애물 이동을 수동으로 알릴 때는 **이전 Bounds와 현재 Bounds의 합집합**을 전달해 이전 점유도 제거할 수 있게 합니다. 영역 알림은 Collider를 등록하지 않으며, Surface의 **미등록·스윕 OFF** 장애물을 동적 추적 대상으로 바꾸거나 정적 마스크를 다시 만드는 API가 아닙니다. 이 상태의 정적 장애물 형상이 바뀌었다면 `NotifyCellsDirty()`로 기본 데이터를 다시 준비합니다.

Dirty 알림은 첫 요청을 수락한 뒤 기존 요청에 처리를 예약하므로 보통 추가 `RequestRebuild()`가 필요하지 않습니다. Surface의 Grid·Source 설정이 stale 상태라면 먼저 유효한 설정으로 `RequestRebuild()`를 제출한 뒤 다른 입력 명령을 사용합니다. 어느 API도 호출 반환 시 계산 완료를 보장하지 않습니다. `StaticBaked`의 재요청은 저장된 v6 기본 데이터의 로드·합성이며 씬의 내비게이션을 새로 베이크하지 않습니다.

### Modifier 필수 설정

1. Play 진입 전에 영향 영역용 Collider와 `FlowFieldSpeedModifier` 또는 `FlowFieldNoiseModifier`를 배치합니다. Collider를 먼저 추가하면 Modifier의 Reset에서 같은 GameObject의 Collider를 기본 참조로 잡을 수 있습니다.
2. Modifier Inspector의 **Flow Field Manager**와 **Influence Collider**를 지정합니다. 영향 Collider는 **Is Trigger**가 켜져 있어야 하며, MeshCollider를 사용할 때는 **Convex**도 필요합니다. 실제 영향이 생기려면 Collider와 GameObject가 활성 상태여야 합니다.
3. 같은 Manager에 등록할 Modifier마다 **Priority를 서로 다르게** 지정합니다. 낮은 값부터 합성하며, 같은 Priority는 등록 순서와 관계없이 거부합니다. 기본값은 Speed 100, Noise 200이므로 Speed를 두 개 쓰면 한쪽 Priority를 101 등으로 변경합니다.
4. Speed의 배율과 Noise의 각도·주파수·Seed를 설정합니다. 컴포넌트는 Play 진입 시 초기화되고 활성 상태에서 Manager에 등록됩니다. 같은 컴포넌트를 수동으로 중복 등록할 필요가 없습니다.
5. 런타임에는 `SetSpeedMultiplier()`, `SetNoise()`, 등록 후 `SetPriority()` 등 공개 메서드로 값을 변경합니다. Collider 이동·활성 변화는 관측 경로가 감지합니다. Surface는 최초 요청 이후 Modifier 변경을 다음 Pump에 예약하며 StaticBaked에서도 최종 합성은 가능합니다.

Modifier는 막힌 셀, Goal Anchor, 도달할 수 없는 셀과 기본 방향이 0인 셀에는 적용하지 않습니다. 이 셀은 속도 0으로 정지하며 Surface2D와 Volume3D가 같은 규칙(`FlowFieldVectorUtility.CanApplyModifier`)을 사용합니다. 예를 들어 Surface2D에서 Default Direction이 바닥 법선과 평행하면 그 셀은 정지 샘플을 반환합니다.

설정 조건을 어기면 등록이 거부되거나 변경 관측 중 Session이 Fault 상태가 될 수 있습니다. 직접 `IFlowFieldVectorModifier`를 구현할 때는 `Observe()`에서 유효한 Collider와 유일한 Priority, `Revision`을 돌려주고, `CaptureSnapshot()`이 반환한 객체는 이후의 `Modify()`에서 변경 가능한 컴포넌트 값을 다시 읽지 않아야 합니다. `Observe()`는 관측 주기마다 호출되므로 할당하지 않게 작성합니다. 값 변경은 `Revision`을 올려 자동 관측에 맡기거나 `NotifyModifierChanged(modifier, FlowFieldModifierChange.Value)`로 알립니다. 통지는 `Revision`과 무관하게 다시 합성합니다.

`Observe()`, `CaptureSnapshot()`, `Modify()` 안에서 호출할 수 있는 Manager 명령은 다음과 같습니다.

| 명령 | 동작 |
| --- | --- |
| `TrySample` 등 읽기 | 바로 실행합니다 |
| `RegisterVectorModifier`, `UnregisterVectorModifier`, `NotifyModifierChanged`, `RegisterDynamicObstacle`, `UnregisterDynamicObstacle` | 진행 중인 작업이 끝난 뒤 실행합니다. 그 사이 Session이 멈췄다면 실행하지 않습니다 |
| 위 등록 명령의 반환값 | `true`는 접수되었다는 뜻입니다. 처리 전에 Session이 Fault나 해제로 멈추면 등록은 버려지고 경고를 남깁니다 |
| Manager를 끄거나 파괴하는 호출 | `OnDisable`, `OnDestroy`의 처리를 작업이 끝난 뒤로 미룹니다 |
| `Init`, `RequestRebuild`, `Release`, `SetSpaceMode`, `SetGoal`, `NotifyCellsDirty`, `NotifyObstacleRegionDirty` | `InvalidOperationException`을 던집니다. 잡지 않으면 Session이 Fault 상태가 됩니다 |

`FieldChanged`와 `StateChanged` 핸들러는 작업이 끝난 뒤 순서대로 호출하며 중첩되지 않습니다. 핸들러에서는 모든 명령을 호출할 수 있습니다. 핸들러가 `Release`·`Suspend`를 호출하면 이전 수명의 남은 이벤트는 버리고, `Released`는 항상 전달합니다. 인자로 받은 상태는 이벤트를 만든 시점의 값이므로, 최신 값은 `State`로 읽습니다.

### Modifier Fault 복구

`manager.IsFaulted`와 `modifier.IsFaulted`는 서로 다른 상태입니다. Manager·Session은 원인을 수정한 뒤 `manager.RequestRebuild()`로 재시도합니다. Modifier의 `Init()` 실패로 생긴 자체 Fault는 Manager 재빌드나 Inspector 값 수정만으로 해제되지 않습니다.

1. Modifier를 비활성화합니다. 정상 Manager에서 등록이 해제된 상태로 값을 고치려면 비활성 상태를 유지합니다.
2. `modifier.Release()`로 초기화 상태와 자체 Fault를 해제합니다.
3. Manager 참조·영향 Collider·유일한 Priority·Modifier 값을 유효하게 수정합니다. 자체 Fault가 해제된 뒤 공개 setter를 호출합니다.
4. Manager가 정상 초기화되어 입력을 수락하는 상태에서, Modifier를 비활성화한 채 `modifier.Init()`을 호출합니다.
5. 원래의 `enabled` 값을 복원합니다. 활성화 시 `OnEnable()`이 초기화된 Modifier를 등록합니다.

다음은 **Manager가 Ready이고 참조·Collider·Priority는 유효하며 Speed 값 오류만 있는 경우**의 예제입니다. 컴포넌트가 활성 상태인 채 Release 후 setter를 호출하면 등록 상태 검사에 걸릴 수 있으므로 비활성화 상태에서 값을 수정합니다.

```csharp
using Common.FlowField;

public static class FlowFieldReadmeRecovery
{
    public static void RecoverSpeedValue(FlowFieldSpeedModifier modifier)
    {
        if (modifier == null || !modifier.IsFaulted)
            return;

        bool wasEnabled = modifier.enabled;
        modifier.enabled = false;
        try
        {
            modifier.Release();
            modifier.SetSpeedMultiplier(1.5f);
            modifier.Init();
        }
        finally
        {
            modifier.enabled = wasEnabled;
        }
    }
}
```

Manager도 함께 Fault 상태라면 Modifier를 비활성화·Release해 설정을 먼저 수정하고, Manager도 `Release()` → `Init()`으로 Session을 재생성해 정상 상태를 확인한 뒤 Modifier의 Init·활성 복원을 진행합니다. Manager의 새 Session에 필요한 동적 장애물 등록도 복원합니다.

## 구조와 실행 흐름

<img src="Docs/Images/flowfield-uml.png" alt="FlowField 요청부터 결과 조회까지의 핵심 흐름" width="70%">

[핵심 흐름 원본 이미지](Docs/Images/flowfield-uml.png) · [PlantUML 원본](Docs/flowfield-uml.puml)

핵심 흐름은 요청 수락, 모드별 실행 경계, 완성 결과 게시와 샘플 조회를 보여줍니다. 상세 흐름은 책임별 호출을 나누어 다음 문서에서 확인할 수 있습니다.

- [Surface2D 상세 흐름](Docs/TechnicalNotes.md#surface2d-상세-흐름): 표면·장애물·Goal 준비, BFS 완료, Modifier 합성과 FieldStore 게시.
- [Volume3D 상세 흐름](Docs/TechnicalNotes.md#volume3d-상세-흐름): 입력 관측, 협력 Scheduler, BuildContext 단계, 불확실 영역과 결과 게시.

### 클래스·인터페이스별 역할

| 경계 | 클래스·인터페이스 | 역할 |
| --- | --- | --- |
| 공개 조회·명령 | `IFlowFieldProvider`, `IFlowFieldController` | 게시 결과 조회, 초기화·해제·재빌드와 입력 변경 |
| Modifier 계약 | `IFlowFieldVectorModifier`, `IFlowFieldModifierSnapshot` | 메타데이터 관측과 작업별 고정 값 계산 |
| Unity 연결 | `FlowFieldManager` | 직렬화 설정, 활성 Session, 이벤트 구독과 수명 전이 |
| 공통 Session 경계 | `FlowFieldSessionBase`, `FlowFieldSessionLifecycle`, `FlowFieldRequestInbox` | 수명 표·공개 상태·이벤트 전달·Scheduler 등록, 최신 요청과 대기 표시. 모드별 배열은 자식 소유 |
| Surface 실행 | `FlowFieldSession`, `FlowFieldSurfaceBaseStage`, `FlowFieldSurfaceSolveStage`, `FlowFieldSurfaceComposeStage`, `FlowFieldComposeRun` | 단계 순서와 수명, 표면·장애물·Goal 준비, 풀이 실행, 프레임에 나눈 합성과 원자적 결과 게시. 끝난 풀이와 합성은 Grid나 설정이 바뀌거나 Goal이 사라진 경우가 아니면 게시 |
| Surface 기본 계산 | `FlowFieldSurfaceBuildPipeline`, `FlowFieldGoalPipeline`, `FlowFieldGoalTracker`, `FlowFieldObstaclePipeline` | 표면·장애물 준비, Goal 해석·변경 관측과 탐색 |
| Surface Modifier | `FlowFieldModifierRegistry`, `FlowFieldModifierPipeline` | 등록·메타데이터 관측, 영역 갱신·Snapshot 캡처·최종 합성. 영역 버퍼는 Registry Entry에 지연 할당 |
| Surface 게시 | `FlowFieldFieldStore` | 게시 Grid·Surface·해석된 Goal·최종 방향·속도의 단일 저장소 |
| Volume 실행 | `FlowFieldVolumeSession` | 최신 요청 수락, 작업 취소·수명, Physics·풀이 Job·Scheduler 연결과 게시 |
| Volume 작업 | `FlowFieldVolumeBuildContext`, `FlowFieldVolumeNavigation` | 한 작업의 배열·진행 커서와 셀 중심 좌표. 26방향 탐색은 `FlowFieldVolumeSolveJob`이 맡음 |
| Volume 셀 쿼리 | `FlowFieldVolumeCellQuery`, `FlowFieldCellRegionCursor` | 스텝(64셀)마다 Physics 후보를 한 번 모아 후보가 닿는 셀만 정확히 검사. 추적한 변경과 Modifier 영향 범위의 셀만 순회 |
| Volume 입력 관측 | `FlowFieldVolumeModifierRegistry`, `FlowFieldVolumeObstacleTracker` | Modifier 관측·캡처와 Collider 변경 Bounds 관측 |
| Volume 안전성·게시 | `FlowFieldVolumeSafetyState`, `FlowFieldVolumeFieldData`, `FlowFieldVolumeSpareArrays` | 불확실 영역의 보수적 정지, 완성 결과 보관, 교체된 결과 배열 재사용. 뷰로 내보낸 결과의 배열은 재사용하지 않음 |
| 격자·읽기 | `FlowFieldGridSpace`, `FlowFieldReadView` | 월드/셀 좌표 변환과 내부 읽기 전용 셀 조회 |
| 정적 입력 | `FlowFieldStaticBakeData`, `FlowFieldVolumeBakeData`, `FlowFieldBakeBlob` | Surface2D·Volume3D의 v6 베이크 저장·검증 |
| 풀이 Job | `FlowFieldVolumeSolveJob`, `FlowFieldSurfaceSolveJob`, `FlowFieldSurfaceTopologyJob`, `IFlowFieldSolveBackend` | 내비게이션 계산을 Burst Job으로 실행. 풀이는 프레임을 막지 않게 워커 스레드에서 돌리고 매 프레임 완료를 확인. Surface Topology는 메인 스레드에서 실행. 게시 결과는 관리 배열이므로 샘플링과 베이크는 Native 타입을 다루지 않음 |
| 협력 실행 | `IFlowFieldBuildOperation`, `FlowFieldBuildScheduler`, `FlowFieldBuildDriver`, `FlowFieldBuildSettings` | 실행 가능한 Surface·Volume 작업을 공통 CPU 예산(기본 2ms)으로 진행. Session 수명과 취소는 Session 소유 |

## 수명과 결과 게시

- **수락·실행·게시 입력**: 최신 요청은 다음 작업을, 실행 중 입력은 해당 계산을, 게시 입력은 현재 공개 결과를 설명합니다. 서로 다른 시점의 소유권이며 연속 요청은 최신 값과 대기 플래그로 병합합니다.
- **Surface 조회**: 일반 샘플과 해석된 Goal 메타데이터는 항상 게시 FieldStore에서 읽습니다. Modifier 합성 중의 작업 배열은 노출하지 않습니다. 장애물·탈출·연결 안전성 검사는 최신 관측을 반영해 이동을 제한할 수 있습니다. 요청 Goal과 게시 결과의 해석된 Goal은 별도 의미를 유지합니다.
- **Volume 부분 재계산**: 입력이 같으면 Modifier 변경은 게시된 내비게이션에 Modifier만 다시 합성하고, Goal만 바뀌면 장애물 결과와 Topology를 재사용합니다. 등록·스윕으로 추적하지 않는 Collider는 다음 전체 빌드에서 반영되므로, 이런 Collider가 바뀌면 `RequestRebuild()`나 `NotifyCellsDirty()`를 호출합니다. Volume의 이 두 호출은 Goal이 함께 바뀌어도 장애물을 다시 조회합니다. `SetGoal()` 뒤에 `RequestRebuild()`를 덧붙이면 Goal 재사용 효과를 잃으므로, Goal만 바꿀 때는 `SetGoal()`만 호출합니다. Goal이 같은 셀 안에서 움직이면 결과가 같으므로 빌드하지 않습니다. 이때 Revision은 그대로이고 `FieldChanged`도 발생하지 않으며, `FlowFieldFieldInfo`의 요청 Goal만 최신 값으로 바뀝니다.
- **Revision과 참조**: Surface Revision은 셀 데이터·Goal의 실질 변경에 따라 증가합니다. 같은 내용의 Snapshot 교체는 Revision 증가 없이 게시 Grid·Surface 참조를 갱신할 수 있습니다. Volume Revision은 완료 결과 교체 정책을 따릅니다.
- **움직이는 장애물**: BFS나 Volume 빌드 중 장애물만 바뀌면 그 결과를 게시하고 재빌드를 예약합니다. 조회는 최신 장애물 상태를 다시 검사합니다. Volume은 이미 반영한 불확실 영역을 게시 때 지우므로, 빌드 중 장애물이 지나간 영역만 다음 게시까지 정지합니다. Surface는 Grid나 설정이 바뀌었거나 Goal이 사라진 풀이·합성 결과만 거부합니다. Goal이 바뀐 결과는 게시한 뒤 최신 Goal을 다시 계산하므로 Goal이 풀이보다 빨리 움직여도 게시가 멈추지 않습니다. 이때 해석된 Goal은 요청 Goal보다 잠시 뒤처질 수 있습니다.
- **재진입 없음**: 이벤트 핸들러는 작업이 끝난 뒤 실행하고, Modifier 코드에서는 다른 작업을 시작할 수 없습니다. 작업 코드는 외부 호출 뒤에 수명을 다시 확인하지 않습니다. 대기 중인 이벤트만 만들어진 수명 세대를 비교하고, `Released`는 항상 전달합니다.
- **활성·실패 처리**: `OnDisable`은 실행을 중단하고 재활성화는 현재 수명의 재계산을 요청합니다. Manager·Session Fault는 명시적인 `RequestRebuild()`로 재시도하며 Modifier 자체 Fault는 위의 별도 복구 절차를 따릅니다. 실행할 수 없는 Volume의 대기 요청은 보관하되 Scheduler에서 예산을 반복 소비하지 않습니다. 꺼진 Manager에 들어온 장애물 입력도 재활성화 때까지 빌드하지 않고, 꺼진 동안 켠 Modifier는 재활성화 때 연결합니다. 설정이 stale이어도 Modifier와 동적 장애물의 등록 해제는 할 수 있습니다.
- **정적 입력**: 두 베이크 형식 모두 v6만 허용합니다. 다른 형식의 자동 변환·마이그레이션은 제공하지 않으므로 이전 형식 Asset은 ReBake해야 합니다. Volume3D v6은 방향·속도를 저장하지 않고 NextCell과 베이크 시점의 Default Direction에서, 탈출 방향은 셀당 1바이트의 면 인덱스에서 로드 시 재구성합니다. Surface2D v6은 높이·법선·탈출·Goal 방향을 `byte[]`로 묶어 비트 그대로 저장합니다. StaticBaked 내비게이션은 고정하며 Default Direction·Modifier는 런타임에 합성합니다. 영향 범위 밖 셀은 두 모드 모두 Manager의 현재 Default Direction을 따릅니다.

## 기능 상세

### FlowField 베이킹

**목적**

Editor에서 씬의 표면 또는 3D 볼륨을 미리 계산해 v6 Bake Asset으로 저장하고, Play Mode의 StaticBaked Session이 저장된 내비게이션 결과를 빠르게 로드하도록 합니다. Surface2D 베이크 결과는 2D 샘플 씬에서, Volume3D 베이크 결과는 3D 테스트 씬에서 확인합니다.

**핵심 구현**

- Surface2D는 `FlowFieldStaticBakeData`, Volume3D는 `FlowFieldVolumeBakeData`에 모드별 격자와 탐색 결과를 저장합니다.
- Editor에서 설정·Goal·장애물 입력을 확인한 뒤 Bake/ReBake하고, StaticBaked 실행에서는 저장된 Asset을 읽습니다.
- StaticBaked Play Mode에서는 샘플이 Goal·동적 장애물을 변경하거나 Bake Asset을 저장하지 않습니다.
- Default Direction과 Modifier 합성은 저장된 기본 내비게이션 데이터 로드 이후의 런타임 처리로 구분합니다.
- 왼쪽은 2D 씬의 Surface2D StaticBaked 로드 결과, 오른쪽은 3D 씬의 Volume3D StaticBaked 로드 결과입니다.

<p align="center">
  <img src="Docs/Images/feature-staticbaked-surface.png" alt="FlowFieldSample 2D 씬의 Surface2D StaticBaked 로드 결과" width="48%">
  <img src="Docs/Images/feature-staticbaked-volume.png" alt="FlowFieldVolume3DTest 3D 씬의 Volume3D StaticBaked 로드 결과" width="48%">
</p>

### 2D FlowField

**목적**

바닥 표면을 따라 이동하는 주체를 위해 Raycast 기반 Surface2D 방향장을 생성합니다. 표면 높이·법선과 경사·단차·장애물 조건을 계산해 8방향 이웃 중 Goal로 향하는 흐름을 제공합니다.

**핵심 구현**

- Surface 격자는 XZ 평면에 배치하고 각 셀의 바닥 높이와 표면 상태를 별도로 보관합니다.
- RuntimeDynamic에서 Goal·등록/스윕 장애물·Modifier 변경을 최신 요청에 반영하고 Manager의 실행 경계에서 계산합니다. 바닥 형상만 변경되면 `NotifyCellsDirty()`로 표면 갱신을 요청합니다.
- Agent는 Provider의 방향·속도를 자신의 업데이트에서 적용하고 미준비·영역 밖 결과에서는 정지 또는 감속합니다.
- 왼쪽은 기본 Ready 상태, 오른쪽은 Goal·장애물 입력 변경 후 새 Revision이 게시된 상태를 보여줍니다.

<p align="center">
  <img src="Docs/Images/feature-surface2d-runtime-baseline.png" alt="Surface2D RuntimeDynamic 기본 Ready 실행 화면" width="48%">
  <img src="Docs/Images/feature-surface2d-runtime-rebuild.png" alt="Surface2D Goal·장애물 변경 후 재빌드 실행 화면" width="48%">
</p>

### 3D FlowField

**목적**

바닥에 제한되지 않는 이동을 위해 설정한 Bounds 전체를 CellSize 정육면체 셀로 채우고 XYZ 방향장을 생성합니다. 비행 주체가 상하 Goal과 장애물을 입체적으로 우회할 수 있도록 합니다.

**핵심 구현**

- 테스트 격자 `20×12×20`을 `4,800`개 셀로 만들며 개별 셀 GameObject는 생성하지 않습니다.
- 26방향 동일 비용 BFS와 대각선 중간 셀 통과 검사를 사용해 코너가 막힌 대각선 이동을 차단합니다.
- RuntimeDynamic에서는 Goal Y·DynamicGate 변경을 최신 요청에 반영하고 Scheduler가 분할 실행합니다. 샘플의 명시적 `RequestRebuild()`도 같은 요청 경로를 사용합니다.
- 방향장 계산은 Agent의 이동 벡터를 제공하며 Agent 간 충돌 회피는 별도 책임입니다.
- 왼쪽은 기본 64 Agent 실행, 오른쪽은 Gate와 Y축이 다른 Goal을 반영한 실행 상태입니다.

<p align="center">
  <img src="Docs/Images/feature-volume3d-runtime-baseline.png" alt="Volume3D RuntimeDynamic 기본 64 Agent 실행 화면" width="48%">
  <img src="Docs/Images/feature-volume3d-runtime-gate.png" alt="Volume3D DynamicGate·Y축 Goal 변경 후 실행 화면" width="48%">
</p>

### Editor 시각화

**목적**

계산 결과를 Editor Scene View에서 확인할 수 있도록 Surface2D 미리보기와 Volume3D FullVolume 표시를 제공합니다. 표시용 표본 수와 실제 계산 격자를 구분해, 큰 필드에서도 화면 가독성을 유지합니다.

**핵심 구현**

- Surface2D는 표면 위 방향장을 표시하고, 현재 100×100 격자는 두 칸 간격으로 50×50개 대상을 선택합니다. 표시 대상 축약은 계산 해상도를 변경하지 않습니다.
- Volume3D FullVolume은 최대 8,192셀, Slice는 최대 4,096셀을 표시하며 실제 테스트 격자 `20×12×20`은 12개 Y층을 포함한 4,800셀을 표시합니다.
- Volume 표시에서는 셀 경계를 숨기고 방향 화살표와 정지 표식을 중심으로 확인할 수 있습니다. Surface의 표시 규칙과 동일하다고 가정하지 않습니다.
- 왼쪽은 Surface2D 전체 영역의 축약된 방향장 미리보기, 오른쪽은 Volume3D 전체 층의 방향장입니다. Surface 상세 화면과 Volume 중앙 Y 단면은 [TechnicalNotes.md](Docs/TechnicalNotes.md)에서 확인합니다.

<p align="center">
  <img src="Docs/Images/feature-surface2d-editor-overview.png" alt="Surface2D 전체 영역의 Scene View 방향장 미리보기" width="48%">
  <img src="Docs/Images/feature-volume3d-editor-full-volume.png" alt="Volume3D FullVolume 전체 Y층 Scene View 방향장" width="48%">
</p>

## 검증과 지원 범위

### 격자·메모리 제한

| 항목 | 조건 |
| --- | --- |
| CellSize | 유한한 값이며 최소 `0.01`입니다. |
| Bounds·Transform 공통 | Bounds 중심·크기와 Manager 월드 위치는 유한해야 하며, 입력 크기는 각 축에서 양수여야 합니다. 아래의 모드별 추가 조건을 함께 만족해야 합니다. Manager는 회전 없이 단위 Scale을 사용합니다. |
| Surface2D Bounds | 입력 Y 높이는 최소 **`0.01`**입니다. X·Z는 아래 식으로 반올림한 셀 수가 **각각 1 이상**이어야 하며, 0셀을 1셀로 보정하지 않습니다. |
| Volume3D Bounds | 입력 X·Y·Z 크기는 양수여야 하며, 반올림한 축별 셀 수를 **최소 1셀로 보정**합니다. 입력 Y에 Surface2D의 별도 `0.01` 높이 하한을 적용하지 않습니다. |
| Surface2D | `Width × Depth` 최대 **100,000셀**입니다. |
| Volume3D | `Width × Height × Depth` 최대 **1,000,000셀**입니다. |
| Volume 추적 메모리 | Manager별 **512MiB** 예산을 적용합니다. 기존 게시 결과와 작업 배열을 고려한 예상량으로 요청을 검증하고 작업 시작 시 다시 확인합니다. |

Surface2D의 X·Z 셀 수는 `floor(axisLength / CellSize + 0.5)`로 계산하며 각 결과가 1 이상이어야 합니다. Volume3D의 X·Y·Z 셀 수는 `max(1, floor(axisLength / CellSize + 0.5))`로 계산합니다. 일반적인 양수 입력도 Surface에서는 반올림 결과가 0이면 거부됩니다.

`CellSize = 1`일 때의 입력 Bounds 크기 예시는 다음과 같습니다. 다른 설정은 유효하다고 가정합니다.

| 입력 크기 `(X, Y, Z)` | Surface2D | Volume3D |
| --- | --- | --- |
| `(1, 0.001, 1)` | Y 높이가 `0.01` 미만이므로 거부 | 양수 입력이며 `1×1×1`셀로 보정 |
| `(0.4, 1, 1)` | X 반올림 결과가 0셀이므로 거부 | X를 최소 1셀로 보정해 `1×1×1`셀 |
| `(0.5, 0.01, 1)` | X·Z가 각각 1셀이고 Y 하한을 만족하므로 `1×1`셀 | 각 축을 최소 1셀로 보정해 `1×1×1`셀 |

실제 계산 Bounds는 중심을 유지하며 Surface2D의 X·Z, Volume3D의 X·Y·Z 길이를 `셀 수 × CellSize`로 스냅합니다. Surface2D의 Y 높이는 입력값을 유지합니다. 입력 Bounds 크기와 실제 계산 격자의 크기를 구분해야 합니다.

셀 수 제한과 메모리 예산은 별도 조건입니다. Bounds를 키우거나 CellSize를 줄이면 초기화·요청 검증에서 거부될 수 있습니다. 512MiB는 해당 Manager가 추적·예상하는 메모리의 제한이며 Unity 프로세스 전체 사용량이나 실제 GPU 메모리 상한을 보장하는 수치는 아닙니다. Surface2D의 Ground Bake Layer·Obstacle Layer와 Volume3D의 Obstacle Layer는 비어 있으면 안 됩니다. Manager의 Default Direction은 유한한 벡터이며 길이 제곱이 `0.0001`보다 커야 합니다. 예를 들어 `Vector3.forward`를 사용할 수 있습니다.

### 검증 기록

Unity 2023.2.20f1·macOS·Metal(Apple M4 Pro)에서 **회귀 테스트 415개가 모두 통과**했습니다(성능 프로브 12개 제외). Windows·Direct3D 11(AMD Radeon RX 9070)의 마지막 기록은 이번 수정 이전의 466개 통과입니다. 저장된 샘플 씬 통합 2개, PlayMode에 진입하는 Manager 통합을 포함합니다. Surface·Volume의 샘플 게시, 재진입·취소·재활성화, 장애물 안전성, Modifier 합성, 베이크 검증·저장 실패 경계와 직렬화 참조를 확인합니다.

리팩터링 안전망으로 다음 검사가 함께 실행됩니다.

- **골든 해시**(`FlowFieldGoldenFieldTests`): 고정된 Surface·Volume 장면의 모든 셀 샘플을 1e-4 단위로 양자화한 해시입니다.
- **베이크 내용 해시**(`FlowFieldBakeAssetContentTests`): 두 베이크 Asset에서 런타임이 읽는 배열의 해시로, 저장 형식과 무관하게 내용이 같아야 합니다.
- **동등성 검사**: Volume 후보 필터와 셀마다 Physics 쿼리, 내부 셀 Topology와 테스트에 적어 둔 통과 규칙, v6 저장 왕복과 원래 값을 비교합니다.

Unity Test Runner에서 `Common.FlowField.Tests`를 선택해 EditMode 전체를 실행할 수 있습니다. Manager 통합 테스트는 이 실행 안에서 PlayMode에 진입합니다. 배치 실행은 Editor에서 열려 있지 않은 프로젝트를 대상으로 합니다.

```powershell
Unity.exe -batchmode -projectPath <ToyProject> -runTests -testPlatform EditMode `
  -assemblyNames Common.FlowField.Tests -testResults <results.xml> -logFile <unity.log>
```

성능 프로브(`FlowFieldPerformanceProbes`, `Performance` 카테고리)는 일반 실행에서 건너뜁니다. 환경 변수 `FLOWFIELD_PERF=1`을 설정하고 `-testCategory Performance`로 실행하면 결과를 `Logs/FlowFieldPerformance/probes.csv`에 추가합니다. 단계별 변경과 결정은 [RefactoringNotes.md](Docs/RefactoringNotes.md)에, 현재 상태와 남은 과제는 [StatusAndBacklog.md](Docs/StatusAndBacklog.md)에 기록합니다. `Logs/`는 Git에서 제외되므로 새 체크아웃에서는 다시 실행해 생성합니다.

| 측정(100³ Volume·장애물 40개, Managed) | 개선 전 | Burst 전환 전 |
| --- | ---: | ---: |
| 전체 빌드, 등록 장애물 0 / 10 / 50개 | 26.1 / 32.3 / 55.4초 | 5.8 / 5.9 / 6.4초 |
| 2ms 협력 예산으로 빌드 완료까지 | 12,585프레임 | 2,214프레임 |
| 등록 장애물 1개 이동 후 재빌드 | 31.3초, 관리 힙 +61MiB | 6.0초, 관리 힙 +52.5MiB |
| Volume 베이크 Asset(20×12×20) | 453,863바이트 | 106,568바이트 |

측정은 한 기기의 Editor 배치 실행 기준이며 실행마다 20~30% 정도 달라질 수 있습니다. 위 표와 Surface 316×316 재빌드 약 1.1~1.3초는 Windows 기록이며, Surface 재빌드는 Topology 재사용 수정 후 Windows에서 다시 재지 않았습니다. 이 표는 Burst 전환 이전의 Managed 기록입니다. 현재 수치는 Mono Mac Player 기준으로 [StatusAndBacklog.md](Docs/StatusAndBacklog.md#player-기준선-2026-11-01)에 있습니다: Volume 첫 빌드 2.3~3.7초, Goal 변경 0.10~0.14초, Modifier 값 변경 0.25~0.29초, Surface Goal 변경 64~115ms.

- 현재 씬 2개·Prefab 2개·v6 베이크 2개와 기존 GUID·직렬화 값은 유지합니다. 구버전 API 래퍼·직렬화 별칭·변환 전용 코드나 에셋은 두지 않습니다.
- BFS는 동일 비용의 격자 이동 횟수를 최소화합니다. 속도 Modifier는 경로 비용을 바꾸지 않으며 Agent 간 충돌 회피는 이동 시스템의 책임입니다.
- Volume의 2ms는 협력 CPU 예산입니다. Physics·Modifier 호출 한 번이나 할당이 이를 초과할 수 있으며 전체 빌드 시간의 상한은 아닙니다. 워커 스레드의 풀이 시간은 이 예산에 들지 않습니다.
- Surface 런타임·StaticBaked 이미지는 현재 HUD로 다시 캡처했고, 나머지 기능 이미지는 기존 캡처입니다. 회귀 실행은 실제 포인터 조작·GUI 렌더링·장시간 군중 이동을 포함하지 않습니다. Surface 샘플의 실제 포인터 조작, 두 샘플의 실제 키 입력, 군중 1000개 3분 이동은 Editor에서 별도로 확인했습니다. 군중 이동은 2026-10-31 기록으로, 그 뒤의 Burst 전환과 구조 개편 후에는 다시 돌리지 않았습니다. RMB+WASD 자유 카메라는 우클릭을 누른 채 유지하는 입력을 만들 수 없어, 버튼 번호만 왼쪽으로 잠시 바꿔 이동·회전·해제를 확인하고 실제 우클릭 입력은 따로 확인했습니다. Mono Mac Player는 `Tools/run-player-probe.sh`로 실행해 확인했고, IL2CPP는 Android ARM64 APK 빌드까지만 확인했습니다.
- macOS 입력 소스가 한글이면 샘플의 글자 키가 `Input.GetKeyDown`에 전달되지 않습니다(M·R·G·O로 확인). Space와 숫자 키는 동작합니다. 샘플을 조작할 때는 영문 입력 소스로 바꿉니다.
