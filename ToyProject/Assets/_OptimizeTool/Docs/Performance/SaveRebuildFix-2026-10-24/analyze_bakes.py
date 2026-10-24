#!/usr/bin/env python3
"""Analyze matched launch clusters without treating RSS samples as independent trials."""
import argparse
import json
from pathlib import Path
import random
import statistics

def quantile(values, probability):
    values = sorted(values)
    position = (len(values) - 1) * probability
    index = int(position)
    return values[index] + (values[min(index + 1, len(values) - 1)] - values[index]) * (position - index)

def analyze(work):
    config = json.loads((work / 'config.json').read_text())
    root = work / 'bake' if (work / 'bake').exists() else work / 'data/bake'
    before, after = [], []
    for pair in range(1, config['pairedRepeats'] + 1):
        rows = [json.loads((root / f'pair-{pair:02d}' / variant / 'validated.json').read_text())
                for variant in ['baseline', 'fixed']]
        for variant, row in zip(['baseline', 'fixed'], rows):
            assert row['pair'] == pair and row['variant'] == variant
            assert row['cubes'] == 3600 and row['subdivisions'] == 1 and row['unityVersion'] == config['unityVersion']
            assert row['sampleCount'] > 0 and row['concurrentBatchEditors'] == []
            assert row['telemetryBefore']['power'].startswith("Now drawing from 'AC Power'")
            assert row['telemetryAfter']['power'].startswith("Now drawing from 'AC Power'")
            assert [(m['vertices'], m['triangles'], m['culled']) for m in row['modes']] == [(86400, 43200, 0), (14400, 7200, 0), (28800, 14400, 2400)]
        before.append(rows[0]); after.append(rows[1])
    for variant, rows in [('baseline', before), ('fixed', after)]:
        assert len({json.dumps(row['sourceHashes'], sort_keys=True) for row in rows}) == 1, variant
    result = {'launchPairs': len(before), 'order': config['order'], 'bootstrapReplicates': 10000, 'seed': 20261024,
              'method': 'Paired launch ratios; one independent fresh-process observation per metric per launch. Resample matched pairs, report median of ratios. Percentile intervals do not capture unmeasured systematic workstation effects.', 'metrics': {}}
    for metric in ['saveMs', 'workflowInitialMs', 'workflowRebuildMs', 'totalMs', 'buildMs', 'processPeakRssBytes', 'bakeSampledPeakRssBytes']:
        bs = [row[metric] for row in before]; aa = [row[metric] for row in after]
        changes = [100 * (a / b - 1) for b, a in zip(bs, aa)]
        rng = random.Random(20261024)
        samples = [statistics.median(rng.choices(changes, k=len(changes))) for _ in range(10000)]
        result['metrics'][metric] = {'baseline': bs, 'fixed': aa, 'baselineMedian': statistics.median(bs),
                                    'fixedMedian': statistics.median(aa), 'pairedMedianPercent': statistics.median(changes),
                                    'bootstrap95PercentCI': [quantile(samples, .025), quantile(samples, .975)],
                                    'pairedChanges': changes}
    return result

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('work', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.output.write_text(json.dumps(analyze(args.work), indent=2) + '\n')
