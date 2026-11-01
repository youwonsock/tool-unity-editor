# tool-unity-editor

Unity 프로젝트에서 사용하던 기능을 분리해 보관하고, `ToyProject`에서 동작 예제를 제공하는 저장소입니다.

## Unity 프로젝트

- Unity 2023.2.20f1 / URP 기반 `ToyProject`
- 샘플 씬: `FlowFieldSample`, `FlowFieldVolume3DTest`, `TransformPathSample`, `OptimizeToolSample` (빌드 설정 순서, 첫 씬은 `FlowFieldSample`)

각 쇼케이스는 `Assets/_기능명/Script`, `Scene`, `Prefab`, `Settings`로 독립 보관하며, 실행 가능한 Demo 루트는 해당 기능의 `Prefab`에 함께 저장합니다. 샘플 코드는 `Script/Samples`, 자동 회귀 검사는 `Script/Tests`에서 시스템 코드와 분리합니다.

세 샘플 씬은 월드 공간 UGUI Overview Board를 포함해 상태, 주요 수치, 현재 모드와 조작법을 동시에 보여줍니다. OptimizeTool은 미리 생성한 메시 결과를 비교하고, PHYSICS 모드에서 RECORD를 누를 때 실제 물리 움직임을 새 AnimationClip·Prefab·원본 기준 데이터로 저장합니다. 단순 Play 진입이나 모드 전환으로는 기록 에셋을 저장하지 않습니다. FlowField/TransformPath도 모든 참조를 직렬화 필드로 구성합니다.

## 테스트 실행

```bash
Tools/run-tests.sh
```

현재 작업 트리의 스냅샷을 별도 Git worktree에서 실행하므로 Unity Editor를 연 채로 돌릴 수 있습니다. EditMode, PlayMode, OptimizeTool Python 테스트를 차례로 실행하며, 옵션은 스크립트 첫머리에 있습니다. 그래픽 Editor가 필요한 `TransformPathEditorUiTests`는 여기서 건너뛰므로 Editor의 Test Runner에서 실행합니다.

GitHub Actions는 쓰지 않습니다. Unity 라이선스와 GPU가 있는 개발 Mac에서 push 전에 검사합니다. 저장소마다 한 번 켭니다.

```bash
git config core.hooksPath Tools/hooks
```

`pre-push` 훅은 push하는 커밋을 그대로 꺼내 전체 테스트를 실행하고, 실패하면 push를 막습니다. `ToyProject`와 `Tools`가 바뀌지 않은 push는 건너뜁니다. 작업 중인 파일은 검사에 섞이지 않으며, 급할 때는 `git push --no-verify`로 건너뜁니다.

실제 Player에서의 프레임 시간과 샘플 씬 동작은 별도로 측정합니다. Player 창이 열리고, 결과는 CSV로 남습니다.

```bash
Tools/run-player-probe.sh
```

## 시스템 문서

- FlowField — [README](./ToyProject/Assets/_FlowField/README.md)
- OptimizeTool — [README](./ToyProject/Assets/_OptimizeTool/README.md) · [저장·재빌드 성능 검증](./ToyProject/Assets/_OptimizeTool/Docs/Performance/PerformanceReport.md)
  - 현재 Mesh 요청 API, 녹화 원본 기준 비교와 v2 계측 결과 형식을 사용합니다. 구형 초기화·결과 별칭과 이전 데이터 호환 경로는 제거했습니다.
- TransformPath — [README](./ToyProject/Assets/_TransformPath/README.md)
