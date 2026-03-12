using System;

namespace ADC_Rec.Models.Filters
{
    public enum FilterType
    {
        PeakingEQ,
        LowShelf,
        HighShelf,
        Compressor,
        NoiseSuppression
    }

    [Flags]
    public enum ChannelBinding
    {
        None = 0,
        CH0 = 1 << 0,
        CH1 = 1 << 1,
        CH2 = 1 << 2,
        CH3 = 1 << 3,
        L = 1 << 4,
        R = 1 << 5
    }

    public interface IFilter
    {
        Guid Id { get; }
        string Name { get; }
        FilterType Type { get; }
        ChannelBinding Channels { get; set; }
        bool IsEnabled { get; set; }
        
        // Audio processing - no allocation here
        void Process(float[] buffer, int offset, int count, ChannelBinding targetChannel);
    }
}
