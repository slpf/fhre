namespace FH6RB.Core;

public readonly struct LoopPair
{
    public long LoopStart { get; init; }
    public long LoopEnd { get; init; }
    public float NoteDistance { get; init; }
    public float LoudnessDifference { get; init; }
    public float Score { get; init; }
}

public static class LoopFinder
{
    private const int NFft = 2048;
    private const int Hop = 512;
    private const int Bins = NFft / 2 + 1;
    private const int NChroma = 12;

    private const double AcceptableNoteDeviation = 0.0875;
    private const double AcceptableLoudnessDifference = 0.5;

    public static List<LoopPair> Find(float[]? mono, int rate,
        double minDurationMultiplier = 0.35, double? minLoopSeconds = null, double? maxLoopSeconds = null,
        int maxResults = 10, bool bruteForce = false)
    {
        var result = new List<LoopPair>();

        if (mono is null || mono.Length < NFft * 4 || rate <= 0)
        {
            return result;
        }

        var total = mono.Length;
        var minLoop = minLoopSeconds is not null
            ? SecondsToFrames(minLoopSeconds.Value, rate)
            : SecondsToFrames(minDurationMultiplier * total / rate, rate);
        var maxLoop = maxLoopSeconds is not null
            ? SecondsToFrames(maxLoopSeconds.Value, rate)
            : SecondsToFrames((double) total / rate, rate);
        minLoop = Math.Max(1, minLoop);

        var power = Stft(mono);
        var nFrames = power.Length;
        if (nFrames < 8)
        {
            return result;
        }

        var chroma = Chroma(power, rate);
        var loudness = Loudness(power, rate);
        var stride = bruteForce ? 1 : CoarseStride(nFrames);
        var anchors = StridedFrames(nFrames, stride);
        if (anchors.Length < 2)
        {
            return result;
        }

        var testOffset = TestOffset(nFrames, rate);
        var weights = Weights(testOffset, Math.Max(2, testOffset / 12), 1);

        var candidates = FindCandidatePairs(chroma, loudness, anchors, minLoop, maxLoop, bruteForce ? 1.0 : 1.3);
        if (candidates.Count == 0)
        {
            return result;
        }

        Assess(chroma, candidates, testOffset, weights);

        if (!bruteForce && stride > 1)
        {
            Refine(candidates, chroma, loudness, testOffset, weights, stride, minLoop, maxLoop, nFrames);
        }

        if (candidates.Count > 1)
        {
            PrioritizeDuration(candidates);
        }

        var picked = new List<Cand>();
        var tol = (int) (1.0 * rate / Hop);

        foreach (var p in candidates)
        {
            var dup = false;
            foreach (var q in picked)
            {
                if (Math.Abs(p.StartFrame - q.StartFrame) <= tol && Math.Abs(p.EndFrame - q.EndFrame) <= tol)
                {
                    dup = true;
                    break;
                }
            }

            if (dup)
            {
                continue;
            }

            picked.Add(p);

            var s = NearestZeroCrossing(mono, rate, (long) p.StartFrame * Hop);
            var e = NearestZeroCrossing(mono, rate, (long) p.EndFrame * Hop);
            result.Add(new LoopPair
            {
                LoopStart = s,
                LoopEnd = e,
                NoteDistance = (float) p.NoteDistance,
                LoudnessDifference = (float) p.LoudnessDifference,
                Score = (float) p.Score,
            });

            if (result.Count >= maxResults)
            {
                break;
            }
        }

        return result;
    }

    private sealed class Cand
    {
        public int StartFrame;
        public int EndFrame;
        public double NoteDistance;
        public double LoudnessDifference;
        public double Score;
    }

