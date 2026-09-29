// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Logging;

namespace osu.Framework.Platform
{
    /// <summary>
    /// Opt-in (<c>OSU_FRAME_STATS=1</c>) measurement of frame times between <see cref="Begin"/> and <see cref="End"/>.
    /// <para>
    /// The draw thread records, for every presented frame, the present-to-present gap, the time spent waiting for the frame-ready signal,
    /// waiting for the update thread (<c>TripleBuffer.GetForRead</c>), drawing and swapping, and whether a gen0 GC happened since the previous frame.
    /// The update thread records its own frame time per update frame, and once per second the allocation, GC pause and GC count deltas.
    /// Everything goes into preallocated arrays (allocated only when the variable is set), so nothing is allocated per frame and nothing costs
    /// anything when it is off.
    /// </para>
    /// <para>
    /// When a session ends, a background task writes one <c>[framestats]</c> summary line to the runtime log, plus a <c>.bin</c> (format below)
    /// and a matching <c>.txt</c> into the <c>framestats</c> folder of the game's data directory.
    /// </para>
    /// <para>
    /// .bin format, version 2, all little-endian: a 64 byte fixed header, then <c>u32 length + UTF-8 field list</c>, then the columns
    /// in field-list order, each column contiguous.
    /// <code>
    ///   0  char[8] magic "OSUFRMST"
    ///   8  u32 version (2)
    ///  12  u32 header size = offset of the first column (64 + 4 + field list length)
    ///  16  u32 frame count N        20  u32 update-frame count U      24  u32 per-second sample count S
    ///  28  i32 first-object draw-frame index, -1 if never marked (an index into the frame columns)
    ///  32  i32 first-object update-frame index, -1 if never marked
    ///  36  u32 flags: bit0 draw buffer filled up, bit1 update buffer filled up
    ///  40  f64 update origin offset in ms (see below)
    ///  48  16 reserved bytes, zero
    /// </code>
    /// Field list: <c>frame[gap_ms:f32,ready_wait_ms:f32,update_wait_ms:f32,draw_ms:f32,swap_ms:f32,gc0:u8];update[frame_ms:f32];second[elapsed_s:f64,alloc_bytes:u64,gc_pause_ms:f64,gen0:u32,gen1:u32,gen2:u32]</c>.
    /// The frame section has N entries per column, update has U, second has S. Draw frame k is presented at the sum of gap_ms[0..k] after the origin
    /// (the first present of the session, which is not itself recorded). Update record j is the time from the start of update frame j to the start of
    /// update frame j+1; update frame j starts at update_origin_offset_ms + sum(frame_ms[0..j-1]) after the draw origin. gc0 is 1 if
    /// <c>GC.CollectionCount(0)</c> changed since the previous frame. Per-second rows are deltas over the interval ending at elapsed_s
    /// (seconds after update frame 0); the last row is the partial final interval. gen0 counts every GC, gen1 every gen1 and gen2 GC, gen2 gen2 only.
    /// </para>
    /// </summary>
    public static class FrameStats
    {
        private const int capacity = 8 * 1024 * 1024;
        private const int update_capacity = 8 * 1024 * 1024;
        private const int second_capacity = 7200;
        private const int header_fixed = 64;

        private const string field_list =
            "frame[gap_ms:f32,ready_wait_ms:f32,update_wait_ms:f32,draw_ms:f32,swap_ms:f32,gc0:u8];update[frame_ms:f32];second[elapsed_s:f64,alloc_bytes:u64,gc_pause_ms:f64,gen0:u32,gen1:u32,gen2:u32]";

        private static readonly bool enabled = FrameworkEnvironment.FrameStats;

        // Draw thread columns (parallel arrays of `capacity`).
        private static readonly float[]? gapMs = enabled ? new float[capacity] : null;
        private static readonly float[]? readyMs = enabled ? new float[capacity] : null;
        private static readonly float[]? readMs = enabled ? new float[capacity] : null;
        private static readonly float[]? drawMs = enabled ? new float[capacity] : null;
        private static readonly float[]? swapMs = enabled ? new float[capacity] : null;
        private static readonly byte[]? gcFlag = enabled ? new byte[capacity] : null;

        // Update thread column.
        private static readonly float[]? updateMs = enabled ? new float[update_capacity] : null;

        // Per-second samples (update thread).
        private static readonly double[]? secElapsed = enabled ? new double[second_capacity] : null;
        private static readonly ulong[]? secAlloc = enabled ? new ulong[second_capacity] : null;
        private static readonly double[]? secPause = enabled ? new double[second_capacity] : null;
        private static readonly uint[]? secGen0 = enabled ? new uint[second_capacity] : null;
        private static readonly uint[]? secGen1 = enabled ? new uint[second_capacity] : null;
        private static readonly uint[]? secGen2 = enabled ? new uint[second_capacity] : null;

