#!/usr/bin/env python3
"""Paired before/after launches. Never runs a Player and a bake process concurrently."""
import argparse
import ctypes
import fcntl
import json
import os
from pathlib import Path
import plistlib
import re
import subprocess
import time

from run_player_benchmark import telemetry, validate

UNITY = '/Applications/Unity/Hub/Editor/2023.2.20f1/Unity.app/Contents/MacOS/Unity'
ORDERS = [('before', 'after'), ('after', 'before'), ('after', 'before'), ('before', 'after')]
OFFSETS = [0, 4, 0, 4]
PROFILES = {'cpu': (3600, 1), 'gpu': (225, 64)}


def write(path, value):
    path.write_text(json.dumps(value, indent=2) + '\n')


def capture(work, output, profile, variant, pair):
    folder = output / 'runtime' / profile / f'pair-{pair + 1:02d}' / variant
    if (folder / 'validated.json').exists():
        validate(folder, PROFILES[profile][0], 4, -1, True)
        return
    folder.mkdir(parents=True, exist_ok=False)
    app = work / variant / 'Build' / (profile + '.app')
    executable = plistlib.loads((app / 'Contents/Info.plist').read_bytes())['CFBundleExecutable']
    args = ['--optimize-benchmark', str(folder), '--optimize-benchmark-rounds', '4',
            '--optimize-benchmark-cubes', str(PROFILES[profile][0]),
            '--optimize-benchmark-warmup-seconds', '2', '--optimize-benchmark-precondition-seconds', '10',
            '--optimize-benchmark-order-offset', str(OFFSETS[pair]),
            '-logFile', str(folder / 'player.log'), '-screen-fullscreen', '0', '-screen-width', '1920', '-screen-height', '1080']
    if pair == 3:
        args.append('--optimize-benchmark-screenshots')
    resource_path = folder.parent / (variant + '-resource.txt')
    command = ['/usr/bin/time', '-l', '-o', str(resource_path),
               str(app / 'Contents/MacOS' / executable), '-batchmode'] + args + ['--optimize-benchmark-offscreen', '--optimize-benchmark-drain-gpu']
    before = telemetry()
    rss_samples = []; player_pid = None; last_activate = 0; last_pid_lookup = 0
    started = time.monotonic()
    print('RUNTIME', profile, pair + 1, variant, flush=True)
    process = subprocess.Popen(command, stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
    try:
        while process.poll() is None:
            elapsed = time.monotonic() - started
            if elapsed > 600: raise TimeoutError(folder)
            if elapsed - last_pid_lookup >= 1 and player_pid is None:
                found = subprocess.run(['pgrep', '-f', re.escape(str(app / 'Contents/MacOS' / executable)) + '.*' + re.escape(str(folder))], capture_output=True, text=True)
                player_pid = next((int(pid) for pid in found.stdout.split() if int(pid) != process.pid), None)
                last_pid_lookup = elapsed
            if player_pid:
                rss = resident_bytes(player_pid)
                if rss: rss_samples.append((elapsed, rss))
            time.sleep(0.02)
        if process.returncode: raise RuntimeError(f'Player failed: {folder}')
    except BaseException:
        # Kill only the executable with this unique capture directory in its arguments.
        matched = subprocess.run(['pgrep', '-f', re.escape(str(app / 'Contents/MacOS' / executable)) + '.*' + re.escape(str(folder))], capture_output=True, text=True)
        for pid in matched.stdout.split():
            try: os.kill(int(pid), 15)
            except ProcessLookupError: pass
        raise
    result = validate(folder, PROFILES[profile][0], 4, -1, True)
    result.update({'variant': variant, 'profile': profile, 'pair': pair, 'elapsedSeconds': time.monotonic() - started,
                   'telemetryBefore': before, 'telemetryAfter': telemetry(), 'loadAverage': os.getloadavg()})
    import csv
    with (folder / 'frames.csv').open() as stream: rows = list(csv.DictReader(stream))
    cpu_valid = sum(float(row['cpu_frame_ms']) > 0 for row in rows) / len(rows)
    if cpu_valid < 0.99: raise ValueError(f'CPU frame timing validity is too low: {cpu_valid}')
    peak = int(re.search(r'(\d+)\s+maximum resident set size', resource_path.read_text()).group(1))
    result.update({'cpuTimingValidFraction': cpu_valid, 'processPeakRssBytes': peak, 'sampledPeakRssBytes': max(v for _, v in rss_samples), 'rssSampleCount': len(rss_samples), 'rssSamplingIntervalSeconds': 0.02, 'rssMonitoredPid': player_pid})
    write(folder / 'rss-samples.json', rss_samples)
    write(folder / 'validated.json', result)
    print('DONE', profile, pair + 1, variant, round(result['elapsedSeconds'], 2), flush=True)
    time.sleep(5)


def resident_bytes(pid):
    # macOS sys/proc_info.h: proc_taskinfo begins with virtual_size, resident_size (uint64).
    buffer = ctypes.create_string_buffer(256)
    size = LIBPROC.proc_pidinfo(pid, 4, 0, buffer, len(buffer))
    return ctypes.c_uint64.from_buffer(buffer, 8).value if size >= 16 else None


def bake(work, output, profile, variant, pair):
    folder = output / 'bake' / profile / f'pair-{pair + 1:02d}' / variant
    if (folder / 'validated.json').exists():
        return
    folder.mkdir(parents=True, exist_ok=False)
    cubes, subdivisions = PROFILES[profile]
    command = ['/usr/bin/time', '-l', '-o', str(folder / 'resource.txt'), UNITY, '-batchmode', '-nographics', '-job-worker-count', '4',
               '-projectPath', str(work / variant / 'Project'), '-executeMethod', 'Common.OptimizeTool.Samples.Editor.RefactoringComparisonHarness.Bake',
               '--ab-output', str(folder), '--ab-cubes', str(cubes), '--ab-subdivisions', str(subdivisions),
               '-quit', '-logFile', str(folder / 'unity.log')]
    before = telemetry(); samples = []; last_phase = ''; pid = None
    print('BAKE', profile, pair + 1, variant, flush=True)
    started = time.monotonic(); process = subprocess.Popen(command, stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
    while process.poll() is None:
        if time.monotonic() - started > 600:
            process.terminate(); raise TimeoutError(folder)
        try:
            phase = json.loads((folder / 'phase.json').read_text()); pid = phase['pid']; last_phase = phase['phase']
        except (FileNotFoundError, json.JSONDecodeError):
            pass
        if pid and last_phase == 'baking':
            rss = resident_bytes(pid)
            if rss: samples.append((time.monotonic() - started, rss))
        time.sleep(0.01)
    if process.returncode:
        raise RuntimeError(f'Bake failed: {folder}')
    result = json.loads((folder / 'bake.json').read_text())
    resource = (folder / 'resource.txt').read_text()
    peak = int(re.search(r'(\d+)\s+maximum resident set size', resource).group(1))
    result.update({'variant': variant, 'profile': profile, 'pair': pair, 'processPeakRssBytes': peak,
                   'bakeSampledPeakRssBytes': max(v for _, v in samples), 'bakeFirstObservedRssBytes': samples[0][1],
                   'sampleCount': len(samples), 'samplingIntervalSeconds': 0.01,
                   'telemetryBefore': before, 'telemetryAfter': telemetry()})
    write(folder / 'rss-samples.json', samples); write(folder / 'validated.json', result)
    print('DONE BAKE', profile, pair + 1, variant, round(result['totalMs'], 2), round(peak / 1048576, 2), flush=True)
    time.sleep(3)


LIBPROC = ctypes.CDLL('/usr/lib/libproc.dylib')
LIBPROC.proc_pidinfo.argtypes = [ctypes.c_int, ctypes.c_int, ctypes.c_uint64, ctypes.c_void_p, ctypes.c_int]
LIBPROC.proc_pidinfo.restype = ctypes.c_int


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--work', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--phase', choices=['runtime', 'bake', 'all'], default='all')
    args = parser.parse_args(); args.work = args.work.resolve(); args.output = args.output.resolve()
    args.output.mkdir(parents=True, exist_ok=True)
    lock = open(Path('/tmp') / 'optimizetool-player-benchmark.lock', 'a+')
    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    for phase in (['runtime', 'bake'] if args.phase == 'all' else [args.phase]):
        for profile in PROFILES:
            for pair, order in enumerate(ORDERS):
                for variant in order:
                    (capture if phase == 'runtime' else bake)(args.work, args.output, profile, variant, pair)
    print('COMPARISON COMPLETE', flush=True)


if __name__ == '__main__':
    main()
