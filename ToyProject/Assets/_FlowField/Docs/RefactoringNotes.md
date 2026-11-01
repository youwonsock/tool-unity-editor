# FlowField 구조 개선 및 회귀 검증

검증 환경: Unity 2023.2.20f1 / macOS / Metal, 2026-10-23~24.

## 보존 범위

- 공개 Provider·Controller API, MonoBehaviour 타입과 직렬화 필드 유지.
- 기존 Scene·Prefab·Bake Asset·유지 에셋의 meta/GUID·Packages·ProjectSettings 유지. 단순화 단계에서 참조가 없는 순수 C# 공통 요청 파일과 그 meta만 함께 제거한다.
- 최초 구조 개선에서 323개, Volume 안전성 분리 단계에서 기존 349개 보존 대상 파일의 SHA-256 일치 확인. 공개 `FlowFieldContracts.cs`도 포함한다. 장애물 관측 분리 단계는 FlowField와 Packages·ProjectSettings의 기존 보존 대상 173개를 별도로 기록해 대조했다.
- BFS 비용·이웃 순서·코너 차단·동률 선택·GPU sentinel 검증·작업 chunk 크기 유지.
- Surface의 내용 변경 기준 Revision과 Volume의 결과 교체 기준 Revision 유지.
- Surface의 콜백 완료 처리, Volume의 Scheduler 검증, Volume Modifier 변경 시 전체 빌드 요청 정책 유지.
- 앞서 추가한 FlowField·TransformPath Samples의 `UnityEngine.UI` 참조 유지.

구조 분리 단계는 책임 분리와 중복 제거다. 실행 시점, 재계산 범위, 배열 재사용 정책과 2ms 협력 예산의 의미는 그대로 유지한다. 이후 관측 콜백에서 수명이 바뀌었는데 이전 실행이 계속되던 오류를 별도로 수정했다.

## 단계별 변경

| 단계 | 변경 | Unity 검증 |
| --- | --- | --- |
| 기준선 | 현재 구현의 회귀 테스트 구성 | 34/34 통과 |
| 1 | Volume Modifier 등록·관측·검증·Snapshot 캡처를 Registry로 분리 | 34/34 통과 |
| 2 | Volume 작업 상태·게시 결과·결정적 탐색 연산·GPU 결과 검증 분리 | 34/34 통과 |
| 추가 보호 | 오래된 실패 콜백과 게시 이벤트 중 Release 사례 추가, 단순 DTO 검사 제거 | 35/35 통과 |
| 3 | Surface 성공·실패 경로의 오래된 결과 폐기 판정을 `RejectStaleBfsResult()`로 통합 | 35/35 통과 |
| 안전성 기준선 | Volume 경계 회귀 20개 추가, 기존 Session 구현에서 먼저 검증 | 55/55 통과 |
| 4 | Volume 안전성 상태·샘플링 판정을 `FlowFieldVolumeSafetyState`로 분리 | 55/55 통과 |
| 검증 보강 | 콜백 예외 수집·소유자 assertion, 이벤트 2개·장애물 회귀 9개 추가 | 의도적 실패 1건 확인 후 66/66 통과 |
| 5 | Volume 장애물 관측 분리, 불완전 스윕·Clear 경계 2개 추가 | 68/68 통과 |
| 관측 재진입 회귀 | 등록·미등록 이동/파괴와 수명 변경, Clear·등록 해제·중첩 관측 25개 추가 | 수정 전 93개 중 19개 실패 재현 |
| 6 | 관측 취소 토큰·Pump 수명 경계 보강, Dispose 요청 해제, 미사용 멤버 2개 제거 | 에셋 검사 4개를 포함해 97/97 통과 |
| Modifier 재진입 기준선 | CaptureSnapshot·Modify와 등록 변경·중첩 Pump 등 42개 추가 | 기존 97개 통과, 추가 42개 중 39개 실패 재현 |
| 7 | 캡처 토큰·작업 소유권 검사·Pump 재진입 방지, Volume 수명 전이·취소 처리 통합 | 139/139 통과 |
| 단순화 기준선 | 요청 소유권·Scheduler·Manager PlayMode 회귀 21개 추가 | 기존 139개 통과, 새 테스트에서 중복 자동 게시 1개 실패 재현 |
| 8 | 미사용 공통 요청·Base 상태·작업 인터페이스 멤버 제거 | 159/160 통과, 기존 중복 게시 1개만 동일하게 실패 |
| 9 | Volume 대기 요청 복사본·Manager 요청 캐시 제거, 최신 수락 입력으로 비교 통일 | 160/160 통과 |
| 10 | Surface 준비 입력·결과와 대기 요청 정리, 미사용 코드 제거 | 184/184 통과 |
| Surface 수명 기준선 | 확인용 6개를 정식 회귀로 편입 | 기존 184개 통과, 신규 6개 실패 재현 |
| 11 | 상태·게시 콜백 수명 보호, 중첩 Pump 차단, 늦은 solver 쓰기 방지 | 인접 경계 포함 203/203 통과 |
| 12 | 잔여 중복 상태·미사용 인자 제거, 실제 GPU 취소 회귀 2개 추가 | 205/205 통과 |

각 단계는 원본 프로젝트의 임시 복사본에서 실제 Unity Test Runner로 실행했다. 최종 컴파일의 C# 오류·경고는 모두 0건이며, 테스트 실패·스킵도 0건이다.

## 최종 책임 분리

| 타입 | 책임 |
| --- | --- |
| `FlowFieldVolumeSession` | 요청 수락·수명·취소·Pump 재진입 경계·셀별 Physics 판정·Scheduler/GPU 실행 연결·게시 정책 |
| `FlowFieldVolumeObstacleTracker` | 등록·이전 Bounds·미등록 관측·Physics 스윕·쿼리 버퍼 소유, 변경 Bounds의 동기 통지 |
| `FlowFieldVolumeSafetyState` | 불확실 영역·안전성 Epoch·셀 Snapshot 소유, 현재 셀·다음 경로·탈출·기본 방향의 보수적 정지 판정 |
| `FlowFieldVolumeModifierRegistry` | 등록·해제, Priority 검증, Collider/Revision 관측, 값 Snapshot 캡처와 캡처 취소 토큰 |
| `FlowFieldVolumeBuildContext` | 한 작업의 staging 배열, 탐색·검증·단계 진행 커서, Readback 참조 |
| `FlowFieldVolumeFieldData` | 함께 게시되는 완성된 기본·최종 결과 배열과 Goal 식별자 |
| `FlowFieldVolumeNavigation` | Physics와 무관한 Topology·Goal·Managed BFS·방향·탈출 계산 |
| `FlowFieldVolumeGpuResultValidator` | 고정된 작업 Topology에 대한 GPU 결과 검증 및 staging 적용 |
| `FlowFieldSession` | Surface 입력·게시 소유권, 수명 세대, 상태·게시 콜백과 Pump 재진입 경계 |
| `FlowFieldSession.RejectStaleBfsResult()` | Surface 성공·실패 완료 경로에 공통인 폐기·재요청 판정 |
| `FlowFieldSessionBase` | 공통 조회·명령과 게시·상태·실패 이벤트, 구독자 예외 격리 |

Volume Session은 2,812줄에서 1,974줄, 안전성 분리 후 1,797줄, 장애물 관측 분리 후 1,572줄로 줄었다. 관측 재진입 보호 후 1,582줄, Modifier 재진입 보호 후 1,623줄이며, 상태 단순화 후 1,572줄, 추가 단순화 후 현재 파일은 1,557줄이다. 원래 Session에 있던 탐색·GPU 검증 함수 13개의 본문을 이동 후 대조해 계산 규칙이 같은지 확인했다. GPU 버퍼와 NativeArray의 소유권은 기존 `FlowFieldComputeSolver`에 남아 있다.

## 회귀 테스트 구성

테스트 어셈블리: `Script/Tests/Common.FlowField.Tests.asmdef`.
Editor 전용이며 `UNITY_INCLUDE_TESTS`와 `TestAssemblies`를 사용한다.

| 테스트 그룹 | 건수 | 주요 검증 |
| --- | ---: | --- |
| Surface | 12 | 소유자 Pump, 초기 미준비, 이전 결과 유지, 최신 Goal, 늦은 콜백, 실패, 이벤트 중 Release, Modifier 탐색 재사용 |
| Volume | 10 | 요청 병합, 기존 Revision 정책, Modifier 값·영역·제거, Priority/Trigger 검증, 새 장애물 차단, 수명 재시작 |
| Volume 기본 계약 | 8 | XYZ 인덱스, 코너 차단, Goal 단일 Anchor, 잘못된 Bake 입력 보존, 격자 상한, 정적 입력 검증 |
| 실제 GPU | 3 | Surface·Volume Managed 결과 일치, GPU wave 상한 초과 후 같은 입력의 Managed 전환 |
| 샘플 PlayMode 통합 | 2 | Surface RuntimeDynamic 게시·Goal 변경, 기존 Volume StaticBaked 로드 |
| Volume 안전성 경계 | 17 | 불확실 영역·복구, 장애물 이동·파괴·상태 변경, 격자 교체, XYZ 경계, 탈출 이웃, 영역 상한, StaticBaked, 세션 격리 |
| Volume GPU 수명 경계 | 3 | 실제 Readback 대기 중 Release·Suspend·격자 교체, 늦은 완료 뒤 새 게시 보존 |
| 이벤트 실패 전달 | 2 | Surface·Volume의 콜백 assertion을 소유자에서 실패로 확인, Session 예외 격리 유지 |
| Volume 장애물 관측 회귀 | 9 | 중복·잘못된 등록, 등록/스윕 전환, 영역 밖 이동, 스윕 재활성화, Layer 정책, 520개 쿼리 버퍼 확장, 파괴 후 대체 |
| Volume 관측 경계 | 2 | 불완전 스윕의 이전 관측 유지, Clear 후 등록·관측 상태 해제 |
| Volume 관측 중 수명 변경 | 20 | 등록/미등록 × 이동/파괴 × Release·Suspend·Dispose·재초기화·Fault, 중단된 Pump의 새 수명 요청 소비 금지 |
| Volume 관측 재진입 경계 | 5 | 발견·갱신·제거 중 Clear, 알림 중 등록 해제, 중첩 스윕 후 이전 관측 재개 금지 |
| 에셋 무결성 | 4 | GUID 참조, Bake payload(당시 v5, 현재 v6), 씬·Prefab의 스크립트/오브젝트 참조, 제거된 직렬화 필드 검사 |
| Modifier 호출 중 수명 변경 | 24 | CaptureSnapshot/Modify × Release·Suspend·Dispose·재초기화·Fault·Cancel × 호출 뒤 예외 유무 |
| Modifier 중첩 Pump | 3 | 같은 Session의 Capture/Modify 중 재진입 방지, 다른 Session의 Pump 허용 |
| Modifier 캡처 중 등록 변경 | 8 | 최초/기존 게시 후 추가·자기 제거·다른 Modifier 제거, 오래된 캡처 예외, 최신 Goal 보존 |
| Modifier 실패 경계 | 7 | 오래된 null Snapshot, 현재 작업의 정상 실패 전달, Fault 상태 콜백에서 재초기화한 뒤 이전 실패 전달 금지 |
| 요청 소유권 | 14 | Cancel·Suspend·Fault 뒤 장애물/Modifier 변경이 최신 Goal·Grid를 사용, 잘못된 입력이 이전 수락 입력을 덮어쓰지 않음 |
| Scheduler 수명 경계 | 3 | 게시 콜백에서 현재 Session Release/Dispose 후 다른 작업 진행, 중첩 PumpAll 방지 |
| Manager PlayMode | 4 | 자동 중복 제거, 명시적 재빌드·재초기화·재활성화, 잘못된 Goal 거부, Goal 콜백 뒤 중복 게시 금지 |
| Surface 입력·결과 단순화 회귀 | 20 | 연속 입력·관측·게시 콜백, 수명과 Backend 소유권, 반복 입력 |
| Volume 메타데이터 getter 수명 | 4 | Priority·Revision 접근 중 Release·재초기화 |
| Surface 수명·재진입 | 19 | 확인용 6개, Retry·Ready·Dispose·중첩 Pump, 늦은 콜백과 작업 배열 쓰기 |
| Surface GPU 취소 | 2 | 실제 GPU 결과 적용·wave 상한 실패 후 Managed fallback 전에 수명 확인 |
| 합계 | 205 | 기존 184 + 추가 21, 실패 0, 스킵 0 |

