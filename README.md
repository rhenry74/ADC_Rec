<img width="1906" height="1063" alt="image" src="https://github.com/user-attachments/assets/7e5a0c2c-ef96-4928-ad1a-68a94b9c6b3f" />


# ADC_Rec — Data Flow Architecture

## Data Flow: Serial Ingest → Audio Monitor Output

### Overview
The app uses a multi-threaded architecture to process serial data through parsing, mixing, and audio output while keeping the UI responsive.

---

## Thread 1: Serial Port RX (System Thread Pool via SerialPort)
**Triggered by: SerialPort.DataReceived event (async callback)**

| Function | Description |
|----------|-------------|
| `SerialService.OnDataReceived()` | Runs on thread pool. Reads raw bytes from serial port buffer, fires `DataReceived` event |
| `MainWindow.SerialService_DataReceived()` | If `_running == true`, calls `Parser.Feed(data)` |

---

## Thread 2: Parser (Called from Serial RX thread)
| Function | Description |
|----------|-------------|
| `Parser.Feed()` | Appends bytes to internal buffer, searches for 0x55 0xAA header sentinel |
| `Parser.ParsePayload()` | Parses 98-byte payload: 4 channels × 8 samples × 3 bytes (16-bit little-endian) |
| `Parser.PacketParsed` event | Fires with `Packet` object containing `uint[4,8] Samples` |
| `MainWindow.Parser_PacketParsed()` | Enqueues packet to `_packetQueue`, increments `_pendingPacketCount` |

---

## Thread 3: Background Drain (Dedicated Long-Running Task)
**Created by: `Task.Factory.StartNew(..., TaskCreationOptions.LongRunning)`**

| Function | Description |
|----------|-------------|
| `MainWindow.DrainLoop()` | Runs on dedicated background thread. Continuously: |
| - Dequeues packets from `_packetQueue` (up to 256 per iteration) | |
| - `_audioMixService.ProcessAndPlotPackets(batch, _plotManager)` | **KEY FUNCTION - processes samples, mixes, and updates plot manager** |

---

## Audio & Plot Processing (Called from Drain Thread)
| Function | Description |
|----------|-------------|
| `AudioMixService.ProcessAndPlotPackets()` | Takes batch of packets, processes each sample: |
| - `ConvertUnsignedToFloat()` | Converts 16-bit unsigned → float [-1, 1] |
| - `ApplyAdaptiveDcBlock()` | Adaptive DC offset removal |
| - Stereo Mixing | 4 channels → 2 channels (L/R) using gain and pan |
| - Updates Metering | Calculates LED levels, peaks, and averages |
| - Sends to `PlotManager` | Stores processed samples for waveform display |
| - `WritePlayback()` | **Sends to NAudio output** |
| - `WriteWav()` | Optional: writes to WAV file |
| - `StoreMonitorSample()` | Adds to output list |
| - `UpdateMeters()` | Calculates peak/avg for LED meters |
| - `WritePlayback()` | **Sends to NAudio output** |
| - `WriteWav()` | Optional: writes to WAV file |

---

## Thread 4: NAudio Audio Playback (NAudio Internal Thread)
| Function | Description |
|----------|-------------|
| `WaveOutEvent.Play()` | NAudio creates its own internal playback thread |
| `BufferedWaveProvider` | Ring buffer that NAudio reads from |
| `WaveOutEvent` calls `AddSamples()` | Drain thread writes float samples here |

---

## Thread 5: UI Refresh Timer (System Thread Pool)
**Created by: `new Timer(_ => ProcessPendingPackets(), null, 0, 50)`**

| Function | Description |
|----------|-------------|
| `ProcessPendingPackets()` | Runs every 50ms (~20Hz) |
| `DispatcherQueue.TryEnqueue()` | Marshals to UI thread |
| `DrawChannel()` | Renders waveforms to Canvas |
| `UpdateMeterUi()` | Updates LED meter visuals |
| `FlushLogsAndUpdateQueueStatus()` | Updates log TextBox |

---

## Thread 6: Counter Timer (System Thread Pool)
**Created by: `new Timer(_ => UpdateBytesUi(), null, Timeout.Infinite, 200)`**

