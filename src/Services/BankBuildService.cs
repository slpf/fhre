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
    public const int OriginalAudioQuality = 100;

    private const int MaxPeakCorrections = 2;
    private const double EncodedPeakMarginDb = 0.2;
    private const double PeakReadingHalfStepDb = 0.05;
    private const double LoudnessToleranceLu = 0.5;

    private sealed record BankSource(string Path, FevBank.BankSkeleton Skeleton, byte[] Header, List<Fsb5.SampleLayout> Samples,
        Dictionary<ulong, int> Indices);

    private sealed record PlannedSample(BuildItem Item, BankSource? Source, int Index);

    private sealed record PreparedSample(byte[] FsbHeader, byte[] Header, FevBank.DataRef Data, long Frames, int SampleRate, int Channels);

    private static readonly JsonSerializerOptions DiagnosticJson = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public static Task<IReadOnlyList<AddedSample>> BuildToFileAsync(
        string sourcePath, string outPath, IReadOnlyList<BuildItem> items,
        AppSettings settings, Action<string>? log = null, Action<string>? progress = null) =>
        BuildToFileAsync(sourcePath, outPath, items, settings, log, progress, verbose: true, useBackupSources: false);

    internal static async Task<IReadOnlyList<AddedSample>> BuildToFileAsync(
        string sourcePath, string outPath, IReadOnlyList<BuildItem> items,
        AppSettings settings, Action<string>? log, Action<string>? progress, bool verbose, bool useBackupSources)
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
            PeakLimiter = settings.PeakLimiter,
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
            var skel = FevBank.ReadSkeleton(sourcePath);
            var xmaEncoder = skel.Mode == 10
                ? await XmaEncoderService.CheckAsync(buildLog).ConfigureAwait(false)
                : new XmaEncoderStatus(false, "XMA encoding is not required by the source bank.");
            Diagnostic(buildLog, "build.start", new { sourcePath, outPath, workDir, tracks = items.Count,
                settings.LoudnessNormalize, settings.PeakLimiter,
                settings.TargetLufs, settings.TargetTruePeak, settings.VorbisQuality,
                settings.EncodeParallelism, xmaEncoder,
                runtime = Environment.Version.ToString(), processors = Environment.ProcessorCount });
            foreach (var tool in buildLog is null || !verbose ? Array.Empty<string>() : new[] { Tools.FfmpegPath, Tools.FsbankclPath, Tools.VgmstreamPath })
            {
                var info = FileVersionInfo.GetVersionInfo(tool);
                Diagnostic(buildLog, "build.tool", new { path = tool, info.FileVersion, info.ProductVersion,
                    bytes = new FileInfo(tool).Length, sha256 = HashRange(tool, 0, new FileInfo(tool).Length) });
            }
            var result = await BuildCoreAsync(sourcePath, stagedOutput, items, settings, skel, xmaEncoder.Available,
                workDir, buildLog, progress, verbose, useBackupSources)
                .ConfigureAwait(false);
            if (verbose) Diagnostic(buildLog, "build.publish.start", new { source = stagedOutput, destination = outPath });
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
        FevBank.BankSkeleton skel, bool xmaEncoderAvailable, string workDir, Action<string>? log,
        Action<string>? progress, bool verbose, bool useBackupSources)
    {
        var targetMode = TargetMode(skel.Mode, skel.Empty, xmaEncoderAvailable);
        var fmtArgs = FormatArgs(targetMode, settings.VorbisQuality);
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
        var parallelism = Math.Clamp(
            settings.EncodeParallelism > 0 ? settings.EncodeParallelism : AppSettings.RecommendedParallelism,
            1, Environment.ProcessorCount);
        if (verbose)
            Diagnostic(log, "bank.source", new { path = sourcePath, bytes = new FileInfo(sourcePath).Length,
                skel.Empty, sourceMode = skel.Mode, targetMode, sourceSamples = source?.Samples.Count ?? 0,
                outputSamples = plan.Count, parallelism });
        var prepared = new PreparedSample[plan.Count];
        var added = new AddedSample?[plan.Count];
        var done = 0;
        if (workCount > 0)
        {
            progress?.Invoke($"Preparing audio…\t0/{workCount}");
        }

        using var importedSources = new FsbSourceLocks();
        var copied = 0;

        var cacheDir = Path.Combine(workDir, "cache");
        Directory.CreateDirectory(cacheDir);
        using var sema = new SemaphoreSlim(parallelism);
        async Task PrepareOneAsync(int i)
        {
            var entry = plan[i];
            var item = entry.Item;
            var rawSource = entry.Source is null && !IsFsbSource(item.SourcePath);
            var effectiveSettings = rawSource ? NormalizationOverride.Resolve(settings, item.Normalization) : settings;
            var trackPrefix = log is null ? null : $"[track={i} sound={JsonSerializer.Serialize(item.SoundName)}] ";
            var failureContext = !verbose && log is not null ? new List<string>() : null;
            Action<string>? trackLog = log is null ? null : message =>
            {
                if (verbose || message.StartsWith("WARN", StringComparison.Ordinal)
                    || message.StartsWith("track.failed", StringComparison.Ordinal)) log(trackPrefix + message);
                else failureContext!.Add(message);
            };
            var detailLog = verbose ? trackLog : null;
            var trackTimer = Stopwatch.StartNew();
            Diagnostic(detailLog, "track.plan", new { item.DisplayName, item.Artist, item.SourcePath,
                item.IsNewCustom, item.IsReplacement, item.RestoreFromBak, item.Enabled, item.GainDb,
                item.MarkerSampleRate, item.Markers, item.Normalization, item.BankId, sourceBank = entry.Source?.Path, sourceIndex = entry.Index });
            Diagnostic(detailLog, "track.normalization", new { requestedOverride = item.Normalization,
                appliesToRawSource = rawSource,
                effective = rawSource ? new
                {
                    effectiveSettings.LoudnessNormalize, effectiveSettings.TargetLufs,
                    effectiveSettings.TargetTruePeak, effectiveSettings.PeakLimiter
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
                var sourceMode = entry.Source?.Skeleton.Mode;
                if (entry.Source is null && IsFsbSource(item.SourcePath))
                {
                    var importPath = item.SourcePath!;
                    if (useBackupSources)
                    {
                        importedSources.Acquire(importPath);
                    }
                    else
                    {
                        var snapshot = Path.Combine(workDir, $"sample_{i}.import.fsb");
                        using var input = File.OpenRead(importPath);
                        using (var output = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            await input.CopyToAsync(output).ConfigureAwait(false);
                        }
                        item = item with { SourcePath = snapshot };
                        Diagnostic(detailLog, "source.fsb.snapshot", new { sourcePath = importPath, path = snapshot,
                            bytes = new FileInfo(snapshot).Length });
                    }
                    sourceMode = FsbMode(item.SourcePath!);
                }
                if (entry.Source is null && sourceMode == targetMode)
                {
                    var passthrough = ReadPreparedSample(item.SourcePath!, targetMode);
                    prepared[i] = passthrough;
                    Diagnostic(detailLog, "track.passthrough", new { path = item.SourcePath,
                        passthrough.Frames, passthrough.SampleRate, passthrough.Channels });
                    trackLog?.Invoke("source.fsb.preserved: normalization and gain are not reapplied to encoded FSB imports.");
                    await ValidateFsbAsync(item.SourcePath!, passthrough, null, verifiedWav, trackLog,
                        measureLoudness: verbose).ConfigureAwait(false);
                    Interlocked.Increment(ref copied);
                }
                else
                {
                    var preserveNativeXma = sourceMode == 10 && targetMode == 15;
                    var preserveEncodedQuality = sourceMode is not null;
                    var encodingQuality = preserveEncodedQuality ? OriginalAudioQuality : settings.VorbisQuality;
                    var encodingArgs = FormatArgs(targetMode, encodingQuality);
                    PreparedSample? nativeImport = null;
                    Diagnostic(trackLog, "track.encoding", new { sourceMode, targetMode,
                        qualityPolicy = preserveEncodedQuality ? "encoded audio conversion" : "user settings",
                        requestedEncodingQuality = settings.VorbisQuality,
                        effectiveEncodingQuality = targetMode is 10 or 15 ? (int?) encodingQuality : null,
                        preserveNativeXma });
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
                            $"-i -W 4 -s 1 -o \"{wav}\" \"{sourceBank}\"", trackLog).ConfigureAwait(false);
                        if (!File.Exists(wav))
                        {
                            throw new InvalidOperationException($"vgmstream produced no audio for {item.SoundName}");
                        }
                    }
                    else if (preserveNativeXma)
                    {
                        nativeImport = ReadPreparedSample(item.SourcePath!, 10);
                        Diagnostic(trackLog, "source.fsb.native", new { path = item.SourcePath,
                            nativeImport.Frames, nativeImport.SampleRate, nativeImport.Channels,
                            processing = "native float decode; source level, rate, channels and timing preserved; user processing bypassed" });
                        await RunAsync(Tools.VgmstreamPath,
                            $"-i -W 4 -s 1 -o \"{wav}\" \"{item.SourcePath}\"", trackLog).ConfigureAwait(false);
                    }
                    else
                    {
                        await CreateInputWavAsync(item, wav, effectiveSettings, trackLog).ConfigureAwait(false);
                    }

                    var pcm = AudioBuildValidation.Inspect(wav);
                    Diagnostic(trackLog, "wav.prepared", pcm);
                    if (entry.Source is null && !preserveNativeXma && (pcm.SampleRate != 48000 || pcm.Channels != 2 || pcm.BitsPerSample != 16))
                    {
                        throw new InvalidDataException("Prepared input must be 48000 Hz stereo PCM16.");
                    }
                    if (nativeImport is { } imported && (pcm.Frames != imported.Frames
                        || pcm.SampleRate != imported.SampleRate || pcm.Channels != imported.Channels))
                    {
                        throw new InvalidDataException("Native XMA decoding changed its audio timing or channel count.");
                    }
                    var normalize = rawSource && effectiveSettings.LoudnessNormalize;
                    var loudnessTarget = AppSettings.ClampTargetLufs(effectiveSettings.TargetLufs) + (item.GainDb ?? 0);
                    prepared[i] = await EncodeValidatedAsync(wav, fsb, verifiedWav, cacheDir, encodingArgs, targetMode,
                        pcm, normalize ? AppSettings.ClampTargetTruePeak(effectiveSettings.TargetTruePeak) : null,
                        normalize ? loudnessTarget : null, trackLog).ConfigureAwait(false);
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
                progress?.Invoke($"Preparing audio…\t{completed}/{workCount}");
            }
            catch (Exception ex)
            {
                if (failureContext is { Count: > 0 })
                    log?.Invoke(trackPrefix + "diagnostics:" + Environment.NewLine + string.Join(Environment.NewLine, failureContext));
                Diagnostic(trackLog, "track.failed", new { item.SourcePath, elapsedMs = trackTimer.ElapsedMilliseconds, error = ex.ToString() });
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
        if (verbose)
            Diagnostic(log, "bank.assemble.start", new { samples = prepared.Length,
                audioBytes = prepared.Sum(p => p.Header.Length + p.Data.Length + (0x20 - p.Data.Length % 0x20) % 0x20), targetMode });
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
        if (verbose) log?.Invoke($"assembled bank: {prepared.Length} samples, FSB5 mode {targetMode}");
        ValidateBank(outPath, plan, prepared, targetMode, log, verbose);
        log?.Invoke($"bank audio: {copied} copied from FSB, {workCount - copied} encoded, {plan.Count - workCount} retained (mode {skel.Mode} -> {targetMode})");
        return added.OfType<AddedSample>().ToArray();
    }

    private static void Diagnostic(Action<string>? log, string stage, object details)
    {
        if (log is not null) log($"{stage} {JsonSerializer.Serialize(details, DiagnosticJson)}");
    }

    private static async Task<PreparedSample> EncodeValidatedAsync(string wavPath, string fsbPath, string decodedPath,
        string cacheDir, string formatArgs, int targetMode, AudioBuildInfo expected, double? peakCeiling,
        double? loudnessTarget, Action<string>? log)
    {
        var currentWav = wavPath;
        var currentFsb = fsbPath;
        var cumulativeGain = 0.0;
        var correctedWavs = new List<string>();
        try
        {
            for (var attempt = 0; attempt <= MaxPeakCorrections; attempt++)
            {
                await RunAsync(Tools.FsbankclPath,
                    $"{formatArgs} -o \"{currentFsb}\" \"{currentWav}\" -cache_dir \"{cacheDir}\"", log).ConfigureAwait(false);
                if (!File.Exists(currentFsb))
                {
                    throw new InvalidOperationException($"fsbankcl produced no output: {currentFsb}");
                }
                var prepared = ReadPreparedSample(currentFsb, targetMode);
                var measured = (await ValidateFsbAsync(currentFsb, prepared, currentWav, decodedPath, log, expected)
                    .ConfigureAwait(false))!;
                if (peakCeiling is not { } ceiling || measured.TruePeakDbtp is not { } peak
                    || peak + PeakReadingHalfStepDb <= ceiling)
                {
                    if (peakCeiling is not null)
                    {
                        var difference = measured.IntegratedLufs - loudnessTarget;
                        Diagnostic(log, "fsb.normalization.result", new { measured.IntegratedLufs, measured.TruePeakDbtp,
                            targetLufs = loudnessTarget, targetTruePeak = peakCeiling, loudnessDifferenceLu = difference,
                            loudnessToleranceLu = LoudnessToleranceLu,
                            withinLoudnessTolerance = difference is { } delta ? (bool?) (Math.Abs(delta) <= LoudnessToleranceLu) : null,
                            corrections = attempt, cumulativeGainDb = cumulativeGain });
                        Diagnostic(log, "fsb.peak_correction.ok", new { attempts = attempt,
                            integratedLufs = measured.IntegratedLufs, truePeakDbtp = measured.TruePeakDbtp,
                            targetTruePeak = peakCeiling });
                        if (difference > LoudnessToleranceLu)
                        {
                            log?.Invoke("WARN fsb.loudness_target: encoded audio is louder than its loudness target; inspect fsb.normalization.result.");
                        }
                        else if (difference < -LoudnessToleranceLu)
                        {
                            log?.Invoke("fsb.loudness.below_target: encoded audio is quieter than its loudness target; peak protection takes priority over loudness.");
                        }
                    }
                    return prepared;
                }
                if (attempt == MaxPeakCorrections)
                {
                    throw new InvalidDataException(
                        $"Encoded audio true peak {peak.ToString("0.0", CultureInfo.InvariantCulture)} dBTP " +
                        $"still exceeds the protected ceiling {ceiling.ToString("0.0", CultureInfo.InvariantCulture)} dBTP " +
                        $"after {MaxPeakCorrections} corrections.");
                }
                var attenuation = Math.Min(-EncodedPeakMarginDb, ceiling - peak - EncodedPeakMarginDb);
                cumulativeGain += attenuation;
                Diagnostic(log, "fsb.peak_correction", new { attempt = attempt + 1, inputTruePeak = peak,
                    targetTruePeak = ceiling, attenuationDb = attenuation, cumulativeGainDb = cumulativeGain,
                    marginDb = EncodedPeakMarginDb });
                currentWav = Path.ChangeExtension(wavPath, $"peakfix_{attempt + 1}.wav");
                currentFsb = Path.ChangeExtension(fsbPath, $"peakfix_{attempt + 1}.fsb");
                correctedWavs.Add(currentWav);
                var gain = cumulativeGain.ToString("0.############", CultureInfo.InvariantCulture);
                await RunAsync(Tools.FfmpegPath,
                    $"-y -hide_banner -nostdin -nostats -xerror -loglevel error -i \"{wavPath}\" -map 0:a:0 " +
                    $"-af \"volume={gain}dB:precision=double\" -c:a pcm_s16le \"{currentWav}\"", log).ConfigureAwait(false);
                expected = AudioBuildValidation.Inspect(currentWav);
                Diagnostic(log, "wav.peak_corrected", expected);
            }
            throw new InvalidOperationException("Encoded audio peak correction did not produce a result.");
        }
        finally
        {
            foreach (var corrected in correctedWavs) TryDelete(corrected);
        }
    }

    private static async Task<LoudnessMeasurement?> ValidateFsbAsync(string fsbPath, PreparedSample sample, string? expectedWav,
        string decodedPath, Action<string>? log, AudioBuildInfo? expected = null, bool measureLoudness = true)
    {
        var timer = Stopwatch.StartNew();
        var detailLog = measureLoudness ? log : null;
        if (detailLog is not null)
            Diagnostic(detailLog, "fsb.validate.start", new { path = fsbPath, bytes = new FileInfo(fsbPath).Length,
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
        await RunAsync(Tools.VgmstreamPath, $"-i -W 4 -s 1 -o \"{decodedPath}\" \"{fsbPath}\"", log).ConfigureAwait(false);
        var decoded = AudioBuildValidation.Inspect(decodedPath);
        Diagnostic(detailLog, "fsb.decoded", decoded);
        if (decoded.Frames != sample.Frames || decoded.SampleRate != sample.SampleRate || decoded.Channels != sample.Channels)
        {
            throw new InvalidDataException($"Decoded FSB {fsbPath} has {decoded.Frames} frames at {decoded.SampleRate} Hz, " +
                $"{decoded.Channels} channel(s); expected {sample.Frames} frames at {sample.SampleRate} Hz, {sample.Channels} channel(s).");
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
        if (!measureLoudness) return null;

        var loudness = await Loudnorm.MeasureOutputAsync(decodedPath, log).ConfigureAwait(false);
        if (loudness.Frames != decoded.Frames)
        {
            throw new InvalidDataException("FSB loudness analysis frame count differs from its decoded audio.");
        }
        Diagnostic(log, "fsb.loudness", new { integratedLufs = loudness.IntegratedLufs,
            truePeakDbtp = loudness.TruePeakDbtp, frames = loudness.Frames });
        Diagnostic(log, "fsb.validate.ok", new { elapsedMs = timer.ElapsedMilliseconds });
        return loudness;
    }

    private static void ValidateBank(string path, IReadOnlyList<PlannedSample> plan,
        IReadOnlyList<PreparedSample> prepared, int targetMode, Action<string>? log, bool verbose)
    {
        var timer = Stopwatch.StartNew();
        if (verbose)
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
            if (verbose)
                Diagnostic(log, "bank.sample.ok", new { index = i, plan[i].Item.SoundName, id = $"0x{id:x16}",
                    stored.DataOff, stored.DataLen, stored.Frames, stored.SampleRate, stored.Channels, dataSha256 = actualHash });
        }
        if (verbose)
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

    internal static int FsbMode(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> header = stackalloc byte[28];
        fs.ReadExactly(header);
        if (!header[..4].SequenceEqual("FSB5"u8)) throw new InvalidDataException("not an FSB5 block");
        return checked((int) BinaryPrimitives.ReadUInt32LittleEndian(header[24..]));
    }

    internal static int TargetMode(int sourceMode, bool empty, bool xmaEncoderAvailable)
        => empty || sourceMode == 10 && !xmaEncoderAvailable ? 15 : sourceMode;

    private static string FormatArgs(int mode, int vorbisQuality) => mode switch
    {
        10 => $"-format xma -quality {vorbisQuality.ToString(CultureInfo.InvariantCulture)} -no_loop",
        15 => $"-format vorbis -quality {vorbisQuality.ToString(CultureInfo.InvariantCulture)}",
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

    private sealed class FsbSourceLocks : IDisposable
    {
        private readonly Dictionary<string, FileStream> _files = new(StringComparer.OrdinalIgnoreCase);

        public void Acquire(string path)
        {
            var fullPath = Path.GetFullPath(path);
            lock (_files)
            {
                if (!_files.ContainsKey(fullPath)) _files.Add(fullPath, File.OpenRead(fullPath));
            }
        }

        public void Dispose()
        {
            foreach (var stream in _files.Values) stream.Dispose();
        }
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
        var failedBuild = string.Equals(exe, Tools.FsbankclPath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            && (outText.Contains("Build failed:", StringComparison.OrdinalIgnoreCase)
                || errText.Contains("Build failed:", StringComparison.OrdinalIgnoreCase));
        if (p.ExitCode != 0 || failedBuild)
        {
            var detail = errText.Length > 0 ? errText : outText;
            throw new InvalidOperationException(
                $"{Path.GetFileName(exe)} {(failedBuild ? "reported a failed build" : $"exited with code {p.ExitCode}")}" +
                (detail.Length > 0 ? $": {detail}" : ""));
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
                processing = "decode and resample; saved normalization and manual gain are not reapplied; fixed-point headroom is protected" });
            var decoded = Path.ChangeExtension(wav, ".decoded.wav");
            var resampled = Path.ChangeExtension(wav, ".float.wav");
            try
            {
                await RunAsync(Tools.VgmstreamPath,
                    $"-i -W 4 -s 1 -o \"{decoded}\" \"{item.SourcePath}\"", log).ConfigureAwait(false);
                if (!File.Exists(decoded))
                {
                    throw new InvalidOperationException($"vgmstream produced no audio for {item.SoundName}");
                }
                await RunAsync(Tools.FfmpegPath,
                    $"-y -hide_banner -nostdin -nostats -xerror -loglevel error -i \"{decoded}\" -map 0:a:0 " +
                    $"-af \"aresample=48000:osf=dbl:ochl=stereo:rematrix_maxval=1\" -c:a pcm_f32le \"{resampled}\"", log)
                    .ConfigureAwait(false);
                var pcm = AudioBuildValidation.Inspect(resampled);
                var peak = pcm.PeakDb.Max();
                var gain = Math.Min(0, -EncodedPeakMarginDb - peak);
                Diagnostic(log, "source.fsb.headroom", new { inputSamplePeak = peak, gainDb = gain,
                    ceilingDb = -EncodedPeakMarginDb, pcm.Frames, pcm.SampleRate, pcm.Channels });
                var gainText = gain.ToString("0.############", CultureInfo.InvariantCulture);
                await RunAsync(Tools.FfmpegPath,
                    $"-y -hide_banner -nostdin -nostats -xerror -loglevel error -i \"{resampled}\" -map 0:a:0 " +
                    $"-af \"volume={gainText}dB:precision=double\" -c:a pcm_s16le \"{wav}\"", log)
                    .ConfigureAwait(false);
                if (!File.Exists(wav))
                {
                    throw new InvalidOperationException($"ffmpeg produced no audio for {item.SoundName}");
                }
            }
            finally
            {
                TryDelete(decoded);
                TryDelete(resampled);
            }
            return;
        }
        var sourcePath = item.SourcePath;
        Diagnostic(log, "wav.encode.start", new { sourcePath, normalize = settings.LoudnessNormalize,
            settings.PeakLimiter,
            settings.TargetLufs, settings.TargetTruePeak, item.GainDb,
            requestedOverride = item.Normalization });
        await Loudnorm.RenderAsync(sourcePath, wav, settings, item.GainDb ?? 0, log).ConfigureAwait(false);
    }
}