샘플 통합 테스트는 EditMode 테스트의 `EnterPlayMode`/`ExitPlayMode`로 실행한다. 테스트 수에 포함된 두 사례이므로 별도의 PlayMode 테스트 2건으로 중복 집계하지 않는다. 임시 복사본에서 실제 샘플 씬을 로드하며 원본 씬은 저장하지 않는다.

GPU 비교는 실제 Compute Shader와 AsyncGPUReadback을 사용한다. 지원하지 않는 환경에서는 GPU 테스트가 명시적으로 스킵되므로, 그 실행 결과를 GPU 통과로 해석하면 안 된다. 이번 Metal 검증에서는 기존 세 사례와 Volume 수명 경계 세 사례, Surface 취소 두 사례 모두 스킵 없이 통과했다.

## Volume 안전성 분리의 동작 경계

`FlowFieldVolumeSafetyState`는 불확실 Bounds 목록, 안전성 Epoch와 셀별 Unknown/Clear/Blocked Snapshot을 소유한다. 경로 중간 셀, 탈출 이웃, 기본 방향의 모든 축 조합에 대한 기존 판정도 이 타입으로 이동했다. Session은 Physics 관측과 재계산 요청, GPU 세대 검증과 결과 게시 순서를 유지한다. 기존 판정·영역 처리 함수 7개의 본문을 가시성과 상태 참조 이름을 정규화해 대조했다.

- Dirty 영역의 기존 `CellSize + ObstacleClearance` 여백, 64개 상한의 보수적 병합과 Epoch 갱신 규칙을 유지한다.
- 장애물 Layer/Clearance 변경으로 전체 Bounds를 무효화하는 경로는 Dirty 알림과 구분하며, 기존처럼 별도의 확장이나 Epoch 갱신을 하지 않는다.
- 새 안전성 Snapshot의 할당·초기화를 끝낸 뒤 게시 필드와 Revision을 교체하고, 안전성 Snapshot을 함께 게시한 다음 기존 조건으로 불확실 영역을 해제한다. 배열은 외부로 노출하지 않는다.
- Suspend는 안전성 상태를 유지한다. Release/Dispose는 Snapshot·영역을 기존 순서로 해제하고 Initialize에서 Epoch를 초기화한다. 기존 논리 메모리 예약식도 유지한다.
- StaticBaked 샘플링의 안전성 Snapshot 예외와 저장된 Bake 데이터는 유지한다.

추가 테스트는 기존 `IFlowFieldBuildOperation`의 실행 예산에 가상 deadline을 주어 장애물 관측이 끝난 시점에서 빌드를 멈춘다. 런타임 코드에 테스트 전용 훅이나 private 필드 접근을 추가하지 않았다. 장애물 이동은 이전 입력의 결과가 중간 게시되는 콜백 안에서도 두 영역이 정지하는지 확인하며, 탈출 검사는 현재 셀을 제외하고 이웃만 불확실해지도록 구성했다.

실제 GPU 테스트는 `IsWaitingForExternalCompletion`을 확인한 뒤 수명·격자를 바꾼다. 이전 Readback을 배출하고 Editor 업데이트를 진행한 후 새 Grid·Goal·Revision·게시 횟수가 유지되는지 확인한다. 샘플 씬 통합 두 사례는 기존 35개 안에 포함돼 있다.

## 콜백 assertion과 장애물 관측 분리

이전 평가에서 게시 콜백 내부의 assertion이 `FlowFieldSessionBase.InvokeUserEvent()`의 구독자 예외 격리에 흡수되는 문제를 확인했다. 테스트 전용 `FlowFieldRegressionFixture.EventFailures`가 `EventFailed` 예외를 수집하고, 콜백 바깥의 TearDown에서 assertion으로 검증한다. `EventFailed` 안에서 다시 assertion을 던지지 않는다. 이 방식은 콜백 중 Release/Dispose가 일어나도 수집한 실패를 유지한다. 생산 코드의 이벤트 예외 격리 정책은 그대로 유지한다.

검증용 복사본의 기존 이동 테스트 콜백에 `Assert.Fail()`을 주입해 실제 Unity에서 테스트 1개가 실패하고 프로세스가 코드 2로 종료되는 것을 확인했다. 이 실패 주입은 복사본에서만 적용하고 즉시 복원했다. Surface·Volume 각각의 영구 회귀 사례는 콜백 assertion 이후에도 Session이 Ready를 유지하면서 소유자의 실패 검사에서 `AssertionException`이 발생하는지 확인한다. 의도적 실패 검증은 최종 68개 통과 집계에 포함하지 않는다.

`FlowFieldVolumeObstacleTracker`는 기존 등록 참조와 이전 Bounds, 미등록 Collider 관측, 완전한 스윕의 제거 확정, 512개에서 최대 65,536개까지 확장되는 쿼리 버퍼를 소유한다. 상태 변경은 기존 호출 위치에서 동기적으로 `MarkObstacleRegionDirty()`에 전달한다. SafetyState의 영역 확장·Epoch·정지 판정과 Session의 요청 병합·수명·게시 정책은 유지한다. 등록 Collider의 셀별 충돌 검사 및 Modifier와 공유하는 overlap 버퍼도 Session에 남겼다.

- 등록 관측은 기존 Bounds 변경 기준, 미등록 관측은 기존 enabled/active/trigger/layer/Bounds 비교 기준을 유지한다.
- 파괴된 Unity Collider의 참조 비교와 캐시 Bounds 보존, 등록·스윕 사이 전환 시 관측 제거 순서를 유지한다.
- 읽는 곳이 없는 `_dynamicBounds` 중복 Dictionary를 제거했다. 등록 순서와 이전 Bounds 배열은 유지한다.
- Release/Dispose는 등록·관측을 비우고, 성장한 스윕 버퍼 용량은 기존처럼 유지한다. 논리 메모리 예약식은 동일한 버퍼 크기를 참조한다.
- 이동한 등록·관측·스윕·상태 비교 본문 7개를 이름과 삭제된 중복 쓰기를 정규화해 대조했다.

실제 Physics 스윕의 버퍼 확장은 Collider 520개로 검증했다. 쿼리 포화로 발생하는 불완전 결과의 관측 유지·제거 확정은 Tracker의 배치 반영 경계에서 검증했으며, 실제 65,536개 Collider 포화 부하를 실행한 것은 아니다.

## 관측 콜백의 수명·재진입 보호

장애물 변경의 동기 알림은 Session의 `StateChanged(Building)` 구독자를 호출할 수 있다. 구독자가 Release/Dispose로 컬렉션을 비운 뒤 이전 관측이 List 인덱스에 쓰거나 Dictionary 순회를 이어가면 예외가 발생했다. Suspend/Fault 뒤 이전 관측이 Building 상태를 다시 만들거나, Release·Initialize 뒤 이전 Pump가 새 요청을 즉시 소비하는 경로도 있었다.

- Tracker는 관측 시작 시 토큰을 캡처하고 각 Dirty 알림 전후에 유효성을 검사한다. Clear·등록·등록 해제·중첩 관측은 이전 토큰을 무효화한다. 중단된 관측은 캐시 쓰기·제거·다음 알림을 이어가지 않는다.
- Session의 기존 세대 변경은 관측도 무효화한다. Pump는 등록 관측 완료 여부와 세대·수명 상태를 다시 검사한 뒤 빌드에 진입한다. 장애물 관측의 취소와 GPU 결과의 세대 검사는 각 소유자가 유지한다.
- Dispose는 대기 요청과 최신 입력의 유효 표시를 함께 해제한다. 수명 종료 뒤 재계산 요청이 남지 않는다.
- 참조가 없던 Volume Session의 `_registrationOrder`와 내부 `SetSpaceMode()`만 제거했다. 공개 Controller·Manager의 모드 변경 API는 유지한다.

새 회귀 25개를 수정 전 구현에 적용하면 기존 68개는 통과하고 추가 25개 중 19개가 실패한다. 수정 후에는 기존 68개와 새 29개 모두 통과한다. 기존 검증 XML의 전체 테스트 식별자를 최종 XML과 대조했고 누락된 사례는 없다. 실제 Metal GPU 6개와 샘플 씬 통합 2개도 스킵 없이 통과했다.

