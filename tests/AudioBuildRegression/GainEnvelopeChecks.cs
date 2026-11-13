using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FH6RB.Services;
using NAudio.Wave;

internal static class GainEnvelopeChecks
{
    public static async Task RunAsync(string temp)
    {
        var source = Path.Combine(temp, "hot-master-quiet-intro.wav");
        var left = "if(lt(t\\,0.5)\\,0\\,if(between(t\\,0.65\\,0.655)+between(t\\,1.85\\,1.855)\\,0.97*sin(2*PI*997*t)\\," +
            "clip(4*(0.2*sin(2*PI*317*t)+0.08*sin(2*PI*1379*t)+0.03*sin(2*PI*6411*t))\\,-0.9\\,0.9)*if(lt(t\\,3)\\,0.02\\,0.5)))";
        var right = "if(lt(t\\,0.5)\\,0\\,if(between(t\\,0.65\\,0.655)+between(t\\,1.85\\,1.855)\\,0.97*sin(2*PI*1361*t)\\," +
            "clip(4*(0.2*sin(2*PI*594*t)+0.08*sin(2*PI*1379*t)+0.03*sin(2*PI*6411*t))\\,-0.9\\,0.9)*if(lt(t\\,3)\\,0.02\\,0.5)))";
        Run($"-y -hide_banner -loglevel error -f lavfi -i \"aevalsrc={left}|{right}:s=48000:d=60\" -c:a pcm_f32le \"{source}\"");
        var measured = MeasureRange(source);
        var intro = MeasureRange(source, 3);
        Check(measured.Integrated is >= -9 and <= -6 && measured.Lra < 1
            && intro.Integrated < measured.Integrated - 6 && measured.TruePeak - 23 - measured.Integrated < -4,
            "The hot-master fixture does not have a quiet intro and a safely attainable linear loudness target.");
        await CheckConstantSelectionAsync(temp, source, "intro", 60, requireIntro: true);
        var steady = Path.Combine(temp, "hot-master-zero-lra.wav");
        Run("-y -hide_banner -loglevel error -f lavfi -i " +
            $"\"aevalsrc=0.5*sin(2*PI*317*t)|0.5*sin(2*PI*594*t):s=48000:d=12\" -c:a pcm_f32le \"{steady}\"");
        Check(MeasureRange(steady).Lra == 0, "The stationary fixture does not exercise a numeric zero loudness range.");
        await CheckConstantSelectionAsync(temp, steady, "zero-lra", 12, requireIntro: false);
        var varying = Path.Combine(temp, "gain-wide-range.wav");
        WriteVaryingFixture(varying);
        await CheckConstantSelectionAsync(temp, varying, "wide-range", 32, requireIntro: false);
        Console.WriteLine("Verified quiet-intro hot masters and zero LRA select constant gain in both limiter choices without startup pumping or waveform changes.");
    }

    internal static void WriteVaryingFixture(string path)
    {
        Run("-y -hide_banner -loglevel error -f lavfi -i " +
            "\"aevalsrc=(0.2*sin(2*PI*317*t)+0.08*sin(2*PI*1379*t))*if(lt(mod(t\\,32)\\,8)\\,0.05\\,if(lt(mod(t\\,32)\\,16)\\,0.8\\,if(lt(mod(t\\,32)\\,24)\\,0.12\\,0.5)))|" +
            "(0.2*sin(2*PI*594*t)+0.08*sin(2*PI*1379*t))*if(lt(mod(t\\,32)\\,8)\\,0.05\\,if(lt(mod(t\\,32)\\,16)\\,0.8\\,if(lt(mod(t\\,32)\\,24)\\,0.12\\,0.5))):s=48000:d=32\" " +
            $"-c:a pcm_f32le \"{path}\"");
        Check(MeasureRange(path).Lra > 15, "The varying fixture lost its wide loudness range.");
    }

