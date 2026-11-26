# FlowField 기술 참고

[README](../README.md)를 보완하는 구현 요약입니다. 사용법, 설정, API와 실패 규칙은 README에 있고, 여기서는 그 동작을 만드는 규칙을 적습니다.

## 어셈블리

| 어셈블리 | 폴더 | 내용 |
| --- | --- | --- |
| `Common.FlowField` | `Script/` | 계약, 격자, Session, 계산 단계, 결과 저장소, Bake 데이터, `FlowFieldManager`와 Modifier 컴포넌트 |
| `Common.FlowField.Editor` | `Script/Editor/` | Inspector, Bake, Scene View 표시 |
| `Common.FlowField.Samples` | `Samples/Scripts/` | 샘플 씬의 에이전트·컨트롤러·UI(UGUI 참조) |
| `Common.FlowField.Tests`, `.Tests.Fixtures` | `Dev/Tests/` | Editor에서만 도는 회귀 테스트와 테스트용 Modifier([회귀 테스트](#회귀-테스트)) |

- 시스템과 에디터 어셈블리는 `Script/` 폴더 하나에 있습니다. 샘플과 테스트는 시스템을 참조하고, 시스템은 그것들을 모릅니다.
- `Script/FlowFieldAssemblyInfo.cs`의 `InternalsVisibleTo`가 Editor 어셈블리와 테스트 어셈블리에 내부를 엽니다. `Script/Editor/FlowFieldEditorAssemblyInfo.cs`는 Editor 어셈블리의 내부를 테스트에 엽니다. Editor가 쓰는 내부는 아래 "Editor 경계"에 있습니다.
- `Dev/`가 없어도 시스템은 그대로 컴파일됩니다.

## 파이프라인과 전략

### Session

- `FlowFieldSession`은 두 모드가 같이 쓰는 파이프라인이고, 모드와 무관한 규칙은 모두 여기 있습니다. 파일은 `FlowFieldSession.cs`(수명, 이벤트), `FlowFieldSession.Inputs.cs`(설정·장애물·Modifier 입력과 불확실 영역), `FlowFieldSession.Pipeline.cs`(빌드 루프), `FlowFieldSession.Reads.cs`(조회)입니다.
- 수명 전이(`Initialize`·`Stop`·`Restart`·`Release`·Fault·`Dispose`)와 세대 번호(`LifetimeGeneration`)를 맡습니다. 세대는 Stop, Restart, Release, Fault 때 늘고, 그 앞에서 시작한 일의 큐 이벤트는 버립니다. 같은 때 Builder가 진행 중인 빌드를 버리므로 늦은 풀이 콜백도 버려집니다.
- 빌드 루프는 `RunPipeline` 하나입니다. Scheduler는 `PumpScheduled`(프레임 예산, 워커를 기다리지 않음, sweep 실행)로, 직접 소유자(Bake, Edit Mode 미리보기)는 `Pump`(워커를 기다림, sweep 없음)로 부릅니다.
- 빌드 중 상태 전이, Revision, `FieldChanged`, Fault, 불확실 영역 갱신은 Session만 합니다.
- Builder는 빌드를 시작할 때 빌드 객체(`IFlowFieldBuild`)와 `FlowFieldBuildStart`(그 빌드가 장애물을 조회하는지, 새 장애물 설정이 불확실하게 만든 영역)를 돌려줍니다. Session은 그 빌드를 들고 단계(`Step`)마다 `FlowFieldBuildStep`(계속·대기·버림·완료)을, 게시(`Publish`) 때 게시 필드가 달라졌는지를 받습니다. 실패는 예외로 알립니다.
- 빌드는 게시, 거부, Builder가 버린 뒤(`CancelWork`, 새 격자) `IsLive`가 false이고, Session은 그때 다음 빌드를 시작합니다. `HasActiveBuild`와 `IsWaitingForExternalCompletion`도 이 빌드에서 읽습니다.
- 버린 결과 뒤에 남은 입력이 있으면 Ready로 돌아가지 않고 Building을 유지합니다.
- Surface 풀이 완료 콜백은 자기 빌드를 붙잡고, 그 빌드가 아직 풀이를 기다릴 때만 진행합니다. 콜백은 `Poll` 안, 곧 그 빌드의 `Step` 안에서만 옵니다.
- 조회: `TrySample`과 `TryClampPositionToGrid`는 예외 없이 상태를 돌려주는 `TrySampleStatus`·`TryClampStatus`를 쓰고, 예외를 던지는 쪽은 그 상태를 `FlowFieldFailures`의 예외로 바꿉니다.
- 입력: 장애물 등록부(`FlowFieldObstacleRegistry`), Modifier 등록부(`FlowFieldModifierRegistry`), 불확실 영역(`FlowFieldSafety`)을 Session이 가집니다. 장애물 변화가 닿는 범위는 Bounds를 셀 크기 + Obstacle Clearance만큼 넓힌 곳이고(`FlowFieldObstacleReach`), 이전·현재 Bounds 어느 쪽도 그렇게 넓혀 격자에 닿지 않는 변화는 건너뜁니다.
- Builder는 Session을 참조하지 않습니다. 등록부와 불확실 영역의 읽기 view, 추가 Goal 집합(`FlowFieldGoalSet`), sweep 여부, 사용자 코드 경계만 담은 `IFlowFieldBuildHost`를 받고, Session 안의 `BuildHost`가 매번 Session의 상태로 넘깁니다.
- Bake 세션은 `CreateSurfaceBake`·`CreateVolumeBake`가 만들고 Builder를 함께 돌려줍니다. Bake는 그 Builder에서 내보내기(`TryExportBakeData`)와 진행률만 읽고, 준비한 요청은 `SubmitPrepared`로 `SubmitConfig`와 같은 입력 규칙(불확실 영역, Building 전환, 상태 알림)을 거칩니다.

### Builder

- 모드마다 다른 일은 `IFlowFieldSpaceBuilder`의 두 구현 `FlowFieldSurfaceBuilder`, `FlowFieldVolumeBuilder`가 맡습니다. 두 Builder의 partial 파일은 같은 구성입니다: 본체(상태, 요청 수락, 수명 훅), `*.Build.cs`(빌드 단계), `*.Config.cs`(설정 검사와 요청), `*.Inputs.cs`(장애물·Modifier 입력), `*.Reads.cs`(게시 필드, 격자, 정보, Bake 내보내기).
- 요청: `Initialize(bakeMode)`, `SubmitConfig`. Session이 정한 Bake Mode를 `Initialize`에서 받아 다음 `Initialize`까지 보관하고, 설정 스냅샷의 Bake Mode는 읽지 않습니다. 실행 중이 아닌 세션은 Session이 입력을 거부하므로 Builder는 수명을 검사하지 않습니다.
- 빌드: `TryBeginBuild`가 빌드 객체를 돌려주고, `BuildPolicy`가 한 Pump에 시작할 수 있는 빌드를 정합니다. Surface는 `FlowFieldSurfaceBuild`(phase, 진행 중인 합성, 게시 인자, 프레임 예산), Volume은 `FlowFieldVolumeBuildContext`가 빌드이고, 추가 Goal은 두 모드 모두 Goal 묶음 빌드(`FlowFieldSurfaceGoalBatch`, `FlowFieldVolumeGoalBatch`)입니다. 한 Builder는 빌드를 하나씩 돌립니다. Session은 `HasPendingRequest`(scheduler가 할 일이 있는지)와 `HasOwedInput`(진행 중인 빌드에 없는 입력)을 따로 읽습니다.
- 상태: 다음 빌드가 갚을 입력은 입력 ledger(`FlowFieldSurfaceInputLedger`, `FlowFieldVolumeInputLedger`)에, 게시 필드는 store(`FlowFieldSurfaceFieldStore`, `FlowFieldVolumeFieldStore`)에 있고, `FlowFieldSurfaceState`에는 staging 데이터만 남습니다. Surface base 단계는 빌드 도중에도 ledger의 dirty 상태를 고쳐 쓰므로, 빌드를 시작할 때 복사하지 않습니다.
- 읽기: `PublishedField`·`TryGetFieldInfo`(Revision은 Session이 넘김), `LatestGrid`(마지막 요청의 Grid, `ClampPositionToGrid`가 씀), `LatestCells`(게시 필드보다 새 셀 정보. 조회마다 `TryCapture`로 배열을 한 번 받음. Surface는 진행 중인 빌드의 기본 단계 결과, Volume은 없음).
- 입력: `ApplyObstacleChange`·`ApplyModifierChange`·`CreateModifierArea`.
- 수명 훅: `CancelWork`·`DropComputedState`·`ReleaseResources`. Dispose는 없고, `ReleaseResources`가 Builder가 가진 것을 모두 놓습니다.

### 전략과 검증

- Manager, Session, Editor는 모드에 따라 달라지는 것을 모드별 정적 묘사 `FlowFieldSpaceStrategy.For(mode)`로 고릅니다: 세션 생성, 검증(`TryValidate`), 격자(`TryCreateLayout`), 최대 셀 수(`MaxCellCount`), Bounds 스냅(`SnapBounds`, 핸들 크기 조정은 `SnapResized`), sweep 조회 범위(`ObstacleSweepBounds`).
- 각 묘사는 Builder의 정적 함수로 넘기고, Builder는 `FlowFieldManagerConfig` 스냅샷으로 요청을 스스로 만듭니다.
- 검증은 한 경로입니다. `TryValidate`가 처음 잘못된 설정의 이유를 돌려주고, Init의 `Validate`는 그 이유로 `InvalidOperationException`을 던집니다. Inspector, Scene View, Volume 표시도 같은 `TryValidate`를 써서 Init과 같은 문구를 보여 줍니다.
- 두 모드 공통 검사는 `FlowFieldManagerConfig.TryValidateShared`가 같은 순서와 메시지로 합니다. 실행 중인 세션과 다른 Bake Mode(`bakeModeLocked`)도 여기서 거부합니다.
- Space Mode 편집은 검사하지 않고 Release까지 기다립니다. 그 편집을 거부하는 것은 실행 중인 세션의 `Init()`과 Fault 재시도뿐입니다.

### 공통 부품

- 게시 필드 `FlowFieldField`(격자와 씬·주체·Goal 층), 조회 `FlowFieldSampler`(모드별 cell view `FlowFieldSurfaceCellView`·`FlowFieldVolumeCellView`를 조회마다 한 번 고름), 요청 보관 `FlowFieldRequestInbox`, 셀 합성 규칙 `FlowFieldVectorUtility.ApplyModifier`, 이벤트 경계 `FlowFieldExecutionBoundary`, 예산 `FlowFieldBuildScheduler`.
- `FlowFieldBuildDriver`가 Play Mode에서 프레임마다 한 번, 다른 LateUpdate 뒤(실행 순서 30000)에 scheduler를 돌립니다. Play 세션이 시작할 때 scheduler의 정적 상태와 `FlowFieldBuildSettings.BudgetMilliseconds`를 되돌립니다. Domain reload를 꺼도 이전 Play의 값이 남지 않게 하기 위해서입니다.
- next 셀 표식 `FlowFieldNextCell`: Default Direction(-1), 막힘(-2), 도달 불가(-3). 다음 셀이 자기 자신이면 Goal anchor입니다. 두 모드의 풀이, Bake Asset 검증, 샘플 안전 규칙이 같이 씁니다.
- 겹침 조회 `FlowFieldOverlapUtility`: 버퍼가 차면 두 배로 늘리고 `MaxOverlapHits`(4,096)에서 멈춥니다. 상한에 닿았을 때의 처리(Fault, 셀별 검사, sweep 중단)는 호출하는 쪽이 정합니다.
- Influence Collider 규칙 `FlowFieldModifierInfluence`(`TryValidateInfluenceCollider`, `IsUsableTrigger`)는 등록부, 두 Builder, Editor가 같이 씁니다.
- Modifier가 닿는 셀을 찾는 방식만 모드별입니다. Surface는 셀 마스크(`FlowFieldSurfaceModifierArea`)를 보관해 바뀐 영역만 다시 합성하고, Volume은 빌드마다 64셀씩 후보를 조회합니다.

### Editor 경계

- Runtime Manager가 공개 API 밖으로 Editor에 내놓는 것은 설정 스냅샷 `CaptureConfig()`, 게시 필드 `TryGetPublishedField`, 직렬화 경로 `PropertyNames`입니다.
- 그 밖에 Editor가 쓰는 시스템 내부는 다음과 같습니다.
  - 모드별 묘사 `FlowFieldSpaceStrategy`(레이아웃, 검증, 스냅, 최대 셀 수)와 Bounds 비교·최소 높이 `FlowFieldLayoutUtility`
  - Bake와 미리보기의 독립 세션 `FlowFieldSession`(`CreateSurfaceBake`·`CreateVolumeBake`·`CreateSurface`, `SubmitPrepared`, `SubmitConfig`, `Pump`)과 Builder 정적 함수(요청, 설정 서명, Goal 해석), 내보내기 `TryExportBakeData`
  - Surface2D Bake의 지면 scan `FlowFieldSurfaceGroundScan`과, 그 결과를 Bake 세션의 지면으로 넘기는 `FlowFieldFixedSurfaceSource`
  - Bake Asset의 검증과 쓰기(`Apply`), Volume 표시의 검증기 `FlowFieldVolumeBakeValidator`
  - 표시가 읽는 게시 필드 `FlowFieldField`와 Goal 표식 `FlowFieldGoalFlags`, Surface 표시의 가장 가까운 지면 찾기 `FlowFieldSurfaceGraphTraversal`
  - Modifier 규칙(`FlowFieldModifierRegistry.DuplicatePriorityMessage`, `FlowFieldModifierInfluence.TryValidateInfluenceCollider`)
- `FlowFieldBakeService`는 레이아웃(`TryGetLayout`)과 설정 검사(`TryValidateConfig`, `TryValidateBakeSettings`)를 strategy로, 요청과 Goal 해석은 Builder 정적 함수로 만듭니다. 두 모드의 독립 Bake 세션(Surface2D는 지면 scan이 끝난 뒤 `CreateSurfaceBakeSession`, Volume3D는 `CreateVolumeBakeSession`)도 만듭니다.
- Volume 표시는 Init이 payload 전에 하는 검사(`TryValidateVolumeConfigBeforePayload`)를 같은 순서·문구로 하고, payload는 단계로 나눠 검증합니다. 검증이 끝났거나 설정이 바뀌면 그 결과를 Bake 검증 캐시에 넣고 `TryValidateConfig`를 그대로 돌립니다.
- Bake 입력 해시(`ComputeBakeInputHash`)는 BakeService가 설정 스냅샷으로 계산하고, Bake는 프레임마다 시작 때의 값과 비교합니다(`CaptureInputGeneration`).
- 쓰기는 `SerializedObject`로 합니다. Bounds 핸들은 Undo를 남기고, Bake Asset 지정은 Bake 저장과 같은 Undo 묶음에, Clear Bake의 해제는 자기 Undo 단계에 기록합니다. 실행 중인 세션에는 `OnValidate`로 통지됩니다.

## 수명과 게시 규칙

<img src="Images/flowfield-pipeline.png" alt="두 모드 공통 빌드와 게시 흐름" width="65%">

<sub>PlantUML 원본: [flowfield-pipeline.puml](flowfield-pipeline.puml)</sub>

### 입력과 Building

- Builder는 최신 요청 한 벌과 대기 표시만 보관합니다(`FlowFieldRequestInbox`).
- Volume은 빌드 시작 때 요청을 BuildContext에 고정하고, Surface는 풀이가 끝나면 최신 요청으로 합성합니다.
- 빌드 중 온 입력은 다음 빌드로 넘기고, 그동안 두 모드 모두 Building을 유지합니다.
- Ready에서 받은 입력(설정, Goal, 장애물, Modifier)은 Session이 그 자리에서 Building으로 바꿉니다. 그래서 명령 직후 `State`와 `IsRebuilding`이 두 모드에서 같고, 다음 Pump를 기다리지 않습니다.
- 할 일이 없어진 Building은 게시된 필드가 있으면 Ready로 돌아갑니다.

### 게시와 Revision

- 완성된 결과만 게시하고, 조회는 게시 값을 읽은 뒤 아래 샘플 안전 규칙을 적용합니다.
- 게시 필드는 격자와 세 층입니다. 층은 게시된 뒤 바뀌지 않으므로 다음 게시가 같은 층을 참조로 다시 쓸 수 있습니다.
  - 씬 층(`FlowFieldSceneLayer`): Surface2D의 지면(`FlowFieldSurfaceData`, Max Slope와 Max Step Height 포함). Volume3D에는 없습니다.
  - 주체 층(`FlowFieldAgentLayer`): 막힘, 지면 Topology(Goal 영향과 상관없는 연결), Volume3D의 탈출 방향.
  - Goal 층(`FlowFieldGoalLayer`): Goal 범위 Topology, Goal 플래그, 다음 칸, 기본·최종 방향과 속도, Modifier 영향(Surface2D), Goal 활성 여부와 Goal 칸. [추가 Goal](#추가-goal)의 층은 Goal까지의 걸음 수도 둡니다.
- 끝난 풀이는 새 입력이 들어와 있어도 게시합니다. 계속 움직이는 Goal 때문에 결과가 끊기지 않게 하기 위해서입니다. Grid나 설정이 바뀌었거나 Goal이 사라진 경우만 거부합니다.
- `NotifyCellsDirty`는 격자를 그대로 두므로 진행 중인 빌드를 거부하지 않고(Surface `Ground` flag), 바닥은 다음 빌드가 다시 scan합니다. 바닥이 실제로 바뀌었으면 그 빌드가 새 격자처럼 전체를 다시 만듭니다.
- Revision은 두 모드 모두 게시 내용(셀 상태, 방향, 속도, Goal)이 실제로 바뀔 때만 늘고, 그때 `FieldChanged`를 보냅니다. 추가 Goal의 필드도 같습니다. Volume은 `FlowFieldField.HasSameContent`로 층마다 비교하고(memcmp), Surface는 commit Job이 바뀐 칸을 찾습니다. 기본 방향·속도와 지면 Topology는 다른 값에서 정해지므로 비교하지 않습니다.
- 내용이 같아도 요청 스냅샷 갱신, 불확실 영역 정리, Ready 전환은 그대로 합니다.
- Revision은 Session이 세지만, Space Mode를 바꿔 Manager가 새 Session을 만들면 이전 Session의 값에서 이어 셉니다(`ContinueRevision`). 그래서 한 Manager는 두 필드에 같은 Revision을 보이지 않습니다.

### 샘플 안전 규칙

`FlowFieldSampleSafetyRule`은 두 모드와 모든 Bake 모드에 같은 규칙이고, `StaticBaked`도 구운 막힘을 기준으로 같은 검사를 합니다. 게시 값을 읽은 뒤 아래 순서로 덮어씁니다. 정지는 방향과 속도 배율을 0으로 바꿉니다. "위험한 칸"은 불확실 영역에 있거나, 최신 정보에서 막혔거나, 걸을 수 없는(Surface 지면 없음) 칸입니다.

1. **지금 칸:** 지금 칸이 불확실 영역이거나 걸을 수 없으면 정지합니다.
2. **막힌 칸:** 지금 칸이 막혔으면 탈출 방향(Surface2D는 지면에 투영)으로 속도 배율 1로 내보냅니다. 탈출 방향이 없거나, 그 방향으로 한 걸음 간 이웃이 격자 밖이거나 불확실 영역이거나 걸을 수 없으면 정지합니다. 그 이웃이 막힌 것은 괜찮습니다. 장애물 깊은 곳에서는 여러 칸을 지나 빠져나가기 때문입니다.
3. **경로 칸:** 다음 칸이 있으면, 지금 칸과 다음 칸을 감싸는 칸 중 하나라도 위험하거나 그 방향 연결이 끊겼으면 정지합니다. Modifier가 최종 방향을 기본 방향과 다르게 돌렸으면 4번의 돌린 방향 검사도 합니다.
4. **Default Direction 칸:** 기본 방향의 축 부호로 갈 수 있는 이웃 조합 중 하나라도 격자 밖이거나 위험하면 정지합니다. 그 방향에 가장 가까운 이웃 걸음 하나(Surface2D 8방향, Volume3D 26방향 중 내적이 가장 큰 쪽)는 3번처럼 감싸는 칸과 지면 연결까지 검사합니다. 지면 연결을 그 걸음에만 보는 것은, 경사면에 투영한 기본 방향의 아주 작은 옆 성분 때문에 옆의 턱에서 멈추지 않게 하기 위해서입니다. Modifier가 방향을 돌렸으면 축 조합 대신 최종 방향에 가장 가까운 이웃 걸음만 같은 방법으로 검사합니다. 3번의 돌린 경로 칸도 경로 걸음 검사에 더해 이 걸음을 검사합니다.
5. **나가는 걸음:** Default Direction 칸과 Modifier가 돌린 경로 칸은, 샘플 위치에서 샘플 방향(투영한, 이동 주체가 따르는 방향)으로 곧게 갈 때 지금 칸을 나가는 걸음도 검사합니다.
   - 처음 닿는 면은 거리와 상관없이 보고, 이동 주체에서 4분의 1칸 안에 닿는 다음 면도 넘는 순서대로 하나씩 봅니다. 이동 주체는 샘플 사이에 칸의 몇 백분의 일씩 움직이므로, 모서리 가까이에서는 다시 샘플하기 전에 두 면을 모두 넘기 때문입니다. 샘플 사이에 4분의 1칸 넘게 움직이는 이동 주체는 모서리를 놓칠 수 있고, 그 안에서 다음 칸의 방향이 낙차를 비껴가면 미리 멈출 수 있습니다.
   - 함께 닿는 면(0.0001칸 안)은 대각선 한 걸음으로 봅니다. Surface2D에서는 옆의 두 칸으로 떨어지는 걸음과, 그중 막히지 않고 올라설 수 있는 칸을 거쳐 떨어지는 걸음도 봅니다.
   - 되돌아올 수 없는 걸음만 정지합니다: 격자 밖, 불확실 영역, 지면 없음, Surface2D에서 Max Step Height보다 깊은 낙차. 칸의 옆 가장자리에 가까우면 조금 옆으로 흐르는 방향도 다음 걸음이 옆 칸이 되므로, 옆의 낙차를 넘기 전에 멈춥니다.
   - 막힌 칸이나 Max Step Height보다 높은 턱으로 넘는 걸음에서는 검사를 끝냅니다. 낙차가 아닌 막힌 칸은 탈출 방향이 다시 내보내고, 높은 턱은 이동 주체가 벽처럼 부딪혀 따라 미끄러지기 때문입니다. 그래서 벽이나 높은 턱을 따라 조금 돌린 방향은 멈추지 않습니다. 충돌 없이 위치만 옮기는 이동 주체는 이런 턱을 직접 막아야 합니다.
   - 벽에 닿은 뒤 미끄러지며 넘는 면은 보지 않으므로, Volume3D에서 Modifier가 돌린 방향은 막힌 칸을 따라 미끄러져 불확실 영역이나 격자 밖에 닿을 수 있습니다.

규칙이 읽는 정보는 다음과 같습니다.

- 모드마다 다른 것(축, 이웃 걸음, 옆 칸, 막힘·지면·연결, 낙차와 높은 턱, 탈출 샘플)은 `IFlowFieldCellView`가 답하고, 규칙 안에는 모드 분기가 없습니다. Sampler가 조회마다 view를 고르고, 같은 view로 칸을 읽은 뒤 규칙을 적용합니다.
- "최신 정보"는 Surface의 경우 진행 중인 빌드의 작업 공간이 게시 필드와 같은 격자·지면일 때의 막힘·지면·탈출이고(조회마다 한 번 받음), 그 밖에는 게시 필드입니다. 게시 필드는 탈출 방향을 두지 않으므로 그때 탈출은 최종 방향입니다. 추가 Goal의 읽기는 [추가 Goal](#추가-goal)에 적습니다.
- 연결은 항상 게시 필드에서 읽습니다. 진행 중인 빌드는 새 Goal의 영향 반경으로 연결을 다시 만들기 때문에, 게시된 걸음과 맞지 않습니다.
- 경로 걸음(3번)은 게시된 Topology를, 4번의 걸음은 지면 연결을 읽습니다. 5번은 연결을 읽지 않고, Surface2D에서는 게시된 칸 높이로 낙차와 높은 턱을 봅니다.
- 지면 연결: Surface2D Topology는 Goal 영향 반경 안의 칸만 잇기 때문에, 게시된 Surface의 단차 연결(Max Step Height, 대각선은 경로처럼 양옆 연결까지)을 씁니다. Max Slope는 칸마다 지면 유효성으로 봅니다. Volume3D는 Topology가 모든 열린 칸을 이으므로 그대로 씁니다.
- 새로 막힌 칸은 3번의 칸 검사가 막습니다. 세션 상태(`IsRebuilding`)는 보지 않습니다.

### 이벤트와 재진입

- `FlowFieldExecutionBoundary`가 이벤트를 큐에 모아 가장 바깥 작업이 끝난 뒤 전달합니다.
- 작업을 여는 것은 생명주기 호출(`Init`, `Release`, `RequestRebuild`, 활성화·비활성화·파괴, Play 중 Inspector 편집), scheduler의 빌드, Fault 진입입니다.
- 입력 명령(`SetGoal`, `NotifyCellsDirty`, 장애물·Modifier 등록·해제·통지)은 작업을 열지 않습니다. 그래서 그 제출이 낸 이벤트는 명령이 돌아오기 전에 핸들러가 받고, 핸들러 안에서 부른 명령이면 그 핸들러가 끝난 뒤 받습니다.
- 세대가 바뀌면(Release, Stop, Restart, Fault) 그 앞의 이벤트는 버리고, `Released`만 항상 전달합니다.
- 핸들러가 서로를 계속 깨우면 100,000건에서 오류를 남기고 남은 일을 버립니다. 이때도 `Released`와 해제 이벤트는 순서대로 한 번 전달하고, 그 핸들러가 새로 만든 일은 버립니다.
- 핸들러가 던진 예외는 Manager가 Console에 남기고, 세션을 Fault로 만들지 않습니다.
- Fault로 바뀌면 Manager는 `StateChanged`를 전달하기 전에 "[FlowField] Build failed: ..."를 오류로 남깁니다. 핸들러가 바로 재시도해도 원인이 Console에 남습니다.
- `StateChanged` 핸들러는 `OnValidate` 안에서 실행될 수 있습니다.

### 정지, 재시작, 수명 종료

- 비활성화는 `Stop`입니다. 빌드를 취소하고 게시 결과를 버리며, 세션은 runtime scheduler에서 빠졌다가 재시작이 요청을 제출할 때 다시 들어갑니다.
- 다시 활성화와 Fault 재시도(`RequestRebuild`)는 같은 `Restart` 경로입니다. Builder가 `DropComputedState`로 계산한 것을 모두 버리므로 Surface2D 바닥까지 처음부터 다시 빌드하고, 등록된 장애물과 Modifier는 유지합니다.
- 재시작은 해제하지 않고 파괴된 Modifier를 지운 뒤 등록된 Modifier를 모두 다시 읽습니다(`FlowFieldModifierRegistry.ObserveAll`). Fault나 비활성 중에 바꾼 Priority는 통지되지 않으므로 이때 새 순서를 쓰고, 겹친 Priority는 Fault로 거부합니다.
- 재시작에 실패하면 Fault로 남고 그 예외를 다시 던집니다. 재시도에서는 호출자가, 활성화에서는 Unity가 Console에서 받습니다.
- Fault 난 세션은 재시도·Release·Dispose까지 scheduler에 남지만 빌드하지 않습니다(그동안 꺼도 그대로입니다). 꺼 둔 채 Fault 난 세션은 scheduler 밖에 있다가, 켠 뒤 재시도하거나 꺼 둔 채 재시도한 뒤 켤 때 들어갑니다. 켜기만 하면 Fault 그대로입니다.
- 꺼 둔 동안 SerializedObject나 Undo로 바꾼 Space Mode도 실행 중처럼 Release까지 기다립니다. 다시 활성화는 세션의 모드로 이어서 빌드하고 설정도 그 모드로 검사합니다(Bake Mode 잠금은 그대로입니다). Fault 재시도는 그 편집을 거부합니다.
- 실행 중의 `SetGoal`·`NotifyCellsDirty`·`RequestRebuild`·Inspector 편집도 세션의 모드로 검사합니다.
- 새 결과가 게시되기 전까지 Ready가 아니고, `ClampPositionToGrid`는 최신 요청의 격자로 계속 동작합니다.
- 비활성 상태에서 `Init()`해도 요청은 바로 제출하므로, 두 모드 모두 그 격자로 Clamp하고 장애물 통지를 받습니다. 이 세션은 켜질 때까지 scheduler에 들어가지 않으므로, 한 번도 켜지 않고 파괴한 Manager(Unity가 `OnDestroy`를 부르지 않습니다)도 다음 Play로 남지 않습니다.
- `Init()`이 Play Mode에서만 되는 것은, Edit Mode에서는 이 컴포넌트의 `OnDestroy`가 불리지 않아 거기서 만든 세션이 Manager가 사라진 뒤에도 runtime scheduler에 남기 때문입니다.
- 수명이 끝나면 실행 중인 풀이 Job에 취소를 요청하고 끝날 때까지 기다립니다. Job은 4,096셀마다 취소를 확인합니다.

### Play 중 Inspector 편집

- 값을 바꾸면 `OnValidate` 안에서 검증하고 바로 다시 빌드합니다. 값이 잘못되면 Fault입니다.
- Grid(또는 Bake source)가 바뀌면 이전 결과를 즉시 버리고 Building을 알립니다. 버린 결과는 Grid를 되돌려도 다시 보이지 않고, 그 격자로 새로 빌드한 결과가 게시될 때까지 읽을 수 없습니다.
- `SetGoal`과 `NotifyCellsDirty`도 같은 검사(`SubmitCheckedConfig`)를 거칩니다. 이 둘이 제출하는 지금 설정은 편집 없이도 잘못될 수 있기 때문입니다(옮긴 `BakedBase` Manager는 Bake와 맞지 않습니다).
- 기다리는 Space Mode 편집이 있으면 Inspector는 세션 모드가 읽는 설정과 Bake Asset 칸을 보여 주고, Space Mode 칸 아래에 그 편집이 다음 Init에 적용된다고 알립니다("The session keeps ... until Release; the edited Space Mode applies at the next Init.").

## 입력 변경

### 컴포넌트 통지

- `FlowFieldDynamicObstacle`, `FlowFieldGoalTarget`, Modifier Volume은 자기 Transform이나 Collider Bounds를 프레임당 한 번 확인합니다(컴포넌트당 O(1)).
- Collider Bounds는 물리 장면을 sync해야 Transform 이동을 따라갑니다. 그래서 Collider를 보는 두 컴포넌트(`FlowFieldDynamicObstacle`, Modifier Volume)는 프레임당 한 번 `Physics.SyncTransforms`(`FlowFieldPhysicsSync`) 뒤에 읽습니다. Auto Sync Transforms가 꺼져 있고 물리 step이 없어도 LateUpdate 전에 옮긴 이동은 그 프레임에, 다른 스크립트가 LateUpdate에서 옮긴 이동은 다음 프레임에 알립니다.
- 등록할 때 읽는 Bounds는 바로 sync합니다. 해제할 때도 장면에 남은 Collider는 바로 sync한 지금 자리를 함께 알리므로, 옮긴 프레임에 해제해도(예: 옮기고 `FlowFieldDynamicObstacle`을 끔) 새 자리를 다시 조회합니다.
- `FlowFieldDynamicObstacle`은 등록한 Manager와 Collider를 기억해, 둘 중 하나가 바뀌거나 Collider만 파괴되면 그 등록을 해제하고 막았던 자리를 다시 빌드합니다.
- `FlowFieldGoalTarget`은 Influence Radius도 확인해, 멈춘 채 반경만 바꿔도 다시 알립니다.
- `FlowFieldGoalTarget`은 Goal을 알린 Manager와 그 Manager의 Goal 수명(`GoalLifetime`, Release가 Goal을 지울 때마다 늘어남)을 기억합니다. 그래서 Release와 Init을 한 프레임에 해도 Goal을 다시 정하고, Manager를 바꾸면 옛 Manager의 Goal을 지운 뒤 새 Manager에 정합니다.
- 끌 때는 Manager의 Goal 버전(`GoalVersion`)으로 자기 Goal인지 확인합니다. 버전은 `SetGoal`(같은 Goal을 다시 정해도), Init의 Goal Transform, Release, Fault 중 지우기로 Goal을 정하거나 지울 때마다 늘어납니다. 그래서 Release가 이미 지웠거나 스크립트나 다른 target이 그 뒤 새 Goal을 정했으면 그대로 둡니다.
- `SetGoal`은 이 버전을 제출 전에 target에 써 주므로, 제출 중 핸들러가 그 target을 꺼도 자기 Goal을 지웁니다.
- Fault 중에 끈 target은 그 Goal을 지우므로(`ClearGoalWhileFaulted`), 재시도는 Goal 없이 빌드합니다.

### 등록부

- 장애물과 Modifier 등록부는 Session만 바꾸고, Builder는 읽기만 합니다.
- `FlowFieldObstacleRegistry`는 Collider에서 자리로 가는 사전을 두어, 등록·해제·이동 통지가 목록을 훑지 않습니다. 등록 순서를 읽는 곳이 없으므로 해제는 마지막 항목을 빈 자리로 옮깁니다.
- 정적 마스크 규칙, sweep, `FlowFieldDynamicObstacle`은 `Contains`로 등록 여부만 묻습니다.
- 해제하지 않고 파괴된 등록 Collider는 할 일이 있는 빌드가 시작되기 직전에 Session이 지우고, 마지막으로 알린 자리를 이동처럼 불확실 영역으로 둡니다. 할 일이 없는 동안에는 등록부를 훑지 않습니다.
- 장면에 남은 채 해제한 Collider는 다음 두 프레임 동안 보고, 그사이 장면을 떠나면 그 자리를 한 번 더 알립니다. 같은 프레임의 `Destroy`는 그 프레임에 시작한 빌드가 그 Collider를 정적 장애물로 scan한 뒤에야 지우기 때문입니다. 꺼 둔 세션도 같은 프레임에 다시 켜면 그 프레임에 빌드를 시작하므로 같이 기록합니다. 이 기록은 `FlowFieldReleasedColliders`가 맡습니다.
- Fault 중에 받은 해제도 Builder가 그 자리를 기록해, `BakedBase` 재시도가 다시 조회합니다.

### 미등록 장애물 sweep

- `FlowFieldObstacleSweep`는 Session이 소유하고, runtime scheduler가 부르는 빌드 루프(`RunPipeline`의 try 안)에서만 프레임당 한 번 돕니다. 그래서 Bake, Edit Mode 미리보기, 직접 `Pump()`하는 세션은 sweep하지 않고(`SweepsObstacles`), sweep이 던진 예외는 그 세션만 Fault로 만듭니다.
- 한 pass는 sweep Bounds(`FlowFieldSpaceStrategy.ObstacleSweepBounds`)를 한 번 조회하고, 등록하지 않은 Collider를 1,024개씩 이전 pass의 Bounds와 비교합니다. 사라짐은 pass 끝에서 판정합니다.
  - Surface2D: 격자 XZ를 Clearance만큼 넓히고, Y는 장애물 조회 상자가 닿는 높이까지입니다.
  - Volume3D: Bounds를 셀 하나와 Clearance만큼 넓힙니다.
- 바뀐 Collider는 이전·현재 Bounds를 `RegionDirty` 장애물 변화로 보내 기존 경로(불확실 영역, 다시 조회)를 탑니다.
- 지켜보던 Collider가 등록되면 그 뒤로는 등록부가 알립니다. 등록은 지금 자리만 알리므로, sweep이 마지막으로 본 자리와 다르면 그 자리를 한 번 알립니다.
- 첫 pass는 목록을 뽑은 그 프레임에 모두 기록만 하고, 그 앞의 변화는 뒤따르는 빌드가 장면 전체를 읽어 받아들입니다. 여러 프레임에 나누면 빌드가 scan한 뒤 옮겨진 Collider가 옮긴 자리로 기록되어 그 이동을 놓치기 때문입니다.
- `BakedBase`도 같아서, sweep이 도는 동안 Surface2D는 swept 마스크로 장애물 전체를 scan하고 Volume3D는 Bake를 다시 읽는 빌드에서 모든 셀을 조회합니다.
- 설정(켜기, 조회 범위, Layer)이 바뀌거나 Stop·Release 때 기록을 비웁니다.
- Collider가 65,536개에 이르면 "[FlowField] The unregistered obstacle sweep found 65536 or more Colliders, so it stopped for this session." 경고를 남기고, 멈춘 Bounds 전체를 다시 빌드합니다. 멈춘 sweep은 설정(켜기, 조회 범위, Layer)이 바뀌거나 Release할 때까지 다시 돌지 않습니다.
- Surface2D 동적 마스크는 등록 overlay와 swept 마스크(모든 obstacle Collider, dirty 영역만 다시 scan)의 합집합입니다.
  - 등록 overlay는 빌드 사이에 보관하고, 각 Collider를 그릴 때의 Bounds와 isTrigger를 기억합니다(꺼진 Collider는 빈 Bounds).
  - 다시 만들 때는 dirty 영역과, 그 값이 바뀌었거나 새로 등록·해제·파괴된 Collider의 이전·현재 칸만 다시 그립니다. 결과는 모든 Collider를 다시 그린 것과 같습니다. 격자·지면·장애물 상자 설정이 바뀌었거나 dirty 영역이 격자 전체이면 전부 그립니다.
  - `RuntimeDynamic`에서 sweep이 이 세션에서 돌면 정적 마스크는 비웁니다. 옮긴 정적 Collider의 잔상을 남기지 않기 위해서입니다.
  - `BakedBase`의 정적 마스크는 Bake에서 시작하므로 그대로 두고, sweep이 알린 영역을 정적 규칙으로도 다시 scan해 옮긴 Collider의 Bake 자리를 풉니다.
  - sweep이 시작되거나 멈추면 모든 장애물 마스크를 다시 만듭니다.
- Volume3D는 다시 조회할 때 레이어의 모든 Collider를 읽으므로 영역 알림만으로 충분합니다. Bake 때 Rigidbody Collider가 있던 자리도 sweep이 그 영역을 알리면 풀립니다.
- 그래서 Volume3D에서 sweep 켜고 끄기는 장애물 설정이 아닙니다. 필드를 불확실하게 만들지 않고 그대로 읽게 두되, 꺼져 있는 동안의 미등록 변화를 받아들이도록 다음 빌드가 `BakedBase`까지 포함해 모든 셀을 Physics로 다시 조회합니다(그 빌드가 버려지면 대신하는 빌드가 이어받습니다).

### 불확실 영역

- 장애물 등록·해제·이동·영역 dirty 통지를 받은 그 프레임에 영역을 표시하고, 그 영역을 조회한 빌드가 게시될 때까지 그 셀과 그 셀로 가는 경로는 속도 0입니다. 목록은 Session만 바꿉니다(`FlowFieldSafety`).
- 장애물을 조회하는 빌드가 시작되면 목록을 옮기고, 다음 게시 때 지웁니다. 시작 후 기록된 영역은 다음 빌드까지 남습니다.
- 게시하지 못하고 끝난 빌드가 옮긴 영역도 남았다가 뒤따르는 빌드의 게시 때 지웁니다. Surface에서 거부된 빌드의 조회 결과는 staging에 남고 대체 빌드는 항상 Goal 플래그를 가지며, Volume에서는 남은 영역 때문에 뒤따르는 빌드가 장애물 결과를 재사용하지 못하고 그 영역(또는 전체)을 다시 조회하기 때문입니다.
- `RequestRebuild`가 시작한 빌드가 버려지면 전체 조회 요청은 대신하는 빌드가 이어받습니다.
- 목록이 64개를 넘으면 합쳐서 더 넓게 정지합니다(Surface는 XZ, Volume은 3D로 비교).
- 장애물 설정(Layer, Clearance 등)이 바뀌면 알린 그 순간부터 필드 전체가 불확실합니다. 비교 대상은 같은 격자·source의 마지막으로 받은 요청이라, 첫 빌드나 이전 설정으로 시작한 빌드가 게시되어도 새 설정으로 조회한 빌드가 게시될 때까지 정지합니다.
- 다른 격자·source의 요청은 게시 필드를 버리므로, 그 뒤의 비교 대상에는 옛 게시 필드가 없습니다.

### 격자 밖 변경

- 이전·현재 Bounds 어느 쪽도 셀 크기와 Clearance만큼 넓혀서 격자에 닿지 않는 장애물 변화(등록·해제·이동·영역 알림, 파괴 정리)는 Session이 건너뜁니다. 불확실 영역도 다시 빌드도 없고, BakedBase의 변화 영역도 넓히지 않습니다.
- 등록부는 이미 그 Bounds를 기록했으므로, 나중에 격자 안으로 옮기면 이전·현재가 모두 알려집니다.
- 건너뛰는 기준은 마지막 요청의 격자입니다. 격자를 바꾸면 이전 격자의 게시 필드를 버리고 새 격자는 장애물을 전부 조회하므로, 이전 격자에만 닿는 변화를 건너뛰어도 옛 필드에 남지 않습니다.

### 다시 빌드와 다시 조회

- `SetGoal()`은 장애물 결과를 재사용합니다. 같이 `RequestRebuild()`를 부르면 이 이점을 잃습니다.
- 실행 중인 세션의 `RequestRebuild()`는 두 모드 모두 장애물을 전체 다시 조회합니다(`FlowFieldConfigChange.Rebuild`). Surface2D는 격자와 Surface 설정이 그대로면 바닥 Raycast를 다시 하지 않으므로, 바닥 형상이 바뀌면 `NotifyCellsDirty()`를 씁니다. `BakedBase`는 Bake 막힘에서 다시 시작해 Init 뒤 바뀐 영역과 등록 장애물 영역만 다시 조회합니다.
- Fault 재시도와 다시 활성화는 위의 `Restart` 경로라 바닥까지 처음부터 다시 만듭니다. `RuntimeDynamic`에서 Manager를 옮긴 뒤의 제출은 새 격자가 되어 역시 전체를 다시 만듭니다. Bake Asset을 쓰는 Manager는 옮기면 Bake와 맞지 않아 검사에서 거부됩니다.
- `NotifyCellsDirty()`는 두 모드 모두 바닥(Volume은 모든 셀)과 장애물을 다시 계산합니다. 진행 중인 빌드는 버리지 않고, 불확실 영역도 표시하지 않습니다(`RequestRebuild()`와 같습니다).
- 풀이 중에 바뀐 Goal과 설정은 Builder가 보관했다가 다음 빌드를 시작할 때 제출합니다.
- Surface2D의 Default Direction은 예외로, 풀이가 끝나면 보관한 새 방향으로 바로 합성하고 다음 빌드에서 다시 합성하지 않습니다. 다시 합성할지는 요청끼리가 아니라 마지막으로 합성한 방향과 비교해 정하므로, 합성이 진행되는 동안 설정을 되돌려도 되돌린 방향으로 다시 합성합니다.
- Surface2D의 정적 장애물은 Obstacle Layer에서 Rigidbody가 없고 등록하지 않은 Collider입니다.
- Surface2D 장애물 변화는 그 변화가 닿는 셀(Bounds + 셀 크기 + Clearance)만 정적 마스크를 다시 scan합니다. 대상은 Rigidbody 없는 Collider의 등록·해제와 `NotifyObstacleRegionDirty`이고, `BakedBase`에서는 모든 장애물 변화입니다. 영역 없이 들어온 장애물 작업(첫 빌드, 장애물 설정 변경)은 전체를 scan합니다.
- `StaticBaked`는 동적 장애물 등록을 거부합니다. 두 Bake 모드 모두 형식이 다른 Bake Asset을 거부하므로 다시 Bake해야 합니다.

## Surface2D

- 통지가 dirty 표시와 요청을 남기면 Scheduler가 Session을 진행합니다. 직접 `Pump()`하는 Bake와 미리보기는 호출자가 입력을 책임집니다.
- 바닥·장애물·Goal·Modifier 영역 단계는 Physics 결과가 한 스냅샷이 되도록 한 프레임에 실행합니다. 그래서 `BuildPolicy`는 예산이 다 된 뒤에도 빌드 시작을 허락하고, 펌프마다 빌드를 하나만 시작합니다. 단, 풀이 중에 알린 Modifier 영역은 풀이가 끝나는 프레임에 다시 만듭니다.
- 장애물 마스크를 다시 만들 때는 먼저 `Physics.SyncTransforms`를 한 번 합니다. 등록부도 Collider Bounds를 읽기 전에 sync하므로, Auto Sync Transforms가 꺼져 있어도 같은 프레임에 옮기고 알린 이동을 놓치지 않습니다.
- BFS와 방향은 워커 스레드, Topology는 메인 스레드의 Burst입니다. 합성은 예산에 맞춰 나눕니다.
- Topology는 두 벌을 같은 Job으로 만듭니다.
  - Goal 범위 Topology는 Goal 영향 반경 안의 칸만 잇습니다. 방향 비트 d는 자기 칸, 이웃 칸, 대각선이면 양쪽 모서리 칸이 모두 영향 안일 때만 남습니다.
  - 지면 Topology는 영향과 상관없이 지면이 있고 막히지 않은 칸을 모두 잇습니다. 주체 층에 게시하고, 바뀐 칸 판정에는 넣지 않습니다.
  - `StaticBaked`는 Goal 범위 Topology를 Bake에서 읽고, 지면 Topology는 읽은 막힘으로 다시 만듭니다. Bake 형식은 그대로입니다.
- 장애물 마스크가 바뀌면 막힘 합치기(정적 | 동적)와 탈출 방향 BFS(네 면 이웃)를 메인 스레드의 Burst Job 하나(`FlowFieldSurfaceEscapeJob`)로 합니다.
- 지면 scan(`FlowFieldSurfaceGroundScan`)은 셀마다 아래로 쏘는 ray를 `RaycastCommand` 묶음(최대 1,024개)으로 워커 스레드에서 돌리고, 결과는 셀 순서대로 읽습니다. ray는 Ground Layer만 맞히고 trigger를 무시하며, 가장 가까운 hit 하나를 쓰고, back face는 `Physics.queriesHitBackfaces`를 따릅니다.
- 이웃 마스크는 Burst Job이 16,384셀씩 만듭니다.
- Editor Bake는 같은 scan을 단계마다 2ms 예산으로 나눠 돌리고, 묶음은 직전 묶음의 ray당 시간으로 남은 예산에 맞게 줄입니다(최소 64개).
- BFS가 예정된 빌드는 사전 합성을 생략하고, 완료 후 전체를 한 번 합성합니다.
- 한 번의 조회에서 겹치는 장애물이 4,096개(`FlowFieldOverlapUtility.MaxOverlapHits`)에 이르면 Session이 Fault 납니다. 일부만 쓰면 장애물을 놓치기 때문입니다. Volume3D는 이때 셀별 검사로 넘어갑니다.
- 특정 Collider가 한 셀에 닿는지 확인할 때(장애물 마스크, Modifier 영향 영역, Volume3D 셀별 검사)도 같은 상한을 씁니다.

## Volume3D

- 셀은 `x + Width × (z + Depth × y)` 순서로 저장합니다. 두 축·세 축 대각선은 중간 셀이 모두 통과 가능할 때만 연결합니다.
- 장애물·Modifier 단계는 64셀씩 진행합니다. 스텝마다 `OverlapBox`로 후보를 모으고, 후보가 닿는 셀만 정확히 검사합니다(`FlowFieldVolumeCellQuery`).
- Modifier 단계는 Modifier마다 칸 상자를 만들고 겹치는 상자를 합쳐서, 서로 겹치지 않는 상자만 돕니다. 같은 칸을 두 번 돌면 Modifier가 두 번 적용되기 때문입니다.
- 등록 장애물은 빌드가 장애물 조회 전에 `Physics.SyncTransforms`한 직후 한 번(`CaptureRegistered`: Collider, 넓힌 Bounds, Layer) 읽고, 스텝마다 그 Bounds가 스텝 상자에 닿는 것만 고릅니다. 읽은 뒤 움직이거나 해제된 등록 장애물은 통지로 불확실 영역이 되고, 그 영역은 이 빌드가 게시된 뒤에도 남아 다음 빌드가 다시 조회합니다.
- Modifier만 바뀌면 게시된 내비게이션에 다시 합성합니다. 첫 빌드 중 바뀐 Modifier도 그 빌드가 게시된 뒤 합성만 다시 합니다.
- 장애물 입력과 불확실 영역이 그대로인 빌드(Goal이나 Default Direction 변경)는 장애물 결과와 Topology를 재사용합니다. 그래서 Play 중 Inspector에서 Default Direction이나 Goal Influence Radius를 바꾸면 장애물을 다시 조회하지 않습니다. Obstacle Layer·Clearance, Sweep Unregistered, Grid를 바꾸면 다시 조회합니다.
- 장애물 결과를 재사용하는 빌드는 이전 필드의 주체 층을 그대로 게시합니다. Modifier만 다시 합성하는 빌드도 주체 층을 그대로 쓰고, 기본 배열을 공유하는 새 Goal 층을 만듭니다. Volume3D의 Topology는 모든 열린 칸을 이으므로 지면 Topology는 Goal 층의 Topology와 같은 배열입니다.
- Goal이 같은 셀 안에서 움직이면 빌드하지 않습니다. Goal을 지우면 진행 중인 빌드를 버립니다.
- 등록하지 않은 Collider는 `RequestRebuild()`나 `NotifyCellsDirty()`의 전체 조회, 또는 `NotifyObstacleRegionDirty()`와 sweep이 알린 영역의 조회에서 반영됩니다. `BakedBase`의 `RequestRebuild()`는 Bake를 다시 읽고 바뀐 영역만 다시 조회하므로(sweep이 돌면 모든 셀), 그 밖의 자리는 영역 알림이나 sweep으로 반영합니다.
- 교체된 게시 배열은 다음 빌드에 다시 씁니다(`FlowFieldSpareArrays.Take`, Surface 게시도 같습니다). 가져온 배열은 지우지 않습니다. 빌드가 모든 칸을 읽기 전에 쓰기 때문입니다.
  - Volume의 막힘은 게시 막힘이나 Bake 복사, 또는 전체 조회가 채우고, 나머지는 Static Bake 복사나 풀이 Job이 모든 칸에 씁니다(탈출 Job은 먼저 전부 0으로 씁니다).
  - 최종 필드는 Modifier 단계가 기본 필드를 4,096개씩 나눠 복사한 데서 시작합니다.
  - Surface 게시는 전체 commit이면 모든 칸을 쓰고, 부분 commit이면 게시 필드를 먼저 통째로 복사합니다.

## 추가 Goal

기본 Goal(`SetGoal`, Goal Transform, `FlowFieldGoalTarget`) 밖에 Goal을 더 둘 수 있습니다(`AddGoal`). 추가 Goal은 기본 Goal과 같은 지면·장애물·Modifier 위에서 자기 Goal 층만 따로 만듭니다. 상태와 집합은 Session의 `FlowFieldGoalSet`이 들고, 빌드는 두 Builder의 `.Goals` partial이 맡습니다.

- **종류:**
  - `Shared`는 여러 주체가 같이 쓰는 목적지이고 `TrySampleNearestShared`의 후보입니다.
  - `Group`은 한 무리가 잠시 쓰는 목적지입니다. 자주 움직인다고 보고 `Shared`보다 먼저 빌드합니다(아래 빌드 순서).
  - 두 종류 모두 핸들(`FlowFieldGoalHandle`)로 읽고 옮기고 지웁니다. Goal 번호는 모든 세션이 함께 세고 다시 쓰지 않으므로, 오래된 핸들이나 다른 Manager의 핸들이 다른 Goal을 가리키지 않습니다.
- **빌드 순서:**
  - 추가 Goal은 기본 Goal의 필드가 게시된 뒤에만 빌드합니다.
  - 기본 Goal에 할 일이 없으면 바로 빌드하고, 둘 다 할 일이 있으면 번갈아 빌드합니다(기본 Goal 빌드 한 번 뒤 추가 Goal 빌드 한 번). 그래서 매 프레임 움직이는 Goal이나 장애물이 있어도 추가 Goal이 멈추지 않습니다. 대신 기본 Goal의 새 결과는 추가 Goal 빌드 한 번만큼 늦을 수 있습니다.
  - 빌드 한 번(`IFlowFieldBuild`)이 한 종류의 할 일이 있는 Goal을 모두 묶어 만듭니다. `Group`이 먼저지만, `Shared`가 기다리면 `Group` 빌드를 두 번 연속하지 않습니다. 그래서 움직이는 `Group` Goal은 모든 `Shared` Goal을 기다리지 않고, 계속 움직여도 `Shared` Goal이 멈추지 않습니다.
  - 빌드는 시작할 때 게시된 필드의 씬·주체 층을 참조로 쓰고, Goal마다 Goal 층 하나를 만듭니다. 장애물을 다시 조회하지 않으므로 이 게시는 불확실 영역을 풀지 않습니다.
  - 새 격자나 Bake source, Stop, 재시작이 필드를 버리면 진행 중인 빌드는 다음 단계에서 멈춥니다.
- **Surface2D:**
  - Goal마다 작업 공간과 풀이 backend를 따로 둡니다. 게시된 막힘으로 탈출 방향을 다시 만든 뒤, Goal 범위 Topology·BFS·Modifier 합성을 기본 Goal과 같은 코드로 돌립니다.
  - 묶음의 모든 Goal의 준비(탈출 방향, 영향 범위, Topology)는 기본 Goal의 기본 단계처럼 예산과 상관없이 한 프레임에 돕니다. BFS는 Goal마다 워커 스레드에서 나란히 돌고, 합성은 프레임 예산에 맞춰 Goal 하나씩 나눠 진행합니다. 게시할 때 Goal 층 배열을 새로 할당합니다.
  - 기본 Goal 빌드가 아직 칸을 찾지 않은 Modifier(추가 Goal 빌드 중에 등록했거나 새 격자)는 이 합성에서 빠집니다. 그 입력이 추가 Goal을 다시 빌드하므로, 다음 결과에는 들어갑니다.
- **Volume3D:**
  - Goal마다 BuildContext를 장애물 재사용 모드로 만듭니다. 막힘·Topology·탈출 방향은 게시된 주체 층을 그대로 쓰므로 Topology와 탈출 Job을 건너뜁니다.
  - Goal마다 따로 둔 spare 배열에 풀고, 풀이는 워커 스레드에서 나란히 돕니다.
- **다시 빌드:**
  - 기본 Goal만 바꾼 입력을 뺀 모든 입력(설정, 장애물, Modifier, 다시 빌드)은 모든 추가 Goal을 다시 빌드합니다(`FlowFieldGoalSet.BaseStamp`).
  - 추가 Goal을 옮기면 그 Goal만 다시 빌드합니다. 0.0001 world unit 안의 이동에 같은 반경이면 아무것도 하지 않습니다.
  - 기본 Goal에 할 일이 남은 채 차례가 와서 만든 추가 Goal은 게시된 필드가 반영한 입력까지만 따라잡은 것입니다(`FlowFieldGoalSet.PublishedStamp`, 기본 Goal 빌드가 시작할 때의 stamp). 그래서 그 기본 Goal 빌드가 게시된 뒤 한 번 더 빌드하고, 그동안 세션은 Building입니다.
  - 빌드 중 들어온 입력은 그 빌드를 버리지 않고 게시한 뒤 다음 빌드를 부릅니다. 기본 Goal과 같이 계속 바뀌는 입력에도 결과가 끊기지 않게 하기 위해서입니다.
- **게시:**
  - 내용이 바뀐 게시는 기본 Goal과 같이 `Revision`을 늘리고 `FieldChanged`를 보냅니다. `RemoveGoal`은 게시가 아니므로 `TrySampleNearestShared`의 답이 바뀌어도 `Revision`을 늘리지 않습니다.
  - Goal 층에는 Goal까지의 걸음 수(`Distances`, BFS 비용)도 둡니다. 닿지 않는 칸은 `int.MaxValue`입니다.
  - 추가 Goal 필드가 읽는 주체 층의 배열은 기본 Goal의 다음 게시가 spare로 돌려받지 않습니다(`FlowFieldGoalSet.HoldsAgent`). 그래서 게시된 추가 Goal 필드는 바뀌지 않고, 기본 Goal의 다음 빌드도 그 배열에 쓰지 않습니다. 대신 기본 Goal과 추가 Goal이 서로 다른 주체 층을 읽는 동안은 그 배열을 새로 할당합니다.
- **읽기:**
  - `TrySample(position, handle, out sample)`은 기본 Goal과 같은 샘플 안전 규칙과 불확실 영역을 씁니다.
  - "최신 정보"는 진행 중인 빌드의 칸(Surface2D)이고, 없으면 기본 Goal의 게시 필드가 그 Goal이 읽는 주체 층보다 새로울 때 그 막힘과 탈출 방향입니다. 기본 Goal 빌드가 게시되면 그 빌드가 조회한 불확실 영역이 풀리는데, 추가 Goal은 다시 빌드될 때까지 이전 주체 층을 읽습니다. 그 사이 새로 막힌 칸으로 가는 길은 이 정보로 멈추고, 새로 막힌 칸 안에서는 기본 Goal 필드처럼 빠져나갑니다. Surface2D에서 지면을 다시 scan해 바뀐 동안은 이 정보가 없고, 추가 Goal은 다시 빌드될 때까지 이전 지면을 읽습니다.
  - 추가 Goal 필드는 기본 Goal의 필드가 같은 격자로 게시되어 있을 때만 답합니다.
  - 새 격자나 Bake source, Stop, 재시작은 모든 추가 Goal 필드를 버립니다(`FieldEpoch`). 그 앞에 시작한 빌드는 아무것도 게시하지 않습니다. Release는 추가 Goal을 모두 지웁니다.
  - `TrySampleNearestShared`는 위치의 칸에서 걸음 수가 가장 적은 `Shared` Goal의 필드로 답하고, 같으면 먼저 추가한 Goal입니다. 어느 Goal도 닿지 않는 칸(막힘, 모든 영향 반경 밖)이면 먼저 추가한 준비된 `Shared` Goal이 자기 샘플처럼 답합니다.
- **Bake:** Bake Asset에는 추가 Goal이 들어가지 않습니다. `BakedBase`는 추가 Goal도 런타임에 BFS로 풀고, `StaticBaked`는 `AddGoal`을 거부합니다.

## Agent 유형

Manager의 Obstacle Clearance, Max Slope, Max Step Height가 `Default` 유형입니다. Additional Agent Types(최대 2개)는 이름과 자기 Clearance, Max Slope, Max Step Height(Surface2D만)를 가지고, 같은 격자·지면 Layer·장애물·Goal 위에서 자기 필드를 만듭니다.

- **구조:**
  - Manager가 유형마다 세션을 하나 더 둡니다(`FlowFieldManager.Agents.cs`). 그 세션은 기본 세션과 같은 Space Mode이고, 설정 스냅샷에서 그 유형의 값만 바꿔 받습니다(`CaptureAgentConfig`).
  - 그래서 파이프라인, 샘플 안전 규칙, 불확실 영역, 추가 Goal, scheduler를 유형마다 그대로 씁니다. 대신 지면 scan, 장애물 조회, sweep, 빌드가 유형마다 따로 돌아 비용이 유형 수만큼 늡니다.
  - Manager는 입력(설정, `SetGoal`, 장애물과 Modifier 등록·해제·통지, 추가 Goal, 다시 빌드)을 모든 세션에 나눠 줍니다. 유형이 있으면 한 입력을 작업 하나로 열어 모든 세션이 받은 뒤에 이벤트를 전달하므로, 핸들러가 낸 입력이 세션 사이에 끼지 않습니다. 추가 Goal은 모든 세션에서 같은 번호이므로 한 핸들로 유형마다 읽습니다.
  - 유형(`FlowFieldAgentType`)은 Init이 만든 세션의 것으로, `TryGetAgentType`은 Init부터 Release까지 그 세션의 유형만 찾습니다. 유형이나 Space Mode가 바뀌어 세션을 새로 만든 뒤의 옛 유형과 다른 Manager의 유형은 아무 유형도 가리키지 않습니다.
- **수명:** 유형 세션은 기본 세션을 따라 Init, 끄기(Stop)와 켜기(Restart), Release, 파괴를 함께 겪습니다. Space Mode를 바꾸거나 유형의 개수·이름을 바꾼 뒤의 Init은 유형 세션을 새로 만듭니다.
- **상태와 이벤트:**
  - `State`, `IsReady`, `IsRebuilding`, `TrySample(position, out sample)`처럼 유형을 받지 않는 호출은 `Default` 유형입니다. 다른 유형은 `IsAgentTypeReady(type)`과 유형을 받는 `TrySample`, `IsGoalReady`, `TrySampleNearestShared`로 읽습니다.
  - 추가 유형의 게시도 Manager의 `Revision`을 늘리고 `FieldChanged`를 보냅니다(`FlowFieldSession.AdvanceRevision`).
- **Fault:**
  - 유형 세션이 Fault면 Manager가 "Agent type "이름" failed: ..."로 Fault가 됩니다. Manager가 Fault인 동안 유형 세션은 멈춥니다.
  - 재시도(`RequestRebuild`)와 켜기는 모든 세션을 처음부터 다시 빌드합니다. 이때 유형 세션은 장애물·Modifier 등록과 추가 Goal을 기본 세션에 맞춥니다. 스스로 Fault였던 동안 받지 못한 입력과, Modifier 코드 안에서 미룬 등록이 Fault 즈음 한 세션에만 닿은 경우를 되돌리기 위해서입니다.
- **설정 변경:**
  - Play 중 유형의 값(Clearance, Max Slope, Max Step Height) 편집은 바로 다시 빌드하고, 잘못된 값은 "Agent type "이름": ..."으로 Fault를 냅니다.
  - 유형의 개수와 이름은 Release 뒤에만 바꿉니다. 세션이 있는 동안 바꾸면 Bake Mode 편집처럼 Fault입니다("The number and names of Additional Agent Types can change only after Release.").
  - 이름은 비어 있지 않고 서로 달라야 하며 `Default`일 수 없습니다.
- **Bake:** Bake Asset에는 `Default` 유형만 들어갑니다. `BakedBase`에서 추가 유형은 `RuntimeDynamic`처럼 장면을 scan해 빌드하고, `StaticBaked`는 Init이 거부합니다("StaticBaked loads the Default agent type only; remove the Additional Agent Types.").
- **Editor:** Scene View 표시, Edit Mode 미리보기, Bake는 `Default` 유형만 다룹니다.

## World 영역

`FlowFieldWorld` Asset(Space Mode, Cell Size, Lattice Origin)을 참조하는 Manager는 그 world의 영역입니다. 사용자가 둔 영역들이 격자가 닿는 곳에서 이어지고, Additive로 불러온 여러 장면에 걸칠 수 있습니다.

- **영역:**
  - Manager의 World 칸이 영역의 world를 정합니다. 영역은 world와 같은 Space Mode와 Cell Size를 쓰고, 격자 원점이 lattice(Lattice Origin에서 셀 단위) 위에 있어야 하며(Surface2D는 X·Z, Volume3D는 세 축), 다른 영역과 겹치면 안 됩니다. Init이 이를 검사해 거부하고, Inspector는 lattice 검사를 미리 보여 줍니다.
  - Init이 world에 들어가고 Release와 파괴(장면 내림 포함)가 나옵니다. World 칸은 Release 뒤에만 바뀌고, Play 중 설정 편집으로 격자가 움직이면 영역도 움직입니다.
  - 두 영역의 상자가 한 셀 안에서 만나면 닿는 영역이고, 그 사이 이웃 걸음(경계 칸 쌍, `FlowFieldWorldAreas`)을 미리 찾아 둡니다.
- **World Goal:**
  - `FlowFieldWorld.AddGoal`이 만든 Goal은 모든 영역의 모든 Agent 유형 세션에 같은 번호의 [추가 Goal](#추가-goal)이 됩니다. 그래서 world의 `TrySample`이나 각 영역 Manager가 같은 핸들로 읽습니다. 영향 반경은 0이어야 합니다.
  - 탐색은 seed(`FlowFieldGoalSeeds`)에서 시작합니다. Goal을 품은 영역은 Goal 칸에서, 모든 영역은 닿는 영역의 걸음 수 + 1에서 시작합니다. 경계를 넘는 걸음은 그 유형의 연결 규칙을 따릅니다. Surface2D는 두 칸 모두 걸을 수 있고 Max Step Height 안이며 대각선은 양옆 칸까지, Volume3D는 두 칸이 열려 있고 대각선은 지나는 모서리 칸까지입니다.
  - Goal 칸이 막혔거나 지면이 없으면, Goal을 품은 영역 안에서 가장 가까운(칸 거리 제곱, 같으면 낮은 번호) 걸을 수 있는 칸에서 시작합니다.
  - 풀이는 seed를 비용 순으로 큐 사이에 넣어 한 출발점 탐색처럼 칸을 비용 순으로 내보냅니다. 영역 안에 더 가까운 칸이 없는 seed 칸은 경계 너머로 걷습니다(`FlowFieldNextCell.Portal`). 그래서 닿는 두 영역은 둘을 덮는 Manager 하나와 모든 칸의 걸음 수가 같습니다. 방향은 경계 칸에서 다를 수 있습니다. 영역 안의 같은 걸음 수 칸을 경계 너머 걸음보다 먼저 고르기 때문입니다.
  - World Goal은 seed로만 빌드합니다. seed를 아직 받지 못한 영역(Goal에 닿을 길이 없거나, 그 Agent 유형이 이웃에 없거나, 이웃이 Fault)은 그 Goal의 필드가 없고 `IsGoalReady`가 false입니다. Agent 유형은 이름으로 영역끼리 이어집니다. 격자가 바뀌거나 Stop·재시작하면 seed도 필드와 함께 버리고 다시 받습니다.
  - 어느 영역에도 없는 Goal은 각 영역의 바깥 경계 칸에서 직선 추정(셀 단위 Chebyshev 거리)으로 시작합니다. 이동 주체는 가장 가까운 경계까지 가서 멈춥니다. 영역 밖으로 가는 흐름은 없습니다.
  - StaticBaked 영역은 자기 Goal을 구운 그대로 두고, World Goal은 구운 칸 위에서 런타임에 탐색합니다(Physics 없음).
- **갱신과 epoch:**
  - `FlowFieldBuildDriver`가 매 프레임 빌드 전에 각 world를 갱신합니다(`FlowFieldWorldGoals.Update`). 모든 영역 세션이 모든 World Goal을 가졌는지 보고, 입력(자기 칸, 자기 Goal 필드, 닿는 영역의 칸과 Goal 필드와 그 epoch)이 바뀐 곳만 seed를 다시 계산합니다.
  - Goal 이동, 영역 들어오고 나감, 어느 영역의 새 입력(`BaseStamp`)은 새 epoch를 엽니다. seed는 그 epoch에 빌드된 이웃 필드에서만 옵니다. 바뀐 길을 거쳐 되돌아오는 옛 걸음 수가 서로를 끝없이 키우지 않게 하기 위해서입니다. 그런 이웃이 아직 없는 영역은 지금 필드를 유지합니다.
  - 새 epoch는 지금 epoch가 퍼지기를 마칠 때(그 Goal의 seed가 바뀌지 않고 빌드할 영역이 없을 때)나 30프레임이 지났을 때 시작합니다. 그래서 입력이 매 프레임 바뀌어도 먼 영역까지 퍼지고, 어느 영역의 입력이든 모든 World Goal이 Goal을 품은 영역부터 다시 퍼집니다. 이웃 하나를 지날 때마다 그 영역의 빌드 한 번이 걸립니다.
  - `FlowFieldWorld.SetGoal`은 영역 요청을 다시 빌드하지 않고 seed로만 새 위치를 전하므로, seed가 바뀐 영역만 다시 빌드합니다.
  - World의 명령과 갱신은 모든 영역 Manager의 작업 하나로 돌아, 그 사이 이벤트는 world가 끝난 뒤 핸들러에 갑니다. Modifier 코드 안에서는 world 명령을 거부합니다. 영역이 나가면 그 세션의 갱신 기록도 지웁니다.
  - World Asset의 값(Space Mode, Cell Size, Lattice Origin)은 Play 중에 바꾸지 않습니다. 들어온 영역은 들어올 때의 lattice로 남습니다.
- **경계를 넘는 조회:**
  - World Goal의 조회는 닿는 영역의 칸을 자기 칸 옆에 둔 view(`FlowFieldStitchedCellView`)로 [샘플 안전 규칙](#샘플-안전-규칙)을 적용합니다. 닿는 영역의 칸은 자기 칸 뒤의 번호이고, 자기 좌표로 읽습니다.
  - 경계 너머 걸음은 격자 밖에서 멈추지 않고, 그 걸음이 닿는 칸(불확실 영역, 막힘, 지면, 낙차와 높은 턱)으로 검사합니다. 닿는 영역의 칸은 그 영역의 최신 정보와 불확실 영역을 씁니다.
  - 닿는 영역이 없는 경계 너머 걸음과, 영역의 Goal이 아닌 필드의 경계 너머 걸음은 격자 밖처럼 멈춥니다.
- **공유 등록:**
  - `FlowFieldWorld.RegisterDynamicObstacle`·`NotifyDynamicObstacleMoved`·`NotifyObstacleRegionDirty`와 `RegisterVectorModifier`·`NotifyModifierChanged`(와 해제)는 world의 모든 영역에 나눠 줍니다. 격자에 닿지 않는 영역은 등록만 유지하므로, 영역을 넘어 움직이는 장애물도 한 번 등록하면 됩니다.
  - 매 프레임 빌드 전에 새로 들어온 영역, Fault 뒤 재시작한 영역, 다시 Init한 영역이 빠진 등록을 받습니다(`FlowFieldWorldRegistrations`). StaticBaked 영역은 받지 않고, 한 영역의 실패(예: 같은 Priority)는 한 번 로그로 남기고 그 영역 세션이 다시 시작할 때까지 다시 시도하지 않으며, 다른 영역을 막지 않습니다.
  - World 해제는 각 Manager에서 그 Collider나 Modifier를 해제하므로, 같은 대상을 Manager에 따로 등록했다면 그것도 풀립니다.

## Modifier

- 적용 여부는 두 모드 모두 `FlowFieldVectorUtility.CanApplyModifier`로 판정합니다. 막힌 셀, Goal Anchor, 도달할 수 없는 셀, 기본 방향이 0인 셀에는 적용하지 않습니다.
- 등록, Priority 순서(낮은 값부터, 중복 거부), 변경 통지, 값 Snapshot은 두 모드가 같은 `FlowFieldModifierRegistry`를 씁니다. 이미 등록된 Modifier를 다시 등록하면 false를 돌려줍니다.
- Editor의 중복 경고(`FlowFieldModifierPriorities`)는 Manager의 씬에서 그 Manager를 지정한 Modifier Volume을 비활성까지 모아 같은 문구(`DuplicatePriorityMessage`)로 보여 줍니다.
- 셀 하나에 적용하는 규칙(적용 조건, `Modify()` 호출, 출력 검사)도 공통입니다. 출력은 유한해야 하고 속도는 0 이상, 방향은 단위 벡터나 0이어야 합니다. Surface는 방향이 표면에 접해야 합니다.
- 기본 Modifier의 런타임 값은 `SetSpeedMultiplier()`, `SetNoise()`, `SetPriority()`로 바꿉니다.
- `CaptureSnapshot()`이 빌드에 쓸 값을 고정합니다. Collider 형상까지 복제하지는 않습니다.
- FlowField는 Modifier를 폴링하지 않습니다. 값, Priority, 영역 변경은 `NotifyModifierChanged`로 알립니다. 기본 Modifier Volume은 setter와 `OnValidate`에서 알리고, Influence Collider의 이동과 파괴는 프레임당 한 번 스스로 알립니다.
- `Observe()`는 등록, 통지, 빌드 시작(Influence Collider), 재시작 때 호출됩니다.
- 등록된 Modifier의 설정이 통지 시점에 잘못되면 두 모드 모두 Fault입니다. 등록되지 않은 Modifier를 통지하면 `InvalidOperationException`("Modifier is not registered.")입니다.
- Fault 중에는 해제만 받습니다. 재시도가 전부 다시 빌드하므로 작업을 예약하지 않고, Fault 중 켜진 Modifier는 재시작의 요청 제출 전에 등록합니다.
- Release는 Modifier 등록부를 비웁니다. 다음 Init은 Modifier Volume만 다시 붙이므로(`FlowFieldModifierAttachment`), `RegisterVectorModifier`로 등록한 직접 구현 Modifier는 다시 등록해야 합니다.
- 셀 판정은 `FlowFieldOverlapUtility.CellHalfExtent`로 0.0001 줄인 상자를 써서, 격자에 맞춘 Collider가 이웃 셀을 잡지 않게 합니다.
- Surface 마스크는 장애물과 같은 세로 검사 상자(Obstacle Check Height·Center Offset)로 칸을 판정하므로, 두 값을 바꾸면 장애물과 함께 모든 Modifier 마스크도 다시 만듭니다. 마스크를 다시 만들 때 영향 칸 목록과 사각형은 마스크가 조회한 사각형(Influence Collider Bounds 주변) 안에서만 찾습니다.
- Surface에서 Modifier를 해제해도, 이미 시작한 합성은 그 Modifier가 가져간 칸 목록으로 끝까지 마치고 다음 빌드가 그 칸을 다시 합성합니다.
- Surface에서 해제와 Priority 변경은 값 변경처럼 원인 flag(`ModifierStack`)로 남으므로, 같은 빌드의 다른 변화가 아무것도 바꾸지 않아도 그 칸을 다시 합성합니다.
- Influence Collider가 파괴되면 설정 오류로 보고 Fault를 냅니다. 무시하면 영향 영역이 조용히 사라지기 때문입니다. 기본 Modifier Volume은 파괴를 다음 LateUpdate에 알립니다.
- Modifier 자체가 해제 없이 파괴되면 그 Modifier가 없어진 것이므로 Fault 없이 그 영향이 사라집니다. Session은 할 일이 있는 빌드 전, 빌드가 끝날 때(빌드 도중 파괴된 것), 재시작이 Modifier를 다시 읽기 전에 등록부에서 지웁니다.
  - 빌드 도중 파괴된 Modifier는 그 빌드가 빼고 합성합니다. 합성 중에 파괴되면 두 모드 모두 그 Modifier 없이 합성을 처음부터 다시 합니다.
  - Surface2D가 일부 영역만 합성하던 중이면, 그 밖의 칸에 남은 영향은 빌드가 끝날 때의 해제로 다음 빌드에서 빠집니다.
  - 늦게 부른 `UnregisterVectorModifier`도 그 참조로 지웁니다.

### 직접 구현한 Modifier

`IFlowFieldVectorModifier`를 구현합니다.

- `Observe()`는 Influence Collider, Priority, Revision을 담은 `FlowFieldModifierObservation`을 돌려줍니다. 등록, 통지, 빌드 시작, 재시작마다 불리므로 할당하지 않아야 합니다.
- `CaptureSnapshot()`은 한 빌드에 쓸 값을 담은 `IFlowFieldModifierSnapshot`을 돌려줍니다. snapshot은 만든 뒤 컴포넌트의 바뀌는 상태를 읽지 않아야 합니다.
- `Modify()`는 `IFlowFieldModifierSnapshot`의 멤버로, 셀마다 지금 방향과 속도(`FlowFieldVectorState`)와 셀 정보(`FlowFieldVectorModifierContext`)를 받아 새 값을 돌려줍니다.
- 값, Priority, 영역이 바뀌면 `NotifyModifierChanged`로 알립니다.

`Observe()`, `CaptureSnapshot()`, `Modify()`는 FlowField 작업 안에서 실행되므로, 그 안의 호출은 다음과 같이 제한됩니다.

| 호출 | 동작 |
| --- | --- |
| `TrySample` 등 읽기 | 바로 실행합니다 |
| `RegisterVectorModifier`, `UnregisterVectorModifier`, `NotifyModifierChanged`, `RegisterDynamicObstacle`, `UnregisterDynamicObstacle` | 작업이 끝난 뒤 실행합니다(아래) |
| `Init`, `RequestRebuild`, `Release`, `SetSpaceMode`, `SetGoal`, `NotifyCellsDirty`, `NotifyObstacleRegionDirty`, `NotifyDynamicObstacleMoved` | `InvalidOperationException`("... cannot be called from a Modifier's Observe, CaptureSnapshot or Modify.") |

미뤄진 호출은 다음 규칙을 따릅니다.

- 부른 쪽에는 바로 true를 돌려줍니다(`NotifyModifierChanged`는 반환값이 없습니다). 실제 결과는 나중에 실행할 때 정해집니다.
- 실행할 때 세션이 초기화돼 있으면 비활성(Suspended)이어도 실행합니다.
- `Release()`나 파괴 뒤에는 버립니다. Fault 뒤에는 해제만 실행하고 나머지는 버립니다.
- 버린 등록은 경고("[FlowField] The session stopped, so a deferred Modifier registration was dropped.", 장애물이면 obstacle)를 남기고, 버린 `NotifyModifierChanged`와 해제는 아무것도 남기지 않습니다.
- 실행하다 던진 예외는 Console에 남습니다. 그 호출을 부른 Modifier는 이미 돌아갔기 때문입니다.

## StaticBaked와 BakedBase

### Asset

- Surface2D는 `FlowFieldSurfaceBakeData`, Volume3D는 `FlowFieldVolumeBakeData`이며 두 Bake 모드가 같은 Asset을 씁니다.
- 둘 다 `FlowFieldBakeAsset`을 상속해 포맷 버전(`SupportedFormatVersion`), Revision, 내용 세대(Inspector 편집과 다시 읽기를 알림)를 같은 방식으로 관리합니다. 형식이 다른 Asset은 두 Bake 모드 모두 거부합니다("Surface2D Bake format ... is unsupported; ReBake is required." 등).
- Volume은 방향·속도·탈출 벡터를 저장하지 않고 NextCell과 면 인덱스(`_escapeFaces`)에서 재구성합니다. Surface는 배열을 `FlowFieldBakeBlob`으로 `byte[]`에 비트 그대로 저장합니다.
- Surface 런타임은 Asset을 검증한 뒤 만든 변하지 않는 사본(`FlowFieldSurfaceBakeSnapshot`, Revision마다 한 번)을 읽습니다. request가 들고 있는 Asset 참조는 Revision·내용 세대와 함께 같은 Bake인지 비교하는 데만 씁니다.
- Surface Asset과 그 사본은 설정 비교를 `FlowFieldSurfaceSignature.TryMatchBake` 하나로 하므로 같은 답을 냅니다.

### 모드별 규칙

- Asset이 필요한지는 `FlowFieldBakeModes.UsesBakeAsset`(두 모드), 필드가 고정인지는 `BakeMode == StaticBaked`로 판단합니다.
- Init의 Goal 일치 검사와 Goal·장애물 입력 거부는 `StaticBaked`만, `NotifyCellsDirty` 거부(`FlowFieldFailures.NotAllowedIn`)는 두 모드 모두입니다.
- `StaticBaked` 런타임은 Asset에 구운 Goal을 그대로 쓰고, `TryGetFieldInfo`도 그 Goal을 알립니다. Play 중 Goal Influence Radius를 바꾸고 `RequestRebuild()`해도 그대로입니다. Init은 Goal 유무, Goal Transform 위치, Goal Influence Radius가 구운 Goal과 같은지 검사합니다. `BakedBase`는 구운 Goal을 쓰지 않으므로 검사하지 않습니다.
- `StaticBaked` Volume3D 빌드는 `StaticData` 단계에서 Bake 필드를 4,096셀씩 배열째 복사하고(`CopyCellsToBuild`), Default Direction 칸에 지금의 Default Direction을 씁니다.

### BakedBase

- Surface2D: request의 source는 `BakedBase`(`ForBakedBase`)이고, Surface는 Bake의 Surface, 정적 마스크는 Bake의 막힘(`_blocked`)에서 시작합니다. escape, topology, Goal, BFS는 Scene 빌드와 같은 단계(`BuildSceneBase`)가 다시 계산합니다.
- Bake 세션에는 등록 장애물이 없으므로, 문처럼 런타임에 등록할 장애물도 Bake 막힘에 들어 있습니다. 그래서 Surface2D는 장애물 변화(등록·해제·이동·영역 알림)마다 이전과 현재 영역을 등록 Collider를 뺀 정적 규칙으로 다시 scan합니다.
- Surface2D에서 Bake를 다시 읽는 빌드(첫 빌드, 재시작, `RequestRebuild` 뒤 전체 빌드)는 Bake 막힘에서 다시 시작해, Init 뒤 변화가 닿은 영역(`BakedBaseTouched`)과 등록 장애물 영역을 다시 scan합니다.
- Volume3D: 첫 빌드와 Bake를 다시 읽는 빌드는 Allocate 다음 `BakedObstacles` 단계에서 막힘·topology·탈출 방향을 예산 단위(4,096셀)로 복사하고(`CopyObstaclesToBuild`), 불확실 영역·Init 뒤 변화가 닿은 영역·등록 장애물 영역만 다시 조회합니다. 셋 다 없으면 장애물 결과를 Bake 그대로 쓰고 BFS만 합니다.
- Volume3D 세션에서 sweep이 돌면 Bake를 복사하지 않고 모든 셀을 조회합니다. sweep의 첫 pass는 기록만 하므로, Init이나 다시 켜기 전에 옮긴 미등록 Collider를 Bake만으로는 알 수 없기 때문입니다.
- 게시된 필드가 있으면 `RuntimeDynamic`처럼 Goal 변경은 장애물 결과를 재사용하고, 장애물 변화는 바뀐 영역만 다시 조회합니다. topology와 탈출 방향은 막힘에만 기대므로 Goal이 다른 Bake에서도 복사할 수 있습니다.
- `BakedBase`가 모르는 것(README "Bake")에 더해: 등록 장애물은 등록할 때 지금 자리를 조회하므로 새 자리는 막히고, 등록 전에 옮겼다면 Bake 때 자리만 막힌 채 남습니다.

### Editor Bake

- 두 모드가 같은 실행 코드(`BakeRun`), 입력 검사, 저장 코드(`SaveBakeAsset`)를 쓰고, 모드마다 다른 이름·파일 접미사·참조 속성은 `BakeTarget`에 둡니다.
- Bake는 시작하기 전에 Init이 하는 설정 검사를 `RuntimeDynamic` 기준으로 합니다(`TryValidateBakeSettings`). 그래서 회전한 Manager나 비운 Obstacle Layer처럼 Asset에 들어가는 설정이 잘못되면 Asset을 쓰지 않고, Inspector의 Bake 버튼도 꺼집니다. Asset 내용과 상관없는 `StaticBaked`의 `Sweep Unregistered` 금지는 빠지므로, 이 경우 Bake는 되고 Init이 거부합니다.
- Bake 입력 해시가 시작 때와 달라지면 그 프레임에 멈추고 기존 Asset을 유지합니다("[FlowField] Surface2D Bake: the inputs changed, so the existing asset was kept." 등). 저장은 Undo 한 묶음입니다.
- 저장에 실패하면 기존 Asset의 내용, Revision, GUID를 유지합니다. Asset을 새로 만든 Bake가 실패하면 그때 만든 Asset과 폴더를 지웁니다.
- Volume Bake 검증(`FlowFieldVolumeBakeValidator`)은 열린 칸의 Topology를 풀이와 같은 Burst topology job(`FlowFieldVolumeSolveData.ForTopology`)이 만든 값과 비교하므로, Bake와 런타임이 같은 규칙을 씁니다. Editor 표시는 job이 끝날 때까지 폴링하고, Init의 검사는 기다립니다. job은 payload를 고정한 채 Editor 프레임을 넘을 수 있어, 검증기를 버릴 때 job을 멈추고 기다립니다.

### Asset 경로와 소유자

- Bake Asset은 자기를 Bake한 Manager(`GlobalObjectId`)를 기록합니다(`FlowFieldBakeAsset.OwnerId`, 런타임은 읽지 않습니다).
- 연결된 Asset이 이 Manager가 Bake한 것이면 그 경로에 씁니다. 참조가 비었거나 연결된 Asset을 다른 Manager가 Bake했으면(Manager를 복제하면 참조도 복사됩니다) 고정 경로 `<씬 폴더>/<씬 이름>/<씬GUID>_<ObjectID>_<PrefabInstanceID>_<이름>_<SurfaceBake|VolumeBake>.asset`에 쓰고 참조를 그 Asset으로 바꿉니다.
- 다른 Manager의 Asset이 연결돼 있었으면 그 Asset은 그대로 두고 Console에 "was not baked by this Manager"로 알립니다.
- Prefab 인스턴스 ID가 경로에 들어가므로 같은 Prefab의 두 인스턴스도 경로가 겹치지 않습니다.
- 고정 경로에 다른 Manager가 Bake한 Asset이 있으면 덮어쓰지 않고 실패합니다("... belongs to another Manager. Move or delete that asset before baking."). 소유자가 비어 있는 같은 형식 Asset은 이 Manager의 것으로 보고 덮어씁니다.
- Project 창에서 Asset을 복제하면 소유자도 복사되므로, 그 복제본을 다른 Manager에 연결해 Bake하면 복제본 대신 그 Manager의 고정 경로에 씁니다.
- 다른 Asset으로 옮긴 Bake를 Undo하면 참조는 되돌아가지만 새로 만든 Asset 파일은 남습니다(새 Asset을 만든 첫 Bake의 Undo도 같습니다).
- 고정 경로는 씬의 위치·이름과 Manager 이름을 따릅니다. 그래서 씬을 옮기거나 이름을 바꾸면 참조가 빈 Bake는 새 경로에 씁니다(README "Bake").

## Editor 표시

- Volume3D 표시 상한은 FullVolume 8,192셀, Slice 4,096셀입니다. 넘으면 격자 칸을 고르게 골라 그리고, 계산 해상도는 바뀌지 않습니다.
- Surface2D Scene View는 격자가 2,500셀을 넘으면 방향 화살표를 가로·세로 한 칸씩 건너 4칸 중 1칸만 그립니다. 막힌 칸과 Modifier 칸 표시는 줄이지 않습니다.
- Play Mode의 Surface 표시는 합성을 거친 최종 결과이므로 Asset의 원본 방향과 다를 수 있습니다. 표시는 게시 필드의 층에서 읽습니다(씬 층의 지면, 주체 층의 막힘, Goal 층의 플래그·최종 방향·Modifier 영향).
- 그릴 때 Display Settings는 칸마다가 아니라 한 번만 읽습니다.
- Volume 표시는 갱신 요청이 있거나 상태가 바뀔 때만 Scene View와 열린 Manager Inspector를 repaint하고, Bake를 검증하는 동안에는 보여 주는 진행률(%)이 바뀔 때만 repaint합니다.
- Volume 표시의 Bake 검증 결과는 Asset의 Revision이나 내용 세대가 바뀌거나 Asset 파일을 다시 가져올 때(`FlowFieldVolumeVisualizationAssetPostprocessor`)만 다시 만들고, Undo나 계층 변경 같은 에디터 변경은 표시만 새로 고칩니다.
- Manager를 껐다 켜도 Volume 검증 결과는 남고, 파괴된 Manager만 표시 상태를 지웁니다. Manager Inspector의 문맥 메뉴 "Refresh Volume3D Visualization"으로 다시 그리게 할 수 있습니다.
- Inspector의 Priority 중복 경고는 Edit Mode에서 Manager마다 결과를 보관하고, 계층·속성 변경, Undo, Play Mode 전환, 씬 닫기, Modifier Volume Inspector 편집 때 버립니다. Play Mode에서는 스크립트가 Priority를 바꿔도 에디터 이벤트가 없으므로 보관하지 않습니다.
- 에디터 이벤트(변경 알림, Undo, 계층 변경, Play Mode, 씬 닫기, 업데이트, reload, 종료)는 `FlowFieldEditorLifecycle` 한 곳에서 받아 Surface 미리보기와 Volume 표시에 알리고, Edit Mode를 떠나거나 reload·종료할 때 Bake를 취소합니다. 실행 중인 Bake만 그동안 자기 업데이트를 따로 구독합니다.
- Surface의 Edit Mode 미리보기(`FlowFieldEditorPreview`)는 처음 그릴 때 바로 풀고, 그 뒤 변경은 표시만 해 두었다가 변경이 약 150ms 멈추면 한 번 다시 풉니다. 그동안에는 마지막 결과를 그립니다.
- 마지막 풀이 뒤에 그려진 미리보기만 다시 풀고, 그려지지 않은 미리보기(Show Field 끔, Volume3D)는 버렸다가 다음에 그릴 때 처음처럼 풉니다.
- 실패한 미리보기는 다음 변경까지 그대로 두고, 그 이유를 Manager Inspector에 보여 줍니다("The Edit Mode preview failed: ...").
- 미리보기는 켜진 Modifier만 등록하고, 등록한 Modifier를 기억합니다. Modifier를 다른 Manager로 옮기면 새 Manager와 함께 그 Modifier를 들고 있던 미리보기도 다시 풉니다. 거부된 Modifier(Priority 중복 등)는 미리보기마다 한 번만 Console에 남깁니다.
- Manager Inspector의 문맥 메뉴 "Clear Goal"은 Play 중 런타임 Goal을 받는 세션에서 `SetGoal(FlowFieldGoalRequest.None)`을 부릅니다.

왼쪽은 Surface2D 상세, 오른쪽은 Volume3D 중앙 단면입니다.

<p align="center">
  <img src="Images/feature-surface2d-editor-detail.png" alt="Surface2D 통로 주변 상세 Scene View 방향장" width="48%">
  <img src="Images/feature-volume3d-editor-slice.png" alt="Volume3D 중앙 Y 단면 Scene View 방향장" width="48%">
</p>

## 회귀 테스트

동작을 바꾸지 않는 변경이 정말 아무것도 바꾸지 않았는지, 고친 버그가 다시 생기지 않는지를 Editor에서 확인하는 테스트입니다. Player 빌드는 하지 않습니다.

- **실행:** Unity Test Runner의 EditMode에서 `Common.FlowField.Tests`를 돌립니다. Play Mode가 필요한 시나리오는 테스트가 샘플 씬을 열고, Bake Mode를 저장하지 않고 바꾼 뒤 직접 Play Mode에 들어갑니다. 그래서 Enter Play Mode Options에서 도메인과 씬 다시 불러오기가 꺼져 있어야 합니다(이 프로젝트의 설정).
- **결과 파일:** 마지막 실행의 모든 테스트 결과와 메시지가 `Library/FlowFieldTests/LastRun.txt`에 남습니다. 일부 실패만 알려 주는 도구로 돌렸을 때 전체를 읽는 용도입니다.
- **분류(Category):**

| 분류 | 내용 |
| --- | --- |
| `Gate` | 두 샘플 씬 × Bake Mode 셋의 게시 필드 해시가 기록한 기준값과 같은지와 필드 불변식(Topology가 Goal 범위 안에만 있고, Goal Topology가 지면 Topology를 Goal 영향으로 거른 것과 같음), Edit Mode Bake 결과가 커밋된 Bake Asset과 같은지, 입력 storm과 수명 storm(Play Mode, 그리고 seed를 고정한 Edit Mode) 뒤의 필드가 fresh Release·Init와 같은지 |
| `Protocol` | 빌드 단계, 풀이 중 Stop·Release, 거부·폐기된 빌드, Default Direction 동기화, 증분 결과와 fresh 결과의 일치 |
| `Safety` | 샘플 안전 규칙: 막힘·지면 없음·격자 밖·낙차로 향하는 움직이는 샘플이 없는지 |
| `Obstacles` | 등록·해제·파괴·순간이동, 불확실 영역, 미등록 장애물 sweep, 격자 왕복 |
| `World` | World 영역: lattice·겹침·경계 칸 쌍, seed 탐색(Goal 칸 seed는 추가 Goal과 같은 필드, 여러 seed는 다중 출발 탐색과 같은 걸음 수), 닿는 두 영역과 하나의 Manager가 같은 걸음 수(두 모드), 경계를 넘는 이동 주체와 경계 너머 알림의 정지, 영역 밖 Goal, 장면 스트리밍, 공유 등록, StaticBaked 영역의 World Goal |
| `Agents` | Agent 유형: Manager 설정과 같은 유형은 같은 필드, 넓은 유형은 기본 유형의 막힘을 포함하고 연결을 넘지 않음, 장애물·Goal·Modifier·켜고 끄기가 모든 유형에 닿음, 유형의 잘못된 값과 개수 변경의 Fault, StaticBaked 거부 |
| `Modifiers`, `SpaceMode` | 빌드 중 Modifier 파괴, Priority, Space Mode 편집, Fault와 재시도, scheduler 등록 |
| `Goal`, `EditorBake` | `FlowFieldGoalTarget`, Goal 반경, 추가 Goal(기본 Goal 자리에서 같은 필드, 입력 storm 뒤 fresh와 같고 게시된 필드가 바뀌지 않음, 가장 가까운 Goal, 핸들, 움직이는 기본 Goal·Group Goal과 번갈아 빌드, 빌드 중 Modifier 등록, 기본 Goal이 먼저 게시한 막힘, Manager API와 StaticBaked 거부), Editor 미리보기와 Volume 표시, Bake 거부·대상·복제·다시 Bake |

- **기록값:** 기대값을 규칙으로 정할 수 없는 시나리오(셀 개수 등)는 테스트를 만든 코드가 낸 값을 `FlowFieldRecorded`로 고정합니다. 값이 바뀌면 의도한 변화인지 확인한 뒤 갱신합니다.
- **기다리는 방법:** 샘플 컨트롤러는 Manager가 처음 Ready가 되면 첫 Goal을 정합니다. 그래서 Play 진입 뒤 한 번 더 안정될 때까지(Ready → 1초 → Ready) 기다린 다음 시나리오를 시작합니다. 테스트가 직접 Pump할 때는 정해진 횟수만큼 확인하면 만료되는 예산(`FlowFieldTestBudget.AfterChecks`)으로 빌드를 정한 지점에서 멈춥니다.
- **재현:** `EditModeStormTests`와 `AdditionalGoalTests`는 테스트가 직접 Pump하는 Edit Mode 세션에서, 정해진 횟수만큼 확인하면 만료되는 예산과 워커를 기다리는 Pump로 돕니다. 그래서 매번 같은 순서로 실행되고, 실패 메시지의 seed와 단계 번호로 같은 실행을 다시 만들 수 있습니다.
- **구조와 테스트:** 테스트는 Session 수준 이름(`HasActiveBuild`, `HasPendingRequest`, `HasOwedInput`, `IsWaitingForExternalCompletion`, `Revision`, `State`, 불확실 영역 수, 추가 Goal 집합과 그 필드)만 읽습니다. 그래서 Builder 내부를 바꿔도 테스트는 그대로입니다.
- **주의:** 테스트가 도는 동안 스크립트를 고치면 Play 진입 때 컴파일이 일어나 그 뒤 테스트가 Play Mode에 들어가지 못합니다.

## UML 갱신

원본은 `Docs/*.puml`이고, 렌더링한 PNG를 `Docs/Images`에 저장해 문서에 넣습니다. Graphviz 없이 PlantUML 1.2024.3으로 렌더링합니다. Docs 폴더에서 실행합니다.

```sh
java -Djava.awt.headless=true -Djava.io.tmpdir="$TMPDIR" -jar plantuml.jar \
  -tpng -charset UTF-8 -o Images \
  flowfield-uml.puml flowfield-structure.puml flowfield-pipeline.puml
```

- 임시 폴더에 쓸 수 없으면 종료 코드 0으로 0바이트 PNG가 나오니 파일 크기를 확인합니다.
- 한글은 macOS의 `Apple SD Gothic Neo` 글꼴로 렌더링합니다.
- 클래스 멤버 줄에 괄호가 들어가면 메서드로 읽히지 않도록 `{field}`를 앞에 붙입니다.
