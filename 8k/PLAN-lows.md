# Plan: fix the frame-time lows, then raise FPS

Status: 2026-09-29. Steps 1 and 2 (candidate) BUILT, uncommitted; waiting on the user's capture runs.
Step 0 and the thread-boost part of Step 3 are DROPPED: the user's Windows and Process Lasso
already handle priority, affinity and cores, so this is a program fix only.

## What the captures show (CapFrameX raw files, Documents\CapFrameX\Captures)

| Run | Frames | True avg fps | 1% low | 0.1% low | Frames > 1 ms | > 5 ms | > 20 ms | Max ms |
|---|---|---|---|---|---|---|---|---|
| 89 s  (17:30) | 548,900   | 6,136.6 | 632.5 | 83.8  | 34  | 13 | 13 | 989 |
| 425 s (17:58) | 2,344,017 | 5,508.6 | 906.9 | 166.3 | 621 | 55 | 51 | 956 |

- CapFrameX shows a negative average for the 425 s run. The raw file has no bad frame times;
  the true average is 5,508.6 fps. It is a CapFrameX display problem.
- The lows come from a few dozen freezes of 25-989 ms. A normal frame is about 0.15 ms.
- In a freeze the time is CPU-side: present call about 0.04 ms, GPU time normal.
- Every frame is PresentMode 3 (hardware independent flip), tearing on, sync interval 0.
  That is already the best-case display path.

## What the code confirms

1. **Draw only presents when update has published a new frame.** `TripleBuffer.GetForRead`
   busy-spins with a 100 ms timeout (`TripleBuffer.cs:21,54-72`, `GameHost.cs:518`).
   An update-thread stall of T ms is a present gap of T ms.
2. **Before each frame, draw waits on the swapchain's frame-ready signal**
   (`GameHost.cs:514-515` -> Veldrid `WaitForNextFrameReady`). The upstream comment there says
   that when this signal is out of step, the wait takes "the timeout value (1 second)" to recover.
   Both captures' worst frames are 989 ms and 956 ms: never above 1 s. **This is the lead
   suspect for the long freezes.** Unproven until measured.
3. There is no thread priority, affinity or power-throttling opt-out for any game thread;
   only the process class goes High while focused (`WindowsGameHost.cs:117-131`). On a
   14900KF, Windows can put a hot thread on an E-core.
4. GC: workstation, concurrent. `SustainedLowLatency` at startup, `LowLatency` in gameplay.
   At 5-6k update frames/s, allocation churn could produce the 1-20 ms tail.
5. Timer resolution is already 1 ms (`timeBeginPeriod(1)`). Ruled out for freezes over 25 ms.
6. `MinimiseOnFocusLossInFullscreen = True`: any focus steal stops presenting.

## Steps

### Step 0 - DROPPED (PC side already tuned with Process Lasso)

### Step 1 - spike logger (BUILT; off by default, `OSU_SPIKE_LOG_MS=5`)
One capture then names the cause instead of guessing. On any draw frame over 5 ms,
write one line to the runtime log with:
- time spent in each phase: frame-ready wait, `GetForRead` spin, draw, `Swap`;
- the update thread's last frame time;
- the GC collection count change (gen0/1/2) and the GC pause-time change since the last frame;
- whether the window was active.
The logger is written to cost nothing when off (a single bool check).

### Step 2 - targeted fix, chosen from the Step 1 log
- `OSU_NO_FRAME_WAIT` was built, tested (no effect) and REMOVED before the commit.
- If the frame-ready wait is the freeze: stop waiting when the last frame did not actually
  present, or skip the wait while tearing is on (it only paces frames, and at 5k+ fps with
  tearing it buys nothing). Gate: `OSU_NO_FRAME_WAIT=1`.
- If update is the freeze and GC counts jump: GC tuning via launcher env vars
  (`DOTNET_GCgen0size`) and cutting the allocations the log points at.
- If update is the freeze with no GC: thread boost (Step 3) and find the blocking call.

### Step 3 - FPS and consistency (build; each off by default)
- Thread boost: DROPPED (Process Lasso already sets priority and cores).
- `OSU_SPIN_PAUSE=1` (BUILT, not yet tested): CPU pause hint in the `GetForRead` spin, so the draw thread stops
  hammering the cache line the update thread writes.

## Measuring (every step)
- CapFrameX, same map, exclusive fullscreen, 300 s, 60 s warm-up first, 3 runs per setting.
- Change one thing per run.
- Judge by counts of frames over 1/5/20 ms and the max frame time. The percentile lows are
  noisy with only a few dozen freezes. I read these straight from the capture files.

