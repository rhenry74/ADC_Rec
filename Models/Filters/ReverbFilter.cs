using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ADC_Rec.Models.Filters;
using System.Collections.Generic;

namespace ADC_Rec.Models.Filters
{
    public class ReverbFilter : FilterBase, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public override string Name { get; set; } = "Reverb";
        public override FilterType Type => FilterType.Reverb;

        private float _roomSize = 0.5f;
        public float RoomSize 
        { 
            get => _roomSize; 
            set { _roomSize = value; OnPropertyChanged(); } 
        }

        private float _damping = 0.5f;
        public float Damping 
        { 
            get => _damping; 
            set { _damping = value; OnPropertyChanged(); } 
        }

        private float _wetLevel = 0.3f;
        public float WetLevel 
        { 
            get => _wetLevel; 
            set { _wetLevel = value; OnPropertyChanged(); } 
        }

        // Simple Comb filter bank for reverb simulation
        private readonly List<DelayLine> _delayLines = new List<DelayLine>();

        public ReverbFilter()
        {
            // Initialize basic delay lines (lengths in samples at 44.1kHz)
            _delayLines.Add(new DelayLine(1116));
            _delayLines.Add(new DelayLine(1188));
            _delayLines.Add(new DelayLine(1277));
            _delayLines.Add(new DelayLine(1356));
        }

        public override float Process(float sample, ChannelBinding targetChannel)
        {
            if (!IsEnabled || (Channels & targetChannel) == 0) return sample;

            float wet = 0f;
            foreach (var delayLine in _delayLines)
            {
                wet += delayLine.Process(sample, RoomSize, Damping);
            }

            return sample * (1f - WetLevel) + (wet / _delayLines.Count) * WetLevel;
        }

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private class DelayLine
        {
            private float[] _buffer;
            private int _index = 0;
            private float _prevOut = 0;

            public DelayLine(int length)
            {
                _buffer = new float[length];
            }

            public float Process(float input, float feedback, float damping)
            {
                float output = _buffer[_index];
                _prevOut = output * (1f - damping) + _prevOut * damping;
                _buffer[_index] = input + _prevOut * feedback;
                _index = (_index + 1) % _buffer.Length;
                return output;
            }
        }
    }
}
