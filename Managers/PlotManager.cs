using System;
using System.Collections.Generic;
using System.Linq;
using ADC_Rec.Models;

namespace ADC_Rec.Managers
{
    public class PlotManager
    {
        private readonly int _capacity;
        private readonly float[][] _buffers;
        private readonly uint[][] _rawBuffers;
        private readonly int[] _writeIndex;
        private readonly int[] _count;
        private readonly object _lock = new object();

        public PlotManager(int historySamplesPerChannel = 44100) // default 1s @ 44.1k
        {
            _capacity = Math.Max(1024, historySamplesPerChannel);
            _buffers = new float[Packet.NumChannels][];
            _rawBuffers = new uint[Packet.NumChannels][];
            _writeIndex = new int[Packet.NumChannels];
            _count = new int[Packet.NumChannels];
            for (int ch = 0; ch < Packet.NumChannels; ch++)
            {
                _buffers[ch] = new float[_capacity];
                _rawBuffers[ch] = new uint[_capacity];
                _writeIndex[ch] = 0;
                _count[ch] = 0;
            }
        }

        // Add a batch of packets in order. voltsPerCycle is accepted for backward compatibility but plotting uses integer scaled samples according to plotBits.
        public void AddPacketsBatch(IEnumerable<Packet> pkts, float voltsPerCycle, int plotBits)
        {
            if (pkts == null) return;
            int shift = Math.Max(0, 24 - Math.Max(1, Math.Min(24, plotBits)));
            lock (_lock)
            {
                foreach (var pkt in pkts)
                {
                    for (int ch = 0; ch < Packet.NumChannels; ch++)
                    {
                        for (int i = 0; i < Packet.BufferLen; i++)
                        {
                            uint raw24 = pkt.Samples[ch, i] & 0x00FFFFFFu;
                            // Store raw value (as float) and keep raw buffer; scaling to bit-depth or autoscale is done on draw
                            float value = (float)raw24;
                            int pos = _writeIndex[ch];
                            _buffers[ch][pos] = value;
                            // keep raw 24-bit available for hover/dump
                            _rawBuffers[ch][pos] = raw24;
                            _writeIndex[ch] = (pos + 1) % _capacity;
                            if (_count[ch] < _capacity) _count[ch]++;
                        }
                    }
                }
            }
        }

        // Convenience for single packet
        public void AddPacket(Packet pkt, float voltsPerCycle, int plotBits)
        {
            AddPacketsBatch(new[] { pkt }, voltsPerCycle, plotBits);
        }

        // Fill provided buffer with latest 'desiredSamples' samples for channel ch (most recent last)
        // Returns the number of samples written into outBuf (<= desiredSamples)
        public int FillChannelSnapshot(int ch, float[] outBuf, int desiredSamples)
        {
            if (ch < 0 || ch >= Packet.NumChannels) return 0;
            if (outBuf == null || outBuf.Length < desiredSamples) return 0;
            lock (_lock)
            {
                int available = Math.Min(_count[ch], desiredSamples);
                if (available == 0) return 0;
                int start = _writeIndex[ch] - available;
                if (start < 0) start += _capacity * ((-start) / _capacity + 1);
                start %= _capacity;
                int idx = 0;
                for (int i = 0; i < available; i++)
                {
                    outBuf[idx++] = _buffers[ch][(start + i) % _capacity];
                }
                return available;
            }
        }

        // Fill provided buffer with raw 24-bit samples (most recent last)
        public int FillChannelRawSnapshot(int ch, uint[] outBuf, int desiredSamples)
        {
            if (ch < 0 || ch >= Packet.NumChannels) return 0;
            if (outBuf == null || outBuf.Length < desiredSamples) return 0;
            lock (_lock)
            {
                int available = Math.Min(_count[ch], desiredSamples);
                if (available == 0) return 0;
                int start = _writeIndex[ch] - available;
                if (start < 0) start += _capacity * ((-start) / _capacity + 1);
                start %= _capacity;
                int idx = 0;
                for (int i = 0; i < available; i++)
                {
                    outBuf[idx++] = _rawBuffers[ch][(start + i) % _capacity];
                }
                return available;
            }
        }

        // Clear history and buffers
        public void Clear()
        {
            lock (_lock)
            {
                for (int ch = 0; ch < Packet.NumChannels; ch++)
                {
                    _writeIndex[ch] = 0;
                    _count[ch] = 0;
                    Array.Clear(_buffers[ch], 0, _buffers[ch].Length);
                    Array.Clear(_rawBuffers[ch], 0, _rawBuffers[ch].Length);
                }
            }
        }

        // Recompute buffers to raw values (no bit shifting) - scaling is done on the UI side to allow autoscale behavior
        public void RescaleBuffers(int plotBits)
        {
            lock (_lock)
            {
                for (int ch = 0; ch < Packet.NumChannels; ch++)
                {
                    for (int i = 0; i < _capacity; i++)
                    {
                        _buffers[ch][i] = _rawBuffers[ch][i];
                    }
                }
            }
        }

        // Return the maximum raw (24-bit) sample currently stored for channel ch
        public uint GetMaxRaw(int ch)
        {
            if (ch < 0 || ch >= Packet.NumChannels) return 0u;
            lock (_lock)
            {
                uint m = 0u;
                int cnt = _count[ch];
                if (cnt == 0) return 0u;
                int start = _writeIndex[ch] - cnt;
                if (start < 0) start += _capacity * ((-start) / _capacity + 1);
                start %= _capacity;
                for (int i = 0; i < cnt; i++)
                {
                    uint v = _rawBuffers[ch][(start + i) % _capacity];
                    if (v > m) m = v;
                }
                return m;
            }
        }

        public int GetHistoryCapacity() => _capacity;

        // Return how many samples are currently stored for channel ch
        public int GetAvailableSamples(int ch)
        {
            if (ch < 0 || ch >= Packet.NumChannels) return 0;
            lock (_lock) return _count[ch];
        }
    }
}