씬 2개·Prefab 2개·Bake 2개를 검사했고, 저장된 GUID 참조 235개와 FlowField 직렬화 컴포넌트·에셋 15개를 확인했다. 기존 FlowField 메타데이터·씬·Prefab·Bake·셰이더·asmdef와 공개 계약·Manager·Bake 소스의 해시는 검토 기준선과 동일하다. 마이그레이션 전용 클래스·변환 에셋, 제거된 직렬화 필드, 미사용 생산 에셋, 누락/중복 GUID는 발견하지 못했다. v4 입력을 거부하는 기존 Bake 검증은 현재 v5 계약의 일부다.

저장 공간 확보 후 재검증 시점에는 기존 보고서 대비 `ProjectSettings/TimeManager.asset`의 외부 변경이 이미 있었다. 해당 파일을 수정하거나 되돌리지 않았으며, 재개 시점의 설정을 사용했다. 검증 전후의 보존 대상 178개 해시는 동일하다.

## Modifier 호출과 Volume 취소 경계

추가 테스트를 먼저 실행해 기존 97개가 통과하는 동안 새 42개 중 39개가 실패하는 것을 확인했다. 캡처 도중 Release·재초기화가 발생하면 이전 요청에 새 세대가 붙어 게시될 수 있었고, 등록 목록 증가 시 Snapshot 배열 범위를 넘거나 제거 시 일부만 캡처된 배열이 게시됐다. Modify 중 수명이 바뀌어도 최대 한 단계의 셀 처리가 계속됐으며, 이후 예외 처리가 새 요청을 지우거나 새 수명을 Fault로 만들 수 있었다.

- Session은 Pump 진입 시 세대와 작업 참조를 보관하고, 같은 Session의 중첩 Pump를 방지한다. 가드는 `finally`에서 해제하며 다른 Session은 독립적으로 Pump할 수 있다.
- Registry는 등록 집합·관측 값 변경과 수명 무효화에 대응하는 캡처 토큰을 가진다. CaptureSnapshot과 이어지는 메타데이터 접근 뒤 토큰을 확인하므로, 무효화된 캡처가 배열 쓰기나 다음 Modifier 호출을 이어가지 않는다.
- Session은 캡처 이전 세대를 작업에 사용한다. 등록 변경으로 중단된 캡처는 최신 수락 입력을 다음 소유자 실행 경계에서 다시 시도한다. 최초 BuildContext가 생기기 전이거나 더 최신 Goal이 대기 중인 경우도 보호한다.
- Modify가 반환하면 결과 검증·셀 배열 쓰기 전에 작업 소유권과 수명을 확인한다. 단계 시작·완료와 게시 경계에서도 같은 조건을 검사한다. 정상적인 실행 중 등록 변경은 기존 Snapshot 작업 집합과 후속 요청 정책을 유지한다.
- `InvalidateBuildGeneration()`은 빌드 세대·관측 토큰·캡처 토큰을 함께 무효화한다. `DiscardBuild()`는 현재 작업과 예약량을 정리하고, `CancelCurrentBuild()`는 여기에 대기 요청 취소를 더한다. Initialize·입력 격자 교체·Suspend·Release·Fault·Dispose·Cancel·게시 격자 교체에서 필요한 조합을 사용한다. 안전성 Epoch와 GPU 자원 소유권은 기존 역할을 유지한다.
- 이전 작업이 수명 변경 뒤 던진 예외는 새 작업에 적용하지 않는다. 현재 유효한 작업의 오류는 기존처럼 Fault와 실패 이벤트 한 번으로 전달한다. Fault 상태 콜백이 Release·재초기화하면 이전 실패 이벤트를 새 수명에 전달하지 않는다.

최종 실행은 기존 97개와 새 42개 모두 통과했고, C# 오류·경고와 스킵은 0건이다. 실제 GPU 6개와 샘플 씬 통합 2개도 포함한다. 씬·Prefab·Bake·기존 meta/GUID·asmdef·셰이더·공개 계약·Manager·Bake 소스와 Packages·ProjectSettings를 포함한 보존 대상 180개의 해시가 변경 전후 동일하다. 새 생산 MonoBehaviour나 직렬화 필드, Bake 형식 변경과 마이그레이션 코드는 추가하지 않았다.

## 미사용 계층 제거와 요청 상태 단순화

이번 단계는 사용하지 않는 공통 표현과 입력 복사본을 줄였다. 새 런타임 타입이나 대체 추상화는 추가하지 않았다.

- 값을 기록한 뒤 읽는 사용처가 없던 공통 요청 DTO와 변환·보관 경로를 제거했다. `FlowFieldBuildRequest.cs`와 그 meta의 GUID는 생산 에셋·Packages에서 참조되지 않는 것을 확인한 뒤 함께 삭제했다.
- Base의 미사용 수명 세대·게시 ID·수락 입력·유효 표시와 관련 조회·변경 메서드를 제거했다. Base에는 공통 명령과 이벤트 경계를 유지하며, 실제 수명 세대와 입력은 각 모드 Session에 남긴다.
- 작업 계약의 미사용 Owner·별도 상태·Progress·Failure·DisposeOperation과 Owner/상태 enum을 제거해 9개 멤버에서 4개로 줄였다. Scheduler가 쓰는 대기·작업 여부·Pump와 회귀에서 검증하는 Cancel은 유지한다. Editor Bake가 실제 사용하는 Session의 BuildProgress도 유지한다.
- 실행 예산은 IsExpired만 노출하며 사용하지 않는 RemainingMilliseconds와 내부 시계의 외부 조회를 제거했다. 예산 판정·시계 호출 방식은 유지한다.
- Volume의 대기 입력 복사본과 Manager의 요청 캐시·유효 표시를 제거했다. 최신 수락 입력 한 벌에 대기 플래그를 붙이고, 작업 중 입력은 BuildContext, 게시 입력은 FieldData가 각각 고정한다. 취소가 대기 작업을 지워도 최신 입력은 유지하며 Release/Dispose에서 무효화한다.
- Manager는 Session의 최신 수락 입력으로 자동 변경을 비교한다. Goal 콜백에서 더 최신 입력이 수락된 뒤 외부 호출의 오래된 입력으로 Manager 캐시가 덮여 같은 결과가 다시 게시되던 문제를 함께 해결했다. 명시적 RequestRebuild·재활성화·재초기화의 같은 설정 재계산 정책은 유지한다.

런타임 타입 3개와 중복/미사용 상태 필드 7개를 제거했고, 수정·삭제한 런타임 파일의 합계는 232줄 줄었다. 동일 비용 BFS·이웃 순서·GPU 검증·안전성 판정·게시 Revision 규칙과 공개 Provider/Controller·Modifier 계약은 유지한다. Surface의 요청 상태는 이 단계에서 유지했으며, 후속 사용처 분석과 회귀를 거쳐 아래 추가 단순화 단계에서 최신·대기 복사본을 통합했다.

기준선에서 기존 139개와 신규 20개는 통과했고, 신규 Manager 재진입 사례 1개가 Revision 2 뒤 같은 입력을 다시 게시해 3이 되는 문제를 재현했다. 미사용 계층 제거 후에도 동일한 결과를 확인했으며 요청 소유권 정리 후 기존 139개와 신규 21개 모두 통과했다. C# 오류·경고와 스킵은 0건이다. 실제 GPU 6개·기존 샘플 씬 2개·추가 Manager PlayMode 4개를 포함한다.

씬·Prefab·Bake·유지 meta/GUID·asmdef·셰이더·공개 계약·Bake 소스·Packages·ProjectSettings의 보존 대상 178개 해시는 동일하다. 의도적으로 제거한 미참조 요청 meta는 별도 기록한다. 수정한 Manager의 직렬화 설정 선언도 변경 전과 동일하다. README·기술 문서·UML 원본과 PNG를 함께 갱신했다.

## Surface 입력·결과와 미사용 코드 추가 단순화

생산 코드를 수정하기 전에 회귀 24개를 추가해 기존 160개와 함께 184개가 통과하는 기준선을 확보했다. 테스트는 Pump 전 연속 요청, GPU 완료·실패 중 최신 Goal 보존, Submit·관측으로 들어오는 Default Direction 변경, 게시 콜백의 다음 요청, Suspend·Release·Fault 후 늦은 콜백, 주입·Factory Backend 소유권, 반복 입력의 무변경 처리와 Modifier 메타데이터 getter의 캡처 수명 중단을 검증한다. 모든 콜백 assertion은 기존 EventFailures 수집 후 테스트 소유자에서 확인한다.

- 호출처 없는 Surface 검증 래퍼와 내부 접근자·보조 메서드 36개를 제거했다. 읽는 곳이 없는 Surface의 LastSurfaceRevision·LastStaticObstacleRevision도 제거했다. 파일 안의 실제 Bake 구현과 기존 meta/GUID는 유지했다.
- Surface 준비 요청은 Obstacles·Goal·SurfaceChanged 세 값만 저장한다. Grid·Surface는 Obstacles에서 얻고, 사용하지 않던 DirtyFlags·MaxGpuWaves·Version 전달을 제거했다. 실제 BFS 요청의 실행 제한과 버전은 유지한다.
- 준비 결과는 ObstacleMaskChanged·ObstacleDirtyRegion·HasWalkableSurface·GoalStatus 네 값만 저장한다. GoalChanged는 GoalStatus에서 계산하며 FlowFieldBuildDelta와 사용하지 않던 배열·Workspace·진단 메타데이터 전달을 제거했다. 장애물 마스크 생성과 Physics 쿼리는 동일하다.
- Surface의 대기 요청 복사본을 제거해 최신 수락 입력과 pending 플래그로 통합했다. Pump의 지역 캡처, 실행 중 요청, 게시 요청, 별도 Default Direction 관측, 요청·기본 필드 버전과 콜백 세대는 유지한다. 이 시점들을 한 값으로 덮어쓰지 않는다.
- Volume Modifier 작업 항목의 미사용 Priority·Revision 저장을 제거했다. getter가 외부 구현이므로 기존 호출 순서와 각 호출 뒤 수명 검사는 유지했다. Registry의 실제 변경 감지와 정렬용 메타데이터도 유지한다.
- 동일한 Surface Backend 해제 구현을 하나로 합쳤다. 주입된 Backend의 재사용·영구 Dispose와 Factory Backend의 해제 정책은 동일하다. 스케줄러가 사용하지 않는 Cancel은 Session 메서드로 옮겨 작업 인터페이스를 4개에서 3개 멤버로 줄였으며 기존 취소 회귀의 호출 경로를 함께 변경했다.

