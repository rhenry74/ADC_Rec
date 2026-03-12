using System;

namespace ADC_Rec.Models.Filters
{
    public abstract class FilterBase : IFilter
    {
        public Guid Id { get; } = Guid.NewGuid();
        public abstract string Name { get; }
        public abstract FilterType Type { get; }
        public ChannelBinding Channels { get; set; } = ChannelBinding.None;
        public bool IsEnabled { get; set; } = true;

        public abstract void Process(float[] buffer, int offset, int count, ChannelBinding targetChannel);
    }
}
