// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Text;
using System.Threading;

namespace osu.Framework.Platform
{
    /// <summary>
    /// Counts why each GC happened, using an in-process <see cref="EventListener"/> on the runtime's GC events.
    /// Only used by <see cref="FrameStats"/> (<c>OSU_FRAME_STATS=1</c>): the listener is created on the first <see cref="Begin"/>
    /// and the runtime events are enabled only between <see cref="Begin"/> and <see cref="Finish"/>, so nothing is raised outside a recorded session.
    /// <para>
    /// GCStart (V2) carries the reason and the depth (generation). Counts go into a fixed array indexed by reason and depth, so counting
    /// allocates nothing. The runtime itself allocates the event payload (a boxed object array per event), and the GC keyword at Informational
    /// level raises several other events per GC. Measured on net10.0 with forced gen0 GCs at 40/s and nothing else allocating: about 7 KB per GC,
    /// 280 KB/s at 40 GCs/s, which is about 3% of the 10 MB/s allocation rate seen in gameplay. That can add up to ~3% more GCs (fewer if the
    /// GC budget is large), so counts taken with the listener slightly overstate an unmeasured run. The per-frame GC flag is unaffected in kind.
    /// </para>
    /// </summary>
    internal sealed class GCReasonListener : EventListener
    {
        private const int reason_count = 16;
        private const int depth_count = 3;
        private const int gc_start_event_id = 1;

        private static readonly string[] reason_names =
        {
            "AllocSmall", "Induced", "LowMemory", "Empty", "AllocLarge", "OutOfSpaceSOH", "OutOfSpaceLOH", "InducedNotForced",
            "Internal", "InducedLowMemory", "InducedCompacting", "LowMemoryHost", "PMFullGC", "LowMemoryHostBlocking", "Reason14", "Reason15"
        };

        // Static, because EventListener's constructor can call OnEventSourceCreated before instance fields are initialised.
        private static readonly int[] counts = new int[reason_count * depth_count];
        private static GCReasonListener? instance;
        private static EventSource? runtimeSource;
        private static bool wanted;
        private static long endTicks; // UTC ticks after which events are ignored; 0 = still recording
        private static int reasonIndex = -1;
        private static int depthIndex = -1;
        private static long eventsSeen;

        /// <summary>
        /// Starts counting. Creates the listener the first time.
        /// </summary>
        public static void Begin()
        {
            try
            {
                lock (counts)
                {
                    Array.Clear(counts);
                    Interlocked.Exchange(ref eventsSeen, 0);
                    Volatile.Write(ref endTicks, 0);
                    wanted = true;

                    instance ??= new GCReasonListener();

                    if (runtimeSource != null)
                        instance.EnableEvents(runtimeSource, EventLevel.Informational, (EventKeywords)0x1);
                }
            }
            catch (Exception e)
            {
                Logging.Logger.Log($"[framestats] GC reason listener failed to start: {e.Message}");
            }
        }

        /// <summary>
        /// Marks the end of the recorded interval. Events stamped later than this are ignored.
        /// </summary>
        public static void MarkEnd() => Volatile.Write(ref endTicks, DateTime.UtcNow.Ticks);

        /// <summary>
        /// Stops the runtime events and returns the counts, formatted for the log line. Call after <see cref="MarkEnd"/>, ideally a little later
        /// (the runtime delivers events to the listener with a short delay), and from a background thread.
        /// </summary>
        /// <param name="totalGcs">The number of GCs that actually happened in the interval (from <c>GC.CollectionCount(0)</c>), for cross-checking.</param>
        public static string Finish(int totalGcs)
        {
            try
            {
                lock (counts)
                {
                    wanted = false;

                    if (instance != null && runtimeSource != null)
                        instance.DisableEvents(runtimeSource);
                }
            }
            catch (Exception e)
            {
                Logging.Logger.Log($"[framestats] GC reason listener failed to stop: {e.Message}");
            }

            var c = CultureInfo.InvariantCulture;
            var sb = new StringBuilder("gc reasons: ");
            long total = 0;
            bool any = false;

            for (int r = 0; r < reason_count; r++)
            {
                int sum = 0;
                for (int d = 0; d < depth_count; d++)
                    sum += counts[r * depth_count + d];

                if (sum == 0) continue;

                if (any) sb.Append(", ");
                any = true;
                total += sum;

                sb.Append(reason_names[r]).Append(' ').Append(sum.ToString(c)).Append(" [");

                bool first = true;
                for (int d = 0; d < depth_count; d++)
                {
                    int n = counts[r * depth_count + d];
                    if (n == 0) continue;
                    if (!first) sb.Append(' ');
                    first = false;
                    sb.Append("gen").Append(d.ToString(c)).Append(' ').Append(n.ToString(c));
                }

                sb.Append(']');
            }

            if (!any)
                sb.Append("none seen");

            sb.Append(string.Create(c, $"; {total} events for {totalGcs} GCs"));
            return sb.ToString();
        }

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name != "Microsoft-Windows-DotNETRuntime")
                return;

            runtimeSource = source;

            // The runtime source can appear after Begin() asked for it (or, from the constructor, before instance is assigned).
            if (wanted && instance != null)
                EnableEvents(source, EventLevel.Informational, (EventKeywords)0x1);
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (e.EventId != gc_start_event_id)
                return;

            long end = Volatile.Read(ref endTicks);
            if (end != 0 && e.TimeStamp.Ticks > end)
                return;

            var names = e.PayloadNames;
            var payload = e.Payload;
            if (names == null || payload == null)
                return;

            if (reasonIndex < 0)
            {
                for (int i = 0; i < names.Count; i++)
                {
                    if (names[i] == "Reason") reasonIndex = i;
                    else if (names[i] == "Depth") depthIndex = i;
                }

                if (reasonIndex < 0 || depthIndex < 0)
                    return;
            }

            if (payload[reasonIndex] is not uint reason || payload[depthIndex] is not uint depth)
                return;

            int r = (int)Math.Min(reason, reason_count - 1);
            int d = (int)Math.Min(depth, depth_count - 1);

            Interlocked.Increment(ref counts[r * depth_count + d]);
            Interlocked.Increment(ref eventsSeen);
        }
    }
}
