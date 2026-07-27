namespace NintendoProBridge;

internal static class SwitchRumble
{
    // Amplitude thresholds used by SDL's Nintendo Switch HID driver. Each index maps to
    // one HD Rumble amplitude code after FFXIV's 0-100 percentage has been scaled to 0-65535.
    private static ReadOnlySpan<ushort> AmplitudeThresholds =>
    [
        0, 514, 775, 921, 1096, 1303, 1550, 1843, 2192, 2606, 3100, 3686, 4383, 5213, 6199,
        7372, 7698, 8039, 8395, 8767, 9155, 9560, 9984, 10426, 10887, 11369, 11873, 12398,
        12947, 13520, 14119, 14744, 15067, 15397, 15734, 16079, 16431, 16790, 17158, 17534,
        17918, 18310, 18711, 19121, 19540, 19967, 20405, 20851, 21308, 21775, 22251, 22739,
        23236, 23745, 24265, 24797, 25340, 25894, 26462, 27041, 27633, 28238, 28856, 29488,
        30134, 30794, 31468, 32157, 32861, 33581, 34316, 35068, 35836, 36620, 37422, 38242,
        39079, 39935, 40809, 41703, 42616, 43549, 44503, 45477, 46473, 47491, 48531, 49593,
        50679, 51789, 52923, 54082, 55266, 56476, 57713, 58977, 60268, 61588, 62936, 64315,
        65535,
    ];

    public static void Encode(Span<byte> destination, ushort lowFrequency, ushort highFrequency)
    {
        if (destination.Length < 4) throw new ArgumentException("Four bytes are required.", nameof(destination));
        if (lowFrequency == 0 && highFrequency == 0)
        {
            SetNeutral(destination);
            return;
        }

        const ushort highFrequencyCode = 0x0074;
        const byte lowFrequencyCode = 0x3D;
        var highAmplitude = (byte)(FindAmplitudeIndex(highFrequency) * 2);
        var lowIndex = FindAmplitudeIndex(lowFrequency);
        var lowAmplitude = (ushort)((lowIndex % 2 == 0 ? 0 : 0x8000) | (0x40 + lowIndex / 2));

        destination[0] = (byte)(highFrequencyCode & 0xFF);
        destination[1] = (byte)(highAmplitude | ((highFrequencyCode >> 8) & 0x01));
        destination[2] = (byte)(lowFrequencyCode | ((lowAmplitude >> 8) & 0x80));
        destination[3] = (byte)(lowAmplitude & 0xFF);
    }

    public static void SetNeutral(Span<byte> destination)
    {
        if (destination.Length < 4) throw new ArgumentException("Four bytes are required.", nameof(destination));
        destination[0] = 0x00;
        destination[1] = 0x01;
        destination[2] = 0x40;
        destination[3] = 0x40;
    }

    private static int FindAmplitudeIndex(ushort amplitude)
    {
        var thresholds = AmplitudeThresholds;
        for (var index = 0; index < thresholds.Length; index++)
            if (amplitude <= thresholds[index]) return index;
        return thresholds.Length - 1;
    }
}