    private static int TestOffset(int nFrames, int rate)
    {
        var t = (int) (6.0 * rate / Hop);
        if (t > nFrames)
        {
            t = nFrames / 4;
        }

        return Math.Max(1, t);
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

    // ---- candidate generation (port of _find_candidate_pairs) ----
    private static List<Cand> FindCandidatePairs(float[][] chroma, double[] loudness, int[] beats,
        int minLoop, int maxLoop, double gateScale = 1.0)
    {
        var list = new List<Cand>();

        var deviation = new double[beats.Length];
        for (var i = 0; i < beats.Length; i++)
        {
            deviation[i] = Norm(chroma[beats[i]]) * AcceptableNoteDeviation * gateScale;
        }

        for (var idx = 0; idx < beats.Length; idx++)
        {
            var loopEnd = beats[idx];
            foreach (var loopStart in beats)
            {
                var len = loopEnd - loopStart;
                if (len < minLoop)
                {
                    break;
                }

                if (len > maxLoop)
                {
                    continue;
                }

                var noteDistance = NormDiff(chroma[loopEnd], chroma[loopStart]);
                if (noteDistance <= deviation[idx])
                {
                    var loud = Math.Abs(loudness[loopEnd] - loudness[loopStart]);
                    if (loud <= AcceptableLoudnessDifference)
                    {
                        list.Add(new Cand
                        {
                            StartFrame = loopStart,
                            EndFrame = loopEnd,
                            NoteDistance = noteDistance,
                            LoudnessDifference = loud,
                        });
                    }
                }
            }
        }

        return list;
    }

    // ---- scoring + pruning (port of _assess_and_filter_loop_pairs) ----
    private static void Assess(float[][] chroma, List<Cand> candidates, int testOffset, double[] weights)
    {
        var pruned = candidates.Count >= 100 ? Prune(candidates) : candidates;

        foreach (var c in pruned)
        {
            c.Score = LoopScore(c.StartFrame, c.EndFrame, chroma, testOffset, weights);
        }

        pruned.Sort((a, b) => b.Score.CompareTo(a.Score));

        candidates.Clear();
        candidates.AddRange(pruned);
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

    private static double LoopScore(int b1, int b2, float[][] chroma, int testDuration, double[] weights)
    {
        var ahead = SubseqSimilarity(b1, b2, chroma, testDuration, weights);
        var rev = (double[]) weights.Clone();
        Array.Reverse(rev);
        var behind = SubseqSimilarity(b1, b2, chroma, -testDuration, rev);
        return Math.Max(ahead, behind);
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

        var sims = new double[maxOffset];
        for (var i = 0; i < maxOffset; i++)
        {
            var a = chroma[b1Start + i];
            var b = chroma[b2Start + i];
            double dot = 0, na = 0, nb = 0;
            for (var k = 0; k < NChroma; k++)
            {
                dot += a[k] * b[k];
                na += a[k] * a[k];
                nb += b[k] * b[k];
            }

            sims[i] = dot / Math.Max(Math.Sqrt(na) * Math.Sqrt(nb), 1e-10);
        }

        // weighted average over testLength (zero-pad missing tail to match weights length)
        double num = 0, den = 0;
        for (var i = 0; i < testLength; i++)
        {
            var w = i < weights.Length ? weights[i] : weights[^1];
            var v = i < maxOffset ? sims[i] : 0.0;
            num += v * w;
            den += w;
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

    // ---- nearest zero crossing (port of Audacity / PyMusicLooper) ----
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

        return sampleIdx + argmin - offset + offsetCorrection;
    }

    // ---- DSP front-end ----
    private static float[][] Stft(float[] x)
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

        Parallel.For(0, nFrames, () => (new double[NFft], new double[NFft]), (f, _, buf) =>
        {
            var (re, im) = buf;
            var center = f * Hop;
            for (var i = 0; i < NFft; i++)
            {
                var src = center - pad + i;
                var s = src >= 0 && src < n ? x[src] : 0.0;

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

    private static float[][] Chroma(float[][] power, int rate)
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
                for (var ch = 0; ch < NChroma; ch++)
                {
                    c[ch] /= max;
                }
            }

            chroma[f] = c;
        });

        return chroma;
    }

    private static double[][] ChromaFilterbank(int rate)
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

        // base_c: roll rows by -3 so C is index 0
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

        var n = power.Length;
        var loud = new double[n];
        for (var f = 0; f < n; f++)
        {
            var p = power[f];
            var maxWeighted = double.NegativeInfinity;
            for (var k = 0; k < Bins; k++)
            {
                var db = 10 * Math.Log10(Math.Max(1e-10, p[k])) + aw[k];
                if (db > maxWeighted)
                {
                    maxWeighted = db;
                }
            }

            loud[f] = 10 * Math.Log10(Math.Max(1e-10, maxWeighted));
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

    // ---- math helpers ----
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

}
