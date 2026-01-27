using System;

namespace ADC_Rec
{
    internal static class PlotUtils
    {
        public static uint ReverseBytes24(uint v)
        {
            byte b0 = (byte)(v & 0xFF);
            byte b1 = (byte)((v >> 8) & 0xFF);
            byte b2 = (byte)((v >> 16) & 0xFF);
            return (uint)((b0 << 16) | (b1 << 8) | b2);
        }

        public static int ClampBits(int bits) => Math.Max(8, Math.Min(24, bits));
    }
}