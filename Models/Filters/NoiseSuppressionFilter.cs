using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ADC_Rec.Models.Filters;
using System.Collections.Generic;

namespace ADC_Rec.Models.Filters
{
    public class NoiseSuppressionFilter : FilterBase, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public override string Name { get; set; } = "Noise Suppression";
        public override FilterType Type => FilterType.NoiseSuppression;

        private float _thresholdDb = -60f;
        public float ThresholdDb 
        { 
            get => _thresholdDb; 
            set { _thresholdDb = value; OnPropertyChanged(); } 
        }

        private float _reductionDb = 20f;
        public float ReductionDb 
        { 
            get => _reductionDb; 
            set { _reductionDb = value; OnPropertyChanged(); } 
        }

        private float _attackMs = 10f;
        public float AttackMs 
        { 
            get => _attackMs; 
            set { _attackMs = value; OnPropertyChanged(); } 
        }

        private float _releaseMs = 100f;
        public float ReleaseMs 
        { 
            get => _releaseMs; 
            set { _releaseMs = value; OnPropertyChanged(); } 
        }

        private Dictionary<ChannelBinding, float> _envelope = new Dictionary<ChannelBinding, float>();

        public override float Process(float sample, ChannelBinding targetChannel)
        {
            if (!IsEnabled || (Channels & targetChannel) == 0) return sample;
            
            float absSample = Math.Abs(sample);
            float threshold = (float)Math.Pow(10, ThresholdDb / 20);

            if (!_envelope.TryGetValue(targetChannel, out float envelope))
                envelope = absSample;

            // Attack/Release constants
            float fs = 44100f;
            float attackCoef = (float)Math.Exp(-1.0 / (AttackMs / 1000.0 * fs));
            float releaseCoef = (float)Math.Exp(-1.0 / (ReleaseMs / 1000.0 * fs));

            // Envelope follower
            if (absSample > envelope)
                envelope = attackCoef * envelope + (1f - attackCoef) * absSample;
            else
                envelope = releaseCoef * envelope + (1f - releaseCoef) * absSample;

            _envelope[targetChannel] = envelope;

            // Simple noise gate
            if (envelope < threshold)
            {
                float reduction = (float)Math.Pow(10, -ReductionDb / 20);
                return sample * reduction;
            }

            return sample;
        }

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
