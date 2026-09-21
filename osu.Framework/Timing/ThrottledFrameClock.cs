// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Threading;
using osu.Framework.Platform;
using osu.Framework.Platform.Linux.Native;
using osu.Framework.Platform.Windows.Native;

namespace osu.Framework.Timing
{
    /// <summary>
    /// A FrameClock which will limit the number of frames processed by adding Thread.Sleep calls on each ProcessFrame.
    /// </summary>
    public class ThrottledFrameClock : FramedClock, IDisposable
    {
        /// <summary>
        /// The target number of updates per second. Only used when <see cref="Throttling"/> is <c>true</c>.
        /// </summary>
        /// <remarks>
        /// A value of 0 is treated the same as "unlimited" or <see cref="double.MaxValue"/>.
        /// </remarks>
        public double MaximumUpdateHz = 1000.0;

        /// <summary>
        /// Whether throttling should be enabled. Defaults to <c>true</c>.
        /// </summary>
        public bool Throttling = true;

        /// <summary>
        /// The trailing portion (in milliseconds) of each throttled wait which is burnt on-CPU instead of being
        /// handed to a kernel timer. Windows' high-resolution waitable timer has a practical granularity of a few
        /// hundred microseconds, so sub-millisecond target periods can only be hit accurately by spinning.
        /// Defaults to <c>0</c>, which preserves the original sleep-only behaviour exactly.
        /// </summary>
        public double SpinWaitThreshold;

        /// <summary>
        /// The time spent in a Thread.Sleep state during the last frame.
        /// </summary>
        public double TimeSlept { get; private set; }

        private readonly INativeSleep? nativeSleep;

        internal ThrottledFrameClock()
        {
            if (RuntimeInfo.OS == RuntimeInfo.Platform.Windows)
                nativeSleep = new WindowsNativeSleep();
            else if (RuntimeInfo.IsUnix && UnixNativeSleep.Available)
                nativeSleep = new UnixNativeSleep();
        }

        public override void ProcessFrame()
        {
            Debug.Assert(MaximumUpdateHz >= 0);

            base.ProcessFrame();

            if (Throttling)
            {
                // Anything at or above int.MaxValue is "unlimited" (GameHost hands the clock int.MaxValue for
                // FrameSync.Unlimited), and must skip the throttle path entirely rather than computing a zero sleep
                // and a per-frame accumulatedSleepError update that can never do anything.
                if (MaximumUpdateHz > 0 && MaximumUpdateHz < int.MaxValue)
                {
                    throttle();
                }
                else
                {
                    // Unlimited means unlimited: never sleep, never yield, never enter the throttle path.
                    // (the old sleepAndUpdateCurrent(0) call returned immediately at its "milliseconds <= 0" guard
                    // without yielding, so dropping it is behaviourally identical minus one call per frame.)
                    TimeSlept = 0;
                }
            }
            else
            {
                TimeSlept = 0;
            }

            Debug.Assert(TimeSlept <= ElapsedFrameTime);
        }

        private double accumulatedSleepError;

        private void throttle()
        {
            double excessFrameTime = 1000d / MaximumUpdateHz - ElapsedFrameTime;

            TimeSlept = sleepAndUpdateCurrent(Math.Max(0, excessFrameTime + accumulatedSleepError));

            accumulatedSleepError += excessFrameTime - TimeSlept;

            // Never allow the sleep error to become too negative and induce too many catch-up frames
            accumulatedSleepError = Math.Max(-1000 / 30.0, accumulatedSleepError);
        }

        private double sleepAndUpdateCurrent(double milliseconds)
        {
            // By returning here, in cases where the game is not keeping up, we don't yield.
            // Not 100% sure if we want to do this, but let's give it a try.
            if (milliseconds <= 0)
                return 0;

            double before = CurrentTime;

            double toSleep = milliseconds - SpinWaitThreshold;

            if (toSleep > 0)
            {
                TimeSpan timeSpan = TimeSpan.FromMilliseconds(toSleep);

                if (nativeSleep?.Sleep(timeSpan) != true)
                    Thread.Sleep(timeSpan);
            }

            if (SpinWaitThreshold > 0)
            {
                // Burn the remainder on-CPU. The backing source is a free-running StopwatchClock, so this always terminates.
                double target = before + milliseconds;

                while (SourceTime < target)
                    Thread.SpinWait(8);
            }

            return (CurrentTime = SourceTime) - before;
        }

        public void Dispose()
        {
            nativeSleep?.Dispose();
        }
    }
}