| Function | Description |
|----------|-------------|
| `UpdateBytesUi()` | Updates diagnostic text (bytes, queue, dropped packets) |

---

## Data Path Latency
```
Serial RX → Parser.Feed() → packetQueue → DrainLoop() → ProcessPackets() → BufferedWaveProvider → NAudio Playback Thread → Speakers
```

---

## Filter Architecture

### UI & Pipeline Processing
The application implements a decoupled, type-safe architecture for filters:

1.  **Pipeline Processing**:
    -   Filters implement `IFilter`.
    -   The `Process(float sample, ChannelBinding targetChannel)` method is called per-sample in the `AudioMixService` processing loop.
    -   Filters maintain internal state (buffers) keyed by `ChannelBinding` to ensure correct audio processing when the same filter instance is applied to multiple channels.
    -   The interface was simplified to operate on a per-sample basis for clarity and consistency, eliminating unnecessary buffer management at the interface level.

2.  **Filter UI**:
    -   Each filter has a corresponding `UserControl` (e.g., `PeakingEQControl`).
    -   `FilterCardControl` acts as a host and uses `ParamPresenter` (a `ContentPresenter`) to inject the specific UI control based on the filter type using a simple type-check.
    -   Controls use `DataBinding` to the filter instance's properties to ensure settings remain synchronized with the processing model.

### Available Filters

-   **Compressor**:
    -   Reduces the dynamic range of audio signals by lowering levels that exceed a specified threshold.
    -   *Parameters*:
        -   `ThresholdDb`: The level above which compression is applied.
        -   `Ratio`: The amount of reduction (e.g., 4:1 means output increases by 1dB for every 4dB of input increase).
        -   `AttackMs`: Time taken for the filter to reach full gain reduction when signal exceeds threshold.
        -   `ReleaseMs`: Time taken for the filter to return to unity gain after signal drops below threshold.
        -   `MakeupGainDb`: Output gain adjustment to compensate for level loss.

-   **Noise Suppression**:
    -   Reduces low-level background noise by attenuating signals that fall below a certain threshold (Noise Gate).
    -   *Parameters*:
        -   `ThresholdDb`: The level below which the gate closes.
        -   `ReductionDb`: The amount of gain reduction applied when the signal is below the threshold.
        -   `AttackMs`: Time taken for the gate to open.
        -   `ReleaseMs`: Time taken for the gate to close.

-   **Peaking EQ**:
    -   A parametric equalizer filter that boosts or cuts a specific frequency range.
    -   *Parameters*:
        -   `Frequency`: The center frequency to be boosted or cut.
        -   `GainDb`: The boost or cut amount in decibels.
        -   `Q`: The quality factor; higher values mean a narrower frequency band is affected.

-   **Shelf (Low/High)**:
    -   Provides a boost or cut above (High Shelf) or below (Low Shelf) a cutoff frequency.
    -   *Parameters*:
        -   `Frequency`: The cutoff frequency.
        -   `GainDb`: The boost or cut amount in decibels.
        -   `Slope`: The steepness of the shelf transition.

-   **Reverb**:
    -   Simulates acoustic reflections to create a sense of space.
    -   *Parameters*:
        -   `RoomSize`: Affects the feedback intensity of the internal delay lines.
        -   `Damping`: Controls how quickly high frequencies decay.
        -   `WetLevel`: The ratio of processed (reverb) signal to input signal.

-   **Delay**:
    -   Produces an echo effect by repeating the audio signal after a specified time.
    -   *Parameters*:
        -   `DelayTimeMs`: The time interval between the original signal and the echo.
        -   `Feedback`: The amount of output signal fed back into the input (0.1 increments).
        -   `WetLevel`: The ratio of processed (delay) signal to input signal (0.1 increments).

---

## Key Components

### Models
- `Packet` - Contains `uint[4,8] Samples` (4 channels × 8 samples each)

### Services
- `SerialService` - Serial port communication
- `Parser` - Binary packet parsing (0x55 0xAA header)
- `AudioMixService` - Audio mixing, gain, pan, DC block, meter calculation

### Managers
- `PlotManager` - Circular buffer for waveform display data
