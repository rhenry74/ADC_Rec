using System;

namespace ADC_Rec.Models.Filters
{
    public enum FilterType
    {
        PeakingEQ,
        LowShelf,
        HighShelf,
        Compressor,
        NoiseSuppression,
        Reverb,
        Delay,
        Phase
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
        string Name { get; set; }
        FilterType Type { get; }
        ChannelBinding Channels { get; set; }
        bool IsEnabled { get; set; }
        
        /// <summary>
        /// Processes a single audio sample for a specific channel.
        /// </summary>
        /// <param name="sample">The input audio sample.</param>
        /// <param name="targetChannel">The channel binding the sample belongs to, used to maintain internal state per channel.</param>
        /// <returns>The processed audio sample.</returns>
        float Process(float sample, ChannelBinding targetChannel);
    }
}
