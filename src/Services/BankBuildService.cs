using System.Diagnostics;
using System.Buffers.Binary;
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
    bool Enabled,
    IReadOnlyDictionary<string, long>? Markers = null,
    bool IsReplacement = false);

public sealed record AddedSample(string SoundName, long Frames, int SampleRate, string? DisplayName, string? Artist, bool Enabled, bool IsReplacement = false);

public sealed record BuildResult(byte[] AssetsBank, IReadOnlyList<AddedSample> Added);

public static class BankBuildService
{
    private static BuildResult Build(byte[] sourceAssetsBank, IReadOnlyList<BuildItem> items,
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
            WorkDirs.Ensure();

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

                var wav = Path.Combine(WorkDirs.WavDir, $"add_{i}.wav");
                Encode(newCustoms[i], wav, settings, log);

                var fsb = Path.Combine(WorkDirs.FsbDir, $"add_{i}.fsb");
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
            added.Add(new AddedSample(newCustoms[i].SoundName, s.Frames, s.SampleRate, newCustoms[i].DisplayName, newCustoms[i].Artist, newCustoms[i].Enabled));
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
        if (item.SourcePath is null)
        {
            return;
        }

        var filter = Loudnorm.Filter(item.SourcePath, settings);

        if (item.GainDb is { } g && Math.Abs(g) > 0.01)
        {
            filter += $",volume={g.ToString("0.0", CultureInfo.InvariantCulture)}dB";
        }

        Run(Tools.FfmpegPath,
            $"-y -hide_banner -loglevel error -i \"{item.SourcePath}\" -ar 48000 -ac 2 -c:a pcm_s16le -af {filter} \"{wav}\"",
            log);
    }

    private static void Run(string exe, string args, Action<string>? log, CancellationToken ct = default)
    {
        log?.Invoke($"$ {Path.GetFileName(exe)} {args}");

        var (outText, errText, code) = Proc.Run(exe, args, ct, timeoutMs: 30 * 60 * 1000);
        outText = outText.Trim();
        errText = errText.Trim();

        if (code != 0)
        {
            if (outText.Length > 0)
            {
                log?.Invoke($"  [out] {outText}");
            }

            if (errText.Length > 0)
            {
                log?.Invoke($"  [err] {errText}");
            }

            var detail = errText.Length > 0 ? errText : outText;

            throw new InvalidOperationException(
                $"{Path.GetFileName(exe)} exited with code {code}" +
                (detail.Length > 0 ? $": {detail}" : ""));
        }

        if (errText.Length > 0)
        {
            log?.Invoke($"  [err] {errText}");
        }
    }

