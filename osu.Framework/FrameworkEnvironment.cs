// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Globalization;
using osu.Framework.Development;
using osu.Framework.Platform;

namespace osu.Framework
{
    public static class FrameworkEnvironment
    {
        public static ExecutionMode? StartupExecutionMode { get; }
        public static bool NoTestTimeout { get; }
        public static bool ForceTestGC { get; }
        public static bool FailFlakyTests { get; }
        public static bool FrameStatisticsViaTouch { get; }
        public static GraphicsSurfaceType? PreferredGraphicsSurface { get; }
        public static string? PreferredGraphicsRenderer { get; }
        public static int? StagingBufferType { get; }
        public static int? VertexBufferCount { get; }
        public static bool NoStructuredBuffers { get; }
        public static string? DeferredRendererEventsOutputPath { get; }
        public static bool UseSDL3 { get; }

        /// <summary>
        /// An optional hard ceiling (in Hz) applied to the "unlimited" frame limiter, read from the
        /// OSU_MAX_SANE_HZ environment variable at process start.
        /// When null (the default), "unlimited" means unlimited. Set OSU_MAX_SANE_HZ=1000 to restore
        /// upstream osu!framework behaviour exactly.
        /// </summary>
        public static int? MaximumSaneFps { get; }

        /// <summary>
        /// Whether consecutive relative mouse motions arriving within one update frame are merged into a
        /// single event, from the OSU_COALESCE_MOUSE environment variable. Defaults to enabled.
        /// Each un-merged motion costs a full positional input queue rebuild plus a hover diff, so at
        /// 4000Hz/8000Hz polling this is the dominant cost in the input path. Set OSU_COALESCE_MOUSE=0 to disable.
        /// </summary>
        public static bool CoalesceMouseMotion { get; }

        /// <summary>
        /// The target rate (in Hz) for the input thread / SDL event pump, from the <c>OSU_INPUT_HZ</c> environment variable.
        /// Unset defaults to 8000. A value of <c>0</c> means uncapped: the clock spins without ever sleeping.
        /// </summary>
        public static double InputHz { get; }

        /// <summary>
        /// Frame-spike logging threshold (in ms), from the <c>OSU_SPIKE_LOG_MS</c> environment variable.
        /// Unset or <c>0</c> disables it. When set, every gap between presented frames longer than this is logged
        /// with a breakdown of where the draw thread spent the time, plus any update-thread stall and GC activity.
        /// </summary>
        public static double SpikeLogMs { get; }

        /// <summary>
        /// Whether in-game frame statistics are enabled, from the <c>OSU_FRAME_STATS</c> environment variable. Defaults to disabled.
        /// When enabled, present-to-present frame times are recorded between <see cref="Platform.FrameStats.Begin"/> and <see cref="Platform.FrameStats.End"/>
        /// and summarised in the runtime log, with the raw times written to the <c>framestats</c> folder of the data directory.
        /// </summary>
        public static bool FrameStats { get; }

        /// <summary>
        /// Whether SDL reads the keyboard through Windows raw input (<c>SDL_HINT_WINDOWS_RAW_KEYBOARD</c>), from the <c>OSU_RAW_KEYBOARD</c> environment variable.
        /// Defaults to disabled (upstream behaviour).
        /// </summary>
        public static bool RawKeyboard { get; }

        /// <summary>
        /// Whether the draw thread's busy-wait for a new update frame issues a CPU pause hint each iteration,
        /// from the <c>OSU_SPIN_PAUSE</c> environment variable. Defaults to disabled (upstream behaviour).
        /// </summary>
        public static bool SpinPause { get; }

        /// <summary>
        /// The GC latency mode used during gameplay, from the <c>OSU_GC_MODE</c> environment variable:
        /// <c>LowLatency</c> (the default, upstream behaviour), <c>Batch</c>, <c>Interactive</c>, <c>SustainedLowLatency</c>,
        /// or <c>Keep</c> (do not change the mode at all). Unset or unrecognised values give the default.
        /// </summary>
        public static string GCMode { get; } = "LowLatency";

        /// <summary>
        /// The generation collected when a gameplay session starts, from <c>OSU_GC_COLLECT_AT_START</c>:
        /// <c>0</c>, <c>1</c> or <c>2</c>, or <c>-1</c> for no collection. Defaults to <c>0</c> (upstream behaviour).
        /// </summary>
        public static int GCCollectAtStart { get; } = 0;

        /// <summary>
        /// Size in megabytes of a no-GC region entered for each gameplay session (and re-entered when its budget runs out),
        /// from <c>OSU_NOGC_MB</c>. Unset or <c>0</c> disables it (upstream behaviour).
        /// </summary>
        public static int NoGCMegabytes { get; }

        /// <summary>
        /// Whether non-SSL requests should be allowed. Debug only. Defaults to disabled.
        /// When disabled, http:// requests will be automatically converted to https://.
        /// </summary>
        public static bool AllowInsecureRequests { get; internal set; }

