#!/usr/bin/env python3
"""Aggregate independent mode blocks; bootstrap paired round medians, not individual correlated frames."""
import argparse, json, math, random, statistics
from collections import defaultdict
from pathlib import Path
from capture_validation import load_capture

NAMES = ['Original', 'Combined', 'Backface', 'Occlusion']
METRICS = ['draw_calls', 'batches', 'setpass_calls', 'cpu_frame_ms', 'cpu_main_ms', 'cpu_render_ms', 'cpu_present_wait_ms', 'gpu_frame_ms', 'wall_delta_ms', 'metal_gpu_ms']

def quantile(values, p):
    values = sorted(values)
    if not values: return None
    pos = (len(values)-1)*p
    low = int(pos)
    return values[low] + (values[min(low+1, len(values)-1)]-values[low])*(pos-low)

def summarize(values):
    values = [x for x in values if math.isfinite(x) and x >= 0]
    return dict(count=len(values), median=quantile(values,.5), p05=quantile(values,.05), p95=quantile(values,.95), mean=statistics.mean(values) if values else None, minimum=min(values) if values else None, maximum=max(values) if values else None)

def analyze(directories):
    modes = defaultdict(list)
    blocks = defaultdict(list)
    metadata = []
    capture_ids, folders = set(), set()
    for run, folder in enumerate(directories):
        resolved = folder.resolve()
        if resolved in folders:
            raise ValueError('The same capture directory was supplied twice')
        folders.add(resolved)
        meta, rows, completion = load_capture(folder)
        if meta['captureId'] in capture_ids:
            raise ValueError('The same capture identity was supplied twice')
        capture_ids.add(meta['captureId'])
        metadata.append(meta)
        assert completion['status'] == 'complete' and completion['samples'] == len(rows), 'Incomplete capture'
        expected = metadata[-1]['rounds'] * 4 * metadata[-1]['samplesPerBlock']
        assert len(rows) == expected, 'Unexpected sample count'
        timestamps = [r['timing_timestamp'] for r in rows]
        if not metadata[-1].get('offscreen'):
            assert len(timestamps) == len(set(timestamps)), 'Repeated timing timestamp'
            assert all(float(r['cpu_frame_ms']) > 0 for r in rows)
        else:
            assert len({r['metal_frame_id'] for r in rows}) == len(rows)
            assert all(float(r['metal_gpu_ms']) > 0 for r in rows)
        for r in rows:
            row={k:float(v) for k,v in r.items()}
            row['run']=run
            mode=int(row['mode'])
            modes[mode].append(row)
            blocks[run,int(row['round']),mode].append(row)
    controlled=('cpu','gpu','graphicsApi','quality','renderPipeline','width','height','msaa','renderScale','srpBatcher','dynamicBatching','shader','materialInstancing','warmupFrames','samplesPerBlock','historyOffset','sourceCubeCount','buildGuid','developmentBuild','editor','cameraOrthographic','cameraOrthographicSize','processArchitecture','targetFrameRate','startupSettleSeconds','minimumWarmupSeconds','splashFinishedBeforeCapture','faceSubdivisions','preconditionSeconds','gpuRecorderSupported','gpuQueueDrained','offscreen','batchMode','gpuTimingSource','cameraWidth','cameraHeight','cameraPosition','cameraRotation','frameTimingHistory','timingDefinition','schemaVersion')
    for key in controlled:
        assert len({json.dumps(m.get(key), sort_keys=True) for m in metadata})==1, 'Conditions differ: '+key
    for key,rows in blocks.items():
        assert len(rows)==metadata[key[0]]['samplesPerBlock'], 'Incomplete mode block'
    output={'metadata':metadata, 'runs':len(directories), 'modes':{}, 'pairedComparisons':{}, 'blockMedians':[]}
    output['observedSyncIntervals']=sorted({r.get('sync_interval') for rows in modes.values() for r in rows})
    output['observedWidthScales']=sorted({r.get('width_scale') for rows in modes.values() for r in rows})
    output['observedHeightScales']=sorted({r.get('height_scale') for rows in modes.values() for r in rows})
    positive_timers = {'cpu_frame_ms', 'cpu_main_ms', 'cpu_render_ms', 'gpu_frame_ms', 'metal_gpu_ms'}
    def values(rows, metric):
        return [r[metric] for r in rows if metric in r and (r[metric] > 0 or metric not in positive_timers)]
    for mode in range(4):
        data=modes[mode]
        output['modes'][NAMES[mode]]={'frames':len(data),'focusedFraction':statistics.mean(r['focused'] for r in data),'metrics':{m:summarize(values(data,m)) for m in METRICS},'gpuValidFraction':sum(r['gpu_frame_ms']>0 for r in data)/len(data), 'metalGpuValidFraction':sum(r.get('metal_gpu_ms',0)>0 for r in data)/len(data)}
    block_metrics={key:{m:statistics.median(values(rows,m)) if values(rows,m) else None for m in METRICS} for key,rows in blocks.items()}
    for (run,round_,mode),values in sorted(block_metrics.items()):
        output['blockMedians'].append({'run':run,'round':round_,'mode':NAMES[mode],**values})
    pairs=sorted({(run,round_) for run,round_,mode in blocks})
    rng=random.Random(20261023)
    for mode in range(1,4):
        output['pairedComparisons'][NAMES[mode]]={}
        for metric in ('draw_calls','cpu_frame_ms','cpu_main_ms','cpu_render_ms','gpu_frame_ms','wall_delta_ms','metal_gpu_ms'):
            values=[]
            for run,round_ in pairs:
                original=block_metrics[(run,round_,0)][metric]
                optimized=block_metrics[(run,round_,mode)][metric]
                if original and optimized is not None: values.append(100*(optimized/original-1))
            if not values: continue
            samples=[statistics.median(rng.choices(values,k=len(values))) for _ in range(10000)]
            output['pairedComparisons'][NAMES[mode]][metric]={'pairs':len(values),'medianPercentChange':statistics.median(values),'bootstrap95PercentCI':[quantile(samples,.025),quantile(samples,.975)], 'minimumPercentChange':min(values),'maximumPercentChange':max(values)}
    return output

if __name__=='__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('runs',nargs='+',type=Path)
    parser.add_argument('--output',required=True,type=Path)
    args=parser.parse_args()
    data=analyze(args.runs)
    args.output.write_text(json.dumps(data,indent=2)+'\n')
    for name,result in data['modes'].items():
        print(name,{metric:round(stats['median'],5) if stats['median'] is not None else None for metric,stats in result['metrics'].items()},'frames',result['frames'])
    print('Paired block changes:',json.dumps(data['pairedComparisons'],indent=2))