새 런타임 타입 없이 미사용 타입 2개를 제거했고, 런타임 파일 15개의 합계는 340줄 줄었다. 기준선·미사용 코드 제거·요청/결과 축소·요청 소유권 정리·최종 검증의 각 단계에서 184개가 통과했다. 실제 Metal GPU 6개와 기존 샘플 씬 통합 2개, Manager PlayMode 통합 4개를 포함하며 스킵과 C# 오류·경고는 0건이다. 원래 160개 테스트의 식별자와 결과도 최종 XML에서 대조해 누락 없이 통과한 것을 확인했다.

씬·Prefab·Bake·기존 meta/GUID·asmdef·셰이더·공개 계약·Bake 소스·Packages·ProjectSettings를 포함한 보존 대상 186개와 Manager 직렬화 설정 선언은 변경 전후 동일하다. 기존 Bake 2개의 v5 payload 검증과 저장된 GUID 참조 235개 해석, 씬·Prefab의 누락 스크립트·참조 검사도 통과했다.

이번 단계의 XML·Unity 로그·소스 차이·보존 해시·검증 요약은 `ToyProject/Logs/FlowFieldSurfaceSimplification/`에 보관한다. 검증은 원본의 실행 중인 Editor 씬을 전환·저장하지 않고 독립 APFS 복사본에서 수행했다. 새 직렬화 필드·MonoBehaviour·마이그레이션 코드나 Bake 형식 변경은 없다.

## Surface 수명 전이·재진입 보강과 잔여 상태 정리

확인용 6개를 `FlowFieldSurfaceLifecycleTests`로 옮긴 뒤 수정 전 Unity 실행에서 기존 184개 통과·추가 6개 실패를 확인했다. 이어 Retry·Ready·Dispose·중첩 Pump·동기 실패·늦은 완료 경계를 보강했다. 늦은 solver 쓰기는 단순히 게시 콜백을 무시해도 다음 최종 합성의 이동 방향을 바꾸는 사례로 재현했다. 테스트 소유자가 결과와 EventFailures를 검사하므로 이벤트 내부 assertion 실패가 누락되지 않는다.

- Building 알림 전 세대를 캡처하고, 알림 뒤 수명이 바뀌면 이전 BFS를 시작하지 않는다. Fault·Retry가 세대를 무효화하며 이전 완료·실패와 예외 처리는 새 시도에 영향을 주지 않는다.
- 초기화·Retry·Fault·게시의 내부 정리는 상태 알림 전에 끝낸다. 구독자가 넣은 최신 요청과 dirty 정보가 알림 뒤 지워지지 않으며, 수명이 바뀌면 이전 게시·실패 이벤트의 후속 전달을 중단한다.
- Pump와 비동기 완료 처리에 재진입 가드를 적용한다. 콜백이 Submit 후 Pump를 호출해도 새 요청은 다음 실행 경계까지 대기한다. Dispose는 Released 알림 전에 종료 상태로 바뀌며 공통 자원 정리만 Release와 공유한다.
- BFS 요청은 기존 Session 세대를 조회하는 유효성 판정을 전달한다. GPU 결과 적용과 Managed 재계산은 작업 배열을 쓰기 전에 이 판정을 확인한다. 실제 GPU 성공과 wave 상한 실패 경로에서 취소 후 배열·이벤트·Backend 전환 상태가 유지되는지 추가 검증했다. GPU 버퍼 소유권과 Readback 배출 규칙은 기존 Solver에 남긴다.
- 읽지 않는 `_runtimeSurface`, `_committedSurface`와 같은 값을 보관하던 `_committedSourceSurface`·`_committedSurfaceRevision`을 제거했다. Volume FieldData의 Grid는 고정 Request에서 조회한다.
- `MarkDirty`의 덮어쓰던 baseChange 인자와 `SurfacesEqual`의 미사용 grid 인자를 제거했다. 항상 true였던 내부 FieldCommitted 인자도 제거하고 Manager·Editor Bake·테스트 구독부를 함께 변경했다. 공개 이벤트와 Provider/Controller·Modifier 계약은 유지한다.

최종 Unity 2023.2.20f1/Metal 실행에서 기존 184개와 신규 21개, 총 205개가 통과했다. 실제 GPU 8개, 저장된 샘플 씬 통합 2개와 기존 Manager PlayMode 4개를 포함하며 실패·스킵·C# 오류·경고는 0건이다. 테스트 식별자를 대조해 기존 184개가 모두 남아 통과하는지 확인했다.

보존 대상 186개 중 185개는 해시가 동일하다. 나머지 Editor Bake 소스는 두 완료 콜백의 미사용 bool 인자만 제거했으며 정규화 대조로 본문 보존을 확인했다. 씬·Prefab·Bake payload·기존 meta/GUID·공개 계약·직렬화 설정과 Bake v5 형식은 동일하다. 새 런타임 클래스·인터페이스·직렬화 필드·마이그레이션 코드는 추가하지 않았다.

전후 XML·Unity 로그·단계별 소스 사본·해시와 검증 요약은 `ToyProject/Logs/FlowFieldSurfaceLifecycle/`에 보관한다. 독립 APFS 복사본에서 검증했으며, 동시에 변경 중인 다른 모듈의 소스는 수정하거나 되돌리지 않았다. UML에 표시된 타입·의존성은 동일하므로 기존 도표를 유지했다.

## Surface Modifier 수명 경계와 미사용 전달값 정리

확인용 6개를 정식 회귀 테스트로 먼저 옮겼다. 수정 전 Unity 실행에서 기존 205개는 모두 통과했고 추가 6개 중 Fault·Retry 경계 4개가 실패했다. SceneBuild·StaticBaked·BFS 완료 후 합성으로 확장한 66개 경계 테스트는 수정 전 28개가 실패했으며, 중단 후 이전 결과 발행과 다음 셀의 Modifier 실행을 재현했다.

- Session의 기존 콜백 세대를 Modifier 요청에도 전달한다. CaptureSnapshot·InfluenceCollider getter·Modify가 반환한 뒤 수명이 유효한지 검사하고, 취소되면 이후 레이어·셀·배열 기록·Commit을 중단한다. 새로운 공통 수명 타입이나 별도의 세대 카운터는 추가하지 않았다.
- Compose/ComposeRegion은 완료 여부를 반환한다. 이전에 사용하지 않던 RebuildFinalField의 bool 결과도 이제 취소 전파에 사용한다. 취소 뒤 반환한 null Snapshot·잘못된 벡터나 늦은 예외가 새 수명을 Fault로 만들지 않도록 소유권을 먼저 확인한다. 유효한 작업의 오류 처리는 유지한다.
- Retry·Suspend 후 재개·Release 후 재초기화에서 제출된 최신 요청을 보존한다. 콜백 안의 Pump는 기존 재진입 가드로 지연하고, 다음 Pump에서 최신 Goal/방향과 Modifier 결과를 한 번 발행한다. StaticBaked는 계속 BFS를 호출하지 않는다.
- FieldStore의 미사용 배열 접근자 8개와 IDisposable/Dispose 래퍼, ReadView의 미사용 Bounds·SpaceMode 조회·Revision·IsRuntimeFinal·Goal 메타데이터, Volume 표시 소스의 SessionGeneration, 표시 선택의 TotalCellCount·Stride 저장값과 전달 인자를 제거했다. 표시 좌표 계산과 결과/소스 식별은 유지한다.
- 사용처 없는 BfsRequest/ComputeRequest.Version과 생성자 전달값, 완료·실패 처리의 미사용 요청 인자, Registry 검증의 미사용 Modifier 인자와 Volume Pump의 단일 전달 계층을 제거했다. 실제 요청 버전·수명 세대·GPU 작업 ID·표시 ResultId는 유지한다.
- RebuildAreaData와 CommitFromWorkspace의 중복 bool 반환/out 채널을 반환값 하나로 정리했다. SurfaceReady는 불변 Surface의 유효성에서, ReadView.ResultId는 Source.ResultId에서 계산한다. 불변 Snapshot과 함께 중복 저장하던 _loadedStaticBakeRevision은 제거하고 참조 교체로 재로딩을 판단한다. 게시 Grid/Surface와 종료 플래그는 수명 역할이 달라 유지한다.

최종 Unity 2023.2.20f1/Metal 실행에서 기존 205개와 신규 77개, 총 282개가 통과했다. 신규 77개는 확인용 6개, 세 경로의 수명 전이 60개, 취소 뒤 유효하지 않은 Modify 반환값의 기록 차단 4개, 같은 Revision의 Snapshot 교체 1개, 읽기 뷰·표시 좌표 6개다. 실제 GPU 8개, 저장된 샘플 씬 통합 2개, Manager PlayMode 통합 4개를 포함하며 실패·스킵·C# 오류·경고는 0건이다.

Snapshot 회귀의 최초 기대값은 호환 소스 교체 시 Pump 전에도 기존 샘플을 숨긴다는 가정이었다. 실제 기존 계약은 호환 소스의 게시 샘플을 Pump 전까지 유지하므로 기대값을 바로잡았고, 같은 Revision의 다른 Snapshot이 Pump 후 변경된 장애물 데이터를 적용하고 동일 입력은 다시 발행하지 않는 것을 확인했다. 이 테스트 기대값을 맞추기 위한 생산 동작 변경은 하지 않았다.

보존 대상 186개 해시가 모두 동일하다. 씬·Prefab·Bake payload·기존 meta/GUID·asmdef·셰이더·공개 계약·Manager 직렬화 설정·Bake v5 형식은 유지했다. 새 런타임 타입·인터페이스·직렬화 필드·마이그레이션 코드나 에셋은 추가하지 않았다. 기존 테스트 식별자 205개는 최종 XML에 모두 남아 통과한다.

