@echo off
REM  ROUND E 3: only change = OSU_RAW_KEYBOARD=1 (keys via raw input, real timestamps).
REM  Also check typing still works in chat, song search and IME.
REM ============================================================
REM  osu!lazer 8k  --  DIAGNOSE RUN A (baseline + spike logger)
REM  Same as play-8k.bat, plus: every gap between presented
REM  frames over 5 ms is written to
REM  %APPDATA%\osu-8k\logs\*.runtime.log as a "[spike]" line,
REM  with where the time went and any GC activity.
REM  Logs only while the window is focused.
REM  OSU_FRAME_STATS=1 also records gameplay frame times: one
REM  "[framestats]" log line plus framestats\*.bin/.txt per map.
REM ============================================================

set OSU_EXTERNAL_UPDATE_PROVIDER=1
set OSU_INPUT_HZ=4000
set OSU_SPIKE_LOG_MS=5
set OSU_FRAME_STATS=1
set OSU_RAW_KEYBOARD=1

start "" "%~dp0osu\osu.Desktop\bin\Release\net10.0\osu!.exe"
