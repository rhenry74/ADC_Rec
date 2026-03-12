# ADC_Rec Application Analysis

## Application Purpose

The ADC_Rec application is a real-time audio data acquisition and playback system designed to process 4-channel analog-to-digital converter (ADC) data. It receives serial data from hardware, parses binary packets, performs audio mixing and processing, and outputs the resulting audio to speakers while providing visual feedback through waveform displays and meter indicators.

## Data Flow

### 1. Serial Data Ingestion
- **Source**: Hardware connected via serial port (COM port)
- **Process**: `SerialService` listens for incoming data via `SerialPort.DataReceived` event
- **Action**: Raw bytes are read from the serial buffer and passed to the parser
- **Thread**: System thread pool (async callback)

### 2. Packet Parsing
- **Component**: `Parser` service
- **Process**: 
  - Buffers incoming bytes until a 0x55 0xAA header is detected
  - Extracts 98-byte payloads (4 channels × 8 samples × 3 bytes each)
  - Converts 16-bit little-endian samples to unsigned integers
- **Output**: `Packet` objects containing `uint[4,8] Samples`
- **Thread**: Called from Serial RX thread

### 3. Packet Queuing
- **Queue**: `ConcurrentQueue<Packet>` in `MainWindow`
- **Purpose**: Decouples parsing from processing to prevent data loss
- **Bounded**: Maximum 4096 packets to prevent memory overflow
- **Management**: Automatic dropping of oldest packets when queue exceeds limit

### 4. Background Processing (Drain Loop)
- **Thread**: Dedicated long-running task
- **Process**: 
  - Dequeues up to 2048 packets from the queue
  - Stores samples in circular buffers for waveform display (`PlotManager`)
  - Processes packets for audio mixing (`AudioMixService`)
- **Frequency**: Continuous processing at high speed

### 5. Audio Processing
- **Component**: `AudioMixService`
- **Process**:
  - **Per-channel DC Blocking**: Estimates DC offset over 1 second and removes it from each channel
  - **Gain Control**: Applies configurable gain per channel
  - **Panning**: Applies stereo panning per channel
  - **Mixing**: Combines 4 channels into stereo output (2 channels)
  - **Output**: Sends processed samples to NAudio for playback

### 6. Audio Output
- **Component**: NAudio (`WaveOutEvent`)
- **Process**:
  - Uses `BufferedWaveProvider` as ring buffer
  - NAudio creates its own internal playback thread
  - Samples are written to the buffer by the drain loop
  - Audio is played through speakers

### 7. User Interface
- **Components**:
  - Waveform displays (Canvas elements)
  - LED meter indicators
  - Diagnostic text displays
  - Control panels (gain, pan, recording)
- **Refresh Rate**: ~20Hz (50ms intervals)
- **Thread**: UI refresh timer on system thread pool

### 8. Recording
- **Functionality**: 
  - Records raw binary packets to .bin files
  - Records mixed audio to .wav files
- **Process**: Writes to disk during processing in the background drain loop

## Key Features

1. **Real-time Processing**: Multi-threaded architecture keeps UI responsive while handling high-speed data
2. **Per-channel DC Blocking**: Improved algorithm estimates DC offset over time for each channel
3. **Configurable Audio Processing**: Gain and pan controls per channel
4. **Visual Feedback**: Waveform displays and LED meters for real-time monitoring
5. **Data Recording**: Both raw and processed audio recording capabilities
6. **Diagnostic Tools**: Comprehensive logging and health metrics

## Thread Architecture

1. **Serial RX Thread**: System thread pool via SerialPort
2. **Parser Thread**: Called from Serial RX thread
3. **Background Drain Thread**: Dedicated long-running task
4. **NAudio Playback Thread**: NAudio internal thread
5. **UI Refresh Timer**: System thread pool (~20Hz)
6. **Counter Timer**: System thread pool (200ms intervals)

## Data Path Latency
```
Serial RX → Parser.Feed() → packetQueue → DrainLoop() → ProcessPackets() → BufferedWaveProvider → NAudio Playback Thread → Speakers
```

This implementation provides a robust, real-time system for processing ADC data with proper separation of concerns between data ingestion, processing, and output.