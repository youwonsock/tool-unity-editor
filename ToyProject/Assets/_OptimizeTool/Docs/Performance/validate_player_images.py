#!/usr/bin/env python3
"""Compare native Player screenshots without changing any image pixels."""
import argparse
import json
from pathlib import Path
import numpy as np
from PIL import Image

parser = argparse.ArgumentParser()
parser.add_argument('root', type=Path)
parser.add_argument('--output', required=True, type=Path)
args = parser.parse_args()
result = {}
cases = sorted(p for p in args.root.glob('cubes-*') if p.is_dir())
assert cases, 'No Player screenshot directories found'
for case in cases:
    folder = case / 'run-01'
    meta = json.loads((folder / 'metadata.json').read_text())
    original = np.asarray(Image.open(folder / 'mode-0.png').convert('RGB'), dtype=np.int16)
    entry = {'buildGuid': meta['buildGuid'], 'resolution': [original.shape[1], original.shape[0]], 'modes': {}}
    for mode in range(1, 4):
        current = np.asarray(Image.open(folder / f'mode-{mode}.png').convert('RGB'), dtype=np.int16)
        assert original.shape == current.shape
        difference = np.abs(current - original)
        mask = (np.abs(original - original[0, 0]).sum(axis=2) > 15) | (np.abs(current - original[0, 0]).sum(axis=2) > 15)
        assert int(mask.sum()) > 1000, 'Camera sees no useful geometry'
        mean = float(difference.mean() / 255)
        foreground = float(difference[mask].mean() / 255)
        entry['modes'][str(mode)] = {'meanNormalizedRgbError': mean, 'foregroundNormalizedRgbError': foreground,
                                   'changedPixels': int((difference.max(axis=2) != 0).sum()), 'identicalPixels': not bool(difference.any())}
        assert mean < .005 and foreground < .01, f'{case.name}, mode {mode}: {entry["modes"][str(mode)]}'
    result[case.name] = entry
args.output.write_text(json.dumps(result, indent=2) + '\n')
print(json.dumps(result, indent=2))
