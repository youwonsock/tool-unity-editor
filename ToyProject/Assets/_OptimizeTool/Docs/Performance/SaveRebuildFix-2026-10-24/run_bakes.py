#!/usr/bin/env python3
"""Run the unchanged 3,600-source bake fixture in paired, fresh Unity processes."""
import ctypes
import fcntl
import hashlib
import json
from pathlib import Path
import re
import shlex
import shutil
import subprocess
import time
from prepare import WORK, SCRIPTS, FILES, prepare

UNITY = '/Applications/Unity/Hub/Editor/2023.2.20f1/Unity.app/Contents/MacOS/Unity'
LIBPROC = ctypes.CDLL('/usr/lib/libproc.dylib')
LIBPROC.proc_pidinfo.argtypes = [ctypes.c_int, ctypes.c_int, ctypes.c_uint64, ctypes.c_void_p, ctypes.c_int]
LIBPROC.proc_pidinfo.restype = ctypes.c_int

class ConcurrentBatchError(RuntimeError):
    pass

def write(path, value):
    path.write_text(json.dumps(value, indent=2) + '\n')

def telemetry():
    return {key: subprocess.run(command, capture_output=True, text=True, timeout=5).stdout.strip()
            for key, command in [('thermal', ['pmset', '-g', 'therm']), ('power', ['pmset', '-g', 'batt'])]}

def resident_bytes(pid):
    buffer = ctypes.create_string_buffer(256)
    size = LIBPROC.proc_pidinfo(pid, 4, 0, buffer, len(buffer))
    return ctypes.c_uint64.from_buffer(buffer, 8).value if size >= 16 else None

def active_batches(include_own=False):
    rows = []
    for line in subprocess.check_output(['ps', '-A', '-o', 'pid=,command='], text=True).splitlines():
        pid, _, command = line.strip().partition(' ')
        try: args = shlex.split(command)
        except ValueError: continue
        if not args or args[0] != UNITY or '-batchmode' not in args:
            continue
        own = '-projectPath' in args and Path(args[args.index('-projectPath') + 1]).resolve() == WORK / 'Project'
        if include_own or not own:
            rows.append({'pid': int(pid), 'command': command})
    return rows

def wait_for_batches():
    last_report = 0
    while True:
        rows = active_batches(include_own=True)
        if not rows: return
        if time.monotonic() - last_report >= 30:
            print('WAIT for active batch Editors:', [r['pid'] for r in rows], flush=True)
            last_report = time.monotonic()
        time.sleep(2)

def bake(variant, pair):
    folder = WORK / 'bake' / f'pair-{pair + 1:02d}' / variant
    if (folder / 'validated.json').exists():
        return
    wait_for_batches()
    folder.mkdir(parents=True, exist_ok=True)
    prepare(variant)
    command = ['/usr/bin/time', '-l', '-o', str(folder / 'resource.txt'), UNITY,
               '-batchmode', '-nographics', '-job-worker-count', '4', '-projectPath', str(WORK / 'Project'),
               '-executeMethod', 'Common.OptimizeTool.Samples.Editor.RefactoringComparisonHarness.Bake',
               '--ab-output', str(folder), '--ab-cubes', '3600', '--ab-subdivisions', '1',
               '-quit', '-logFile', str(folder / 'unity.log')]
    write(folder / 'command.json', command)
    sources = {name: hashlib.sha256((SCRIPTS / name).read_bytes()).hexdigest() for name in FILES}
    before = telemetry(); samples = []; last_phase = ''; pid = None; overlaps = []; last_check = 0
    print('START', pair + 1, variant, flush=True)
    start = time.monotonic()
    process = subprocess.Popen(command, stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
    while process.poll() is None:
        if time.monotonic() - start > 600:
            process.terminate(); raise TimeoutError(folder)
        if time.monotonic() - last_check >= 1:
            rows = active_batches()
            if rows: overlaps.append({'seconds': time.monotonic() - start, 'processes': rows})
            last_check = time.monotonic()
        try:
            phase = json.loads((folder / 'phase.json').read_text()); pid = phase['pid']; last_phase = phase['phase']
        except (FileNotFoundError, json.JSONDecodeError):
            pass
        if pid and last_phase == 'baking':
            rss = resident_bytes(pid)
            if rss: samples.append((time.monotonic() - start, rss))
        time.sleep(0.01)
    if process.returncode or not (folder / 'bake.json').exists():
        raise RuntimeError(f'Bake failed: {folder} exit={process.returncode}')
    result = json.loads((folder / 'bake.json').read_text())
    assert result['cubes'] == 3600 and result['subdivisions'] == 1
    assert [(m['vertices'], m['triangles'], m['culled']) for m in result['modes']] == [(86400, 43200, 0), (14400, 7200, 0), (28800, 14400, 2400)]
    peak = int(re.search(r'(\d+)\s+maximum resident set size', (folder / 'resource.txt').read_text()).group(1))
    result.update({'variant': variant, 'pair': pair + 1, 'processPeakRssBytes': peak,
                   'bakeSampledPeakRssBytes': max(v for _, v in samples),
                   'bakeFirstObservedRssBytes': samples[0][1], 'sampleCount': len(samples),
                   'samplingIntervalSeconds': 0.01, 'telemetryBefore': before, 'telemetryAfter': telemetry(),
                   'sourceHashes': sources, 'concurrentBatchEditors': overlaps})
    write(folder / 'rss-samples.json', samples); write(folder / 'validated.json', result)
    if overlaps:
        raise ConcurrentBatchError('Concurrent batch Editor observed; preserve and rerun the complete pair: ' + str(folder))
    assemblies = WORK / ('API-before' if variant == 'baseline' else 'API-after')
    assemblies.mkdir(exist_ok=True)
    for name in ['Common.OptimizeTool', 'Common.OptimizeTool.Samples', 'Common.OptimizeTool.Editor', 'Common.OptimizeTool.Samples.Editor']:
        shutil.copy2(WORK / 'Project/Library/ScriptAssemblies' / (name + '.dll'), assemblies / (name + '.dll'))
    print('DONE', pair + 1, variant, {key: round(result[key], 2) for key in ['saveMs', 'workflowInitialMs', 'workflowRebuildMs', 'totalMs']}, flush=True)
    time.sleep(3)

if __name__ == '__main__':
    lock = open('/tmp/optimizetool-player-benchmark.lock', 'a+')
    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    config = json.loads((WORK / 'config.json').read_text())
    for pair, order in enumerate(config['order']):
        attempt = 0
        while True:
            try:
                for existing in (WORK / 'bake' / f'pair-{pair + 1:02d}').glob('*/validated.json'):
                    if json.loads(existing.read_text()).get('concurrentBatchEditors'):
                        raise ConcurrentBatchError('Previously rejected capture: ' + str(existing))
                for variant in order:
                    bake(variant, pair)
                break
            except ConcurrentBatchError as error:
                attempt += 1
                parent = WORK / 'excluded-overlap'
                parent.mkdir(exist_ok=True)
                destination = parent / f'pair-{pair + 1:02d}-attempt-{time.time_ns()}'
                shutil.move(str(WORK / 'bake' / f'pair-{pair + 1:02d}'), destination)
                write(destination / 'exclusion.json', {'reason': str(error), 'policy': 'Rerun the entire matched pair regardless of performance values.'})
                print('REJECT COMPLETE PAIR', pair + 1, destination.name, flush=True)
    print('ALL COMPLETE', flush=True)
