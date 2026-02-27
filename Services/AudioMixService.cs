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
        private const int BitsPerSample = 24;
        private const int LedCount = 20;

        private readonly object _lock = new object();
        private readonly float[] _channelGains = new float[Packet.NumChannels];
        private readonly float[] _channelPans = new float[Packet.NumChannels];
        private readonly int[] _channelInputBits = new int[Packet.NumChannels];

        private WaveOutEvent? _waveOut;
        private BufferedWaveProvider? _playbackBuffer;
        private bool _playbackStarted;

        private BufferedWaveProvider? _wavBuffer;
        private MediaFoundationResampler? _wavResampler;

        private BinaryWriter? _wavWriter;
        private bool _writeWav;
        private long _wavDataBytes;

        private float _dcLeft;
        private float _dcRight;
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
                _channelInputBits[ch] = 12;
            }
        }

        public void SetChannelInputBits(int ch, int bits)
        {
            if (ch < 0 || ch >= Packet.NumChannels) return;
            bits = Math.Max(8, Math.Min(24, bits));
            lock (_lock) { _channelInputBits[ch] = bits; }
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
            var bits = new int[Packet.NumChannels];
            lock (_lock) { Array.Copy(_channelInputBits, bits, Packet.NumChannels); }
            return bits;
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
                BufferLength = InputSampleRate * OutputChannels * sizeof(float),
                DiscardOnBufferOverflow = true
            };

            var wavInputFormat = WaveFormat.CreateIeeeFloatWaveFormat(InputSampleRate, OutputChannels);
            _wavBuffer = new BufferedWaveProvider(wavInputFormat)
            {
                BufferLength = InputSampleRate * OutputChannels * sizeof(float),
                DiscardOnBufferOverflow = true
            };
            _wavResampler = new MediaFoundationResampler(_wavBuffer, new WaveFormat(OutputSampleRate, 32, OutputChannels))
            {
                ResamplerQuality = 60
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
            _wavResampler?.Dispose();
            _wavResampler = null;
            _wavBuffer = null;
        }

        public string StartWavWrite(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("Invalid folder", nameof(folder));
            StopWavWrite();
            string path = Path.Combine(folder, $"ADCRecMix_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
            var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            _wavWriter = new BinaryWriter(fs);
            _wavDataBytes = 0;
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
            int[] bits = new int[Packet.NumChannels];
            lock (_lock)
            {
                Array.Copy(_channelGains, gains, Packet.NumChannels);
                Array.Copy(_channelPans, pans, Packet.NumChannels);
                Array.Copy(_channelInputBits, bits, Packet.NumChannels);
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
                    float mixL = 0f;
                    float mixR = 0f;
                    for (int ch = 0; ch < Packet.NumChannels; ch++)
                    {
                        uint raw = pkt.Samples[ch, i] & 0x00FFFFFFu;
                        float sample = ConvertUnsignedToFloat(raw, bits[ch]);
                        float gain = gains[ch];
                        float pan = pans[ch];
                        float angle = (pan + 1f) * 0.25f * (float)Math.PI;
                        float leftGain = (float)Math.Cos(angle);
                        float rightGain = (float)Math.Sin(angle);
                        mixL += sample * gain * leftGain;
                        mixR += sample * gain * rightGain;
                    }
                    if (dcEnabled)
                    {
                        mixL = ApplyDcBlock(mixL, ref _dcLeft);
                        mixR = ApplyDcBlock(mixR, ref _dcRight);
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
            _wavBuffer?.AddSamples(bytes, 0, bytes.Length);
        }

        private void WriteWav(List<float> outputSamples)
        {
            if (!_writeWav || _wavWriter == null) return;
            if (_wavResampler == null) return;
            var resampled = new float[outputSamples.Count];
            int bytesNeeded = resampled.Length * sizeof(float);
            var resampleBytes = new byte[bytesNeeded];
            int bytesRead = _wavResampler.Read(resampleBytes, 0, bytesNeeded);
            if (bytesRead <= 0) return;
            int samplesRead = bytesRead / sizeof(float);
            Buffer.BlockCopy(resampleBytes, 0, resampled, 0, bytesRead);
            for (int i = 0; i < samplesRead; i++)
            {
                int v = FloatTo24Bit(resampled[i]);
                _wavWriter.Write((byte)(v & 0xFF));
                _wavWriter.Write((byte)((v >> 8) & 0xFF));
                _wavWriter.Write((byte)((v >> 16) & 0xFF));
                _wavDataBytes += 3;
            }
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
                float l = Math.Abs(outputSamples[i]);
                float r = Math.Abs(outputSamples[i + 1]);
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
            int bits = Math.Max(1, Math.Min(24, inputBits));
            int maxVal = (1 << bits) - 1;
            float mid = maxVal / 2f;
            float centered = raw - mid;
            return Math.Max(-1f, Math.Min(1f, centered / mid));
        }

        public static float GetNormalizationGainForBits(int inputBits)
        {
            int bits = Math.Max(1, Math.Min(24, inputBits));
            int maxVal = (1 << bits) - 1;
            float mid = maxVal / 2f;
            return 1f / Math.Max(1f, mid);
        }

        public static float GetScaleTo24BitCounts(int inputBits)
        {
            int bits = Math.Max(1, Math.Min(24, inputBits));
            int shift = 24 - bits;
            return (float)(1 << Math.Max(0, shift));
        }

        private static int FloatTo24Bit(float sample)
        {
            sample = Math.Max(-1f, Math.Min(1f, sample));
            int v = (int)Math.Round(sample * 8388607f);
            if (v < 0) v += 1 << 24;
            return v & 0x00FFFFFF;
        }

        private static float ApplyDcBlock(float sample, ref float state)
        {
            float outSample = sample - state;
            state = sample + outSample * DcAlpha;
            return outSample;
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