전후 XML·Unity 로그·단계별 소스 사본·해시와 검증 요약은 `ToyProject/Logs/FlowFieldSurfaceModifierLifecycle/`에 보관한다. 독립 APFS 복사본의 검증 소스를 현재 FlowField 소스와 대조했고, 검증용 복사본은 결과 기록 후 정리한다. 다른 모듈과 원본 씬은 수정하거나 저장하지 않았다. GUI 렌더링·Player/기기·장시간 군중 이동·성능 벤치마크는 이번 검증에 포함하지 않는다.

## Modifier 등록·관측 재진입과 Surface 베이크 버전 비교

확인용 7개를 `FlowFieldModifierObservationRegressionTests`에 정식 회귀로 추가했다. 수정 전 전체 실행은 기존 282개 모두 통과, 추가 7개 중 예상한 5개 실패였다. 등록·관측 getter의 Release/재초기화·Fault·Suspend·예외와 중첩 관측으로 확장한 65개 경계 테스트에서는 39개 실패를 재현했다.

- Surface Registry는 각 등록·관측 작업을 구별한다. Session의 Retry·Fault·Suspend·Release가 이전 관측을 무효화하며, 각 사용자 getter 직후 유효성을 확인한다. 재진입으로 취소된 등록은 추가 기록을 하지 않고, 취소된 Value/Priority 알림은 뒤따르는 Area 알림까지 중단한다.
- Volume Registry는 관측 작업 버전과 기존 Snapshot 캡처 버전을 구분한다. 변경 없는 관측은 유효한 빌드를 취소하지 않으며, 수명 변경·중첩 관측·등록 변경이 발생한 이전 호출은 캐시나 등록 목록을 덮어쓰지 않는다. 등록 메타데이터를 읽고 검증한 다음 목록에 추가한다.
- 두 Registry는 전체 관측을 임시 버퍼에 모은 뒤 검증·반영한다. 검증과 정렬에서는 관측한 Priority를 사용하므로 정렬 비교 함수가 사용자 getter를 재호출하지 않는다. 버퍼는 풀에서 빌리고 `finally`에서 반환한다. 취소된 getter의 늦은 예외만 무시하며 현재 작업의 예외는 그대로 전달한다.
- Surface 요청은 불변 Snapshot의 Revision과 요청 생성 시 캡처한 ContentGeneration으로 에셋 버전을 비교한다. 같은 가변 에셋의 현재 값을 양쪽에서 다시 읽던 비교를 제거했다. 재베이크 및 동일 Revision의 직렬화 재로딩을 모두 검증하며, 변경된 소스의 이전 샘플은 새 요청 제출 즉시 숨기고 새 필드를 한 번 발행한다. 변경 없는 요청과 기존의 호환 Snapshot 교체 계약은 유지한다.
- Manager의 `_callbacksAttached`는 `_attachedSession`으로 판단하도록 정리했다. Volume 전용 Attach/Detach 전달 래퍼와 중복 Detach 호출, Surface Modifier의 단일 전달 메서드 4개, Volume 변경 검증 전달 메서드를 제거했다. Volume BuildContext의 Count는 불변 요청 Grid에서 계산하고, 안전성 판정의 미사용 center 인자를 제거했다.

최종 Unity 2023.2.20f1/Metal 전체 검증은 기존 282개와 신규 81개, 총 **363/363 통과**다. 신규 81개는 확인용 7개, 등록·관측 수명 경계 58개, 동일 Revision의 에셋 재검증 1개, 현재 getter 예외 전달 12개, Priority 검증·정렬 2개, Manager 콜백 중복 연결 방지 1개다. 실제 GPU 8개, 샘플 씬 통합 2개, PlayMode에 진입하는 Manager 통합 5개를 포함하며 실패·스킵·C# 오류·경고는 0건이다. 수정 완료 후와 추가 정리 후의 전체 검증이 각각 통과했다.

기존 282개 테스트 식별자와 본문을 보존했다. 검증에 사용한 FlowField 컴파일 입력 84개가 현재 소스와 일치한다. 씬·Prefab·Bake payload·기존 meta/GUID·asmdef·셰이더·공개 계약 등 보존 대상 190개는 바이트 단위로 동일하고, Manager 직렬화 설정 및 Bake v5 형식도 유지했다. 새 런타임 타입·인터페이스·직렬화 필드·마이그레이션 코드나 에셋은 추가하지 않았다. 수명 안전성 검사로 전체 런타임 코드는 54줄 늘었고, 그 이후 중복 상태·전달 계층 정리에서는 33줄을 줄였다. 관측 버전과 실제 캡처/수명 버전은 역할이 달라 유지한다.

전후 XML·Unity 로그·단계별 소스 사본·해시·작업 범위 diff와 `verification.json`은 `ToyProject/Logs/FlowFieldModifierObservationFix/`에 보관한다. 중간 테스트 fixture의 잘못된 Bake payload 및 컴파일 지역 변수 이름 충돌도 로그에 남겼으며, 최종 검증은 이를 바로잡은 현재 소스로 수행했다. 독립 검증 프로젝트는 기록 후 제거한다. 원본 씬·저장된 베이크와 다른 모듈은 수정하지 않았고 GUI 렌더링·Player/기기·장시간 군중 이동·성능 측정은 수행하지 않았다.

## Surface 처리 예약·Manager 소유권·소스 생성 경계

확인용 9개를 `FlowFieldSchedulingOwnershipRegressionTests`에 정식 회귀로 추가했다. 수정 전 전체 Unity 실행은 기존 363개 모두 통과, 추가 9개 모두 실패였다. 초기 45개 경계 테스트에서는 33개 실패를 재현했다.

- Surface의 dirty 변경은 유효한 최신 요청이 있을 때 동일한 pending 플래그로 처리 예약을 한다. 첫 Submit 전에는 실행하지 않는다. Register·Notify·Unregister·자동 관측 뒤 추가 Submit 없이 다음 소유 실행 경계에서 합성한다. Manager FixedUpdate는 현재 Goal·기본 방향 등 정규화된 입력을 제출한 뒤 Pump하여 이전 요청의 스칼라 값이 다시 사용되지 않게 한다.
- Manager의 실행 분기는 설정 필드 대신 수락한 Session의 SpaceMode를 따른다. Faulted 상태에서도 같은 Session의 진단을 유지한다. 콜백 연결은 대상 Session을 명시해 이전 연결에서 새 연결로 옮긴다. Release 알림에서 다른 모드나 같은 Session을 재초기화할 수 있으므로, 알림 뒤에도 여전히 Released인 이전 소유자만 연결을 해제한다.
- Surface 영향 마스크 getter 전후에 기존 Session 수명 세대와 Registry 작업 버전을 확인한다. 재진입으로 등록·관측 목록이 바뀌면 해제된 캐시를 쓰거나 이전 마스크를 발행하지 않고 다음 경계로 재예약한다. 영역 처리의 bool은 완료/취소, changedRegion은 실제 마스크 변경을 나타낸다. 마지막 Modifier 해제처럼 변경 영역이 없어도 초기 필드 합성이 필요하면 생략하지 않는다.
- 완료된 BFS의 결과는 다시 풀지 않고 재합성한다. 마스크 처리에서 BFS 시작 전에 취소됐다면, 준비만 끝난 Goal을 초기화해 필요한 첫 solve를 재예약한다. SceneBuild·StaticBaked·BFS 시작 전·완료 후 경로에서 이전 발행 차단과 최신 입력의 단일 발행을 검증한다.
- SurfaceSource.Build 반환 직후, payload 검증과 공유 상태 기록보다 먼저 수명 세대 및 요청 버전을 확인한다. Release/재초기화·Suspend/재개·Fault/Retry·Dispose와 취소된 null/잘못된 Surface·늦은 예외를 검증했다. 같은 수명에서 새 방향·Goal·Grid를 Submit한 경우도 이전 소스 처리가 새 요청 플래그를 소비하지 않는다. 현재 작업의 실제 오류는 기존대로 한 번 Fault 처리한다.
- 미사용 Surface Registry.Contains, RegistryResult의 3인자 전달 생성자, SurfaceName 및 소스 이름 전달값, SessionState.Release 전달 메서드를 제거했다. Modifier의 Fault 여부는 저장된 예외에서 계산한다. Priority 중복 거부·순서 회귀를 확인하고 미사용 동점 처리용 RegistrationOrder와 카운터를 제거했다. 공통 수명 클래스나 인터페이스는 추가하지 않았다.

최종 **Unity 2023.2.20f1/Metal에서 426/426 통과**했다. 기존 363개, 확인용 9개와 추가 경계 테스트 54개이며 실패·스킵·C# 오류·경고는 0건이다. 실제 GPU 8개, 저장된 샘플 씬 통합 2개, PlayMode에 진입하는 Manager 통합 15개를 포함한다. 기존 363개 식별자는 모두 유지하며 기존 테스트 변경은 미사용 이름 인자 제거뿐이다. 검증한 FlowField 컴파일 입력 85개가 현재 소스와 일치한다.

씬·Prefab·Bake payload·기존 meta/GUID·asmdef·셰이더·공개 계약 등 보존 대상 190개는 바이트 단위로 동일하다. 별도로 보호하던 Surface 베이크 Editor 어댑터 1개는 미사용 이름 인자 한 개만 제거했으며, 이를 정규화한 전체 소스가 이전과 동일함을 확인했다. Manager 직렬화 설정과 Bake v5 형식은 유지했다. 새 런타임 타입·인터페이스·직렬화 필드·마이그레이션 코드나 에셋은 추가하지 않았다.

중간 검증에서는 Unity가 PlayMode 종료 중 네이티브 에셋 정리 대기에 멈춘 사례가 한 번 있었다. 프로세스 샘플을 기록하고 해당 독립 검증 Editor만 종료해 다시 실행했다. 이후 전체 420개 검증과 정리 후 전체 420개 검증이 통과했으며, 같은 수명 내 소스 Submit 경계 6개를 실패 재현한 뒤 보강한 최종 426개 실행도 통과했다. 중단된 실행은 성공 증거에 포함하지 않는다.

전후 XML·Unity 로그·소스 사본·해시·작업 범위 diff와 `verification.json`은 `ToyProject/Logs/FlowFieldSchedulingOwnershipFix/`에 보관한다. 원본 씬 전환·저장이나 영구 베이크 재생성은 수행하지 않았고 다른 모듈은 수정하지 않았다. 독립 검증 프로젝트는 기록 후 정리한다. GUI 렌더링·실제 포인터 입력·Player/기기·장시간 군중 이동·성능 측정은 이번 검증 범위에 포함하지 않는다.

