@echo off
REM ============================================================
REM  osu!lazer 8k  --  FULLY UNCAPPED INPUT PUMP
REM  OSU_INPUT_HZ=0 means the SDL event pump free-runs with no
REM  sleep and no pacing (~30,000-150,000 Hz observed).
REM
REM  Costs the same CPU as the 8000 Hz default (that already
REM  spins its whole budget) and buys roughly 0.05 ms, which is
REM  below the USB/HID floor. This profile exists because you
REM  asked for the lowest possible, not because it is better.
REM ============================================================

set OSU_EXTERNAL_UPDATE_PROVIDER=1
set OSU_INPUT_HZ=0

start "" "%~dp0osu\osu.Desktop\bin\Release\net10.0\osu!.exe"
