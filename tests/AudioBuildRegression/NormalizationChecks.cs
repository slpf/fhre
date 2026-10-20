using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FH6RB.Services;
using NAudio.Wave;

internal static class NormalizationChecks
{
    public static async Task RunAsync(string temp)
    {
        Check(!new AppSettings().DynamicNormalization, "Dynamic normalization is enabled by default.");
        var legacySettings = JsonSerializer.Deserialize<AppSettings>("{\"LoudnessNormalize\":true,\"TargetLufs\":-23}")!;
        Check(!legacySettings.DynamicNormalization, "Existing settings unexpectedly enabled dynamic normalization.");
        var persisted = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new AppSettings
        {
            DynamicNormalization = true, TargetLufs = -20.5, TargetTruePeak = -2.5,
        }))!;
        Check(persisted.DynamicNormalization && persisted.TargetLufs == -20.5 && persisted.TargetTruePeak == -2.5,
            "Normalization options did not survive settings serialization.");
        var rich = Path.Combine(temp, "normalization-rich.wav");
        WriteSignal(rich, 48000, 2, 8, limited: false, transients: false);
        var settings = new AppSettings { LoudnessNormalize = true, DynamicNormalization = false, TargetLufs = -23, TargetTruePeak = -1 };
        var rendered = Path.Combine(temp, "normalization-rich-rendered.wav");
        await Loudnorm.RenderAsync(rich, rendered, settings);
        CheckConstantGain(rich, rendered);
        var measured = Measure(rendered);
        Check(Math.Abs(measured.Integrated + 23) < 0.3, "Constant gain normalization did not reach its attainable loudness target.");
        Check(measured.TruePeak <= -0.9, "Constant gain normalization exceeded the true peak ceiling.");

        var trimmed = Path.Combine(temp, "normalization-rich-trimmed.wav");
        await Loudnorm.RenderAsync(rich, trimmed, settings, -3);
        var trimComparison = AudioBuildValidation.Compare(rendered, trimmed);
        Check(trimComparison.ChannelCorrelation.All(c => c > 0.999999) && trimComparison.GainDb.All(g => Math.Abs(g + 3) < 0.01), "Manual trim altered the signal or was not applied after normalization.");

        var limitedRaw = Path.Combine(temp, "normalization-limited-raw.wav");
        var limited = Path.Combine(temp, "normalization-limited-minus6.wav");
        WriteSignal(limitedRaw, 48000, 2, 8, limited: true, transients: false);
        var loudness = Measure(limitedRaw).Integrated;
        RunTool($"-y -hide_banner -loglevel error -i \"{limitedRaw}\" -af volume={(-6 - loudness).ToString("R", CultureInfo.InvariantCulture)}dB -c:a pcm_f32le \"{limited}\"");
        Check(Math.Abs(Measure(limited).Integrated + 6) < 0.2, "The limited fixture does not represent a loud master near -6 LUFS.");
        var limitedOutput = Path.Combine(temp, "normalization-limited-rendered.wav");
        await Loudnorm.RenderAsync(limited, limitedOutput, settings);
        CheckConstantGain(limited, limitedOutput);
        Check(Math.Abs(Measure(limitedOutput).Integrated + 23) < 0.3, "Loud masters are not attenuated to the selected target.");

        var transient = Path.Combine(temp, "normalization-transient.wav");
        WriteSignal(transient, 48000, 2, 8, limited: false, transients: true);
        var capped = Path.Combine(temp, "normalization-capped.wav");
        var cappedSettings = new AppSettings { LoudnessNormalize = true, TargetLufs = -9, TargetTruePeak = -3 };
        await Loudnorm.RenderAsync(transient, capped, cappedSettings, 30);
        CheckConstantGain(transient, capped);
        Check(Measure(capped).TruePeak <= -2.9, "Positive trim bypassed the true peak ceiling.");
        Check(Measure(capped).Integrated < -10, "Peak limiting unexpectedly forced a transient master to the LUFS target.");

        foreach (var channels in new[] { 1, 6 })
        {
            var input = Path.Combine(temp, $"normalization-{channels}ch-44100.wav");
            var reference = Path.Combine(temp, $"normalization-{channels}ch-reference.wav");
            var output = Path.Combine(temp, $"normalization-{channels}ch-rendered.wav");
            WriteSignal(input, 44100, channels, 3, limited: false, transients: false);
            RunTool($"-y -hide_banner -loglevel error -i \"{input}\" -map 0:a:0 -ar 48000 -ac 2 -c:a pcm_s16le \"{reference}\"");
            await Loudnorm.RenderAsync(input, output, new AppSettings { LoudnessNormalize = false });
            var conversion = AudioBuildValidation.Compare(reference, output);
            Check(conversion.ChannelCorrelation.All(c => c > 0.9999999) && conversion.GainDb.All(g => Math.Abs(g) < 0.001),
                "The renderer does not match the standard 48 kHz stereo conversion when normalization is disabled.");
        }

        var plain = Path.Combine(temp, "normalization-off.wav");
        var plainTrim = Path.Combine(temp, "normalization-off-trim.wav");
        var noNormLog = new List<string>();
        await Loudnorm.RenderAsync(rich, plain, new AppSettings { LoudnessNormalize = false }, log: noNormLog.Add);
        await Loudnorm.RenderAsync(rich, plainTrim, new AppSettings { LoudnessNormalize = false }, -3);
        CheckConstantGain(rich, plain);
        Check(AudioBuildValidation.Compare(plain, plainTrim).GainDb.All(g => Math.Abs(g + 3) < 0.01), "Disabling normalization also disabled manual trim.");
        Check(!HasAnalysisStatus(noNormLog, "miss"), "Disabled normalization still ran an analysis pass.");

        var dynamic = Path.Combine(temp, "normalization-dynamic.wav");
        var dynamicSettings = new AppSettings { LoudnessNormalize = true, DynamicNormalization = true, TargetLufs = -23, TargetTruePeak = -1 };
        var dynamicLog = new List<string>();
        await Loudnorm.RenderAsync(rich, dynamic, dynamicSettings, log: dynamicLog.Add);
        var dynamicInfo = AudioBuildValidation.Inspect(dynamic);
        Check(dynamicInfo.Frames == 48000 * 8 && dynamicInfo.SampleRate == 48000 && dynamicInfo.Channels == 2, "Explicit dynamic normalization changed duration or output dimensions.");
        Check(dynamicLog.Any(l => l.Contains("\"mode\":\"dynamic\"", StringComparison.Ordinal)) && Measure(dynamic).TruePeak <= -0.9,
            "Explicit dynamic mode was not used or exceeded its true peak ceiling.");
        var dynamicStable = Path.Combine(temp, "normalization-dynamic-stable.wav");
        await Loudnorm.RenderAsync(limited, dynamicStable, dynamicSettings);
        Check(Math.Abs(Measure(dynamicStable).Integrated + 23) < 0.5, "Dynamic normalization missed the target on a stable loud master.");

        await CheckSilenceAndErrorsAsync(temp, settings);
        await CheckCacheAsync(temp, rich, settings);
        Console.WriteLine("Verified constant gain, rich stereo dynamics, -6 LUFS limited audio, true peak caps, trims, mono/surround resampling, explicit dynamic mode, silence, errors and cache invalidation.");
    }

    private static async Task CheckSilenceAndErrorsAsync(string temp, AppSettings settings)
    {
        var silence = Path.Combine(temp, "normalization-silence.wav");
        WriteSignal(silence, 48000, 2, 3, limited: false, transients: false, silence: true);
        var output = Path.Combine(temp, "normalization-silence-rendered.wav");
        await Loudnorm.RenderAsync(silence, output, settings);
        var info = AudioBuildValidation.Inspect(output);
        Check(info.Frames == 48000 * 3 && info.RmsDb.All(double.IsNegativeInfinity), "Silent input was rejected or changed during normalization.");
        var empty = Path.Combine(temp, "normalization-empty.wav");
        WriteSignal(empty, 48000, 2, 0, limited: false, transients: false);
        var malformed = Path.Combine(temp, "normalization-malformed.mp3");
        File.WriteAllBytes(malformed, [1, 3, 5, 7, 9]);
        var nonfinite = Path.Combine(temp, "normalization-nan.wav");
        WriteSignal(nonfinite, 48000, 2, 1, limited: false, transients: false);
        using (var file = new FileStream(nonfinite, FileMode.Open, FileAccess.Write))
        {
            file.Position = 44;
            Span<byte> sample = stackalloc byte[4];
            BinaryPrimitives.WriteSingleLittleEndian(sample, float.NaN);
            file.Write(sample);
        }
        foreach (var input in new[] { empty, malformed, nonfinite })
        {
            var failed = false;
            try { await Loudnorm.RenderAsync(input, output, settings); }
            catch (InvalidDataException) { failed = true; }
            catch (InvalidOperationException) { failed = true; }
            Check(failed, "Invalid audio silently fell back to a different normalization mode.");
        }
        foreach (var mode in new[]
        {
            new AppSettings { LoudnessNormalize = false },
            new AppSettings { LoudnessNormalize = true, DynamicNormalization = true },
        })
        {
            var failed = false;
            try { await Loudnorm.RenderAsync(nonfinite, output, mode); }
            catch (InvalidDataException) { failed = true; }
            catch (InvalidOperationException) { failed = true; }
            Check(failed, "Manual or dynamic rendering accepted non-finite source samples.");
        }
        var shortClip = Path.Combine(temp, "normalization-short.wav");
        WriteSignal(shortClip, 48000, 2, 0.1, limited: false, transients: false);
        await Loudnorm.RenderAsync(shortClip, output, settings);
        Check(AudioBuildValidation.Inspect(output).Frames == 4800, "A valid short clip was rejected or stretched.");
        var quiet = Path.Combine(temp, "normalization-below-gate.wav");
        WriteSignal(quiet, 48000, 2, 2, limited: false, transients: false, amplitude: 0.002);
        await Loudnorm.RenderAsync(quiet, output, settings);
        Check(AudioBuildValidation.Inspect(output).Frames == 96000 && AudioBuildValidation.Inspect(output).RmsDb.All(r => r < -65),
            "A valid below-gate clip was rejected or unexpectedly amplified to the loudness target.");
    }

    private static async Task CheckCacheAsync(string temp, string rich, AppSettings settings)
    {
        var input = Path.Combine(temp, "normalization-cache-source.wav");
        File.Copy(rich, input);
        var firstLog = new List<string>();
        var secondLog = new List<string>();
        await Loudnorm.RenderAsync(input, Path.Combine(temp, "normalization-cache-first.wav"), settings, log: firstLog.Add);
        await Loudnorm.RenderAsync(input, Path.Combine(temp, "normalization-cache-second.wav"), settings, log: secondLog.Add);
        Check(HasAnalysisStatus(firstLog, "miss"), "A new source was not analyzed.");
        Check(HasAnalysisStatus(secondLog, "hit"), "Repeated normalization did not reuse source measurements.");
        var preview = AudioDecoder.DecodeAdded(input, settings);
        Check(AudioDecoder.DecodeAdded(input, settings) == preview, "An unchanged preview was not cached.");
        var trimmed = AudioDecoder.DecodeAdded(input, settings, gainDb: -3);
        Check(trimmed != preview && AudioBuildValidation.Compare(preview, trimmed).GainDb.All(g => Math.Abs(g + 3) < 0.01), "Preview cache ignored manual gain.");
        var targetSettings = new AppSettings { LoudnessNormalize = true, TargetLufs = -26, TargetTruePeak = -1 };
        Check(AudioDecoder.DecodeAdded(input, targetSettings) != preview, "Preview cache ignored its loudness target.");
        var peakSettings = new AppSettings { LoudnessNormalize = true, TargetLufs = -23, TargetTruePeak = -3 };
        Check(AudioDecoder.DecodeAdded(input, peakSettings) != preview, "Preview cache ignored its true peak ceiling.");
        var dynamicSettings = new AppSettings { LoudnessNormalize = true, DynamicNormalization = true, TargetLufs = -23, TargetTruePeak = -1 };
        Check(AudioDecoder.DecodeAdded(input, dynamicSettings) != preview, "Preview cache ignored dynamic normalization mode.");
        WriteSignal(input, 48000, 2, 8, limited: false, transients: false, seed: 19);
        File.SetLastWriteTimeUtc(input, DateTime.UtcNow.AddSeconds(2));
        var changedLog = new List<string>();
        await Loudnorm.RenderAsync(input, Path.Combine(temp, "normalization-cache-changed.wav"), settings, log: changedLog.Add);
        Check(HasAnalysisStatus(changedLog, "miss"), "Source changes reused stale measurements.");
        Check(AudioDecoder.DecodeAdded(input, settings) != preview, "Source changes reused stale preview audio.");
        var concurrent = Path.Combine(temp, "normalization-cache-concurrent.wav");
        File.Copy(rich, concurrent);
        var concurrentLog = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var outputs = new[] { Path.Combine(temp, "normalization-concurrent-a.wav"), Path.Combine(temp, "normalization-concurrent-b.wav") };
        await Task.WhenAll(outputs.Select(path => Loudnorm.RenderAsync(concurrent, path, settings, log: concurrentLog.Enqueue)));
        Check(concurrentLog.Count(l => l.StartsWith("loudnorm.measure.start ", StringComparison.Ordinal)) == 1,
            "Concurrent renders duplicated the source analysis.");
        Check(AudioBuildValidation.Inspect(outputs[0]).PcmSha256 == AudioBuildValidation.Inspect(outputs[1]).PcmSha256,
            "Concurrent renders produced different PCM.");
    }

    private static void CheckConstantGain(string original, string rendered)
    {
        var comparison = AudioBuildValidation.Compare(original, rendered);
        Check(comparison.ChannelCorrelation.All(c => c > 0.999999), "Default normalization changed spectral content or channel phase.");
        Check(comparison.GainDb.Max() - comparison.GainDb.Min() < 0.003, "Default normalization changed stereo balance.");
        var source = Read(original);
        var output = Read(rendered);
        var gains = comparison.GainDb.Select(g => Math.Pow(10, g / 20)).ToArray();
        var window = 12000;
        for (var offset = 0; offset < source.Length; offset += window * 2)
        {
            var count = Math.Min(window * 2, source.Length - offset);
            for (var channel = 0; channel < 2; channel++)
            {
                double referencePower = 0;
                double actualPower = 0;
                double errorPower = 0;
                for (var i = offset + channel; i < offset + count; i += 2)
                {
                    var expected = source[i] * gains[channel];
                    var difference = output[i] - expected;
                    referencePower += expected * expected;
                    actualPower += output[i] * output[i];
                    errorPower += difference * difference;
                }
                if (referencePower <= 1e-12) continue;
                Check(Math.Abs(10 * Math.Log10(actualPower / referencePower)) < 0.03, "Default normalization changed the short-term dynamics.");
                Check(errorPower / referencePower < 0.00001, "Gain compensated audio differs beyond 16-bit quantization.");
            }
        }
    }

    internal static (double Integrated, double TruePeak) Measure(string path)
    {
        var result = Proc.Run(Tools.FfmpegPath, $"-hide_banner -nostats -i \"{path}\" -map 0:a:0 -af ebur128=peak=true:framelog=verbose -f null -");
        Check(result.ExitCode == 0, "Independent loudness measurement failed: " + result.StdErr);
        var integrated = Regex.Matches(result.StdErr, @"(?m)^\s*I:\s*(\S+)\s+LUFS\s*$").LastOrDefault();
        var peak = Regex.Matches(result.StdErr, @"True peak:\s*Peak:\s*(\S+)\s+dBFS").LastOrDefault();
        Check(integrated is not null && peak is not null, "Independent loudness measurement returned no statistics.");
        return (double.Parse(integrated!.Groups[1].Value, CultureInfo.InvariantCulture),
            double.Parse(peak!.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    internal static void WriteSignal(string path, int rate, int channels, double seconds, bool limited, bool transients, bool silence = false, uint seed = 7, double amplitude = 1)
    {
        var frames = checked((int) Math.Round(rate * seconds));
        using var output = File.Create(path);
        using var writer = new BinaryWriter(output);
        writer.Write("RIFF"u8);
        writer.Write(36 + frames * channels * 4);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short) 3);
        writer.Write((short) channels);
        writer.Write(rate);
        writer.Write(rate * channels * 4);
        writer.Write((short) (channels * 4));
        writer.Write((short) 32);
        writer.Write("data"u8);
        writer.Write(frames * channels * 4);
        var random = seed;
        double[] envelopeLevels = [0.12, 0.8, 0.24, 0.5];
        for (var frame = 0; frame < frames; frame++)
        {
            var time = frame / (double) rate;
            var envelope = envelopeLevels[(frame / (rate * 2)) % 4];
            for (var channel = 0; channel < channels; channel++)
            {
                random = unchecked(random * 1664525 + 1013904223);
                var noise = random / (double) uint.MaxValue * 2 - 1;
                var value = 0.2 * Math.Sin(time * 2 * Math.PI * (317 + channel * 277)) + 0.08 * Math.Sin(time * 2 * Math.PI * 1379) + 0.02 * noise;
                value = limited ? Math.Clamp(value * 12, -0.95, 0.95) : value * envelope;
                if (transients && frame % (rate / 2) == 0) value = channel % 2 == 0 ? 0.98 : -0.98;
                writer.Write(silence ? 0f : (float) (value * amplitude));
            }
        }
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
                    : throw new InvalidDataException("Unexpected normalization test WAV format.");
        return samples;
    }

    private static void RunTool(string arguments)
    {
        var result = Proc.Run(Tools.FfmpegPath, arguments);
        Check(result.ExitCode == 0, "Fixture conversion failed: " + result.StdErr);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static bool HasAnalysisStatus(IEnumerable<string> log, string status)
    {
        const string marker = "loudnorm.analysis.cache ";
        foreach (var line in log)
        {
            var index = line.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0) continue;
            using var document = JsonDocument.Parse(line[(index + marker.Length)..]);
            if (document.RootElement.GetProperty("status").GetString() == status) return true;
        }
        return false;
    }
}
