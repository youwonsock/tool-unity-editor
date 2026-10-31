# FlowField 작업 현황과 남은 과제

기준: 2026-10-31. 단계별 결정과 근거는 [RefactoringNotes.md](RefactoringNotes.md#성능구조-개선-2026-10-2930), 사용법과 측정 표는 [README.md](../README.md#검증-기록)에 있다. 이 문서는 지금 상태와 다음에 할 일만 다룬다.

## 현재 상태

| 항목 | 상태 |
| --- | --- |
| 테스트 | macOS·Metal(Apple M4 Pro)에서 `Common.FlowField.Tests` 550개 통과, 실패 0, 성능 프로브 14개 제외. 저장소 루트의 `Tools/run-tests.sh`로 실행한다. Windows·Direct3D 11 마지막 기록은 이번 수정 이전의 466개 |
| 베이크 | Surface·Volume 모두 v6만 허용. 마이그레이션 코드는 없다. 맞닿은 셀 판정을 바꾼 뒤 Surface Asset을 다시 베이크해 보니 막힌 셀 변경이 0개여서 Asset은 그대로 뒀다 |
| 공개 계약 | Provider·Controller API, Manager 직렬화 설정, 씬·Prefab의 GUID 참조 유지. `FlowFieldManager.GpuFallbackReason` 추가 |
| 성능 (100³ Volume, Managed, Windows 기록) | 전체 빌드 26~55초 → 약 6초, 2ms 예산 빌드 12,585 → 2,214프레임, 장애물 1개 이동 후 재빌드 31.3 → 6.0초 |

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
| `2f0c578`, `3332488` | 스냅샷·Modifier·Goal 변경·GPU 프로브 | 런타임 코드 변경 없음 |
| `45cb514` | Volume: 명시적 재빌드는 Goal이 함께 바뀌어도 장애물을 다시 조회 | 재조회 누락 해결, 회귀 테스트 6개 |
| `b0ad1a9` | Surface: 끊긴 재시도 뒤의 일회성 Goal 변경도 거부 | 낡은 Goal 게시 해결, 회귀 테스트 2개 |
| `00a912c` | Volume: 값이 중복된 안전 스냅샷 제거 | 게시마다 하던 전체 셀 복사와 셀당 1바이트 제거 |
| `bd74357` | Surface: 재시도 때 Goal을 다시 풀고 stale 상태의 해제 허용 | 재시도 후 기본 방향이 게시되던 문제, `OnDisable` 예외 해결, 회귀 테스트 4개 |
| `5de252c` | Volume: GPU 대기·비활성 중 수명과 관측 보존 | GPU 대기 중 장애물 미감지, 꺼진 Manager의 빌드, Modifier 재연결 누락, 대기 중 예산 공회전 해결, 회귀 테스트 9개 |
| `016bc8e` | Surface: Modifier 갱신 영역을 마스크 기준으로 계산 | 격자에 맞춘 Collider의 옛 값 잔류 해결, 값 변경 통지가 Revision과 무관하게 동작, 회귀 테스트 3개 |
| `46a9c09` | Volume: StaticBaked 기본 방향과 같은 셀 Goal 이동 처리 | 방향과 안전 검사 불일치 해결, 같은 셀 이동의 재빌드 제거, 회귀 테스트 7개 |
| `5df5067` | 맞닿은 셀 판정 통일 | 1칸 Collider가 이웃을 잡던 문제 해결, 골든 해시 2개 갱신, 회귀 테스트 7개 |
| 이번 수정 | 비활성 중 재시도와 재활성화 | 재시도가 Suspended로 돌아가 `OnEnable` 예외와 비활성 중 빌드 해결, 늦게 켠 Modifier 재연결, Volume `Release`의 `LastError` 해제, 회귀 테스트 11개 |
| 이번 수정 | 백로그 정리 | `CellCount`·`IsValid` 저장, Surface 베이크 검증 캐시, Volume StaticBaked의 탐색 배열 제거, Goal 셀 없는 Surface 베이크 거부, 실패한 베이크가 이전 Undo를 되돌리지 않음, Editor 표시 수정, 죽은 코드 제거 |
| 이번 수정 | Surface 감지 실패와 `timeScale = 0` | `LateUpdate` 감지 예외를 Fault로 처리, 시간이 멈춰도 재빌드, 감지 주기를 실제 시간 기준으로 변경, Volume 스윕이 `_refreshRate`를 따름 |

사용자에게 보이는 변경은 다음과 같다.

- Goal을 지정했는데 이동 가능한 셀이 없으면 Surface 베이크가 실패한다. Volume은 이미 그랬다.
- Volume도 `_refreshRate`가 0.05 미만이면 초기화가 실패한다.
- 감지 주기 `_refreshRate`가 실제 시간 기준으로 동작하고 Volume의 미등록 장애물 스윕에도 적용된다. Volume Inspector에도 표시한다. Volume 스윕의 감지 지연이 최대 `_refreshRate`만큼 늘어난다.
- Surface: 풀이 중 장애물만 바뀌면 그 결과를 게시한다. Goal이 풀이보다 자주 바뀌면 밀린 결과를 번갈아 게시하므로, 해석된 Goal이 요청 Goal보다 잠시 뒤처질 수 있다.
- Volume: Goal이나 Modifier만 바뀌면 추적하지 않는 Collider를 다시 읽지 않는다. `RequestRebuild()`와 `NotifyCellsDirty()`는 다시 읽는다.
- `SetGoal()` 뒤의 `RequestRebuild()`는 Volume의 Goal 재사용 효과를 없앤다. Goal만 바꿀 때는 `SetGoal()`만 호출한다.
- GPU를 끄고 Managed로 전환하면 경고를 한 번 남기고 `GpuFallbackReason`으로 이유를 제공한다.
- Volume StaticBaked의 영향 범위 밖 셀이 베이크 시점 대신 Manager의 현재 Default Direction을 따른다.
- Volume의 같은 셀 안 Goal 이동은 Revision을 올리지 않고 `FieldChanged`도 발생시키지 않는다. Volume의 요청 Goal 정보는 게시 결과가 아니라 최신 입력을 보고한다.
- 격자에 딱 맞춘 Collider는 맞닿은 이웃 셀을 더 이상 막거나 Modifier로 덮지 않는다. 이웃까지 필요하면 Obstacle Clearance를 쓴다.
- Surface는 Revision을 올리지 않은 `NotifyModifierChanged(Value)`도 다시 합성한다. 설정이 stale이어도 Modifier와 동적 장애물의 등록을 해제할 수 있다.

Editor Play Mode 확인: Surface 샘플은 장애물이 매 Editor 틱 움직이는 동안 Revision 68까지 게시하고 Goal 변경을 반영했다. Volume 샘플은 StaticBaked v6를 로드했고, RuntimeDynamic으로 바꿔 장애물을 왕복시켰을 때 Revision 29, 정지 셀 133/4800이었다. 경고와 오류는 없었다. 군중 1000개를 3분 이동시켰을 때 NaN·경계 이탈·샘플 실패는 0이었고, Goal을 40번 바꾸는 동안 GC 후 힙은 744MB로 유지됐다. 결과 XML·로그·CSV는 Git에서 제외되는 `ToyProject/Logs/FlowFieldMaintenance/`에 있다.

## 남은 개선 사항

### 알려진 문제

| 문제 | 현황 | 해결 방향 |
| --- | --- | --- |
| 베이크 GPU 경로 | Surface 베이크만 GPU를 쓴다. 통일할 근거가 되는 측정이 없어 보류했다 | Volume 베이크 시간이 문제 될 때 |
| Surface GPU: Goal이 풀이보다 자주 바뀌면 풀이가 하나 걸러 버려지고, 같은 셀 안의 이동도 풀이를 무효화 | 일회성 변경의 낡은 결과를 게시하지 않는 계약의 비용. GPU 경로에서만 생기며 실제 프레임 측정이 없다 | Player 프레임을 측정할 수 있을 때 해석된 셀 비교 검토 |

### 검증 공백

| 항목 | 현황 | 할 일 |
| --- | --- | --- |
| 베이크 에디터 | `FlowFieldBakeEditorTests`가 임시 씬에서 재베이크의 `_revision` 외 차이 없음, 입력 변경 시 Asset 유지, 대상 소유권을 검사한다. 저장 실패 복구는 주입할 방법이 없어 검사하지 않는다 | 필요할 때 저장 실패 경로 검증 |
| Windows | 이번 수정은 Metal에서만 실행했다 | Windows·Direct3D 11에서 전체 테스트 재실행 |
| 실제 플레이 | 실제 포인터로 Surface의 게이트·Next Goal 버튼을, 실제 키로 Surface의 Space·M·R·G·1·2·3·O·F와 Volume StaticBaked의 Space·R·O·G·F, RuntimeDynamic의 Space·O·R·G를 확인했다. Surface의 C는 입력을 받지만, 진단이 매 프레임 갱신되므로 눌러도 결과가 달라지지 않는다. 재조회 수정 뒤 RuntimeDynamic의 Space·O·R·G를 다시 확인했고, 두 HUD의 GPU 전환 이유는 값을 넣어 표시를 확인했다. RMB+WASD 자유 카메라는 확인하지 않았다. 결함 수정 뒤에는 키 입력 대신 공개 API로 Play Mode를 점검했다: Surface의 Goal 변경·Gate·비활성화 후 복귀, Volume StaticBaked 재로드, RuntimeDynamic의 Goal 변경·같은 셀 이동·꺼진 동안의 장애물 등록과 Modifier 연결. 경고와 오류는 없었다 | 필요할 때 카메라 조작과 실제 키 입력 재확인 |
| GPU 경로 | 골든 해시는 Managed 고정이다. 동률 이웃이 다를 수 있어 큰 장면(64×48, 32×12×32)은 Blocked와 셀별 경로 길이를 Managed와 비교한다. Modifier 합성 결과는 비교하지 않는다. GPU 빌드 시간은 배치 실행의 프레임 대기를 포함한 수치만 있다 | 프레임 끊김은 Player 검증을 재개할 때 비교 |
| Player 스크립트 | Mac Standalone 어셈블리 컴파일만 오류 없이 확인했다. 실행은 범위에서 제외했다 | 보류 항목 참고 |
| 자동 실행 | 저장소에 CI 설정이 없다. Unity 라이선스에 로그인한 PC에서 수동으로 실행한다 | 자체 호스트 러너 등 검토 |

### 성능

Apple M4 Pro·Managed 측정(`FlowFieldPerformanceProbes`). 결과 CSV는 `ToyProject/Logs/FlowFieldMaintenance/`에 있다.

| 항목 | 개선 전 | 현재 | 원인과 수정 |
| --- | ---: | ---: | --- |
| Surface 316×316 Goal 재빌드 | 425ms | 157ms | 셀마다 `CanTraverse`를 다시 부르고 Goal 준비를 두 번 했다. 준비된 Topology를 재사용하고 `CellCount`·`IsValid`를 저장한다 |
| Volume 100³ 안전 스냅샷, 영역 1 / 64개 | 323ms / 8,872ms | 없음 | 게시 프레임에 전체 셀×영역을 검사했다. 값이 중복되어 스냅샷을 제거했다 |
| Volume 100³ Modifier 값 변경 | 3,304ms | 424ms | 전체 빌드를 했다. Modifier 단계만 다시 실행한다 |
| Volume 100³ Goal 변경 | 약 3,100ms | 2,135ms | Goal만 바뀌면 장애물 결과와 Topology를 재사용한다 |

Surface Managed 재빌드 약 150ms는 여전히 한 번의 Pump에서 동기로 끝난다. Goal 준비 70ms, BFS 58ms, 합성 11ms, 탈출 방향 13ms다. BFS를 프레임으로 나누는 작업은 Session 변경이 커서 하지 않았고, GPU 경로를 쓸 수 없는 환경에서 150ms가 문제 될 때 다시 본다.

Volume 100³·장애물 50개·Modifier 3개의 전체 빌드는 약 3.5초다.

| Directions | BFS | Modifiers | Topology | Obstacles | Escape | Goal |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 964ms (28%) | 677ms (19%) | 615ms (18%) | 521ms (15%) | 380ms (11%) | 184ms (5%) | 119ms (3%) |

GPU 경로(같은 장면, 배치 실행의 프레임 대기 포함)는 Surface 253ms, 그중 첫 Pump 동기 구간 142ms이고, Volume 2,802ms로 Managed 3,086ms보다 조금 짧다. 이전 기록 5,814ms는 프로브의 `Pump(1000d)`가 GPU를 기다리며 예산을 공회전으로 쓴 값이었다(`5de252c`에서 제거). 프레임 끊김 비교는 실제 Player 프레임 기준으로 다시 재야 한다.

### 보류한 항목

| 항목 | 보류 이유 | 다시 볼 조건 |
| --- | --- | --- |
| Player 빌드 검증 | 2026-10-31 이번 계획 범위에서 제외했다. 실행 기록이 없어 Player에서의 동작은 미확인이다. v6 로드는 `MemoryMarshal`과 `ISerializationCallbackReceiver`에 의존하고, 이 Mac의 2023.2.20f1에는 Mono용 Mac Player만 있다 | Player 배포가 필요해질 때. IL2CPP는 Mac Build Support (IL2CPP) 설치 후 |
| Burst 전환 | Directions·BFS·Topology가 합계 62%지만 관리 배열 변환이 크고, Burst 동작은 Player에서 확인해야 하는데 Player 검증을 범위에서 제외했다 | Player 검증을 재개하고 Volume 전체 빌드 시간이 문제 될 때 |
| 게시 배열 재사용 | 빌드 중 GC 0회. 재사용하면 상주 메모리가 약 50MB 늘어난다 | 실제 프로파일에서 재빌드 뒤 GC 스파이크가 문제 될 때 |
| 증분 장애물 갱신 | 장애물 단계가 전체의 12%다. BFS·방향까지 증분이 아니면 효과가 작다 | 복잡한 Collider 장면에서 이 단계 비중이 커질 때 |
| GPU 실패 재시도 | 관찰된 지연은 `GL.Flush`로 해결했다. 파도 수 초과·결과 검증 실패 후에는 같은 수명 동안 Managed로 고정한다. 실행 중 충돌은 해당 빌드만 Managed로 처리한다(`376ca80`) | 실제 기기에서 일시적 GPU 실패가 확인될 때 |
| Surface 버전·지연 플래그 제거 | 오래된 결과 판정 외에 Source 재진입 감지, Goal 재시도, 명시적 재빌드 게이트에도 쓰인다. `FlowFieldSurfaceRebuildTracker`로 한곳에 모았다 | Surface Session을 다시 설계할 때 |

### 구조

2026-10-31 구조 개편으로 Manager와 세션을 나눴다. 동작과 테스트 기대값은 바꾸지 않았다.

| 항목 | 개편 전 | 개편 후 |
| --- | ---: | ---: |
| `FlowFieldManager.cs` | 1,433줄 | 614줄 |
| `FlowFieldSession.cs` | 1,989줄 | 803줄 |
| `FlowFieldVolumeSession.cs` | 1,592줄 | 745줄 |
| Manager의 모드 분기 | 14곳 | 드라이버 선택 1곳 |
| 100줄을 넘는 메서드 | 10개 | 0개 |
| 외부 호출 뒤 소유권 재검사 | 약 43곳 | 약 41곳 |

- `FlowFieldSessionBase`: 공개 상태, Fault 순서, 작업 세대 같은 공통 수명 규칙. 재시도와 해제의 자원 처리는 모드별로 달라 각 세션에 있다.
- `FlowFieldManager.Modes.cs`: Surface와 Volume의 차이. Manager 본체에는 모드 분기가 없다. `FlowFieldManager.Validation.cs`, `FlowFieldManager.Bake.cs`는 설정 검증과 베이크 연동이다.
- 세션의 `.Build`, `.Sampling`, `.Views`, `.Memory`는 partial 파일이다. 필드를 공유하므로 읽기 쉬워졌을 뿐 결합이 줄지는 않았다. `FlowFieldSession.Build.cs`(841줄)가 가장 크다.
- 복잡성의 근원은 남아 있다: 콜백 안의 Release·Init·Suspend를 허용해 외부 호출 뒤마다 소유권을 다시 검사한다. Modifier와 Surface Source도 외부 호출이므로 이벤트 지연 전달만으로는 사라지지 않아 진행하지 않았다.

## 다음 순서

2026-10-31 재분석에서 찾은 결함과 백로그, 구조 개편을 마쳤다. Player 실행은 범위에서 제외했다. 남은 필수 항목은 이 Mac에서 할 수 없는 Windows·Direct3D 11 전체 테스트 재실행이다.

테스트와 프로브 실행 방법은 [README.md](../README.md#검증-기록)를 따른다. 단계별 XML·로그와 프로브 CSV는 Git에서 제외되는 `ToyProject/Logs/`에 있어 작업한 PC에만 남는다.