        private static readonly double ticksToMs = 1000.0 / Stopwatch.Frequency;

        // Written by the update thread (Begin/End), read by the draw thread.
        private static volatile bool recording;
        private static int session;

        // Set while the draw thread is inside its write, so End can wait for the last in-flight write to finish.
        private static int inRecord;

        // Set until a finished session has been written out, so a new session cannot reuse the buffers too early.
        private static int busy;

        // Draw thread only, except that the finishing task reads them after waiting for inRecord == 0.
        private static int seenSession;
        private static int count;
        private static bool full;
        private static long lastPresent;
        private static long drawOrigin;
        private static int lastGc0;
        private static long readyAcc, readAcc;

        // Marker: written by any thread (first call wins), reset by Begin.
        private static int firstObjectFrame = -1;
        private static int firstObjectUpdate = -1;

        // Update thread only.
        private static bool open;
        private static bool updateStarted;
        private static int updateCount;
        private static bool updateFull;
        private static long updateOrigin;
        private static long lastUpdate;
        private static long nextSample;
        private static int secCount;
        private static ulong lastAlloc;
        private static TimeSpan lastPause;
        private static int lastGen0, lastGen1, lastGen2;

        /// <summary>
        /// The storage the raw files are written to. Set by <see cref="GameHost"/>; falls back to the user's application data folder.
        /// </summary>
        internal static Storage? Storage { get; set; }

        /// <summary>
        /// Whether frame stats are enabled (<c>OSU_FRAME_STATS</c>).
        /// </summary>
        public static bool Enabled => enabled;

        /// <summary>
        /// Starts recording. Call from the update thread. Does nothing when disabled, or if the previous session is still being processed.
        /// </summary>
        public static void Begin()
        {
            if (!enabled || open) return;

            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
                return;

            open = true;

            // Update thread state.
            updateStarted = false;
            updateCount = 0;
            updateFull = false;
            secCount = 0;

            Volatile.Write(ref firstObjectFrame, -1);
            Volatile.Write(ref firstObjectUpdate, -1);

            Interlocked.Increment(ref session);
            recording = true;
        }

        /// <summary>
        /// Stops recording and writes the results in the background. Call from the update thread.
        /// </summary>
        public static void End()
        {
            if (!enabled || !open) return;

            if (updateStarted)
                sample(Stopwatch.GetTimestamp());

            open = false;
            recording = false;
            Interlocked.MemoryBarrier();

            Task.Run(finish);
        }

        /// <summary>
        /// Marks the current draw frame as the one where the gameplay clock reached the first hit object.
        /// Thread-safe, callable from the update thread. Only the first call of a session counts; does nothing when not recording.
        /// </summary>
        public static void MarkFirstObject()
        {
            if (!enabled || !recording) return;

            int frame = Volatile.Read(ref seenSession) == Volatile.Read(ref session) ? Volatile.Read(ref count) : 0;

            if (Interlocked.CompareExchange(ref firstObjectFrame, frame, -1) == -1)
                Volatile.Write(ref firstObjectUpdate, updateCount);
        }

        /// <summary>
        /// Whether a session is being recorded and the first hit object has not been marked yet. Cheap enough to poll every update.
        /// </summary>
        public static bool AwaitingFirstObject => enabled && recording && Volatile.Read(ref firstObjectFrame) < 0;

        /// <summary>
        /// Called on the update thread at the start of every update frame.
        /// </summary>
        internal static void UpdateStarted()
        {
            if (!open) return;

            long now = Stopwatch.GetTimestamp();

            if (!updateStarted)
            {
                updateStarted = true;
                updateOrigin = lastUpdate = now;
                nextSample = now + Stopwatch.Frequency;
                snapshotCounters();
                return;
            }

            if (updateCount < update_capacity)
                updateMs![updateCount++] = (float)((now - lastUpdate) * ticksToMs);
            else
                updateFull = true;

            lastUpdate = now;

            if (now >= nextSample)
            {
                sample(now);
                nextSample = now + Stopwatch.Frequency;
            }
        }

        private static void snapshotCounters()
        {
            lastAlloc = (ulong)GC.GetTotalAllocatedBytes(false);
            lastPause = GC.GetTotalPauseDuration();
            lastGen0 = GC.CollectionCount(0);
            lastGen1 = GC.CollectionCount(1);
            lastGen2 = GC.CollectionCount(2);
        }

        private static void sample(long now)
        {
            if (secCount >= second_capacity) return;

            ulong alloc = (ulong)GC.GetTotalAllocatedBytes(false);
            var pause = GC.GetTotalPauseDuration();
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);