## 게시 샘플·Goal 메타데이터 일원화와 초기화 수명 경계

확인용 10개를 `FlowFieldPublishedOwnershipRegressionTests`에 정식 회귀로 추가했다. 수정 전 Unity 전체 실행은 기존 426개와 제어 사례 2개 통과, 확인용 실패 8개였다. 초기 확장 18개 경계 테스트에서는 15개 실패를 재현했다.

- Surface의 일반 Sample은 항상 FieldStore의 게시 배열·Grid·Surface에서 읽는다. 동기 final 합성 중 Modifier가 재진입 조회해도 reset된 staging 속도나 부분 합성을 노출하지 않는다. 최신 장애물·탈출 방향·경로 차단 판정은 기존 staging 안전성 overlay로 유지한다.
- 해결된 Goal의 존재 여부와 셀은 FieldStore의 HasActiveGoal·ResolvedGoalIndex에서 조회한다. BFS 대기 중 이전 결과의 Revision에 새 Goal의 해결 위치가 섞이지 않으며 StaticBaked도 베이크된 Goal을 정확히 보고한다. RequestedGoal은 기존대로 최신 수락 입력을 설명한다. Goal 제거 중에도 Commit 전까지 해결된 Goal 메타데이터는 이전 게시 결과를 따른다.
- 게시 Grid·Surface 중복 필드를 제거하고 FieldStore를 단일 소유자로 삼았다. 동일한 cell 데이터라도 명시적 Surface 전체 재빌드는 새 불변 Surface 참조·Revision을 반영할 수 있으며, 데이터가 같으면 결과 Revision을 올리지 않는다. 호환 Snapshot만 바꾸는 경우 기존 geometry를 재사용하는 계약은 유지한다.
- Manager는 초기화·재활성화·Release·Disable·Dispose 시도에 대한 비직렬화 수명 버전을 사용한다. 콜백 뒤 다른 시도가 소유권을 가져갔으면 이전 작업의 재빌드·Modifier 재연결·스케줄러 등록을 중단한다. Volume의 기존 build generation은 정상 Commit에서도 변하므로 재사용하지 않았다. 콜백 안에서 정당한 Volume Pump가 완료돼도 초기화가 잘못 취소되지 않는 제어 회귀를 추가했다.
- Volume 스케줄러 등록은 유효한 현재 재빌드 소유자에 대해서만 한다. 초기 Fault 뒤 명시적 RequestRebuild도 등록을 복구한다. Surface Fault 재시도에서는 게시 stale 상태와 미제출 설정을 구분한다. RequiresExplicitRebuild는 기존 versioned Grid flags에서 계산하며, 검증된 Submit을 처리할 수 있게 하면서 오래된 게시 결과는 Commit 전까지 숨긴다. 추가 저장 플래그는 만들지 않았다.
- Suspended Volume은 최신 요청을 계속 보관하지만 runnable 작업으로 보고하지 않는다. 정지 작업의 반복 호출을 막고, 함께 등록된 실행 가능 작업의 완료와 Resume 후 보관 요청의 발행을 검증했다.
- 중복 일반 셀 샘플러·GoalTracker의 게시 전용 접근자·도달 불가능한 Surface OnEnable Fault 분기를 제거했다. Surface 전용 캐시 클래스를 Entry에 통합하되 마스크·인덱스 목록의 지연 할당과 해제 경계를 유지했다. FixedSurfaceSource의 테스트용 BuildCount는 테스트 fixture의 counting source로 옮겼다. 런타임 타입 1개를 줄였으며 새로운 공통 요청·상태 계층이나 인터페이스는 추가하지 않았다.

최종 **Unity 2023.2.20f1/Metal 전체 445/445 통과**다. 기존 426개, 확인용 10개와 추가 회귀 9개이며 실패·스킵·C# 오류·경고는 0건이다. 실제 GPU 8개, 저장된 샘플 씬 통합 2개, PlayMode에 진입하는 Manager 통합 20개를 포함한다. 새 Manager 통합 4개는 Surface/Volume의 초기화·재활성화에서 Release·동일 모드 재시작·모드 교체·Disable·Fault·Dispose의 24개 전이를 확인하고, Disable 후 재활성화와 Fault 후 명시적 재시도까지 단일 발행을 확인한다. 나머지 새 통합 1개는 정상 inline Volume Pump 제어 사례다.

기존 426개 식별자와 테스트 본문을 보존했다. 기존 테스트 쪽 변경은 counting source 도우미 이동과 이를 받는 fixture 필드 타입뿐이다. 검증한 FlowField 컴파일 입력 86개가 현재 소스와 일치한다. 씬·Prefab·Bake payload·기존 meta/GUID·asmdef·셰이더·공개 계약 등 보존 대상 192개가 모두 바이트 단위로 동일하다. Manager 직렬화 설정과 Bake v5 형식도 유지했다. 새 직렬화 필드·마이그레이션 코드·에셋은 추가하지 않았다. 전체 런타임 코드는 57줄, 중복 상태·캐시 정리 단계에서는 80줄 줄었다.

동일 내용 Snapshot 회귀의 최초 기대값은 언제나 새 geometry 참조를 게시한다는 가정이었다. 실제 계약은 호환 geometry를 재사용할 수 있으며, Snapshot 생성자는 Surface 자체를 복제하지 않고 전달된 참조를 그대로 보관한다. 이에 맞춰 일반 Snapshot 교체와 명시적 Surface 재빌드의 기대값을 각각 검증했다. 이 테스트를 맞추기 위해 geometry를 강제로 복사하거나 동일 cell 데이터의 발행을 늘리지 않았다.

중간 전체 실행은 Unity 네이티브 Enlighten/Mono 오류로 한 번 종료됐다. 같은 로그에 저장 공간 경고도 있었으나 인과관계는 단정하지 않는다. 원본은 유지하고 검증 복사본에서만 참조되지 않는 다른 모듈을 잠시 제외했다. 이후 공간 확보 뒤 전체 프로젝트 구성을 복원한 최종 실행에서도 445개가 통과했다. 중단되거나 실패한 중간 실행은 성공 증거에 포함하지 않는다.

XML·Unity 로그·전후 소스 사본·해시·작업 범위 diff와 `verification.json`은 `ToyProject/Logs/FlowFieldPublishedOwnershipFix/`에 보관한다. 원본 씬 전환·저장이나 영구 베이크 재생성은 수행하지 않았고 다른 모듈은 수정하지 않았다. 검증 복사본은 기록 후 정리한다. 실제 포인터·GUI 렌더링·Player/기기·장시간 군중 이동·성능 측정은 이번 검증 범위에 포함하지 않는다.

## 현재 구조 문서 갱신과 구버전 지원 잔여 검사

README에 사용 방법, 클래스·인터페이스별 역할, 실행 경계, 게시 샘플·Goal 메타데이터의 소유권, 재진입·재활성화·Fault 재시도와 검증 범위를 반영했다. 기술 참고의 Surface 등록 순서와 Volume Snapshot 캡처 주체에 관한 오래된 설명을 수정했다. 구조도는 현재 30개 핵심 타입의 관계로 갱신하고, 제거된 FieldStore.Dispose 등 오래된 멤버 표시를 없앴다. 세부 책임은 README 표에서 확인할 수 있다.

FlowField의 Runtime·Editor·Samples·Tests와 저장된 씬·Prefab·Settings를 검사했다. 삭제할 잔여 마이그레이션 실행 코드, 구버전 API 래퍼, 직렬화 호환 별칭·이름 이동 속성, 버전별 Unity 분기나 구형 베이크·변환 전용 에셋은 발견하지 못했다. RuntimeDynamic의 0 값은 현재 enum 값 그대로 유지하면서 구버전 Manager 보존을 설명하던 주석과 미사용 import만 제거했다. 실행문·공개 API·직렬화 형식은 변경하지 않았다.

검색에 걸리는 HasCompatibleCommittedSource는 현재 재계산 중 읽을 수 있는 게시 결과를 판정하는 함수다. LegacyRuntime.ttf는 Unity 2023의 기본 UGUI 폰트 참조이며 AssetPostprocessor의 movedFromAssetPaths는 실제 에셋 이동 알림 인자다. 이들은 구버전 호환 어댑터가 아니다. GPU 실패의 Managed 대체 계산, v5 이외 베이크 입력 거부와 작업 수명 버전 검사도 현재 실행·안전성 계약이므로 보존했다. 과거 리팩터링의 실패 재현·보존 기록은 Assets 밖 Logs에 남기며 컴파일·샘플 에셋에 포함하지 않는다.

독립 프로젝트 복사본의 **Unity 2023.2.20f1/Metal에서 445/445 통과**했다. 기존 테스트 식별자와 본문을 유지했고 추가 테스트는 만들지 않았다. 실패·스킵·C# 오류·경고는 0건이며 실제 GPU 8개, 저장된 샘플 씬 통합 2개, PlayMode에 진입하는 Manager 통합 20개를 포함한다. 씬·Prefab·베이크 payload·기존 meta/GUID·asmdef·셰이더와 Manager 설정은 바이트 단위로 동일하다. 검증한 FlowField 컴파일 입력 86개가 현재 소스와 일치한다.

README·기술 참고·변경 기록·PlantUML·렌더 PNG와 계약 파일의 주석/import만 수정했다. 문서 링크·이미지 경로, 30개 구조도 타입의 실제 정의, 기존 meta 및 GUI 자산 참조를 점검했다. XML·Unity 로그·전후 소스 해시·잔여 검색 판정과 검증 요약은 `ToyProject/Logs/FlowFieldDocumentationCleanup/`에 보관한다. 원본 Editor의 씬 저장·영구 ReBake·다른 모듈 변경은 수행하지 않았다. 실제 포인터·GUI 렌더링·Player/기기·장시간 이동·성능 검증은 포함하지 않는다.

## 재실행

Unity에서 `Window > General > Test Runner`를 열고 EditMode의 `Common.FlowField.Tests`를 실행한다.

명령행 검증은 Editor에 열려 있지 않은 프로젝트 복사본을 대상으로 한다. 실제 GPU 검증을 위해 `-nographics`를 생략한다.

