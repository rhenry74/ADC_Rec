using System;

namespace ADC_Rec.Models.Filters
{
    public abstract class FilterBase : IFilter
    {
        public Guid Id { get; } = Guid.NewGuid();
        public abstract string Name { get; set; }
        public abstract FilterType Type { get; }
        public ChannelBinding Channels { get; set; } = ChannelBinding.None;
        public bool IsEnabled { get; set; } = true;

        public abstract float Process(float sample, ChannelBinding targetChannel);
    }
}
