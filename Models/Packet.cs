namespace ADC_Rec.Models
{
    public class Packet
    {
        public const int NumChannels = 4;
        public const int BufferLen = 16;

        // Samples[ channel, sampleIndex ] where sampleIndex in [0..BufferLen-1]
        public ushort[,] Samples = new ushort[NumChannels, BufferLen];
    }
}