## Out of scope
- Timestamped input (README "Not done").
- Changing the renderer (D3D11 is right).
- Capping the update thread (adds latency, gains no fps).

## Results of the diagnose runs (2026-09-29, two captures of about 135 s, one map each)

| Run | Avg fps | 1% low | 0.1% low | CapFrameX frames > 5 ms | Game-logged present gaps > 5 ms during gameplay |
|---|---|---|---|---|---|
| A: logger only     | 6,246.1 | 1,883.9 | 1,091.4 | 3  | 0 |
| B: + no frame wait | 6,180.2 | 1,663.5 | 656.6   | 12 | 0 |

- **The frame-ready wait is not the cause.** It was 0.00 ms in every logged spike in both runs.
  `OSU_NO_FRAME_WAIT` changed nothing and was removed.
- No freeze near 1 s in either run.
- **During gameplay the game's own clock logged no gap over 5 ms,** yet CapFrameX reports
  gaps of 37-200 ms in the middle of the same gameplay. The logger times every present the
  game makes. Leading explanation: PresentMon (ETW) loses events at about 6,000 presents/s, and
  a lost event shows up as one long frame. Strongly suggested by the evidence, not proven.
- **Real hitches happen at screen changes only** (song select -> loading -> map start, and results):
  - draw thread 20-73 ms with no GC and update fine: first-time drawing/texture upload for the new screen;
  - update thread 25-64 ms, sometimes with a GC (3-15 ms of it): loading work on the update thread.
- Unfocused periods (alt-tab to start or stop a capture) show as one multi-second gap on refocus. Not stutter.

## Decisions 2026-09-29 and what is next
See NEXT.txt. Only gameplay frame times matter; menus and map loading do not.
Next: in-game frame stats (OSU_FRAME_STATS) to check CapFrameX's lows against the game's own clock.

## Result of the first in-game frame stats run (2026-09-30, one map, 139 s)

The game's own measurement (`OSU_FRAME_STATS=1`) and CapFrameX recorded the same map:

| | game | CapFrameX |
|---|---|---|
| frames | 837,772 | 822,307 |
| avg fps | 6,038 | 5,990 |
| 1% low | 1,811 | 1,662 |
| 0.1% low | 1,034 | 710 |
| max | 46.85 ms | 139.55 ms |
| frames > 5 ms | 6 | 9 |
| frames > 20 ms | 1 | 4 |

- CapFrameX over-reports: it shows three extra frames over 20 ms and a 140 ms max that the game never
  presented. The game's own numbers are the ones to trust from now on.
- All six of the game's frames over 5 ms fall in the first ~5 s after the session starts (map start).
  They are update-thread gaps of 13-43 ms, and gen0/gen1 GCs happen in the same frames. Per the
  user's standing answer 3, map start does not count.
- Skipping the first 5 s leaves: 0 frames over 5 ms, max 4.49 ms, 1% low 1,882, 0.1% low 1,203,
  avg 6,031 fps. The rest of the gameplay is clean.
- Conclusion: there is no real gameplay stutter left to fix in Step 2. The lows from earlier
  captures were a PresentMon/CapFrameX measurement artefact plus the hitches at map start.

## Plan 2026-09-30: raise the gameplay 1% low to 2k+ (AGREED 2026-09-30; Step A in progress)

Correction from the user: map LOADING does not count, but everything from the first circle onward does.

What the data says (research 2026-09-30, one map):
- The 1% low (1,811) is set by a broad band of 0.4-1 ms frames, about 25-35 per second over the whole
  map. It is not set by spikes: removing every frame >2 ms or the first 5 s moves it by only ~80 fps.
- The worst 1% averages 0.552 ms. 2,000 fps needs that down to 0.50 ms (a ~10% trim of the band);
  3,000 needs 0.333 ms, which means flattening everything above ~p95. 2k looks reachable; 3k is a stretch.
- The band follows a 64 Hz (15.625 ms) rhythm: slow frames are 4x more likely in one third of the cycle.
  The cause is unknown.
- The 13-14 ms hitches at 22:07:19-20 fall 2-3 s after the intro seek, so they may be at the first circle.
- Nothing allocation- or JIT-related is configured: default workstation concurrent GC (gen0 budget
  ~6 MB), tiered JIT with PGO, and osu.Game.dll is plain IL (not ReadyToRun).

Step A - measure what the band is (code, all under OSU_FRAME_STATS, nothing new when off)
- Per frame, beside the present gap, also record: wait-for-update, draw and swap time (the [spike]
  breakdown, for every frame) and the gen0 GC count (cheap), so each 0.4-1 ms frame is attributed to
  a thread, a phase or a GC.
