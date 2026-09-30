# osu!lazer 8k — low-latency build

A source build of osu!lazer patched for high-polling-rate input and a genuinely
uncapped frame rate. Completely separate from your official install.

- Game repo: `osu/` — ppy/osu @ `6408b4e`, branch `8k-latency`
- Framework: `osu-framework/` — ppy/osu-framework @ `247dee9` (= tag `2026.921.0`), branch `8k-latency`
- Built binary: `osu/osu.Desktop/bin/Release/net10.0/osu!.exe`
- Data directory: `%APPDATA%\osu-8k` (your official install uses `%APPDATA%\osu` and is never touched)

---

## Run it

| Launcher | Input pump | Frame cap |
|---|---|---|
| `play-8k.bat` | 8000 Hz (0.125 ms) | uncapped |
| `play-8k-uncapped.bat` | free-running, no sleep | uncapped |
| `play-8k-stock.bat` | 1000 Hz | 1000 fps (stock) — for A/B |

### One manual step, required

**Settings → Graphics → Frame limiter → "Unlimited".**

The default is `Limit2x`. Without this the update thread still runs at 2× refresh
and you keep roughly half the latency. This was deliberately left as a UI toggle so
it stays reversible.

Also: **run exclusive fullscreen**, not borderless. Borderless routes the swapchain
through DWM and costs a full refresh period (4.2 ms at 240 Hz) — that is 4–8× larger
than everything these patches recover.

---

## Measured result

From `%APPDATA%\osu-8k\logs\*.runtime.log`, this machine:

```
[input] 8,019 Hz effective, mean loop period 0.1247 ms (target 8,000 Hz)
[input] 8,000 Hz effective, mean loop period 0.1250 ms (target 8,000 Hz)
```

