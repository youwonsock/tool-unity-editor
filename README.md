# tool-unity-editor

Unity 프로젝트에서 사용하던 기능을 분리해 보관하고, `ToyProject`에서 동작 예제를 제공하는 저장소입니다.

## Unity 프로젝트

- Unity 2023.2.20f1 / URP 기반 `ToyProject`
- 샘플 씬: `FlowFieldSample`, `TransformPathSample`, `OptimizeToolSample`

각 쇼케이스는 `Assets/_기능명/Script`, `Scene`, `Prefab`, `Settings`로 독립 보관하며, 실행 가능한 Demo 루트는 해당 기능의 `Prefab`에 함께 저장합니다. 샘플·기능 검증 클래스는 각 `Script/Temp`에 모아 시스템 코드와 분리합니다.

## 시스템 문서

- FlowField — [README](./ToyProject/Assets/_FlowField/README.md)
- OptimizeTool — [README](./ToyProject/Assets/_OptimizeTool/README.md)
- TransformPath — [README](./ToyProject/Assets/_TransformPath/README.md)
