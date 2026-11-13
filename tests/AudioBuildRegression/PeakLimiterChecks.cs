using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using FH6RB.Core;
using FH6RB.Services;
using NAudio.Wave;

internal static class PeakLimiterChecks
{
    public static async Task RunAsync(string temp, Action<string> writeEmptyBank)
    {
        var source = Path.Combine(temp, "limiter-isolated-peak.wav");
        WriteLinked(source, 48000 * 4, frame => frame == 48000 * 2 ? 1.4 :
            0.05 * Math.Sin(frame * 2 * Math.PI * 317 / 48000) + 0.03 * Math.Sin(frame * 2 * Math.PI * 1379 / 48000)
            + 0.02 * Math.Sin(frame * 2 * Math.PI * 6411 / 48000));
        var capped = Path.Combine(temp, "limiter-capped.wav");
        var limited = Path.Combine(temp, "limiter-active.wav");
        await Loudnorm.RenderAsync(source, capped, Settings(false));
        var log = new List<string>();
        await Loudnorm.RenderAsync(source, limited, Settings(true), log: log.Add);
        var plan = Events(log, "loudnorm.render.plan").Single();
        Check(plan.GetProperty("mode").GetString() == "limited" && plan.GetProperty("limiterActive").GetBoolean()
            && plan.GetProperty("predictedTruePeak").GetDouble() > -1
            && log.Any(line => line.Contains("alimiter=", StringComparison.Ordinal)),
            "A peak-constrained source did not activate the optional limiter.");
        var original = Read(source);
        var actual = Read(limited);
        Check(original.Length == actual.Length && AudioBuildValidation.Inspect(limited).Frames == 48000 * 4,
            "The limiter changed duration or channel count.");
        double product = 0;
        double power = 0;
        for (var i = 0; i < original.Length; i += 2)
        {
            Check(Math.Abs(actual[i + 1] - actual[i] * 0.25) <= 2.01 / 32768,
                "Peak limiting changed the fixed stereo balance instead of linking its channel gain.");
            var time = i / (48000.0 * 2);
            if (time < 0.02 || time > 3.98 || Math.Abs(time - 2) < 0.3) continue;
            product += original[i] * actual[i];
            power += original[i] * original[i];
        }
        var outsideGain = product / power;
        double error = 0;
        for (var i = 0; i < original.Length; i += 2)
        {
            var time = i / (48000.0 * 2);
            if (time < 0.02 || time > 3.98 || Math.Abs(time - 2) < 0.3) continue;
            error = Math.Max(error, Math.Abs(actual[i] - original[i] * outsideGain));
        }
        Check(outsideGain > 1 && error <= 3.01 / 32768,
            "The limiter pumped or changed waveform alignment outside the isolated peak region.");
        var peakFrame = Enumerable.Range(0, actual.Length / 2).MaxBy(frame => Math.Abs(actual[frame * 2]));
        Check(Math.Abs(peakFrame - 48000 * 2) <= 1, "Limiter lookahead shifted the isolated peak.");
        var output = AudioBuildValidation.Inspect(limited);
        var quiet = AudioBuildValidation.Inspect(capped);
        Check(output.RmsDb[0] > quiet.RmsDb[0] + 3 && NormalizationChecks.Measure(limited).TruePeak <= -1,
            "The limiter lost the requested body level or exceeded its true peak ceiling.");
        await CheckTinyAndTailAsync(temp);
        await CheckFloatProtectionAsync(temp);
        await CheckMixedBankAsync(temp, source, capped, limited, writeEmptyBank);
        Console.WriteLine("Verified linked stereo limiting, stable gain away from peaks, float safety, exact alignment/tiny/tail frames, preview and mixed-bank limiter settings.");
    }

