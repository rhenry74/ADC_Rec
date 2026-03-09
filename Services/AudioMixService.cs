using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ADC_Rec.Models;
using NAudio.Wave;

namespace ADC_Rec.Services
{
    public class AudioMixService : IDisposable
    {
        private const int InputSampleRate = 44100;
        private const int OutputSampleRate = 44100;
        private const int OutputChannels = 2;
        private const int BitsPerSample = 16;
        private const int LedCount = 20;

        private readonly object _lock = new object();
        private readonly float[] _channelGains = new float[Packet.NumChannels];
        private readonly float[] _channelPans = new float[Packet.NumChannels];

        private WaveOutEvent? _waveOut;
        private BufferedWaveProvider? _playbackBuffer;
        private bool _playbackStarted;

        // WAV writing - separate from playback to avoid race conditions
        private readonly object _wavLock = new object();
        private List<float> _wavSampleBuffer = new List<float>();

        private BinaryWriter? _wavWriter;
        private bool _writeWav;
        private long _wavDataBytes;
        private const int WavBufferSamplesMax = 44100 * 2 * 8; // 8 seconds max buffer

        private float _dcLeft;
        private float _dcRight;
        private readonly float[] _dcChannelEstimates = new float[Packet.NumChannels];
        
        private const float DcAlpha = 0.995f;
        private bool _dcBlockEnabled = true;

        private readonly float[] _meterLedsLeft = new float[LedCount];
        private readonly float[] _meterLedsRight = new float[LedCount];

        private float _peakHoldLeft;
        private float _peakHoldRight;
        private float _avgHoldLeft;
        private float _avgHoldRight;
        private const float PeakHoldDecay = 0.98f;
        private const float AvgHoldSmoothing = 0.9f;

        public AudioMixService()
        {
            for (int ch = 0; ch < Packet.NumChannels; ch++)
            {
                _channelGains[ch] = 1.0f;
                _channelPans[ch] = 0.0f;
            }
        }

        public void SetChannelGain(int ch, float gain)
        {
            if (ch < 0 || ch >= Packet.NumChannels) return;
            lock (_lock) { _channelGains[ch] = gain; }
        }

        public float[] GetChannelGainsSnapshot()
        {
            var gains = new float[Packet.NumChannels];
            lock (_lock) { Array.Copy(_channelGains, gains, Packet.NumChannels); }
            return gains;
        }

        public int[] GetChannelInputBitsSnapshot()
        {
            // Return default 16-bit for all channels (not 0!)
            int[] result = new int[Packet.NumChannels];
            for (int i = 0; i < result.Length; i++) result[i] = 16;
            return result;
        }

        public void SetChannelPan(int ch, float pan)
        {
            if (ch < 0 || ch >= Packet.NumChannels) return;
            pan = Math.Max(-1f, Math.Min(1f, pan));
            lock (_lock) { _channelPans[ch] = pan; }
        }

        public void SetDcBlockEnabled(bool enabled)
        {
            lock (_lock) { _dcBlockEnabled = enabled; }
        }

        public float[] GetMeterLedsLeft() => _meterLedsLeft;
        public float[] GetMeterLedsRight() => _meterLedsRight;
        public float PeakHoldLeft => _peakHoldLeft;
        public float PeakHoldRight => _peakHoldRight;
        public float AvgHoldLeft => _avgHoldLeft;
        public float AvgHoldRight => _avgHoldRight;

        public double GetPlaybackBufferedMilliseconds()
        {
            if (_playbackBuffer == null) return 0;
            var format = _playbackBuffer.WaveFormat;
            if (format == null || format.AverageBytesPerSecond <= 0) return 0;
            return _playbackBuffer.BufferedBytes * 1000.0 / format.AverageBytesPerSecond;
        }

        public void StartPlayback()
        {
            if (_playbackStarted) return;
            var monitorFormat = WaveFormat.CreateIeeeFloatWaveFormat(InputSampleRate, OutputChannels);
            _playbackBuffer = new BufferedWaveProvider(monitorFormat)
            {
                BufferLength = InputSampleRate * OutputChannels * sizeof(float) * 4, // 4 seconds
                DiscardOnBufferOverflow = true
            };

            _waveOut = new WaveOutEvent();
            _waveOut.Init(_playbackBuffer);
            _waveOut.Play();
            _playbackStarted = true;
        }

        public void StopPlayback()
        {
            _playbackStarted = false;
            try { _waveOut?.Stop(); } catch { }
            try { _waveOut?.Dispose(); } catch { }
            _waveOut = null;
            _playbackBuffer = null;
        }

