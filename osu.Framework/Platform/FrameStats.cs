// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Logging;
using static SDL.SDL3;

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
    /// Input delay: for every key press/release, mouse button press/release, mouse move and pen move/touch/button the game records when the OS stamped the event (SDL's own
    /// timestamp, see the README for what clock that is), when the input thread handled it, when the update thread consumed it, and when the first
    /// frame containing that update frame was presented. Only kinds and times are kept, never which key or button or where the mouse was.
    /// </para>
    /// <para>
    /// .bin format, version 4 (version 3 had no draw-frame update index and no publish time, 2 had no input section), all little-endian: a 64 byte fixed header, then <c>u32 length + UTF-8 field list</c>, then the columns
    /// in field-list order, each column contiguous.
    /// <code>
    ///   0  char[8] magic "OSUFRMST"
    ///   8  u32 version (4)
    ///  12  u32 header size = offset of the first column (64 + 4 + field list length)
    ///  16  u32 frame count N        20  u32 update-frame count U      24  u32 per-second sample count S
    ///  28  i32 first-object draw-frame index, -1 if never marked (an index into the frame columns)
    ///  32  i32 first-object update-frame index, -1 if never marked
    ///  36  u32 flags: bit0 draw buffer filled up, bit1 update buffer filled up
    ///  40  f64 update origin offset in ms (see below)
    ///  48  u32 input-event count I
    ///  52  u32 input flags: bit0 an input buffer filled up, bit1 events were lost before the update thread consumed them
    ///  56  f64 first-object time in ms after the draw origin, -1 if never marked
    /// </code>
    /// Field list: <c>frame[gap_ms:f32,ready_wait_ms:f32,update_wait_ms:f32,draw_ms:f32,swap_ms:f32,gc0:u8,update_index:i32];update[frame_ms:f32,publish_ms:f32];second[elapsed_s:f64,alloc_bytes:u64,gc_pause_ms:f64,gen0:u32,gen1:u32,gen2:u32];input[pump_ms:f64,os_to_pump_ms:f32,pump_to_update_ms:f32,update_to_present_ms:f32,kind:u8]</c>.
    /// The input section has I entries (see the README for the meaning of each time; NaN = not available). The frame section has N entries per column, update has U, second has S. Draw frame k is presented at the sum of gap_ms[0..k] after the origin
    /// (the first present of the session, which is not itself recorded). Update record j is the time from the start of update frame j to the start of
    /// update frame j+1; update frame j starts at update_origin_offset_ms + sum(frame_ms[0..j-1]) after the draw origin. publish_ms is the time from the start of
    /// update frame j until its draw root was stored in the triple buffer (NaN if not recorded). update_index is the update frame that draw frame drew (-1 = unknown).
    /// Draw frame k started drawing at present(k) - swap_ms[k] - draw_ms[k]. gc0 is 1 if
    /// <c>GC.CollectionCount(0)</c> changed since the previous frame. Per-second rows are deltas over the interval ending at elapsed_s
    /// (seconds after update frame 0); the last row is the partial final interval. gen0 counts every GC, gen1 every gen1 and gen2 GC, gen2 gen2 only.
    /// </para>
    /// </summary>
    public static class FrameStats
    {
        private const int capacity = 8 * 1024 * 1024;
        private const int update_capacity = 8 * 1024 * 1024;
        private const int second_capacity = 7200;
        private const int key_input_capacity = 64 * 1024;
        private const int mouse_input_capacity = 4 * 1024 * 1024;
        private const int pen_input_capacity = 4 * 1024 * 1024;
        private const int input_ring = 64 * 1024;
        private const int header_fixed = 64;

        private const string field_list =
            "frame[gap_ms:f32,ready_wait_ms:f32,update_wait_ms:f32,draw_ms:f32,swap_ms:f32,gc0:u8,update_index:i32];update[frame_ms:f32,publish_ms:f32];second[elapsed_s:f64,alloc_bytes:u64,gc_pause_ms:f64,gen0:u32,gen1:u32,gen2:u32];input[pump_ms:f64,os_to_pump_ms:f32,pump_to_update_ms:f32,update_to_present_ms:f32,kind:u8]";

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

        // Time from the start of each update frame to the moment its draw root was stored (parallel to updateMs, but the frame still in progress at End is not serialised).
        private static readonly float[]? publishMs = enabled ? new float[update_capacity + 1] : null;

        // Per-second samples (update thread).
        private static readonly double[]? secElapsed = enabled ? new double[second_capacity] : null;
        private static readonly ulong[]? secAlloc = enabled ? new ulong[second_capacity] : null;
        private static readonly double[]? secPause = enabled ? new double[second_capacity] : null;
        private static readonly uint[]? secGen0 = enabled ? new uint[second_capacity] : null;
        private static readonly uint[]? secGen1 = enabled ? new uint[second_capacity] : null;
        private static readonly uint[]? secGen2 = enabled ? new uint[second_capacity] : null;

        // Gen0 size just before / after the most recent GC at the time of each sample (GC.GetGCMemoryInfo). Only in the .txt.
        private static readonly ulong[]? secGen0Before = enabled ? new ulong[second_capacity] : null;
        private static readonly ulong[]? secGen0After = enabled ? new ulong[second_capacity] : null;

        private static readonly double ticksToMs = 1000.0 / Stopwatch.Frequency;

        // Update frame index of the frame stored in each of the three draw-root buffers (written by the update thread, read by the draw thread after it gets that buffer).
        private static readonly int[] bufferUpdate = { -1, -1, -1 };

        // Update frame index drawn by each recorded draw frame (parallel to the frame columns).
        private static readonly int[]? drawnUpdate = enabled ? new int[capacity] : null;

        // Offset that turns SDL_GetTicksNS() (nanoseconds) into Stopwatch ticks. SDL's clock is QueryPerformanceCounter based, so it is constant.
        private static readonly long sdlToStopwatchTicks = enabled ? measureSdlOffset() : 0;

        // Lane 0 = keyboard handler, lane 1 = mouse handler, lane 2 = pen handler. Each lane is fed by the input thread and drained by the update thread.
        private static readonly InputLane[]? lanes = enabled ? new[] { new InputLane(key_input_capacity), new InputLane(mouse_input_capacity), new InputLane(pen_input_capacity) } : null;

        // Input thread only: the SDL event being handled right now.
        private static ulong eventOsNs;
        private static long eventPump;
        private static bool eventRepeat;

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
        private static long firstObjectTicks;

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

        // GC reasons: the session's GC count at Begin/End (update thread), and the formatted result (finishing task).
        private static int beginGcCount, endGcCount;
        private static string gcReasonsLine = string.Empty;
        private static string gen0SizeLine = string.Empty;
        private static string inputLine = string.Empty;

        // Input rows merged from both lanes by the finishing task.
        private static double[]? inPumpMs;
        private static float[]? inOsToPump, inPumpToUpdate, inUpdateToPresent;
        private static float[]? inUpdateToPublish, inPublishToDraw, inDrawToPresent;
        private static byte[]? inKind;
        private static int inCount;

        // Per draw frame (finishing task): present time minus the start of the update frame it drew, and minus that frame's publish time. NaN = not available.
        private static float[]? ageAtPresent, publishToPresentMs;

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
            Volatile.Write(ref firstObjectTicks, 0);

            foreach (var lane in lanes!)
                lane.Reset();

            beginGcCount = GC.CollectionCount(0);
            GCReasonListener.Begin();

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
            endGcCount = GC.CollectionCount(0);
            GCReasonListener.MarkEnd();
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
            {
                Volatile.Write(ref firstObjectUpdate, updateCount);
                Volatile.Write(ref firstObjectTicks, Stopwatch.GetTimestamp());
            }
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
                publishMs![0] = float.NaN;
                nextSample = now + Stopwatch.Frequency;
                snapshotCounters();
                return;
            }

            if (updateCount < update_capacity)
                updateMs![updateCount++] = (float)((now - lastUpdate) * ticksToMs);
            else
                updateFull = true;

            lastUpdate = now;

            if (updateCount < update_capacity)
                publishMs![updateCount] = float.NaN;

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

            var info = GC.GetGCMemoryInfo(GCKind.Any);

            if (info.GenerationInfo.Length > 0)
            {
                secGen0Before![i] = (ulong)info.GenerationInfo[0].SizeBeforeBytes;
                secGen0After![i] = (ulong)info.GenerationInfo[0].SizeAfterBytes;
            }

            lastAlloc = alloc;
            lastPause = pause;
            lastGen0 = g0;
            lastGen1 = g1;
            lastGen2 = g2;
        }

        /// <summary>
        /// Called on the update thread when it stores a new draw root, with the index of the buffer it wrote.
        /// </summary>
        internal static void BufferWritten(int bufferIndex)
        {
            if (open && updateStarted && !updateFull)
            {
                int frame = updateCount;
                bufferUpdate[bufferIndex] = frame;

                if (frame <= update_capacity)
                    publishMs![frame] = (float)((Stopwatch.GetTimestamp() - lastUpdate) * ticksToMs);
            }
            else
                bufferUpdate[bufferIndex] = -1;
        }

        /// <summary>
        /// Called on the input thread at the start of handling a key, mouse button, mouse motion or pen event, with the event's SDL timestamp (nanoseconds).
        /// </summary>
        internal static void SetInputEvent(ulong sdlTimestampNs, bool keyRepeat = false)
        {
            eventOsNs = sdlTimestampNs;
            eventRepeat = keyRepeat;
            eventPump = Stopwatch.GetTimestamp();
        }

        /// <summary>
        /// Called on the input thread when the event handling has finished.
        /// </summary>
        internal static void ClearInputEvent()
        {
            eventPump = 0;
            eventRepeat = false;
        }

        /// <summary>
        /// Called on the input thread by an input handler just before it enqueues an input (so the record exists before the update thread can dequeue it).
        /// </summary>
        /// <param name="lane">0 for the keyboard handler, 1 for the mouse handler, 2 for the pen handler.</param>
        /// <param name="kind">0 key down, 1 key up, 2 mouse button down, 3 mouse button up, 4 mouse move, 5 pen move, 6 pen touch down, 7 pen touch up, 8 pen button down, 9 pen button up, 255 not measured (still counted).</param>
        internal static void InputEnqueued(int lane, byte kind)
        {
            long pump = eventPump;
            float osToPump = float.NaN;

            // OS key repeats are not new presses.
            if (eventRepeat && pump != 0)
                kind = 255;

            if (pump != 0)
            {
                if (eventOsNs != 0)
                    osToPump = (float)((pump - ((long)(eventOsNs * (Stopwatch.Frequency / 1e9)) + sdlToStopwatchTicks)) * ticksToMs);
            }
            else
                pump = Stopwatch.GetTimestamp();

            lanes![lane].Enqueue(pump, osToPump, kind);
        }

        /// <summary>
        /// Called on the update thread after an input handler handed <paramref name="count"/> inputs to the input manager.
        /// </summary>
        internal static void InputsConsumed(int lane, int count)
        {
            if (count == 0) return;

            lanes![lane].Consume(count, open && updateStarted && !updateFull, updateCount);
        }

        private static long measureSdlOffset()
        {
            // The pair with the smallest gap between the two reads is the most accurate one.
            long bestGap = long.MaxValue, offset = 0;

            for (int i = 0; i < 2000; i++)
            {
                long a = Stopwatch.GetTimestamp();
                ulong ns = SDL_GetTicksNS();
                long b = Stopwatch.GetTimestamp();

                if (b - a < bestGap)
                {
                    bestGap = b - a;
                    offset = a + (b - a) / 2 - (long)(ns * (Stopwatch.Frequency / 1e9));
                }
            }

            return offset;
        }

        private sealed class InputLane
        {
            // Ring of events the input thread has handled (index = sequence & mask). Sequence numbers match the handler's queue order.
            private readonly long[] ringPump = new long[input_ring];
            private readonly float[] ringOs = new float[input_ring];
            private readonly byte[] ringKind = new byte[input_ring];
            private long produced;
            private long consumed;

            // Recorded session rows (update thread), in consumption order.
            public readonly long[] Pump, Update;
            public readonly float[] OsToPump;
            public readonly int[] UpdateFrame;
            public readonly byte[] Kind;
            public int Count;
            public bool Full, Lost;

            public InputLane(int capacity)
            {
                Pump = new long[capacity];
                Update = new long[capacity];
                OsToPump = new float[capacity];
                UpdateFrame = new int[capacity];
                Kind = new byte[capacity];
            }

            public void Reset()
            {
                Count = 0;
                Full = Lost = false;
            }

            public void Enqueue(long pump, float osToPump, byte kind)
            {
                long seq = produced;
                int i = (int)(seq & (input_ring - 1));
                ringPump[i] = pump;
                ringOs[i] = osToPump;
                ringKind[i] = kind;
                Volatile.Write(ref produced, seq + 1);
            }

            public void Consume(int count, bool record, int updateFrame)
            {
                long first = consumed;
                long available = Volatile.Read(ref produced);
                consumed = first + count;

                if (!record) return;

                long now = Stopwatch.GetTimestamp();
                long end = Math.Min(first + count, available);

                for (long s = first; s < end; s++)
                {
                    if (available - s > input_ring)
                    {
                        // The input thread has already reused this slot.
                        Lost = true;
                        continue;
                    }

                    int i = (int)(s & (input_ring - 1));
                    byte kind = ringKind[i];
                    if (kind == 255) continue;

                    if (Count >= Pump.Length)
                    {
                        Full = true;
                        return;
                    }

                    int r = Count++;
                    Pump[r] = ringPump[i];
                    OsToPump[r] = ringOs[i];
                    Kind[r] = kind;
                    Update[r] = now;
                    UpdateFrame[r] = updateFrame;
                }
            }
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
        /// <param name="bufferIndex">The index of the draw-root buffer that was drawn.</param>
        internal static void Presented(long now, long drawTicks, long swapTicks, int bufferIndex)
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
                        drawnUpdate![i] = bufferUpdate[bufferIndex];
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

                // The runtime hands GC events to the listener with a short delay; give it time to deliver the last ones before counting.
                Thread.Sleep(300);
                gcReasonsLine = "[framestats] " + GCReasonListener.Finish(endGcCount - beginGcCount);
                gen0SizeLine = gen0Summary();

                int n = seenSession == Volatile.Read(ref session) ? count : 0;

                if (n == 0)
                {
                    Logger.Log("[framestats] no frames recorded");
                    return;
                }

                int firstFrame = Volatile.Read(ref firstObjectFrame);
                if (firstFrame >= n) firstFrame = -1;

                string line = summarise(n, firstFrame, full);
                buildInputs(n);
                Logger.Log(line);
                Logger.Log(gcReasonsLine);
                Logger.Log(gen0SizeLine);
                Logger.Log(inputLine);
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

        private static void buildInputs(int n)
        {
            int total = 0;
            foreach (var lane in lanes!)
                total += lane.Count;

            inCount = total;
            inPumpMs = new double[total];
            inOsToPump = new float[total];
            inPumpToUpdate = new float[total];
            inUpdateToPresent = new float[total];
            inUpdateToPublish = new float[total];
            inPublishToDraw = new float[total];
            inDrawToPresent = new float[total];
            inKind = new byte[total];

            // Present time of draw frame k = sum of gap_ms[0..k] after the draw origin.
            double[] presentMs = new double[n];
            double acc = 0;

            for (int k = 0; k < n; k++)
            {
                acc += gapMs![k];
                presentMs[k] = acc;
            }

            // Start of update frame j = update origin offset + sum of frame_ms[0..j-1], after the draw origin. Frame u (still running at the end) has a start but no publish time.
            int u = updateCount;
            double[] updateStartMs = new double[u + 1];
            updateStartMs[0] = (updateOrigin - drawOrigin) * ticksToMs;

            for (int j = 1; j <= u; j++)
                updateStartMs[j] = updateStartMs[j - 1] + updateMs![j - 1];

            // Frame age at present for every draw frame.
            ageAtPresent = new float[n];
            publishToPresentMs = new float[n];

            for (int k = 0; k < n; k++)
            {
                int j = drawnUpdate![k];
                bool known = j >= 0 && j <= u;
                ageAtPresent[k] = known ? (float)(presentMs[k] - updateStartMs[j]) : float.NaN;
                publishToPresentMs[k] = known && j < u && !float.IsNaN(publishMs![j]) ? (float)(presentMs[k] - (updateStartMs[j] + publishMs[j])) : float.NaN;
            }

            int o = 0;

            foreach (var lane in lanes)
            {
                for (int r = 0; r < lane.Count; r++, o++)
                {
                    inPumpMs[o] = (lane.Pump[r] - drawOrigin) * ticksToMs;
                    inOsToPump[o] = lane.OsToPump[r];
                    inPumpToUpdate[o] = (float)((lane.Update[r] - lane.Pump[r]) * ticksToMs);
                    inKind[o] = lane.Kind[r];

                    // First draw frame that drew this update frame or a later one (the input was applied before that frame's draw nodes were built).
                    int j = lane.UpdateFrame[r];
                    int lo = 0, hi = n;

                    while (lo < hi)
                    {
                        int mid = (lo + hi) >> 1;
                        if (drawnUpdate![mid] >= j) hi = mid;
                        else lo = mid + 1;
                    }

                    double consumeMs = (lane.Update[r] - drawOrigin) * ticksToMs;
                    inUpdateToPresent[o] = lo < n ? (float)(presentMs[lo] - consumeMs) : float.NaN;

                    // update = consume -> publish of the consuming update frame, queue = publish -> start of the presenting draw, draw+swap = draw start -> present.
                    inUpdateToPublish[o] = inPublishToDraw[o] = inDrawToPresent[o] = float.NaN;

                    if (lo < n && j >= 0 && j < u && !float.IsNaN(publishMs![j]))
                    {
                        double publishAbs = updateStartMs[j] + publishMs[j];
                        double drawStart = presentMs[lo] - swapMs![lo] - drawMs![lo];
                        inUpdateToPublish[o] = (float)(publishAbs - consumeMs);
                        inPublishToDraw[o] = (float)(drawStart - publishAbs);
                        inDrawToPresent[o] = (float)(presentMs[lo] - drawStart);
                    }
                }
            }

            inputLine = summariseInputs();
        }

        private static string summariseInputs()
        {
            if (inCount == 0)
                return "[inputdelay] no inputs recorded";

            long firstTicks = Volatile.Read(ref firstObjectTicks);
            double firstMs = firstTicks != 0 ? (firstTicks - drawOrigin) * ticksToMs : double.NaN;
            var sb = new StringBuilder("[inputdelay] ms; os = SDL event timestamp, pump = input thread, update = update thread consumed it, present = end of the first swap drawing that update frame; update->present = update(consume->publish) + queue(publish->draw start) + draw+swap(draw start->present), left out (n/a) where the consuming update frame has no publish time");

            if (!double.IsNaN(firstMs))
                appendInputGroups(sb, "from first object", firstMs);
            else
                sb.Append(" | first object not marked");

            appendInputGroups(sb, "whole session", double.NegativeInfinity);

            int firstFrame = Volatile.Read(ref firstObjectFrame);

            if (firstFrame >= 0 && firstFrame < ageAtPresent!.Length)
                appendFrameAge(sb, "from first object", firstFrame);

            appendFrameAge(sb, "whole session", 0);

            foreach (var lane in lanes!)
            {
                if (lane.Full) sb.Append(" (input buffer full, recording stopped early)");
                if (lane.Lost) sb.Append(" (some inputs were lost before the update thread consumed them)");
            }

            return sb.ToString();
        }

        private static void appendInputGroups(StringBuilder sb, string title, double fromMs)
        {
            sb.Append(" | ").Append(title).Append(':');
            appendInputGroup(sb, "keys+buttons", fromMs, 0, 3);
            appendInputGroup(sb, "mouse move", fromMs, 4, 4);
            appendInputGroup(sb, "pen move", fromMs, 5, 5);
            appendInputGroup(sb, "pen touch+buttons", fromMs, 6, 9);
        }

        private static void appendInputGroup(StringBuilder sb, string name, double fromMs, int kindFrom, int kindTo)
        {
            var osPump = new List<float>();
            var pumpUpdate = new List<float>();
            var osUpdate = new List<float>();
            var osPresent = new List<float>();
            var updatePresent = new List<float>();
            var updatePublish = new List<float>();
            var publishDraw = new List<float>();
            var drawPresent = new List<float>();
            int count = 0;

            for (int i = 0; i < inCount; i++)
            {
                if (inKind![i] < kindFrom || inKind[i] > kindTo) continue;
                if (inPumpMs![i] + inPumpToUpdate![i] < fromMs) continue;

                count++;
                pumpUpdate.Add(inPumpToUpdate[i]);

                if (!float.IsNaN(inOsToPump![i]))
                {
                    osPump.Add(inOsToPump[i]);
                    osUpdate.Add(inOsToPump[i] + inPumpToUpdate[i]);

                    if (!float.IsNaN(inUpdateToPresent![i]))
                        osPresent.Add(inOsToPump[i] + inPumpToUpdate[i] + inUpdateToPresent[i]);
                }

                if (!float.IsNaN(inUpdateToPresent![i]))
                    updatePresent.Add(inUpdateToPresent[i]);

                if (!float.IsNaN(inUpdateToPublish![i]))
                {
                    updatePublish.Add(inUpdateToPublish[i]);
                    publishDraw.Add(inPublishToDraw![i]);
                    drawPresent.Add(inDrawToPresent![i]);
                }
            }

            sb.Append(" [").Append(name).Append(" n=").Append(count.ToString(CultureInfo.InvariantCulture)).Append(']');
            appendMetric(sb, "os->pump", osPump);
            appendMetric(sb, "pump->update", pumpUpdate);
            appendMetric(sb, "os->update", osUpdate);
            appendMetric(sb, "os->present", osPresent);
            appendMetric(sb, "update->present", updatePresent);
            appendMetric(sb, "= update(consume->publish)", updatePublish);
            appendMetric(sb, "+ queue(publish->draw start)", publishDraw);
            appendMetric(sb, "+ draw+swap(draw start->present)", drawPresent);
        }

        private static void appendFrameAge(StringBuilder sb, string title, int fromFrame)
        {
            var age = new List<float>();
            var publish = new List<float>();

            for (int k = fromFrame; k < ageAtPresent!.Length; k++)
            {
                if (!float.IsNaN(ageAtPresent[k]))
                    age.Add(ageAtPresent[k]);

                if (!float.IsNaN(publishToPresentMs![k]))
                    publish.Add(publishToPresentMs[k]);
            }

            sb.Append(" | ").Append(title).Append(": [frame age at present n=").Append(age.Count.ToString(CultureInfo.InvariantCulture)).Append(']');
            appendMetric(sb, "present - start of update frame drawn", age);
            appendMetric(sb, "present - its publish", publish);
        }

        private static void appendMetric(StringBuilder sb, string name, List<float> values)
        {
            var c = CultureInfo.InvariantCulture;
            sb.Append(' ').Append(name).Append(' ');

            if (values.Count == 0)
            {
                sb.Append("n/a;");
                return;
            }

            values.Sort();
            double sum = 0;
            foreach (float f in values)
                sum += f;

            sb.Append(string.Create(c, $"mean {sum / values.Count:F3} p50 {values[(values.Count - 1) / 2]:F3} p99 {values[Math.Max(0, (int)Math.Ceiling(0.99 * values.Count) - 1)]:F3} max {values[^1]:F3};"));
        }

        private static string gen0Summary()
        {
            var c = CultureInfo.InvariantCulture;
            int n = secCount;
            var sizes = new List<ulong>();

            for (int i = 0; i < n; i++)
            {
                if (secGen0Before![i] > 0)
                    sizes.Add(secGen0Before[i]);
            }

            if (sizes.Count == 0)
                return "[framestats] gen0 size before GC: no samples";

            sizes.Sort();
            return string.Create(c, $"[framestats] gen0 size before the most recent GC, sampled per second: median {sizes[sizes.Count / 2] / 1024.0:F0} KB, min {sizes[0] / 1024.0:F0} KB, max {sizes[^1] / 1024.0:F0} KB ({sizes.Count} samples)");
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
            uint inputFlags = 0;
            foreach (var lane in lanes!)
                inputFlags |= (lane.Full ? 1u : 0u) | (lane.Lost ? 2u : 0u);

            long firstTicks = Volatile.Read(ref firstObjectTicks);
            double firstObjectMs = firstTicks != 0 ? (firstTicks - drawOrigin) * ticksToMs : -1;
            int firstUpdate = Volatile.Read(ref firstObjectUpdate);

            byte[] header = new byte[headerSize];
            Encoding.ASCII.GetBytes("OSUFRMST").CopyTo(header, 0);
            BitConverter.TryWriteBytes(header.AsSpan(8), 4u);
            BitConverter.TryWriteBytes(header.AsSpan(12), (uint)headerSize);
            BitConverter.TryWriteBytes(header.AsSpan(16), (uint)n);
            BitConverter.TryWriteBytes(header.AsSpan(20), (uint)u);
            BitConverter.TryWriteBytes(header.AsSpan(24), (uint)sec);
            BitConverter.TryWriteBytes(header.AsSpan(28), firstFrame);
            BitConverter.TryWriteBytes(header.AsSpan(32), firstFrame >= 0 && firstUpdate >= 0 && firstUpdate <= u ? firstUpdate : -1);
            BitConverter.TryWriteBytes(header.AsSpan(36), flags);
            BitConverter.TryWriteBytes(header.AsSpan(40), offsetMs);
            BitConverter.TryWriteBytes(header.AsSpan(48), (uint)inCount);
            BitConverter.TryWriteBytes(header.AsSpan(52), inputFlags);
            BitConverter.TryWriteBytes(header.AsSpan(56), firstObjectMs);
            BitConverter.TryWriteBytes(header.AsSpan(header_fixed), (uint)list.Length);
            list.CopyTo(header, header_fixed + 4);
            s.Write(header, 0, header.Length);

            s.Write(MemoryMarshal.AsBytes(gapMs.AsSpan(0, n)));
            s.Write(MemoryMarshal.AsBytes(readyMs.AsSpan(0, n)));
            s.Write(MemoryMarshal.AsBytes(readMs.AsSpan(0, n)));
            s.Write(MemoryMarshal.AsBytes(drawMs.AsSpan(0, n)));
            s.Write(MemoryMarshal.AsBytes(swapMs.AsSpan(0, n)));
            s.Write(gcFlag.AsSpan(0, n));
            s.Write(MemoryMarshal.AsBytes(drawnUpdate.AsSpan(0, n)));
            s.Write(MemoryMarshal.AsBytes(updateMs.AsSpan(0, u)));
            s.Write(MemoryMarshal.AsBytes(publishMs.AsSpan(0, u)));
            s.Write(MemoryMarshal.AsBytes(secElapsed.AsSpan(0, sec)));
            s.Write(MemoryMarshal.AsBytes(secAlloc.AsSpan(0, sec)));
            s.Write(MemoryMarshal.AsBytes(secPause.AsSpan(0, sec)));
            s.Write(MemoryMarshal.AsBytes(secGen0.AsSpan(0, sec)));
            s.Write(MemoryMarshal.AsBytes(secGen1.AsSpan(0, sec)));
            s.Write(MemoryMarshal.AsBytes(secGen2.AsSpan(0, sec)));
            s.Write(MemoryMarshal.AsBytes(inPumpMs.AsSpan(0, inCount)));
            s.Write(MemoryMarshal.AsBytes(inOsToPump.AsSpan(0, inCount)));
            s.Write(MemoryMarshal.AsBytes(inPumpToUpdate.AsSpan(0, inCount)));
            s.Write(MemoryMarshal.AsBytes(inUpdateToPresent.AsSpan(0, inCount)));
            s.Write(inKind.AsSpan(0, inCount));
        }

        private static void writeTxt(StreamWriter w, string line)
        {
            var c = CultureInfo.InvariantCulture;
            w.WriteLine(line);
            w.WriteLine(gcReasonsLine);
            w.WriteLine(gen0SizeLine);
            w.WriteLine(inputLine);
            w.WriteLine("per second (deltas): elapsed_s alloc_MB gc_pause_ms gen0 gen1 gen2 gen0_before_KB gen0_after_KB");

            for (int i = 0; i < secCount; i++)
                w.WriteLine(string.Create(c, $"{secElapsed![i]:F2} {secAlloc![i] / 1048576.0:F2} {secPause![i]:F2} {secGen0![i]} {secGen1![i]} {secGen2![i]} {secGen0Before![i] / 1024.0:F0} {secGen0After![i] / 1024.0:F0}"));
        }
    }
}
