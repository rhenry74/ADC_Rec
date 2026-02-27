using System;
using System.Collections.Generic;
using ADC_Rec.Models;

namespace ADC_Rec.Services
{
    public class Parser
    {
        public event Action<Packet>? PacketParsed;
        public event Action<string>? DebugLine;

        public long ParsedPacketCount => System.Threading.Interlocked.Read(ref _parsedPacketCount);
        public long InvalidPacketCount => System.Threading.Interlocked.Read(ref _invalidPacketCount);
        public long TrimmedBytesCount => System.Threading.Interlocked.Read(ref _trimmedBytesCount);

        // When verbose is false (default), parser will not emit ASCII debug lines to avoid flooding the host
        public bool Verbose { get; set; } = false;

        private readonly List<byte> _buf = new List<byte>();
        private const int HeaderSize = 2;
        private const int PayloadSize = 4 * 8 * 2; // NUM_CHANNELS * BUFFER_LEN * 2
        private const int PacketSize = HeaderSize + PayloadSize;

        private long _parsedPacketCount = 0;
        private long _invalidPacketCount = 0;
        private long _trimmedBytesCount = 0;

        public void Feed(byte[] data)
        {
            if (data == null || data.Length == 0) return;
            _buf.AddRange(data);

            // Check for ASCII debug lines (simple heuristic)
            //int newlineIdx = _buf.FindIndex(b => b == (byte)'\n');
            //while (newlineIdx >= 0)
            //{
            //    var lineBytes = _buf.GetRange(0, newlineIdx + 1).ToArray();
            //    string line = System.Text.Encoding.ASCII.GetString(lineBytes).TrimEnd('\r', '\n');
            //    // If line contains printable characters and no binary header, treat as debug
            //    if (line.Length > 0 && !line.Contains("\0"))
            //    {
            //        if (Verbose)
            //        {
            //            DebugLine?.Invoke(line);
            //        }
            //    }
            //    _buf.RemoveRange(0, newlineIdx + 1);
            //    newlineIdx = _buf.FindIndex(b => b == (byte)'\n');
            //}

            // Binary packet parsing: look for sentinel 0x55 0xAA
            int i = 0;
            while (i + PacketSize <= _buf.Count)
            {
                if (_buf[i] == 0x55 && _buf[i + 1] == 0xAA)
                {
                    byte[] payload = _buf.GetRange(i + HeaderSize, PayloadSize).ToArray();
                    var pkt = ParsePayload(payload);
                    if (pkt != null)
                    {
                        System.Threading.Interlocked.Increment(ref _parsedPacketCount);
                        PacketParsed?.Invoke(pkt);
                        _buf.RemoveRange(i, PacketSize);
                        i = 0; // restart
                        continue;
                    }
                    else
                    {
                        // malformed -> drop sentinel and continue
                        System.Threading.Interlocked.Increment(ref _invalidPacketCount);
                        _buf.RemoveAt(i);
                        continue;
                    }
                }
                else
                {
                    i++;
                }
            }

            // If buffer grows too large, trim front to avoid memory runaway
            const int MaxBuf = 64 * 1024;
            if (_buf.Count > MaxBuf)
            {
                int trimmed = _buf.Count - MaxBuf;
                _buf.RemoveRange(0, trimmed);
                System.Threading.Interlocked.Add(ref _trimmedBytesCount, trimmed);
            }
        }

        private Packet ParsePayload(byte[] payload)
        {
            if (payload == null || payload.Length != PayloadSize) return null;
            var pkt = new Packet();
            int off = 0;
            for (int ch = 0; ch < Packet.NumChannels; ch++)
            {
                for (int i = 0; i < Packet.BufferLen; i++)
                {
                    ushort v = (ushort)(payload[off++] | (payload[off++] << 8));
                    pkt.Samples[ch, i] = v;
                }
            }
            return pkt;
        }
    }
}