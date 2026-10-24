#!/usr/bin/env python3
"""Run one visible macOS Player at a time and validate every complete capture."""
import argparse
import csv
import fcntl
import tempfile
import hashlib
import json
import os
from pathlib import Path
import plistlib
import re
import signal
import statistics
import sys
import subprocess
import time
from capture_validation import load_capture


def validate(folder, cubes, rounds, fps, offscreen=False):
    meta, rows, complete = load_capture(folder)
    assert complete['samples'] == rounds * 4 * 240 and complete['status'] == 'complete'
    assert len(rows) == complete['samples']
    if not offscreen: assert len({row['timing_timestamp'] for row in rows}) == len(rows)
    assert meta['editor'] is False and meta['developmentBuild'] is True
    assert meta['processArchitecture'].lower() == 'arm64', 'Player must run natively on Apple Silicon'
    assert meta['splashFinishedBeforeCapture'] is True and meta['startupSettleSeconds'] >= 3
    assert meta['minimumWarmupSeconds'] >= 0.25
    assert meta['sourceCubeCount'] == cubes and meta['rounds'] == rounds
    assert meta['targetFrameRate'] == fps
    assert (meta['cameraWidth'], meta['cameraHeight']) == (1920, 1080)
    assert meta['graphicsApi'] == 'Metal'
    assert meta['materialInstancing'] is False and meta['dynamicBatching'] is False
    if not offscreen: assert all(float(row['cpu_frame_ms']) > 0 for row in rows)
    gpu_key = 'metal_gpu_ms' if offscreen else 'gpu_frame_ms'
    assert all(float(row[gpu_key]) > 0 for row in rows), 'Invalid GPU timing'
    if offscreen:
        assert len({row['metal_frame_id'] for row in rows}) == len(rows)
        assert all(abs((float(row['metal_gpu_end_seconds']) - float(row['metal_gpu_start_seconds'])) * 1000 - float(row['metal_gpu_ms'])) < 0.00001 for row in rows)
    if offscreen:
        assert meta.get('offscreen') is True and meta.get('batchMode') is True
    else:
        assert all(row['focused'] == '1' for row in rows), 'Player lost focus'
    if not offscreen: assert all(float(row['width_scale']) == 1 and float(row['height_scale']) == 1 for row in rows)
    modes = {}
    for mode in range(4):
        data = [row for row in rows if int(row['mode']) == mode]
        assert len(data) == rounds * 240
        modes[str(mode)] = {key: statistics.median(float(row[key]) for row in data)
                            for key in ('draw_calls', 'cpu_frame_ms', gpu_key, 'wall_delta_ms')}
    return {'samples': len(rows), 'modes': modes, 'buildGuid': meta['buildGuid'],
            'gpuMetric': gpu_key, 'syncIntervals': sorted({row['sync_interval'] for row in rows})}


def telemetry():
    result = {}
    for name, command in [('thermal', ['pmset', '-g', 'therm']), ('power', ['pmset', '-g', 'batt'])]:
        item = subprocess.run(command, capture_output=True, text=True, timeout=5)
        result[name] = item.stdout.strip()
    return result


