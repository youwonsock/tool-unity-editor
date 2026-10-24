#!/usr/bin/env python3
"""Run predeclared, interleaved queued/drained GPU conditions in separate Players."""
import argparse,fcntl,json,subprocess,sys
from pathlib import Path

p=argparse.ArgumentParser()
p.add_argument('--app',type=Path,required=True);p.add_argument('--output',type=Path,required=True)
p.add_argument('--repeats',type=int,default=3);p.add_argument('--rounds',type=int,default=8)
a=p.parse_args();a.output.mkdir(parents=True,exist_ok=True)
assert 1<=a.repeats<=16 and 1<=a.rounds<=32
lock=(a.output/'suite.lock').open('a+');fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
launcher=Path(__file__).with_name('run_player_benchmark.py')
base=[sys.executable,'-u',str(launcher),'--app',str(a.app.resolve()),'--offscreen','--warmup-seconds','2','--precondition-seconds','15','--rotate-orders','--cooldown','5','--resume']
plan=[]
for repeat in range(a.repeats):
 for name in (['queued','drained'] if repeat%2==0 else ['drained','queued']):plan.append((name,repeat+1,a.rounds,-1))
for repeat in range(a.repeats):plan.append(('paced-60',repeat+1,2,60))
for name,target,rounds,fps in plan:
 manifest=a.output/name/'launch-manifest.json'
 if manifest.exists() and len(json.loads(manifest.read_text())['runs'])>=target:continue
 command=base+['--output',str((a.output/name).resolve()),'--cubes','225','--rounds',str(rounds),'--repeats',str(a.repeats),'--max-new-runs','1']
 if name!='queued':command.append('--drain-gpu')
 if fps!=-1:command+=['--fps',str(fps)]
 subprocess.run(command,check=True)
subprocess.run(base+['--output',str((a.output/'visuals').resolve()),'--cubes','9','36','225','--rounds','1','--repeats','1','--drain-gpu','--screenshots'],check=True)
print('GPU suite complete. Analyze each condition separately with summarize_player_benchmark.py.')