    private static async Task CheckTinyAndTailAsync(string temp)
    {
        foreach (var frames in new[] { 1, 2, 10 })
        {
            var source = Path.Combine(temp, $"limiter-tiny-{frames}.wav");
            var output = Path.Combine(temp, $"limiter-tiny-{frames}-output.wav");
            WriteLinked(source, frames, _ => 0.75);
            var log = new List<string>();
            await Loudnorm.RenderAsync(source, output, Settings(true), 30, log.Add);
            var measured = await Loudnorm.MeasureOutputAsync(output);
            Check(Events(log, "loudnorm.render.plan").Single().GetProperty("limiterActive").GetBoolean()
                && measured.Frames == frames && measured.TruePeakDbtp is { } peak && peak <= -1
                && AudioBuildValidation.Inspect(output).RmsDb.Any(value => value > -60),
                "An active limiter dropped a tiny signal, changed its frame count or bypassed its peak ceiling.");
        }
        var tailSource = Path.Combine(temp, "limiter-tail.wav");
        var tailOutput = Path.Combine(temp, "limiter-tail-output.wav");
        WriteLinked(tailSource, 96000, frame => frame == 95999 ? 0.9 : 0);
        var tailLog = new List<string>();
        await Loudnorm.RenderAsync(tailSource, tailOutput, Settings(true), 30, tailLog.Add);
        var tail = Read(tailOutput);
        Check(Events(tailLog, "loudnorm.render.plan").Single().GetProperty("limiterActive").GetBoolean()
            && tail.Length == 96000 * 2 && tail.Skip(tail.Length - 16).Any(value => Math.Abs(value) > 0.0001)
            && NormalizationChecks.Measure(tailOutput).TruePeak <= -1,
            "Limiter delay compensation lost the final impulse or exposed synthetic padding as audio.");
    }

    private static async Task CheckFloatProtectionAsync(string temp)
    {
        foreach (var (frequency, ceiling) in new[] { (20000, -1d), (12000, 0d) })
        {
            var source = Path.Combine(temp, $"limiter-float-{frequency}.wav");
            var reference = Path.Combine(temp, $"limiter-float-{frequency}-reference.wav");
            var output = Path.Combine(temp, $"limiter-float-{frequency}-output.wav");
            Run(Tools.FfmpegPath, "-y -hide_banner -loglevel error -f lavfi -i " +
                $"\"aevalsrc=0.01*sin(2*PI*317*t)+1.2*sgn(sin(2*PI*{frequency}*t))*lt(mod(t\\,0.1)\\,0.002)|" +
                $"0.01*sin(2*PI*594*t)+1.2*sgn(sin(2*PI*{frequency - 100}*t))*lt(mod(t\\,0.1)\\,0.002):s=48000:d=4\" -c:a pcm_f32le \"{source}\"");
            Check(AudioBuildValidation.Inspect(source).PeakDb.Max() > 1,
                "The floating fixture does not exercise samples above the PCM16 range.");
            var settings = Settings(true);
            settings.TargetLufs = -9;
            settings.TargetTruePeak = ceiling;
            var log = new List<string>();
            await Loudnorm.RenderAsync(source, output, settings, 20, log.Add);
            var plan = Events(log, "loudnorm.render.plan").Single();
            Check(plan.GetProperty("limiterActive").GetBoolean(), "The above-full-scale fixture did not activate the limiter.");
            var gain = plan.GetProperty("requestedGainDb").GetDouble();
            var limit = Math.Pow(10, (ceiling - 0.2) / 20);
            Run(Tools.FfmpegPath, $"-y -hide_banner -loglevel error -i \"{source}\" -af " +
                $"\"aresample=48000:osf=dbl:ochl=stereo:rematrix_maxval=1,volume={Fmt(gain)}dB:precision=double,apad=pad_len=480," +
                $"aresample=192000:osf=dbl,alimiter=limit={Fmt(limit)}:attack=5:release=50:asc=false:level=false:latency=true," +
                $"aresample=48000:osf=dbl:ochl=stereo:rematrix_maxval=1,atrim=end_sample=192000\" -c:a pcm_f32le \"{reference}\"");
            var expected = Read(reference);
            var actual = Read(output);
            Check(expected.Length == actual.Length, "Float protection changed the limited waveform length.");
            double product = 0;
            double power = 0;
            for (var i = 0; i < expected.Length; i++) { product += expected[i] * actual[i]; power += expected[i] * expected[i]; }
            var attenuation = product / power;
            double error = 0;
            for (var i = 0; i < expected.Length; i++) error = Math.Max(error, Math.Abs(actual[i] - expected[i] * attenuation));
            Check(attenuation is > 0 and <= 1.00001 && error <= 2.01 / 32768
                && NormalizationChecks.Measure(output).TruePeak <= ceiling && Events(log, "loudnorm.pcm.protection").Count == 1,
                "Limiting or PCM conversion clipped the floating waveform before applying final peak protection.");
        }
    }

