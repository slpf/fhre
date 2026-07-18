namespace FH6RB.Services;

public static class BankSize
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int Align = 0x20;
    private const int SampleHeaderBytes = 80;
    private const double VbrHeadroom = 1.0;

    private static readonly (int Q, double Kbps)[] VorbisCurve =
    {
        (0, 48), (40, 113), (50, 130), (60, 161), (70, 195),
        (80, 238), (85, 268), (90, 306), (95, 417), (100, 479)
    };

    private static double VorbisBytesPerSec(int quality)
    {
        var q = Math.Clamp(quality, 0, 100);

        for (var i = 1; i < VorbisCurve.Length; i++)
        {
            if (q <= VorbisCurve[i].Q)
            {
                var (q0, k0) = VorbisCurve[i - 1];
                var (q1, k1) = VorbisCurve[i];
                var kbps = k0 + (k1 - k0) * (q - q0) / (q1 - q0);
                return kbps * 125.0 * VbrHeadroom;
            }
        }

        return VorbisCurve[^1].Kbps * 125.0 * VbrHeadroom;
    }

    public static long EncodedBytes(double seconds, int mode, int vorbisQuality)
    {
        if (seconds <= 0) return 0;

        long data = mode == 16
            ? (long) Math.Ceiling(seconds * SampleRate * Channels * 0.5)
            : (long) Math.Ceiling(seconds * VorbisBytesPerSec(vorbisQuality));

        data += (Align - (int)(data % Align)) % Align;
        return data + SampleHeaderBytes;
    }

    public static string Mb(long bytes) => $"{bytes / (1024.0 * 1024.0):0.0} MB";
}
