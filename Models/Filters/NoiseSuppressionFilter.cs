using System;
using ADC_Rec.Models.Filters;

namespace ADC_Rec.Models.Filters
{
    public class NoiseSuppressionFilter : FilterBase
    {
        public override string Name => "Noise Suppression";
        public override FilterType Type => FilterType.NoiseSuppression;

        public float ThresholdDb { get; set; } = -60f;
        public float ReductionDb { get; set; } = 20f;
        public float AttackMs { get; set; } = 10f;
        public float ReleaseMs { get; set; } = 100f;

        public override void Process(float[] buffer, int offset, int count, ChannelBinding targetChannel)
        {
            if (!IsEnabled) return;
            if ((Channels & targetChannel) == 0) return;
            
            // TODO: Efficient noise gate/suppression implementation (no new allocations)
        }
    }
}