        static FrameworkEnvironment()
        {
            StartupExecutionMode = Enum.TryParse<ExecutionMode>(Environment.GetEnvironmentVariable("OSU_EXECUTION_MODE"), true, out var mode) ? mode : null;

            // OSU_INPUT_HZ: unset => 8000, "0" => uncapped (pure spin, no sleep at all), N => N Hz.
            InputHz = int.TryParse(Environment.GetEnvironmentVariable("OSU_INPUT_HZ"), out int inputHz) && inputHz >= 0 ? inputHz : 8000;

            NoTestTimeout = parseBool(Environment.GetEnvironmentVariable("OSU_TESTS_NO_TIMEOUT")) ?? false;
            ForceTestGC = parseBool(Environment.GetEnvironmentVariable("OSU_TESTS_FORCED_GC")) ?? false;
            FailFlakyTests = Environment.GetEnvironmentVariable("OSU_TESTS_FAIL_FLAKY") == "1";

            FrameStatisticsViaTouch = parseBool(Environment.GetEnvironmentVariable("OSU_FRAME_STATISTICS_VIA_TOUCH")) ?? false;
            PreferredGraphicsSurface = Enum.TryParse<GraphicsSurfaceType>(Environment.GetEnvironmentVariable("OSU_GRAPHICS_SURFACE"), true, out var surface) ? surface : null;
            PreferredGraphicsRenderer = Environment.GetEnvironmentVariable("OSU_GRAPHICS_RENDERER")?.ToLowerInvariant();

            if (int.TryParse(Environment.GetEnvironmentVariable("OSU_GRAPHICS_VBO_COUNT"), out int count))
                VertexBufferCount = count;

            if (int.TryParse(Environment.GetEnvironmentVariable("OSU_GRAPHICS_STAGING_BUFFER_TYPE"), out int stagingBufferImplementation))
                StagingBufferType = stagingBufferImplementation;

            NoStructuredBuffers = parseBool(Environment.GetEnvironmentVariable("OSU_GRAPHICS_NO_SSBO")) ?? false;

            DeferredRendererEventsOutputPath = Environment.GetEnvironmentVariable("DEFERRED_RENDERER_EVENTS_OUTPUT");

            // Merge consecutive relative mouse motions per update frame. On by default; OSU_COALESCE_MOUSE=0 disables.
            CoalesceMouseMotion = parseBool(Environment.GetEnvironmentVariable("OSU_COALESCE_MOUSE")) ?? true;

            // Opt-in ceiling for the "unlimited" frame limiter. Unset = genuinely uncapped; set to 1000 for stock behaviour.
            if (int.TryParse(Environment.GetEnvironmentVariable("OSU_MAX_SANE_HZ"), out int maxSaneHz) && maxSaneHz > 0)
                MaximumSaneFps = maxSaneHz;

            // Frame-spike diagnostics and the draw-thread spin hint. Both off by default.
            SpikeLogMs = double.TryParse(Environment.GetEnvironmentVariable("OSU_SPIKE_LOG_MS"), NumberStyles.Float, CultureInfo.InvariantCulture, out double spikeMs) && spikeMs > 0 ? spikeMs : 0;
            FrameStats = parseBool(Environment.GetEnvironmentVariable("OSU_FRAME_STATS")) ?? false;
            SpinPause = parseBool(Environment.GetEnvironmentVariable("OSU_SPIN_PAUSE")) ?? false;
            RawKeyboard = parseBool(Environment.GetEnvironmentVariable("OSU_RAW_KEYBOARD")) ?? false;

            // Gameplay GC behaviour. All default to upstream behaviour.
            string? gcMode = Environment.GetEnvironmentVariable("OSU_GC_MODE");

            foreach (string known in new[] { "LowLatency", "Batch", "Interactive", "SustainedLowLatency", "Keep" })
            {
                if (string.Equals(gcMode?.Trim(), known, StringComparison.OrdinalIgnoreCase))
                    GCMode = known;
            }

            if (int.TryParse(Environment.GetEnvironmentVariable("OSU_GC_COLLECT_AT_START"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int collectAt) && collectAt >= -1 && collectAt <= 2)
                GCCollectAtStart = collectAt;

            NoGCMegabytes = int.TryParse(Environment.GetEnvironmentVariable("OSU_NOGC_MB"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int noGcMb) && noGcMb > 0 ? noGcMb : 0;

            if (DebugUtils.IsDebugBuild)
                AllowInsecureRequests = parseBool(Environment.GetEnvironmentVariable("OSU_INSECURE_REQUESTS")) ?? false;

            if (parseBool(Environment.GetEnvironmentVariable("OSU_SDL3")) is bool userSDL3Override)
                UseSDL3 = userSDL3Override;
            else
                // Some desktop platforms have remaining issues, see https://github.com/ppy/osu-framework/issues/6540.
                UseSDL3 = RuntimeInfo.OS != RuntimeInfo.Platform.macOS;
        }

        private static bool? parseBool(string? value)
        {
            switch (value)
            {
                case "0":
                    return false;

                case "1":
                    return true;

                default:
                    return bool.TryParse(value, out bool b) ? b : null;
            }
        }
    }
}