- Per second: allocated bytes and GC pause time.
- osu side: mark the frame where the gameplay clock reaches the first hit object. The summary then
  reports lows "from first object", and the loading/intro part is excluded automatically.
- Analyser: attribution table and the 64 Hz check per phase.
- Needs one map played by the user afterwards.

Step B - A/B with launcher env vars only (no code; each is a new play-8k-ab-*.bat)
- B1 GC: DOTNET_GCgen0size=0x10000000 (bigger gen0 budget, fewer gen0 GCs).
- B2 JIT: DOTNET_TieredPGO=0 + DOTNET_TC_CallCountingDelayMs=0.
- B3 JIT: DOTNET_TieredCompilation=0 (full opt at first call, no rejit).
- B4 OSU_SPIN_PAUSE=1.
- Same map, one run each (more if close), compared on the from-first-object 1% / 0.1% lows.

Step C - code fixes, chosen from A and B (each off by default behind an env var)
- C1 GC.TryStartNoGCRegion sized from Step A's allocation rate, entered when gameplay starts, ended
  at session end; and/or a blocking GC just before the Player is pushed.
- C2 pre-warm during loading: bigger initial hit-object pools (OsuPlayfield registers 20), so the
  first circles do not create drawables / JIT / look up skins on the update thread.
- C3 ReadyToRun publish into a separate folder (new launcher), if JIT still shows at map start.
- C4 whatever Step A names as the band's source (e.g. a per-frame allocation hot spot, or the thread
  behind the 64 Hz rhythm).

## Step A/B results (2026-09-30, same map x5; marker = first object's hit time)

From first object (runs in order):
| run | avg fps | 1% low | 0.1% low | gen0 GCs |
|---|---|---|---|---|
| baseline (diagnose) | 6,021 | 1,850 | 1,164 | 5,469* |
| B1 GCgen0size=256MB | 5,981 | 1,863 | 1,173 | 5,437* |
| B2 TieredPGO=0 + CallCountingDelay=0 | 5,553 | 1,810 | 1,127 | 5,602* |
| B3 TieredCompilation=0 | 4,326 | 1,573 | 963 | 4,962* |
| B4 OSU_SPIN_PAUSE=1 | 5,751 | 1,803 | 1,173 | 6,111* |
(* whole session, about 139 s)

- No launcher setting helps. B2-B4 lower the average; B1 is ignored (the GC count does not change).
- The band is gen0 GCs: about 40 per second at only ~10 MB/s allocated, i.e. one GC per ~250 KB.
  30% of the 0.4-0.5 ms frames, 80% of 0.5-0.7 ms and 90% of 0.7-1 ms frames contain a gen0 GC. The
  mean pause is ~0.36 ms (1,973 ms of pause in 139 s).
- What-if: if the GC frames were typical frames, the baseline 1% low would be 2,380 and the 0.1% low 1,642
  (B4 run: 2,346 / 1,668). Removing the GCs is enough for the 2k target.
- The gen0 budget is far too small (~250 KB). Suspects: GCLatencyMode.LowLatency, which
  HighPerformanceSessionManager sets for gameplay, or something inducing GCs. B1 being ignored
  fits either.
- The 64 Hz rhythm remains (peak 2,135x the median); not yet explained.

Step C as chosen from this data:
- C0 move the first-object marker to when the first object becomes visible (user, 2026-09-30).
- C4a log the GC trigger reason (in-process EventListener on the runtime GC events) under OSU_FRAME_STATS.
- C4b env-gated GC mode for gameplay (OSU_GC_MODE: default LowLatency as upstream, or
  Batch/Interactive/SustainedLowLatency), plus a launcher for DOTNET_GCgen0MaxBudget.
- C1 env-gated no-GC region during gameplay (OSU_NOGC_MB), re-armed when it runs out.
- C2 (pool pre-warm) and C3 (ReadyToRun) are parked: from the first object there are only 2 frames >5 ms.

## Step C built (2026-09-30, Sonnet agent): scratch-test finding

- Scratch test (net10.0, ~8 MB/s of small allocations, 5 s each): GCLatencyMode.LowLatency gives one gen0 GC per ~257 KB
  (107 GCs); Interactive 1 GC (27 MB per GC), SustainedLowLatency 2 GCs (14 MB per GC), Batch 1 GC. So the game's ~250 KB gen0 budget
  is what HighPerformanceSessionManager's LowLatency mode causes, not something inducing GCs. Play-test OSU_GC_MODE=SustainedLowLatency
  / Interactive (new launchers) to confirm in the game's own numbers.
