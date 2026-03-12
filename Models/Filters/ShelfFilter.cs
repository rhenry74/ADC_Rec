using System;
using ADC_Rec.Models.Filters;

namespace ADC_Rec.Models.Filters
{
    public class ShelfFilter : FilterBase
    {
        private readonly bool _isLow;
        public override string Name => _isLow ? "Low Shelf" : "High Shelf";
        public override FilterType Type => _isLow ? FilterType.LowShelf : FilterType.HighShelf;

        public float Frequency { get; set; } = 1000f;
        public float GainDb { get; set; } = 0f;
        public float Slope { get; set; } = 1.0f;

        public ShelfFilter(bool isLow)
        {
            _isLow = isLow;
        }

        public override void Process(float[] buffer, int offset, int count, ChannelBinding targetChannel)
        {
            if (!IsEnabled) return;
            if ((Channels & targetChannel) == 0) return;
            
            // TODO: Efficient shelf filter implementation (no new allocations)
        }
    }
}
