@echo off
REM ============================================================
REM  osu!lazer 8k  --  STOCK BASELINE (for A/B testing)
REM  Restores upstream behaviour exactly:
REM    input pump back to 1000 Hz
REM    "Unlimited" frame limiter clamped back to 1000 fps
REM  Same binary, same data dir. Use this to feel the difference.
REM ============================================================

set OSU_EXTERNAL_UPDATE_PROVIDER=1
set OSU_INPUT_HZ=1000
set OSU_MAX_SANE_HZ=1000

start "" "%~dp0osu\osu.Desktop\bin\Release\net10.0\osu!.exe"