def main():
    capture_lock = open(Path(tempfile.gettempdir()) / 'optimizetool-player-benchmark.lock', 'a+')
    try: fcntl.flock(capture_lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError: raise RuntimeError('Another benchmark launcher is active; concurrent captures are forbidden.')
    parser = argparse.ArgumentParser()
    parser.add_argument('--app', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--cubes', nargs='+', type=int, default=[225, 900, 3600])
    parser.add_argument('--rounds', type=int, default=8)
    parser.add_argument('--repeats', type=int, default=3)
    parser.add_argument('--timeout', type=int, default=900)
    parser.add_argument('--screenshots', action='store_true')
    parser.add_argument('--resume', action='store_true', help='Reuse validated captures from the same Player executable; preserve rejected captures separately.')
    parser.add_argument('--fps', type=int, default=-1, help='Optional controlled frame rate (default uncapped).')
    parser.add_argument('--warmup-seconds', type=float, default=0.25)
    parser.add_argument('--precondition-seconds', type=float, default=0)
    parser.add_argument('--rotate-orders', action='store_true')
    parser.add_argument('--offscreen', action='store_true', help='Graphics-enabled batch Player renders to a fixed 1920x1080 RenderTexture without display or focus coupling.')
    parser.add_argument('--max-new-runs', type=int, help='Stop after this many new validated launches, for interleaved condition collection.')
    parser.add_argument('--drain-gpu', action='store_true', help='Diagnostic: synchronous one-pixel readback bounds inter-frame GPU overlap.')
    parser.add_argument('--cooldown', type=float, default=3)
    args = parser.parse_args()
    args.app = args.app.resolve(); args.output = args.output.resolve()
    args.output.mkdir(parents=True, exist_ok=True)
    with (args.app / 'Contents/Info.plist').open('rb') as stream:
        info = plistlib.load(stream)
    executable = args.app / 'Contents/MacOS' / info['CFBundleExecutable']
    architecture = subprocess.check_output(['file', '-b', str(executable)], text=True).strip()
    assert 'arm64' in architecture, architecture
    manifest = {'app': str(args.app), 'architecture': architecture,
                'executableSha256': hashlib.sha256(executable.read_bytes()).hexdigest(),
                'rounds': args.rounds, 'repeats': args.repeats, 'cubes': args.cubes, 'targetFrameRate': args.fps, 'runs': []}
    manifest.update({'warmupSeconds': args.warmup_seconds, 'preconditionSeconds': args.precondition_seconds, 'rotateOrders': args.rotate_orders, 'cooldownSeconds': args.cooldown, 'gpuQueueDrained': args.drain_gpu, 'offscreen': args.offscreen})
    manifest_path = args.output / 'launch-manifest.json'
    previous_runs = {}
    if args.resume and manifest_path.exists():
        previous = json.loads(manifest_path.read_text())
        for key in ('executableSha256', 'rounds', 'repeats', 'cubes', 'targetFrameRate', 'warmupSeconds', 'preconditionSeconds', 'rotateOrders', 'cooldownSeconds', 'gpuQueueDrained', 'offscreen'):
            assert key in previous and previous[key] == manifest[key], f'Cannot resume with missing or different {key}'
        previous_runs = {run['folder']: run for run in previous['runs']}
    new_runs = 0
    for repeat in range(args.repeats):
        shift = repeat % len(args.cubes)
        for cubes in args.cubes[shift:] + args.cubes[:shift]:
            folder = args.output / f'cubes-{cubes}' / f'run-{repeat + 1:02d}'
            if args.resume and folder.exists():
                try:
                    reused = validate(folder, cubes, args.rounds, args.fps, args.offscreen)
                    assert str(folder) in previous_runs, 'Capture was not validated by the previous launcher'
                    result = previous_runs[str(folder)]
                    assert reused['buildGuid'] == result['buildGuid']
                except (AssertionError, FileNotFoundError, ValueError) as error:
                    excluded = args.output / 'excluded' / f'cubes-{cubes}-run-{repeat + 1:02d}-{time.time_ns()}'
                    excluded.parent.mkdir(exist_ok=True)
                    folder.rename(excluded)
                    (excluded / 'rejection.txt').write_text(str(error) + '\n')
                    print(f'Rejected previous capture: {error}; preserved at {excluded}', flush=True)
                else:
                    manifest['runs'].append(result)
                    manifest_path.write_text(json.dumps(manifest, indent=2) + '\n')
                    print(f'Reused: {cubes} cubes, repeat {repeat + 1}', flush=True)
                    continue
            folder.mkdir(parents=True, exist_ok=False)
            command = ['open', '-n', '-W', str(args.app), '--args',
                       '--optimize-benchmark', str(folder), '--optimize-benchmark-rounds', str(args.rounds),
                       '--optimize-benchmark-cubes', str(cubes), '-logFile', str(folder / 'player.log'),
                       '-screen-fullscreen', '0', '-screen-width', '1920', '-screen-height', '1080']
            if args.offscreen: command = [str(executable), '-batchmode'] + command[5:] + ['--optimize-benchmark-offscreen']
            if args.drain_gpu: command.append('--optimize-benchmark-drain-gpu')
            if args.screenshots: command.append('--optimize-benchmark-screenshots')
            if args.fps != -1: command += ['--optimize-benchmark-fps', str(args.fps)]
            command += ['--optimize-benchmark-warmup-seconds', str(args.warmup_seconds), '--optimize-benchmark-precondition-seconds', str(args.precondition_seconds)]
            if args.rotate_orders: command += ['--optimize-benchmark-order-offset', str(repeat % 8)]
            print(f'Start: {cubes} cubes, repeat {repeat + 1}', flush=True)
            before = telemetry()
            started = time.time()
            try:
                opened = subprocess.Popen(command, stdout=subprocess.DEVNULL if args.offscreen else None, stderr=subprocess.STDOUT if args.offscreen else None)
                # Activate after Unity has created its native window, before the focus gate releases.
                if not args.offscreen:
                    time.sleep(3)
                    subprocess.run(['open', '-a', str(args.app)], check=True)
                if opened.wait(timeout=args.timeout): raise RuntimeError('Player launch failed')
                result = validate(folder, cubes, args.rounds, args.fps, args.offscreen)
                meta = json.loads((folder / 'metadata.json').read_text())
                assert meta['gpuQueueDrained'] == args.drain_gpu
                if args.drain_gpu:
                    with (folder / 'frames.csv').open() as stream:
                        assert all(row['gpu_drain_complete'] == '1' for row in csv.DictReader(stream)), 'GPU drain missed a measured frame'
                assert meta['minimumWarmupSeconds'] == args.warmup_seconds
                assert meta['preconditionSeconds'] == args.precondition_seconds
                assert meta['orderOffset'] == (repeat % 8 if args.rotate_orders else 0)
            except BaseException:
                # Match only the executable and unique output path belonging to this launch.
                pattern = re.escape(str(executable)) + '.*' + re.escape(str(folder))
                found = subprocess.run(['pgrep', '-f', pattern], capture_output=True, text=True)
                for pid in found.stdout.split():
                    try: os.kill(int(pid), signal.SIGTERM)
                    except ProcessLookupError: pass
                raise
            result.update({'telemetryBefore': before, 'telemetryAfter': telemetry()})
            result.update({'folder': str(folder), 'cubes': cubes, 'repeat': repeat + 1,
                           'elapsedSeconds': time.time() - started})
            manifest['runs'].append(result)
            manifest_path.write_text(json.dumps(manifest, indent=2) + '\n')
            print('Complete: ' + json.dumps(result), flush=True)
            time.sleep(args.cooldown)
            new_runs += 1
            if args.max_new_runs and new_runs >= args.max_new_runs:
                print('Validated launch checkpoint: ' + str(manifest_path), flush=True)
                return
    print('Validated all Player captures: ' + str(manifest_path), flush=True)


if __name__ == '__main__':
    try:
        main()
    except (AssertionError, FileNotFoundError, ValueError, RuntimeError) as error:
        print('Capture rejected: ' + str(error), file=sys.stderr)
        raise SystemExit(1)