```sh
"/Applications/Unity/Hub/Editor/2023.2.20f1/Unity.app/Contents/MacOS/Unity" \
  -batchmode \
  -projectPath "/absolute/path/to/validation/ToyProject" \
  -runTests -testPlatform EditMode -testFilter Common.FlowField.Tests \
  -testResults "/absolute/path/to/results.xml" \
  -logFile "/absolute/path/to/unity-tests.log"
```

현재 작업의 단계별 XML·Unity 로그·보존 검사 요약은 Git에서 제외되는 `ToyProject/Logs/FlowFieldRefactoring/`에 보관했다. Volume 안전성 단계의 전후 XML·로그·보존 해시·검증 요약은 `ToyProject/Logs/FlowFieldVolumeSafety/`에 보관했다. 장애물 관측 단계의 의도적 실패·기준선 66개·최종 68개 XML과 로그, 보존 해시·검증 요약은 `ToyProject/Logs/FlowFieldObstacleObservation/`에 보관했다. 이 경로의 보고서는 새 체크아웃에서 재실행하면 다시 생성해야 한다.

관측 재진입 수정의 전후 XML·로그·소스 차이·보존 해시·검증 요약은 `ToyProject/Logs/FlowFieldLifecycleFix/`에 보관했다. 저장 공간 정리로 사라진 임시 검증 기록은 검토 당시 소스 해시와 일치하는 기준선을 복구해 실패 재현으로 다시 확보했다. 검증용 프로젝트 복사본은 결과 기록 후 정리한다.

Modifier 재진입 수정의 전후 XML·로그·소스 차이·보존 해시·검증 요약은 `ToyProject/Logs/FlowFieldModifierReentrancy/`에 보관했다. 실패 주입이나 새 회귀의 실패가 포함된 수정 전 기록과 최종 통과 기록은 별도로 유지한다.

구조 단순화의 기준선·미사용 계층 제거·최종 XML과 로그, 소스 차이·보존 해시·검증 요약은 `ToyProject/Logs/FlowFieldSimplification/`에 보관했다. 삭제된 파일의 사본은 Assets 밖의 이 검증 기록에만 남으며 Unity에서 컴파일하지 않는다.

성능 수치, 장시간 군중 이동, 기기별 동작과 Player 빌드는 이번 검증 범위에 포함하지 않는다.

## 성능·구조 개선 (2026-10-29~30)

검증 환경: Unity 2023.2.20f1 / Windows 11 / Direct3D 11 (AMD Radeon RX 9070). 기준선은 같은 환경에서 기존 테스트 449개(성능 프로브 7개 제외) 통과로 시작했다.

| 단계 | 변경 | 검증 |
| --- | --- | --- |
| 0. 안전망 | 고정 장면의 전체 셀 샘플 골든 해시(Managed 고정), 성능 프로브(`FLOWFIELD_PERF=1`일 때만 실행) | 449 통과, 기준 수치 기록 |
| 1. 정리 | 미사용 멤버 3개, Overlap 버퍼 확장·FieldStore 복사·Grid 레이아웃·마스크 인자 검증 중복, 샘플 `IsFinite`·`Approximately`·`FindGoalIndex` 중복, Scheduler·Pump·이벤트 클로저 할당 제거 | 448 통과, 골든 일치 |
| GPU 수정 | Readback 요청 직후 `GL.Flush`. 배치 모드 D3D11에서 Readback 한 번이 수백 프레임 지연되어 샘플 씬 Goal 재빌드가 20~60초 이상 걸리던 문제 | 장면 테스트 27~31초(30초 제한 경계 실패) → 2초, 449 통과 |
| 2. Modifier 규칙 | Surface·Volume 공통 `CanApplyModifier`(막힘·Anchor·Unreachable·기본 방향 0이면 미적용), Surface 기본 방향 0이면 속도 0 | Surface 골든만 의도대로 변경(Anchor 속도 1→0), 457 통과 |
| 3. Volume 셀 쿼리 | 스텝 단위 Physics 후보 필터(`FlowFieldVolumeCellQuery`), 내부 셀 Topology 오프셋, 단계 루프의 Grid 복사 제거 | 후보 필터·Topology 동등성 테스트, 461 통과 |
| 4. Surface 합성 | BFS가 예정된 빌드의 사전 `ComposeFinal` 생략 | 이전 코드 9회 호출(실패) → 0회, 462 통과 |
| 5. Manager | Session 공통 수명 명령(Suspend·Release·RetryFault·ReportFault), RequestRebuild·Release·OnDisable 중복 정리, 설정 검증 분리 | 462 통과 |
| 6. 베이크 v6 | Volume 방향·속도·탈출 벡터를 NextCell·면 인덱스에서 재구성, Surface float·Vector3 배열을 byte[]로 저장. 두 Asset을 같은 GUID로 다시 베이크 | 내용 해시가 v5와 동일, 왕복 테스트, 464 통과 |
| 7. 배열 재사용 | 커밋된 빌드의 Costs·Queue·Influence와 안전성 Snapshot 배열 재사용 | A→B→A 재빌드 결과 동일, 467 통과 |
| 레거시 정리 | 샘플의 결과를 읽지 않던 겹침 진단·자동 Goal 토글·기록 전용 필드와 미사용 공개 속성 17개 제거 | 466 통과(임시 진단 제외), 직렬화 무결성 통과 |

Volume 100^3(장애물 40개) 단계별 시간에서 Topology가 17.8초로 전체의 70%였다. 경계가 아닌 셀의 이웃·코너 검사를 평탄 인덱스 오프셋과 Blocked 조회로 바꿔 1.5초가 되었다. 전체 빌드는 26.1/32.3/55.4초(등록 장애물 0/10/50)에서 5.8/5.9/6.4초, 2ms 예산 빌드는 12,585에서 2,214프레임, 장애물 1개 이동 후 재빌드는 31.3에서 6.0초가 되었다.

계획에서 측정 후 결정한 항목과 중단한 항목(다시 볼 조건은 [StatusAndBacklog.md](StatusAndBacklog.md) 참고):

- **Surface 버전·지연 플래그 제거(4단계 일부) 중단**: 버전 값은 오래된 BFS 결과 판정 외에도 Surface Source 재진입 감지, BFS 전 취소된 Goal 재시도(빌드 밖에서도 사용), 설정 무효화 후 명시적 재빌드 게이트에 쓰인다. 계획의 중단 조건에 따라 유지했다.
- **게시 배열 재사용(7b) 보류**: Volume 빌드 중 GC가 0회였고, 재사용하면 상주 메모리가 약 50MB 늘어난다.
- **증분 장애물 갱신(8) 보류**: 3단계 후 장애물 단계는 약 0.46초로 전체 약 6초의 8%다.
- **GPU 재시도 정책(9) 보류**: 선택 항목이며, 관찰된 GPU 지연은 `GL.Flush` 수정으로 해결했다.
- **샘플 직렬화 필드의 기반 클래스 이동 취소**: 기반 클래스는 Agent 수명만 소유한다는 설계 의도가 있고, `_spawnSpacing` 타입과 기본값이 샘플마다 달라 중복 메서드만 옮겼다.

베이크 v6 전환은 마이그레이션 코드 없이 진행했다. 현재 코드로 v5를 다시 베이크하면 Revision 한 줄 외에 내용이 같음을 먼저 확인했고, v6 코드로 다시 베이크한 Asset의 내용 해시가 v5와 같음을 `FlowFieldBakeAssetContentTests`로 확인했다. Volume Asset은 453,863에서 106,568바이트, Surface Asset은 1,286,861에서 981,158바이트가 되었다.

Windows 배치 실행 예:

```powershell
& "C:\Program Files\Unity\Hub\Editor\2023.2.20f1\Editor\Unity.exe" -batchmode -projectPath <ToyProject> `
  -runTests -testPlatform EditMode -assemblyNames Common.FlowField.Tests `
  -testResults <results.xml> -logFile <unity.log>
$env:FLOWFIELD_PERF = "1"   # 성능 프로브: 위 명령에 -testCategory Performance 추가
```

단계별 XML·로그와 프로브 CSV는 Git에서 제외되는 `ToyProject/Logs/FlowFieldImprovement/`, `ToyProject/Logs/FlowFieldPerformance/`에 보관했다.

## 이동 장애물 결함과 유지보수 정리 (2026-10-31)

