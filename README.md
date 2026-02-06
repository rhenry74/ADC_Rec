# ADC_Rec — Architecture & Performance Notes

This document explains how the application processes incoming ADC data, how plotting is performed, and where current performance bottlenecks likely occur. It also provides concrete ideas for improving throughput and reducing dropped packets.

## 1. High‑Level Data Flow

```
SerialPort -> Parser -> Packet queue -> DrainLoop -> PlotManager buffers -> UI timer -> DrawChannel
```

**Key steps:**

1. **Serial receive** (`SerialService.OnDataReceived`)
   - Reads all available bytes from the COM port into a byte[] and fires `DataReceived`.

2. **Parser** (`Services.Parser.Feed`)
   - Buffers bytes and scans for packet sync `0x55 0xAA`.
   - Packet size is fixed: `2 + (NUM_CHANNELS * BUFFER_LEN * 3)` = `2 + (4 * 8 * 3) = 98` bytes.
   - Parses payload into `Packet` objects (`Packet.Samples[ch, i]`).
   - Emits `PacketParsed` event for each valid packet.
   - Maintains counters: `ParsedPacketCount`, `InvalidPacketCount`, `TrimmedBytesCount`.

3. **Packet queueing** (`MainWindow.Parser_PacketParsed`)
   - Enqueues packets into `_packetQueue` (bounded by `MaxPacketQueue = 4096`).
   - If queue exceeds limit, oldest packets are dropped immediately.
   - Drop logging is throttled (summary every 10s).
   - Maintains counters: `_pendingPacketCount`, `_droppedPacketCount`.

4. **DrainLoop (background)** (`MainWindow.DrainLoop`)
   - Runs continuously on a background task.
   - When `_running` is true, dequeues up to `DrainBatchSize` packets (512) per cycle.
   - Each batch is written to `PlotManager.AddPacketsBatch` (raw samples).
   - Maintains counters: `_processedPacketCount`, `_drainLoopIterations`, `_lastDrainBatchCount`, `_lastDrainTick`.

5. **Plot buffer storage** (`Managers.PlotManager`)
   - Stores raw 24‑bit samples in circular buffers per channel.
   - `FillChannelSnapshot` provides the most recent samples for drawing.

6. **UI Timer & Draw** (`MainWindow.ProcessPendingPackets` → `DrawChannel`)
   - UI timer fires ~30 Hz.
   - Pulls latest samples from `PlotManager` and draws on canvases.
   - Draw uses decimation by bucket (min/max per pixel column).
   - Plot bits control **vertical range** (0..2^bits−1).
   - Fit‑to‑data centers around midpoint (AC‑coupled).

## 2. Diagnostic Output (Copy Diag)

`Copy Diag` includes:

- `proc`: packets processed by DrainLoop
- `parsed`: packets parsed by Parser
- `invalid`: malformed packets
- `dropped`: packets dropped due to queue limit
- `queue`: current queue depth
- `trimmedBytes`: bytes trimmed from parser buffer overflow
- `drainIters`, `lastBatch`, `drainAgeMs`: drain loop activity
- `chX samples/max`: how many samples are stored and max raw value

If `parsed` increases but `proc` stays at 0, the drain loop is not running.

## 3. Why Graphs Are Choppy / Sluggish

Even with low CPU usage, the UI can still appear sluggish when:

1. **UI thread saturation / blocking**
   - `ProcessPendingPackets` runs on the UI thread and performs decimation + drawing for every channel each tick.
   - Canvas drawing uses a large `PointCollection` and rebuilds every frame.

2. **Queue pressure**
   - If Parser produces packets faster than DrainLoop consumes them, `_packetQueue` grows and drops occur.
   - Dropped packets reduce temporal resolution and visible continuity.

3. **Excessive allocations per frame**
   - `DrawChannel` creates new `Polyline`, `PointCollection`, and `Line` each frame.
   - `ProcessPendingPackets` allocates temporary arrays for hover values.

4. **DrainLoop scheduling**
   - If the DrainLoop doesn’t run or is delayed, buffers never update and queue overflows.

## 4. Performance Improvement Ideas

### A) Improve Drain Throughput
1. **Increase drain batch size**
   - Raise `DrainBatchSize` (e.g., 1024–4096) to reduce scheduling overhead.
2. **Decrease drain idle delay**
   - Lower `DrainIdleMs` when queue is non‑empty.
3. **Avoid per-packet allocations**
   - Reuse `List<Packet>` and preallocate buffers.

### B) Reduce UI Rendering Cost
1. **Lower UI refresh rate**
   - Drop UI timer to 15–20 Hz instead of 30 Hz.
2. **Reuse drawing objects**
   - Keep a single `Polyline` and update points instead of recreating objects.
3. **Reduce decimation overhead**
   - Reduce bucket count or precompute min/max in background.

### C) Mitigate Queue Overflows
1. **Backpressure strategy**
   - Instead of dropping per packet, drop in larger chunks when queue exceeds limit.
2. **Prioritize newest data**
   - If behind, skip old packets and only keep the newest batch.

### D) Serial Input Improvements
1. **Increase SerialPort buffer**
   - Adjust `SerialPort.ReadBufferSize` to reduce OS‑level drops.
2. **Use larger read sizes**
   - If possible, read fixed packet multiples to reduce fragmentation.

## 5. Next Steps (Suggested)

1. Confirm drain loop stability and `proc` increasing.
2. Increase drain batch size and reduce idle delay.
3. Reduce UI redraw frequency and reuse drawing objects.
4. Measure improvements with `Copy Diag` (proc, queue, dropped).

---

If you'd like, I can implement specific optimizations from the list above and measure improvements iteratively.