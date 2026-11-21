using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using FH6RB.Core;
using FH6RB.Services;

internal static class AudioPipelineChecks
{
    public static async Task RunAsync(string root, Action<string> writeEmptyBank)
    {
        var temp = Path.Combine(root, "audio-pipeline");
        Directory.CreateDirectory(temp);
        string PathFor(string name) => Path.Combine(temp, name);
        var template = PathFor("empty.bank");
        writeEmptyBank(template);
        var tone = PathFor("tone.wav");
        WriteTone(tone, 1);
        var manual = new AppSettings { LoudnessNormalize = false, EncodeParallelism = 1 };
        var output = PathFor("output.bank");
        await BankBuildService.BuildToFileAsync(template, output, [Item(501, tone)], manual);

        var stream = TrackPack.ExtractSampleFsb(output, 0);
        var packetOffset = 60 + BinaryPrimitives.ReadInt32LittleEndian(stream.AsSpan(12))
            + BinaryPrimitives.ReadInt32LittleEndian(stream.AsSpan(16));
        for (var i = 0; i < 28; i++)
            packetOffset += 2 + BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(packetOffset));
        Array.Clear(stream, packetOffset, stream.Length - packetOffset);
        var truncated = PathFor("truncated.fsb");
        File.WriteAllBytes(truncated, stream);
        var previous = File.ReadAllBytes(output);
        try
        {
            await BankBuildService.BuildToFileAsync(template, output, [Item(502, truncated)], manual);
            throw new Exception("Truncated Vorbis passed validation.");
        }
        catch (InvalidOperationException ex) when (ex.InnerException is InvalidDataException) { }
        Check(File.ReadAllBytes(output).AsSpan().SequenceEqual(previous), "Damaged audio replaced a valid output.");

        var invalid = PathFor("invalid.wav");
        File.WriteAllText(invalid, "Invalid audio");
        try { MetadataReader.Read(invalid); throw new Exception("Invalid audio passed metadata reading."); }
        catch (InvalidDataException) { }
        var m4a = PathFor("two-streams.m4a");
        Run(Tools.FfmpegPath, "-hide_banner -loglevel error -f lavfi -i sine=frequency=440:duration=2 " +
            $"-f lavfi -i sine=frequency=220:duration=8 -map 0:a -map 1:a -c:a aac \"{m4a}\"");
        Check(Math.Abs(MetadataReader.Read(m4a).Duration - 2) < 0.05, "Metadata used the duration of another audio stream.");

        var log = new ConcurrentQueue<string>();
        try
        {
            await BankBuildService.BuildToFileAsync(template, output, [Item(503, invalid), Item(504, tone)], manual, log.Enqueue);
            throw new Exception("The invalid track did not fail the build.");
        }
        catch (InvalidOperationException ex) when (ex.InnerException is InvalidOperationException) { }
        Check(!log.Any(line => line.Contains("[track=1 ", StringComparison.Ordinal)
            && line.Contains("process.exit", StringComparison.Ordinal)), "A later track ran after the only worker failed.");

        using var cancellation = new CancellationTokenSource();
        try
        {
            await BankBuildService.BuildToFileAsync(template, output, [Item(505, tone)], manual, progress: progress =>
            {
                if (progress.StartsWith("Assembling", StringComparison.Ordinal)) cancellation.Cancel();
            }, ct: cancellation.Token);
            throw new Exception("Build ignored cancellation before publication.");
        }
        catch (OperationCanceledException) { }
        Check(File.ReadAllBytes(output).AsSpan().SequenceEqual(previous)
            && !Directory.EnumerateFiles(temp, ".fhre-*.bank").Any(), "Failure or cancellation left a published or staged bank.");

        using var cancelProcess = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            Proc.Run(Tools.FfmpegPath, "-hide_banner -loglevel error -re -f lavfi -i sine=duration=20 -f null -",
                cancelProcess.Token);
            throw new Exception("The running audio process ignored cancellation.");
        }
        catch (OperationCanceledException) { }

        var settings = new AppSettings { LoudnessNormalize = true, TargetLufs = -23 };
        var preview = AudioDecoder.DecodeAdded(tone, settings);
        var stamp = File.GetLastWriteTimeUtc(tone);
        var length = new FileInfo(tone).Length;
        var quiet = PathFor("quiet.wav");
        WriteTone(quiet, 0.1);
        File.Copy(quiet, tone, overwrite: true);
        File.SetLastWriteTimeUtc(tone, stamp);
        Check(new FileInfo(tone).Length == length && File.GetLastWriteTimeUtc(tone) == stamp, "Fixture changed its file stamp.");
        Check(AudioDecoder.DecodeAdded(tone, settings) != preview, "Preview cached audio from the previous source.");
        var rendered = PathFor("corrected.wav");
        await Loudnorm.RenderAsync(tone, rendered, settings);
        var measured = await Loudnorm.MeasureOutputAsync(rendered);
        Check(measured.IntegratedLufs is { } integrated && Math.Abs(integrated + 23) < 0.2,
            "Normalization reused a measurement from the previous source.");

        var surround = PathFor("surround.wav");
        Run(Tools.FfmpegPath, "-hide_banner -loglevel error -f lavfi -i " +
            $"\"aevalsrc=0|0|0|0.2*sin(2*PI*80*t)|0|0:s=44100:d=2:c=5.1\" -c:a pcm_s16le \"{surround}\"");
        var cache = Directory.CreateDirectory(PathFor("cache")).FullName;
        var encoded = PathFor("surround.fsb");
        Run(Tools.FsbankclPath, $"-format fadpcm -o \"{encoded}\" \"{surround}\" -cache_dir \"{cache}\"");
        await BankBuildService.BuildToFileAsync(template, output, [Item(506, encoded)], manual);
        var converted = PathFor("converted.fsb");
        var decoded = PathFor("converted.wav");
        File.WriteAllBytes(converted, TrackPack.ExtractSampleFsb(output, 0));
        Run(Tools.VgmstreamPath, $"-i -W 4 -s 1 -o \"{decoded}\" \"{converted}\"");
        var pcm = AudioBuildValidation.Inspect(decoded);
        Check(pcm.SampleRate == 44100 && pcm.Channels == 6 && pcm.Frames == Fsb5.Parse(File.ReadAllBytes(encoded)).Samples[0].Frames
            && pcm.PeakDb[3] > -20, "Cross-codec import lost its native dimensions or the LFE signal.");
        Console.WriteLine("Verified Vorbis truncation rejection, native surround conversion, stream metadata, source identity and build cancellation.");
    }

    private static BuildItem Item(int id, string source) => new(Naming.MakeSoundName(id), true, source, null, null, null, true);

    private static void WriteTone(string output, double gain) => Run(Tools.FfmpegPath,
        "-hide_banner -loglevel error -f lavfi -i sine=frequency=503:sample_rate=44100:duration=4 " +
        $"-af volume={gain.ToString(CultureInfo.InvariantCulture)} -c:a pcm_f32le \"{output}\"");

    private static void Run(string exe, string args)
    {
        var result = Proc.Run(exe, args);
        Check(result.ExitCode == 0, result.StdErr + result.StdOut);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