검증 환경: Unity 2023.2.20f1 / macOS / Metal(Apple M4 Pro). 커밋별 내용은 [StatusAndBacklog.md](StatusAndBacklog.md#유지보수-작업-2026-10-31)에 있다.

- **낡은 BFS 결과 게시**: 이전에는 장애물 관측이 하나라도 있으면 BFS 결과를 버렸다. Goal Dirty는 이미 소비되고 Goal Tracker는 새 Goal을 기록한 뒤라, 마스크가 그대로면 Goal 변경이 사라졌고 풀이 때마다 움직이면 게시가 멈췄다. 샘플링이 최신 장애물을 다시 검사하므로 장애물만 낡은 결과는 게시한다. 이 경로를 지나는 테스트가 없어 드러나지 않았다.
- **불확실 영역 분리**: 세대 번호만 붙이면 겹치는 영역이 합쳐지며 계속 최신 세대가 되어 지워지지 않는다. 빌드 시작 때 목록을 옮기는 방식을 택했다. 폐기된 빌드의 목록은 다음 빌드가 게시될 때 함께 지운다. 모든 빌드가 장애물을 처음부터 다시 조회하므로 안전하다.
- **GPU 전환**: 일시적 충돌과 재시도해도 반복될 실패를 나눴다. 전환 이유는 공개 인터페이스에 넣지 않고 Manager 속성으로만 제공한다.
- **Driver**: `HideAndDontSave` 객체는 Play 종료 후 남고 `FindObjectsByType`에 보이지 않아, 도메인 리로드 뒤 Play마다 Driver가 늘고 `PumpAll`이 프레임당 여러 번 돌았다. Editor에서만 생기는 문제이며 Editor 측정값에 영향을 줬을 수 있다.
- **움직이는 Goal**: Goal이 풀이 시간보다 자주 셀을 옮기면 결과가 계속 거부되어 게시가 멈췄다(프로브에서 풀이 30회, 게시 0회). 낡은 Goal을 게시하지 않는다는 기존 계약은 일회성 변경에서 유지하고, 연속으로 밀린 풀이만 게시한다. 이때 해석된 Goal은 요청 Goal보다 잠시 뒤처진다. Surface 샘플에서 Goal을 매 Editor 틱 움직이는 동안 게시가 이어지는 것을 확인했다.
- **Surface BFS**: 프레임 분할이나 Burst 전환 전에 낭비부터 없앴다. 재빌드가 425ms에서 205ms로 줄어 분할은 하지 않았다.
- **Volume Modifier 재합성**: 전체 빌드가 함께 갱신하던 미추적 Collider의 반영 시점이 달라진다. README가 이미 미등록·스윕 OFF 장애물을 추적 대상이 아니라고 설명하고 Surface도 같게 동작하므로 재합성을 택했다.
- **Volume Goal 재사용**: 처음에는 같은 입력의 재제출을 명시적 재빌드로 추측했다. Goal Transform이 움직이면 요청이 달라져 `NotifyCellsDirty()`와 `RequestRebuild()`가 재조회를 건너뛰고, Modifier의 내부 재제출은 재빌드로 잘못 판정됐다. 호출자가 `requeryObstacles`를 직접 전달하게 고쳤다. `SetGoal()`만 재조회를 요청하지 않는다.
- **연속 Goal 거부**: 표시를 게시 경로에서만 지워서 재시도가 실패하거나 Grid 변경으로 거부되면 남았다. 풀이가 끝날 때마다 한곳에서 갱신한다.
- **안전 스냅샷 제거**: Blocked는 같은 커밋으로 게시되는 필드와, Unknown은 다음 게시까지 남는 불확실 영역 목록과 값이 같았다. 앞서 영역 근처 셀만 검사하도록 줄인 비용도 이 제거로 사라졌다.
- **Burst 미전환**: Burst 동작은 Player에서 확인해야 하는데 Player 검증이 범위 밖이라 진행하지 않았다. 2026-11-01 계획에서 Player 검증을 재개해 전환했다.
- **베이크 에디터 검증**: Surface·Volume 모두 다시 베이크하면 `_revision`만 달라지므로, 정리 전후 Asset을 바이트로 비교했다. 씬과 Asset은 원복했다.
- **샌드박스**: 배치 실행은 Unity 라이선스 소켓 때문에 Claude Code 샌드박스 밖에서 실행해야 한다. 별도 worktree에서 실행하면 열려 있는 Editor와 충돌하지 않는다.

## 남은 작업 일괄 정리 (2026-11-01)

검증 환경: Unity 2023.2.20f1 / macOS / Metal(Apple M4 Pro), Mono arm64 Mac Player. PR별 내용은 [StatusAndBacklog.md](StatusAndBacklog.md#2026-11-01-계획), 최종 수치는 [같은 문서의 Player 기준선](StatusAndBacklog.md#player-기준선-2026-11-01)에 있다. Windows 전용 작업을 뺀 나머지를 한 계획으로 묶었고, 구버전 호환과 공개 계약 유지는 요구하지 않았다.

이 계획으로 위 절의 "Burst 미전환"과 현황 문서의 Player 빌드 검증, 게시 배열 재사용, 증분 장애물 갱신 보류는 뒤집혔다.

- **측정부터**: Player 수치 없이 Burst와 GPU를 판단할 수 없어 측정 도구를 먼저 만들었다. 이 측정에서 `isStatic`이 Player에서 항상 false라 Surface가 벽을 무시하던 결함이 나왔다. 정적 장애물 규칙을 Rigidbody 없이 등록하지 않은 Collider로 바꿨다.
- **pre-push 훅**: 저장소가 공개라 외부 fork가 `pull_request` 워크플로를 수정할 수 있다. Unity 라이선스가 있는 PC에 자체 호스트 러너를 두지 않고 저장소 안의 훅으로 검사한다. 켜지 않은 PC와 `--no-verify`는 막지 못한다.
- **사용자 코드 경계**: 복잡성의 근원은 빌드 도중 사용자 코드가 수명을 바꾸는 경우였다. 이벤트를 큐에 모아 작업 뒤에 전달하고 Modifier 안의 수명 명령을 거부해, 소유권 확인을 115줄에서 13줄로 줄였다. 남은 13줄은 GPU 콜백용이었고 GPU 경로와 함께 지웠다. 도달할 수 없게 된 재진입 테스트 케이스 224개를 지우고 거부·지연·순서 케이스 73개를 넣었다.
- **GPU 제거**: 기준은 Player에서 Burst의 최대 프레임 시간이 GPU보다 나쁘지 않고 총 빌드 시간이 1.1배 이내일 것이었다. GPU는 Volume 빌드의 19%인 BFS만 대체하므로 유지 비용을 정당화해야 했다. 같은 실행의 Goal 변경이 Surface 17~23ms 대 274~314ms, Volume 0.65초 대 3.9초여서 지웠다. **이 판정은 이 Mac 하나의 결과다.** 계획에 있던 GPU 재시도와 GPU·Managed 합성 비교는 해당 없어졌다.
- **끝난 풀이 게시**: 버전과 지연 플래그로 판정하던 `FlowFieldSurfaceRebuildTracker`를 지우고, Grid가 바뀌거나 Goal이 사라진 경우 외에는 게시한다. 새 Goal이 들어와 있어도 이전 Goal의 풀이가 잠깐 게시될 수 있지만 필드가 멈추는 것보다 낫다고 판단했다.
- **Physics는 나누지 않음**: 표면과 장애물 조회는 한 스냅샷이어야 해서 한 프레임에 실행한다. Surface 첫 빌드 115~120ms와 장애물 이동 12~15ms는 이 결정의 비용이다.
- **세션 분리의 한계**: `.Obstacles` 등 partial 파일은 필드를 공유하므로 읽기 쉬워졌을 뿐 결합이 줄지는 않았다. Surface 빌드 단계만 클래스로 나눴다.
- **증분 장애물의 동작 변경**: 추적되지 않은 Collider는 다음 장애물 변경 때 함께 반영되지 않고, `RequestRebuild()`나 같은 요청의 재제출로 전체 조회할 때 반영된다. 증분과 전체 빌드의 해시가 같은지는 이동·등록·해제·파괴 4가지로 검사한다.
- **게시 배열 재사용**: 기준은 GC 스파이크가 프레임 예산을 넘을 때만이었다. 움직이는 Volume Goal에서 GC 프레임이 28ms로 넘어 적용했고, 10초 GC가 330회에서 0회로 줄었다. 놀고 있을 때 빌드 하나 분량의 메모리를 더 잡는다. 뷰로 내준 필드의 배열은 재사용하지 않는다.
- **2ms 목표 미달**: Surface Goal 변경 2.4~3.7ms와 Volume 2.3~5.1ms는 목표를 조금 넘는다. Goal 변경 때 프레임이 17ms 안팎이라 더 나누지 않았다.
- **확인하지 못한 것**: Windows·Direct3D 11 테스트, IL2CPP 실행, 다른 하드웨어의 GPU 판단, 우클릭을 유지한 RMB+WASD 조작, 개편 후 장시간 군중 이동.

## 계획 뒤 리뷰 수정 (2026-11-01)

PR #26~#37을 다시 검토해 찾은 결함이다. 수정 전 코드에서 재현 테스트가 실패하는 것을 먼저 확인했다.

- **제출 확인**: 4단계에서 빌드를 Scheduler로 옮기면서 Manager의 사전 검사가 함께 옮겨지지 않았다. Scheduler가 이전 요청으로 빌드해, stale 설정이 `RequestRebuild` 없이 풀리고 설정 변경은 적용되지 않았다. Play 모드에서 재현했다. 풀이 중 바뀐 Goal의 Dirty 표시도 이전 요청의 빌드가 지워 다음 감지 주기까지 반영이 늦어졌다. stale 검사만 추가하지 않고 "Manager가 제출한 요청만 빌드한다"는 규칙으로 고쳐, 설정이 잘못된 경우도 함께 막았다. 일시적인 오류는 값이 돌아오면 다음 제출로 복구된다. 대기 중인 작업이 프레임 예산을 쓰지 않도록 대기 상태로 보고한다.
- **합성 중 입력 변경**: 합성을 프레임에 나눈 뒤로는 끝난 풀이의 거부 규칙이 합성에 적용되지 않았다. 입력은 프레임 사이에만 바뀌므로 합성을 이어 가기 직전에 검사한다. Goal 없는 요청은 `GoalRemoved`가 계속 true라서, 활성 Goal이 있는 작업에만 Goal 제거 규칙을 적용한다.
- **실패 로그**: 처음에는 `Failed` 이벤트를 세대 검사에서 빼려 했다. 그러나 재시작한 수명에 이전 실패를 알리지 않는 것은 기존 테스트가 지키는 규칙이다. 이벤트는 그대로 두고 Manager가 Faulted 상태를 받을 때 로그를 남긴다.
- **뷰 배열**: 뷰로 내준 필드의 `Blocked`를 공유하는 다음 필드가 뷰 표시 없이 교체되면 배열이 재사용되었다. 공유하는 필드에 표시를 넘긴다.
- **등록 명령**: Modifier 코드 안의 등록은 접수만 하므로 `true`를 반환한다. 처리 전에 Session이 멈추면 버려지는데, 조용히 사라지지 않게 경고를 남긴다.
- **탈출 방향 공유**: 탈출 방향은 `Blocked`로만 정해지므로, 장애물 결과를 재사용하는 빌드는 `Topology`처럼 공유하고 다시 계산하지 않는다. 측정이 실행마다 최대 2배 흔들려 개선폭은 단정하지 않았다. 2회 측정에서 Volume Goal 변경 104~132ms, 이동 Goal 초당 9.4~9.5회로 나빠지지 않았다.
- **풀이 실패 경로 제거**: Burst 풀이는 실패를 보고하지 않는다. `failed` 콜백과 `OnBfsFailed`는 주입 백엔드 테스트에서만 쓰여 함께 지웠다. 백엔드가 시작을 거부하는 경로는 유지한다.
- **측정 어셈블리**: `Common.FlowField.PlayerProbe`는 `UNITY_EDITOR || FLOWFIELD_PLAYER_PROBE`일 때만 컴파일한다. 측정 빌드만 define을 넘긴다.
