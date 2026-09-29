// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Logging;

namespace osu.Framework.Platform
{
    /// <summary>
    /// Opt-in (<c>OSU_FRAME_STATS=1</c>) measurement of present-to-present frame times between <see cref="Begin"/> and <see cref="End"/>.
    /// The draw thread records one float (ms) per presented frame into a preallocated buffer, so nothing is allocated per frame.
    /// When a session ends, a background task writes one <c>[framestats]</c> summary line to the runtime log and a raw
    /// <c>.bin</c> (float32 little-endian ms) plus matching <c>.txt</c> into the <c>framestats</c> folder of the game's data directory.
    /// Costs nothing when the variable is not set.
    /// </summary>
    public static class FrameStats
    {
        private const int capacity = 8 * 1024 * 1024;

        private static readonly float[]? buffer = FrameworkEnvironment.FrameStats ? new float[capacity] : null;

        // Written by the update thread (Begin/End), read by the draw thread.
        private static volatile bool recording;
        private static int session;

        // Set while the draw thread is inside its write, so End can wait for the last in-flight write to finish.
        private static int inRecord;

        // Set until a finished session's buffer has been copied out, so a new session cannot reuse the buffer too early.
        private static int busy;

        // Draw thread only, except that the finishing task reads them after waiting for inRecord == 0.
        private static int seenSession;
        private static int count;
        private static bool full;
        private static long lastPresent;

        // Update thread only.
        private static bool open;

        /// <summary>
        /// The storage the raw files are written to. Set by <see cref="GameHost"/>; falls back to the user's application data folder.
        /// </summary>
        internal static Storage? Storage { get; set; }

        internal static bool Enabled => buffer != null;

        /// <summary>
        /// Starts recording. Call from the update thread. Does nothing when disabled, or if the previous session is still being processed.
        /// </summary>
        public static void Begin()
        {
            if (buffer == null || open) return;

            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
                return;

            open = true;
            Interlocked.Increment(ref session);
            recording = true;
        }

        /// <summary>
        /// Stops recording and writes the results in the background. Call from the update thread.
        /// </summary>
        public static void End()
        {
            if (buffer == null || !open) return;

            open = false;
            recording = false;
            Interlocked.MemoryBarrier();

            Task.Run(finish);
        }

        /// <summary>
        /// Called on the draw thread immediately after the buffers are swapped.
        /// </summary>
        internal static void Presented()
        {
            long now = Stopwatch.GetTimestamp();

            if (!recording)
            {
                lastPresent = now;
                return;
            }

            Interlocked.Exchange(ref inRecord, 1);

            try
            {
                if (!recording)
                {
                    lastPresent = now;
                    return;
                }

                int current = Volatile.Read(ref session);

                if (current != seenSession)
                {
                    // First frame of a new session: start clean, and skip the gap that led up to it.
                    seenSession = current;
                    count = 0;
                    full = false;
                }
                else if (count < capacity)
                    buffer![count++] = (float)((now - lastPresent) * 1000.0 / Stopwatch.Frequency);
                else
                    full = true;

                lastPresent = now;
            }
            finally
            {
                Volatile.Write(ref inRecord, 0);
            }
        }

        private static void finish()
        {
            try
            {
                var spin = new SpinWait();

                // Recording is already false, so at most one write is still in flight.
                while (Volatile.Read(ref inRecord) != 0)
                    spin.SpinOnce();

                int n = seenSession == Volatile.Read(ref session) ? count : 0;
                bool wasFull = full;
                float[] frames = new float[n];
                Array.Copy(buffer!, frames, n);

                Volatile.Write(ref busy, 0);

                if (n == 0)
                {
                    Logger.Log("[framestats] no frames recorded");
                    return;
                }

                string line = summarise(frames, wasFull);
                Logger.Log(line);
                write(frames, line);
            }
            catch (Exception e)
            {
                Volatile.Write(ref busy, 0);
                Logger.Log($"[framestats] failed: {e.Message}");
            }
        }

        private static string summarise(float[] frames, bool wasFull)
        {
            int n = frames.Length;
            double total = 0;
            int over1 = 0, over2 = 0, over5 = 0, over20 = 0;

            foreach (float f in frames)
            {
                total += f;
                if (f > 1) over1++;
                if (f > 2) over2++;
                if (f > 5) over5++;
                if (f > 20) over20++;
            }

            // Worst first, so the worst 1% / 0.1% are a prefix.
            float[] sorted = (float[])frames.Clone();
            Array.Sort(sorted);
            Array.Reverse(sorted);

            double avg = n / (total / 1000);
            double low1 = low(sorted, 0.01);
            double low01 = low(sorted, 0.001);

            var c = CultureInfo.InvariantCulture;
            string line = string.Create(c,
                $"[framestats] {total / 1000:F1} s, {n} frames, avg {avg:F1} fps, 1% low {low1:F1}, 0.1% low {low01:F1}, max {sorted[0]:F2} ms, frames >1ms {over1} >2ms {over2} >5ms {over5} >20ms {over20}");

            if (wasFull)
                line += " (buffer full, recording stopped early)";

            return line;
        }

        private static double low(float[] worstFirst, double fraction)
        {
            int take = Math.Max(1, (int)(worstFirst.Length * fraction));
            double sum = 0;
            for (int i = 0; i < take; i++)
                sum += worstFirst[i];
            return 1000 / (sum / take);
        }

        private static void write(float[] frames, string line)
        {
            string name = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

            byte[] bytes = new byte[frames.Length * sizeof(float)];
            Buffer.BlockCopy(frames, 0, bytes, 0, bytes.Length);

            if (!BitConverter.IsLittleEndian)
            {
                for (int i = 0; i < bytes.Length; i += 4)
                    Array.Reverse(bytes, i, 4);
            }

            Storage? storage = Storage;

            if (storage != null)
            {
                using (var s = storage.CreateFileSafely($"{name}.bin"))
                    s.Write(bytes, 0, bytes.Length);

                using (var s = storage.CreateFileSafely($"{name}.txt"))
                using (var w = new StreamWriter(s))
                    w.WriteLine(line);
            }
            else
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "osu-8k", "framestats");
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, $"{name}.bin"), bytes);
                File.WriteAllText(Path.Combine(dir, $"{name}.txt"), line + Environment.NewLine);
            }
        }
    }
}
