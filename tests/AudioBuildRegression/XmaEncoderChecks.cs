using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using FH6RB.Services;

internal static class XmaEncoderChecks
{
    public static async Task RunAsync(string temp)
    {
        CheckCodecSelection();
        CheckRejectedProbeAudio(temp);
        Check(!Tools.MissingRequired().Any(IsOptionalEncoder) && !Tools.MissingForBuild().Any(IsOptionalEncoder),
            "The optional XMA encoder became a required application or build dependency.");
        if (File.Exists(Tools.XmaEncoderPath))
        {
            Console.WriteLine("Verified XMA codec-selection policy; missing-encoder checks skipped because an external DLL is present. XMA roundtrip was not tested.");
            return;
        }
        var log = new List<string>();
        var result = await XmaEncoderService.CheckAsync(log.Add);
        Check(!result.Available && !XmaEncoderService.IsAvailable && !string.IsNullOrWhiteSpace(result.Reason),
            "A missing external encoder was reported as verified or omitted its failure reason.");
        Check(!log.Any(IsProcessLog), "Checking a missing encoder launched a probe process.");
        var repeated = new List<string>();
        var second = await XmaEncoderService.CheckAsync(repeated.Add);
        Check(!second.Available && !repeated.Any(IsProcessLog), "Repeated missing-encoder checks launched a probe or changed capability.");
        await CheckZeroExitFailureAsync(temp);
        Console.WriteLine("Verified optional/missing XMA capability, whole-bank codec selection, encoding quality arguments and real FSBank errors despite exit code zero. XMA roundtrip was not tested.");
    }

    private static void CheckCodecSelection()
    {
        var targetMode = Method("TargetMode");
        foreach (var test in new (int Source, bool Empty, bool Available, int Expected)[]
        {
            (10, false, false, 15), (10, false, true, 10),
            (0, true, false, 15), (0, true, true, 15),
            (10, true, false, 15), (10, true, true, 15),
            (15, false, false, 15), (15, false, true, 15),
            (16, false, false, 16), (16, false, true, 16),
        })
        {
            var actual = (int) (targetMode.Invoke(null, [test.Source, test.Empty, test.Available])
                ?? throw new InvalidOperationException("Codec selection returned no mode."));
            Check(actual == test.Expected,
                $"Unexpected codec selection: source={test.Source}, empty={test.Empty}, available={test.Available}, output={actual}.");
        }
        var format = Method("FormatArgs");
        foreach (var quality in new[] { 15, 95 })
        {
            var xma = (string) format.Invoke(null, [10, quality])!;
            var vorbis = (string) format.Invoke(null, [15, quality])!;
            Check(xma.Contains("-format xma", StringComparison.Ordinal) && xma.Contains($"-quality {quality}", StringComparison.Ordinal)
                && vorbis.Contains("-format vorbis", StringComparison.Ordinal) && vorbis.Contains($"-quality {quality}", StringComparison.Ordinal),
                "Codec selection lost the user quality setting or used the wrong FSBank format.");
        }
        Check(((string) format.Invoke(null, [16, 95])!).Contains("-format fadpcm", StringComparison.Ordinal),
            "Adding optional XMA support changed the existing FADPCM format.");
    }

