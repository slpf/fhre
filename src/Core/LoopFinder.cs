namespace FH6RB.Core;

public readonly struct LoopPair
{
    public long LoopStart { get; init; }
    public long LoopEnd { get; init; }
    public float NoteDistance { get; init; }
    public float LoudnessDifference { get; init; }
    public float Score { get; init; }
    public string? Source { get; init; }
}

public enum LoopRole { Generic, Track, Post }

[Flags]
public enum LoopStage
{
    None = 0,
    BorderFilter = 1 << 0,
    SmoothnessFilter = 1 << 1,
    FluxFilter = 1 << 2,
    XCorr = 1 << 3,
    ZeroCrossingSnap = 1 << 4,
    Cyclicity = 1 << 5,
    Phase = 1 << 6,
    BarSnap = 1 << 7,
    PhraseSnap = 1 << 8,
    All = BorderFilter | SmoothnessFilter | FluxFilter | XCorr | ZeroCrossingSnap | Cyclicity | Phase,
}

public sealed record LoopSearchOptions
{
    public double MinDurationMultiplier { get; init; } = 0.35;
    public double? MinLoopSeconds { get; init; }
    public double? MaxLoopSeconds { get; init; }
    public int MaxResults { get; init; } = 10;
    public LoopRole Role { get; init; } = LoopRole.Generic;
    public bool AutoTune { get; init; } = true;

    public double NoteDeviation { get; init; } = 0.0875;
    public double LoudnessDifference { get; init; } = 0.4;
    public double GateScale { get; init; } = 1.0;
    public int PreRollFrames { get; init; } = 1;
    public bool DisablePruning { get; init; } = false;
    public bool PreEmphasis { get; init; } = false;
    public bool MultiResolution { get; init; } = false;
    public double BorderSimilarityThreshold { get; init; } = 0.3;
    public int RefinePasses { get; init; } = 3;
    public bool RequireOnsetAlignment { get; init; } = true;
    public double TransitionSmoothnessThreshold { get; init; } = 0.3;
    public double TimbreWeight { get; init; } = 0.2;
    public double SectionWeight { get; init; } = 0.1;
    public double ChromaPeakThreshold { get; init; } = 0.0;
    public double VadThreshold { get; init; } = 0.0;
    public bool RequireVocalPhrase { get; init; } = false;
    public double PhaseContinuityWeight { get; init; } = 0.15;
    public LoopStage Stages { get; init; } = LoopStage.All;
    public bool UseHarmonicChroma { get; init; } = false;
    public bool UseAutocorrOffset { get; init; } = true;
    public bool UseSsmNomination { get; init; } = true;
    public bool UseRhythmNomination { get; init; } = true;

    public static LoopSearchOptions Default { get; } = new();
}

public static class LoopSearchDefaults
{
    public const double MinLoopSeconds = 15.0;

    public const LoopStage AutoStages =
        LoopStage.BorderFilter | LoopStage.SmoothnessFilter | LoopStage.XCorr |
        LoopStage.Cyclicity | LoopStage.Phase | LoopStage.PhraseSnap;
}

public static class LoopFinder
{
    private const int NFft = 4096;
    private const int Hop = 512;
    private const int Bins = NFft / 2 + 1;
    private const int NChroma = 12;

    private sealed record Analysis(
        List<Cand> Candidates, double Bpm, int[] Sections, int[] Bars, int NFrames,
        int TrimOffset, int OriginalLength, double SectionWeight);

