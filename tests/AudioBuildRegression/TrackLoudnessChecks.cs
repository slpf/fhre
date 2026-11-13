using System.Diagnostics;
using FH6RB.Core;
using FH6RB.Services;

internal static class TrackLoudnessChecks
{
    public static async Task RunAsync(string root, Action<string> writeEmptyBank)
    {
        var source = Path.Combine(root, "ui-loudness.wav");
        NormalizationChecks.WriteSignal(source, 48000, 2, 3.013, limited: false, transients: false);
        var original = File.ReadAllBytes(source);
        var fast = await TrackLoudnessService.MeasureAsync(source);
        var reference = await Loudnorm.MeasureOutputAsync(source);
        Check(fast is not null && reference.IntegratedLufs is { } lufs && Math.Abs(fast.Value - lufs) < 0.11,
            "The fast UI measurement disagrees with the full loudness meter.");
        var template = Path.Combine(root, "ui-loudness.empty.bank");
        var bank = Path.Combine(root, "ui-loudness.bank");
        writeEmptyBank(template);
        var name = Naming.MakeSoundName(632);
        await BankBuildService.BuildToFileAsync(template, bank,
            [new BuildItem(name, true, source, "UI loudness", null, null, true)], new AppSettings { LoudnessNormalize = false });
        var fsb = Path.Combine(root, "ui-loudness.fsb");
        File.WriteAllBytes(fsb, TrackPack.ExtractSampleFsb(bank, 0));
        var native = await TrackLoudnessService.MeasureAsync(fsb, 0);
        var wrapped = await TrackLoudnessService.MeasureAsync(bank, 0);
        Check(native is not null && wrapped is not null && Math.Abs(native.Value - wrapped.Value) < 0.01,
            "Streaming a wrapped bank changed its measured loudness.");
        Check(File.ReadAllBytes(source).SequenceEqual(original), "The UI measurement modified the source audio.");
        var attenuated = Path.Combine(root, "ui-loudness.minus6.wav");
        Run($"-y -hide_banner -loglevel error -i \"{source}\" -af volume=-6dB -c:a pcm_s16le \"{attenuated}\"");
        File.Copy(attenuated, source, overwrite: true);
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddSeconds(2));
        var changed = await TrackLoudnessService.MeasureAsync(source);
        Check(changed is not null && Math.Abs(changed.Value - fast!.Value + 6) < 0.2, "The loudness cache reused stale file contents.");
        var silence = Path.Combine(root, "ui-loudness.silence.wav");
        Run($"-y -hide_banner -loglevel error -f lavfi -i anullsrc=r=48000:cl=stereo -t 1 -c:a pcm_s16le \"{silence}\"");
        Check(await TrackLoudnessService.MeasureAsync(silence) is null, "Silent audio was presented as a finite LUFS value.");
        var invalid = Path.Combine(root, "ui-loudness.invalid.wav");
        File.WriteAllText(invalid, "not audio");
        var rejected = false;
        try { await TrackLoudnessService.MeasureAsync(invalid); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "The UI meter accepted a failed decoder result.");
        var longSource = Path.Combine(root, "ui-loudness.cancel.wav");
        Run($"-y -hide_banner -loglevel error -f lavfi -i sine=frequency=529:sample_rate=48000:duration=600 -ac 2 -c:a pcm_s16le \"{longSource}\"");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(60));
        var timer = Stopwatch.StartNew();
        var canceled = false;
        try { await TrackLoudnessService.MeasureAsync(longSource, ct: cancellation.Token); }
        catch (OperationCanceledException) { canceled = true; }
        Check(canceled && timer.Elapsed < TimeSpan.FromSeconds(5), "Canceling a UI meter did not promptly stop its process.");
        var released = Stopwatch.StartNew();
        while (true)
        {
            try { using (File.Open(longSource, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } break; }
            catch (IOException) when (released.Elapsed < TimeSpan.FromSeconds(2)) { await Task.Delay(25); }
        }
        var recovery = Path.Combine(root, "ui-loudness.recovery.wav");
        File.Copy(source, recovery);
        Check(await TrackLoudnessService.MeasureAsync(recovery) == changed, "Cancellation poisoned a cached result or blocked the worker queue.");
        var tasks = Enumerable.Range(0, 6).Select(index =>
        {
            var copy = Path.Combine(root, $"ui-loudness.concurrent-{index}.fsb");
            File.Copy(fsb, copy);
            return TrackLoudnessService.MeasureAsync(copy, 0);
        }).ToArray();
        await Task.WhenAll(tasks);
        Check(tasks.All(t => t.Result == wrapped), "Concurrent UI requests produced inconsistent bank measurements.");
        Console.WriteLine("Verified fast UI LUFS against the full meter, standalone/wrapped FSB streaming, source/cache safety, silence, invalid audio and cancellation.");
    }

    private static void Run(string arguments)
    {
        var result = Proc.Run(Tools.FfmpegPath, arguments);
        Check(result.ExitCode == 0, result.StdErr);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
