namespace NintendoProBridge;

internal static class Switch2Rumble
{
    private const ushort HighFrequency = 0x0187;
    private const ushort LowFrequency = 0x0112;

    public static void Encode(Span<byte> report, byte sequence, ushort highAmplitude, ushort lowAmplitude)
    {
        if (report.Length < 64)
            throw new ArgumentException("A 64-byte Switch 2 output report is required.", nameof(report));

        report.Clear();
        report[0] = 0x02;
        report[1] = (byte)(0x50 | (sequence & 0x0F));
        report[2] = (byte)(HighFrequency & 0xFF);
        report[3] = (byte)(((highAmplitude >> 4) & 0xFC) | ((HighFrequency >> 8) & 0x03));
        report[4] = (byte)((highAmplitude >> 12) | (LowFrequency << 4));
        report[5] = (byte)((lowAmplitude & 0xC0) | ((LowFrequency >> 4) & 0x3F));
        report[6] = (byte)(lowAmplitude >> 8);
        report.Slice(1, 6).CopyTo(report.Slice(0x11, 6));
    }
}
