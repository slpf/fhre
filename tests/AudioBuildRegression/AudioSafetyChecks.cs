using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using FH6RB.Core;
using FH6RB.Services;
using NAudio.Wave;

internal static class AudioSafetyChecks
{
    public static async Task RunAsync(string temp, Action<string, int> writeEmptyBank)
    {
        await CheckQuietAudioAsync(temp);
        await CheckTinyMeasurementsAsync(temp);
        await CheckEncodedSnapshotsAsync(temp, writeEmptyBank);
        CheckPreviewPublication(temp);
        Console.WriteLine("Verified quiet silence, short and quiet fallbacks, tiny native meters, unclipped pre-PCM protection, manual gain, encoded import snapshots and cross-codec headroom.");
    }

    private static async Task CheckQuietAudioAsync(string temp)
    {
        var settings = LimiterSettings(-23, -1);
        foreach (var (name, seconds, amplitude, gain) in new[]
        {
            ("silent-short", 0.1, 0d, 0d), ("silent-long", 2d, 0d, 0d),
            ("quiet", 2d, 0.0001, 0d), ("quiet-trim", 2d, 0.0001, 6d),
            ("audible-short", 0.1, 0.1, -3d),
        })
        {
            var source = Path.Combine(temp, $"final-{name}.wav");
            var reference = Path.Combine(temp, $"final-{name}-reference.wav");
            var output = Path.Combine(temp, $"final-{name}-output.wav");
            Run(Tools.FfmpegPath, "-y -hide_banner -loglevel error -f lavfi -i " +
                $"\"aevalsrc={Fmt(amplitude)}*sin(2*PI*997*t)|{Fmt(amplitude)}*sin(2*PI*1361*t):s=48000:d={Fmt(seconds)}\" -c:a pcm_f32le \"{source}\"");
            Run(Tools.FfmpegPath, $"-y -hide_banner -loglevel error -i \"{source}\" -af " +
                $"\"aresample=48000:osf=dbl:ochl=stereo:rematrix_maxval=1,volume={Fmt(gain)}dB:precision=double\" -c:a pcm_s16le \"{reference}\"");
            var log = new List<string>();
            await Loudnorm.RenderAsync(source, output, settings, gain, log.Add);
            var info = AudioBuildValidation.Inspect(output);
            Check(info.Frames == (long) (48000 * seconds) && info.Channels == 2,
                "An exceptional quiet source changed duration or channel count.");
            Check(MaxDifference(Read(reference), Read(output)) <= 1.01 / 32768,
                $"Quiet {name} audio was amplified, corrupted or ignored its manual trim.");
            Check(Events(log, "loudnorm.render.plan").Single().GetProperty("mode").GetString() == "constant",
                $"The {name} quiet fallback was not explicitly diagnosed.");
            if (amplitude == 0) Check(info.RmsDb.All(double.IsNegativeInfinity), "Quiet silence turned into nonzero full-scale PCM.");
        }
        foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var source = Path.Combine(temp, "final-nonfinite-" + BitConverter.SingleToInt32Bits(invalid) + ".wav");
            WriteConstant(source, 48000, 48000, invalid);
            var output = Path.Combine(temp, Path.GetFileNameWithoutExtension(source) + "-output.wav");
            var rejected = false;
            try { await Loudnorm.RenderAsync(source, output, settings); }
            catch (InvalidDataException) { rejected = true; }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected && !File.Exists(output), "Limiter processing silently converted non-finite input into valid PCM.");
        }
        var manual = Path.Combine(temp, "final-manual-source.wav");
        var manualOutput = Path.Combine(temp, "final-manual-output.wav");
        WriteConstant(manual, 48000, 48000, 0.75f);
        await Loudnorm.RenderAsync(manual, manualOutput, new AppSettings { LoudnessNormalize = false, PeakLimiter = true, TargetTruePeak = -9 }, 3);
        Check(AudioBuildValidation.Inspect(manualOutput).PeakDb.All(value => value > -4),
            "Disabling normalization still silently capped manual gain at the true peak setting.");
    }

    private static async Task CheckTinyMeasurementsAsync(string temp)
    {
        foreach (var frames in new[] { 1, 2, 10 })
        {
            var source = Path.Combine(temp, $"final-tiny-{frames}.wav");
            WriteConstant(source, 48000, frames, 0.75f);
            var measured = await Loudnorm.MeasureOutputAsync(source);
            Check(measured.Frames == frames && measured.TruePeakDbtp is { } peak && double.IsFinite(peak) && peak > -4,
                "A nonzero tiny source was reported as silence by the native output meter.");
            var output = Path.Combine(temp, $"final-tiny-{frames}-protected.wav");
            await Loudnorm.RenderAsync(source, output, new AppSettings { LoudnessNormalize = true, TargetTruePeak = -9 }, 30);
            var protectedOutput = await Loudnorm.MeasureOutputAsync(output);
            Check(protectedOutput.Frames == frames && protectedOutput.TruePeakDbtp is { } protectedPeak && protectedPeak <= -9,
                "The peak ceiling was bypassed for a nonzero tiny source.");
            var silence = Path.Combine(temp, $"final-tiny-{frames}-silence.wav");
            WriteConstant(silence, 48000, frames, 0);
            var silent = await Loudnorm.MeasureOutputAsync(silence);
            Check(silent.Frames == frames && silent.TruePeakDbtp is null && silent.IntegratedLufs is null,
                "A tiny silent source acquired fabricated loudness or lost its frame count.");
        }
        var tail = Path.Combine(temp, "final-tail-impulse.wav");
        WriteConstant(tail, 48000, 48000, 0);
        using (var file = new FileStream(tail, FileMode.Open, FileAccess.Write))
        {
            file.Position = 44 + 47999 * 4;
            Span<byte> sample = stackalloc byte[4];
            BinaryPrimitives.WriteSingleLittleEndian(sample, 0.75f);
            file.Write(sample);
        }
        var tailMeasurement = await Loudnorm.MeasureOutputAsync(tail);
        Check(tailMeasurement.Frames == 48000 && tailMeasurement.TruePeakDbtp is { } tailPeak && tailPeak > -4,
            "Native true peak analysis dropped a final nonzero sample or counted synthetic meter padding as audio.");
    }

    private static async Task CheckEncodedSnapshotsAsync(string temp, Action<string, int> writeEmptyBank)
    {
        var input = Path.Combine(temp, "final-encoded-source.wav");
        Run(Tools.FfmpegPath, "-y -hide_banner -loglevel error -f lavfi -i " +
            $"\"aevalsrc=0.99*sgn(sin(2*PI*7000*t))|0.99*sgn(sin(2*PI*7100*t)):s=48000:d=3\" -c:a pcm_s16le \"{input}\"");
        var vorbis = Encode(temp, input, "final-encoded-vorbis", "vorbis -quality 95");
        var original = File.ReadAllBytes(vorbis);
        var mutable = Path.Combine(temp, "final-mutable-import.fsb");
        File.WriteAllBytes(mutable, original);
        var expected = Fsb5.Parse(original).Samples.Single().Data;
        var empty = Path.Combine(temp, "final-snapshot-empty.bank");
        var output = Path.Combine(temp, "final-snapshot.bank");
        writeEmptyBank(empty, 12);
        var dataStart = 60 + (int) BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(12))
            + (int) BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(16));
        Check(BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(dataStart)) > 1,
            "The native FSB fixture has no mutable audio packet payload.");
        var mutated = false;
        void MutateValidatedImport(string line)
        {
            if (mutated || !line.Contains("fsb.validate.ok ", StringComparison.Ordinal)) return;
            using var file = new FileStream(mutable, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            file.Position = dataStart + 3;
            file.WriteByte((byte) (original[dataStart + 3] ^ 0x80));
            mutated = true;
        }
        await BankBuildService.BuildToFileAsync(empty, output,
            [new BuildItem(Naming.MakeSoundName(271), true, mutable, "Snapshot", null, 20, true)],
            LimiterSettings(-9, -9), MutateValidatedImport);
        Check(mutated && !File.ReadAllBytes(mutable).AsSpan().SequenceEqual(original)
            && Fsb5.Parse(new FevBank(File.ReadAllBytes(output)).ExtractFsb5()).Samples.Single().Data.AsSpan().SequenceEqual(expected),
            "An encoded import changed between validation and assembly instead of using its validated snapshot.");
        var decoded = Path.Combine(temp, "final-encoded-overshoot.wav");
        Run(Tools.VgmstreamPath, $"-i -W 4 -s 1 -o \"{decoded}\" \"{vorbis}\"");
        Check(AudioBuildValidation.Inspect(decoded).PeakDb.Max() > 1,
            "The encoded import fixture no longer overshoots the fixed-point range.");
        var fadpcm = Encode(temp, input, "final-encoded-fadpcm", "fadpcm");
        var targetEmpty = Path.Combine(temp, "final-crosscodec-empty.bank");
        writeEmptyBank(targetEmpty, 0);
        var target = Path.Combine(temp, "final-crosscodec-source.bank");
        File.WriteAllBytes(target, new FevBank(File.ReadAllBytes(targetEmpty)).FillEmpty(
            FevBank.BuildStbl([(Lookup.SoundNameToId(Naming.MakeSoundName(272)), 0)]), File.ReadAllBytes(fadpcm)));
        var converted = Path.Combine(temp, "final-crosscodec.bank");
        var log = new List<string>();
        await BankBuildService.BuildToFileAsync(target, converted,
            [new BuildItem(Naming.MakeSoundName(273), true, vorbis, "Headroom", null, 30, true,
                Normalization: new NormalizationOverride(true, -9, -9, true))], LimiterSettings(-9, -9), log.Add);
        var headroom = Events(log, "source.fsb.headroom").Single();
        var prepared = Events(log, "wav.prepared").Single();
        Check(headroom.GetProperty("inputSamplePeak").GetDouble() > 1
            && headroom.GetProperty("gainDb").GetDouble() < -1
            && prepared.GetProperty("PeakDb").EnumerateArray().All(value => value.GetDouble() < -0.1)
            && !log.Any(line => line.Contains("loudnorm.render.start ", StringComparison.Ordinal))
            && Events(log, "fsb.peak_correction").Count == 0 && FevBank.ReadSkeleton(converted).Mode == 16,
            "Cross-codec import clipped PCM16 or reapplied global loudness, gain or normalization.");
    }

    private static string Encode(string temp, string input, string name, string format)
    {
        var output = Path.Combine(temp, name + ".fsb");
        var cache = Path.Combine(temp, name + "-cache");
        Directory.CreateDirectory(cache);
        Run(Tools.FsbankclPath, $"-format {format} -o \"{output}\" \"{input}\" -cache_dir \"{cache}\"");
        Check(File.Exists(output), "The native encoded fixture did not produce an FSB.");
        return output;
    }

    private static void CheckPreviewPublication(string temp)
    {
        var publish = typeof(AudioDecoder).GetMethod("Publish", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(AudioDecoder), "Publish");
        var winner = Path.Combine(temp, "final-preview-winner.wav");
        var part = Path.Combine(temp, "final-preview-loser.part.wav");
        WriteConstant(winner, 48000, 4800, 0.25f);
        WriteConstant(part, 48000, 4800, 0.5f);
        var original = File.ReadAllBytes(winner);
        using (var reader = new FileStream(winner, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            publish.Invoke(null, [part, winner, "Final normalization regression", CancellationToken.None]);
            Check(!File.Exists(part) && File.ReadAllBytes(winner).AsSpan().SequenceEqual(original),
                "Publishing a concurrent preview overwrote or failed to reuse an actively read cache winner.");
        }
        var cancelledPart = Path.Combine(temp, "final-preview-cancelled.part.wav");
        var cancelledOutput = Path.Combine(temp, "final-preview-cancelled.wav");
        WriteConstant(cancelledPart, 48000, 4800, 0.75f);
        var unpublished = File.ReadAllBytes(cancelledPart);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var rejected = false;
        try { publish.Invoke(null, [cancelledPart, cancelledOutput, "Final normalization regression", cancellation.Token]); }
        catch (TargetInvocationException ex) when (ex.InnerException is OperationCanceledException) { rejected = true; }
        Check(rejected && !File.Exists(cancelledOutput) && File.ReadAllBytes(cancelledPart).AsSpan().SequenceEqual(unpublished),
            "A cancelled preview publish mutated or exposed unfinished cache audio.");
    }

    private static AppSettings LimiterSettings(double lufs, double peak) => new()
    {
        LoudnessNormalize = true, PeakLimiter = true,
        TargetLufs = lufs, TargetTruePeak = peak, VorbisQuality = 95, EncodeParallelism = 1,
    };

    private static void WriteConstant(string path, int rate, int frames, float value)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + frames * 4);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short) 3);
        writer.Write((short) 1);
        writer.Write(rate);
        writer.Write(rate * 4);
        writer.Write((short) 4);
        writer.Write((short) 32);
        writer.Write("data"u8);
        writer.Write(frames * 4);
        for (var i = 0; i < frames; i++) writer.Write(value);
    }

    private static double[] Read(string path)
    {
        using var input = new WaveFileReader(path);
        var format = input.WaveFormat.AsStandardWaveFormat();
        var bytes = new byte[checked((int) input.Length)];
        input.ReadExactly(bytes);
        var width = format.BitsPerSample / 8;
        var samples = new double[bytes.Length / width];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = format.Encoding == WaveFormatEncoding.IeeeFloat && width == 4
                ? BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * width))
                : format.Encoding == WaveFormatEncoding.Pcm && width == 2
                    ? BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * width)) / 32768.0
                    : throw new InvalidDataException("Unexpected final normalization fixture WAV format.");
        return samples;
    }

    private static double MaxDifference(double[] expected, double[] actual)
    {
        Check(expected.Length == actual.Length, "The rendered PCM dimensions differ from the reference.");
        double maximum = 0;
        for (var i = 0; i < expected.Length; i++) maximum = Math.Max(maximum, Math.Abs(expected[i] - actual[i]));
        return maximum;
    }

    private static List<JsonElement> Events(IEnumerable<string> log, string stage)
    {
        var result = new List<JsonElement>();
        var marker = stage + " ";
        foreach (var line in log)
        {
            var index = line.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0) continue;
            using var document = JsonDocument.Parse(line[(index + marker.Length)..]);
            result.Add(document.RootElement.Clone());
        }
        return result;
    }

    private static string Fmt(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static void Run(string tool, string arguments)
    {
        var result = Proc.Run(tool, arguments);
        Check(result.ExitCode == 0, "Native fixture tool failed: " + result.StdErr);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

