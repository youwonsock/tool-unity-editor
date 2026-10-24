from pathlib import Path
import shutil
import sys

WORK = Path(__file__).resolve().parent
SCRIPTS = WORK / 'Project/Assets/_OptimizeTool/Script'
FILES = ['MeshAssetTransaction.cs', 'MeshSceneOutput.cs', 'MeshCombiner.cs']

def prepare(variant, probe=False):
    source = WORK / ('source-before' if variant == 'baseline' else 'source-fixed')
    for name in FILES:
        shutil.copy2(source / name, SCRIPTS / name)
    shutil.copytree(WORK / 'source-fixed/Tests', SCRIPTS / 'Tests', dirs_exist_ok=True)
    shutil.copy2(WORK / ('Harness-probed.cs' if probe else 'Harness-unprobed.cs'),
                 SCRIPTS / 'Samples/Editor/RefactoringComparisonHarness.cs')
    helper = SCRIPTS / 'BakeProbe.cs'
    if not probe:
        helper.unlink(missing_ok=True)
        return
    shutil.copy2(WORK / 'BakeProbe.cs', helper)
    if variant == 'baseline':
        for name in FILES:
            shutil.copy2(WORK / 'probed-baseline' / name, SCRIPTS / name)
        return
    path = SCRIPTS / 'MeshAssetTransaction.cs'
    text = path.read_text()
    replacements = {
      'Undo.RegisterCompleteObjectUndo(existing[i], undoName);': 'using (BakeProbe.Measure("AssetUndo")) Undo.RegisterCompleteObjectUndo(existing[i], undoName);',
      'EditorUtility.CopySerialized(staged[i], existing[i]);': 'using (BakeProbe.Measure("Copy")) EditorUtility.CopySerialized(staged[i], existing[i]);',
      'saved[i] = Object.Instantiate(staged[i]);': 'using (BakeProbe.Measure("Clone")) saved[i] = Object.Instantiate(staged[i]);',
      'AssetDatabase.CreateAsset(saved[i], normalized[i]);': 'using (BakeProbe.Measure("CreateAsset")) AssetDatabase.CreateAsset(saved[i], normalized[i]); BakeProbe.Bytes("InitialFileBytes", new FileInfo(normalized[i]).Length);',
      'apply?.Invoke(saved);': 'using (BakeProbe.Measure("ApplyScene")) apply?.Invoke(saved);',
      'foreach (Mesh mesh in saved) AssetDatabase.SaveAssetIfDirty(mesh);': 'foreach (Mesh mesh in saved) using (BakeProbe.Measure("SaveDirty")) AssetDatabase.SaveAssetIfDirty(mesh);',
      'mesh.name = Path.GetFileNameWithoutExtension(path);': 'using (BakeProbe.Measure("Readability")) { mesh.name = Path.GetFileNameWithoutExtension(path);',
      '{ readable.boolValue = false; serialized.ApplyModifiedPropertiesWithoutUndo(); }': '{ readable.boolValue = false; serialized.ApplyModifiedPropertiesWithoutUndo(); } }',
    }
    for before, after in replacements.items():
        assert text.count(before) == 1, before
        text = text.replace(before, after)
    path.write_text(text)
    path = SCRIPTS / 'MeshSceneOutput.cs'
    text = path.read_text().replace('Undo.RegisterCompleteObjectUndo(renderer,', 'using (BakeProbe.Measure("RendererState")) { BakeProbe.Bytes("RendererActualChanges", 1);\n            Undo.RegisterCompleteObjectUndo(renderer,')
    text = text.replace('PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);', 'PrefabUtility.RecordPrefabInstancePropertyModifications(renderer); }')
    path.write_text(text)
    path = SCRIPTS / 'MeshCombiner.cs'
    text = path.read_text().replace('Undo.RegisterCompleteObjectUndo(this,', 'using (BakeProbe.Measure("OwnerUndo")) Undo.RegisterCompleteObjectUndo(this,')
    text = text.replace('PrefabUtility.RecordPrefabInstancePropertyModifications(this);', 'using (BakeProbe.Measure("OwnerPrefab")) PrefabUtility.RecordPrefabInstancePropertyModifications(this);')
    path.write_text(text)

if __name__ == '__main__':
    prepare(sys.argv[1], '--probe' in sys.argv)
