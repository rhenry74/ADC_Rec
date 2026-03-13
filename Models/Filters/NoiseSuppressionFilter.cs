using System;
using ADC_Rec.Models.Filters;

namespace ADC_Rec.Models.Filters
{
    public class NoiseSuppressionFilter : FilterBase
    {
        public override string Name { get; set; } = "Noise Suppression";
        public override FilterType Type => FilterType.NoiseSuppression;

        public float ThresholdDb { get; set; } = -60f;
        public float ReductionDb { get; set; } = 20f;
        public float AttackMs { get; set; } = 10f;
        public float ReleaseMs { get; set; } = 100f;

        public override float Process(float sample, ChannelBinding targetChannel)
        {
            if (!IsEnabled || (Channels & targetChannel) == 0) return sample;
            
            // TODO: Efficient noise gate/suppression implementation (no new allocations)
            return sample;
        }
    }
}
