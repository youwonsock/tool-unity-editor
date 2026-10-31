# FlowField 작업 현황과 남은 과제

기준: 2026-10-31, 코드 커밋 `5cdde7f`. 단계별 결정과 근거는 [RefactoringNotes.md](RefactoringNotes.md#성능구조-개선-2026-10-2930), 사용법과 측정 표는 [README.md](../README.md#검증-기록)에 있다. 이 문서는 지금 상태와 다음에 할 일만 다룬다.

## 현재 상태

| 항목 | 상태 |
| --- | --- |
| 테스트 | macOS·Metal(Apple M4 Pro)에서 `Common.FlowField.Tests` 486개 통과, 실패 0, 성능 프로브 15개 제외. Windows·Direct3D 11 마지막 기록은 이번 수정 이전의 466개 |
| 베이크 | Surface·Volume 모두 v6만 허용. Asset 2개는 같은 GUID로 다시 베이크했고 내용 해시는 v5와 같다. 마이그레이션 코드는 없다 |
| 공개 계약 | Provider·Controller API, Manager 직렬화 설정, 씬·Prefab의 GUID 참조 유지. `FlowFieldManager.GpuFallbackReason` 추가 |
| 성능 (100³ Volume, Managed) | 전체 빌드 26~55초 → 약 6초, 2ms 예산 빌드 12,585 → 2,214프레임, 장애물 1개 이동 후 재빌드 31.3 → 6.0초 |

## 완료한 작업

`30d485d`에서 `cec0b80`까지 11개 커밋을 `main`에 fast-forward로 병합했다.

| 커밋 | 작업 | 결과 |
| --- | --- | --- |
| `18b4fa1` | 안전망: 전체 셀 골든 해시, 성능 프로브 | 기준선 449개 통과와 기준 수치 확보 |
| `ca0b060` | 미사용 멤버·중복 헬퍼·클로저 할당 제거 | 골든 해시 일치 |
| `5ff76ac` | GPU Readback 요청 뒤 `GL.Flush` | 배치 모드에서 수백 프레임 늦던 Readback 해결, 장면 테스트 27~31초 → 2초 |
| `deb4822` | Modifier 적용 규칙을 Surface·Volume 공통으로 통일 | 이전까지 모드마다 달랐던 판정 제거 |
| `6b2ee7f` | Volume 셀 쿼리 후보 필터, 내부 셀 Topology 경로 | Topology 17.8초 → 1.5초, 등록 장애물 50개 빌드 55.4초 → 6.4초 |
| `bdae69a` | BFS가 예정된 Surface 빌드의 사전 합성 생략 | 불필요한 Modifier 호출 9회 → 0회 |
| `6b81e3c` | Session 공통 수명 명령, Manager 중복 정리 | 동작 변경 없음 |
| `07b751b` | 베이크 v6 | Volume Asset 453,863 → 106,568바이트, Surface Asset 1,286,861 → 981,158바이트 |
| `dbc419a` | Volume 빌드 전용 배열·Safety Snapshot 재사용 | 재빌드 관리 힙 +61 → +52.5MiB |
| `adbfd6a` | 샘플의 죽은 진단 기능, 미사용 공개 속성 17개 제거 | 직렬화 무결성 테스트 통과 |
| `cec0b80` | README·기술 문서·구조도 갱신 | 문서 링크 51개 확인 |

사용자에게 보이는 변경은 다음과 같다.

- Surface의 Goal Anchor 셀 속도가 1에서 0으로 바뀌었다. Default Direction이 바닥 법선과 평행한 셀도 정지한다.
- v5 베이크 Asset은 거부되므로 ReBake해야 한다.
- 샘플 컴포넌트의 미사용 공개 속성이 사라졌다. 샘플을 직접 참조하는 외부 코드만 영향을 받는다.

### 유지보수 작업 (2026-10-31)

| 커밋 | 작업 | 결과 |
| --- | --- | --- |
| `a56c4c4` | Surface: 장애물만 낡은 BFS 결과도 게시, 거부 경로는 Goal 재계산 | Goal 유실·게시 정지 해결, 회귀 테스트 4개 |
| `0ae4a7e` | Volume: 불확실 영역을 빌드 시작 전후로 분리 | 계속 움직이는 장애물의 경로 전체 정지 해결, 회귀 테스트 1개 |
| `376ca80` | GPU: 실행 중 충돌은 해당 빌드만 Managed, 전환 이유 공개 | GPU 영구 비활성화 해결, 회귀 테스트 3개 |
| `f4ca046` | Driver: Play 종료와 함께 제거 | 숨은 Driver 5개 누적 해결(프레임 예산도 함께 늘던 문제), 회귀 테스트 1개 |
| `e32ae63` | 런타임 중복 정리 | 마스크 순회, 요청 조립, 파도 수 제한, 미리보기 호출, `ComposeRegion`, `IsMissingModifier` |
| `8f9d0d8` | 베이크 에디터 입력 비교·저장 분리 | `BakeAndAssign` 302 → 약 200줄. 전후 베이크 바이트 동일 |
| `2232963` | 단계별 시간 프로브 | 런타임 코드 변경 없음 |
| `d78a54a` | Surface 재빌드 버전 상태 분리 | `FlowFieldSurfaceRebuildTracker` |
| `9da2f6b` | 리뷰 반영 | Volume 전환 이유 초기화, 거부 시 경고 상태 유지, Driver 정리, 회귀 테스트 2개 |
| `5d6432f` | Surface: Goal이 연속으로 밀리면 푼 결과를 게시 | 움직이는 Goal의 게시 정지 해결, 회귀 테스트 1개 |
| `fdd553e` | Surface Managed BFS가 준비된 Topology 재사용 | 재빌드 425 → 205ms |
| `f51f420` | Volume 안전 스냅샷이 영역 근처 셀만 검사 | 영역 64개 8,872 → 6.6ms, 회귀 테스트 1개 |
| `cbe2a5e` | Volume Modifier 변경은 게시된 내비게이션을 재합성 | 값 변경 3,304 → 425ms, 회귀 테스트 4개 |
| `afb1fdf` | 샘플 HUD 패널 확장 | 안내 문구와 버튼 겹침 해결 |
| `5cdde7f` | Volume: Goal만 바뀌면 장애물 결과 재사용 | Goal 변경 약 3,100 → 2,227ms, 회귀 테스트 3개 |

Editor Play Mode 확인: Surface 샘플은 장애물이 매 Editor 틱 움직이는 동안 Revision 68까지 게시하고 Goal 변경을 반영했다. Volume 샘플은 StaticBaked v6를 로드했고, RuntimeDynamic으로 바꿔 장애물을 왕복시켰을 때 Revision 29, 정지 셀 133/4800이었다. 경고와 오류는 없었다. 결과 XML·로그·CSV는 Git에서 제외되는 `ToyProject/Logs/FlowFieldMaintenance/`에 있다.

## 남은 개선 사항

### 알려진 문제

| 문제 | 현황 | 해결 방향 |
| --- | --- | --- |
| Surface: 장애물과 Modifier 영역이 풀이 중 함께 바뀌면 영역을 한 번 더 만듦 | 결과는 같고 드문 경로의 비용만 중복 | 필요할 때 플래그 분리 |

### 검증 공백

| 항목 | 현황 | 할 일 |
| --- | --- | --- |
| 베이크 에디터 | 테스트 어셈블리가 `Common.FlowField.Editor`를 참조하지 않아 `BakeAndAssign`을 직접 검사하지 않는다. 지금은 실제 베이크 후 `_revision` 외 차이가 없는지 수동으로 비교한다 | 자동 테스트 추가 검토 |
| Windows | 이번 수정은 Metal에서만 실행했다 | Windows·Direct3D 11에서 전체 테스트 재실행 |
| 실제 플레이 | Surface 샘플에서 실제 포인터로 게이트·Next Goal 버튼을, 두 샘플에서 Space를 확인했다. macOS 입력 소스가 한글인 상태에서는 R 키가 반응하지 않아 O·R·G·F 실제 키 입력은 확인하지 못했고, Volume의 게이트·카메라 핸들러는 직접 호출로 확인했다 | 영문 입력 소스에서 키 확인 |
| GPU 경로 | 프로브와 골든 해시는 Managed 고정이다. GPU 빌드 시간 수치가 없고, GPU 결과는 위상 유효성만 검사하므로 동률 이웃이 Managed와 다를 수 있다 | 프로브에 GPU 변형 추가 |
| Player 스크립트 | Mac Standalone 어셈블리 컴파일만 오류 없이 확인했다. 실행은 범위에서 제외했다 | 보류 항목 참고 |
| 자동 실행 | 저장소에 CI 설정이 없다. Unity 라이선스에 로그인한 PC에서 수동으로 실행한다 | 자체 호스트 러너 등 검토 |

### 성능

Apple M4 Pro·Managed 측정(`FlowFieldPerformanceProbes`). 결과 CSV는 `ToyProject/Logs/FlowFieldMaintenance/`에 있다.

| 항목 | 개선 전 | 현재 | 원인과 수정 |
| --- | ---: | ---: | --- |
| Surface 316×316 Goal 재빌드 | 425ms | 205ms | 셀마다 `CanTraverse`를 다시 부르고 Goal 준비를 두 번 했다. 준비된 Topology를 재사용한다 |
| Volume 100³ 안전 스냅샷, 영역 1 / 64개 | 323ms / 8,872ms | 1.8ms / 6.6ms | 게시 프레임에 전체 셀×영역을 검사했다. 영역 근처 셀만 검사한다 |
| Volume 100³ Modifier 값 변경 | 3,304ms | 425ms | 전체 빌드를 했다. Modifier 단계만 다시 실행한다 |
| Volume 100³ Goal 변경 | 약 3,100ms | 2,227ms | Goal만 바뀌면 장애물 결과와 Topology를 재사용한다 |

Surface Managed 재빌드 약 200ms는 여전히 한 번의 Pump에서 동기로 끝난다. Goal 준비 88ms, BFS 76ms, 합성 22ms, 탈출 방향 13ms다. BFS를 프레임으로 나누는 작업은 Session 변경이 커서 하지 않았고, GPU 경로를 쓸 수 없는 환경에서 200ms가 문제 될 때 다시 본다.

Volume 100³·장애물 50개·Modifier 3개의 전체 빌드는 약 3.6초다.

| Directions | BFS | Modifiers | Topology | Obstacles | Escape | Goal |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 983ms (27%) | 695ms (20%) | 599ms (17%) | 532ms (15%) | 405ms (12%) | 187ms (5%) | 120ms (3%) |

GPU 경로(같은 장면, 배치 실행의 프레임 대기 포함)는 Surface 332ms, 그중 첫 Pump 동기 구간 170ms이고, Volume 5,714ms다. 이 환경에서는 수정 후 Managed가 전체 시간은 더 짧다. 프레임 끊김 비교는 실제 Player 프레임 기준으로 다시 재야 한다.

### 보류한 항목

| 항목 | 보류 이유 | 다시 볼 조건 |
| --- | --- | --- |
| Player 빌드 검증 | 2026-10-31 이번 계획 범위에서 제외했다. 실행 기록이 없어 Player에서의 동작은 미확인이다. v6 로드는 `MemoryMarshal`과 `ISerializationCallbackReceiver`에 의존하고, 첫 빌드 씬은 템플릿 `SampleScene`이며, 이 Mac의 2023.2.20f1에는 Mono용 Mac Player만 있다 | Player 배포가 필요해질 때. IL2CPP는 Mac Build Support (IL2CPP) 설치 후 |
| Burst 전환 | Directions·BFS·Topology가 합계 62%지만 관리 배열 변환이 크고, Burst 동작은 Player에서 확인해야 하는데 Player 검증을 범위에서 제외했다 | Player 검증을 재개하고 Volume 전체 빌드 시간이 문제 될 때 |
| 게시 배열 재사용 | 빌드 중 GC 0회. 재사용하면 상주 메모리가 약 50MB 늘어난다 | 실제 프로파일에서 재빌드 뒤 GC 스파이크가 문제 될 때 |
| 증분 장애물 갱신 | 장애물 단계가 전체의 7~8%다. BFS·방향까지 증분이 아니면 효과가 작다 | 복잡한 Collider 장면에서 이 단계 비중이 커질 때 |
| GPU 실패 재시도 | 관찰된 지연은 `GL.Flush`로 해결했다. 파도 수 초과·결과 검증 실패 후에는 같은 수명 동안 Managed로 고정한다. 실행 중 충돌로 인한 고정은 다음 순서 2단계에서 고친다 | 실제 기기에서 일시적 GPU 실패가 확인될 때 |
| Surface 버전·지연 플래그 제거 | 오래된 결과 판정 외에 Source 재진입 감지, Goal 재시도, 명시적 재빌드 게이트에도 쓰인다. `FlowFieldSurfaceRebuildTracker`로 한곳에 모았다 | Surface Session을 다시 설계할 때 |

### 구조

- 큰 파일: `FlowFieldSession.cs` 1,977줄, `FlowFieldVolumeRuntime.cs` 1,600줄, `FlowFieldManager.cs` 1,414줄. 수명·재진입 검사가 곳곳에 있어 분리 자체의 회귀 위험이 크다. 해당 파일을 크게 바꾸는 기능 작업과 함께 진행한다.
- 파일 이름: `FlowFieldVolumeRuntime.cs`에는 `FlowFieldVolumeRequest`와 `FlowFieldVolumeSession`이 들어 있다. `.meta`를 유지한 채 이름을 맞출 수 있다.
- 복잡성의 근원: 콜백 안의 Release·Init·Suspend를 허용해 외부 호출 뒤마다 소유권을 다시 검사한다. 이벤트 지연 전달은 공개 동작을 바꾸므로 진행하지 않았다.
- Manager: `SpaceMode == Volume3D` 분기 11곳. 모드별 어댑터 분리는 Manager를 크게 바꾸는 기능 작업과 함께 진행한다.

## 다음 순서

2026-10-31 계획을 완료했다. Player 실행은 범위에서 제외했다. 다음 항목은 이 Mac이나 현재 입력 환경에서 할 수 없다.

1. Windows·Direct3D 11 전체 테스트 재실행
2. 영문 입력 소스에서 샘플의 O·R·G·F 키 확인

테스트와 프로브 실행 방법은 [README.md](../README.md#검증-기록)를 따른다. 단계별 XML·로그와 프로브 CSV는 Git에서 제외되는 `ToyProject/Logs/`에 있어 작업한 PC에만 남는다.