    public static async Task<IReadOnlyList<AddedSample>> BuildToFileAsync(
        string sourcePath, string outPath, IReadOnlyList<BuildItem> items,
        AppSettings settings, Action<string>? log = null, Action<string>? progress = null)
    {
        var skel = FevBank.ReadSkeleton(sourcePath);

        if (skel.Empty)
        {
            log?.Invoke("target: EMPTY skeleton -> in-memory fill");
            var res = Build(File.ReadAllBytes(sourcePath), items, settings, log, progress);
            File.WriteAllBytes(outPath, res.AssetsBank);
            WorkDirs.Clean();
            return res.Added;
        }

        var (srcHeader60, srcLayout) = Fsb5.ReadLayout(skel.Fsb5HeaderRegion);
        
        if (srcLayout.Count != skel.Stbl.Count)
        {
            throw new InvalidDataException($"STBL entries ({skel.Stbl.Count}) != FSB5 samples ({srcLayout.Count})");
        }

        var hashToIndex = new Dictionary<ulong, int>();
        
        foreach (var (id, idx) in skel.Stbl)
        {
            hashToIndex[id] = idx;
        }

        var encoded = items.Where(i => i.IsNewCustom || i.IsReplacement).ToList();
        var fadpcm = skel.Mode == 16;
        var fmtArgs = fadpcm ? "-format fadpcm" : $"-format vorbis -quality {settings.VorbisQuality.ToString(CultureInfo.InvariantCulture)}";
        
        log?.Invoke($"target: populated, FSB5 mode {skel.Mode} -> {(fadpcm ? "FADPCM" : "Vorbis")}; {items.Count} items, {encoded.Count} new");

        WorkDirs.Ensure();

        var custHeaders = new byte[encoded.Count][];
        var custRefs = new FevBank.DataRef[encoded.Count];
        var added = new AddedSample[encoded.Count];
        var parallelism = settings.EncodeParallelism > 0 ? settings.EncodeParallelism : AppSettings.RecommendedParallelism;
        
        parallelism = Math.Clamp(parallelism, 1, Environment.ProcessorCount);
        log?.Invoke($"encoding {encoded.Count} track(s), parallelism = {parallelism}");

        if (encoded.Count > 0)
        {
            progress?.Invoke($"Encoding…\t0/{encoded.Count}");
        }

        using var sema = new SemaphoreSlim(parallelism);
        var startedCount = 0;

        async Task EncodeOneAsync(int i)
        {
            await sema.WaitAsync().ConfigureAwait(false);
            try
            {
                var item = encoded[i];

                var wav = Path.Combine(WorkDirs.WavDir, $"add_{i}.wav");
                await EncodeAsync(item, wav, settings, log).ConfigureAwait(false);

                var n = Interlocked.Increment(ref startedCount);
                progress?.Invoke($"Encoding…\t{n}/{encoded.Count}");

                var fsb = Path.Combine(WorkDirs.FsbDir, $"add_{i}.fsb");
                await RunAsync(Tools.FsbankclPath, $"{fmtArgs} -o \"{fsb}\" \"{wav}\"", log).ConfigureAwait(false);
                if (!File.Exists(fsb)) throw new InvalidOperationException($"fsbankcl produced no output for {item.SoundName}");

                try { File.Delete(wav); } catch { /* ignored */ }

                var region = ReadFsb5HeaderRegion(fsb);
                var (_, clay) = Fsb5.ReadLayout(region);
                if (clay.Count < 1) throw new InvalidOperationException($"fsbankcl FSB5 has no subsounds for {item.SoundName}");

                var c = clay[0];
                var shs = (int) BinaryPrimitives.ReadUInt32LittleEndian(region.AsSpan(12));
                var ns = (int) BinaryPrimitives.ReadUInt32LittleEndian(region.AsSpan(16));

                custHeaders[i] = c.Header;
                custRefs[i] = new FevBank.DataRef(fsb, 60 + shs + ns + c.DataOff, c.DataLen);
                added[i] = new AddedSample(item.SoundName, c.Frames, c.SampleRate, item.DisplayName, item.Artist, item.Enabled, item.IsReplacement);
            }
            finally
            {
                sema.Release();
            }
        }

        await Task.WhenAll(Enumerable.Range(0, encoded.Count).Select(EncodeOneAsync)).ConfigureAwait(false);

        var outHeaders = new List<byte[]>(items.Count);
        var outData = new List<FevBank.DataRef>(items.Count);
        var stbl = new List<(ulong Id, int Index)>(items.Count);
        var seen = new HashSet<ulong>();
        var k = 0;
        var newIndex = 0;

        foreach (var it in items)
        {
            var hash = Lookup.SoundNameToId(it.SoundName);
            
            if (it.IsNewCustom || it.IsReplacement)
            {
                if (!seen.Add(hash))
                {
                    throw new InvalidOperationException($"duplicate STBL id in plan: {it.SoundName}");
                }
                
                outHeaders.Add(custHeaders[k]); outData.Add(custRefs[k]); stbl.Add((hash, newIndex));
                k++; newIndex++;
            }
            else if (hashToIndex.TryGetValue(hash, out var idx) && idx >= 0 && idx < srcLayout.Count)
            {
                if (!seen.Add(hash))
                {
                    throw new InvalidOperationException($"duplicate STBL id in plan: {it.SoundName}");
                }
                
                var sl = srcLayout[idx];
                outHeaders.Add(sl.Header);
                outData.Add(new FevBank.DataRef(sourcePath, skel.DataStartAbs + sl.DataOff, sl.DataLen));
                stbl.Add((hash, newIndex));
                newIndex++;
            }
            else
            {
                log?.Invoke($"  WARN: {it.SoundName} not found in source bank, skipped");
            }
        }

        if (outHeaders.Count == 0)
        {
            throw new InvalidOperationException("nothing to build (no matching samples)");
        }

        progress?.Invoke("Assembling bank…");
        
        var stblPayload = FevBank.BuildStbl(stbl);
        
        FevBank.AssembleToFile(outPath, skel.FmtChunk, skel.ListPayload, skel.StblRelOff, skel.StblOldSize, stblPayload, srcHeader60, outHeaders, outData);
        log?.Invoke($"streamed populated bank: {outHeaders.Count} sample(s)");
        WorkDirs.Clean();
        
        return added;
    }

    private static byte[] ReadFsb5HeaderRegion(string fsbPath)
    {
        using var fs = File.OpenRead(fsbPath);
        var h60 = new byte[60];
        
        if (fs.Read(h60, 0, 60) < 60)
        {
            throw new InvalidDataException("custom FSB5 too small");
        }
        
        var shs = (int) BinaryPrimitives.ReadUInt32LittleEndian(h60.AsSpan(12));
        var region = new byte[60 + shs];
        
        fs.Seek(0, SeekOrigin.Begin);
        
        var read = 0;
        
        while (read < region.Length)
        {
            var n = fs.Read(region, read, region.Length - read); 
            if (n <= 0)
            {
                throw new InvalidDataException("custom FSB5 header truncated");
            } 
            read += n;
        }
        
        return region;
    }


    private static async Task RunAsync(string exe, string args, Action<string>? log)
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
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        
        await p.WaitForExitAsync().ConfigureAwait(false);

        var outText = (await so.ConfigureAwait(false)).Trim();
        var errText = (await se.ConfigureAwait(false)).Trim();

        if (p.ExitCode != 0)
        {
            if (outText.Length > 0)
            {
                log?.Invoke($"  [out] {outText}");
            }
            
            if (errText.Length > 0)
            {
                log?.Invoke($"  [err] {errText}");
            }
            
            var detail = errText.Length > 0 ? errText : outText;
            throw new InvalidOperationException(
                $"{Path.GetFileName(exe)} exited with code {p.ExitCode}" + (detail.Length > 0 ? $": {detail}" : ""));
        }

        if (errText.Length > 0)
        {
            log?.Invoke($"  [err] {errText}");
        }
    }

    private static async Task EncodeAsync(BuildItem item, string wav, AppSettings settings, Action<string>? log)
    {
        if (item.SourcePath is null)
        {
            return;
        }

        var filter = await Loudnorm.FilterAsync(item.SourcePath, settings).ConfigureAwait(false);

        if (item.GainDb is { } g && Math.Abs(g) > 0.01)
        {
            filter += $",volume={g.ToString("0.0", CultureInfo.InvariantCulture)}dB";
        }

        await RunAsync(Tools.FfmpegPath,
            $"-y -hide_banner -loglevel error -i \"{item.SourcePath}\" -ar 48000 -ac 2 -c:a pcm_s16le -af {filter} \"{wav}\"",
            log).ConfigureAwait(false);
    }
}