            int i = secCount++;
            secElapsed![i] = (now - updateOrigin) / (double)Stopwatch.Frequency;
            secAlloc![i] = alloc - lastAlloc;
            secPause![i] = (pause - lastPause).TotalMilliseconds;
            secGen0![i] = (uint)(g0 - lastGen0);
            secGen1![i] = (uint)(g1 - lastGen1);
            secGen2![i] = (uint)(g2 - lastGen2);

            lastAlloc = alloc;
            lastPause = pause;
            lastGen0 = g0;
            lastGen1 = g1;
            lastGen2 = g2;
        }

        /// <summary>
        /// Called on the draw thread after the frame-ready wait and the wait for the update thread. Accumulates until the next present.
        /// </summary>
        internal static void AddWaits(long readyTicks, long readTicks)
        {
            readyAcc += readyTicks;
            readAcc += readTicks;
        }

        /// <summary>
        /// Called on the draw thread immediately after the buffers are swapped.
        /// </summary>
        /// <param name="now">The timestamp taken right after the swap.</param>
        /// <param name="drawTicks">Time from the end of the wait for the update thread to the start of the swap.</param>
        /// <param name="swapTicks">Time spent in the swap.</param>
        internal static void Presented(long now, long drawTicks, long swapTicks)
        {
            if (!recording)
            {
                lastPresent = now;
                readyAcc = readAcc = 0;
                return;
            }

            Interlocked.Exchange(ref inRecord, 1);

            try
            {
                if (!recording)
                {
                    lastPresent = now;
                    readyAcc = readAcc = 0;
                    return;
                }

                int current = Volatile.Read(ref session);
                int gc0 = GC.CollectionCount(0);

                if (current != seenSession)
                {
                    // First frame of a new session: start clean, and skip the gap that led up to it.
                    seenSession = current;
                    Volatile.Write(ref count, 0);
                    full = false;
                    drawOrigin = now;
                }
                else
                {
                    int i = count;

                    if (i < capacity)
                    {
                        gapMs![i] = (float)((now - lastPresent) * ticksToMs);
                        readyMs![i] = (float)(readyAcc * ticksToMs);
                        readMs![i] = (float)(readAcc * ticksToMs);
                        drawMs![i] = (float)(drawTicks * ticksToMs);
                        swapMs![i] = (float)(swapTicks * ticksToMs);
                        gcFlag![i] = gc0 != lastGc0 ? (byte)1 : (byte)0;
                        Volatile.Write(ref count, i + 1);
                    }
                    else
                        full = true;
                }

                lastGc0 = gc0;
                lastPresent = now;
                readyAcc = readAcc = 0;
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

                if (n == 0)
                {
                    Logger.Log("[framestats] no frames recorded");
                    return;
                }

                int firstFrame = Volatile.Read(ref firstObjectFrame);
                if (firstFrame >= n) firstFrame = -1;

                string line = summarise(n, firstFrame, full);
                Logger.Log(line);
                write(n, firstFrame, line);
            }
            catch (Exception e)
            {
                Logger.Log($"[framestats] failed: {e.Message}");
            }
            finally
            {
                Volatile.Write(ref busy, 0);
            }
        }

        private readonly record struct Summary(double Seconds, int Frames, double Avg, double Low1, double Low01, float Max, int Over1, int Over2, int Over5, int Over20);

        private static Summary compute(int start, int n)
        {
            var span = new ReadOnlySpan<float>(gapMs!, start, n - start);
            double total = 0;
            int over1 = 0, over2 = 0, over5 = 0, over20 = 0;

            foreach (float f in span)
            {
                total += f;
                if (f > 1) over1++;
                if (f > 2) over2++;
                if (f > 5) over5++;
                if (f > 20) over20++;
            }

            // Worst first, so the worst 1% / 0.1% are a prefix.
            float[] sorted = span.ToArray();
            Array.Sort(sorted);
            Array.Reverse(sorted);

            return new Summary(total / 1000, sorted.Length, sorted.Length / (total / 1000), low(sorted, 0.01), low(sorted, 0.001), sorted[0], over1, over2, over5, over20);
        }

