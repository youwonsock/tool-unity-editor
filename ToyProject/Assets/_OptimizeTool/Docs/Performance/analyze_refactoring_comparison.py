import argparse,json,math,random,statistics
from pathlib import Path
from capture_validation import load_capture
parser=argparse.ArgumentParser()
parser.add_argument('work',type=Path)
parser.add_argument('--output',type=Path)
args=parser.parse_args()
WORK=args.work.resolve()
ROOT=WORK/'results' if (WORK/'results').exists() else WORK/'data'
NAMES=['Original','Combined','Backface','Occlusion']
def q(values,p):
 values=sorted(values);pos=(len(values)-1)*p;i=int(pos)
 return values[i]+(values[min(i+1,len(values)-1)]-values[i])*(pos-i)
def summary(values):
 return {'median':statistics.median(values),'p05':q(values,.05),'p95':q(values,.95),'minimum':min(values),'maximum':max(values)}
def paired(before,after):
 # Lists of launch clusters; each cluster contains matched round medians.
 changes=[[100*(a/b-1) for b,a in zip(bs,aa)] for bs,aa in zip(before,after)]
 differences=[[a-b for b,a in zip(bs,aa)] for bs,aa in zip(before,after)]
 rng=random.Random(20261024);percent=[];absolute=[]
 for _ in range(10000):
  pp=[];dd=[]
  for cluster in rng.choices(range(len(changes)),k=len(changes)):
   for block in rng.choices(range(len(changes[cluster])),k=len(changes[cluster])):
    pp.append(changes[cluster][block]);dd.append(differences[cluster][block])
  percent.append(statistics.median(pp));absolute.append(statistics.median(dd))
 return {'launchPairs':len(changes),'blockPairs':sum(map(len,changes)),
         'medianPercentChange':statistics.median([x for row in changes for x in row]),
         'bootstrap95PercentCI':[q(percent,.025),q(percent,.975)],
         'medianAbsoluteChange':statistics.median([x for row in differences for x in row]),
         'bootstrap95AbsoluteCI':[q(absolute,.025),q(absolute,.975)],
         'perLaunchMedianPercentChanges':[statistics.median(c) for c in changes]}
def classify(result):
 lo,hi=result['bootstrap95PercentCI'];dl,dh=result['bootstrap95AbsoluteCI']
 if lo>5 and dl>.1:return 'material regression observed'
 if hi<0:return 'improvement observed'
 if hi<=5 or dh<=.1:return 'material regression excluded within the declared margin'
 return 'inconclusive; no equivalence claim'
conditions=['sourceCubeCount','faceSubdivisions','unityVersion','cpu','gpu','processArchitecture','graphicsApi',
 'quality','renderPipeline','msaa','renderScale','srpBatcher','dynamicBatching','shader','materialInstancing',
 'cameraWidth','cameraHeight','cameraPosition','cameraRotation','cameraOrthographic','cameraOrthographicSize',
 'targetFrameRate','vSyncCount','warmupFrames','minimumWarmupSeconds','preconditionSeconds','rounds',
 'samplesPerBlock','historyOffset','gpuQueueDrained','offscreen','batchMode']
result={'method':'Four AB/BA/BA/AB paired launches per profile; four balanced mode-order rounds per launch. Bootstrap resamples launch pairs and matched rounds. Lower is better.',
 'regressionMargin':{'percent':5,'absoluteMilliseconds':.1},'runtime':{},'bake':{},'conditions':{}}
