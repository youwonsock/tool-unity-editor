# tool-unity-editor

Unity 프로젝트에서 쓰던 기능을 독립 모듈로 분리하고, `ToyProject`에서 동작 예제를 제공하는 저장소입니다.

| 모듈 | 내용 |
| --- | --- |
| [FlowField](./ToyProject/Assets/_FlowField/README.md) | Goal과 장애물로 방향과 속도 배율을 계산하는 방향장 (Surface2D, Volume3D) |
| [TransformPath](./ToyProject/Assets/_TransformPath/README.md) | Transform 제어점 경로의 재생, 연결 경로, Queue 이동 |
| [OptimizeTool](./ToyProject/Assets/_OptimizeTool/README.md) | 서로 독립된 두 시스템: Mesh 결합·컬링 베이크(Editor)와 물리 움직임을 Animation Prefab으로 저장하는 Physics Recorder |

## 실행

- Unity 2023.2.20f1 / URP로 `ToyProject`를 엽니다.
- 샘플 씬은 `FlowFieldSample`, `FlowFieldVolume3DTest`, `TransformPathSample`, `OptimizeToolSample`입니다. 상태와 조작법은 화면의 Overview Board가 보여 주고, `FlowFieldVolume3DTest`는 OnGUI 패널이 보여 줍니다.
- FlowField, TransformPath, OptimizeTool은 다른 프로젝트로 옮기기 쉽게 시스템·에디터 어셈블리만 `Script/`에 두고, 샘플(씬·프리팹·설정 포함)은 `Samples/`에 둡니다. 이 저장소에서만 쓰는 도구는 `Dev/`에 있습니다: FlowField의 회귀 테스트, TransformPath의 EditMode 테스트, OptimizeTool의 벤치마크입니다.
- OptimizeTool은 Mesh 최적화와 Physics Recorder를 `Script/` 안에서 따로 옮길 수 있습니다. 어느 쪽을 옮기든 공용 Editor 어셈블리 `Script/Shared`도 함께 옮깁니다.
- 세 도구 모두 코드 메시지(예외, 로그, Inspector 문구·Tooltip·Header·HelpBox, XML·코드 주석, 샘플 화면 문구)는 모두 영어입니다. README와 TechnicalNotes 본문은 한국어이고, 인용하는 UI·로그 문구는 영어 원문 그대로 적습니다.

## 명령줄 테스트

`Tools/run-transformpath-tests.sh`는 TransformPath EditMode 테스트를 batchmode로 돌립니다. 작업 트리의 스냅샷을 별도 Git worktree에서 열므로 Editor를 연 채로 돌릴 수 있습니다. Unity Hub가 켜져 있어야 하며, 옵션과 시간 제한은 스크립트 첫머리에 있습니다.