- GC reason capture (EventListener) counts match GC.CollectionCount exactly; it allocates ~7 KB per GC (about 280 KB/s at 40 GCs/s).
- No-GC region: arming 256 MB costs ~0.7 ms on an empty heap (the real cost shows in the [gc] log line); sizes up to 32768 MB are accepted here,
  65536 MB returns false. The region ends by itself when the budget is used and the mode reverts to the one set before it.
- Marker moved to first-object-visible (StartTime - TimePreempt; osu! and catch; taiko/mania fall back to StartTime).

## Step C results (2026-09-30, same map x5; marker = first object VISIBLE)

From first object:
| run | avg fps | 1% low | 0.1% low | max ms | >2 ms | GCs | GC pause total |
|---|---|---|---|---|---|---|---|
| baseline LowLatency (diagnose) | 5,714 | 1,709 | 1,072 | 14.9 | 9 | 6,120 | 2,328 ms |
| OSU_GC_MODE=SustainedLowLatency | 5,822 | 2,221 | 1,205 | 22.9 | 43 | 92 | 155 ms |
| OSU_GC_MODE=Interactive | 5,814 | **2,246** | **1,264** | 13.3 | 39 | 90 | 148 ms |
| OSU_NOGC_MB=256 | 5,756 | 2,171 | 1,119 | 43.2 | 16 (8 >5 ms) | 184 | - |
| DOTNET_GCgen0MaxBudget=256MB | 5,690 | 1,713 | 1,049 | 14.8 | 7 | 5,800 | - |

- Confirmed: LowLatency caps gen0 at 256 KB (reason AllocSmall). Interactive and Sustained give ~18 MB.
- Interactive passes the 2k target: 1% low 2,246, 0.1% low 1,264, total GC pause 16x lower.
- Trade: ~90 GCs of ~1.6 ms each replace ~6,000 of ~0.36 ms; frames >2 ms go from 9 to ~40.
- nogc: the user felt several stutters (3 frames >20 ms, max 43 ms) - rejected. gen0budget: no effect,
  the user felt a little stutter - rejected.
- What is left in the band (Interactive): 0.3-0.5 ms frames with a slower swap (~0.2 ms vs 0.04) and
  draw, no GC. That is the next target (with the 64 Hz rhythm).
- Input latency is NOT measured end to end yet (only the input pump rate). Needed before anything is
  made default (user 2026-09-30: never trade input delay for fps).

## Step D results (2026-09-30, round D: same map, runs in order; input delay measured)

The user ran baseline twice (1a may have been disturbed by a third-party app). From first object; delays in ms, keys+buttons
(about 915 presses/releases per run). pump->present = pump->update + update->present.
| run | avg fps | 1% low | 0.1% low | max ms | >2 ms | GCs | pump->update mean / p99 | update->present mean / p99 / max |
|---|---|---|---|---|---|---|---|---|
| 1a baseline LowLatency | 5,736 | 1,517 | 978 | 14.8 | 7 | 5,102 | 0.068 / 0.165 | 0.717 / 1.55 / 15.5 |
| 1b baseline LowLatency | 5,902 | 1,764 | 1,111 | 4.0 | 2 | 5,270 | 0.067 / 0.168 | 0.661 / 1.51 / 1.74 |
| 2 Interactive | 5,938 | **2,193** | **1,164** | 16.8 | 50 | 82 | 0.071 / 0.172 | **0.543 / 1.05** / 17.0 |
| 3 SustainedLowLatency | 5,939 | 2,163 | 1,132 | 14.2 | 50 | 72 | 0.069 / 0.186 | 0.556 / 1.06 / 14.4 |

- Interactive and Sustained LOWER input delay too: update->present mean -0.12 ms and p99 -0.46 ms versus baseline 1b.
  The cost: rare long frames (max 17 ms / 14 ms; one input in each run landed on one). Baseline 1b had none over 4 ms.
- pump->update is ~0.07 ms in every mode (input thread + update drain is not the bottleneck).
- update->present (~0.55 ms mean) is ~3 frames at ~0.16 ms/frame: the update -> TripleBuffer -> draw -> swap pipeline.
  This is the biggest software share left and the next lever for input delay.
- os->pump for keys is NOT usable: mean ~8.3 ms, spread 0-16.5 ms = SDL stamps keys from GetMessageTime (15.6 ms tick).
- Mouse move n=0 in every run: the user plays with a Wacom tablet through the Pen handler (input.json: PenHandler
  enabled, OpenTabletDriver disabled), which Step D does not record. The cursor path is unmeasured.
- 64 Hz rhythm still present (Interactive: peak 64 Hz, 5212x the median). Slow band 0.3-0.5 ms: swap ~0.18-0.23 ms
  vs 0.033 typical, no GC.
- What the user felt: not yet asked.
