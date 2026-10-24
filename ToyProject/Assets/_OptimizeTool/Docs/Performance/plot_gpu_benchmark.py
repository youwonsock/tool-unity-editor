#!/usr/bin/env python3
"""Plot measured GPU camera scopes and paired launch-cluster comparisons."""
import argparse,json
from pathlib import Path
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import numpy as np
p=argparse.ArgumentParser();p.add_argument('archive',type=Path);args=p.parse_args()
a=args.archive
q=json.loads((a/'queued-summary.json').read_text())['scales']['225']
d=json.loads((a/'drained-summary.json').read_text())['scales']['225']
c=json.loads((a/'paced-summary.json').read_text())['scales']['225']
names=['Original','Combined','Backface','Occlusion'];x=np.arange(4)
colors=['#387cc0','#e58932','#6b7886']
condition_colors={'Queued':colors[0],'Drained':colors[1],'Drained, 60 FPS cap':colors[2]}
plt.rcParams.update({'font.family':'DejaVu Sans','font.size':10,'axes.spines.top':False,'axes.spines.right':False})
fig,axes=plt.subplots(2,2,figsize=(12.5,8.5),constrained_layout=True)
for ax,metric,title,ylabel,items in [
 (axes[0,0],'metal_gpu_ms','GPU camera scope (hardware timestamps)','GPU time (ms)',[(q,'Queued'),(d,'Drained')]),
 (axes[0,1],'wall_delta_ms','Observed Player loop interval','Loop interval (ms)',[(q,'Queued'),(d,'Drained'),(c,'Drained, 60 FPS cap')]),
 (axes[1,1],'metal_gpu_ms','Pacing control: drained GPU camera scope','GPU time (ms)',[(d,'Drained'),(c,'Drained, 60 FPS cap')])]:
 width=.78/len(items)
 for i,(summary,label) in enumerate(items):
  vals=[summary['modes'][n]['metrics'][metric]['median'] for n in names]
  positions=x+(i-(len(items)-1)/2)*width
  ax.bar(positions,vals,width=width*.9,color=condition_colors[label],label=label,alpha=.85)
  if metric=='metal_gpu_ms':
   blocks=summary['blockMedians']
   for k,name in enumerate(names):
    for run in sorted({b['run'] for b in blocks}):
     values=[b[metric] for b in blocks if b['run']==run and b['mode']==name]
     ax.scatter(positions[k]+(run-1)*.025,np.median(values),s=20,c='#202936',zorder=5)
 ax.set_xticks(x,names);ax.set_title(title,fontweight='bold');ax.set_ylabel(ylabel);ax.set_ylim(bottom=0);ax.grid(axis='y',alpha=.18);ax.set_axisbelow(True);ax.legend(fontsize=8)
ax=axes[1,0]
for i,(summary,label) in enumerate([(q,'Queued'),(d,'Drained')]):
 for j,name in enumerate(names[1:]):
  z=summary['clusteredComparisons'][name]['metal_gpu_ms'];v=z['medianPercentChange'];lo,hi=z['bootstrap95PercentCI']
  pos=j+(i-.5)*.18
  ax.errorbar(v,pos,xerr=[[max(0,v-lo)],[max(0,hi-v)]],fmt='o',capsize=4,color=colors[i],label=label if j==0 else None)
ax.axvline(0,color='#9da6ad',lw=1);ax.set_yticks(range(3),names[1:]);ax.invert_yaxis();ax.set_xlabel('GPU change vs Original (%)');ax.set_title('Paired round changes: hierarchical bootstrap 95% CI',fontweight='bold');ax.grid(axis='x',alpha=.18);ax.legend(fontsize=8)
fig.suptitle('OptimizeTool | 225 cubes / 11.06M source triangles / Metal / ARM64 Player / 1920 x 1080\nOffscreen camera rendering; dots show launch-level medians of round medians. GPU scope excludes diagnostic readback.',fontsize=12)
fig.savefig(a/'gpu-validation.png',dpi=180);fig.savefig(a/'gpu-validation.svg')
