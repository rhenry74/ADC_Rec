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
| `Parser.ParsePayload()` | Parses 98-byte payload: 4 channels × 8 samples × 3 bytes (24-bit little-endian) |
| `Parser.PacketParsed` event | Fires with `Packet` object containing `uint[4,8] Samples` |
| `MainWindow.Parser_PacketParsed()` | Enqueues packet to `_packetQueue`, increments `_pendingPacketCount` |

---

## Thread 3: Background Drain (Dedicated Long-Running Task)
**Created by: `Task.Factory.StartNew(..., TaskCreationOptions.LongRunning)`**

| Function | Description |
|----------|-------------|
| `MainWindow.DrainLoop()` | Runs on dedicated background thread. Continuously: |
| - Dequeues up to 2048 packets from `_packetQueue` | |
| - `_plotManager.AddPacketsBatch(batch)` | Stores samples in circular buffers for waveform display |
| - `_audioMixService.ProcessPackets(batch)` | **KEY FUNCTION - sends to audio** |

---

## Audio Processing (Called from Drain Thread)
| Function | Description |
|----------|-------------|
| `AudioMixService.ProcessPackets()` | Takes batch of packets, processes each sample: |
| - `ConvertUnsignedToFloat()` | Converts 24-bit unsigned → float [-1, 1] |
| - Applies gain & pan per channel | Stereo mixing: 4ch → 2ch (L/R) |
| - `ApplyDcBlock()` | DC offset removal (optional) |
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

## Key Components

### Models
- `Packet` - Contains `uint[4,8] Samples` (4 channels × 8 samples each)

### Services
- `SerialService` - Serial port communication
- `Parser` - Binary packet parsing (0x55 0xAA header)
- `AudioMixService` - Audio mixing, gain, pan, DC block, meter calculation

### Managers
- `PlotManager` - Circular buffer for waveform display data
