using System;
using ADC_Rec.Models.Filters;

namespace ADC_Rec.Models.Filters
{
    public class CompressorFilter : FilterBase
    {
        public override string Name => "Compressor";
        public override FilterType Type => FilterType.Compressor;

        public float ThresholdDb { get; set; } = -20f;
        public float Ratio { get; set; } = 4f;
        public float AttackMs { get; set; } = 10f;
        public float ReleaseMs { get; set; } = 100f;
        public float MakeupGainDb { get; set; } = 0f;

        public override void Process(float[] buffer, int offset, int count, ChannelBinding targetChannel)
        {
            if (!IsEnabled) return;
            if ((Channels & targetChannel) == 0) return;
            
            // TODO: Efficient compressor implementation (no new allocations)
        }
    }
}