    // Cached per role-independent key: the heavy search runs once; Track/Post only
    // re-rank the shared candidate set.
    private const int CacheCap = 32;
    private static readonly object _cacheLock = new();
    private static readonly LinkedList<(string Path, LoopSearchOptions Options)> _cacheOrder = new();
    private static readonly Dictionary<(string Path, LoopSearchOptions Options), LinkedListNode<(string Path, LoopSearchOptions Options)>> _cacheNodes = new();
    private static readonly Dictionary<(string Path, LoopSearchOptions Options), Analysis> _cache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, double[][]> _chromaFilterbanks = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int Rate, int NMels), double[][]> _melFilterbanks = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<double[], double[]> _reversedWeights = new();

    private static LoopSearchOptions CacheKeyOptions(LoopSearchOptions o)
    {
        if (!o.AutoTune) return o with { Role = LoopRole.Generic };
        return o with
        {
            Role = LoopRole.Generic,
            NoteDeviation = 0,
            LoudnessDifference = 0,
            TimbreWeight = 0,
            SectionWeight = 0,
            PhaseContinuityWeight = 0,
        };
    }

    private static double[] ReversedWeights(double[] weights)
        => _reversedWeights.GetValue(weights, static w =>
        {
            var r = (double[]) w.Clone();
            Array.Reverse(r);
            return r;
        });

    internal sealed record LoopAutoProfile
    {
        public double NoteDeviation { get; init; }
        public double LoudnessTolerance { get; init; }
        public double TimbreWeight { get; init; }
        public double SectionWeight { get; init; }
        public double PhaseContinuityWeight { get; init; }

        public static bool IsPercussive(float[] onset, int nFrames, int rate)
            => ComputeOnsetDensity(onset, nFrames, rate) > 1.5;

        public static LoopAutoProfile Derive(float[][] chroma, float[] onsetEnvelope,
            double[] loudness, int[] beats, int[] sections, double bpm, int nFrames, int rate)
        {
            var chromaVar = ComputeChromaVariance(chroma);
            var onsetDensity = ComputeOnsetDensity(onsetEnvelope, nFrames, rate);
            var dynamicRange = ComputeDynamicRange(loudness);
            var percussive = onsetDensity > 1.5;

            static double C01(double x) => Math.Clamp(x, 0.0, 1.0);

            var noteDev = Math.Clamp(0.07 + 0.02 * C01((chromaVar - 0.15) / 0.35), 0.06, 0.10);
            var loudTol = Math.Clamp(0.25 + 0.15 * C01((dynamicRange - 8.0) / 7.0), 0.20, 0.45);
            var timbreW = percussive
                ? Math.Clamp(0.20 - 0.15 * C01((onsetDensity - 1.5) / 2.0), 0.03, 0.20)
                : 0.20;
            var sectionW = Math.Clamp(0.10 + 0.10 * C01((sections.Length - 2) / 6.0), 0.05, 0.22);
            var phaseW = percussive ? 0.10 : 0.18;

            return new LoopAutoProfile
            {
                NoteDeviation = noteDev,
                LoudnessTolerance = loudTol,
                TimbreWeight = timbreW,
                SectionWeight = sectionW,
                PhaseContinuityWeight = phaseW,
            };
        }

        private static double ComputeChromaVariance(float[][] chroma)
        {
            if (chroma.Length < 4) return 0.2;
            var step = Math.Max(1, chroma.Length / 200);
            var dists = new List<double>(chroma.Length / step + 1);
            for (var f = step; f < chroma.Length; f += step)
            {
                dists.Add(NormDiff(chroma[f], chroma[f - step]));
            }
            if (dists.Count == 0) return 0.2;
            dists.Sort();
            return dists[dists.Count / 2];
        }

        private static double ComputeOnsetDensity(float[] onset, int nFrames, int rate)
        {
            if (nFrames == 0 || rate <= 0) return 0;
            var totalSec = (double) nFrames * Hop / rate;
            if (totalSec <= 0) return 0;
            var count = 0;
            for (var f = 0; f < nFrames && f < onset.Length; f++)
            {
                if (onset[f] > 0.05) count++;
            }
            return count / totalSec;
        }

        private static double ComputeDynamicRange(double[] loudness)
        {
            if (loudness.Length < 4) return 10;
            return Percentile(loudness, 95) - Percentile(loudness, 10);
        }
    }

    private static double MeanVad(float[] vad, int nFrames)
    {
        if (vad.Length == 0) return 0;
        var lim = Math.Min(nFrames, vad.Length);
        double sum = 0;
        for (var f = 0; f < lim; f++) sum += vad[f];
        return lim > 0 ? sum / lim : 0;
    }

    public static List<LoopPair> Find(float[]? mono, int rate, LoopSearchOptions options, string? cacheKey = null, Action<string>? log = null, float[]? vocalStem = null, CancellationToken ct = default)
    {
        var result = new List<LoopPair>();
        try
        {
            if (mono is null || mono.Length < NFft * 4 || rate <= 0 || options is null)
            {
                return result;
            }

            if (cacheKey is not null && GetCached((cacheKey, CacheKeyOptions(options))) is { } cached)
            {
                RankAndAppend(PreProcess(mono, out _), rate, cached, options, result, log);
                return result;
            }

            var originalLength = mono.Length;
            mono = PreProcess(mono, out var trimOffset);
            if (mono.Length < NFft * 4)
            {
                return result;
            }

            var total = mono.Length;
            var totalSec = (double) total / rate;
            var minLoopBaseSec = options.MinLoopSeconds
                ?? options.MinDurationMultiplier * totalSec;
            var maxLoopBaseSec = options.MaxLoopSeconds ?? totalSec;
    
            var power = Stft(mono, options.PreEmphasis);
            var nFrames = power.Length;
            if (nFrames < 8)
            {
                return result;
            }
    
            var useVocalStem = vocalStem != null && vocalStem.Length >= NFft * 4;
            float[][]? vocalPower = null;

            var onsetEnv = OnsetEnvelope(power, rate);
    
            var autoHarmonic = false;
            var autoVadThreshold = 0.0;
            var autoRequireVocalPhrase = false;
            float[]? vadScores = null;
            if (options.AutoTune)
            {
                autoHarmonic = LoopAutoProfile.IsPercussive(onsetEnv, nFrames, rate);
                vocalPower ??= useVocalStem ? Stft(vocalStem!, false) : power;
                vadScores = DetectVocalActivity(vocalPower, rate);
                if (MeanVad(vadScores, nFrames) > 0.45)
                {
                    autoVadThreshold = 0.4;
                    autoRequireVocalPhrase = true;
                }
            }
            else if (options.VadThreshold > 0)
            {
                vocalPower ??= useVocalStem ? Stft(vocalStem!, false) : power;
                vadScores = DetectVocalActivity(vocalPower, rate);
            }
    
            var useHarmonic = options.UseHarmonicChroma || autoHarmonic;
            var vadThreshold = options.VadThreshold > 0 ? options.VadThreshold : autoVadThreshold;
            var requireVocalPhrase = options.RequireVocalPhrase || autoRequireVocalPhrase;
            var useSsm = options.UseSsmNomination;
            var useRhythm = options.UseRhythmNomination;
    
            var chromaSource = useHarmonic ? HarmonicComponent(power, rate) : power;
            var chromaFull = Chroma(chromaSource, rate, vadScores, options.ChromaPeakThreshold, vadThreshold);
            var chromaLow = ChromaLowBand(chromaSource, rate, vadScores, options.ChromaPeakThreshold, vadThreshold);
            var chromaCombined = BlendChroma(chromaFull, chromaLow, fullWeight: 0.7, lowWeight: 0.3);
            var tuningOffset = DetectTuningOffset(power, rate);
            var chroma = Math.Abs(tuningOffset) > 0.05
                ? RotateChroma(chromaCombined, -tuningOffset)
                : chromaCombined;
            var loudness = Loudness(power, rate);
    
            var (bpm, beats) = DetectBeats(power, rate, onsetEnv);
    
            var stride = CoarseStride(nFrames);
            var coarseAnchors = StridedFrames(nFrames, stride);
            var anchors = beats.Length >= 2 ? UnionSorted(beats, coarseAnchors) : coarseAnchors;
            var sections = options.SectionWeight > 0 ? DetectSections(chroma, beats, nFrames, rate) : [];
            if (sections.Length > 0)
            {
                anchors = UnionSorted(anchors, sections);
            }
            if (requireVocalPhrase && vadScores != null)
            {
                var phrases = DetectVocalPhrases(vadScores, vadThreshold, nFrames, rate);
                if (phrases.Length >= 2)
                {
                    var phrasePoints = new List<int>(phrases.Length / 2 + 1);
                    for (var i = 0; i < phrases.Length; i += 2)
                    {
                        phrasePoints.Add(phrases[i]);
                    }
                    anchors = UnionSorted(anchors, phrasePoints.ToArray());
                }
            }
    
            LoopAutoProfile? profile = null;
            LoopSearchOptions effOptions = options;
            if (options.AutoTune)
            {
                profile = LoopAutoProfile.Derive(chroma, onsetEnv, loudness, beats, sections,
                    bpm, nFrames, rate);
                effOptions = options with
                {
                    NoteDeviation = profile.NoteDeviation,
                    LoudnessDifference = profile.LoudnessTolerance,
                    TimbreWeight = profile.TimbreWeight,
                    SectionWeight = profile.SectionWeight,
                    PhaseContinuityWeight = profile.PhaseContinuityWeight,
                    UseHarmonicChroma = useHarmonic,
                    VadThreshold = vadThreshold,
                    RequireVocalPhrase = requireVocalPhrase,
                    UseSsmNomination = useSsm,
                };
            }
            var effNoteDev = effOptions.NoteDeviation;
            var mfcc = effOptions.TimbreWeight > 0 ? Mfcc(power, rate) : null;
    
            if (anchors.Length < 2)
            {
                return result;
            }
    
            var testOffset = TestOffset(nFrames, rate, bpm, chroma, options.UseAutocorrOffset);
            var weights = Weights(testOffset, Math.Max(2, testOffset / 12), 1);
    
            void LogLine(string s) => log?.Invoke(s);
    
            LogLine($"[LoopFinder] rate={rate} dur={totalSec:0.0}s frames={nFrames} role={options.Role} auto={options.AutoTune} hps={useHarmonic} autoOffset={options.UseAutocorrOffset} ssm={useSsm} rhythm={useRhythm} vad={vadThreshold:0.00} phrase={requireVocalPhrase} vocal={(useVocalStem ? "ml" : "mix")}");
            LogLine($"  bpm={bpm:0.0} tuning={tuningOffset:0.000} sections={sections.Length} anchors={anchors.Length} testOffset={testOffset}");
            LogLine($"  noteDev={effNoteDev:0.000} loudDiff={effOptions.LoudnessDifference:0.00} minLoop={minLoopBaseSec:0.0}s maxLoop={maxLoopBaseSec:0.0}s");
            LogLine($"  border>={effOptions.BorderSimilarityThreshold:0.00} smooth>={effOptions.TransitionSmoothnessThreshold:0.00} onset={effOptions.RequireOnsetAlignment} timbre={effOptions.TimbreWeight:0.00} section={effOptions.SectionWeight:0.00} phase={effOptions.PhaseContinuityWeight:0.00} prune={!effOptions.DisablePruning}");
    
            var passSpecs = new List<(int MinLoop, int MaxLoop)>();
            var baseMinLoop = Math.Max(1, SecondsToFrames(minLoopBaseSec, rate));
            var baseMaxLoop = SecondsToFrames(maxLoopBaseSec, rate);
            passSpecs.Add((baseMinLoop, baseMaxLoop));
    
            if (effOptions.MultiResolution && minLoopBaseSec / 2 >= 1)
            {
                passSpecs.Add((Math.Max(1, SecondsToFrames(minLoopBaseSec / 2, rate)), baseMaxLoop));
                if (maxLoopBaseSec < totalSec)
                {
                    passSpecs.Add((baseMinLoop, SecondsToFrames(Math.Min(totalSec, maxLoopBaseSec * 2), rate)));
                }
            }
    
            var allCandidates = new List<Cand>();

            List<Cand>? ssmCands = null;
            if (useSsm || useRhythm)
            {
                var ds = Math.Max(2, (int) Math.Round(0.1 * rate / Hop));
                if (nFrames / ds >= 16)
                {
                    var winFrames = Math.Clamp((int) Math.Round(2.0 * rate / Hop / ds), 2, Math.Max(2, nFrames / ds / 4));
                    var mfccCands = useSsm
                        ? FindSsmCandidates(Mfcc(AggregatePower(power, ds), rate), baseMinLoop, baseMaxLoop, ds, nFrames, rate, SrcSsm)
                        : new List<Cand>();
                    var rhythmCands = useRhythm
                        ? FindSsmCandidates(OnsetWindowFeat(onsetEnv, ds, winFrames), baseMinLoop, baseMaxLoop, ds, nFrames, rate, SrcRhythm)
                        : new List<Cand>();
                    ssmCands = new List<Cand>(mfccCands.Count + rhythmCands.Count);
                    ssmCands.AddRange(mfccCands);
                    ssmCands.AddRange(rhythmCands);
                    LogLine($"  nominators -> ssm:{mfccCands.Count} rhythm:{rhythmCands.Count}");
                }
            }

            int[]? phraseOnsets = null;
            if ((effOptions.Stages & LoopStage.PhraseSnap) != 0)
            {
                var snapVad = vadScores ?? DetectVocalActivity(power, rate);
                var snapThresh = vadThreshold > 0 ? vadThreshold : 0.4;
                var phrases = DetectVocalPhrases(snapVad, snapThresh, nFrames, rate);
                if (phrases.Length >= 4)
                {
                    phraseOnsets = new int[phrases.Length / 2];
                    for (var i = 0; i < phraseOnsets.Length; i++) phraseOnsets[i] = phrases[i * 2];
                }
            }

            var ssmAdded = false;
            foreach (var (minLoop, maxLoop) in passSpecs)
            {
                if (ct.IsCancellationRequested) break;
                var candidates = FindCandidatePairs(
                    chroma, loudness, anchors, minLoop, maxLoop,
                    effOptions.GateScale, effOptions.PreRollFrames,
                    effNoteDev, effOptions.LoudnessDifference);
                if (!ssmAdded && ssmCands is { Count: > 0 })
                {
                    candidates.AddRange(ssmCands);
                    ssmAdded = true;
                    MarkCrossValidated(candidates, rate);
                }
                LogLine($"pass [{minLoop}-{maxLoop}]: gate -> {candidates.Count}");
                if (candidates.Count == 0)
                {
                    continue;
                }
    
                Assess(chroma, candidates, testOffset, weights, effOptions.DisablePruning);
                LogLine($"  assess/prune -> {candidates.Count}");
    
                if (effOptions.RefinePasses >= 1 && stride > 1)
                {
                    Refine(candidates, chroma, loudness, testOffset, weights, stride, minLoop, maxLoop, nFrames);
                }
                if (effOptions.RefinePasses >= 2)
                {
                    RefineHop(candidates, chroma, loudness, testOffset, weights, minLoop, maxLoop, nFrames);
                }
                if (effOptions.RefinePasses >= 3)
                {
                    RefineSub(candidates, chroma, loudness, testOffset, weights, nFrames);
                }
    
                if ((effOptions.Stages & LoopStage.XCorr) != 0)
                {
                    RefineXCorr(candidates, mono, rate);
                }

                if ((effOptions.Stages & LoopStage.PhraseSnap) != 0 && phraseOnsets is { Length: >= 2 })
                {
                    RefinePhraseSnap(candidates, mono, phraseOnsets, nFrames, rate);
                }
    
                var timbreSignal = (mfcc is not null && effOptions.TimbreWeight > 0) ? mfcc : chroma;
                var timbreEnabled = ReferenceEquals(timbreSignal, mfcc);
                foreach (var c in candidates)
                {
                    var len = c.EndFrame - c.StartFrame;
                    var chromaSeam = c.FrameAdjusted
                        ? LoopScore(c.StartFrame, c.EndFrame, chroma, testOffset, weights)
                        : c.Score;
                    var quality = chromaSeam;
                    var w = 1.0;
    
                    var timbre = -1.0;
                    if (timbreEnabled)
                    {
                        timbre = LoopScore(c.StartFrame, c.EndFrame, timbreSignal, testOffset, weights);
                        var tw = 3.0 * effOptions.TimbreWeight;
                        quality += tw * timbre;
                        w += tw;
                    }
    
                    var b3 = 2 * c.EndFrame - c.StartFrame;
                    if ((effOptions.Stages & LoopStage.Cyclicity) != 0
                        && b3 > c.EndFrame && b3 + len < nFrames
                        && chromaSeam >= 0.4 && (!timbreEnabled || timbre >= 0.3))
                    {
                        var cyc = LoopScore(c.EndFrame, b3, timbreSignal, testOffset, weights);
                        quality += 0.3 * cyc;
                        w += 0.3;
                    }
    
                    if ((effOptions.Stages & LoopStage.Phase) != 0 && effOptions.PhaseContinuityWeight > 0)
                    {
                        var phase = PhaseContinuity(mono, c.StartFrame, c.EndFrame);
                        quality += effOptions.PhaseContinuityWeight * phase;
                        w += effOptions.PhaseContinuityWeight;
                    }
    
                    quality /= w;
    
                    if (timbre >= 0)
                    {
                        var gap = chromaSeam - timbre;
                        if (gap > 0.15)
                        {
                            quality -= 0.25 * (gap - 0.15);
                        }
                    }

                    var srcBits = c.Sources;
                    var srcCount = ((srcBits & 1) != 0 ? 1 : 0) + ((srcBits & 2) != 0 ? 1 : 0) + ((srcBits & 4) != 0 ? 1 : 0);
                    if (srcCount > 1) quality += 0.04 * (srcCount - 1);

                    c.Score = Math.Clamp(quality, 0.0, 1.0);
                }
    
                if ((effOptions.Stages & LoopStage.BorderFilter) != 0 && effOptions.BorderSimilarityThreshold > 0)
                {
                    var snapshot = new List<Cand>(candidates);
                    BorderSimilarityFilter(candidates, chroma, rate, effOptions.BorderSimilarityThreshold);
                    if (candidates.Count == 0)
                    {
                        candidates.AddRange(snapshot);
                    }
    
                    LogLine($"  border>={effOptions.BorderSimilarityThreshold:0.00} -> {candidates.Count}");
                }
    
                if (effOptions.RequireOnsetAlignment && beats.Length >= 2)
                {
                    var snapshot = new List<Cand>(candidates);
                    candidates.RemoveAll(c => !HasAtLeastTwoBeatsInRange(beats, c.StartFrame, c.EndFrame));
                    if (candidates.Count == 0)
                    {
                        candidates.AddRange(snapshot);
                    }
    
                    LogLine($"  onset -> {candidates.Count}");
                }
    
                if (effOptions.TransitionSmoothnessThreshold > 0)
                {
                    if ((effOptions.Stages & LoopStage.SmoothnessFilter) != 0)
                    {
                        var snapshot = new List<Cand>(candidates);
                        TransitionSmoothnessFilter(candidates, mono, rate, effOptions.TransitionSmoothnessThreshold);
                        if (candidates.Count == 0)
                        {
                            candidates.AddRange(snapshot);
                        }
    
                        LogLine($"  smooth>={effOptions.TransitionSmoothnessThreshold:0.00} -> {candidates.Count}");
                    }
    
                    if ((effOptions.Stages & LoopStage.FluxFilter) != 0)
                    {
                        var fluxSnapshot = new List<Cand>(candidates);
                        SpectralFluxFilter(candidates, power, rate, effOptions.TransitionSmoothnessThreshold);
                        if (candidates.Count == 0)
                        {
                            candidates.AddRange(fluxSnapshot);
                        }
    
                        LogLine($"  flux<{effOptions.TransitionSmoothnessThreshold:0.00} -> {candidates.Count}");
                    }
                }
    
                allCandidates.AddRange(candidates);
            }
    
            allCandidates.Sort((a, b) => b.Score.CompareTo(a.Score));

            int[] bars = [];
            if (bpm > 1 && beats.Length >= 1)
            {
                var barLen = 4.0 * 60.0 / bpm * rate / Hop;
                if (barLen > 1)
                {
                    var barList = new List<int>();
                    for (var b = beats[0]; b < nFrames; b += (int) barLen)
                    {
                        barList.Add(b);
                    }
                    bars = barList.ToArray();
                }
            }

            var analysis = new Analysis(allCandidates, bpm, sections, bars, nFrames, trimOffset, originalLength, effOptions.SectionWeight);
            if (cacheKey is not null)
            {
                SetCache((cacheKey, CacheKeyOptions(options)), analysis);
            }
    
            RankAndAppend(mono, rate, analysis, options, result, log);
    
            return result;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[LoopFinder] unexpected error: {ex.Message}");
            return result;
        }
    }

    public static List<LoopPair> Find(float[]? mono, int rate,
        double minDurationMultiplier = 0.35, double? minLoopSeconds = null, double? maxLoopSeconds = null,
        int maxResults = 10, string? cacheKey = null)
    {
        return Find(mono, rate, new LoopSearchOptions
        {
            MinDurationMultiplier = minDurationMultiplier,
            MinLoopSeconds = minLoopSeconds,
            MaxLoopSeconds = maxLoopSeconds,
            MaxResults = maxResults,
        }, cacheKey);
    }

    private static void RankAndAppend(float[] mono, int rate, Analysis analysis,
        LoopSearchOptions options, List<LoopPair> result, Action<string>? log)
    {
        void LogLine(string s) => log?.Invoke(s);

        var candidates = new List<Cand>(analysis.Candidates);
        if (candidates.Count > 1)
        {
            if (options.Role == LoopRole.Generic)
            {
                PrioritizeDuration(candidates);
            }
            else
            {
                RankForRole(candidates, options.Role, analysis.NFrames, analysis.Bpm, rate,
                    analysis.Sections, analysis.SectionWeight);
            }
        }

        var topN = Math.Min(12, candidates.Count);
        for (var i = 0; i < topN; i++)
        {
            var c = candidates[i];
            LogLine($"  cand#{i} {(double) (c.StartFrame * Hop) / rate:0.00}s -> {(double) (c.EndFrame * Hop) / rate:0.00}s len={(double) ((c.EndFrame - c.StartFrame) * Hop) / rate:0.0}s score={c.Score:0.000} src={SourceLabel(c.Sources)}");
        }

        var barGrid = (options.Stages & LoopStage.BarSnap) != 0 ? analysis.Bars : null;
        var barTol = analysis.Bpm > 1 ? (int) Math.Round(0.5 * 60.0 / analysis.Bpm * rate) : 0;
        AppendLoops(mono, rate, candidates, result, options.MaxResults, analysis.TrimOffset, analysis.OriginalLength, (options.Stages & LoopStage.ZeroCrossingSnap) != 0, barGrid, barTol);

        LogLine($"final: {result.Count} loops");
        foreach (var lp in result)
        {
            LogLine($"  {(double) lp.LoopStart / rate:0.00}s -> {(double) lp.LoopEnd / rate:0.00}s len={(double) (lp.LoopEnd - lp.LoopStart) / rate:0.0}s score={lp.Score:0.000} src={lp.Source}");
        }
    }

    private static long SnapToBar(long sample, int[] barGrid, int tol)
    {
        var target = sample / Hop;
        var lo = 0;
        var hi = barGrid.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (barGrid[mid] < target) lo = mid + 1;
            else hi = mid;
        }
        long best = sample;
        long bestDist = tol;
        if (lo < barGrid.Length)
        {
            var bs = (long) barGrid[lo] * Hop;
            var d = bs - sample;
            if (d >= 0 && d <= bestDist) { bestDist = d; best = bs; }
        }
        if (lo > 0)
        {
            var bs = (long) barGrid[lo - 1] * Hop;
            var d = sample - bs;
            if (d >= 0 && d <= bestDist) { bestDist = d; best = bs; }
        }
        return best;
    }

    private static void AppendLoops(float[] mono, int rate, List<Cand> candidates,
        List<LoopPair> result, int maxResults, long trimOffset, long originalLength, bool snapToZeroCrossing, int[]? barGrid, int barTol)
    {
        var tol = (int) (1.0 * rate / Hop);
        var tolSamples = (long) tol * Hop;
        var clusterSamples = (long) (5.0 * rate);

        foreach (var p in candidates)
        {
            var startBase = (long) p.StartFrame * Hop;
            var endBase = (long) p.EndFrame * Hop;
            var sRaw = snapToZeroCrossing ? NearestZeroCrossing(mono, rate, startBase) : startBase;
            var eRaw = snapToZeroCrossing ? NearestZeroCrossing(mono, rate, endBase) : endBase;
            if (barGrid is { Length: > 0 } && barTol > 0)
            {
                sRaw = SnapToBar(sRaw, barGrid, barTol);
                eRaw = SnapToBar(eRaw, barGrid, barTol);
            }
            var s = Math.Clamp(sRaw + trimOffset, 0L, originalLength - 1);
            var e = Math.Clamp(eRaw + trimOffset, 0L, originalLength - 1);

            var dup = result.Any(q =>
            {
                if (Math.Abs(q.LoopStart - s) <= tolSamples && Math.Abs(q.LoopEnd - e) <= tolSamples)
                {
                    return true;
                }

                var lenQ = q.LoopEnd - q.LoopStart;
                var lenC = e - s;
                var similarLen = Math.Abs(lenQ - lenC) <= 0.20 * Math.Max(lenQ, lenC);
                return similarLen && Math.Abs(q.LoopStart - s) <= clusterSamples;
            });
            if (dup)
            {
                continue;
            }

            result.Add(new LoopPair
            {
                LoopStart = s,
                LoopEnd = e,
                NoteDistance = (float) p.NoteDistance,
                LoudnessDifference = (float) p.LoudnessDifference,
                Score = (float) p.Score,
                Source = SourceLabel(p.Sources),
            });

            if (result.Count >= maxResults)
            {
                break;
            }
        }
    }

    public static void ClearCache()
    {
        lock (_cacheLock)
        {
            _cache.Clear();
            _cacheNodes.Clear();
            _cacheOrder.Clear();
        }
    }

    private static Analysis? GetCached((string Path, LoopSearchOptions Options) key)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var analysis))
            {
                var node = _cacheNodes[key];
                _cacheOrder.Remove(node);
                _cacheOrder.AddFirst(node);
                return analysis;
            }

            return null;
        }
    }

    private static void SetCache((string Path, LoopSearchOptions Options) key, Analysis analysis)
    {
        lock (_cacheLock)
        {
            if (_cacheNodes.TryGetValue(key, out var existing))
            {
                _cacheOrder.Remove(existing);
            }

            _cacheNodes[key] = _cacheOrder.AddFirst(key);
            _cache[key] = analysis;

            while (_cache.Count > CacheCap)
            {
                var lru = _cacheOrder.Last!.Value;
                _cacheOrder.RemoveLast();
                _cacheNodes.Remove(lru);
                _cache.Remove(lru);
            }
        }
    }

    private const int SrcChroma = 1;
    private const int SrcSsm = 2;
    private const int SrcRhythm = 4;

    private static string SourceLabel(int sources)
    {
        var parts = new List<string>(3);
        if ((sources & SrcChroma) != 0) parts.Add("Chroma");
        if ((sources & SrcSsm) != 0) parts.Add("SSM");
        if ((sources & SrcRhythm) != 0) parts.Add("Rhythm");
        return parts.Count == 0 ? "" : string.Join(", ", parts);
    }

    private sealed class Cand
    {
        public int StartFrame;
        public int EndFrame;
        public double NoteDistance;
        public double LoudnessDifference;
        public double Score;
        public bool FrameAdjusted;
        public int Sources = SrcChroma;
    }

    private static float[][] HarmonicComponent(float[][] power, int rate)
    {
        var n = power.Length;
        if (n == 0) return power;
        var bins = power[0].Length;
        const int kernel = 17;
        var half = kernel / 2;
        var cutoffBin = Math.Min(bins, Math.Max(1, (int) (6000.0 * NFft / rate)));

        var harmonic = new float[n][];
        Parallel.For(0, n, t =>
        {
            var dst = new float[bins];
            var timeBuf = new float[kernel];
            var freqBuf = new float[kernel];
            for (var f = 0; f < cutoffBin; f++)
            {
                var k = 0;
                for (var d = -half; d <= half; d++)
                {
                    var tt = t + d;
                    timeBuf[k] = (tt >= 0 && tt < n) ? power[tt][f] : 0f;
                    k++;
                }
                Array.Sort(timeBuf);
                var hMed = timeBuf[half];

                k = 0;
                for (var d = -half; d <= half; d++)
                {
                    var ff = f + d;
                    freqBuf[k] = (ff >= 0 && ff < bins) ? power[t][ff] : 0f;
                    k++;
                }
                Array.Sort(freqBuf);
                var pMed = freqBuf[half];

                var denom = hMed + pMed;
                dst[f] = denom > 1e-12 ? power[t][f] * (hMed / denom) : power[t][f];
            }
            for (var f = cutoffBin; f < bins; f++)
            {
                dst[f] = power[t][f];
            }
            harmonic[t] = dst;
        });

        return harmonic;
    }

    private static int AutocorrOffset(float[][] chroma, int rate)
    {
        var n = chroma.Length;
        if (n < 16) return 0;

        var ds = Math.Max(1, (int) Math.Round(0.050 * rate / Hop));
        var m = n / ds;
        if (m < 16)
        {
            ds = Math.Max(1, n / 16);
            m = n / ds;
        }
        if (m < 8) return 0;

        var agg = new float[m][];
        for (var i = 0; i < m; i++)
        {
            var a = new float[NChroma];
            var cnt = 0;
            for (var k = 0; k < ds && i * ds + k < n; k++)
            {
                var c = chroma[i * ds + k];
                for (var j = 0; j < NChroma; j++) a[j] += c[j];
                cnt++;
            }
            if (cnt > 0)
            {
                var inv = 1f / cnt;
                for (var j = 0; j < NChroma; j++) a[j] *= inv;
            }
            agg[i] = a;
        }

        var framesPerSec = (double) rate / Hop / ds;
        var minLag = Math.Max(2, (int) Math.Round(0.8 * framesPerSec));
        var maxLag = Math.Min(m / 2, (int) Math.Round(12.0 * framesPerSec));
        if (maxLag <= minLag) return 0;

        var bestLag = minLag;
        var bestScore = double.NegativeInfinity;
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            double dotSum = 0;
            var cnt = 0;
            for (var t = 0; t + lag < m; t++)
            {
                dotSum += Cosine(agg[t], agg[t + lag]);
                cnt++;
            }
            if (cnt <= 0) continue;
            var score = dotSum / cnt;
            if (score > bestScore)
            {
                bestScore = score;
                bestLag = lag;
            }
        }

        return bestLag * ds;
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return dot / Math.Max(Math.Sqrt(na) * Math.Sqrt(nb), 1e-10);
    }

    private static float[][] AggregatePower(float[][] power, int ds)
    {
        var n = power.Length;
        var m = Math.Max(1, n / ds);
        var agg = new float[m][];
        for (var i = 0; i < m; i++)
        {
            var row = new float[Bins];
            var cnt = 0;
            for (var k = 0; k < ds && i * ds + k < n; k++)
            {
                var p = power[i * ds + k];
                for (var b = 0; b < Bins; b++) row[b] += p[b];
                cnt++;
            }
            if (cnt > 0)
            {
                var inv = 1f / cnt;
                for (var b = 0; b < Bins; b++) row[b] *= inv;
            }
            agg[i] = row;
        }
        return agg;
    }

    private static float[][] OnsetWindowFeat(float[] onset, int ds, int winFrames)
    {
        var m = Math.Max(1, onset.Length / ds);
        var agg = new float[m];
        for (var i = 0; i < m; i++)
        {
            float s = 0;
            var cnt = 0;
            for (var k = 0; k < ds && i * ds + k < onset.Length; k++)
            {
                s += onset[i * ds + k];
                cnt++;
            }
            agg[i] = cnt > 0 ? s / cnt : 0f;
        }
        var w = Math.Max(1, winFrames);
        var feat = new float[m][];
        for (var t = 0; t < m; t++)
        {
            var row = new float[2 * w + 1];
            for (var k = -w; k <= w; k++)
            {
                var idx = t + k;
                row[k + w] = (idx >= 0 && idx < m) ? agg[idx] : 0f;
            }
            feat[t] = row;
        }
        return feat;
    }

    private static List<Cand> FindSsmCandidates(float[][] feat, int minLoop, int maxLoop, int ds, int nFrames, int rate, int source)
    {
        var collected = new List<(int D, int BestI, double Score)>();
        var m = feat.Length;
        if (m < 16 || ds < 1) return new List<Cand>();

        var minD = Math.Max(1, minLoop / ds);
        var maxD = Math.Min(m - 2, maxLoop / ds);
        if (maxD <= minD) return new List<Cand>();

        var winFrames = Math.Clamp((int) Math.Round(2.0 * rate / Hop / ds), 2, Math.Max(2, m / 4));
        var dStep = Math.Max(1, (int) Math.Round(0.25 * rate / Hop / ds));
        var sim = new double[m];

        for (var d = minD; d <= maxD; d += dStep)
        {
            var span = m - d;
            if (span <= winFrames)
            {
                collected.Add((d, -1, 0));
                continue;
            }

            for (var i = 0; i < span; i++) sim[i] = Cosine(feat[i], feat[i + d]);

            double runSum = 0;
            for (var i = 0; i < winFrames; i++) runSum += sim[i];
            var bestI = 0;
            var bestScore = runSum / winFrames;
            for (var i = 1; i + winFrames <= span; i++)
            {
                runSum += sim[i + winFrames - 1] - sim[i - 1];
                var avg = runSum / winFrames;
                if (avg > bestScore)
                {
                    bestScore = avg;
                    bestI = i;
                }
            }
            collected.Add((d, bestI, bestScore));
        }

        if (collected.Count == 0) return new List<Cand>();

        var sortedScores = new double[collected.Count];
        for (var i = 0; i < collected.Count; i++) sortedScores[i] = collected[i].Score;
        Array.Sort(sortedScores);
        var baseline = sortedScores[sortedScores.Length / 2];
        var topScore = sortedScores[^1];
        var threshold = Math.Max(0.6, baseline + 0.5 * (topScore - baseline));

        var peaks = new List<(int D, int BestI, double Score)>();
        for (var idx = 0; idx < collected.Count; idx++)
        {
            var (d, bestI, sc) = collected[idx];
            if (bestI < 0 || sc < threshold) continue;
            var prevS = idx > 0 ? collected[idx - 1].Score : double.NegativeInfinity;
            var nextS = idx < collected.Count - 1 ? collected[idx + 1].Score : double.NegativeInfinity;
            if (sc >= prevS && sc >= nextS) peaks.Add((d, bestI, sc));
        }

        peaks.Sort((x, y) => y.Score.CompareTo(x.Score));
        var result = new List<Cand>();
        var take = Math.Min(40, peaks.Count);
        for (var k = 0; k < take; k++)
        {
            var (d, bestI, sc) = peaks[k];
            var start = bestI * ds;
            var end = (bestI + d) * ds;
            if (end < nFrames && end - start >= minLoop && end - start <= maxLoop)
            {
                result.Add(new Cand { StartFrame = start, EndFrame = end, Score = sc, Sources = source });
            }
        }
        return result;
    }

    private static void MarkCrossValidated(List<Cand> candidates, int rate)
    {
        var maxStart = (int) Math.Max(1, Math.Round(2.0 * rate / Hop));
        var n = candidates.Count;
        for (var i = 0; i < n; i++)
        {
            var a = candidates[i];
            var lenA = a.EndFrame - a.StartFrame;
            for (var j = i + 1; j < n; j++)
            {
                var b = candidates[j];
                if ((a.Sources & b.Sources) != 0) continue;
                var lenB = b.EndFrame - b.StartFrame;
                if (Math.Abs(lenA - lenB) > 0.10 * Math.Max(lenA, lenB)) continue;
                if (Math.Abs(a.StartFrame - b.StartFrame) > maxStart) continue;
                var merged = a.Sources | b.Sources;
                a.Sources = merged;
                b.Sources = merged;
            }
        }
    }

    private static int TestOffset(int nFrames, int rate, double bpm, float[][]? chroma, bool useAutocorr)
    {
        if (useAutocorr)
        {
            var minWin = Math.Max(1, (int) Math.Round(1.5 * rate / Hop));
            var maxWin = Math.Max(minWin, Math.Min(nFrames, (int) Math.Round(30.0 * rate / Hop)));

            if (chroma is { Length: >= 16 })
            {
                var auto = AutocorrOffset(chroma, rate);
                if (auto >= minWin)
                {
                    return Math.Clamp(auto, minWin, maxWin);
                }
            }

            var fallback = (int) Math.Round(8.0 * 60.0 / Math.Max(bpm, 30.0) * rate / Hop);
            return Math.Clamp(fallback, minWin, maxWin);
        }

        var beats = 12.0;
        if (chroma is { Length: >= 8 })
        {
            var step = Math.Max(1, chroma.Length / 100);
            double varSum = 0;
            var sampleCount = 0;
            var prev = chroma[0];
            for (var f = step; f < chroma.Length; f += step)
            {
                var c = chroma[f];
                double d = 0;
                for (var k = 0; k < NChroma; k++)
                {
                    var diff = prev[k] - c[k];
                    d += diff * diff;
                }
                varSum += d;
                sampleCount++;
                prev = c;
            }
            var avgVar = sampleCount > 0 ? varSum / sampleCount : 0;
            if (avgVar > 0.5) beats = 6;
            else if (avgVar < 0.1) beats = 24;
        }

        var frames = (int) Math.Round(beats * 60.0 / Math.Max(bpm, 30.0) * rate / Hop);
        var lo = Math.Max(1, nFrames / 4);
        return Math.Clamp(frames, lo, Math.Max(lo, nFrames));
    }

    private static int CoarseStride(int nFrames) => Math.Max(1, nFrames / 2500);

    private static int[] StridedFrames(int nFrames, int stride)
    {
        var list = new List<int>(nFrames / stride + 2);
        for (var f = 0; f < nFrames; f += stride)
        {
            list.Add(f);
        }

        if (list.Count == 0 || list[^1] != nFrames - 1)
        {
            list.Add(nFrames - 1);
        }

        return list.ToArray();
    }

    private static void Refine(List<Cand> list, float[][] chroma, double[] loudness, int testOffset, double[] weights,
        int stride, int minLoop, int maxLoop, int nFrames)
    {
        var topN = Math.Min(list.Count, 12);
        for (var idx = 0; idx < topN; idx++)
        {
            var c = list[idx];
            var bestS = c.StartFrame;
            var bestE = c.EndFrame;
            var bestScore = c.Score;

            for (var ds = -stride; ds <= stride; ds++)
            {
                var s = c.StartFrame + ds;
                if (s < 0 || s >= nFrames)
                {
                    continue;
                }

                for (var de = -stride; de <= stride; de++)
                {
                    var en = c.EndFrame + de;
                    if (en < 0 || en >= nFrames)
                    {
                        continue;
                    }

                    var len = en - s;
                    if (len < minLoop || len > maxLoop)
                    {
                        continue;
                    }

                    var sc = LoopScore(s, en, chroma, testOffset, weights);
                    if (sc > bestScore)
                    {
                        bestScore = sc;
                        bestS = s;
                        bestE = en;
                    }
                }
            }

            if (bestS != c.StartFrame || bestE != c.EndFrame)
            {
                c.StartFrame = bestS;
                c.EndFrame = bestE;
                c.Score = bestScore;
                c.NoteDistance = NormDiff(chroma[bestE], chroma[bestS]);
                c.LoudnessDifference = Math.Abs(loudness[bestE] - loudness[bestS]);
            }
        }

        list.Sort((a, b) => b.Score.CompareTo(a.Score));
    }

    private static List<Cand> FindCandidatePairs(float[][] chroma, double[] loudness, int[] beats,
        int minLoop, int maxLoop, double gateScale, int preRollFrames,
        double noteDeviation, double loudnessDifference)
    {
        var list = new List<Cand>();
        var w = Math.Max(0, preRollFrames);

        var averaged = new float[beats.Length][];
        var deviation = new double[beats.Length];
        for (var i = 0; i < beats.Length; i++)
        {
            averaged[i] = AverageChroma(chroma, beats[i], w);
            deviation[i] = Norm(averaged[i]) * noteDeviation * gateScale;
        }

        for (var idx = 0; idx < beats.Length; idx++)
        {
            if (deviation[idx] < 0.01) continue;
            var loopEnd = beats[idx];
            var endAvg = averaged[idx];
            for (var j = 0; j < beats.Length; j++)
            {
                var loopStart = beats[j];
                var len = loopEnd - loopStart;
                if (len < minLoop)
                {
                    break;
                }

                if (len > maxLoop)
                {
                    continue;
                }

                var noteDistance = NormDiff(endAvg, averaged[j]);
                if (noteDistance <= deviation[idx])
                {
                    var loud = Math.Abs(loudness[loopEnd] - loudness[loopStart]);
                    if (loud <= loudnessDifference)
                    {
                        list.Add(new Cand
                        {
                            StartFrame = loopStart,
                            EndFrame = loopEnd,
                            NoteDistance = noteDistance,
                            LoudnessDifference = loud,
                            Sources = SrcChroma,
                        });
                    }
                }
            }
        }

        return list;
    }

    private static float[] AverageChroma(float[][] chroma, int frame, int halfWindow)
    {
        if (halfWindow <= 0 || chroma.Length == 0)
        {
            return chroma[Math.Clamp(frame, 0, chroma.Length - 1)];
        }

        var lo = Math.Max(0, frame - halfWindow);
        var hi = Math.Min(chroma.Length - 1, frame + halfWindow);
        var n = hi - lo + 1;
        var avg = new float[NChroma];
        for (var f = lo; f <= hi; f++)
        {
            var c = chroma[f];
            for (var k = 0; k < NChroma; k++)
            {
                avg[k] += c[k];
            }
        }
        var inv = 1f / n;
        for (var k = 0; k < NChroma; k++)
        {
            avg[k] *= inv;
        }

        return avg;
    }

    private static void Assess(float[][] chroma, List<Cand> candidates, int testOffset, double[] weights,
        bool disablePruning)
    {
        var pool = candidates;

        if (!disablePruning && candidates.Count >= 100)
        {
            pool = Prune(candidates);
        }

        foreach (var c in pool)
        {
            c.Score = LoopScore(c.StartFrame, c.EndFrame, chroma, testOffset, weights);
        }

        pool.Sort((a, b) => b.Score.CompareTo(a.Score));

        if (!ReferenceEquals(pool, candidates))
        {
            candidates.Clear();
            candidates.AddRange(pool);
        }
    }

    private static List<Cand> Prune(List<Cand> candidates,
        double keepTopNotes = 75, double keepTopLoudness = 50, double acceptableLoudness = 0.25)
    {
        const double eps = 1e-3;
        var dbAll = candidates.Select(c => c.LoudnessDifference).ToArray();
        var noteAll = candidates.Select(c => c.NoteDistance).ToArray();

        var dbAdj = dbAll.Where(x => x > eps).ToArray();
        var noteAdj = noteAll.Where(x => x > eps).ToArray();

        var dbThreshold = dbAdj.Length > 3 ? Percentile(dbAdj, keepTopLoudness) : dbAll.Max();
        var noteThreshold = noteAdj.Length > 3 ? Percentile(noteAdj, keepTopNotes) : noteAll.Max();

        var keep = new List<Cand>();
        foreach (var c in candidates)
        {
            if (c.LoudnessDifference <= Math.Max(acceptableLoudness, dbThreshold) && c.NoteDistance <= noteThreshold)
            {
                keep.Add(c);
            }
        }

        return keep;
    }

    private static (double Bpm, int[] Beats) DetectBeats(float[][] power, int rate, float[]? onset = null)
    {
        onset ??= OnsetEnvelope(power, rate);
        if (onset.Length < 16)
        {
            return (120, []);
        }

        var bpm = EstimateTempo(onset, rate);
        if (bpm < 30)
        {
            bpm = 120;
        }

        var beats = TrackBeats(onset, bpm, rate);
        if (beats.Length < 2)
        {
            return (bpm, []);
        }

        return (bpm, beats);
    }

    private static float[] OnsetEnvelope(float[][] power, int rate)
    {
        var n = power.Length;
        var onset = new float[n];
        if (n < 2) return onset;

        var lowEnd = Math.Max(1, (int) (250.0 * NFft / rate));
        var midEnd = Math.Min(Bins, Math.Max(lowEnd + 1, (int) (2000.0 * NFft / rate)));

        for (var f = 1; f < n; f++)
        {
            var prev = power[f - 1];
            var cur = power[f];
            float low = 0, mid = 0, high = 0;
            for (var k = 1; k < Bins; k++)
            {
                var diff = cur[k] - prev[k];
                if (diff <= 0) continue;
                if (k < lowEnd) low += diff;
                else if (k < midEnd) mid += diff;
                else high += diff;
            }

            onset[f] = Math.Max(low, Math.Max(mid, high));
        }

        for (var f = 0; f < n; f++)
        {
            onset[f] = (float) Math.Log(1.0 + onset[f] * 1e6);
        }

        return onset;
    }

    private static double EstimateTempo(float[] onset, int rate)
    {
        var n = onset.Length;
        var minBpm = 60.0;
        var maxBpm = 200.0;
        var minLag = Math.Max(2, (int) Math.Round(60.0 / maxBpm * rate / Hop));
        var maxLag = Math.Max(minLag + 1, (int) Math.Round(60.0 / minBpm * rate / Hop));
        maxLag = Math.Min(maxLag, n - 1);
        if (maxLag <= minLag)
        {
            return 120;
        }

        var halfWin = Math.Min(n / 4, maxLag * 4);
        if (halfWin < minLag * 2) halfWin = minLag * 2;
        var mid = n / 2.0;
        var sigma = halfWin / 2.0;

        double bestScore = double.NegativeInfinity;
        int bestLag = minLag;
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            double s = 0;
            double wSum = 0;
            var count = n - lag;
            if (count <= 0) continue;
            for (var i = 0; i < count; i++)
            {
                var dist = i - mid;
                var w = Math.Exp(-0.5 * (dist / sigma) * (dist / sigma));
                s += w * onset[i] * onset[i + lag];
                wSum += w;
            }
            s /= Math.Max(wSum, 1e-10);
            if (s > bestScore)
            {
                bestScore = s;
                bestLag = lag;
            }
        }

        var bpm = 60.0 * rate / Hop / bestLag;
        while (bpm < minBpm) bpm *= 2;
        while (bpm > maxBpm) bpm /= 2;
        return bpm;
    }

    private static int[] TrackBeats(float[] onset, double bpm, int rate)
    {
        var n = onset.Length;
        var beatPeriod = Math.Max(2.0, 60.0 / bpm * rate / Hop);
        var tightness = 100.0;

        var dpBeat = new double[n];
        var dpSkip = new double[n];
        var bestPrevBeat = new int[n];

        dpBeat[0] = onset[0];
        dpSkip[0] = double.NegativeInfinity;
        bestPrevBeat[0] = -1;

        for (var f = 1; f < n; f++)
        {
            dpSkip[f] = Math.Max(dpSkip[f - 1], dpBeat[f - 1]);

            var lagLo = Math.Max(1, (int) Math.Round(beatPeriod * 0.5));
            var lagHi = Math.Max(lagLo + 1, (int) Math.Round(beatPeriod * 1.5));

            double bestScore = double.NegativeInfinity;
            int bestLag = -1;
            var upperLag = Math.Min(lagHi, f);
            for (var lag = lagLo; lag <= upperLag; lag++)
            {
                var prevScore = Math.Max(dpSkip[f - lag], dpBeat[f - lag]);
                if (prevScore <= double.NegativeInfinity / 2) continue;
                var x = (lag - beatPeriod) / beatPeriod;
                var pen = Math.Exp(-0.5 * tightness * x * x);
                var s = prevScore + pen + onset[f];
                if (s > bestScore)
                {
                    bestScore = s;
                    bestLag = lag;
                }
            }

            dpBeat[f] = bestScore;
            bestPrevBeat[f] = bestLag;
        }

        var beats = new List<int>();
        var lastBeat = -1;
        for (var f = n - 1; f >= 0; f--)
        {
            if (dpBeat[f] >= dpSkip[f] && dpBeat[f] > double.NegativeInfinity / 2)
            {
                if (lastBeat < 0 || f != lastBeat)
                {
                    beats.Add(f);
                    lastBeat = f;
                    var prev = bestPrevBeat[f];
                    if (prev < 1) break;
                    f = f - prev + 1;
                }
            }
        }

        beats.Reverse();
        return beats.ToArray();
    }

    private static int[] UnionSorted(int[] a, int[] b)
    {
        var set = new HashSet<int>(a);
        foreach (var x in b) set.Add(x);
        var arr = set.ToArray();
        Array.Sort(arr);
        return arr;
    }

    private static void RefineHop(List<Cand> list, float[][] chroma, double[] loudness, int testOffset,
        double[] weights, int minLoop, int maxLoop, int nFrames)
    {
        var topN = Math.Min(list.Count, 6);
        for (var idx = 0; idx < topN; idx++)
        {
            var c = list[idx];
            var bestS = c.StartFrame;
            var bestE = c.EndFrame;
            var bestScore = c.Score;

            for (var ds = -1; ds <= 1; ds++)
            {
                var s = c.StartFrame + ds;
                if (s < 0 || s >= nFrames) continue;
                for (var de = -1; de <= 1; de++)
                {
                    var en = c.EndFrame + de;
                    if (en < 0 || en >= nFrames) continue;
                    var len = en - s;
                    if (len < minLoop || len > maxLoop) continue;
                    var sc = LoopScore(s, en, chroma, testOffset, weights);
                    if (sc > bestScore)
                    {
                        bestScore = sc;
                        bestS = s;
                        bestE = en;
                    }
                }
            }

            if (bestS != c.StartFrame || bestE != c.EndFrame)
            {
                c.StartFrame = bestS;
                c.EndFrame = bestE;
                c.Score = bestScore;
                c.NoteDistance = NormDiff(chroma[bestE], chroma[bestS]);
                c.LoudnessDifference = Math.Abs(loudness[bestE] - loudness[bestS]);
            }
        }

        list.Sort((a, b) => b.Score.CompareTo(a.Score));
    }

    private static void RefineSub(List<Cand> candidates, float[][] chroma, double[] loudness, int testOffset,
        double[] weights, int nFrames)
    {
        var topN = Math.Min(candidates.Count, 4);
        var work = candidates.GetRange(0, topN);
        var testLength = Math.Abs(testOffset);

        foreach (var c in work)
        {
            var bestS = (double) c.StartFrame;
            var bestE = (double) c.EndFrame;
            var bestScore = c.Score;

            for (var dS = -0.5; dS <= 0.5; dS += 1.0)
            {
                for (var dE = -0.5; dE <= 0.5; dE += 1.0)
                {
                    if (dS == 0 && dE == 0) continue;
                    var sFrac = c.StartFrame + dS;
                    var eFrac = c.EndFrame + dE;
                    if (sFrac < 0 || eFrac < 1 || sFrac >= nFrames - 1 || eFrac >= nFrames - 1) continue;

                    var score = InterpolatedSubseqScore(chroma, sFrac, eFrac, testLength, weights);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestS = sFrac;
                        bestE = eFrac;
                    }
                }
            }

            var sInt = (int) Math.Round(bestS);
            var eInt = (int) Math.Round(bestE);
            if (bestScore > c.Score)
            {
                c.StartFrame = sInt;
                c.EndFrame = eInt;
                c.Score = bestScore;
                c.NoteDistance = NormDiff(chroma[eInt], chroma[sInt]);
                c.LoudnessDifference = Math.Abs(loudness[eInt] - loudness[sInt]);
            }
        }

        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
    }

    private static double InterpolatedSubseqScore(float[][] chroma, double sFrac, double eFrac,
        int testLength, double[] weights)
    {
        var n = chroma.Length;
        double num = 0, den = 0;

        for (var i = 0; i < testLength; i++)
        {
            var pos1 = sFrac + i;
            var pos2 = eFrac + i;
            if (pos1 >= n - 1 || pos2 >= n - 1) break;

            var w = i < weights.Length ? weights[i] : weights[^1];
            num += InterpolatedCosine(chroma, pos1, pos2) * w;
            den += w;
        }

        return den > 0 ? num / den : 0;
    }

    private static double InterpolatedCosine(float[][] chroma, double f1, double f2)
    {
        var i1 = (int) f1;
        var t1 = f1 - i1;
        var i2 = (int) f2;
        var t2 = f2 - i2;

        var a0 = chroma[i1];
        var a1 = chroma[Math.Min(chroma.Length - 1, i1 + 1)];
        var b0 = chroma[i2];
        var b1 = chroma[Math.Min(chroma.Length - 1, i2 + 1)];

        double dot = 0, na = 0, nb = 0;
        for (var k = 0; k < NChroma; k++)
        {
            var a = a0[k] * (1 - t1) + a1[k] * t1;
            var b = b0[k] * (1 - t2) + b1[k] * t2;
            dot += a * b;
            na += a * a;
            nb += b * b;
        }

        return dot / Math.Max(Math.Sqrt(na) * Math.Sqrt(nb), 1e-10);
    }

    private static void RefineXCorr(List<Cand> candidates, float[] audio, int rate)
    {
        var topN = Math.Min(candidates.Count, 16);
        var work = candidates.GetRange(0, topN);
        var maxLag = Math.Max(64, rate / 100);
        var win = Math.Min(2048, rate / 20);

        foreach (var c in work)
        {
            if (c.Score < 0.5) continue;

            var sBase = c.StartFrame * Hop;
            var eBase = c.EndFrame * Hop;
            if (eBase + win + maxLag >= audio.Length || sBase - maxLag < 0) continue;

            var (bestS, bestE, bestCorr) = ScanXCorr(audio, sBase, eBase, win, maxLag, 32, double.NegativeInfinity);
            if (bestCorr > 0.5)
            {
                var (fs, fe, fc) = ScanXCorr(audio, bestS, bestE, win, 32, 8, bestCorr);
                bestS = fs;
                bestE = fe;
                bestCorr = fc;
            }

            if (bestCorr > 0.5)
            {
                var moved = bestS != sBase || bestE != eBase;
                c.StartFrame = (int) Math.Round((double) bestS / Hop);
                c.EndFrame = (int) Math.Round((double) bestE / Hop);
                c.FrameAdjusted = moved;
                if (moved)
                {
                    c.Score = Math.Min(1.0, c.Score + 0.05 * bestCorr);
                }
            }
        }
    }

    private static (int S, int E, double Corr) ScanXCorr(float[] audio, int sBase, int eBase, int win, int maxLag, int step, double minCorr)
    {
        var bestS = sBase;
        var bestE = eBase;
        var bestCorr = minCorr;
        for (var dS = -maxLag; dS <= maxLag; dS += step)
        {
            for (var dE = -maxLag; dE <= maxLag; dE += step)
            {
                var sSample = sBase + dS;
                var eSample = eBase + dE;
                if (sSample < 0 || eSample + win >= audio.Length) continue;

                var corr = XcorrSegment(audio, sSample, eSample, win);
                if (corr > bestCorr)
                {
                    bestCorr = corr;
                    bestS = sSample;
                    bestE = eSample;
                }
            }
        }
        return (bestS, bestE, bestCorr);
    }

    private static double XcorrSegment(float[] audio, int sSample, int eSample, int win)
    {
        var len = Math.Min(win, Math.Min(audio.Length - sSample, audio.Length - eSample));
        if (len <= 0) return 0;
        double sum = 0, na = 0, nb = 0;
        for (var i = 0; i < len; i++)
        {
            var a = audio[sSample + i];
            var b = audio[eSample + i];
            sum += a * b;
            na += a * a;
            nb += b * b;
        }
        return sum / Math.Max(Math.Sqrt(na) * Math.Sqrt(nb), 1e-10);
    }

    private static void RefinePhraseSnap(List<Cand> candidates, float[] mono, int[] onsets, int nFrames, int rate)
    {
        if (onsets.Length < 2) return;
        var tol = Math.Max(4, (int) Math.Round(1.5 * rate / Hop));
        var tolHalf = tol / 2;
        var topN = Math.Min(candidates.Count, 16);

        for (var idx = 0; idx < topN; idx++)
        {
            var c = candidates[idx];
            if (c.Score < 0.5) continue;

            var basePhase = PhaseContinuity(mono, c.StartFrame, c.EndFrame);
            var phaseGate = Math.Max(0.40, basePhase - 0.15);

            var lo = LowerBound(onsets, c.StartFrame - tol);
            var bestDelta = 0;
            var bestDist = int.MaxValue;

            for (var k = lo; k < onsets.Length; k++)
            {
                var onset = onsets[k];
                if (onset > c.StartFrame + tol) break;

                var delta = onset - c.StartFrame;
                var newEnd = c.EndFrame + delta;
                if (newEnd <= 0 || newEnd >= nFrames) continue;

                var endLo = LowerBound(onsets, newEnd - tolHalf);
                if (endLo >= onsets.Length || onsets[endLo] > newEnd + tolHalf) continue;

                var phase = PhaseContinuity(mono, onset, newEnd);
                if (phase < phaseGate) continue;

                var dist = Math.Abs(delta);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestDelta = delta;
                }
            }

            if (bestDelta != 0)
            {
                c.StartFrame += bestDelta;
                c.EndFrame += bestDelta;
                c.FrameAdjusted = true;
            }
        }
    }

    private static void BorderSimilarityFilter(List<Cand> candidates, float[][] chroma, int rate, double threshold)
    {
        var borderFrames = Math.Max(2, (int) Math.Round(0.050 * rate / Hop));
        borderFrames = Math.Min(borderFrames, Math.Max(2, chroma.Length / 4));

        var keep = new List<Cand>(candidates.Count);
        foreach (var c in candidates)
        {
            var sim = BorderCosine(chroma, c.StartFrame, c.EndFrame, borderFrames);
            if (sim >= threshold)
            {
                keep.Add(c);
            }
        }

        candidates.Clear();
        candidates.AddRange(keep);
    }

    private static bool HasAtLeastTwoBeatsInRange(int[] beats, int startFrame, int endFrame)
    {
        var lo = LowerBound(beats, startFrame);
        return lo + 1 < beats.Length && beats[lo + 1] <= endFrame;
    }

    private static int LowerBound(int[] arr, int value)
    {
        var lo = 0;
        var hi = arr.Length;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (arr[mid] < value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static double SpectralFluxAt(float[][] power, int frame, int hop, int halfWindow)
    {
        var n = power.Length;
        var lo = Math.Max(1, frame - halfWindow);
        var hi = Math.Min(n - 1, frame + halfWindow);
        double flux = 0;
        int bins = 0;
        for (var f = lo; f < hi; f++)
        {
            var cur = power[f];
            var nxt = power[f + 1];
            int binEnd = Math.Min(cur.Length, nxt.Length);
            for (var k = 0; k < binEnd; k++)
            {
                var d = nxt[k] - cur[k];
                if (d > 0) flux += d;
            }
            bins++;
        }
        return bins > 0 ? flux / bins : 0;
    }

    private static double SpectralFluxContinuity(float[][] power, int rate, int startFrame, int endFrame)
    {
        var hop = Hop;
        var halfWindow = Math.Max(1, (int) (0.025 * rate / hop));
        var fluxEnd = SpectralFluxAt(power, endFrame, hop, halfWindow);
        var fluxStart = SpectralFluxAt(power, startFrame, hop, halfWindow);
        var sum = Math.Max(fluxEnd + fluxStart, 1e-9);
        return 1.0 - Math.Min(Math.Abs(fluxEnd - fluxStart) / sum, 1.0);
    }

    private static int SectionCrossingCount(int[] sections, int startFrame, int endFrame)
    {
        if (sections.Length == 0) return 0;
        var n = 0;
        foreach (var s in sections)
        {
            if (s > startFrame && s < endFrame) n++;
        }
        return n;
    }

    private static double PhaseContinuity(float[] mono, int startFrame, int endFrame)
    {
        const int winSize = 1024;
        var halfBins = winSize / 2;
        var startSample = startFrame * Hop;
        var endSample = endFrame * Hop;

        if (endSample - winSize < 0 || startSample + winSize > mono.Length || endSample <= startSample)
        {
            return 1.0;
        }

        var reA = new double[winSize];
        var imA = new double[winSize];
        var reB = new double[winSize];
        var imB = new double[winSize];
        WindowedFft(mono, endSample - winSize, winSize, reA, imA);
        WindowedFft(mono, startSample, winSize, reB, imB);

        double sumRe = 0, sumIm = 0, wSum = 0;
        for (var k = 1; k < halfBins; k++)
        {
            var aRe = reA[k];
            var aIm = imA[k];
            var bRe = reB[k];
            var bIm = imB[k];
            var magA = Math.Sqrt(aRe * aRe + aIm * aIm);
            var magB = Math.Sqrt(bRe * bRe + bIm * bIm);
            var prod = magA * magB;
            if (prod <= 1e-20) continue;
            var weight = Math.Min(magA, magB);
            sumRe += weight * (aRe * bRe + aIm * bIm) / prod;
            sumIm += weight * (aIm * bRe - aRe * bIm) / prod;
            wSum += weight;
        }

        if (wSum <= 1e-9) return 1.0;
        return Math.Clamp(Math.Sqrt(sumRe * sumRe + sumIm * sumIm) / wSum, 0.0, 1.0);
    }

    private static void WindowedFft(float[] mono, int start, int n, double[] re, double[] im)
    {
        for (var i = 0; i < n; i++)
        {
            var s = start + i;
            var x = (s >= 0 && s < mono.Length) ? mono[s] : 0.0;
            var win = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n);
            re[i] = x * win;
            im[i] = 0.0;
        }
        Fft(re, im);
    }

    private static double TransitionSmoothness(float[] audio, int rate, int startFrame, int endFrame)
    {
        var ms = (int) (0.050 * rate);
        var endSample = endFrame * Hop;
        var startSample = startFrame * Hop;

        var endStart = Math.Max(0, endSample - ms);
        var endLen = Math.Min(audio.Length, endSample) - endStart;
        var startEnd = Math.Min(audio.Length, startSample + ms);
        var startLen = startEnd - startSample;

        if (endLen <= 0 || startLen <= 0) return 1.0;

        double rms1 = 0, rms2 = 0;
        for (var i = 0; i < endLen; i++)
        {
            var v = audio[endStart + i];
            rms1 += v * v;
        }
        for (var i = 0; i < startLen; i++)
        {
            var v = audio[startSample + i];
            rms2 += v * v;
        }
        rms1 = Math.Sqrt(rms1 / endLen);
        rms2 = Math.Sqrt(rms2 / startLen);

        if (rms1 < 1e-6 && rms2 < 1e-6) return 1.0;
        if (rms1 < 1e-6 || rms2 < 1e-6) return 0.0;

        var ratio = Math.Max(rms1, rms2) / Math.Min(rms1, rms2);
        return 1.0 / ratio;
    }

    private static void TransitionSmoothnessFilter(List<Cand> candidates, float[] audio, int rate, double threshold)
    {
        candidates.RemoveAll(c => TransitionSmoothness(audio, rate, c.StartFrame, c.EndFrame) < threshold);
    }

    private static void SpectralFluxFilter(List<Cand> candidates, float[][] power, int rate, double threshold)
    {
        candidates.RemoveAll(c => SpectralFluxContinuity(power, rate, c.StartFrame, c.EndFrame) < threshold);
    }

    private static double BorderCosine(float[][] chroma, int fStart, int fEnd, int span)
    {
        var n = chroma.Length;
        var startAvg = new float[NChroma];
        var endAvg = new float[NChroma];
        var startN = 0;
        var endN = 0;
        for (var i = 0; i < span; i++)
        {
            var si = fStart + i;
            var ei = fEnd + i;
            if (si >= 0 && si < n)
            {
                var s = chroma[si];
                for (var k = 0; k < NChroma; k++) startAvg[k] += s[k];
                startN++;
            }
            if (ei >= 0 && ei < n)
            {
                var e = chroma[ei];
                for (var k = 0; k < NChroma; k++) endAvg[k] += e[k];
                endN++;
            }
        }
        if (startN == 0 || endN == 0) return 0;
        var inv1 = 1f / startN;
        var inv2 = 1f / endN;
        double dot = 0, na = 0, nb = 0;
        for (var k = 0; k < NChroma; k++)
        {
            var a = startAvg[k] * inv1;
            var b = endAvg[k] * inv2;
            dot += a * b;
            na += a * a;
            nb += b * b;
        }
        return dot / Math.Max(Math.Sqrt(na) * Math.Sqrt(nb), 1e-10);
    }

    private static void PrioritizeDuration(List<Cand> list)
    {
        if (list.Count == 0)
        {
            return;
        }

        var dbThreshold = Median(list.Select(c => c.LoudnessDifference).ToArray());
        var scoreThreshold = Percentile(list.Select(c => c.Score).ToArray(), 90);
        scoreThreshold = Math.Max(scoreThreshold, list[0].Score - 1e-4);

        var durationArgmax = 0;
        long durationMax = 0;
        for (var idx = 0; idx < list.Count; idx++)
        {
            var pair = list[idx];
            if (pair.Score < scoreThreshold)
            {
                break;
            }

            long duration = pair.EndFrame - pair.StartFrame;
            if (duration > durationMax && pair.LoudnessDifference <= dbThreshold)
            {
                durationMax = duration;
                durationArgmax = idx;
            }
        }

        if (durationArgmax > 0)
        {
            var pick = list[durationArgmax];
            list.RemoveAt(durationArgmax);
            list.Insert(0, pick);
        }
    }

    private static void RankForRole(List<Cand> list, LoopRole role, int nFrames, double bpm, int rate,
        int[] sections, double sectionWeight)
    {
        var barFrames = bpm > 1 ? 60.0 / bpm * 4.0 * rate / Hop : 0;

        static double Gauss(double x, double mu, double sig)
        {
            var d = (x - mu) / sig;
            return Math.Exp(-0.5 * d * d);
        }

        double Priority(Cand c)
        {
            var len = c.EndFrame - c.StartFrame;
            var lenFrac = (double) len / nFrames;
            var startFrac = (double) c.StartFrame / nFrames;
            var endFrac = (double) c.EndFrame / nFrames;
            var pr = c.Score;

            // Empirical priors from official FH4/FH5/FH6 RadioInfo loops (700+ samples).
            // Quality (Score) stays dominant; these are moderate nudges shaping the order.

            // Bar-quantization: ~90% of official loops are an integer number of bars,
            // clustered on round counts. Role bar-count targets: Track ~80, Post ~16.
            if (barFrames > 1)
            {
                var bars = len / barFrames;
                var bdist = Math.Min(bars - Math.Floor(bars), Math.Ceiling(bars) - bars);
                pr += 0.04 * (1 - 2 * bdist);

                var nb = (int) Math.Round(bars);
                if (nb % 16 == 0)
                {
                    pr += 0.02;
                }
                else if (nb % 8 == 0)
                {
                    pr += 0.012;
                }
                else if (nb % 4 == 0)
                {
                    pr += 0.006;
                }

                if (bars >= 1)
                {
                    var target = role == LoopRole.Track ? 80.0 : 16.0;
                    var lr = Math.Log(bars / target);
                    pr += 0.025 * Math.Exp(-lr * lr / (2 * 0.5 * 0.5));
                }
            }

            // Position / length priors (soft Gaussians around the official medians).
            if (role == LoopRole.Track)
            {
                pr += 0.03 * Gauss(startFrac, 0.16, 0.12);
                pr += 0.03 * Gauss(endFrac, 0.88, 0.10);
                pr += 0.025 * Gauss(lenFrac, 0.68, 0.20);

                if (lenFrac < 0.15)
                {
                    pr -= 0.06 * (0.15 - lenFrac) / 0.15;
                }
            }
            else
            {
                pr += 0.03 * Gauss(startFrac, 0.76, 0.12);
                pr += 0.03 * Gauss(endFrac, 0.92, 0.08);
                pr += 0.025 * Gauss(lenFrac, 0.16, 0.10);

                // Post loops live in the second half — keep this a firm structural penalty.
                if (startFrac < 0.5)
                {
                    pr -= 0.30 * (0.5 - startFrac) * 2.0;
                }

                if (lenFrac > 0.4)
                {
                    pr -= 0.06 * Math.Min(1.0, (lenFrac - 0.4) / 0.6);
                }
            }

            if (sectionWeight > 0 && sections.Length >= 3)
            {
                var crossings = SectionCrossingCount(sections, c.StartFrame, c.EndFrame);
                pr -= 0.08 * crossings * sectionWeight;
            }

            return pr;
        }

        list.Sort((a, b) => Priority(b).CompareTo(Priority(a)));
    }

    private static double LoopScore(int b1, int b2, float[][] chroma, int testDuration, double[] weights)
    {
        var ahead = SubseqSimilarityConsensus(b1, b2, chroma, testDuration, weights);
        var behind = SubseqSimilarityConsensus(b1, b2, chroma, -testDuration, ReversedWeights(weights));
        return Math.Max(ahead, behind);
    }

    private static double SubseqSimilarityConsensus(int b1Start, int b2Start, float[][] chroma,
        int testEndOffset, double[] weights)
    {
        var baseScore = SubseqSimilarity(b1Start, b2Start, chroma, testEndOffset, weights);
        var testLength = Math.Abs(testEndOffset);
        if (testLength < 9) return baseScore;

        var thirdLen = testLength / 3;
        if (thirdLen < 3) return baseScore;

        var dir = testEndOffset >= 0 ? 1 : -1;
        var chromaLen = chroma.Length;
        var subWeights = new double[thirdLen];
        var scores = new double[3];
        for (var t = 0; t < 3; t++)
        {
            var shift = dir * t * thirdLen;
            var newB1 = b1Start + shift;
            var newB2 = b2Start + shift;

            if (newB1 < 0 || newB2 < 0
                || newB1 + thirdLen > chromaLen
                || newB2 + thirdLen > chromaLen)
            {
                scores[t] = baseScore;
                continue;
            }

            Array.Copy(weights, t * thirdLen, subWeights, 0, thirdLen);
            scores[t] = SubseqSimilarity(newB1, newB2, chroma, dir * thirdLen, subWeights);
        }

        var mean = (scores[0] + scores[1] + scores[2]) / 3.0;
        var min = Math.Min(scores[0], Math.Min(scores[1], scores[2]));
        return Math.Min(baseScore, 0.7 * mean + 0.3 * min);
    }

    private static double SubseqSimilarity(int b1Start, int b2Start, float[][] chroma, int testEndOffset, double[] weights)
    {
        var chromaLen = chroma.Length;
        var testLength = Math.Abs(testEndOffset);
        int b1End, b2End, maxOffset;

        if (testEndOffset < 0)
        {
            var maxNeg = Math.Max(testEndOffset, Math.Max(-b1Start, -b2Start));
            b1Start += maxNeg;
            b2Start += maxNeg;
            maxOffset = Math.Abs(maxNeg);
            var remaining = Math.Min(chromaLen - b1Start, chromaLen - b2Start);
            if (remaining < maxOffset) maxOffset = Math.Max(0, remaining);
        }
        else
        {
            b1End = Math.Min(b1Start + testLength, chromaLen);
            b2End = Math.Min(b2Start + testLength, chromaLen);
            maxOffset = Math.Min(b1End - b1Start, b2End - b2Start);
        }

        if (maxOffset <= 0)
        {
            return 0;
        }

        var loopLen = Math.Min(testLength, maxOffset);
        double num = 0, den = 0;
        for (var i = 0; i < loopLen; i++)
        {
            var a = chroma[b1Start + i];
            var b = chroma[b2Start + i];
            double dot = 0, na = 0, nb = 0;
            for (var k = 0; k < a.Length; k++)
            {
                dot += a[k] * b[k];
                na += a[k] * a[k];
                nb += b[k] * b[k];
            }

            var v = dot / Math.Max(Math.Sqrt(na) * Math.Sqrt(nb), 1e-10);
            var w = i < weights.Length ? weights[i] : weights[^1];
            num += v * w;
            den += w;
        }
        for (var i = loopLen; i < testLength; i++)
        {
            den += i < weights.Length ? weights[i] : weights[^1];
        }

        return den > 0 ? num / den : 0;
    }

    private static double[] Weights(int length, double start, double stop)
    {
        var w = new double[Math.Max(1, length)];
        if (w.Length == 1)
        {
            w[0] = start;
            return w;
        }

        var logStart = Math.Log(start);
        var logStop = Math.Log(stop);
        for (var i = 0; i < w.Length; i++)
        {
            var t = (double) i / (w.Length - 1);
            w[i] = Math.Exp(logStart + (logStop - logStart) * t);
        }

        return w;
    }

    private static long NearestZeroCrossing(float[] audio, int rate, long sampleIdx)
    {
        var windowSize = Math.Max(1, rate / 100);
        var offset = windowSize / 2;
        var neg = (int) Math.Max(0, sampleIdx - offset);
        var pos = (int) Math.Min(audio.Length, sampleIdx + offset);
        var len = pos - neg;
        if (len <= 0)
        {
            return sampleIdx;
        }

        var offsetCorrection = sampleIdx - offset < 0 ? (int) Math.Abs(sampleIdx - offset) : 0;

        var dist = new double[len];
        var prev = 2.0;
        for (var i = 0; i < len; i++)
        {
            var v = audio[neg + i];
            var fdist = Math.Abs(v);
            if (prev * v > 0)
            {
                fdist += 0.4f;
            }
            else if (prev > 0.0)
            {
                fdist += 0.1f;
            }

            prev = v;
            dist[i] = fdist + 0.1 * Math.Abs(i - offset + offsetCorrection) / (windowSize / 2.0);
        }

        var argmin = 0;
        for (var i = 1; i < len; i++)
        {
            if (dist[i] < dist[argmin])
            {
                argmin = i;
            }
        }

        if (dist[argmin] > 0.2)
        {
            return sampleIdx;
        }

        return Math.Max(0, sampleIdx + argmin - offset + offsetCorrection);
    }

    private static float[][] Stft(float[] x, bool preEmphasis)
    {
        var win = new double[NFft];
        for (var i = 0; i < NFft; i++)
        {
            win[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / NFft);
        }

        var pad = NFft / 2;
        var n = x.Length;
        var nFrames = n / Hop + 1;
        var frames = new float[nFrames][];

        const double preCoef = 0.97;

        Parallel.For(0, nFrames, () => (new double[NFft], new double[NFft]), (f, _, buf) =>
        {
            var (re, im) = buf;
            var center = f * Hop;
            for (var i = 0; i < NFft; i++)
            {
                var src = center - pad + i;
                var s = src >= 0 && src < n ? x[src] : 0.0;
                if (preEmphasis)
                {
                    var prevSrc = src - 1;
                    var prev = prevSrc >= 0 && prevSrc < n ? x[prevSrc] : 0.0;
                    s = s - preCoef * prev;
                }

                re[i] = s * win[i];
                im[i] = 0;
            }

            Fft(re, im);

            var mag = new float[Bins];
            for (var k = 0; k < Bins; k++)
            {
                mag[k] = (float) (re[k] * re[k] + im[k] * im[k]);
            }

            frames[f] = mag;
            return buf;
        }, _ => { });

        return frames;
    }

    private static float[][] ChromaLowBand(float[][] power, int rate, float[]? vadScores, double peakThresh, double vadThresh)
    {
        var cutoff = Math.Max(1, (int) (250.0 * NFft / rate));
        var masked = new float[power.Length][];
        for (var f = 0; f < power.Length; f++)
        {
            masked[f] = new float[Bins];
            Array.Copy(power[f], masked[f], cutoff);
        }
        return Chroma(masked, rate, vadScores, peakThresh, vadThresh);
    }

    private static float[][] BlendChroma(float[][] full, float[][] low, double fullWeight, double lowWeight)
    {
        var n = full.Length;
        var blended = new float[n][];
        for (var f = 0; f < n; f++)
        {
            var dst = new float[NChroma];
            var a = full[f];
            var b = low[f];
            for (var k = 0; k < NChroma; k++)
            {
                dst[k] = (float) (a[k] * fullWeight + b[k] * lowWeight);
            }
            blended[f] = dst;
        }
        return blended;
    }

    private static double DetectTuningOffset(float[][] power, int rate)
    {
        var loBin = Math.Max(2, (int) (100.0 * NFft / rate));
        var hiBin = Math.Min(Bins - 2, (int) (1500.0 * NFft / rate));
        if (hiBin <= loBin) return 0;

        double sum = 0;
        var count = 0;
        var step = Math.Max(1, power.Length / 200);
        for (var f = 0; f < power.Length; f += step)
        {
            var pf = power[f];
            var maxBin = loBin;
            var maxVal = 0.0;
            for (var k = loBin; k <= hiBin; k++)
            {
                if (pf[k] > maxVal) { maxVal = pf[k]; maxBin = k; }
            }
            if (maxVal > 0.001 && maxBin > loBin && maxBin < hiBin)
            {
                var yL = pf[maxBin - 1];
                var yC = pf[maxBin];
                var yR = pf[maxBin + 1];
                var denom = yL - 2.0 * yC + yR;
                var peakOffset = denom != 0 ? 0.5 * (yL - yR) / denom : 0.0;
                peakOffset = Math.Clamp(peakOffset, -0.5, 0.5);
                var freq = (double) (maxBin + peakOffset) * rate / NFft;
                var midi = 69 + 12 * Math.Log2(freq / 440.0);
                sum += midi - Math.Round(midi);
                count++;
            }
        }
        return count >= 10 ? sum / count : 0;
    }

    private static float[][] RotateChroma(float[][] chroma, double shift)
    {
        var n = chroma.Length;
        var rotated = new float[n][];
        var intShift = (int) Math.Floor(shift);
        var fracShift = shift - intShift;
        for (var f = 0; f < n; f++)
        {
            var src = chroma[f];
            var dst = new float[NChroma];
            for (var k = 0; k < NChroma; k++)
            {
                var k0 = ((k - intShift) % NChroma + NChroma) % NChroma;
                var k1 = (k0 + 1) % NChroma;
                dst[k] = (float) (src[k0] * (1 - fracShift) + src[k1] * fracShift);
            }
            rotated[f] = dst;
        }
        return rotated;
    }

    private static int[] DetectSections(float[][] chroma, int[] beats, int nFrames, int rate)
    {
        int b;
        float[][] feat;
        int[] centerFrame;

        if (beats.Length >= 8)
        {
            b = beats.Length - 1;
            feat = new float[b][];
            centerFrame = new int[b];
            for (var i = 0; i < b; i++)
            {
                feat[i] = SegmentMean(chroma, beats[i], beats[i + 1]);
                centerFrame[i] = (beats[i] + beats[i + 1]) / 2;
            }
        }
        else
        {
            var step = Math.Max(1, (int) (0.5 * rate / Hop));
            b = Math.Max(2, nFrames / step);
            feat = new float[b][];
            centerFrame = new int[b];
            for (var i = 0; i < b; i++)
            {
                var a0 = i * step;
                var a1 = Math.Min(nFrames, a0 + step);
                feat[i] = SegmentMean(chroma, a0, a1);
                centerFrame[i] = (a0 + a1) / 2;
            }
        }

        if (b < 8)
        {
            return [];
        }

        for (var i = 0; i < b; i++)
        {
            double nrm = 0;
            for (var k = 0; k < NChroma; k++)
            {
                nrm += feat[i][k] * feat[i][k];
            }

            nrm = Math.Sqrt(nrm);
            if (nrm > 1e-9)
            {
                for (var k = 0; k < NChroma; k++)
                {
                    feat[i][k] /= (float) nrm;
                }
            }
        }

        var ssm = new float[b][];
        for (var i = 0; i < b; i++)
        {
            ssm[i] = new float[b];
            for (var j = 0; j < b; j++)
            {
                double dot = 0;
                for (var k = 0; k < NChroma; k++)
                {
                    dot += feat[i][k] * feat[j][k];
                }

                ssm[i][j] = (float) dot;
            }
        }

        var l = Math.Max(4, b / 16);
        var sigma = l / 2.0;
        var kernel = new double[2 * l + 1][];
        for (var a = -l; a <= l; a++)
        {
            kernel[a + l] = new double[2 * l + 1];
            for (var bb = -l; bb <= l; bb++)
            {
                var g = Math.Exp(-(a * a + bb * bb) / (2 * sigma * sigma));
                var sign = (a < 0) == (bb < 0) ? 1.0 : -1.0;
                kernel[a + l][bb + l] = sign * g;
            }
        }

        var novelty = new double[b];
        for (var i = 0; i < b; i++)
        {
            double sum = 0;
            for (var a = -l; a <= l; a++)
            {
                var ia = i + a;
                if (ia < 0 || ia >= b)
                {
                    continue;
                }

                for (var bb = -l; bb <= l; bb++)
                {
                    var jb = i + bb;
                    if (jb < 0 || jb >= b)
                    {
                        continue;
                    }

                    sum += kernel[a + l][bb + l] * ssm[ia][jb];
                }
            }

            novelty[i] = sum;
        }

        double mean = 0;
        for (var i = 0; i < b; i++)
        {
            mean += novelty[i];
        }

        mean /= b;
        double varSum = 0;
        for (var i = 0; i < b; i++)
        {
            varSum += (novelty[i] - mean) * (novelty[i] - mean);
        }

        var std = Math.Sqrt(varSum / b);
        var thresh = mean + 0.5 * std;

        var boundaries = new List<int>();
        var lastPeak = -l;
        for (var i = l; i < b - l; i++)
        {
            if (novelty[i] < thresh)
            {
                continue;
            }

            if (novelty[i] < novelty[i - 1] || novelty[i] < novelty[i + 1])
            {
                continue;
            }

            if (i - lastPeak < l)
            {
                continue;
            }

            boundaries.Add(centerFrame[i]);
            lastPeak = i;
        }

        return boundaries.ToArray();
    }

    private static float[] SegmentMean(float[][] chroma, int a, int b)
    {
        var c = new float[NChroma];
        if (b <= a)
        {
            b = a + 1;
        }

        var cnt = 0;
        for (var f = a; f < b && f < chroma.Length; f++)
        {
            var cf = chroma[f];
            for (var k = 0; k < NChroma; k++)
            {
                c[k] += cf[k];
            }

            cnt++;
        }

        if (cnt > 0)
        {
            for (var k = 0; k < NChroma; k++)
            {
                c[k] /= cnt;
            }
        }

        return c;
    }

    private static double[][] MelFilterbank(int rate, int nMels)
        => _melFilterbanks.GetOrAdd((rate, nMels), static key => BuildMelFilterbank(key.Rate, key.NMels));

    private static double[][] BuildMelFilterbank(int rate, int nMels)
    {
        double HzToMel(double f) => 2595.0 * Math.Log10(1 + f / 700.0);
        double MelToHz(double m) => 700.0 * (Math.Pow(10, m / 2595.0) - 1);

        var melMin = HzToMel(0);
        var melMax = HzToMel(rate / 2.0);

        var points = new double[nMels + 2];
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = MelToHz(melMin + (melMax - melMin) * i / (nMels + 1));
        }

        var fb = new double[nMels][];
        for (var m = 0; m < nMels; m++)
        {
            fb[m] = new double[Bins];
            var lo = points[m];
            var ctr = points[m + 1];
            var hi = points[m + 2];
            for (var k = 0; k < Bins; k++)
            {
                var hz = (double) k * rate / NFft;
                double w = 0;
                if (hz >= lo && hz <= ctr && ctr > lo)
                {
                    w = (hz - lo) / (ctr - lo);
                }
                else if (hz > ctr && hz <= hi && hi > ctr)
                {
                    w = (hi - hz) / (hi - ctr);
                }

                fb[m][k] = w;
            }
        }

        return fb;
    }

    private static float[][] Mfcc(float[][] power, int rate)
    {
        const int nMels = 40;
        const int kCoef = 8;
        var fb = MelFilterbank(rate, nMels);
        var n = power.Length;
        var mfcc = new float[n][];

        Parallel.For(0, n, f =>
        {
            var p = power[f];
            var logMel = new double[nMels];
            for (var m = 0; m < nMels; m++)
            {
                double e = 0;
                var row = fb[m];
                for (var k = 0; k < Bins; k++)
                {
                    e += row[k] * p[k];
                }

                logMel[m] = Math.Log(Math.Max(1e-10, e));
            }

            var c = new float[kCoef];
            for (var j = 0; j < kCoef; j++)
            {
                double sum = 0;
                for (var m = 0; m < nMels; m++)
                {
                    sum += logMel[m] * Math.Cos(Math.PI * (j + 1) * (m + 0.5) / nMels);
                }

                c[j] = (float) sum;
            }

            mfcc[f] = c;
        });

        return mfcc;
    }

    private static float[][] Chroma(float[][] power, int rate, float[]? vadScores, double peakThresh, double vadThresh)
    {
        var fb = ChromaFilterbank(rate);
        var n = power.Length;
        var chroma = new float[n][];

        Parallel.For(0, n, f =>
        {
            var p = power[f];
            var c = new float[NChroma];
            for (var ch = 0; ch < NChroma; ch++)
            {
                double sum = 0;
                var row = fb[ch];
                for (var k = 0; k < Bins; k++)
                {
                    sum += row[k] * p[k];
                }

                c[ch] = (float) sum;
            }

            var max = 0f;
            for (var ch = 0; ch < NChroma; ch++)
            {
                if (c[ch] > max)
                {
                    max = c[ch];
                }
            }

            if (max > 0)
            {
                double s = 0;
                for (var ch = 0; ch < NChroma; ch++) s += c[ch];
                var peakiness = s > 0 ? (float) (max / s) : 0f;

                var vadOk = vadScores == null || vadThresh <= 0 || vadScores[f] >= vadThresh;
                var peakOk = peakThresh <= 0 || peakiness >= peakThresh;

                if (vadOk && peakOk)
                {
                    for (var ch = 0; ch < NChroma; ch++)
                    {
                        c[ch] /= max;
                    }
                }
                else
                {
                    for (var ch = 0; ch < NChroma; ch++)
                    {
                        c[ch] = 0;
                    }
                }
            }

            chroma[f] = c;
        });

        return chroma;
    }

    private static double[][] ChromaFilterbank(int rate)
        => _chromaFilterbanks.GetOrAdd(rate, static r => BuildChromaFilterbank(r));

    private static double[][] BuildChromaFilterbank(int rate)
    {
        var frqbins = new double[Bins];
        for (var k = 1; k < Bins; k++)
        {
            var hz = (double) k * rate / NFft;
            frqbins[k] = NChroma * Math.Log2(hz / 27.5);
        }

        frqbins[0] = frqbins[1] - 1.5 * NChroma;

        var binwidth = new double[Bins];
        for (var k = 0; k < Bins - 1; k++)
        {
            binwidth[k] = Math.Max(frqbins[k + 1] - frqbins[k], 1.0);
        }

        binwidth[Bins - 1] = 1.0;

        var wts = new double[NChroma][];
        for (var c = 0; c < NChroma; c++)
        {
            wts[c] = new double[Bins];
        }

        const double n2 = 6.0;
        for (var k = 0; k < Bins; k++)
        {
            for (var c = 0; c < NChroma; c++)
            {
                var d = frqbins[k] - c;
                d = Mod(d + n2 + 10 * NChroma, NChroma) - n2;
                wts[c][k] = Math.Exp(-0.5 * Math.Pow(2 * d / binwidth[k], 2));
            }

            double colNorm = 0;
            for (var c = 0; c < NChroma; c++)
            {
                colNorm += wts[c][k] * wts[c][k];
            }

            colNorm = Math.Sqrt(colNorm);
            if (colNorm > 0)
            {
                for (var c = 0; c < NChroma; c++)
                {
                    wts[c][k] /= colNorm;
                }
            }

            var oct = Math.Exp(-0.5 * Math.Pow((frqbins[k] / NChroma - 5.0) / 2.0, 2));
            for (var c = 0; c < NChroma; c++)
            {
                wts[c][k] *= oct;
            }
        }

        var rolled = new double[NChroma][];
        for (var c = 0; c < NChroma; c++)
        {
            rolled[c] = wts[(c + 3) % NChroma];
        }

        return rolled;
    }

    private static double[] Loudness(float[][] power, int rate)
    {
        var aw = new double[Bins];
        for (var k = 0; k < Bins; k++)
        {
            var hz = (double) k * rate / NFft;
            aw[k] = AWeighting(hz);
        }

        const int TopK = 5;

        var n = power.Length;
        var loud = new double[n];
        var topBuf = new double[TopK];
        for (var f = 0; f < n; f++)
        {
            var p = power[f];
            for (var i = 0; i < TopK; i++) topBuf[i] = double.NegativeInfinity;
            for (var k = 0; k < Bins; k++)
            {
                var db = 10 * Math.Log10(Math.Max(1e-10, p[k])) + aw[k];
                if (db > topBuf[TopK - 1])
                {
                    var j = TopK - 1;
                    while (j > 0 && topBuf[j - 1] < db)
                    {
                        topBuf[j] = topBuf[j - 1];
                        j--;
                    }
                    topBuf[j] = db;
                }
            }

            double meanDb = 0;
            for (var i = 0; i < TopK; i++) meanDb += topBuf[i];
            meanDb /= TopK;

            loud[f] = meanDb;
        }

        return loud;
    }

    private static double AWeighting(double f)
    {
        if (f <= 0)
        {
            return -80.0;
        }

        var f2 = f * f;
        var num = 12194.0 * 12194.0 * f2 * f2;
        var den = (f2 + 20.6 * 20.6)
                  * Math.Sqrt((f2 + 107.7 * 107.7) * (f2 + 737.9 * 737.9))
                  * (f2 + 12194.0 * 12194.0);
        var ra = num / den;
        return 2.0 + 20.0 * Math.Log10(ra);
    }

    private static double Norm(float[] a)
    {
        double s = 0;
        for (var i = 0; i < a.Length; i++)
        {
            s += a[i] * a[i];
        }

        return Math.Sqrt(s);
    }

    private static double NormDiff(float[] a, float[] b)
    {
        double s = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var d = a[i] - b[i];
            s += d * d;
        }

        return Math.Sqrt(s);
    }

    private static double Mod(double a, double m)
    {
        var r = a % m;
        return r < 0 ? r + m : r;
    }

    private static int SecondsToFrames(double seconds, int rate) => (int) Math.Floor(seconds * rate / Hop);

    private static float[] PreProcess(float[] x, out int trimOffset)
    {
        var n = x.Length;

        float max = 0;
        for (var i = 0; i < n; i++)
        {
            var a = Math.Abs(x[i]);
            if (a > max) max = a;
        }

        var scale = (max > 0 && Math.Abs(max - 1f) > 1e-3f) ? 0.99f / max : 1f;

        const float silenceThresh = 1e-3f;
        var first = 0;
        var last = n - 1;
        for (var i = 0; i < n; i++)
        {
            if (Math.Abs(x[i]) > silenceThresh) { first = i; break; }
        }
        for (var i = n - 1; i >= 0; i--)
        {
            if (Math.Abs(x[i]) > silenceThresh) { last = i; break; }
        }

        var trimLen = last - first + 1;
        var shouldTrim = trimLen < n * 0.85 && trimLen >= NFft * 4;
        trimOffset = shouldTrim ? first : 0;

        if (!shouldTrim && scale == 1f)
        {
            return x;
        }

        var srcStart = shouldTrim ? first : 0;
        var outLen = shouldTrim ? trimLen : n;
        var result = new float[outLen];
        if (scale == 1f)
        {
            Array.Copy(x, srcStart, result, 0, outLen);
        }
        else
        {
            for (var k = 0; k < outLen; k++)
            {
                result[k] = x[srcStart + k] * scale;
            }
        }

        return result;
    }

    private static double Percentile(double[] data, double p)
    {
        if (data.Length == 0)
        {
            return 0;
        }

        var sorted = (double[]) data.Clone();
        Array.Sort(sorted);
        var rank = p / 100.0 * (sorted.Length - 1);
        var lo = (int) Math.Floor(rank);
        var hi = (int) Math.Ceiling(rank);
        if (lo == hi)
        {
            return sorted[lo];
        }

        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    private static double Median(double[] data) => Percentile(data, 50);

    private static void Fft(double[] re, double[] im)
    {
        var n = re.Length;
        System.Diagnostics.Debug.Assert((n & (n - 1)) == 0, $"FFT size must be power of two, got {n}");

        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2 * Math.PI / len;
            var wr = Math.Cos(ang);
            var wi = Math.Sin(ang);
            var half = len / 2;
            for (var i = 0; i < n; i += len)
            {
                double cwr = 1, cwi = 0;
                for (var k = 0; k < half; k++)
                {
                    var pr = re[i + k + half] * cwr - im[i + k + half] * cwi;
                    var pi = re[i + k + half] * cwi + im[i + k + half] * cwr;
                    var ur = re[i + k];
                    var ui = im[i + k];
                    re[i + k] = ur + pr;
                    im[i + k] = ui + pi;
                    re[i + k + half] = ur - pr;
                    im[i + k + half] = ui - pi;
                    var nwr = cwr * wr - cwi * wi;
                    cwi = cwr * wi + cwi * wr;
                    cwr = nwr;
                }
            }
        }
    }

    private static float[] DetectVocalActivity(float[][] power, int rate)
    {
        var n = power.Length;
        var vad = new float[n];

        var highStart = Math.Max(2, (int) (4000.0 * NFft / rate));
        var lowEnd = Math.Max(1, (int) (250.0 * NFft / rate));
        var hfEnd = Math.Min(Bins, (int) (8000.0 * NFft / rate));

        Parallel.For(0, n, f =>
        {
            var p = power[f];

            double logSum = 0;
            double arithSum = 0;
            int count = 0;
            for (var k = lowEnd; k < hfEnd; k++)
            {
                var v = Math.Max(p[k], 1e-10);
                logSum += Math.Log(v);
                arithSum += v;
                count++;
            }
            var flatness = count > 0 ? Math.Exp(logSum / count) / Math.Max(arithSum / count, 1e-10) : 1.0;

            double totalE = 0;
            double hfE = 0;
            for (var k = 1; k < Bins; k++)
            {
                totalE += p[k];
                if (k >= highStart) hfE += p[k];
            }
            var hfRatio = totalE > 0 ? hfE / totalE : 0;

            var score = (1.0 - Math.Min(flatness, 1.0)) * (1.0 - Math.Min(hfRatio, 1.0));
            vad[f] = (float) Math.Clamp(score * 2.5, 0.0, 1.0);
        });

        return vad;
    }

    private static int[] DetectVocalPhrases(float[] vadScores, double vadThresh, int nFrames, int rate)
    {
        var minGapFrames = Math.Max(2, (int) (0.2 * rate / Hop));
        var minPhraseFrames = Math.Max(2, (int) (0.5 * rate / Hop));
        var phrases = new List<int>();

        var inPhrase = false;
        var phraseStart = 0;
        var gapCount = 0;

        for (var f = 0; f < nFrames && f < vadScores.Length; f++)
        {
            var isVocal = vadScores[f] >= vadThresh;
            if (isVocal)
            {
                if (!inPhrase)
                {
                    inPhrase = true;
                    phraseStart = f;
                }
                gapCount = 0;
            }
            else
            {
                if (inPhrase)
                {
                    gapCount++;
                    if (gapCount > minGapFrames)
                    {
                        var phraseEnd = f - gapCount;
                        if (phraseEnd - phraseStart >= minPhraseFrames)
                        {
                            phrases.Add(phraseStart);
                            phrases.Add(phraseEnd);
                        }
                        inPhrase = false;
                    }
                }
            }
        }

        if (inPhrase)
        {
            var phraseEnd = Math.Min(nFrames - 1, vadScores.Length - 1);
            if (phraseEnd - phraseStart >= minPhraseFrames)
            {
                phrases.Add(phraseStart);
                phrases.Add(phraseEnd);
            }
        }

        return phrases.ToArray();
    }
}