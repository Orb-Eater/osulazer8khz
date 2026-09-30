// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using osu.Framework.Allocation;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Game.Performance;

namespace osu.Desktop.Performance
{
    public class HighPerformanceSessionManager : IHighPerformanceSessionManager
    {
        public bool IsSessionActive => activeSessions > 0;

        private int activeSessions;

        public IDisposable BeginSession()
        {
            enterSession();
            return new InvokeOnDisposal<HighPerformanceSessionManager>(this, static m => m.exitSession());
        }

        private void enterSession()
        {
            if (Interlocked.Increment(ref activeSessions) > 1)
            {
                Logger.Log($"High performance session requested ({activeSessions} running in total)");
                return;
            }

            Logger.Log("Starting high performance session");

            FrameStats.Begin();

            // Upstream behaviour (LowLatency, then GC.Collect(0)) unless OSU_GC_MODE / OSU_GC_COLLECT_AT_START / OSU_NOGC_MB say otherwise.
            GCSession.Begin();
        }

        private void exitSession()
        {
            if (Interlocked.Decrement(ref activeSessions) > 0)
            {
                Logger.Log($"High performance session finished ({activeSessions} others remain)");
                return;
            }

            Logger.Log("Ending high performance session");

            FrameStats.End();

            GCSession.End();

            // No GC.Collect() as we were already collecting at a higher frequency in the old mode.
        }
    }
}
