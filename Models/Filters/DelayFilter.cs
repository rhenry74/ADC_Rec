using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ADC_Rec.Models.Filters;
using System.Collections.Generic;

namespace ADC_Rec.Models.Filters
{
    public class DelayFilter : FilterBase, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public override string Name { get; set; } = "Delay";
        public override FilterType Type => FilterType.Delay;

        private float _delayTimeMs = 250f;
        public float DelayTimeMs
        {
            get => _delayTimeMs;
            set { _delayTimeMs = value; OnPropertyChanged(); }
        }

        private float _feedback = 0.3f;
        public float Feedback
        {
            get => _feedback;
            set { _feedback = value; OnPropertyChanged(); }
        }

        private float _wetLevel = 0.3f;
        public float WetLevel
        {
            get => _wetLevel;
            set { _wetLevel = value; OnPropertyChanged(); }
        }

        private Dictionary<ChannelBinding, DelayLine> _delayLines = new Dictionary<ChannelBinding, DelayLine>();

        public override float Process(float sample, ChannelBinding targetChannel)
        {
            if (!IsEnabled || (Channels & targetChannel) == 0) return sample;

            if (!_delayLines.TryGetValue(targetChannel, out var delayLine))
            {
                // Max delay 2 seconds
                delayLine = new DelayLine(44100 * 2);
                _delayLines[targetChannel] = delayLine;
            }

            float delaySamples = (DelayTimeMs / 1000f) * 44100f;
            float delayedSample = delayLine.Process(sample, Feedback, delaySamples);

            return sample * (1f - WetLevel) + delayedSample * WetLevel;
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

            public float Process(float input, float feedback, float delaySamples)
            {
                int delayInt = (int)delaySamples;
                int readIndex = (_index - delayInt + _buffer.Length) % _buffer.Length;
                
                float output = _buffer[readIndex];
                _buffer[_index] = input + (output * feedback);
                _index = (_index + 1) % _buffer.Length;
                
                return output;
            }
        }
    }
}
