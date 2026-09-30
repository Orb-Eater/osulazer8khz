# osu!lazer 8k - NEXT (handoff for a cloud agent, written 2026-09-30)

This branch (`8k-next`) is a patched osu!lazer: 8 kHz input pump, a truly uncapped frame rate, in-game
frame and input-delay measurement, and gameplay GC control. The owner plays it on Windows and runs every
test. You cannot run or play the game; you write the code, prove it compiles, and prepare numbered test
launchers. The owner downloads your work with `update-8k.bat`, plays, and brings the numbers back.

Read first: `8k/README.md` (every patch, env var and the `.bin` format), then `8k/PLAN-lows.md`
(all evidence; the last section, "Step D results", is the current picture). Raw analyser output of the
latest round: `8k/results/round-D/`.


## Layout - two branches, one repo

| Branch | Content | Owner's local folder |
|---|---|---|
| `8k-next` | osu (game) + this `8k/` folder | `osu\` |
| `8k-next-framework` | osu-framework (most patches live here) | `osu-framework\` |

`osu.Game/osu.Game.csproj` references `..\..\osu-framework\osu.Framework\osu.Framework.csproj`, so the
framework must sit NEXT TO the osu checkout. In the cloud clone, from the osu checkout's root:

    git fetch origin 8k-next-framework
    git worktree add ../osu-framework 8k-next-framework     # same repo, same remote, so push works
    dotnet build osu.Desktop -c Release                     # must be 0 errors; compare warnings before/after

Push only to `8k-next` and `8k-next-framework`. Never push `master` or `8khz-support-+-more`, never
force-push, never rewrite history the owner already pulled (`update-8k.bat` is fast-forward only; a
rewritten branch makes it stop).


## Rules (from the owner - these override anything generic)

1. **Plan before you build.** Write the plan for a step into `8k/PLAN-lows.md` (what, why, how it is
   measured, what the launchers test) before code. If a step is not in "Agreed next steps" below, ask.
2. **Never trade input delay for fps.** Goal order: gameplay 1% low of 2,000+ fps, then lower input delay.
   Nothing becomes a default until its input delay is measured and the owner says yes.
3. **Everything new is opt-in behind an env var**, zero cost when off, preallocated, no per-frame or
   per-event allocation on the hot paths. Upstream behaviour is the default.
4. **No Windows / NVIDIA / power plan / affinity / priority / driver suggestions, and no code that sets
   them.** Process Lasso owns that. Fixes go in the program.
5. **This repo is PUBLIC. No personal data** in code, docs, commits or results: no beatmap names,
   usernames, scores, user folder paths or screenshots. The game's runtime logs contain beatmap names -
   if the owner pastes log lines, copy only the numbers.
6. Trust the game's own `[framestats]` / `[inputdelay]`, not CapFrameX (it misreports at ~6,000 fps).
   Loading, menus, song select and results do not count; everything from the first circle becoming
   visible counts (the "from first object" numbers).
7. Say when a number cannot be computed rather than computing a worse one. Record honestly (see the
   key timestamp finding below).
8. Commit messages end with the Co-Authored-By / session lines your harness gives you.


## Test rounds (how the owner tests your work)

- Create launchers in `8k/launchers/` NUMBERED in play order: `1-<name>.bat`, `2-<name>.bat`, ...
  Each is a copy of `8k/launchers/play-8k-diagnose.bat` with ONE change and a `REM` saying what it tests.
  Paths are relative (`%~dp0osu\...`) - keep them that way. Always include `1-baseline.bat` (no change).
- `update-8k.bat` (owner side) pulls both branches, builds Release, moves the old numbered launchers to
  `launchers-retired\` and copies yours in. It only copies `N-*.bat`; `play-8k*.bat` are changed only
  with the owner's yes.
- The owner plays the same map once per launcher and reports the `[framestats]` and `[inputdelay]`
  numbers (or a local session analyses the `.bin` files with `8k/tools/framestats_analyse.py`).
  Also ask what each run FELT like - the owner rejected two modes for felt stutter.
- Write each round's results table into `8k/PLAN-lows.md`.


## State (end of 2026-09-30)

From first object, round D (same map; delays in ms, keys+buttons, ~915 events per run):

| run | 1% low | 0.1% low | max ms | GCs | pump->update mean | update->present mean / p99 / max |
|---|---|---|---|---|---|---|
| baseline (upstream LowLatency) | 1,764 | 1,111 | 4.0 | 5,270 | 0.067 | 0.661 / 1.51 / 1.74 |
| `OSU_GC_MODE=Interactive` | **2,193** | **1,164** | 16.8 | 82 | 0.071 | **0.543 / 1.05** / 17.0 |
| `OSU_GC_MODE=SustainedLowLatency` | 2,163 | 1,132 | 14.2 | 72 | 0.069 | 0.556 / 1.06 / 14.4 |

- Interactive passes the fps target AND lowers typical input delay. The cost is rare long frames
  (one ~17 ms frame per map). **Whether it becomes the default in `play-8k.bat` is the owner's call -
  do not change it.**
- `pump->update` is ~0.07 ms. `update->present` (~0.55 ms, ~3 frames at ~0.16 ms/frame) is the largest
  software share left.
- **Key timestamps are coarse.** SDL3 on Windows stamps keyboard events from the Windows message time
  (`GetMessageTime`, 15.6 ms tick): measured os->pump mean ~8.3 ms spread 0-16.5 ms. Useless as latency.
  Raw (relative) mouse events carry a real QPC stamp.
- **The cursor is not measured.** The owner plays with a Wacom tablet through osu-framework's
  `PenHandler` (SDL pen events). Step D records only KeyboardHandler and MouseHandler, so mouse move
  n=0 in every run.
- Still open for the lows: the 0.3-0.5 ms slow band (swap ~0.2 ms vs 0.033 typical, no GC) and a strong
  64 Hz (15.625 ms) rhythm in the slow frames.


## Agreed next steps (in order)

Written by the local session on 2026-09-30 from the round D results; the owner asked for a cloud agent
to work on "the next steps". If the owner's message to you says otherwise, the owner wins. Plan each in
`PLAN-lows.md` first.

**E1 - measure the tablet cursor.** Add the Pen handler to the input-delay recorder (pen motion and pen
touch/button; a third group "pen" in the `[inputdelay]` line, the `.bin` and the analyser; bump the
format version, keep reading v1-v3). Find out from SDL3 source what `SDL_PenMotionEvent.timestamp` is on
Windows (WM_POINTER message time? QPC?) and document it the same way as keys. Kinds only - no positions,
pressure or button ids.

**E2 - key timestamps and key delay.** SDL3 has `SDL_HINT_WINDOWS_RAW_KEYBOARD`: keyboard through raw
input. Check in SDL source whether that gives keys a real QPC stamp and whether it changes when the event
reaches the pump. Add it opt-in (`OSU_RAW_KEYBOARD=1`), check it does not break text input / IME in
menus (read the code paths; the owner will also check), and prepare a round:
`1-baseline`, `2-rawkeyboard`.

**E3 - the update->present pipeline (input delay).** Work out from `GameHost` / `TripleBuffer` / the draw
loop why an input consumed by update frame k reaches the screen ~0.55 ms later at ~0.16 ms frames. Record
frame age at present if needed. Propose opt-in changes that shorten it without costing the lows; each
becomes one numbered launcher. Measure with `pump->update` + `update->present` (not os->..., see keys).

**Every round should include an `OSU_GC_MODE=Interactive` variant** of the new change as well as the
upstream-default one, since Interactive is the likely future default.

## Not agreed - ask the owner first
- The slow band (swap/draw) and the 64 Hz rhythm (next lever for the lows).
- Parked: pool pre-warm, ReadyToRun publish, `OSU_SPIN_PAUSE` (lowered avg fps).
- `OSU_NOGC_MB` and `DOTNET_GCgen0MaxBudget`: rejected (felt stutter / no effect).
