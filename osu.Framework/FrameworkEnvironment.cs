// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
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
        /// The target rate (in Hz) for the input thread / SDL event pump, from the <c>OSU_INPUT_HZ</c> environment variable.
        /// Unset defaults to 8000. A value of <c>0</c> means uncapped: the clock spins without ever sleeping.
        /// </summary>
        public static double InputHz { get; }

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

            // Opt-in ceiling for the "unlimited" frame limiter. Unset = genuinely uncapped; set to 1000 for stock behaviour.
            if (int.TryParse(Environment.GetEnvironmentVariable("OSU_MAX_SANE_HZ"), out int maxSaneHz) && maxSaneHz > 0)
                MaximumSaneFps = maxSaneHz;

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
