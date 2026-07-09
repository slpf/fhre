namespace FH6RB.Services;

public static class BankSize
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int Align = 0x20;
    private const int SampleHeaderBytes = 80;
    private const double VorbisQ70BytesPerSec = 17000.0;

    public static long EncodedBytes(double seconds, int mode)
    {
        if (seconds <= 0) return 0;

        long data = mode == 16
            ? (long) Math.Ceiling(seconds * SampleRate * Channels * 0.5)
            : (long) Math.Ceiling(seconds * VorbisQ70BytesPerSec);

        data += (Align - (int)(data % Align)) % Align;
        return data + SampleHeaderBytes;
    }

    public static string Mb(long bytes) => $"{bytes / (1024.0 * 1024.0):0.0} MB";
}