    private static async Task CheckMixedBankAsync(string temp, string source, string capped, string limited, Action<string> writeEmptyBank)
    {
        Check(AudioBuildValidation.Inspect(AudioDecoder.DecodeAdded(source, Settings(false))).PcmSha256 == AudioBuildValidation.Inspect(capped).PcmSha256
            && AudioBuildValidation.Inspect(AudioDecoder.DecodeAdded(source, Settings(true))).PcmSha256 == AudioBuildValidation.Inspect(limited).PcmSha256,
            "Preview caching ignored the limiter option or changed rendered PCM.");
        var empty = Path.Combine(temp, "limiter-mixed-empty.bank");
        var output = Path.Combine(temp, "limiter-mixed.bank");
        writeEmptyBank(empty);
        var log = new ConcurrentQueue<string>();
        await BankBuildService.BuildToFileAsync(empty, output,
            [new BuildItem(Naming.MakeSoundName(231), true, source, "Capped", null, null, true),
             new BuildItem(Naming.MakeSoundName(232), true, source, "Limited", null, null, true,
                 Normalization: new NormalizationOverride(true, -16, -1, true))], Settings(false), log.Enqueue);
        for (var i = 0; i < 2; i++)
        {
            var trackLog = log.Where(line => line.Contains($"[track={i} ", StringComparison.Ordinal)).ToArray();
            var expected = AudioBuildValidation.Inspect(i == 0 ? capped : limited).PcmSha256;
            Check(Events(trackLog, "wav.prepared").Single().GetProperty("PcmSha256").GetString() == expected
                && Events(trackLog, "loudnorm.render.plan").Single().GetProperty("limiterActive").GetBoolean() == (i == 1)
                && NormalizationChecks.Measure(AudioDecoder.DecodeBank(output, i)).TruePeak <= -1,
                "Mixed-bank processing ignored inherited/overridden limiter settings or lost final peak protection.");
        }
    }

    private static AppSettings Settings(bool limiter) => new()
    {
        LoudnessNormalize = true, PeakLimiter = limiter, TargetLufs = -16, TargetTruePeak = -1,
        VorbisQuality = 95, EncodeParallelism = 1,
    };

    private static void WriteLinked(string path, int frames, Func<int, double> sample)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8); writer.Write(36 + frames * 8); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short) 3); writer.Write((short) 2); writer.Write(48000); writer.Write(48000 * 8);
        writer.Write((short) 8); writer.Write((short) 32); writer.Write("data"u8); writer.Write(frames * 8);
        for (var frame = 0; frame < frames; frame++) { var value = sample(frame); writer.Write((float) value); writer.Write((float) (value * 0.25)); }
    }

    private static double[] Read(string path)
    {
        using var input = new WaveFileReader(path);
        var format = input.WaveFormat.AsStandardWaveFormat();
        var bytes = new byte[checked((int) input.Length)]; input.ReadExactly(bytes);
        var width = format.BitsPerSample / 8;
        var result = new double[bytes.Length / width];
        for (var i = 0; i < result.Length; i++) result[i] = format.Encoding == WaveFormatEncoding.IeeeFloat && width == 4
            ? BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * width))
            : format.Encoding == WaveFormatEncoding.Pcm && width == 2 ? BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * width)) / 32768.0
                : throw new InvalidDataException("Unexpected limiter fixture format.");
        return result;
    }

    private static List<JsonElement> Events(IEnumerable<string> log, string stage)
    {
        var result = new List<JsonElement>();
        foreach (var line in log)
        {
            var index = line.IndexOf(stage + " ", StringComparison.Ordinal);
            if (index < 0) continue;
            using var document = JsonDocument.Parse(line[(index + stage.Length + 1)..]); result.Add(document.RootElement.Clone());
        }
        return result;
    }

    private static string Fmt(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static void Run(string tool, string arguments)
    {
        var result = Proc.Run(tool, arguments);
        Check(result.ExitCode == 0, "Native limiter fixture failed: " + result.StdErr);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
