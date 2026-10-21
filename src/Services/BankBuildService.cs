using System.Diagnostics;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    bool IsReplacement = false,
    bool RestoreFromBak = false,
    int MarkerSampleRate = 0,
    NormalizationOverride? Normalization = null,
    ulong? BankId = null);

public sealed record AddedSample(string SoundName, long Frames, int SampleRate, string? DisplayName, string? Artist, bool Enabled, bool IsReplacement = false);

public static class BankBuildService
{
    private sealed record BankSource(string Path, FevBank.BankSkeleton Skeleton, byte[] Header, List<Fsb5.SampleLayout> Samples,
        Dictionary<ulong, int> Indices);

    private sealed record PlannedSample(BuildItem Item, BankSource? Source, int Index);

    private sealed record PreparedSample(byte[] FsbHeader, byte[] Header, FevBank.DataRef Data, long Frames, int SampleRate, int Channels);

    private static readonly JsonSerializerOptions DiagnosticJson = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public static async Task<IReadOnlyList<AddedSample>> BuildToFileAsync(
        string sourcePath, string outPath, IReadOnlyList<BuildItem> items,
        AppSettings settings, Action<string>? log = null, Action<string>? progress = null)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(outPath), comparison))
        {
            throw new ArgumentException("The output bank must differ from the source bank.", nameof(outPath));
        }

        if (!Tools.HasVgmstream)
        {
            throw new FileNotFoundException("vgmstream is required to validate encoded audio.", Tools.VgmstreamPath);
        }
        settings = new AppSettings
        {
            LoudnessNormalize = settings.LoudnessNormalize,
            DynamicNormalization = settings.DynamicNormalization,
            TargetLufs = settings.TargetLufs,
            TargetTruePeak = settings.TargetTruePeak,
            VorbisQuality = settings.VorbisQuality,
            EncodeParallelism = settings.EncodeParallelism
        };
        var buildId = Guid.NewGuid().ToString("N");
        var stagedOutput = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath))!, $".fhre-{buildId}.bank");
        var timer = Stopwatch.StartNew();
        Action<string>? buildLog = log is null ? null : message => log($"[build={buildId}] {message}");
        var workDir = Path.Combine(AppPaths.TempBase, "build", buildId);
        Directory.CreateDirectory(workDir);
        try
        {
            Diagnostic(buildLog, "build.start", new { sourcePath, outPath, workDir, tracks = items.Count,
                settings.LoudnessNormalize, settings.DynamicNormalization, settings.TargetLufs, settings.TargetTruePeak, settings.VorbisQuality,
                settings.EncodeParallelism, runtime = Environment.Version.ToString(), processors = Environment.ProcessorCount });
            foreach (var tool in buildLog is null ? Array.Empty<string>() : new[] { Tools.FfmpegPath, Tools.FsbankclPath, Tools.VgmstreamPath })
            {
                var info = FileVersionInfo.GetVersionInfo(tool);
                Diagnostic(buildLog, "build.tool", new { path = tool, info.FileVersion, info.ProductVersion,
                    bytes = new FileInfo(tool).Length, sha256 = HashRange(tool, 0, new FileInfo(tool).Length) });
            }
            var skel = FevBank.ReadSkeleton(sourcePath);
            var result = await BuildCoreAsync(sourcePath, stagedOutput, items, settings, skel, workDir, buildLog, progress)
                .ConfigureAwait(false);
            Diagnostic(buildLog, "build.publish.start", new { source = stagedOutput, destination = outPath });
            File.Move(stagedOutput, outPath, overwrite: true);
            Diagnostic(buildLog, "build.ok", new { elapsedMs = timer.ElapsedMilliseconds, bytes = new FileInfo(outPath).Length });
            return result;
        }
        catch (Exception ex)
        {
            Diagnostic(buildLog, "build.failed", new { elapsedMs = timer.ElapsedMilliseconds, error = ex.ToString() });
            throw;
        }
        finally
        {
            TryDelete(stagedOutput);
            try { Directory.Delete(workDir, recursive: true); }
            catch (Exception ex) { buildLog?.Invoke($"could not clean build directory: {ex.Message}"); }
        }
    }

    private static async Task<IReadOnlyList<AddedSample>> BuildCoreAsync(
        string sourcePath, string outPath, IReadOnlyList<BuildItem> items, AppSettings settings,
        FevBank.BankSkeleton skel, string workDir, Action<string>? log, Action<string>? progress)
    {
        var targetMode = skel.Empty || skel.Mode == 10 ? 15 : skel.Mode;
        var fmtArgs = FormatArgs(targetMode, settings);
        var source = skel.Empty ? null : ReadSource(sourcePath, skel);
        BankSource? backup = null;
        if (!skel.Empty && items.Any(i => i.RestoreFromBak))
        {
            var bakPath = sourcePath + ".bak";
            if (File.Exists(bakPath))
            {
                var bakSkel = FevBank.ReadSkeleton(bakPath);
                if (!bakSkel.Empty) backup = ReadSource(bakPath, bakSkel);
                else log?.Invoke("  WARN: .bak skeleton empty, restore falls back to current bank");
            }
            else
            {
                log?.Invoke($"  WARN: .bak not found for restore: {Path.GetFileName(bakPath)}");
            }
        }

        var plan = new List<PlannedSample>(items.Count);
        var seen = new HashSet<ulong>();
        foreach (var item in items)
        {
            var hash = BankIdFor(item);
            PlannedSample entry;
            if (item.IsNewCustom || item.IsReplacement && !skel.Empty)
            {
                if (item.SourcePath is null || !File.Exists(item.SourcePath))
                {
                    throw new FileNotFoundException($"Audio source not found for {item.SoundName}.", item.SourcePath);
                }
                entry = new PlannedSample(item, null, -1);
            }
            else if (source is not null && source.Indices.TryGetValue(hash, out var index))
            {
                var selected = source;
                if (item.RestoreFromBak)
                {
                    if (backup is not null && backup.Indices.TryGetValue(hash, out var backupIndex))
                    {
                        selected = backup;
                        index = backupIndex;
                    }
                    else
                    {
                        log?.Invoke($"  WARN: {item.SoundName} not found in .bak, kept from current bank");
                    }
                }
                entry = new PlannedSample(item, selected, index);
            }
            else
            {
                log?.Invoke($"  WARN: {item.SoundName} not found in source bank, skipped");
                continue;
            }
            if (!seen.Add(hash)) throw new InvalidOperationException($"duplicate STBL id in plan: {item.SoundName}");
            plan.Add(entry);
        }

        if (plan.Count == 0 && (skel.Empty || items.Count > 0))
        {
            throw new InvalidOperationException("nothing to build (no matching samples)");
        }

        var conversionCount = plan.Count(p => p.Source is not null && p.Source.Skeleton.Mode != targetMode);
        var encodeCount = plan.Count(p => p.Source is null);
        var workCount = conversionCount + encodeCount;
        log?.Invoke($"target: FSB5 mode {skel.Mode} -> {targetMode}; {plan.Count} samples, {encodeCount} new/replaced, {conversionCount} converted");
        var parallelism = Math.Clamp(
            settings.EncodeParallelism > 0 ? settings.EncodeParallelism : AppSettings.RecommendedParallelism,
            1, Environment.ProcessorCount);
        Diagnostic(log, "bank.source", new { path = sourcePath, bytes = new FileInfo(sourcePath).Length,
            skel.Empty, sourceMode = skel.Mode, targetMode, sourceSamples = source?.Samples.Count ?? 0,
            outputSamples = plan.Count, parallelism });
        var prepared = new PreparedSample[plan.Count];
        var added = new AddedSample?[plan.Count];
        var done = 0;
        if (workCount > 0)
        {
            progress?.Invoke($"Encoding…\t0/{workCount}");
            log?.Invoke($"encoding {workCount} track(s), parallelism = {parallelism}");
        }

        var cacheDir = Path.Combine(workDir, "cache");
        Directory.CreateDirectory(cacheDir);
        using var sema = new SemaphoreSlim(parallelism);
        async Task PrepareOneAsync(int i)
        {
            var entry = plan[i];
            var item = entry.Item;
            var rawSource = entry.Source is null && !IsFsbSource(item.SourcePath);
            var effectiveSettings = rawSource ? NormalizationOverride.Resolve(settings, item.Normalization) : settings;
            Action<string>? trackLog = log is null ? null : message => log($"[track={i} sound={JsonSerializer.Serialize(item.SoundName)}] {message}");
            var trackTimer = Stopwatch.StartNew();
            Diagnostic(trackLog, "track.plan", new { item.DisplayName, item.Artist, item.SourcePath,
                item.IsNewCustom, item.IsReplacement, item.RestoreFromBak, item.Enabled, item.GainDb,
                item.MarkerSampleRate, item.Markers, item.Normalization, item.BankId, sourceBank = entry.Source?.Path, sourceIndex = entry.Index });
            Diagnostic(trackLog, "track.normalization", new { requestedOverride = item.Normalization,
                appliesToRawSource = rawSource,
                effective = rawSource ? new
                {
                    effectiveSettings.LoudnessNormalize, effectiveSettings.TargetLufs,
                    effectiveSettings.TargetTruePeak, effectiveSettings.DynamicNormalization
                } : null,
                bypass = rawSource ? null : entry.Source is not null ? "existing bank audio" : "encoded FSB import" });
            if (entry.Source is { } original && original.Skeleton.Mode == targetMode)
            {
                var sample = original.Samples[entry.Index];
                prepared[i] = new PreparedSample(original.Header, sample.Header,
                    new FevBank.DataRef(original.Path, original.Skeleton.DataStartAbs + sample.DataOff, sample.DataLen),
                    sample.Frames, sample.SampleRate, sample.Channels);
                Diagnostic(trackLog, "track.retained", new { sample.Frames, sample.SampleRate, sample.Channels, sample.DataOff, sample.DataLen });
                if (item.RestoreFromBak && ReferenceEquals(original, backup))
                {
                    added[i] = new AddedSample(item.SoundName, sample.Frames, sample.SampleRate,
                        item.DisplayName, item.Artist, item.Enabled, IsReplacement: true);
                }
                return;
            }

            await sema.WaitAsync().ConfigureAwait(false);
            var wav = Path.Combine(workDir, $"sample_{i}.wav");
            var fsb = Path.Combine(workDir, $"sample_{i}.fsb");
            var verifiedWav = Path.Combine(workDir, $"sample_{i}.verified.wav");
            string? sourceBank = null;
            try
            {
                if (entry.Source is null && TryPassthrough(item.SourcePath, targetMode) is { } passthrough)
                {
                    prepared[i] = passthrough;
                    Diagnostic(trackLog, "track.passthrough", new { path = item.SourcePath, passthrough.Frames, passthrough.SampleRate, passthrough.Channels });
                    trackLog?.Invoke("source.fsb.preserved: normalization and gain are not reapplied to encoded FSB imports.");
                    await ValidateFsbAsync(item.SourcePath!, passthrough, null, verifiedWav, trackLog).ConfigureAwait(false);
                }
                else
                {
                    if (entry.Source is { } toConvert)
                    {
                        trackLog?.Invoke($"converting mode {toConvert.Skeleton.Mode} -> {targetMode}");
                        sourceBank = Path.Combine(workDir, $"sample_{i}.source.fsb");
                        var sample = toConvert.Samples[entry.Index];
                        WriteSingleFsb(sourceBank, toConvert.Header, sample.Header,
                            new FevBank.DataRef(toConvert.Path, toConvert.Skeleton.DataStartAbs + sample.DataOff, sample.DataLen));
                        Diagnostic(trackLog, "source.extract", new { path = toConvert.Path, index = entry.Index,
                            fsb = sourceBank, sample.Frames, sample.SampleRate, sample.Channels });
                        await RunAsync(Tools.VgmstreamPath,
                            $"-i -w -s 1 -o \"{wav}\" \"{sourceBank}\"", trackLog).ConfigureAwait(false);
                        if (!File.Exists(wav))
                        {
                            throw new InvalidOperationException($"vgmstream produced no audio for {item.SoundName}");
                        }
                    }
                    else
                    {
                        await CreateInputWavAsync(item, wav, effectiveSettings, trackLog).ConfigureAwait(false);
                    }

                    var pcm = AudioBuildValidation.Inspect(wav);
                    Diagnostic(trackLog, "wav.prepared", pcm);
                    if (entry.Source is null && (pcm.SampleRate != 48000 || pcm.Channels != 2 || pcm.BitsPerSample != 16))
                    {
                        throw new InvalidDataException("Prepared input must be 48000 Hz stereo PCM16.");
                    }
                    await RunAsync(Tools.FsbankclPath,
                        $"{fmtArgs} -o \"{fsb}\" \"{wav}\" -cache_dir \"{cacheDir}\"", trackLog).ConfigureAwait(false);
                    if (!File.Exists(fsb))
                    {
                        throw new InvalidOperationException($"fsbankcl produced no output for {item.SoundName}");
                    }
                    prepared[i] = ReadPreparedSample(fsb, targetMode);
                    await ValidateFsbAsync(fsb, prepared[i], wav, verifiedWav, trackLog, pcm,
                        rawSource && effectiveSettings.LoudnessNormalize
                            ? effectiveSettings.TargetTruePeak : null).ConfigureAwait(false);
                    if (entry.Source is { } converted)
                    {
                        var originalSample = converted.Samples[entry.Index];
                        var allowedPadding = targetMode == 16 ? 255 : 0;
                        if (prepared[i].Frames < originalSample.Frames || prepared[i].Frames - originalSample.Frames > allowedPadding
                            || prepared[i].SampleRate != originalSample.SampleRate || prepared[i].Channels != originalSample.Channels)
                        {
                            throw new InvalidDataException(
                                $"Converting {item.SoundName} changed its audio timing " +
                                $"({originalSample.Frames} frames at {originalSample.SampleRate} Hz -> " +
                                $"{prepared[i].Frames} frames at {prepared[i].SampleRate} Hz).");
                        }
                    }
                }

                if (entry.Source is null || item.RestoreFromBak && ReferenceEquals(entry.Source, backup))
                {
                    var sample = prepared[i];
                    added[i] = new AddedSample(item.SoundName, sample.Frames, sample.SampleRate,
                        item.DisplayName, item.Artist, item.Enabled, item.IsReplacement || item.RestoreFromBak);
                }
                var completed = Interlocked.Increment(ref done);
                Diagnostic(trackLog, "track.ok", new { elapsedMs = trackTimer.ElapsedMilliseconds });
                progress?.Invoke($"Encoding…\t{completed}/{workCount}");
            }
            catch (Exception ex)
            {
                Diagnostic(trackLog, "track.failed", new { elapsedMs = trackTimer.ElapsedMilliseconds, error = ex.ToString() });
                throw new InvalidOperationException($"Audio preparation failed for {item.SoundName} ({item.DisplayName}): {ex.Message}", ex);
            }
            finally
            {
                TryDelete(wav);
                TryDelete(verifiedWav);
                if (sourceBank is not null) TryDelete(sourceBank);
                sema.Release();
            }
        }

        await Task.WhenAll(Enumerable.Range(0, plan.Count).Select(PrepareOneAsync)).ConfigureAwait(false);

        byte[] targetHeader;
        if (prepared.Length > 0) targetHeader = prepared[0].FsbHeader;
        else if (source is not null && source.Skeleton.Mode == targetMode) targetHeader = source.Header;
        else
        {
            var wav = Path.Combine(workDir, "template.wav");
            var fsb = Path.Combine(workDir, "template.fsb");
            await RunAsync(Tools.FfmpegPath,
                $"-y -hide_banner -loglevel error -f lavfi -i anullsrc=r=48000:cl=stereo -t 0.01 -c:a pcm_s16le \"{wav}\"", log)
                .ConfigureAwait(false);
            await RunAsync(Tools.FsbankclPath,
                $"{fmtArgs} -o \"{fsb}\" \"{wav}\" -cache_dir \"{cacheDir}\"", log).ConfigureAwait(false);
            targetHeader = ReadPreparedSample(fsb, targetMode).FsbHeader;
        }

        var stbl = plan.Select((p, i) => (BankIdFor(p.Item), i));
        var stblPayload = FevBank.BuildStbl(stbl);
        var audioBytes = prepared.Sum(p => p.Header.Length + p.Data.Length + (0x20 - p.Data.Length % 0x20) % 0x20);
        log?.Invoke($"prepared audio: {BankSize.Mb(audioBytes)} ({prepared.Length} samples)");
        Diagnostic(log, "bank.assemble.start", new { samples = prepared.Length, audioBytes, targetMode });
        progress?.Invoke("Assembling bank…");
        if (skel.Empty)
        {
            var samples = prepared.Select(p => new Fsb5Sample
            {
                Header = p.Header, Data = ReadSampleData(p.Data), Frames = p.Frames, SampleRate = p.SampleRate, Channels = p.Channels,
            }).ToArray();
            var template = Fsb5.Parse(Fsb5.BuildSingleSample(targetHeader, samples[0]));
            var combined = template.Build(samples);
            var bank = new FevBank(File.ReadAllBytes(sourcePath)).FillEmpty(stblPayload, combined);
            using var output = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            output.Write(bank);
            output.Write(FevBank.ModMarker);
            output.Flush(flushToDisk: true);
        }
        else
        {
            FevBank.AssembleToFile(outPath, skel.FmtChunk, skel.ListPayload, skel.StblRelOff, skel.StblOldSize,
                stblPayload, targetHeader, prepared.Select(p => p.Header).ToArray(), prepared.Select(p => p.Data).ToArray());
        }
        log?.Invoke($"assembled bank: {prepared.Length} samples, FSB5 mode {targetMode}");
        ValidateBank(outPath, plan, prepared, targetMode, log);
        return added.OfType<AddedSample>().ToArray();
    }

    private static void Diagnostic(Action<string>? log, string stage, object details)
    {
        if (log is not null) log($"{stage} {JsonSerializer.Serialize(details, DiagnosticJson)}");
    }

    private static async Task ValidateFsbAsync(string fsbPath, PreparedSample sample, string? expectedWav,
        string decodedPath, Action<string>? log, AudioBuildInfo? expected = null, double? peakCeiling = null)
    {
        var timer = Stopwatch.StartNew();
        Diagnostic(log, "fsb.validate.start", new { path = fsbPath, bytes = new FileInfo(fsbPath).Length,
            sample.Frames, sample.SampleRate, sample.Channels, headerSha256 = Convert.ToHexString(SHA256.HashData(sample.Header)) });
        var maximumTrailingFrames = BinaryPrimitives.ReadUInt32LittleEndian(sample.FsbHeader.AsSpan(24)) == 16 ? 255 : 0;
        if (expected is not null && (sample.Frames < expected.Frames || sample.Frames - expected.Frames > maximumTrailingFrames
            || sample.SampleRate != expected.SampleRate || sample.Channels != expected.Channels))
        {
            throw new InvalidDataException($"FSB metadata differs from its input WAV: " +
                $"{sample.Frames} frames at {sample.SampleRate} Hz, {sample.Channels} channels; " +
                $"expected {expected.Frames} frames at {expected.SampleRate} Hz, {expected.Channels} channels.");
        }
        if (expected is not null && sample.Frames != expected.Frames)
        {
            Diagnostic(log, "fsb.padding", new { inputFrames = expected.Frames, encodedFrames = sample.Frames,
                trailingFrames = sample.Frames - expected.Frames, maximumTrailingFrames });
        }
        await RunAsync(Tools.VgmstreamPath, $"-i -w -s 1 -o \"{decodedPath}\" \"{fsbPath}\"", log).ConfigureAwait(false);
        var decoded = AudioBuildValidation.Inspect(decodedPath);
        Diagnostic(log, "fsb.decoded", decoded);
        if (peakCeiling is { } ceiling && decoded.PeakDb.Any(peak => peak > ceiling))
        {
            log?.Invoke("WARN fsb.peak_ceiling: decoded sample peak exceeds the normalization ceiling; " +
                "audio compression can change peaks after normalization.");
        }
        if (decoded.Frames != sample.Frames || decoded.SampleRate != sample.SampleRate || decoded.Channels != sample.Channels)
        {
            throw new InvalidDataException("Decoded FSB timing or channels do not match its header.");
        }
        if (expectedWav is not null)
        {
            var comparison = AudioBuildValidation.Compare(expectedWav, decodedPath, maximumTrailingFrames);
            Diagnostic(log, "fsb.compare", comparison);
            if (expected is not null && expected.RmsDb.Where((level, channel) =>
                    level > -60 && double.IsNegativeInfinity(decoded.RmsDb[channel])).Any())
            {
                throw new InvalidDataException("FSB decoding produced silence in an audible input channel.");
            }
            if (comparison.ChannelCorrelation.Any(c => c < 0.95) || comparison.GainDb.Any(g => Math.Abs(g) > 3))
            {
                log?.Invoke("WARN fsb.audio_difference: decoded audio differs substantially from the input WAV; inspect fsb.compare and channel levels.");
            }
        }
        if (decoded.PeakDb.Any(peak => peak >= -0.1))
        {
            log?.Invoke("WARN fsb.peak: decoded audio reaches full scale; inspect input levels and gain after normalization.");
        }
        Diagnostic(log, "fsb.validate.ok", new { elapsedMs = timer.ElapsedMilliseconds });
    }

    private static void ValidateBank(string path, IReadOnlyList<PlannedSample> plan,
        IReadOnlyList<PreparedSample> prepared, int targetMode, Action<string>? log)
    {
        var timer = Stopwatch.StartNew();
        Diagnostic(log, "bank.validate.start", new { path, bytes = new FileInfo(path).Length, samples = plan.Count });
        var skeleton = FevBank.ReadSkeleton(path);
        if (skeleton.Empty || skeleton.Mode != targetMode)
        {
            throw new InvalidDataException("Assembled bank has no FSB5 block or its codec differs from the planned codec.");
        }
        var actual = Fsb5.ReadLayout(skeleton.Fsb5HeaderRegion).Samples;
        if (actual.Count != prepared.Count || skeleton.Stbl.Count != plan.Count)
        {
            throw new InvalidDataException("Assembled bank sample count differs from its build plan.");
        }
        var ids = new Dictionary<int, ulong>();
        foreach (var (id, index) in skeleton.Stbl)
        {
            if (!ids.TryAdd(index, id)) throw new InvalidDataException("Assembled bank has duplicate STBL indices.");
        }
        using var bankStream = File.OpenRead(path);
        for (var i = 0; i < prepared.Count; i++)
        {
            var expected = prepared[i];
            var stored = actual[i];
            if (!ids.TryGetValue(i, out var id) || id != BankIdFor(plan[i].Item))
            {
                throw new InvalidDataException($"Assembled bank STBL mapping differs for sample {i}.");
            }
            if (!Fsb5.RebaseHeader(expected.Header, stored.DataOff).AsSpan().SequenceEqual(stored.Header)
                || stored.Frames != expected.Frames || stored.SampleRate != expected.SampleRate || stored.Channels != expected.Channels)
            {
                throw new InvalidDataException($"Assembled bank changed sample {i} metadata.");
            }
            var padding = (0x20 - expected.Data.Length % 0x20) % 0x20;
            if (stored.DataLen != expected.Data.Length + padding)
            {
                throw new InvalidDataException($"Assembled bank changed sample {i} data length.");
            }
            var expectedHash = HashRange(expected.Data.Path, expected.Data.Offset, expected.Data.Length);
            var actualHash = HashRange(bankStream, skeleton.DataStartAbs + stored.DataOff, expected.Data.Length);
            if (!string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Assembled bank changed encoded audio bytes for sample {i}.");
            }
            for (long p = 0; p < padding; p++)
            {
                if (bankStream.ReadByte() != 0) throw new InvalidDataException($"Assembled bank sample {i} has invalid padding.");
            }
            Diagnostic(log, "bank.sample.ok", new { index = i, plan[i].Item.SoundName, id = $"0x{id:x16}",
                stored.DataOff, stored.DataLen, stored.Frames, stored.SampleRate, stored.Channels, dataSha256 = actualHash });
        }
        Diagnostic(log, "bank.validate.ok", new { samples = prepared.Count, elapsedMs = timer.ElapsedMilliseconds });
    }

    private static string HashRange(string path, long offset, long length)
    {
        using var stream = File.OpenRead(path);
        return HashRange(stream, offset, length);
    }

    private static void WriteSingleFsb(string outputPath, byte[] header60, byte[] sampleHeader, FevBank.DataRef data)
    {
        using var source = File.OpenRead(data.Path);
        if (data.Offset < 0 || data.Length < 0 || data.Offset > source.Length - data.Length)
        {
            throw new InvalidDataException("Source FSB sample lies outside its bank.");
        }
        var padding = (0x20 - data.Length % 0x20) % 0x20;
        var header = (byte[]) header60.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)sampleHeader.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), checked((uint)(data.Length + padding)));
        using var output = File.Create(outputPath);
        output.Write(header);
        output.Write(Fsb5.RebaseHeader(sampleHeader, 0));
        source.Position = data.Offset;
        var buffer = new byte[128 * 1024];
        var remaining = data.Length;
        while (remaining > 0)
        {
            var count = source.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
            if (count == 0) throw new EndOfStreamException("Source FSB sample data is truncated.");
            output.Write(buffer, 0, count);
            remaining -= count;
        }
        output.Write(new byte[(int)padding]);
    }

    private static string HashRange(Stream stream, long offset, long length)
    {
        if (offset < 0 || length < 0 || offset > stream.Length - length)
        {
            throw new InvalidDataException("Audio data range lies outside its file.");
        }
        stream.Position = offset;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        while (length > 0)
        {
            var count = stream.Read(buffer, 0, (int)Math.Min(length, buffer.Length));
            if (count == 0) throw new EndOfStreamException("Audio data ended while calculating its checksum.");
            hash.AppendData(buffer, 0, count);
            length -= count;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static BankSource ReadSource(string path, FevBank.BankSkeleton skeleton)
    {
        var (header, samples) = Fsb5.ReadLayout(skeleton.Fsb5HeaderRegion);
        if (samples.Count != skeleton.Stbl.Count)
        {
            throw new InvalidDataException($"STBL entries ({skeleton.Stbl.Count}) != FSB5 samples ({samples.Count})");
        }
        var indices = new Dictionary<ulong, int>();
        foreach (var (id, index) in skeleton.Stbl)
        {
            if (index < 0 || index >= samples.Count)
            {
                throw new InvalidDataException($"STBL sample index {index} is outside the FSB5 sample table.");
            }
            indices[id] = index;
        }
        return new BankSource(path, skeleton, header, samples, indices);
    }

    private static byte[] ReadSampleData(FevBank.DataRef sample)
    {
        var data = new byte[checked((int) sample.Length)];
        using var stream = File.OpenRead(sample.Path);
        stream.Position = sample.Offset;
        stream.ReadExactly(data);
        return data;
    }

    private static byte[] ReadFsb5HeaderRegion(string fsbPath)
    {
        using var fs = File.OpenRead(fsbPath);
        var header = new byte[60];
        fs.ReadExactly(header);
        if (!header.AsSpan(0, 4).SequenceEqual("FSB5"u8))
        {
            throw new InvalidDataException("not an FSB5 block");
        }
        var sampleHeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        var nameSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
        var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));
        var regionSize = 60L + sampleHeaderSize;
        if (regionSize > 64 * 1024 * 1024 || regionSize + nameSize + dataSize > fs.Length)
        {
            throw new InvalidDataException("custom FSB5 header or data region is invalid");
        }
        var region = new byte[checked((int) regionSize)];
        header.CopyTo(region, 0);
        fs.ReadExactly(region.AsSpan(60));
        return region;
    }

    private static bool IsFsbSource(string? path)
        => path is not null && path.EndsWith(".fsb", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    private static int FsbMode(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> header = stackalloc byte[28];
        fs.ReadExactly(header);
        if (!header[..4].SequenceEqual("FSB5"u8)) throw new InvalidDataException("not an FSB5 block");
        return checked((int) BinaryPrimitives.ReadUInt32LittleEndian(header[24..]));
    }

    private static string FormatArgs(int mode, AppSettings settings) => mode switch
    {
        15 => $"-format vorbis -quality {settings.VorbisQuality.ToString(CultureInfo.InvariantCulture)}",
        16 => "-format fadpcm",
        _ => throw new NotSupportedException($"Encoding FSB5 codec mode {mode} is not supported."),
    };

    private static PreparedSample ReadPreparedSample(string path, int targetMode)
    {
        var region = ReadFsb5HeaderRegion(path);
        var mode = BinaryPrimitives.ReadUInt32LittleEndian(region.AsSpan(24));
        if (mode != targetMode)
        {
            throw new InvalidDataException($"FSB5 codec mode {mode} does not match target mode {targetMode}.");
        }
        var (header, samples) = Fsb5.ReadLayout(region);
        if (samples.Count != 1) throw new InvalidDataException($"Expected a single-sample FSB5, found {samples.Count}: {Path.GetFileName(path)}");
        var sample = samples[0];
        if (sample.Frames <= 0 || sample.SampleRate <= 0 || sample.Channels <= 0 || sample.DataLen <= 0)
        {
            throw new InvalidDataException("FSB5 contains empty audio or invalid timing/channel metadata.");
        }
        var dataStart = 60L + BinaryPrimitives.ReadUInt32LittleEndian(region.AsSpan(12))
            + BinaryPrimitives.ReadUInt32LittleEndian(region.AsSpan(16));
        if (sample.DataOff < 0 || sample.DataLen < 0 || dataStart + sample.DataOff + sample.DataLen > new FileInfo(path).Length)
        {
            throw new InvalidDataException("FSB5 sample data region is invalid.");
        }
        if (targetMode == 15)
        {
            AudioBuildValidation.ValidateVorbisPackets(path, dataStart + sample.DataOff, sample.DataLen);
        }
        return new PreparedSample(header, sample.Header,
            new FevBank.DataRef(path, dataStart + sample.DataOff, sample.DataLen), sample.Frames, sample.SampleRate, sample.Channels);
    }

    private static PreparedSample? TryPassthrough(string? path, int targetMode)
    {
        if (!IsFsbSource(path) || FsbMode(path!) != targetMode) return null;
        return ReadPreparedSample(path!, targetMode);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static async Task<string> RunAsync(string exe, string args, Action<string>? log)
    {
        var timer = Stopwatch.StartNew();
        log?.Invoke($"$ {Path.GetFileName(exe)} {args}");
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"cannot start {exe}");
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        try
        {
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            await p.WaitForExitAsync().ConfigureAwait(false);
            LogProcessOutput(log, "stdout", await so.ConfigureAwait(false));
            LogProcessOutput(log, "stderr", await se.ConfigureAwait(false));
            Diagnostic(log, "process.timeout", new { exe, elapsedMs = timer.ElapsedMilliseconds });
            throw new TimeoutException($"{Path.GetFileName(exe)} exceeded 20 minutes.");
        }

        var outText = (await so.ConfigureAwait(false)).Trim();
        var errText = (await se.ConfigureAwait(false)).Trim();
        LogProcessOutput(log, "stdout", outText);
        LogProcessOutput(log, "stderr", errText);
        Diagnostic(log, "process.exit", new { exe, p.ExitCode, elapsedMs = timer.ElapsedMilliseconds });
        if (p.ExitCode != 0)
        {
            var detail = errText.Length > 0 ? errText : outText;
            throw new InvalidOperationException(
                $"{Path.GetFileName(exe)} exited with code {p.ExitCode}" + (detail.Length > 0 ? $": {detail}" : ""));
        }
        return errText;
    }

    private static void LogProcessOutput(Action<string>? log, string stream, string output)
    {
        if (log is null) return;
        foreach (var line in output.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            log($"process.{stream} {line}");
        }
    }

    private static ulong BankIdFor(BuildItem item) => item.BankId ?? Lookup.SoundNameToId(item.SoundName);

    private static async Task CreateInputWavAsync(BuildItem item, string wav, AppSettings settings, Action<string>? log)
    {
        if (item.SourcePath is null || !File.Exists(item.SourcePath))
        {
            throw new FileNotFoundException($"Audio source not found for {item.SoundName}.", item.SourcePath);
        }
        var sourceInfo = new FileInfo(item.SourcePath);
        Diagnostic(log, "source.info", new { path = item.SourcePath, sourceInfo.Length, sourceInfo.LastWriteTimeUtc,
            sha256 = HashRange(item.SourcePath, 0, sourceInfo.Length) });
        var isFsb = IsFsbSource(item.SourcePath);
        if (isFsb)
        {
            var imported = ReadPreparedSample(item.SourcePath, FsbMode(item.SourcePath));
            Diagnostic(log, "source.fsb", new { imported.Frames, imported.SampleRate, imported.Channels,
                processing = "decode and resample; normalization and gain are not reapplied to encoded FSB imports" });
            var decoded = Path.ChangeExtension(wav, ".decoded.wav");
            try
            {
                await RunAsync(Tools.VgmstreamPath,
                    $"-i -w -s 1 -o \"{decoded}\" \"{item.SourcePath}\"", log).ConfigureAwait(false);
                if (!File.Exists(decoded))
                {
                    throw new InvalidOperationException($"vgmstream produced no audio for {item.SoundName}");
                }
                await RunAsync(Tools.FfmpegPath,
                    $"-y -hide_banner -loglevel error -i \"{decoded}\" -ar 48000 -ac 2 -c:a pcm_s16le \"{wav}\"", log)
                    .ConfigureAwait(false);
                if (!File.Exists(wav))
                {
                    throw new InvalidOperationException($"ffmpeg produced no audio for {item.SoundName}");
                }
            }
            finally
            {
                TryDelete(decoded);
            }
            return;
        }
        var sourcePath = item.SourcePath;
        Diagnostic(log, "wav.encode.start", new { sourcePath, normalize = settings.LoudnessNormalize,
            settings.DynamicNormalization, settings.TargetLufs, settings.TargetTruePeak, item.GainDb,
            requestedOverride = item.Normalization });
        await Loudnorm.RenderAsync(sourcePath, wav, settings, item.GainDb ?? 0, log).ConfigureAwait(false);
    }
}
