# FlowField 작업 현황과 남은 과제

기준: 2026-10-31, 코드 커밋 `cec0b80`. 단계별 결정과 근거는 [RefactoringNotes.md](RefactoringNotes.md#성능구조-개선-2026-10-2930), 사용법과 측정 표는 [README.md](../README.md#검증-기록)에 있다. 이 문서는 지금 상태와 다음에 할 일만 다룬다.

## 현재 상태

| 항목 | 상태 |
| --- | --- |
| 테스트 | `Common.FlowField.Tests` 466개 통과, 실패 0. Windows 11·Direct3D 11은 배치 실행, macOS·Metal(Apple M4 Pro)은 열린 Editor에서 실행(`d70e2c3`). 성능 프로브 7개는 일반 실행에서 제외 |
| 베이크 | Surface·Volume 모두 v6만 허용. Asset 2개는 같은 GUID로 다시 베이크했고 내용 해시는 v5와 같다. 마이그레이션 코드는 없다 |
| 공개 계약 | Provider·Controller API, Manager 직렬화 설정, 씬·Prefab의 GUID 참조 유지 |
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

## 남은 개선 사항

### 재현된 결함

2026-10-31 macOS·Metal Editor에서 임시 프로브로 재현했다. 프로브는 저장소에 남기지 않았다. 실제 씬 재현은 아니며, 수정 때 같은 시나리오를 정식 회귀 테스트로 추가한다.

| 결함 | 재현 결과 | 원인 |
| --- | --- | --- |
| Surface: BFS 중 장애물이 움직이면 Goal 변경이 사라짐 | 높이 100에서 마스크 변화 없이 이동. 대조군은 Goal B 게시, 이동 시 Revision +0으로 A 유지, 재시도 없음 | Goal Dirty를 소비한 뒤 `RejectStaleBfsResult`의 프로브 대기 경로가 복원하지 않고, Goal Tracker는 B를 처리한 것으로 기록 |
| Surface: 풀이 때마다 장애물이 움직이면 게시가 멈춤 | BFS 50회 전부 폐기, 게시 0회. 정지 후 게시 | 같은 경로. 이 경로를 지나는 회귀 테스트가 없음. Managed는 동기 완료라 해당 없음 |
| Volume: 계속 움직이는 장애물 주변 Agent가 풀리지 않음 | 12×4×12, 60,000스텝, 빌드 967회 동안 217/576셀 정지. 정지 후 1/576 | 대기 요청이나 Epoch 변경이 있으면 커밋 때 불확실 영역을 지우지 않아 합집합이 계속 유지됨 |
| Volume: GPU 풀이 중 Suspend→Resume 후 바로 BFS에 들어가면 GPU가 수명 내내 꺼짐 | 이후 빌드 GPU 미사용. Surface는 Suspend 때 Backend를 분리해 해당 없음 | 이전 Readback으로 `Start`가 거부되면 `_gpuDisabled = true` |

### 검증 공백

구조 변경 전에 처리한다.

| 항목 | 현황 | 할 일 |
| --- | --- | --- |
| macOS/Metal 배치 실행 | 2026-10-31 열린 Editor에서 466개 통과, 실패 0, 37.7초. GPU 테스트는 건너뛰지 않았고 v6 로드와 Readback 경로도 통과했다. 장면 테스트는 1.6초·0.66초다. 다만 Readback 지연은 원래 배치 모드에서 관찰됐고, 이 실행은 결과 XML을 남기지 않았으며 도메인·씬 리로드를 끈 설정이었다 | Editor를 닫고 Metal 배치 모드로 다시 실행해 `GL.Flush` 확인, 결과 XML 보관 |
| 베이크 에디터 | 테스트 어셈블리가 `Common.FlowField.Editor`를 참조하지 않아 `BakeAndAssign`을 직접 검사하지 않는다 | 정리 전에 Editor 테스트나 베이크 내용 해시 비교 추가 |
| Driver 수명 | Play 종료 후 숨은 `FlowFieldBuildDriver`가 남고 `FindObjectsByType`로 찾히지 않는다. 중복 생성 여부는 미확인 | 도메인 리로드를 켠 Play 반복으로 확인, 중복되면 수정 |
| 실제 플레이 | 포인터 조작, GUI 렌더링, 장시간 군중 이동을 확인하지 않았다. README의 기능 이미지도 이전 캡처다 | 샘플 씬을 직접 실행해 확인하고 달라졌으면 재캡처 |
| GPU 경로 | 프로브와 골든 해시는 Managed 고정이다. GPU 빌드 시간 수치가 없고, GPU 결과는 위상 유효성만 검사하므로 동률 이웃이 Managed와 다를 수 있다 | 프로브에 GPU 변형 추가 |
| 자동 실행 | 저장소에 CI 설정이 없다. Unity 라이선스에 로그인한 PC에서 수동으로 실행한다 | 자체 호스트 러너 등 검토 |

### 성능

Volume의 남은 시간 분포는 다음과 같다. 100³·장애물 40개·Managed에서 3단계 완료 시점에 임시 계측으로 1회 잰 값이며(합계 약 6.8초), 계측 코드는 저장소에 남기지 않았다.

| Directions | BFS | Topology | Obstacles | Escape | Modifiers | Goal |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2,240ms (33%) | 1,628ms (24%) | 1,483ms (22%) | 460ms (7%) | 420ms (6%) | 281ms (4%) | 225ms (3%) |

| 순위 | 항목 | 근거 | 예상 효과와 주의점 |
| ---: | --- | --- | --- |
| 1 | 단계별 시간을 프로브에 포함 | 지금은 단계 시간을 잴 때마다 임시 코드를 넣었다 뺀다 | 아래 항목의 전제. 런타임에 측정 코드가 남지 않게 한다 |
| 2 | Surface 재빌드 원인 분석 | 316×316(약 10만 셀) Managed 재빌드가 1.1~1.3초인데 단계 분해가 없다. Volume은 100만 셀의 BFS가 1.6초다. Managed BFS는 한 번의 Pump에서 동기로 끝나고 최초 빌드의 관리 힙 증가는 183MiB다 | Managed 경로의 프레임 끊김과 직결된다. 효과는 측정 후 판단한다 |
| 3 | Volume Modifier 값 변경 시 부분 재합성 | 값만 바뀌어도 `QueueCurrentRequest()`로 전체 빌드(약 6초)를 다시 한다. Surface의 같은 변경은 10만 셀에서 약 15ms다 | 1초 미만 예상(미측정). 전체 빌드는 지금까지 유지한 정책이므로 변경 결정이 필요하다. 그 빌드에서 함께 갱신되던 미추적 장애물의 반영 시점이 달라진다 |
| 4 | Volume Goal만 바뀔 때 Obstacles·Topology 재사용 | 두 단계 합계 약 1.9초(29%) | 약 29% 단축. 3번과 같은 주의점이 있다 |
| 5 | Directions 단계 최적화 | 가장 큰 단계(33%) | 결과가 골든 해시와 같아야 한다 |

2ms 예산 빌드는 2,214프레임, 60FPS 기준 약 37초가 걸리며 그동안 이전 결과를 계속 샘플링한다. 3~5번은 이 시간도 함께 줄인다.

### 보류한 항목

| 항목 | 보류 이유 | 다시 볼 조건 |
| --- | --- | --- |
| Player 빌드 검증 | 2026-10-31 이번 계획 범위에서 제외했다. 실행 기록이 없어 Player에서의 동작은 미확인이다. v6 로드는 `MemoryMarshal`과 `ISerializationCallbackReceiver`에 의존하고, 첫 빌드 씬은 템플릿 `SampleScene`이며, 이 Mac의 2023.2.20f1에는 Mono용 Mac Player만 있다 | Player 배포가 필요해질 때. IL2CPP는 Mac Build Support (IL2CPP) 설치 후 |
| 게시 배열 재사용 | 빌드 중 GC 0회. 재사용하면 상주 메모리가 약 50MB 늘어난다 | 실제 프로파일에서 재빌드 뒤 GC 스파이크가 문제 될 때 |
| 증분 장애물 갱신 | 장애물 단계가 전체의 7~8%다. BFS·방향까지 증분이 아니면 효과가 작다 | 복잡한 Collider 장면에서 이 단계 비중이 커질 때 |
| GPU 실패 재시도 | 관찰된 지연은 `GL.Flush`로 해결했다. 파도 수 초과·결과 검증 실패 후에는 같은 수명 동안 Managed로 고정한다. 실행 중 충돌로 인한 고정은 다음 순서 2단계에서 고친다 | 실제 기기에서 일시적 GPU 실패가 확인될 때 |
| Surface 버전·지연 플래그 제거 | 오래된 결과 판정 외에 Source 재진입 감지, Goal 재시도, 명시적 재빌드 게이트에도 쓰인다 | Surface Session을 다시 설계할 때 |

### 구조

- 큰 파일: `FlowFieldSession.cs` 2,103줄, `FlowFieldVolumeRuntime.cs` 1,587줄, `FlowFieldManager.cs` 1,449줄. 수명·재진입 검사가 곳곳에 있어 분리 자체의 회귀 위험이 크다. 해당 파일을 크게 바꾸는 기능 작업과 함께 진행한다.
- 파일 이름: `FlowFieldVolumeRuntime.cs`에는 `FlowFieldVolumeRequest`와 `FlowFieldVolumeSession`이 들어 있다. `.meta`를 유지한 채 이름을 맞출 수 있다.
- 복잡성의 근원: 콜백 안의 Release·Init·Suspend를 허용해 외부 호출 뒤마다 소유권을 다시 검사한다. 이벤트 지연 전달은 공개 동작을 바꾸므로 지금은 진행하지 않는다.
- Surface 재빌드 상태: 요청 복사본 3개, 대기·Dirty·지연 플래그, 버전 값이 `FlowFieldSession`에 흩어져 있다.
- Manager: `SpaceMode == Volume3D` 분기 11곳, `#if UNITY_EDITOR` 19곳.
- 중복: `FlowFieldObstacleMaskBuilder`의 `Build`·`BuildStatic`, Surface 요청 조립 4곳, Volume 요청 조립 2곳, 파도 수 제한 계산, `IsCurrentInput` 블록 4곳, `ComposeRegion`의 중복 검사, `IsMissingModifier`.

## 다음 순서

2026-10-31 확정. 단계마다 별도 커밋으로 나누고 테스트 466개와 골든 해시를 유지한다. 새 회귀 테스트만큼 기준 수가 늘어난다.

1. **이동 장애물 결함.**
   - Surface: 장애물만 낡은 BFS 결과는 버리지 않고 게시한 뒤 재빌드를 예약한다. 샘플링이 최신 장애물을 다시 검사하고, Volume도 같은 방식이다. 커밋이 장애물 Dirty를 지우지 않게 한다. Grid·Source가 바뀐 결과는 계속 거부하고, 남은 거부 경로는 Goal Dirty를 복원한다.
   - Volume: 불확실 영역을 빌드 시작 전후 두 목록으로 나눠, 끝난 빌드가 반영한 목록만 지운다. 빌드가 긴 격자에서 그 사이 지나간 영역의 정지는 안전 정책으로 유지한다.
   - 완료 조건: 재현 시나리오 3개를 회귀 테스트로 추가, `MovingRegisteredObstacleDuringBuildProtectsOldAndNewRegions` 유지.
2. **GPU 전환 정책.** Volume에서 Solver 실행 중 시작 실패와 격자 변경은 해당 빌드만 Managed로 처리한다. 파도 수 초과·결과 검증 실패는 고정을 유지한다. Manager에서 GPU 상태와 전환 이유를 확인할 수 있게 하고 전환 시 경고를 한 번 남긴다.
3. **Editor 환경 검증.** Metal 배치 실행과 결과 XML 보관, Editor Play Mode에서 샘플 씬 실행, Driver 중복 확인. Player 빌드는 제외한다.
4. **런타임 중복 정리.** 동작 변경 없이 위 중복 중 베이크 에디터 외 항목을 정리한다. 마스크 공통화는 셀 단위 경로이므로 구조체 전략을 쓴다.
5. **베이크 에디터 정리.** 검증 수단을 먼저 추가한 뒤 `IsCurrentInput` 반복과 `BakeAndAssign`을 나눈다.
6. **구조·성능.** 단계별 계측과 Surface 재빌드 원인 분석을 진행한다. Surface 재빌드 추적은 1단계 후, Manager 어댑터와 Volume Modifier 부분 재합성은 정책 결정 후 함께 진행한다.

테스트와 프로브 실행 방법은 [README.md](../README.md#검증-기록)를 따른다. 단계별 XML·로그와 프로브 CSV는 Git에서 제외되는 `ToyProject/Logs/`에 있어 작업한 PC에만 남는다.
