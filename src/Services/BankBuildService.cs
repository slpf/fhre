using System.Diagnostics;
using System.Globalization;
using FH6RB.Core;

namespace FH6RB.Services;

public sealed record BuildItem(
    string SoundName,
    bool IsNewCustom,
    string? SourcePath,
    string? DisplayName,
    string? Artist,
    double? GainDb,
    bool Enabled);

public sealed record AddedSample(string SoundName, long Frames, int SampleRate, string? DisplayName, string? Artist, bool Enabled);

public sealed record BuildResult(byte[] AssetsBank, IReadOnlyList<AddedSample> Added);

public static class BankBuildService
{
    public static BuildResult Build(byte[] sourceAssetsBank, IReadOnlyList<BuildItem> items,
                                    AppSettings settings, Action<string>? log = null, Action<string>? progress = null)
    {
        var fev = new FevBank(sourceAssetsBank);
        var empty = fev.IsEmptySkeleton;
        log?.Invoke($"target: {(empty ? "EMPTY skeleton" : "populated bank")}");

        var newCustoms = items.Where(i => i.IsNewCustom).ToList();
        log?.Invoke($"items: {items.Count} total, {newCustoms.Count} new custom(s)");
        
        var addedSamples = new List<Fsb5Sample>(newCustoms.Count);
        Fsb5? templateFsb = null;

        if (newCustoms.Count > 0)
        {
            using var scratch = new ScratchDir();
            
            var mode = empty ? 15 : fev.Fsb5Mode;
            var fadpcm = mode == 16;
            var fmtArgs = fadpcm
                ? "-format fadpcm"
                : $"-format vorbis -quality {settings.VorbisQuality.ToString(CultureInfo.InvariantCulture)}";
            log?.Invoke($"source FSB5 mode = {mode} -> encoding customs as {(fadpcm ? "FADPCM" : "Vorbis")}");

            for (var i = 0; i < newCustoms.Count; i++)
            {
                var name = newCustoms[i].DisplayName ?? newCustoms[i].SoundName;
                progress?.Invoke($"Encoding {i + 1}/{newCustoms.Count}: {name}");
                log?.Invoke($"track {i + 1}/{newCustoms.Count}: {name}");

                var wav = scratch.File($"add_{i}.wav");
                Encode(newCustoms[i], wav, settings, log);

                var fsb = scratch.File($"add_{i}.fsb");
                Run(Tools.FsbankclPath, $"{fmtArgs} -o \"{fsb}\" \"{wav}\"", log);
                if (!File.Exists(fsb))
                {
                    throw new InvalidOperationException("fsbankcl produced no output (.fsb)");
                }

                var parsed = Fsb5.Parse(File.ReadAllBytes(fsb));
                if (parsed.Samples.Count < 1)
                {
                    throw new InvalidOperationException($"fsbankcl FSB5 has no subsounds for {newCustoms[i].SoundName}");
                }

                templateFsb ??= parsed;
                addedSamples.Add(parsed.Samples[0]);
            }
        }

        var added = new List<AddedSample>(newCustoms.Count);
        for (var i = 0; i < newCustoms.Count; i++)
        {
            var s = addedSamples[i];
            added.Add(new AddedSample(newCustoms[i].SoundName, s.Frames, s.SampleRate,
                newCustoms[i].DisplayName, newCustoms[i].Artist, newCustoms[i].Enabled));
        }

        byte[] outBank;

        progress?.Invoke("Assembling bank…");

        if (empty)
        {
            if (newCustoms.Count == 0 || templateFsb is null)
            {
                throw new InvalidOperationException("Empty bank: nothing to build (no new tracks).");
            }
            
            var combined = templateFsb.Build(addedSamples);
            var entries = newCustoms.Select((c, i) => (Lookup.SoundNameToId(c.SoundName), i)).ToList();
            var stbl = FevBank.BuildStbl(entries);
            outBank = fev.FillEmpty(stbl, combined);
            log?.Invoke($"filled empty skeleton: {newCustoms.Count} sample(s)");
        }
        else
        {
            var editor = new BankEditor(sourceAssetsBank);
            var hashToIndex = new Dictionary<ulong, int>();
            for (var i = 0; i < editor.SourceSampleCount; i++)
            {
                hashToIndex[editor.HashForIndex(i)] = i;
            }

            var plan = new List<PlanItem>(items.Count);
            var k = 0;
            foreach (var it in items)
            {
                if (it.IsNewCustom)
                {
                    plan.Add(PlanItem.Add(Lookup.SoundNameToId(it.SoundName), addedSamples[k++]));
                }
                else if (hashToIndex.TryGetValue(Lookup.SoundNameToId(it.SoundName), out var idx))
                {
                    plan.Add(PlanItem.Keep(idx));
                }
                else
                {
                    log?.Invoke($"  WARN: {it.SoundName} not found in source bank, skipped");
                }
            }

            outBank = editor.Build(plan);
            log?.Invoke($"rebuilt populated bank: {plan.Count} sample(s)");
        }

        return new BuildResult(outBank, added);
    }
    
    private static void Encode(BuildItem item, string wav, AppSettings settings, Action<string>? log)
    {
        var i  = settings.TargetLufs.ToString(CultureInfo.InvariantCulture);
        var tp = settings.TargetTruePeak.ToString(CultureInfo.InvariantCulture);
        var filter = $"loudnorm=I={i}:TP={tp}:LRA=11";

        if (item.GainDb is { } g && Math.Abs(g) > 0.01)
        {
            filter += $",volume={g.ToString("0.0", CultureInfo.InvariantCulture)}dB";
        }

        Run(Tools.FfmpegPath,
            $"-y -hide_banner -loglevel error -i \"{item.SourcePath}\" -ar 48000 -ac 2 -c:a pcm_s16le -af {filter} \"{wav}\"",
            log);
    }

    private static void Run(string exe, string args, Action<string>? log)
    {
        log?.Invoke($"$ {Path.GetFileName(exe)} {args}");

        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"cannot start {exe}");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();

        var outText = stdout.Result.Trim();
        var errText = stderr.Result.Trim();

        if (p.ExitCode != 0)
        {
            if (outText.Length > 0) log?.Invoke($"  [out] {outText}");
            if (errText.Length > 0) log?.Invoke($"  [err] {errText}");
            var detail = (errText.Length > 0 ? errText : outText);
            throw new InvalidOperationException(
                $"{Path.GetFileName(exe)} exited with code {p.ExitCode}" +
                (detail.Length > 0 ? $": {detail}" : ""));
        }

        if (errText.Length > 0) log?.Invoke($"  [err] {errText}");
    }
}
