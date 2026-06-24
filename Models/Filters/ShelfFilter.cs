using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ADC_Rec.Models.Filters;
using System.Collections.Generic;

namespace ADC_Rec.Models.Filters
{
    public class ShelfFilter : FilterBase, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private readonly bool _isLow;
        public override string Name { get; set; }
        public override FilterType Type => _isLow ? FilterType.LowShelf : FilterType.HighShelf;

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

        private float _slope = 1.0f;
        public float Slope 
        { 
            get => _slope; 
            set { _slope = value; OnPropertyChanged(); } 
        }

        public ShelfFilter(bool isLow)
        {
            _isLow = isLow;
            Name = _isLow ? "Low Shelf" : "High Shelf";
        }

        private float _b0, _b1, _b2, _a1, _a2;
        private float _x1, _x2, _y1, _y2;
        private Dictionary<ChannelBinding, float[]> _state = new Dictionary<ChannelBinding, float[]>();

        private void UpdateCoefficients()
        {
            float fs = 44100f;
            float A = (float)Math.Pow(10, GainDb / 40);
            float w0 = 2f * (float)Math.PI * Frequency / fs;
            float cosW0 = (float)Math.Cos(w0);
            float sinW0 = (float)Math.Sin(w0);
            float alpha = sinW0 / 2f * (float)Math.Sqrt((A + 1f / A) * (1f / Slope - 1f) + 2f);

            float a0, b0, b1, b2, a1, a2;
            float Aplus1 = A + 1f;
            float Aminus1 = A - 1f;

            if (_isLow)
            {
                float Aminus1cos = Aminus1 * cosW0;
                float Aplus1cos = Aplus1 * cosW0;
                float twoSqrtAAlpha = 2f * (float)Math.Sqrt(A) * alpha;
                a0 = Aplus1 + Aminus1cos + twoSqrtAAlpha;
                b0 = A * (Aplus1 - Aminus1cos + twoSqrtAAlpha);
                b1 = 2f * A * (Aminus1 - Aplus1cos);
                b2 = A * (Aplus1 - Aminus1cos - twoSqrtAAlpha);
                a1 = -2f * (Aminus1 + Aplus1cos);
                a2 = Aplus1 + Aminus1cos - twoSqrtAAlpha;
            }
            else
            {
                float Aminus1cos = Aminus1 * cosW0;
                float Aplus1cos = Aplus1 * cosW0;
                float twoSqrtAAlpha = 2f * (float)Math.Sqrt(A) * alpha;
                a0 = Aplus1 - Aminus1cos + twoSqrtAAlpha;
                b0 = A * (Aplus1 + Aminus1cos + twoSqrtAAlpha);
                b1 = -2f * A * (Aminus1 + Aplus1cos);
                b2 = A * (Aplus1 + Aminus1cos - twoSqrtAAlpha);
                a1 = 2f * (Aminus1 - Aplus1cos);
                a2 = Aplus1 - Aminus1cos - twoSqrtAAlpha;
            }

            _b0 = b0 / a0;
            _b1 = b1 / a0;
            _b2 = b2 / a0;
            _a1 = a1 / a0;
            _a2 = a2 / a0;
        }

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
