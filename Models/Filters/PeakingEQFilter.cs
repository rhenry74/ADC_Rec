using System;
using ADC_Rec.Models.Filters;

namespace ADC_Rec.Models.Filters
{
    public class PeakingEQFilter : FilterBase
    {
        public override string Name => "Peaking EQ";
        public override FilterType Type => FilterType.PeakingEQ;
        
        public float Frequency { get; set; } = 1000f;
        public float GainDb { get; set; } = 0f;
        public float Q { get; set; } = 1.0f;

        public override void Process(float[] buffer, int offset, int count, ChannelBinding targetChannel)
        {
            if (!IsEnabled) return;
            if ((Channels & targetChannel) == 0) return;
            
            // TODO: Efficient IIR filter implementation (no new allocations)
        }
    }
}
