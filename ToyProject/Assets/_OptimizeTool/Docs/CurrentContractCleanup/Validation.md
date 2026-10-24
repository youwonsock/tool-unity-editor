# OptimizeTool 현재 계약 정리 — 2026-10-24

이 작업은 README를 현재 구현에 맞추고, 이전 API·데이터 형식을 유지하기 위한 실행 경로를 제거합니다. 호환용 API의 삭제는 의도한 변경입니다. 과거 측정 보고서·로그·압축 소스 스냅샷은 당시 결과의 근거로 보존합니다.

## 제거한 코드와 현재 사용법

| 제거 대상 | 현재 경로 |
| --- | --- |
| MeshCombiner / BackfaceCullMeshBaker의 `Init`, `Release`, `IsInitialized`, `IsFaulted`와 관련 상태·중복 검증 | `BuildMeshes`, `CombineAndSaveMeshes`, `BakeBackFaceCullingMesh`가 호출마다 검증하고 예외를 반환합니다. 설정을 고친 뒤 같은 API를 재호출합니다. |
| MeshBuildResult의 `Materials` 호환 투영 | `Parts[i].Mesh`와 `Parts[i].Materials`로 대응 관계를 조회합니다. |
| PhysicsRecorder의 `ResultClip`, `ResultPrefab`, `IsSaveCompleted` | `Result` 하나에서 `Clip`, `Prefab`, `Reference`, `Succeeded`, `Error`, `Metrics`를 조회합니다. |
| 초기화 실패 결과에 이전 성공 에셋을 다시 게시하던 처리 | 실패 결과는 실패 정보만 게시하고 Clip·Prefab을 비웁니다. 이전에 저장된 파일과 호출자가 보관한 성공 결과는 유지합니다. |
| 원본 상태 목록이 없는 호출을 허용하던 씬 출력 시그니처·null 분기 | MeshCombiner가 현재 상태 목록을 복제해 MeshSceneOutput에 전달합니다. 초기 실행은 빈 목록으로 시작합니다. |
| 기준 데이터 없는 녹화본의 Prefab 단독 재생·오차 생략 | 같은 녹화의 `.anim`·`.prefab`·`.reference.asset`이 필요합니다. 기준 데이터가 없으면 인스턴스를 정리하고 실패를 표시하며, 새 녹화로 복구합니다. |
| 계측 v1/버전 누락 결과 읽기, 구형 재개 매니페스트의 누락 필드 기본값 | v2의 실행 ID·클레임·완료 상태·CSV/메타데이터 SHA-256을 검증합니다. 재개에는 동일한 실행 옵션이 모두 필요합니다. |
| 렌더 파이프라인 속성의 reflection 및 기본값 대체 | Unity 2023.2 / URP 16의 UniversalRenderPipelineAsset에서 실제 설정을 읽습니다. |

상태·자원을 실제로 소유하는 PhysicsRecorder와 샘플 UI의 초기화/해제 수명주기는 현재 기능입니다. Unity Animation의 `legacy = true`와 내장 `LegacyRuntime.ttf`도 녹화 출력·재생·UI에 사용하는 엔진 기능으로 남습니다. Game View reflection은 Unity의 비공개 API에 접근하기 위한 현재 어댑터이며 이전 버전별 분기는 없습니다.

독립 마이그레이션 스크립트나 직렬화 이름 별칭은 제작용 코드에 없습니다. 구형 두 모드 컨트롤러는 이 작업 시작 전에 삭제된 상태였습니다. 테스트 이름과 README에서도 현재 사용법을 기준으로 설명합니다.

## 유지한 동작과 데이터

- 메시 저장의 경로·충돌 사전 검사, 기존 에셋 GUID, 저장 콜백의 변경 반영과 예외 롤백을 유지합니다.
- 재빌드는 유지되는 Renderer의 원래 상태 기록을 이어받고, 제거된 입력만 복원합니다. 반복 Undo/Redo, 원래 비활성 상태, 수동 변경, 실패 중단 후 재시도를 검증합니다.
- 녹화의 시작·정지 구간·종료 시각, 에셋 세 개의 저장/실패 복원과 재컴파일·비활성화·파괴 정리를 유지합니다.
- 원본 상태 목록은 새 작업에서 빈 목록으로 시작하며, 샘플 프리팹에는 현재 녹화 기준 에셋 참조를 명시합니다. 이 참조의 저장 외에 기존 오브젝트 ID·GUID·계층 참조를 변경하지 않습니다. 기준 데이터는 Player에서도 직렬화된 참조로 사용할 수 있어야 합니다.
- 코어 제작용 DLL의 공개 멤버는 **128→116개**로, 위 호환 멤버 **12개만 삭제**됐고 추가는 없습니다. 코어 직렬화 필드 **47개**의 이름·형식 집합은 같습니다. [API 차이](CoreApiAudit.txt)는 보관된 동일 코어 소스의 기준 DLL과 현재 컴파일 결과를 비교했습니다.

## 검증 자료

**EditMode 103/103·PlayMode 38/38·Python 10/10** 통과했습니다. 마지막 상태 표시·검증 문구 변경 뒤 관련 PhysicsDemo PlayMode **22/22**도 통과했으며, 최종 소스가 격리 검증 스냅샷과 같은 것을 확인했습니다. 실행 상태와 표본 수는 [Validation.json](Validation.json)에 기록합니다. 전체 NUnit 결과는 `EditMode.xml`, `PlayMode.xml`, 로그는 gzip으로 보존합니다. 테스트는 독립 프로젝트에서 실행했으며 원래 Editor의 씬·Play 상태를 바꾸지 않습니다.

