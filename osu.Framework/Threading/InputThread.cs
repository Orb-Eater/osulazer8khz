// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Statistics;
using System.Collections.Generic;
using osu.Framework.Development;

namespace osu.Framework.Threading
{
    public class InputThread : GameThread
    {
        public InputThread()
            : base(name: "Input")
        {
            // This thread now targets sub-millisecond periods (OSU_INPUT_HZ defaults to 8000 = 0.125ms),
            // which no kernel timer can resolve. Spin out the tail of every throttled wait instead.
            Clock.SpinWaitThreshold = 0.5;
        }

        internal override IEnumerable<StatisticsCounterType> StatisticsCounters => new[]
        {
            StatisticsCounterType.MouseEvents,
            StatisticsCounterType.KeyEvents,
            StatisticsCounterType.JoystickEvents,
            StatisticsCounterType.MidiEvents,
            StatisticsCounterType.TabletEvents,
            StatisticsCounterType.TouchEvents,
        };

        protected override void PrepareForWork()
        {
            // Intentionally inhibiting the base implementation which spawns a native thread.
            // Therefore, we need to run Initialize inline.
            Initialize(true);

            if (!rateLoggingAttached)
            {
                rateLoggingAttached = true;
                OnNewFrame += logInputRate;
            }
        }

        private bool rateLoggingAttached;
        private readonly System.Diagnostics.Stopwatch rateStopwatch = System.Diagnostics.Stopwatch.StartNew();
        private long framesSinceLastReport;

        private void logInputRate()
        {
            framesSinceLastReport++;

            // Reading the stopwatch is a QPC call. On an uncapped input thread this runs tens of thousands
            // of times a second on the hottest loop in the process, so only consult the clock periodically.
            if ((framesSinceLastReport & 255) != 0)
                return;

            double elapsedMs = rateStopwatch.Elapsed.TotalMilliseconds;

            if (elapsedMs < 5000)
                return;

            string target = Clock.MaximumUpdateHz > 0 ? $"{Clock.MaximumUpdateHz:N0} Hz" : "uncapped";

            osu.Framework.Logging.Logger.Log(
                $"[input] {framesSinceLastReport * 1000.0 / elapsedMs:N0} Hz effective, mean loop period {elapsedMs / framesSinceLastReport:N4} ms (target {target})");

            framesSinceLastReport = 0;
            rateStopwatch.Restart();
        }

        public override bool IsCurrent => ThreadSafety.IsInputThread;

        internal sealed override void MakeCurrent()
        {
            base.MakeCurrent();

            ThreadSafety.IsInputThread = true;
        }
    }
}