        public string StartWavWrite(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("Invalid folder", nameof(folder));
            StopWavWrite();
            string path = Path.Combine(folder, $"ADCRecMix_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
            var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            _wavWriter = new BinaryWriter(fs);
            _wavDataBytes = 0;
            // Clear and initialize the WAV buffer
            lock (_wavLock)
            {
                _wavSampleBuffer.Clear();
            }
            WriteWavHeaderPlaceholder(_wavWriter);
            _writeWav = true;
            return path;
        }

        public void StopWavWrite()
        {
            _writeWav = false;
            if (_wavWriter == null) return;
            try
            {
                // Flush any remaining samples in the buffer
                lock (_wavLock)
                {
                    while (_wavSampleBuffer.Count > 0)
                    {
                        FlushWavBuffer();
                    }
                    _wavSampleBuffer.Clear();
                }
                UpdateWavHeader(_wavWriter, _wavDataBytes);
            }
            catch { }
            try { _wavWriter.Dispose(); } catch { }
            _wavWriter = null;
        }

        public void ProcessPackets(IEnumerable<Packet> packets)
        {
            if (packets == null) return;

            float[] gains = new float[Packet.NumChannels];
            float[] pans = new float[Packet.NumChannels];
            lock (_lock)
            {
                Array.Copy(_channelGains, gains, Packet.NumChannels);
                Array.Copy(_channelPans, pans, Packet.NumChannels);
                // read once per batch for consistency
            }
            bool dcEnabled;
            lock (_lock)
            {
                dcEnabled = _dcBlockEnabled;
            }

            var outputSamples = new List<float>();
            foreach (var pkt in packets)
            {
                for (int i = 0; i < Packet.BufferLen; i++)
                {
                    float[] processedSamples = new float[Packet.NumChannels];
                    for (int ch = 0; ch < Packet.NumChannels; ch++)
                    {
                        uint raw = pkt.Samples[ch, i];
                        float sample = ConvertUnsignedToFloat(raw, 16); // Fixed 16-bit
                        
                        if (dcEnabled)
                        {
                            sample = ApplyAdaptiveDcBlock(sample, ch);
                        }
                        processedSamples[ch] = sample;
                    }

                    float mixL = 0f;
                    float mixR = 0f;
                    for (int ch = 0; ch < Packet.NumChannels; ch++)
                    {
                        float sample = processedSamples[ch];
                        float gain = gains[ch];
                        float pan = pans[ch];
                        float angle = (pan + 1f) * 0.25f * (float)Math.PI;
                        float leftGain = (float)Math.Cos(angle);
                        float rightGain = (float)Math.Sin(angle);
                        mixL += sample * gain * leftGain;
                        mixR += sample * gain * rightGain;
                    }
                    
                    StoreMonitorSample(mixL, mixR, outputSamples);
                }
            }

            if (outputSamples.Count > 0)
            {
                UpdateMeters(outputSamples);
                WritePlayback(outputSamples);
                WriteWav(outputSamples);
            }
        }

        private static void StoreMonitorSample(float left, float right, List<float> outputSamples)
        {
            outputSamples.Add(left);
            outputSamples.Add(right);
        }

        private void WritePlayback(List<float> outputSamples)
        {
            if (!_playbackStarted || _playbackBuffer == null) return;
            var bytes = new byte[outputSamples.Count * sizeof(float)];
            Buffer.BlockCopy(outputSamples.ToArray(), 0, bytes, 0, bytes.Length);
            _playbackBuffer.AddSamples(bytes, 0, bytes.Length);
        }

        private void WriteWav(List<float> outputSamples)
        {
            if (!_writeWav || _wavWriter == null) return;

            // Buffer samples for WAV writing (thread-safe)
            lock (_wavLock)
            {
                _wavSampleBuffer.AddRange(outputSamples);

                // Write in chunks to avoid blocking too long
                while (_wavSampleBuffer.Count >= 4096)
                {
                    FlushWavBuffer();
                }
            }
        }

        private void FlushWavBuffer()
        {
            if (_wavWriter == null || _wavSampleBuffer.Count == 0) return;

            int toWrite = Math.Min(_wavSampleBuffer.Count, 4096);
            for (int i = 0; i < toWrite; i++)
            {
                short v = (short)FloatTo16Bit(_wavSampleBuffer[i]);
                _wavWriter.Write(v);
                _wavDataBytes += 2;
            }
            _wavSampleBuffer.RemoveRange(0, toWrite);
            // Flush to ensure data is written to disk
            _wavWriter.Flush();
        }

        private void UpdateMeters(List<float> outputSamples)
        {
            float peakL = 0f;
            float peakR = 0f;
            float sumL = 0f;
            float sumR = 0f;
            int frameSamples = 0;
            for (int i = 0; i < outputSamples.Count; i += 2)
            {
                // VU meters should measure the amplitude (absolute value)
                // If DC Block is OFF, raw samples are centered on DC bias.
                // Convert to AC-only amplitude for the VU meter logic.
                float l = Math.Abs(outputSamples[i]);
                float r = Math.Abs(outputSamples[i + 1]);

                // Adjust for DC offset if DC Block is OFF
                if (!_dcBlockEnabled)
                {
                    // If DC block is OFF, the signal is on a DC bias.
                    // A 16-bit signal (32767 peak) has a mid-point bias.
                    // This means a full-scale signal (0 to 65535) centers on 32767.
                    // When converted to [-1, 1], the center is 0.0.
                    // The range is effectively doubled in terms of magnitude if DC isn't removed.
                    l *= 0.5f;
                    r *= 0.5f;
                }

                if (l > peakL) peakL = l;
                if (r > peakR) peakR = r;
                sumL += l;
                sumR += r;
                frameSamples++;
            }
            
            UpdateLedArray(_meterLedsLeft, peakL);
            UpdateLedArray(_meterLedsRight, peakR);

            _peakHoldLeft = Math.Max(peakL, _peakHoldLeft * PeakHoldDecay);
            _peakHoldRight = Math.Max(peakR, _peakHoldRight * PeakHoldDecay);

            if (frameSamples > 0)
            {
                float avgL = sumL / frameSamples;
                float avgR = sumR / frameSamples;
                _avgHoldLeft = (_avgHoldLeft * AvgHoldSmoothing) + (avgL * (1f - AvgHoldSmoothing));
                _avgHoldRight = (_avgHoldRight * AvgHoldSmoothing) + (avgR * (1f - AvgHoldSmoothing));
            }
        }

        private void UpdateLedArray(float[] leds, float level)
        {
            level = Math.Max(0f, Math.Min(1f, level));
            int lit = (int)Math.Round(level * leds.Length);
            for (int i = 0; i < leds.Length; i++)
            {
                leds[i] = i < lit ? 1f : 0f;
            }
        }

        private static float ConvertUnsignedToFloat(uint raw, int inputBits)
        {
            int bits = Math.Max(1, Math.Min(16, inputBits));
            int maxVal = (1 << bits) - 1;
            float mid = maxVal / 2f;
            float centered = raw - mid;
            return Math.Max(-1f, Math.Min(1f, centered / mid));
        }

        public static float GetNormalizationGainForBits(int inputBits)
        {
            int bits = Math.Max(1, Math.Min(16, inputBits));
            int maxVal = (1 << bits) - 1;
            float mid = maxVal / 2f;
            return 1f / Math.Max(1f, mid);
        }

        public static float GetScaleTo16BitCounts(int inputBits)
        {
            int bits = Math.Max(1, Math.Min(16, inputBits));
            int shift = 16 - bits;
            return (float)(1 << Math.Max(0, shift));
        }

        private static int FloatTo16Bit(float sample)
        {
            // If we don't clamp, it clips naturally when cast to short
            // This IS the hard clipper
            int v = (int)Math.Round(sample * 32767f);
            return Math.Max(-32768, Math.Min(32767, v));
        }

        private float ApplyAdaptiveDcBlock(float sample, int channelIndex)
        {
            // Always track the estimate to handle steady-state DC offsets.
            // Using a slightly faster time constant allows tracking moving offsets,
            // but is still slow enough not to affect audio frequencies.
            const float tau = 0.5f; // 0.5 seconds
            const float alpha = 1.0f / (tau * 44100.0f);

            // Always update, no threshold-based freeze to allow steady-state DC removal
            _dcChannelEstimates[channelIndex] += alpha * (sample - _dcChannelEstimates[channelIndex]);

            return sample - _dcChannelEstimates[channelIndex];
        }


        private static void WriteWavHeaderPlaceholder(BinaryWriter writer)
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(0);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)OutputChannels);
            writer.Write(OutputSampleRate);
            int byteRate = OutputSampleRate * OutputChannels * (BitsPerSample / 8);
            writer.Write(byteRate);
            short blockAlign = (short)(OutputChannels * (BitsPerSample / 8));
            writer.Write(blockAlign);
            writer.Write((short)BitsPerSample);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(0);
        }

        private static void UpdateWavHeader(BinaryWriter writer, long dataBytes)
        {
            long fileSize = 36 + dataBytes;
            writer.Seek(4, SeekOrigin.Begin);
            writer.Write((int)fileSize);
            writer.Seek(40, SeekOrigin.Begin);
            writer.Write((int)dataBytes);
        }

        public void Dispose()
        {
            StopWavWrite();
            StopPlayback();
        }
    }
}