for profile in ['cpu','gpu']:
 data={};meta={};validation={};total=0
 for pair in range(1,5):
  for variant in ['before','after']:
   folder=ROOT/'runtime'/profile/f'pair-{pair:02d}'/variant
   m,rows,c=load_capture(folder);meta[pair,variant]=m;data[pair,variant]=rows;total+=len(rows)
   validation[pair,variant]=json.loads((folder/'validated.json').read_text())
 for key in conditions:
  values={json.dumps(m.get(key),sort_keys=True) for m in meta.values()}
  assert len(values)==1,(profile,key,values)
 result['conditions'][profile]={key:next(iter(meta.values())).get(key) for key in conditions}
 result['runtime'][profile]={'totalFrames':total,'modes':{},'memory':{}}
 for variant in ['before','after']:
  assert all(validation[p,variant]['cpuTimingValidFraction']>=.99 for p in range(1,5))
  result['runtime'][profile]['memory'][variant]=summary([validation[p,variant]['processPeakRssBytes']/1048576 for p in range(1,5)])
 result['runtime'][profile]['memory']['comparison']=paired([[validation[p,'before']['processPeakRssBytes']/1048576] for p in range(1,5)],[[validation[p,'after']['processPeakRssBytes']/1048576] for p in range(1,5)])
 for mode,name in enumerate(NAMES):
  output={}
  for metric in ['draw_calls','cpu_frame_ms','metal_gpu_ms','cpu_main_ms','wall_delta_ms']:
   blocks={};values={}
   for variant in ['before','after']:
    all_values=[];clusters=[]
    for pair in range(1,5):
     rounds=[]
     for round_ in range(4):
      samples=[float(row[metric]) for row in data[pair,variant] if int(row['mode'])==mode and int(row['round'])==round_]
      assert len(samples)==240
      assert all(math.isfinite(x) and x>=0 for x in samples)
      if metric in ['cpu_frame_ms','cpu_main_ms']: samples=[x for x in samples if x>0]
      elif metric!='draw_calls':assert all(x>0 for x in samples)
      assert len(samples)>=238, 'At least 99 percent positive CPU timings per block'
      all_values.extend(samples);rounds.append(statistics.median(samples))
     clusters.append(rounds)
    values[variant]=summary(all_values);blocks[variant]=clusters
   comparison=paired(blocks['before'],blocks['after'])
   comparison['classification']=classify(comparison) if metric!='draw_calls' else ('unchanged' if all(x==0 for x in comparison['perLaunchMedianPercentChanges']) else 'changed')
   # Exact draw-call distributions must remain identical, not merely their medians.
   if metric=='draw_calls':
    assert {row['draw_calls'] for pair in range(1,5) for row in data[pair,'before'] if int(row['mode'])==mode}=={row['draw_calls'] for pair in range(1,5) for row in data[pair,'after'] if int(row['mode'])==mode}
   output[metric]={**values,'comparison':comparison}
  result['runtime'][profile]['modes'][name]=output
 for name in NAMES[1:]:
  for metric in ['cpu_frame_ms','metal_gpu_ms']:
   baseline=[];after=[]
   for variant,clusters in [('before',baseline),('after',after)]:
    for pair in range(1,5):
     pairs=[]
     for round_ in range(4):
      grouped=lambda mode: statistics.median(float(row[metric]) for row in data[pair,variant] if int(row['mode'])==mode and int(row['round'])==round_ and float(row[metric])>0)
      pairs.append(grouped(NAMES.index(name))/grouped(0))
     clusters.append(pairs)
   result['runtime'][profile]['modes'][name][metric]['relativeToOriginalControl']=paired(baseline,after)
 bakes={}
 for pair in range(1,5):
  for variant in ['before','after']:
   bakes[pair,variant]=json.loads((ROOT/'bake'/profile/f'pair-{pair:02d}'/variant/'validated.json').read_text())
 for pair in range(1,5):
  before=bakes[pair,'before']['modes'];after=bakes[pair,'after']['modes']
  assert [{k:m[k] for k in ['mode','vertices','triangles','culled']} for m in before]==[{k:m[k] for k in ['mode','vertices','triangles','culled']} for m in after]
 output={}
 for metric in ['totalMs','buildMs','saveMs','workflowInitialMs','workflowRebuildMs','processPeakRssBytes','bakeSampledPeakRssBytes','unityAllocatedAtStart','unityAllocatedAfterBuild']:
  divisor=1048576 if metric.endswith('Bytes') or metric.startswith('unity') else 1
  before=[[bakes[p,'before'][metric]/divisor] for p in range(1,5)]
  after=[[bakes[p,'after'][metric]/divisor] for p in range(1,5)]
  output[metric]={'before':summary([x[0] for x in before]),'after':summary([x[0] for x in after]),'comparison':paired(before,after),'unit':'MiB' if divisor!=1 else 'ms'}
 for mode,name in enumerate(NAMES[1:]):
  before=[[bakes[p,'before']['modes'][mode]['milliseconds']] for p in range(1,5)]
  after=[[bakes[p,'after']['modes'][mode]['milliseconds']] for p in range(1,5)]
  output[name]={'before':summary([x[0] for x in before]),'after':summary([x[0] for x in after]),'comparison':paired(before,after)}
 result['bake'][profile]=output
if (WORK/'memory-stage-analysis.json').exists():
 stages=json.loads((WORK/'memory-stage-analysis.json').read_text())
 for profile in ['cpu','gpu']:
  for metric in ['buildOnlySampledPeakMiB','saveSampledPeakMiB']:
   a=[[next(r[metric] for r in stages[profile] if r['pair']==p and r['variant']=='before')] for p in range(1,5)]
   b=[[next(r[metric] for r in stages[profile] if r['pair']==p and r['variant']=='after')] for p in range(1,5)]
   result['bake'][profile][metric]={'before':summary([r[0] for r in a]),'after':summary([r[0] for r in b]),'comparison':paired(a,b),'unit':'MiB, approximately 10 ms RSS sampling'}
(args.output or WORK/'replayed-summary.json').write_text(json.dumps(result,indent=2)+'\n')
print(json.dumps(result,indent=2))
