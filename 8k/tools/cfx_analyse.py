# Reads a CapFrameX capture JSON and prints true avg fps, 1%/0.1% low (CapFrameX method: 1000 / mean of the worst 1%/0.1% frame times), slow-frame counts and spike timing.
# Usage: python tools/cfx_analyse.py "<path to CapFrameX-osu!.exe-....json>"
import json, sys, statistics as st
from collections import Counter
f=sys.argv[1]
d=json.load(open(f,encoding='utf-8-sig')); cd=d['Runs'][0]['CaptureData']
ft=cd['MsBetweenPresents']; t=cd['TimeInSeconds']; n=len(ft)
print(f.split('T')[-1], 'frames',n,'span s',round(t[-1]-t[0],1))
neg=[x for x in ft if x<=0]; print('non-positive frametimes',len(neg), 'min',min(ft),'max',max(ft))
good=[x for x in ft if x>0]
print('avg fps = frames/time:', round(len(good)/(sum(good)/1000),1))
s=sorted(good,reverse=True)
for p in (0.01,0.001):
    w=s[:max(1,int(len(s)*p))]; print(f'{p*100}% low avg fps (1000/mean worst):',round(1000/st.mean(w),1), ' worst-frame ms range', round(w[-1],3),'-',round(w[0],2))
for th in (1,2,5,10,20):
    print(f'frames > {th} ms:', sum(1 for x in good if x>th))
print('present modes', Counter(cd['PresentMode']).most_common(3), 'tearing', Counter(cd['AllowsTearing']).most_common(2), 'sync', Counter(cd['SyncInterval']).most_common(2))
idx=[i for i,x in enumerate(ft) if x>5]
print('spike >5ms: mean MsInPresentAPI', round(st.mean(cd['MsInPresentAPI'][i] for i in idx),3) if idx else '-', ' mean CpuActive', round(st.mean(cd['CpuActive'][i] for i in idx),3) if idx else '-', ' mean GpuActive', round(st.mean(cd['GpuActive'][i] for i in idx),3) if idx else '-')
print('normal frame: median ms', round(st.median(good),4), ' median CpuActive', round(st.median(cd['CpuActive']),4), ' median GpuActive', round(st.median(cd['GpuActive']),4))
ts=[t[i] for i in idx]; gaps=[round(b-a,2) for a,b in zip(ts,ts[1:])]
print('spike count', len(idx), ' first 15 spike times s', [round(x,1) for x in ts[:15]])
print('spike ms first 15', [round(ft[i],1) for i in idx[:15]])
print('commonest gaps between spikes (s)', Counter(round(g,1) for g in gaps).most_common(8))
