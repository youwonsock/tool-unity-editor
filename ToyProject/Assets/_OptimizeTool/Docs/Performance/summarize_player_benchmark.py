#!/usr/bin/env python3
"""Validate Player scale captures and report paired block changes with run-cluster resampling."""
import argparse
import importlib.util
import json
from pathlib import Path
import random
import statistics
import sys

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location('benchmark_analysis', Path(__file__).with_name('analyze_benchmark.py'))
analysis = importlib.util.module_from_spec(spec)
spec.loader.exec_module(analysis)


def clustered_changes(summary):
    rng = random.Random(20261024)
    blocks = {(b['run'], b['round'], b['mode']): b for b in summary['blockMedians']}
    runs = sorted({key[0] for key in blocks})
    result = {}
    for mode in analysis.NAMES[1:]:
        result[mode] = {}
        for metric in ('cpu_frame_ms', 'cpu_main_ms', 'cpu_render_ms', 'gpu_frame_ms', 'wall_delta_ms', 'metal_gpu_ms'):
            by_run = {}
            for run in runs:
                rounds = sorted({r for n, r, _ in blocks if n == run})
                by_run[run] = [100 * (blocks[run, r, mode][metric] / blocks[run, r, 'Original'][metric] - 1) for r in rounds
                               if blocks[run, r, 'Original'][metric] is not None and blocks[run, r, 'Original'][metric] > 0
                               and blocks[run, r, mode][metric] is not None]
            active_runs = [run for run in runs if by_run[run]]
            if not active_runs: continue
            all_values = [v for values in by_run.values() for v in values]
            resampled = []
            for _ in range(10000):
                values = []
                for run in rng.choices(active_runs, k=len(active_runs)):
                    values.extend(rng.choices(by_run[run], k=len(by_run[run])))
                resampled.append(statistics.median(values))
            result[mode][metric] = {'runs': len(active_runs), 'pairs': len(all_values),
                                    'medianPercentChange': statistics.median(all_values),
                                    'bootstrap95PercentCI': [analysis.quantile(resampled, .025), analysis.quantile(resampled, .975)]}
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('root', type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    output = {'method': 'Paired round medians; bootstrap resamples launches, then blocks within each launch.', 'scales': {}}
    scales = sorted((p for p in args.root.glob('cubes-*') if p.is_dir()), key=lambda p: int(p.name.split('-')[1]))
    assert scales, 'No scale capture directories found'
    for folder in scales:
        runs = sorted(p for p in folder.glob('run-*') if p.is_dir())
        summary = analysis.analyze(runs)
        summary['clusteredComparisons'] = clustered_changes(summary)
        cubes = int(folder.name.split('-')[1])
        output['scales'][str(cubes)] = summary
        print('Cubes:', cubes)
        for mode in analysis.NAMES:
            stats = summary['modes'][mode]
            print(mode, {k: round(stats['metrics'][k]['median'], 6) if stats['metrics'][k]['median'] is not None else None for k in ('draw_calls', 'cpu_frame_ms', 'gpu_frame_ms', 'metal_gpu_ms', 'wall_delta_ms')})
        print('Clustered comparisons:', json.dumps(summary['clusteredComparisons']))
    args.output.write_text(json.dumps(output, indent=2) + '\n')


if __name__ == '__main__':
    main()
