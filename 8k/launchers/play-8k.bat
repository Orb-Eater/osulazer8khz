@echo off
REM ============================================================
REM  osu!lazer 8k  --  DEFAULT PROFILE
REM  Data dir: %APPDATA%\osu-8k   (your official install is untouched)
REM ============================================================

REM Disarms Velopack: no self-update, and no hijacking the osu:// /
REM .osz / .osr file associations your official install owns.
set OSU_EXTERNAL_UPDATE_PROVIDER=1

REM Match the pump to your mouse's actual report rate. Polling faster than
REM the device reports just spins a core looking for events that do not
REM exist yet, and costs zero latency to drop. Set this to your real
REM polling rate: 4000 for a 4kHz mouse, 8000 for an 8kHz one.
set OSU_INPUT_HZ=4000

REM Merge consecutive mouse motions that land in the same update frame.
REM On by default; this line is here so you can A/B it with =0.
REM set OSU_COALESCE_MOUSE=0

REM ---- AUDIO LATENCY (opt-in, upstream's own testing hook) ----
REM osu!framework hardcodes Bass.DeviceBufferLength = 10 (ms). This env var
REM overrides the BASS device period and drops the buffer to twice that period
REM instead. Positive = milliseconds, negative = exact sample count (-256 etc).
REM Upstream's own log message warns: "Incorrect settings may lead to serious
REM issues." Start at 5, then 2. If you get crackling or dropouts, back off.
REM Your official install runs the default, so changing this makes the two
REM builds sound different -- A/B it before keeping it.
REM set OSU_TEMP_TESTING_BASS_CONFIG_DEV_PERIOD=5

start "" "%~dp0osu\osu.Desktop\bin\Release\net10.0\osu!.exe"