    private static async Task CheckConstantSelectionAsync(string temp, string source, string name, int seconds, bool requireIntro)
    {
        var baseline = Path.Combine(temp, $"gain-{name}-baseline.wav");
        await Loudnorm.RenderAsync(source, baseline, Settings(false));
        var baselineInfo = AudioBuildValidation.Inspect(baseline);
        foreach (var limiter in new[] { false, true })
        {
            var output = Path.Combine(temp, $"gain-{name}-{(limiter ? "limiter" : "constant")}.wav");
            var log = new List<string>();
            await Loudnorm.RenderAsync(source, output, Settings(limiter), log: log.Add);
            var info = AudioBuildValidation.Inspect(output);
            Check(info.Frames == 48000L * seconds && info.SampleRate == 48000 && info.Channels == 2 && info.BitsPerSample == 16,
                "Constant gain changed audio timing or output format.");
            var plan = Events(log, "loudnorm.render.plan").Single();
            Check(plan.GetProperty("mode").GetString() == "constant" && !plan.GetProperty("limiterActive").GetBoolean()
                && Math.Abs(plan.GetProperty("requestedGainDb").GetDouble() - plan.GetProperty("effectiveGainDb").GetDouble()) < 0.000001
                && !log.Any(line => line.Contains("alimiter=", StringComparison.Ordinal)),
                "An attainable constant-gain target needlessly activated the peak limiter.");
            Check(info.PcmSha256 == baselineInfo.PcmSha256,
                "An inactive peak limiter changed the ordinary constant-gain render.");
            CheckGainEnvelope(source, output, requireIntro);
            var meter = MeasureRange(output);
            Check(Math.Abs(meter.Integrated + 23) <= 0.5 && meter.TruePeak <= -1,
                "Constant gain lost the selected loudness target or peak ceiling.");
        }
    }
    private static void CheckGainEnvelope(string source, string output, bool requireIntro)
    {
        var reference = Read(source);
        var actual = Read(output);
        Check(reference.Length == actual.Length, "Constant-gain output changed the waveform's length.");
        double product = 0;
        double referencePower = 0;
        for (var i = 0; i < reference.Length; i++)
        {
            product += reference[i] * actual[i];
            referencePower += reference[i] * reference[i];
        }
        var gain = product / referencePower;
        var gainDb = 20 * Math.Log10(gain);
        double worstError = 0;
        for (var i = 0; i < reference.Length; i++) worstError = Math.Max(worstError, Math.Abs(actual[i] - reference[i] * gain));
        Check(gain > 0 && worstError <= 2.01 / 32768,
            "Gain-compensated hot-master audio changed beyond PCM16 quantization.");
        var intro = new List<double>();
        var body = new List<double>();
        var previousOffset = -1;
        double previousGain = 0;
        const int windowSamples = 4800 * 2;
        for (var offset = 0; offset < reference.Length; offset += windowSamples)
        {
            var count = Math.Min(windowSamples, reference.Length - offset);
            double sourcePower = 0;
            double outputPower = 0;
            for (var i = offset; i < offset + count; i++)
            {
                sourcePower += reference[i] * reference[i];
                outputPower += actual[i] * actual[i];
            }
            if (sourcePower == 0 || 10 * Math.Log10(sourcePower / count * gain * gain) < -65) continue;
            var windowGain = 10 * Math.Log10(outputPower / sourcePower);
            Check(double.IsFinite(windowGain) && Math.Abs(windowGain - gainDb) <= 0.1,
                "The limiter option changed the gain of an audible 100 ms window.");
            if (previousOffset + windowSamples == offset)
                Check(Math.Abs(windowGain - previousGain) <= 0.1, "The limiter option introduced an abrupt gain step absent from the source.");
            var time = offset / (48000.0 * 2);
            if (time < 3) intro.Add(windowGain);
            if (time >= 10) body.Add(windowGain);
            previousGain = windowGain;
            previousOffset = offset;
        }
        if (requireIntro)
            Check(intro.Count > 5 && body.Count > 20 && Math.Abs(Median(intro) - Median(body)) <= 0.1,
                "The quiet intro received different gain from the hot body of the master.");
    }

    private static (double Integrated, double TruePeak, double Lra) MeasureRange(string path, int? seconds = null)
    {
        var filter = (seconds is { } duration ? $"atrim=end={duration}," : "") + "ebur128=peak=true:framelog=quiet";
        var result = Proc.Run(Tools.FfmpegPath, $"-hide_banner -nostats -i \"{path}\" -map 0:a:0 -af \"{filter}\" -f null -");
        Check(result.ExitCode == 0, "Independent loudness-range measurement failed: " + result.StdErr);
        double Number(string pattern)
        {
            var match = Regex.Matches(result.StdErr, pattern).LastOrDefault();
            Check(match is not null, "Independent loudness-range measurement omitted a required statistic.");
            return double.Parse(match!.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        return (Number(@"(?m)^\s*I:\s*(\S+)\s+LUFS\s*$"), Number(@"True peak:\s*Peak:\s*(\S+)\s+dBFS"),
            Number(@"(?m)^\s*LRA:\s*(\S+)\s+LU\s*$"));
    }

    private static AppSettings Settings(bool limiter) => new()
    {
        LoudnessNormalize = true, PeakLimiter = limiter, TargetLufs = -23, TargetTruePeak = -1,
    };
    private static double Median(List<double> values)
    {
        values.Sort();
        return values[values.Count / 2];
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
                    : throw new InvalidDataException("Unexpected hot-master fixture WAV format.");
        return samples;
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

    private static void Run(string arguments)
    {
        var result = Proc.Run(Tools.FfmpegPath, arguments);
        Check(result.ExitCode == 0, "Synthetic normalization fixture failed: " + result.StdErr);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