        private static string summarise(int n, int firstFrame, bool wasFull)
        {
            var c = CultureInfo.InvariantCulture;
            var all = compute(0, n);

            string line = string.Create(c,
                $"[framestats] {all.Seconds:F1} s, {all.Frames} frames, avg {all.Avg:F1} fps, 1% low {all.Low1:F1}, 0.1% low {all.Low01:F1}, max {all.Max:F2} ms, frames >1ms {all.Over1} >2ms {all.Over2} >5ms {all.Over5} >20ms {all.Over20}");

            if (firstFrame >= 0 && firstFrame < n)
            {
                var s = compute(firstFrame, n);
                line += string.Create(c,
                    $" | from first object: {s.Seconds:F1} s, {s.Frames} frames, avg {s.Avg:F1} fps, 1% low {s.Low1:F1}, 0.1% low {s.Low01:F1}, max {s.Max:F2} ms, >1ms {s.Over1} >2ms {s.Over2} >5ms {s.Over5} >20ms {s.Over20}");
            }
            else
                line += " | first object not marked";

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

        private static void write(int n, int firstFrame, string line)
        {
            if (!BitConverter.IsLittleEndian)
            {
                Logger.Log("[framestats] raw files are only written on little-endian machines");
                return;
            }

            string name = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            Storage? storage = Storage;

            string? dir = null;

            if (storage == null)
            {
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "osu-8k", "framestats");
                Directory.CreateDirectory(dir);
            }

            using (var s = storage != null ? storage.CreateFileSafely($"{name}.bin") : File.Create(Path.Combine(dir!, $"{name}.bin")))
                writeBin(s, n, firstFrame);

            using (var s = storage != null ? storage.CreateFileSafely($"{name}.txt") : File.Create(Path.Combine(dir!, $"{name}.txt")))
            using (var w = new StreamWriter(s))
                writeTxt(w, line);
        }

        private static void writeBin(Stream s, int n, int firstFrame)
        {
            int u = updateCount;
            int sec = secCount;
            byte[] list = Encoding.UTF8.GetBytes(field_list);
            int headerSize = header_fixed + 4 + list.Length;

            // Update frame 0 starts at (updateOrigin - drawOrigin); both are Stopwatch timestamps.
            double offsetMs = (updateOrigin - drawOrigin) * ticksToMs;
            uint flags = (full ? 1u : 0u) | (updateFull ? 2u : 0u);
            int firstUpdate = Volatile.Read(ref firstObjectUpdate);

            byte[] header = new byte[headerSize];
            Encoding.ASCII.GetBytes("OSUFRMST").CopyTo(header, 0);
            BitConverter.TryWriteBytes(header.AsSpan(8), 2u);
            BitConverter.TryWriteBytes(header.AsSpan(12), (uint)headerSize);
            BitConverter.TryWriteBytes(header.AsSpan(16), (uint)n);
            BitConverter.TryWriteBytes(header.AsSpan(20), (uint)u);
            BitConverter.TryWriteBytes(header.AsSpan(24), (uint)sec);
            BitConverter.TryWriteBytes(header.AsSpan(28), firstFrame);
            BitConverter.TryWriteBytes(header.AsSpan(32), firstFrame >= 0 && firstUpdate >= 0 && firstUpdate <= u ? firstUpdate : -1);
            BitConverter.TryWriteBytes(header.AsSpan(36), flags);
            BitConverter.TryWriteBytes(header.AsSpan(40), offsetMs);
            BitConverter.TryWriteBytes(header.AsSpan(header_fixed), (uint)list.Length);
            list.CopyTo(header, header_fixed + 4);
            s.Write(header, 0, header.Length);

            s.Write(MemoryMarshal.AsBytes(gapMs.AsSpan(0, n)));
            s.Write(MemoryMarshal.AsBytes(readyMs.AsSpan(0, n)));
            s.Write(MemoryMarshal.AsBytes(readMs.AsSpan(0, n)));
            s.Write(MemoryMarshal.AsBytes(drawMs.AsSpan(0, n)));
            s.Write(MemoryMarshal.AsBytes(swapMs.AsSpan(0, n)));
            s.Write(gcFlag.AsSpan(0, n));
            s.Write(MemoryMarshal.AsBytes(updateMs.AsSpan(0, u)));
            s.Write(MemoryMarshal.AsBytes(secElapsed.AsSpan(0, sec)));
            s.Write(MemoryMarshal.AsBytes(secAlloc.AsSpan(0, sec)));
            s.Write(MemoryMarshal.AsBytes(secPause.AsSpan(0, sec)));
            s.Write(MemoryMarshal.AsBytes(secGen0.AsSpan(0, sec)));
            s.Write(MemoryMarshal.AsBytes(secGen1.AsSpan(0, sec)));
            s.Write(MemoryMarshal.AsBytes(secGen2.AsSpan(0, sec)));
        }

        private static void writeTxt(StreamWriter w, string line)
        {
            var c = CultureInfo.InvariantCulture;
            w.WriteLine(line);
            w.WriteLine("per second (deltas): elapsed_s alloc_MB gc_pause_ms gen0 gen1 gen2");

            for (int i = 0; i < secCount; i++)
                w.WriteLine(string.Create(c, $"{secElapsed![i]:F2} {secAlloc![i] / 1048576.0:F2} {secPause![i]:F2} {secGen0![i]} {secGen1![i]} {secGen2![i]}"));
        }
    }
}
