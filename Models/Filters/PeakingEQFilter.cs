using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ADC_Rec.Models.Filters;

namespace ADC_Rec.Models.Filters
{
    public class PeakingEQFilter : FilterBase, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public override string Name { get; set; } = "Peaking EQ";
        public override FilterType Type => FilterType.PeakingEQ;
        
        private float _frequency = 1000f;
        public float Frequency 
        { 
            get => _frequency; 
            set { _frequency = value; OnPropertyChanged(); } 
        }

        private float _gainDb = 0f;
        public float GainDb 
        { 
            get => _gainDb; 
            set { _gainDb = value; OnPropertyChanged(); } 
        }

        private float _q = 1.0f;
        public float Q 
        { 
            get => _q; 
            set { _q = value; OnPropertyChanged(); } 
        }

        private float _b0, _b1, _b2, _a0, _a1, _a2;
        private float _x1, _x2, _y1, _y2;

        private void UpdateCoefficients()
        {
            float fs = 44100f;
            float A = (float)Math.Pow(10, GainDb / 40);
            float w0 = 2f * (float)Math.PI * Frequency / fs;
            float cosW0 = (float)Math.Cos(w0);
            float sinW0 = (float)Math.Sin(w0);
            float alpha = sinW0 / (2f * Q);

            float a0 = 1f + alpha / A;
            _b0 = (1f + alpha * A) / a0;
            _b1 = (-2f * cosW0) / a0;
            _b2 = (1f - alpha * A) / a0;
            _a1 = (-2f * cosW0) / a0;
            _a2 = (1f - alpha / A) / a0;
        }

        private Dictionary<ChannelBinding, float[]> _state = new Dictionary<ChannelBinding, float[]>();

        public override float Process(float sample, ChannelBinding targetChannel)
        {
            if (!IsEnabled || (Channels & targetChannel) == 0) return sample;

            if (!_state.TryGetValue(targetChannel, out float[] state))
            {
                state = new float[4]; // x1, x2, y1, y2
                _state[targetChannel] = state;
            }

            UpdateCoefficients();

            float output = (_b0 * sample) + (_b1 * state[0]) + (_b2 * state[1]) - (_a1 * state[2]) - (_a2 * state[3]);

            state[1] = state[0];
            state[0] = sample;
            state[3] = state[2];
            state[2] = output;

            return output;
        }

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
