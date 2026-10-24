#!/usr/bin/env python3
"""Export the measured Player comparisons as publication-quality PNG and SVG charts."""
import argparse
import json
from pathlib import Path
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from matplotlib.ticker import NullLocator, ScalarFormatter

parser = argparse.ArgumentParser()
parser.add_argument('archive', type=Path)
args = parser.parse_args()
data = json.loads((args.archive / 'uncapped-summary.json').read_text())['scales']
paced = json.loads((args.archive / 'paced-240/launch-manifest.json').read_text())
counts = sorted(map(int, data))
names = ['Original', 'Combined', 'Backface', 'Occlusion']
colors = ['#3f4652', '#2678c9', '#18a078', '#e38b22']
plt.rcParams.update({'font.family': 'DejaVu Sans', 'font.size': 10, 'axes.spines.top': False,
                     'axes.spines.right': False, 'axes.titleweight': 'bold'})
fig, axes = plt.subplots(2, 2, figsize=(12.5, 8), constrained_layout=True)
for ax, metric, title, label in [(axes[0, 0], 'draw_calls', 'Draw calls per frame', 'Draw calls (log scale)'),
                                (axes[0, 1], 'cpu_frame_ms', 'CPU total frame time', 'CPU time (ms)'),
                                (axes[1, 0], 'gpu_frame_ms', 'Reported GPU frame time', 'GPU time (ms)')]:
    for name, color in zip(names, colors):
        values = [data[str(count)]['modes'][name]['metrics'][metric]['median'] for count in counts]
        ax.plot(counts, values, marker='o', lw=2, label=name, color=color)
    ax.set_xscale('log'); ax.set_xticks(counts); ax.xaxis.set_major_formatter(ScalarFormatter())
    ax.xaxis.set_minor_locator(NullLocator())
    ax.set_xlabel('Source cube renderers'); ax.set_ylabel(label); ax.set_title(title)
    ax.grid(alpha=.2)
    if metric == 'draw_calls': ax.set_yscale('log')
    else: ax.set_ylim(bottom=0)
ax = axes[1, 1]
runs = sorted(paced['runs'], key=lambda run: run['repeat'])
for mode, (name, color) in enumerate(zip(names, colors)):
    ax.plot([1, 2, 3], [r['modes'][str(mode)]['gpu_frame_ms'] for r in runs], marker='o', lw=2, color=color, label=name)
ax.set_xticks([1, 2, 3]); ax.set_xlabel('Separate Player launch'); ax.set_ylabel('Reported GPU time (ms)')
ax.set_title('GPU control: 3,600 cubes, 240 FPS cap'); ax.grid(alpha=.2); ax.set_ylim(bottom=0)
fig.suptitle('OptimizeTool | Apple M4 Pro / Metal / ARM64 Development Player / 1920 x 1080\n'
             'Baseline medians: targetFrameRate=-1 request. Native GPU durations vary between launches.', fontsize=12)
handles, labels = axes[0, 1].get_legend_handles_labels()
fig.legend(handles, labels, loc='outside lower center', ncol=4, frameon=False)
fig.savefig(args.archive / 'player-scale-summary.png', dpi=180)
fig.savefig(args.archive / 'player-scale-summary.svg')
