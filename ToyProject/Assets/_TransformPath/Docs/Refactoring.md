# TransformPath 리팩터링과 검증

이 문서는 앞선 리팩터링 당시의 구조와 실행 결과를 기록합니다. 이후 Engine·Scope와 Queue 내부 계층을 합친 현재 구조 및 최종 검증은 [구조 단순화](Simplification.md)를 참고하세요.

- 작업일: 2026-10-24 (Asia/Seoul)
- 환경: macOS, Unity 2023.2.20f1
- 결과: EditMode 기능·UI 91/91, 성능 진단 7/7, PlayMode 5/5 통과
- 보존: 기존 네임스페이스·어셈블리·공개 컴포넌트 API·직렬화 필드·GUID와 씬·프리팹·샘플을 유지했습니다.

## 1. 경로 변경 정책과 Aggregate 오류

변경 전 소스에서 Aggregate(PathData, override)를 재생한 뒤 기하를 재빌드하면 Moving 대신 Ready로 바뀌는 현상을 단독 EditMode 테스트로 재현했습니다. Aggregate는 provider 이동 설정 스냅샷을 보관하지 않지만, 기존 갱신 코드는 그 스냅샷이 없다는 이유로 중단했습니다.

갱신 판단을 `PathSnapshotRefreshPolicy`로 분리했습니다. Aggregate는 명시적 override를 계속 사용하고, 해당 provider의 기하 변경은 기존 로컬 진행률에 반영합니다. 이벤트 정의 변경과 provider 무효화에 대한 중단은 유지합니다.

| 변경 | Single | Aggregate | Sequence |
|---|---|---|---|
| 기하·길이 변경 | 로컬 진행률·PlaybackId 유지 | override·진행률·PlaybackId 유지 | 구간 로컬 진행률·PlaybackId 유지, 전체 거리 비율 재계산 |
| provider 기본 이동 설정 변경 | 중단 | 무시하고 override 유지 | 실제 descriptor 이동 설정이 바뀌는 경우 중단 |
| 고정 runtime descriptor의 provider 기본값 변경 | 해당 없음 | override 유지 | 고정 descriptor 설정 유지 |
| 이벤트 정의 변경 | 중단 | PathData 등 직접 이벤트 원본이 있으면 중단 | 구간 이벤트 정의가 바뀌면 중단 |
| 구간 순서·provider·실제 descriptor 설정 변경 | 해당 없음 | 기존 provider 샘플링 계약 유지 | 중단 |
| provider 준비 상태 무효화 | 중단 | 중단 | 중단 |
| 일시정지 중 변경 | Resume 후 Tick에서 위 정책 적용 | 동일 | 동일 |

MultiPath를 Aggregate로 재생하면 기존처럼 구간별 이벤트·이동 설정을 Sequence 방식으로 소비하지 않습니다. 연결되지 않은 구간을 자동 보정하지 않으며, 기하 변경 시 실제 이동 거리가 아닌 로컬 거리 비율을 유지합니다.

## 2. 진행 계산 분리

`PathProgressCalculator`는 컴포넌트를 생성하지 않고 시간·거리 진행, 소비한 프레임 시간과 곡선 정·역산을 계산합니다. `PathProgressStep`은 결과 값만 반환합니다. Single/Aggregate와 Sequence가 같은 계산을 사용합니다.

`PathFollower`는 기존 공개 API와 직렬화 필드를 그대로 유지하고 구간·Loop 전환, 이벤트 통과, 콜백 직후 재생 ID/버전 검사, 최종 위치·회전 및 CharacterController를 담당합니다. 한 프레임 전체를 먼저 계산한 뒤 이벤트를 일괄 실행하는 형태로 변경하지 않았습니다. 여러 구간을 통과하는 프레임과 경계 콜백에서 Pause하는 경우를 검증했습니다.

## 3. Queue 등록 수명과 차단 정책 분리

- `PathQueueLease`: 한 Manager 등록 토큰과 follower 제약 연결을 소유합니다. 해제 중 콜백이 새 등록을 획득하면 이전 토큰만 해제합니다.
- `PathQueueHoldPolicy`: 수동 차단·외부 정지·Manager 결과, 재출발 지연, hysteresis, 속도 보간을 계산합니다.
- `QueuedPathFollower`: 기존 공개 API와 이벤트를 유지하고 두 객체를 실제 Follower 및 Manager에 연결합니다.

Queue 무효화 콜백에서 다른 Manager에 새 재생을 등록했을 때 새 등록이 남는 경우까지 검증했습니다. ForceBlock과 Pause의 기존 등록 수명 차이도 유지합니다.

## 4. 이벤트와 에디터 변경 감지

`PathEventDefinitionValidator`에 기존 이벤트 그래프 검증을 옮기고 `PathScalarAdjustment`에 재생 ID에 묶인 속도·Duration 보간을 옮겼습니다. 기존 SO 필드, 속도 0 및 Duration 9999의 Pause 해석, terminal Resume 규칙, 이벤트 수신기 이후 재진입 검사와 시간 기준은 유지했습니다.

