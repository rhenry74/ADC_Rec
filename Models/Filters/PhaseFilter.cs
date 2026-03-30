using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ADC_Rec.Models.Filters;
using System.Collections.Generic;

namespace ADC_Rec.Models.Filters
{
    public class PhaseFilter : FilterBase, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public override string Name { get; set; } = "Sync Delay";
        public override FilterType Type => FilterType.Phase;

        private float _delayTimeMs = 0f;
        public float DelayTimeMs
        {
            get => _delayTimeMs;
            set { _delayTimeMs = value; OnPropertyChanged(); }
        }

        private Dictionary<ChannelBinding, DelayLine> _delayLines = new Dictionary<ChannelBinding, DelayLine>();

        public override float Process(float sample, ChannelBinding targetChannel)
        {
            if (!IsEnabled || (Channels & targetChannel) == 0 || DelayTimeMs <= 0) return sample;

            if (!_delayLines.TryGetValue(targetChannel, out var delayLine))
            {
                // Max delay 1000ms
                delayLine = new DelayLine(44100);
                _delayLines[targetChannel] = delayLine;
            }

            float delaySamples = (DelayTimeMs / 1000f) * 44100f;
            return delayLine.Process(sample, delaySamples);
        }

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private class DelayLine
        {
            private float[] _buffer;
            private int _index = 0;

            public DelayLine(int maxSamples)
            {
                _buffer = new float[maxSamples];
            }

            public float Process(float input, float delaySamples)
            {
                int delayInt = Math.Max(0, (int)delaySamples);
                if (delayInt >= _buffer.Length) delayInt = _buffer.Length - 1;
                
                int readIndex = (_index - delayInt + _buffer.Length) % _buffer.Length;
                
                float output = _buffer[readIndex];
                _buffer[_index] = input;
                _index = (_index + 1) % _buffer.Length;
                
                return output;
            }
        }
    }
}