    private static async Task CheckZeroExitFailureAsync(string temp)
    {
        var source = Path.Combine(temp, "missing-xma-encoder.wav");
        var output = Path.Combine(temp, "missing-xma-encoder.fsb");
        var cache = Path.Combine(temp, "missing-xma-encoder-cache");
        Directory.CreateDirectory(cache);
        var generated = Proc.Run(Tools.FfmpegPath,
            $"-y -hide_banner -loglevel error -f lavfi -i sine=frequency=997:sample_rate=48000:duration=2 -af atrim=end_sample=65536 -ac 2 -c:a pcm_s16le \"{source}\"");
        Check(generated.ExitCode == 0, "The missing-encoder fixture could not be generated: " + generated.StdErr);
        var log = new List<string>();
        var failure = "";
        var arguments = $"-format xma -quality 50 -o \"{output}\" \"{source}\" -cache_dir \"{cache}\"";
        try
        {
            var task = (Task<string>) (Method("RunAsync").Invoke(null, [Tools.FsbankclPath, arguments, (Action<string>) log.Add])
                ?? throw new InvalidOperationException("The process runner returned no task."));
            await task;
        }
        catch (InvalidOperationException ex) { failure = ex.Message; }
        var exit = log.Single(line => line.Contains("process.exit ", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(exit[(exit.IndexOf("process.exit ", StringComparison.Ordinal) + "process.exit ".Length)..]);
        Check(document.RootElement.GetProperty("ExitCode").GetInt32() == 0 && !File.Exists(output)
            && failure.Contains("encoder", StringComparison.OrdinalIgnoreCase)
            && log.Any(line => line.Contains("ERROR:", StringComparison.OrdinalIgnoreCase)),
            "FSBank's missing-encoder error was accepted because its process returned exit code zero.");
    }

    private static void CheckRejectedProbeAudio(string temp)
    {
        var validate = typeof(XmaEncoderService).GetMethod("ValidateProbe", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(XmaEncoderService), "ValidateProbe");
        foreach (var frames in new[] { 65536, 65537 })
        {
            var source = Path.Combine(temp, $"xma-validation-source-{frames}.wav");
            NormalizationChecks.WriteSignal(source, 48000, 2, frames / 48000.0, limited: false, transients: false);
            var fsb = Path.Combine(temp, $"xma-metadata-only-{frames}.fsb");
            var bytes = new byte[100];
            "FSB5"u8.CopyTo(bytes);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 8);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 32);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 10);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(60), 9UL << 1 | 1UL << 5 | (ulong) frames << 34);
            File.WriteAllBytes(fsb, bytes);
            foreach (var (name, rate, channels, outputFrames, amplitude, silent, expectedError) in new[]
            {
                ("frames", 48000, 2, frames - 1, 1d, false, "frame count"),
                ("rate", 44100, 2, frames, 1d, false, "sample rate"),
                ("channels", 48000, 1, frames, 1d, false, "channels"),
                ("level", 48000, 2, frames, 0.1, false, "signal or level"),
                ("inversion", 48000, 2, frames, -1d, false, "signal or level"),
                ("silence", 48000, 2, frames, 1d, true, "signal or level"),
            })
            {
                var decoded = Path.Combine(temp, $"xma-validation-{name}-{frames}.wav");
                NormalizationChecks.WriteSignal(decoded, rate, channels, outputFrames / (double) rate,
                    limited: false, transients: false, silence: silent, amplitude: amplitude);
                RejectProbe(validate, source, fsb, decoded, expectedError);
            }
            var padded = (byte[]) bytes.Clone();
            BinaryPrimitives.WriteUInt64LittleEndian(padded.AsSpan(60), 9UL << 1 | 1UL << 5 | (ulong) (frames + 256) << 34);
            var paddedPath = Path.Combine(temp, $"xma-validation-padded-{frames}.fsb");
            File.WriteAllBytes(paddedPath, padded);
            RejectProbe(validate, source, paddedPath, source, "padding or stretching");
            var wrongCodec = (byte[]) bytes.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(wrongCodec.AsSpan(24), 15);
            var codecPath = Path.Combine(temp, $"xma-validation-wrong-codec-{frames}.fsb");
            File.WriteAllBytes(codecPath, wrongCodec);
            RejectProbe(validate, source, codecPath, source, "mode 10");
            var nonfinite = Path.Combine(temp, $"xma-validation-nonfinite-{frames}.wav");
            File.Copy(source, nonfinite);
            using (var file = new FileStream(nonfinite, FileMode.Open, FileAccess.Write))
            {
                file.Position = 44;
                var sample = new byte[4];
                BinaryPrimitives.WriteSingleLittleEndian(sample, float.NaN);
                file.Write(sample);
            }
            RejectProbe(validate, source, fsb, nonfinite, "non-finite");
        }
    }

    private static void RejectProbe(MethodInfo method, string source, string fsb, string decoded, string expectedError)
    {
        var rejected = false;
        try { method.Invoke(null, [source, fsb, decoded, null]); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException failure)
        {
            rejected = failure.Message.Contains(expectedError, StringComparison.OrdinalIgnoreCase);
        }
        Check(rejected, "Invalid probe audio or metadata was accepted, or failed for an unrelated reason: " + expectedError);
    }

    private static MethodInfo Method(string name) => typeof(BankBuildService)
        .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new MissingMethodException(nameof(BankBuildService), name);

    private static bool IsOptionalEncoder(string value)
        => value.Contains("xma", StringComparison.OrdinalIgnoreCase);

    private static bool IsProcessLog(string value)
        => value.Contains("$ ", StringComparison.Ordinal) || value.Contains("process.start", StringComparison.OrdinalIgnoreCase)
            || value.Contains("process.exit", StringComparison.OrdinalIgnoreCase)
            || value.Contains("xma.probe.command", StringComparison.Ordinal) || value.Contains("xma.probe.start", StringComparison.Ordinal);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
