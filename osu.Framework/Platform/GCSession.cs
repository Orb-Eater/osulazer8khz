// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using osu.Framework.Logging;

namespace osu.Framework.Platform
{
    /// <summary>
    /// GC behaviour for a gameplay session. Called by the game's high performance session manager at the start and end of a session.
    /// With no environment variables set this is exactly the upstream behaviour: <see cref="GCLatencyMode.LowLatency"/> plus one gen0 collection at
    /// the start, and the previous mode restored at the end.
    /// <list type="bullet">
    /// <item><c>OSU_GC_MODE</c>: the latency mode (see <see cref="FrameworkEnvironment.GCMode"/>).</item>
    /// <item><c>OSU_GC_COLLECT_AT_START</c>: the generation collected at the start (see <see cref="FrameworkEnvironment.GCCollectAtStart"/>).</item>
    /// <item><c>OSU_NOGC_MB</c>: enter a no-GC region of this size for the session and re-arm it whenever the runtime ends it because the budget ran out
    /// (see <see cref="FrameworkEnvironment.NoGCMegabytes"/>). Re-arming is checked once a second from <see cref="Tick"/> on the update thread,
    /// and every arm is logged with what it cost, because entering the region performs a blocking GC.</item>
    /// </list>
    /// </summary>
    public static class GCSession
    {
        private const long megabyte = 1024 * 1024;
        private const long minimum_no_gc_bytes = 16 * megabyte;

        private static readonly object gate = new object();

        private static bool nonDefault =>
            FrameworkEnvironment.GCMode != "LowLatency" || FrameworkEnvironment.GCCollectAtStart != 0 || FrameworkEnvironment.NoGCMegabytes > 0;

        private static GCLatencyMode originalMode;
        private static bool modeChanged;
        private static GCLatencyMode appliedMode;

        private static volatile bool noGcActive;
        private static long noGcBytes;
        private static long nextCheck;
        private static int armFailures;

        /// <summary>
        /// Whether a no-GC region is being maintained for the running session. Cheap enough to test every update frame.
        /// </summary>
        internal static bool NoGCActive => noGcActive;

        /// <summary>
        /// Starts the GC behaviour for a gameplay session. Call once when the first session starts.
        /// </summary>
        public static void Begin()
        {
            lock (gate)
            {
                originalMode = GCSettings.LatencyMode;
                modeChanged = false;

                var target = parseMode(FrameworkEnvironment.GCMode);

                if (target != null)
                {
                    try
                    {
                        GCSettings.LatencyMode = target.Value;
                        appliedMode = target.Value;
                        modeChanged = true;
                    }
                    catch (Exception e)
                    {
                        Logger.Log($"[gc] could not set latency mode {target}: {e.Message}");
                    }
                }

                int mb = FrameworkEnvironment.NoGCMegabytes;

                if (nonDefault)
                    Logger.Log($"[gc] session start: OSU_GC_MODE={FrameworkEnvironment.GCMode} (was {originalMode}, now {GCSettings.LatencyMode}), collect at start {(FrameworkEnvironment.GCCollectAtStart < 0 ? "none" : "gen" + FrameworkEnvironment.GCCollectAtStart)}, OSU_NOGC_MB={mb}");

                if (mb > 0)
                {
                    noGcBytes = mb * megabyte;
                    armFailures = 0;
                    nextCheck = Stopwatch.GetTimestamp() + Stopwatch.Frequency;

                    // Entering the region performs a blocking GC itself, so the start-of-session collection is not needed.
                    noGcActive = arm("start");
                    return;
                }

                // Without doing this, the new GC mode won't kick in until the next GC, which could be at a more noticeable point in time.
                if (FrameworkEnvironment.GCCollectAtStart >= 0)
                    GC.Collect(FrameworkEnvironment.GCCollectAtStart);
            }
        }

        /// <summary>
        /// Ends the GC behaviour for a gameplay session and restores the previous latency mode.
        /// </summary>
        public static void End()
        {
            lock (gate)
            {
                bool wasActive = noGcActive;
                noGcActive = false;

                if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
                {
                    try
                    {
                        GC.EndNoGCRegion();
                    }
                    catch (Exception e)
                    {
                        // Throws if the region already ended (budget used up) or another GC mode call ended it.
                        Logger.Log($"[gc] EndNoGCRegion: {e.GetType().Name}");
                    }
                }

                if (wasActive)
                    Logger.Log($"[gc] no-GC region ended with the session (mode now {GCSettings.LatencyMode})");

                // Upstream only restores when the mode is still the one it set; this is the same rule for whichever mode was applied.
                if (modeChanged && GCSettings.LatencyMode == appliedMode)
                {
                    try
                    {
                        GCSettings.LatencyMode = originalMode;
                    }
                    catch (Exception e)
                    {
                        Logger.Log($"[gc] could not restore latency mode {originalMode}: {e.Message}");
                    }
                }

                modeChanged = false;
            }
        }

        /// <summary>
        /// Called on the update thread every update frame while <see cref="NoGCActive"/>. Once a second, re-arms the no-GC region if the runtime ended it.
        /// </summary>
        internal static void Tick()
        {
            long now = Stopwatch.GetTimestamp();
            if (now < nextCheck) return;

            nextCheck = now + Stopwatch.Frequency;

            lock (gate)
            {
                if (!noGcActive || GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
                    return;

                if (!arm("re-arm"))
                {
                    if (++armFailures >= 3)
                    {
                        noGcActive = false;
                        Logger.Log("[gc] no-GC region could not be re-armed 3 times in a row, giving up for this session");
                    }
                }
                else
                    armFailures = 0;
            }
        }

        private static bool arm(string why)
        {
            long bytes = noGcBytes;
            var c = CultureInfo.InvariantCulture;

            while (bytes >= minimum_no_gc_bytes)
            {
                int gcsBefore = GC.CollectionCount(0);
                var sw = Stopwatch.StartNew();

                try
                {
                    bool ok = GC.TryStartNoGCRegion(bytes);
                    sw.Stop();

                    Logger.Log(string.Create(c, $"[gc] no-GC region {why}: {bytes / megabyte} MB {(ok ? "armed" : "refused")} in {sw.Elapsed.TotalMilliseconds:F2} ms ({GC.CollectionCount(0) - gcsBefore} GCs during the call, mode {GCSettings.LatencyMode})"));
                    noGcBytes = bytes;
                    return ok;
                }
                catch (ArgumentOutOfRangeException e)
                {
                    sw.Stop();
                    Logger.Log(string.Create(c, $"[gc] no-GC region of {bytes / megabyte} MB is over the allowed limit ({e.Message.Split('\n')[0]}), trying {bytes / 2 / megabyte} MB"));
                    bytes /= 2;
                }
                catch (InvalidOperationException e)
                {
                    // Already inside a region, or a GC mode change is in progress.
                    Logger.Log($"[gc] no-GC region {why}: {e.Message}");
                    return GCSettings.LatencyMode == GCLatencyMode.NoGCRegion;
                }
            }

            Logger.Log("[gc] no-GC region: no size down to 16 MB was accepted, disabled for this session");
            return false;
        }

        private static GCLatencyMode? parseMode(string mode) => mode switch
        {
            "Batch" => GCLatencyMode.Batch,
            "Interactive" => GCLatencyMode.Interactive,
            "SustainedLowLatency" => GCLatencyMode.SustainedLowLatency,
            "Keep" => null,
            _ => GCLatencyMode.LowLatency,
        };
    }
}