- `OptimizeToolContractTests`: 제거한 API의 부재, 현재 녹화 수명주기, 프리팹의 직렬화 참조 및 기본 녹화 클립/기준 데이터 연결.
- `MeshRefactoringTests` / `MeshOptimizationTests`: 실패 후 직접 재시도, 저장·재임포트, 콜백, 원본 상태와 Undo/Redo, 프리팹 인스턴스, 중단 롤백.
- `OptimizeToolPhysicsDemoTests`: 기준 데이터 누락 시 실패·인스턴스 정리·기존 파일 보존, RESET/모드 전환 후에도 거부, 재녹화로 복구.
- 나머지 기존 녹화·UI·씬 재로드·계측 중단·렌더 회귀 검사도 함께 실행합니다.
- Python 10개: v2 정상/압축 입력, 변조·중복·미완료·실행 ID 불일치·비정상 시간값 거부, v1/버전 누락/미지원 버전 및 클레임 누락 거부.

한 차례 PlayMode 배치 실행은 NUnit 결과를 만들기 전 Mono/Burst 네이티브 컴파일 스택에서 종료됐습니다. 실패 로그와 종료 코드 −10을 보존하고 동일 명령으로 다시 실행했습니다. 실패 실행을 통과 결과로 집계하지 않습니다.

이번 단계는 기능·계약 정리 검증입니다. 새 Player 빌드나 성능 재측정은 수행하지 않았습니다. README의 저장 54.05%·재빌드 32.17% 단축 수치는 이전 저장·재빌드 수정 당시의 별도 실측이며, 이번 코드 삭제의 새 성능 성과로 계산하지 않습니다.

## 추가 검토와 실제 클릭 검증

구형 계약을 제거한 작업본을 다시 검토하면서 기본 Showcase Prefab의 필수 기준 데이터 참조가 비어 있음을 확인했다. Editor에서는 같은 이름의 sidecar 조회로 가려질 수 있지만 Player에서는 이 조회를 사용할 수 없다. 현재 저장된 클립에 대응하는 `PhysicsRecordingReference`를 Prefab에 직렬화하고, Editor 조회 없이 해당 참조·클립 연결을 검사하는 테스트를 추가했다. 이번 단계에서 Player를 새로 빌드하거나 실행한 것은 아니다.

기준 데이터가 없는 실패에도 이전 녹화로 COMPARE할 수 있다고 안내하던 상태 표시를 수정했다. 저장본 자체가 없는 경우에는 기존의 RECORD 재시도 안내를 유지하고, Prefab만 있고 기준 데이터가 없으면 새 녹화를 요구한다. 첫 전체 실행에서 저장본 없는 실패 안내에 관한 PlayMode 검사 1개가 실패해 이를 수정했다. 중간 실패 결과는 최종 통과 결과와 분리했다.

| 최종 재실행 | 결과 |
| --- | --- |
| EditMode | **103/103**, 실패·건너뛰기 0 |
| PlayMode | **38/38**, 실패·건너뛰기 0 |
| Python v2 검증 | **10/10** |
| 작업본 대조 | 테스트한 스크립트·테스트·메타·asmdef 147개 모두 일치 |

실제 OptimizeToolSample에서는 CGEvent 마우스 클릭 **5회**를 기록했다. PHYSICS 진입 후 기준 데이터 없는 임시 Prefab을 지정하고 COMPARE로 오류·인스턴스 정리를 확인했다. RECORD로 0.8초 녹화를 새로 저장해 세 에셋을 만들고 원본 관찰값 **42/42개** 비교를 완료했다. RESET 후 Ready 복귀와 다시 누른 COMPARE의 Finished 도달도 확인했다. 임시 녹화만 썼으며 기본 기록 에셋은 갱신하지 않았다. 재컴파일로 관찰이 끊긴 첫 세션과 입력이 전달되지 않은 시도는 완료 클릭에서 제외했다.

기준 데이터 누락에 따른 예상 예외 1개를 기록했으며 그 외 오류·경고는 없었다. 예상 로그 보관 후 콘솔을 정리했고 최종 Editor는 Play 종료·씬 dirty=false·컴파일 중 아님·Game View 최대화 해제·기존 Enter Play Mode 옵션 유지 상태다. 임시 에셋과 테스트 복사본을 제거했다. 기존 씬·설정·저장 데이터·메타·asmdef 등 180개를 대조했고, Prefab의 오브젝트 ID 169개는 같으며 실제 내용 차이는 필수 기준 데이터 참조 1곳뿐이다.

README는 메시 도구의 무상태 호출, `Result`·`Parts` 조회, 필수 녹화 에셋 세 개와 v2만 지원하는 분석기를 기준으로 검토했다. 구형 계약의 검증 이력과 현재 결과를 구분했고 로컬 문서·이미지 링크의 누락은 없다. Unity의 Legacy Animation과 내장 폰트는 현재 엔진 기능으로 남는다.

최종 결과는 [Review.json](Review.json), [EditMode XML](ReviewEditMode.xml), [PlayMode XML](ReviewPlayMode.xml)에 보관했다. 원본 로그·실패 실행·소스 해시·검증 스크립트는 `ToyProject/Logs/OptimizeToolCurrentContractTests/`, 실제 클릭·화면·임시 저장 검사는 `ToyProject/Logs/OptimizeToolCurrentContractReview/`에 있다.
