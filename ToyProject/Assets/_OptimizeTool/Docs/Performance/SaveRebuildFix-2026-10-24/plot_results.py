from pathlib import Path
import json
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import numpy as np

archive = Path(__file__).resolve().parent
data = json.loads((archive / 'summary.json').read_text())
fig, axes = plt.subplots(1, 3, figsize=(11.6, 3.9), layout='constrained')
for ax, metric, title in zip(axes, ['saveMs', 'workflowRebuildMs', 'totalMs'],
                           ['Fresh save: 3 mesh assets', 'Public API rebuild: 3,600 sources', 'Core build + fresh save']):
    row = data['metrics'][metric]
    colors = ['#466b9a', '#198367']
    ax.bar([0, 1], [row['baselineMedian'], row['fixedMedian']], color=colors, width=.58, alpha=.88)
    for before, after in zip(row['baseline'], row['fixed']):
        ax.plot([0, 1], [before, after], color='#292f38', alpha=.45, marker='o', markersize=4, linewidth=.8)
    ax.set_xticks([0, 1], ['Before fix', 'After fix'])
    ax.set_ylabel('Elapsed time (ms)')
    ax.set_title(title, fontsize=10, fontweight='bold')
    ax.set_ylim(0, max(row['baseline'] + row['fixed']) * 1.18)
    ax.spines[['right', 'top']].set_visible(False)
    ax.yaxis.grid(True, alpha=.15); ax.set_axisbelow(True)
    for i, value in enumerate([row['baselineMedian'], row['fixedMedian']]):
        ax.text(i, value + ax.get_ylim()[1] * .025, f'{value:.1f}', ha='center', va='bottom', fontsize=10, fontweight='bold')
    ax.text(.5, .96, f'Paired median: {row["pairedMedianPercent"]:.1f}%', transform=ax.transAxes, ha='center', va='top', fontsize=10)
fig.suptitle('3,600-cube regression fix | four matched fresh Unity Editor launches', fontsize=12, fontweight='bold')
fig.supxlabel('Bars: launch medians. Lines/dots: all four matched launches, including outliers. Lower is better.', fontsize=9)
fig.savefig(archive / 'comparison.png', dpi=170)
fig.savefig(archive / 'comparison.svg')
