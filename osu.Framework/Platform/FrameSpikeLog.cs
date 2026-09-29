// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Threading;
using osu.Framework.Logging;

namespace osu.Framework.Platform
{
    /// <summary>
    /// Opt-in diagnostics (<c>OSU_SPIKE_LOG_MS</c>) for long gaps between presented frames.
    /// The draw thread reports how long each phase of the current frame took; when the gap since the last present
    /// exceeds the threshold, one line is logged with that breakdown, the longest update-thread gap seen meanwhile,
    /// and the GC activity over the gap. Update-thread gaps over the threshold are logged on their own as well.
    /// </summary>
    internal sealed class FrameSpikeLog
    {
        private readonly long thresholdTicks;

        // Draw thread only.
        private long lastPresent;
        private long waitTicks, readTicks, drawTicks, swapTicks;
        private int emptyReads;
        private GcSnapshot drawGc;

        // Update thread only, except maxUpdateGap which the draw thread reads and resets.
        private long lastUpdateStart;
        private long maxUpdateGap;
        private GcSnapshot updateGc;

        public FrameSpikeLog(double thresholdMs)
        {
            thresholdTicks = (long)(thresholdMs * Stopwatch.Frequency / 1000);
            lastPresent = Stopwatch.GetTimestamp();
            drawGc = updateGc = GcSnapshot.Take();
        }

        public static long Now => Stopwatch.GetTimestamp();

        /// <param name="active">Whether the window is focused. Unfocused, the update rate is throttled on purpose, so gaps are not logged.</param>
        public void UpdateStarted(bool active)
        {
            long now = Now;
            var gc = GcSnapshot.Take();

            if (lastUpdateStart != 0 && active)
            {
                long gap = now - lastUpdateStart;

                long prev;
                while (gap > (prev = Interlocked.Read(ref maxUpdateGap)))
                {
                    if (Interlocked.CompareExchange(ref maxUpdateGap, gap, prev) == prev)
                        break;
                }

                if (gap > thresholdTicks)
                    Logger.Log($"[spike] update thread gap {ms(gap):N2} ms, {gc.Since(updateGc)}");
            }

            lastUpdateStart = now;
            updateGc = gc;
        }

        public void AddWait(long ticks) => waitTicks += ticks;

        public void AddRead(long ticks, bool gotBuffer)
        {
            readTicks += ticks;
            if (!gotBuffer) emptyReads++;
        }

        public void AddDraw(long ticks) => drawTicks += ticks;

        public void AddSwap(long ticks) => swapTicks += ticks;

        public void Presented(bool active, WindowState windowState)
        {
            long now = Now;
            long gap = now - lastPresent;
            long updateGap = Interlocked.Exchange(ref maxUpdateGap, 0);
            var gc = GcSnapshot.Take();

            // Only logged while focused; a gap that spans a focus loss is still caught by the first present after focus returns.
            if (gap > thresholdTicks && active)
            {
                long other = gap - waitTicks - readTicks - drawTicks - swapTicks;

                Logger.Log($"[spike] present gap {ms(gap):N2} ms: frame-ready wait {ms(waitTicks):N2}, waiting for update {ms(readTicks):N2} ({emptyReads} empty reads), "
                           + $"draw {ms(drawTicks):N2}, swap {ms(swapTicks):N2}, other {ms(other):N2}; longest update gap {ms(updateGap):N2} ms; "
                           + $"active {active}, {windowState}; {gc.Since(drawGc)}");
            }

            lastPresent = now;
            waitTicks = readTicks = drawTicks = swapTicks = 0;
            emptyReads = 0;
            drawGc = gc;
        }

        private static double ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        private readonly struct GcSnapshot
        {
            private readonly int gen0, gen1, gen2;
            private readonly TimeSpan pause;

            private GcSnapshot(int gen0, int gen1, int gen2, TimeSpan pause)
            {
                this.gen0 = gen0;
                this.gen1 = gen1;
                this.gen2 = gen2;
                this.pause = pause;
            }

            public static GcSnapshot Take() => new GcSnapshot(GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration());

            public string Since(GcSnapshot earlier) =>
                $"GC gen0 +{gen0 - earlier.gen0} gen1 +{gen1 - earlier.gen1} gen2 +{gen2 - earlier.gen2}, GC pause {(pause - earlier.pause).TotalMilliseconds:N2} ms";
        }
    }
}