Input pump period: **1.000 ms → 0.125 ms.** The logger prints every 5 seconds, so you
can confirm any config change actually took effect rather than trusting the FPS counter
(which only ever showed the *update* thread's frame time).

Real input-to-game-state latency, mean, software portion only:

| | before | after |
|---|---|---|
| SDL pump | 0.50 ms | 0.06 ms |
| update-thread drain | 0.52 ms | 0.05–0.15 ms |
| **total** | **~1.02 ms** | **~0.11–0.21 ms** |

Worst case falls from ~2.1 ms to ~0.3 ms. After this, roughly 65–85% of what remains is
USB/HID and the Windows input stack — which osu! stable pays identically.

---

## Environment variables

| Variable | Default | Effect |
|---|---|---|
| `OSU_INPUT_HZ` | `8000` | SDL event pump rate. `0` = uncapped spin. Read once at startup. |
| `OSU_MAX_SANE_HZ` | unset | Unset = "Unlimited" means unlimited. Set to `1000` for exact stock behaviour. |
| `OSU_EXTERNAL_UPDATE_PROVIDER` | set by launchers | Disarms Velopack: no self-update, and no hijacking the `osu://` / `.osz` / `.osr` file associations your official install owns. **Keep this set.** |
| `OSU_SPIKE_LOG_MS` | unset (off) | Logs every gap between presented frames longer than this many ms as a `[spike]` line in the runtime log: time in the frame-ready wait, waiting for update, draw and swap, the longest update-thread gap, and GC activity. Only while focused. `play-8k-diagnose.bat` sets it to `5`. See `PLAN-lows.md`. |
| `OSU_FRAME_STATS` | unset (off) | `1` = the game measures its own frame times during gameplay (between "Starting/Ending high performance session"): present gaps with a wait/draw/swap breakdown and a GC flag per frame, update-thread frame times, per-second allocation and GC counters, a marker at the first hit object, and the software delay of every key, mouse button and mouse move (see Input delay below). When the session ends it writes one `[framestats]` line to the runtime log, plus `%APPDATA%\osu-8k\framestats\<UTC yyyyMMdd-HHmmss>.bin` (format under Measuring) and a matching `.txt`; also one `[inputdelay]` line. No cost when off. `play-8k-diagnose.bat` sets it. |
| `OSU_SPIN_PAUSE` | unset (off) | Adds a CPU pause hint to the draw thread's busy-wait for a new update frame. Untested. |
| `OSU_GC_MODE` | unset = `LowLatency` (upstream) | GC latency mode during gameplay: `LowLatency`, `Batch`, `Interactive`, `SustainedLowLatency`, or `Keep` (do not change the mode). Restored at session end. Found 2026-09-30: `LowLatency` gives a gen0 budget of only ~250 KB (about 40 GCs/s in gameplay); see `PLAN-lows.md`. |
| `OSU_GC_COLLECT_AT_START` | unset = `0` (upstream) | Generation collected when a gameplay session starts: `0`, `1`, `2`, or `-1` for none. Skipped when `OSU_NOGC_MB` is set (entering the region collects anyway). |
| `OSU_NOGC_MB` | unset (off) | Size in MB of a no-GC region (`GC.TryStartNoGCRegion`) entered when gameplay starts. The runtime ends it (with one GC) when the budget is used up; the update thread checks once a second and re-arms it. Ended at session end. Every arm is logged as `[gc] no-GC region start/re-arm: ... in X ms`. Sizes the runtime rejects are halved until one is accepted. On this machine (98 GB RAM, net10.0) sizes up to 32768 MB are accepted and 65536 MB is refused. Committed memory grows by about the size. |

### Measuring

Run `play-8k-diagnose.bat`, play a map, and read the `[framestats]` line at the end of the newest
`%APPDATA%\osu-8k\logs\*.runtime.log`, or the `.txt` in `%APPDATA%\osu-8k\framestats`. Average is
frames / time; 1% / 0.1% lows are 1000 / mean of the worst 1% / 0.1% frame times, the same method as
CapFrameX. The line has the whole gameplay session, then `| from first object: ...` (the same stats from
the frame where the first hit object became visible, so loading and intro are excluded), or
`| first object not marked`. `python tools\framestats_analyse.py <file.bin> [capframex.json]` prints both,
an attribution table for slow frames, a 64 Hz check, a "what-if GC frames were typical" line (frames with a gen0 GC replaced by the median, then the 1% / 0.1% lows recomputed), the per-second GC numbers and, for newer files, the GC reason counts from the `.txt`; it also reads the old
headerless v1 files. Only gameplay is measured. No beatmap names, usernames or scores are written.

What is recorded (all preallocated, only when `OSU_FRAME_STATS` is set; about 200 MB of address space,
touched only as frames arrive; recording stops when a buffer fills, 8M frames is over 20 minutes at 6,000 fps):

- Per presented frame (draw thread): present-to-present gap, frame-ready wait, wait for the update thread,
  draw, swap, and a flag "a gen0 GC happened since the previous frame". "Other" = gap minus the four phases.
- Per update frame (update thread): the time since the previous update frame started.
- Per second (update thread): bytes allocated, GC pause time, gen0/gen1/gen2 GC counts (deltas). Also listed
  in the `.txt`.

GC reasons: the `[framestats] gc reasons: AllocSmall N [gen0 N gen1 N], Induced N [...]; N events for N GCs` line (also in the
`.txt`) comes from an in-process `EventListener` on the runtime GC events (GCStart: reason and generation). The runtime events are
enabled only between the session start and end, and the last GCs are counted a fraction of a second after the session ends. The runtime
allocates the event payload: measured about 7 KB per GC (about 280 KB/s at 40 GCs/s, ~3% of the gameplay allocation rate), so the counts can
overstate an unmeasured run by up to about 3%. The "N events for N GCs" cross-check compares with `GC.CollectionCount`. The next line,
`gen0 size before the most recent GC`, is sampled once a second from `GC.GetGCMemoryInfo` (the effective gen0 budget). The `.txt` per-second
table has two extra columns for it (`gen0_before_KB gen0_after_KB`); the `.bin` has no columns for it.

First-object marker: the moment the first hit object becomes visible to the player: `StartTime - TimePreempt` for rulesets whose hit
objects implement `IHasTimePreempt` (osu! and catch). taiko and mania fall back to the first object's `StartTime` (when it is hit).
Files recorded before this change (Step C, 2026-09-30) marked `StartTime` for every ruleset, so their "from first object" numbers start about `TimePreempt` (450 to 1200 ms) later.

#### Input delay

Recorded only under `OSU_FRAME_STATS`, between the session start and end, into preallocated buffers (nothing allocated per event; 4M mouse events
and 64K key/button events per session, then recording stops and a flag is set). Per event, nothing but kinds and times is kept: no key codes, button ids,
positions, beatmap, username or score. Kinds: key down, key up, mouse button down, mouse button up, mouse move (mouse wheel is not measured). OS key repeats are not counted.

| Time | What it is |
|---|---|
| os | The SDL event's own `timestamp` (ns, `SDL_GetTicksNS`), converted to the Stopwatch clock. See the clock finding below; how good it is depends on the event type. |
| pump | `Stopwatch` timestamp taken when the input thread started handling the event in `SDL3Window` |
| update | `Stopwatch` timestamp taken right after the update thread dequeued the input from its handler (`KeyboardHandler` / `MouseHandler`, in `InputHandler.CollectPendingInputs`). Mouse moves are merged per update frame afterwards by `CoalesceMouseMotion`; every physical event still has its own record with that frame's time. |
| present | End of the first `Swap()` whose draw used the update frame that consumed the input or a later one. Identity, not time: the update thread stores its session update-frame index for each of the three draw-root (TripleBuffer) slots, the draw thread reads the index of the slot it draws and stores it per draw frame, and the writer finds the first draw frame with index >= the input's. The consuming frame itself counts because input is applied inside `UpdateSubTree`, before that frame's draw nodes are built. |

The `[inputdelay]` line (runtime log and `.txt`) gives, for keys+buttons and for mouse move, from the first object and for the whole session:
count and mean / p50 / p99 / max in ms of os->pump, pump->update, os->update, os->present and update->present (nearest-rank percentiles). "From first object"
= inputs consumed at or after the first-object marker. Values that are not available (no OS stamp, no later present) are left out of that metric.
`python tools\framestats_analyse.py` prints the same for v3 files.

**The SDL timestamp (measured 2026-09-30, SDL 3.5.0 from `ppy.SDL3-CS 2026.722.0`, Windows 11, scratch program that injects input with `SendInput` into an SDL window):**

- Clock: `SDL_GetTicksNS` is based on `QueryPerformanceCounter`, the same counter as `Stopwatch` (10 MHz here). The offset between the two is constant: paired reads taken 3 s apart differed by 0.0 us, and the game's conversion was 0.1 us off on a fresh pair. The offset is taken at startup from the pair with the smallest gap out of 2,000.
- Keyboard, and mouse buttons and motion when the window is not in relative (raw) mode: SDL stamps these from the Windows message time (`GetMessageTime`, a 32-bit millisecond tick) and converts it to the `SDL_GetTicksNS` scale (`WIN_GetEventTimestamp` in `SDL_windowsevents.c`). Measured: the stamp is stepped (only a few distinct sub-millisecond values over 60 events; it stayed that way with `timeBeginPeriod(1)` set), ran from 3 ms after to 15 ms before the moment the event was sent, and the very first event after startup was 330 ms off (SDL sets its offset from the first message). So for keys os->pump is not a measure of the OS-to-pump delay: it is that delay plus up to about 15 ms of clock granularity, and can be negative. Use pump->update and update->present for keys.
- Mouse buttons and motion in relative mode (raw input, which gameplay uses when the cursor is hidden): measured stamps were 0.03 to 0.3 ms after the send (microsecond resolution, 60 distinct values in 60), while the pump ran up to 4.5 ms after the send. So these are stamped by SDL's raw-input handling when the OS delivers them, not at pump time, and os->pump is a real OS-to-pump delay here. Pen, touch and tablet handlers are not measured.
- Events from the global mouse poll (cursor outside the window) have no SDL timestamp; they are recorded with pump and update times only.
- Not covered: the time from the physical action to the OS stamp (USB/HID, Windows input stack), and from `Swap()` to the photons.

#### `.bin` format, version 3 (little-endian)

64-byte fixed header, then `u32 length` + UTF-8 field list, then the columns in field-list order, each
contiguous. Old v1 files have no header (raw float32 gaps) and do not start with the magic.

| Offset | Type | Meaning |
|---|---|---|
| 0 | char[8] | magic `OSUFRMST` |
| 8 | u32 | version, 3 (2 = same without the input section and with the reserved bytes zero; readers accept 1, 2 and 3) |
| 12 | u32 | header size = offset of the first column (64 + 4 + field list length) |
| 16 / 20 / 24 | u32 | frame count N / update-frame count U / per-second sample count S |
| 28 | i32 | first-object draw-frame index into the frame columns, -1 if never marked |
| 32 | i32 | first-object update-frame index, -1 if never marked |
| 36 | u32 | flags: bit0 draw buffer filled, bit1 update buffer filled |
| 40 | f64 | update origin offset, ms (see below) |
| 48 | u32 | input-event count I (0 in v2) |
| 52 | u32 | input flags: bit0 an input buffer filled, bit1 inputs were lost before the update thread consumed them |
| 56 | f64 | first-object time, ms after the draw origin, -1 if never marked. An input is "from first object" if its update time (pump_ms + pump_to_update_ms) is at or after this. |

Field list: `frame[gap_ms:f32,ready_wait_ms:f32,update_wait_ms:f32,draw_ms:f32,swap_ms:f32,gc0:u8];update[frame_ms:f32];second[elapsed_s:f64,alloc_bytes:u64,gc_pause_ms:f64,gen0:u32,gen1:u32,gen2:u32];input[pump_ms:f64,os_to_pump_ms:f32,pump_to_update_ms:f32,update_to_present_ms:f32,kind:u8]`.
`frame` columns have N entries, `update` U, `second` S. Draw frame k is presented at sum(gap_ms[0..k]) after the
origin (the first present of the session, not itself recorded). Update record j is the time from the start of
update frame j to the start of update frame j+1; update frame j starts at `update origin offset` +
sum(update frame_ms[0..j-1]) after the draw origin. `second` rows are deltas over the interval ending at
`elapsed_s` (seconds after update frame 0; the last row is the partial final interval). gen0 counts every GC,
gen1 every gen1+gen2 GC, gen2 gen2 only. `input` columns have I entries (keyboard-handler records first, then mouse-handler records, each in the
order the update thread consumed them): `pump_ms` = pump time, ms after the draw origin; `os_to_pump_ms`, `pump_to_update_ms`, `update_to_present_ms` as in
Input delay (NaN = not available); `kind` 0 key down, 1 key up, 2 mouse button down, 3 mouse button up, 4 mouse move. os->update = os_to_pump + pump_to_update;
os->present adds update_to_present.

### A/B launchers

Each is `play-8k-diagnose.bat` plus one change (a `REM` in the file says what it tests). Play the same map
once with each and compare the `from first object` lows.

The rounds below are finished and their launchers now live in `launchers-retired\` (results in
`PLAN-lows.md`). New test rounds use numbered launchers in play order: `1-<name>.bat`, `2-<name>.bat`, ...

| Launcher | Change |
|---|---|
| `launchers-retired/play-8k-ab-gen0.bat` | `DOTNET_GCgen0size=0x10000000` (256 MB gen0 budget, fewer gen0 GCs) |
| `launchers-retired/play-8k-ab-pgo.bat` | `DOTNET_TieredPGO=0` + `DOTNET_TC_CallCountingDelayMs=0` (JIT tiers up straight away, no PGO) |
| `launchers-retired/play-8k-ab-notiered.bat` | `DOTNET_TieredCompilation=0` (full optimisation at first call) |
| `launchers-retired/play-8k-ab-spinpause.bat` | `OSU_SPIN_PAUSE=1` |
| `launchers-retired/play-8k-ab-gcmode-sustained.bat` | `OSU_GC_MODE=SustainedLowLatency` (gameplay GC mode; the scratch test gave ~14 MB per gen0 GC instead of ~250 KB) |
| `launchers-retired/play-8k-ab-gcmode-interactive.bat` | `OSU_GC_MODE=Interactive` |
| `launchers-retired/play-8k-ab-gen0budget.bat` | `DOTNET_GCgen0MaxBudget=0x10000000` (256 MB; `GCgen0size` alone was ignored) |
| `launchers-retired/play-8k-ab-nogc.bat` | `OSU_NOGC_MB=256` (no-GC region for gameplay, re-armed when used up) |

---

## What was changed

### `osu-framework` (commit `fd44c77`)
- `Platform/ThreadRunner.cs` — **the 1 ms floor.** The SDL event pump was pinned to
  `DEFAULT_ACTIVE_HZ` (1000) in multithreaded mode and the frame limiter could never
  reach it. Now follows `OSU_INPUT_HZ`.
- `Timing/ThrottledFrameClock.cs` — added `SpinWaitThreshold`. The wait is split: the
  kernel timer gets everything beyond the threshold, the tail is burnt on-CPU with
  `Thread.SpinWait`. Windows' waitable timer cannot resolve 0.125 ms, so without this
  8000 Hz would overshoot to ~2000. Also skips the throttle path entirely when uncapped.
- `Threading/InputThread.cs` — opts the input thread (only) into a 0.5 ms spin tail, and
  logs effective Hz every 5 s.
- `Platform/GameHost.cs` — `maximum_sane_fps` was hardcoded to 1000 and `Math.Min`'d over
  both limiters, which is why "Unlimited" meant 1000. Now `int.MaxValue` unless
  `OSU_MAX_SANE_HZ` is set. Also allows tearing outside exclusive fullscreen.
- `Configuration/FrameSync.cs` — dropdown label "Basically unlimited" → "Unlimited".
- `FrameworkEnvironment.cs` — declares/parses both new variables.
- `Platform/GCSession.cs` — gameplay GC control called from the game's high performance session: `OSU_GC_MODE`, `OSU_GC_COLLECT_AT_START`,
  `OSU_NOGC_MB` (no-GC region with a once-a-second re-arm from `GameHost.UpdateFrame`, every arm logged with its cost). With no variable set it does
  exactly what upstream did (LowLatency, `GC.Collect(0)`, restore).
- `Platform/GCReasonListener.cs` — under `OSU_FRAME_STATS`, counts GC reasons and generations from the runtime's GC events (see Measuring).
- `Platform/FrameStats.cs` — opt-in (`OSU_FRAME_STATS`) in-game frame-time recorder: the draw thread stores
  the present gap, wait/draw/swap phases and a gen0-GC flag per present (timestamps shared with
  `FrameSpikeLog` in `GameHost.DrawFrame`) into preallocated buffers; the update thread stores its frame times and
  per-second alloc/GC counters; `MarkFirstObject()` marks the first hit object. `Begin()`/`End()` bracket a session
  and a background task writes the `[framestats]` summary and the `.bin`/`.txt`. Also the input-delay recorder (Step D, 2026-09-30): the input handlers
  (`KeyboardHandler`, `MouseHandler`) and `SDL3Window` hand it each event's SDL timestamp and pump time, `CollectPendingInputs` the consume time, and
  `GameHost` the update-frame index of each draw-root buffer.

### `osu` (commit `de5730a`)
- `osu.Desktop/Program.cs` — `base_game_name` `osu` → `osu-8k`. This single change moves
  the entire data directory.
- `osu.Game/OsuGame.cs` — separate IPC pipe, so both clients can run at once.
- `osu.Game/OsuGameBase.cs` — window title "osu! (8k)".
- `osu.Game/Screens/Play/Player.cs` — while `OSU_FRAME_STATS` is recording, calls `FrameStats.MarkFirstObject()` when the
  gameplay clock reaches the moment the first hit object becomes visible (`StartTime - TimePreempt` where the object has one, else `StartTime`).
- `osu.Desktop/Performance/HighPerformanceSessionManager.cs` — calls `FrameStats.Begin()` / `End()` and `GCSession.Begin()` / `End()`
  (instead of the hard-coded LowLatency and `GC.Collect(0)`) when the gameplay high-performance session starts / ends.
- `osu.Game/Graphics/UserInterface/FPSCounter.cs` — stops rendering permanently red and
  never fading once the target rate is `int.MaxValue`.

---

## ⚠️ Never do this

**Do not symlink, junction, or copy `%APPDATA%\osu\files` (or `client.realm`, or
`storage.ini`) into `%APPDATA%\osu-8k`.**

lazer runs `RealmFileStore.Cleanup()` on every startup, which deletes every blob the
current realm does not reference. A fresh realm references none of your library, so a
linked `files/` directory means **your entire official beatmap library is deleted on
first launch.** This build starts empty by design.

## ✅ But "Import from osu!stable" IS safe — use it

The above warning is about manually symlinking or copying your **lazer** (`%APPDATA%\osu`)
file store. It does *not* apply to lazer's built-in first-run **import from osu!stable**,
which is the correct way to fill this build.

That import is safe for two separate reasons:

1. **It reads osu!stable, never your lazer install.** Verified: after a full import of 142
   beatmaps, `%APPDATA%\osu` stayed at 4859 files with `client.realm` byte-identical.
2. **It uses hard links** (`Hard link support for beatmaps is True` in the runtime log), so
   the imported files are *referenced* by the new realm — cleanup won't touch them — and
   they cost almost no extra disk. Proof:

   ```
   > fsutil hardlink list "%APPDATA%\osu-8k\files\d\df\df33b31b..."
   \Users\<you>\AppData\Roaming\osu-8k\files\d\df\df33b31b...
   \Users\<you>\AppData\Local\osu!\Songs\<beatmap folder>\<file>
   ```

   Two names, one copy on disk. Hard links are symmetric: deleting a beatmap in this build
   leaves stable's copy alone, and vice versa. Data survives until the last link is removed.

Side effect worth knowing: your stable `Songs` folder's *directory* timestamp updates during
the import. That is NTFS refreshing the directory entry as link counts change — no file
contents are written.

---

## Account safety

Building lazer from source is not against the rules, but this binary's timing precision
exceeds any official client's, and every score carries `version_hash` = MD5 of the
executable. Treat this as an offline/practice client, or use a secondary account. Do not
log the same account into both clients simultaneously, especially in multiplayer.

`play-8k-stock.bat` reproduces stock timing exactly if you want to submit from this build.

---

## Not done — the remaining ~0.1 ms

Hit judgement is **not** timestamped from the input event. SDL3 stamps every event with a
nanosecond timestamp and osu!framework discards it for keys and buttons; the hit is judged
at `Time.Current` of whatever update frame happens to consume it
(`DrawableHitObject.cs:767`). peppy confirmed this upstream on ppy/osu#26457:
*"It's a framework side issue which requires support for timestamped input."*

So accuracy is still quantised to the update frame — these patches shrink that quantum
from ~2 ms to ~0.1–0.3 ms, but do not make it frame-rate-independent the way stable is.

Carrying `evtKey.timestamp` through `IInput` → `KeyboardKeyInput` → `CheckForResult` would
reach ~0.2 ms at **zero CPU cost**, and is the only change that truly matches stable. It
spans 6+ files and changes public framework API, so it was scoped out of this build.

There is also a verified systematic **one-update-frame early bias**: the root input manager
dispatches the press before `FrameStabilityContainer` advances the gameplay clock for that
frame. These patches shrink it rather than remove it.

**Expect your average offset to shift 1–2 ms later** — that is the bias being corrected,
not new latency. Re-run offset calibration after the first session.

---

## Rebuild / revert

```powershell
# rebuild
cd <this folder>\osu
dotnet build osu.Desktop\osu.Desktop.csproj -c Release

# revert everything, keep the clones
git -C <this folder>\osu           checkout master
git -C <this folder>\osu-framework checkout master
```

`nuget.config` in this folder adds nuget.org as a package source for these clones only —
your machine-level NuGet config was left alone (its `<packageSources>` is empty, which is
why a plain `dotnet restore` fails here).

## Tuning if it feels worse

Three threads now spin with no yield, so the OS scheduler becomes the dominant source of
worst-case latency. On fewer than 6 physical cores, or on battery, this can be net worse
than stock. Back off in this order:

1. Frame limiter → `8x refresh` (the input pump stays uncapped — it no longer routes
   through `maximum_sane_fps`)
2. `OSU_INPUT_HZ=2000`
3. `play-8k-stock.bat`