`PathEditorChangeMonitor`가 0.05초 간격 갱신과 Hierarchy·provider/Registry 속성·Undo/Redo·리로드 구독을 공유합니다. Transform 이동은 위치 스냅샷을 무효화하지 않습니다. Linker의 연결점 정렬 및 그룹 동기화 알고리즘은 그대로 유지합니다.

| 기존 컴포넌트 | 이전 줄 수 | 이후 줄 수 | 추출한 책임 |
|---|---:|---:|---|
| PathFollower | 1551 | 1447 | 갱신 정책, 진행·곡선 계산 |
| QueuedPathFollower | 1054 | 992 | 등록 수명, 차단·재출발 계산 |
| PathEventHandler | 777 | 514 | 그래프 검증, 값 보간 |
| MultiPathAutoLinker | 433 | 363 | 에디터 구독·갱신·변경 감지 |
| PathWaypointSyncLinker | 375 | 302 | 에디터 구독·갱신·변경 감지 |

## 5. 실행 검증

초기 현재 에디터 테스트 실행은 새 테스트를 시작하지 못했습니다. 에디터 로그에서 다른 모듈의 AssetFileSnapshot 컴파일 오류도 확인해 TransformPath 소스와 자산만 복사한 별도 Unity 프로젝트로 검증을 분리했습니다. 이후 현재 에디터의 컴파일 오류 로그 0건을 확인했지만 별도 테스트 작업이 실행 중이어서 해당 작업을 중단하거나 중복 실행하지 않았습니다.

최종 검증 복사본의 C#/asmdef는 현재 TransformPath와 바이트 단위로 일치합니다. 그래픽 EditMode 결과는 98/98이며, 그중 기능·UI가 91개, 성능 진단이 7개입니다. 성능 진단의 할당량 계측 보호를 보완한 뒤 해당 7개를 별도로 다시 실행해 모두 통과했습니다. PlayMode는 아래 5개가 통과했습니다.

- 기존 Showcase의 Normal·MultiPath·Queue 실행과 Queue Pause/Resume
- 실제 재생 중 비활성화 시 CharacterController 복원
- 자연 완료 시 최종 위치 및 CharacterController 복원
- 실제 프레임에서 Aggregate MultiPath의 마지막 구간 통과
- Aggregate 기하·기본 설정 변경 시 override 유지, Pause 동안 위치 유지, Resume 후 새 기하 적용

에디터 UI 9개는 원본 CustomEditor를 임시 EditorWindow에 표시하고 Unity 입력 이벤트와 SerializedObject/Undo로 실행했습니다. 자동 연결, 웨이포인트 그룹, 목록 및 멤버 교체, Undo/Redo가 통과했습니다. OS 포인터로 Scene View 핸들을 드래그한 검증은 포함하지 않습니다.

회귀 테스트는 기존 50개에 변경 정책·공통 계산·Queue·보간 32개를 추가했습니다. Test Runner에서 `TransformPathRefactoringTests`를 선택하면 새 기능 회귀를 실행할 수 있습니다. `TransformPathEditorUiTests`는 그래픽 에디터가 필요하며 `TransformPathPerformanceProbes`에는 하드웨어 의존 시간 임계값이 없습니다.

## 6. 성능 진단과 남은 최적화 경계

아래 값은 고정 진행률의 경량 가짜 대상, 워밍업 16회, 5개 표본 중앙값입니다. Driver와 Queue는 각각 128회, Linear 재빌드는 각 32회, 곡선 계산은 4096회 반복했습니다. 실제 Follower의 Transform·이벤트·물리 비용이나 기기 FPS를 측정한 수치가 아닙니다.

| 대상 수 | Driver Tick | Queue Tick |
|---:|---:|---:|
| 100 | 0.0108 ms | 0.0437 ms |
| 500 | 0.0491 ms | 0.1935 ms |
| 1000 | 0.1006 ms | 0.3920 ms |

Linear 500분할 재빌드는 제어점 4·16·64개에서 각각 약 0.0425·0.0687·0.1668 ms, Queue 상한을 포함한 TimeBased 곡선 진행 계산은 약 0.992 μs였습니다. 기존 알고리즘을 유지했으며 전후 성능 향상률을 주장하지 않습니다.

이 Unity Mono 환경에서는 `GC.GetAllocatedBytesForCurrentThread`가 알려진 1 KiB 할당에도 증가하지 않았습니다. 할당량은 측정 불가로 기록했고 0 할당으로 판단하지 않았습니다. 실제 경로 재빌드는 결과 배열을 새로 발행합니다.

Queue 정렬, Linear 재빌드의 반복 구간 검색과 곡선 역산은 사용 규모에 따른 추가 측정 대상으로 남깁니다. 이번 수치만으로 동작·보간 정밀도·캐시 소유권을 바꾸는 최적화는 적용하지 않았습니다.

기계 판독용 결과와 보호·소스 체크섬은 [RefactoringValidation.json](RefactoringValidation.json)에 있습니다.
