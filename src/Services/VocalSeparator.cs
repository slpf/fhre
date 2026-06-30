#if false
using System.Collections.Concurrent;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FH6RB.Services;

public static class VocalSeparator
{
    private const int ModelRate = 44100;
    private const int ChunkDefault = 262144;
    private const double Overlap = 0.5;

    private static readonly ConcurrentDictionary<string, float[]> _cache = new();
    private static readonly object _gate = new();
    private static InferenceSession? _session;
    private static Contract? _contract;
    private static bool _tried;

    public static bool IsAvailable => Tools.HasDemucsModel;

    public static float[]? Separate(string wavPath, float[] mono, int rate, CancellationToken ct = default)
    {
        if (!IsAvailable || mono.Length < 4096 || rate <= 0) return null;

        var key = wavPath + "|" + rate + "|" + mono.Length;
        if (_cache.TryGetValue(key, out var hit)) return hit;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            EnsureSession();
            if (_session is null || _contract is null)
            {
                Log.Line("[VocalSeparator] no session available");
                return null;
            }

            var modelMono = rate == ModelRate ? mono : ResampleLinear(mono, rate, ModelRate);

            float[]? vocalModel = null;
            try
            {
                vocalModel = SeparateModel(_session, _contract, modelMono, ct);
            }
            catch (Exception ex)
            {
                Log.Line($"[VocalSeparator] inference error ({_contract.Ep}): {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
            }

            if (vocalModel is null && !ct.IsCancellationRequested && _contract.Ep == "dml" && EnsureCpuSession())
            {
                try { vocalModel = SeparateModel(_session!, _contract!, modelMono, ct); }
                catch (Exception ex) { Log.Line($"[VocalSeparator] inference error (cpu): {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
            }

            if (ct.IsCancellationRequested) return null;

            if (vocalModel is null) return null;

            var vocal = rate == ModelRate ? vocalModel : ResampleLinear(vocalModel, ModelRate, rate);
            vocal = MatchLength(vocal, mono.Length);

            var (rms, peak) = LevelStats(vocal);
            Log.Line($"[VocalSeparator] {Path.GetFileName(wavPath)} ep={_contract.Ep} stems={_contract.StemCount} chunk={_contract.ChunkSize} {sw.ElapsedMilliseconds}ms rms={rms:0.0000} peak={peak:0.000}");
            _cache[key] = vocal;
            return vocal;
        }
        catch (Exception ex)
        {
            Log.Line($"[VocalSeparator] failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static void EnsureSession()
    {
        if (_tried) return;
        lock (_gate)
        {
            if (_tried) return;
            _tried = true;
            try
            {
                var modelPath = Tools.DemucsModelPath();
                if (modelPath is null) return;
                BootNative();

                var (session, ep) = CreateSession(modelPath);
                var contract = ProbeContract(session);
                if (contract is not null) contract.Ep = ep;

                if (contract is null || contract.InDims.Length > 3 || contract.ChunkSize < 4096)
                {
                    Log.Line($"[VocalSeparator] unsupported contract inDims=[{JoinDims(contract?.InDims)}] outDims=[{JoinDims(contract?.OutDims)}] chunk={contract?.ChunkSize} — need a waveform-in/out model (input ~[N] or [1,2,N]); this looks spectrogram-domain (MDX/STFT). Skipping ML, using full-mix analysis.");
                    var custom = session.ModelMetadata?.CustomMetadataMap;
                    if (custom is { Count: > 0 })
                        Log.Line($"[VocalSeparator] model metadata: {string.Join(" ", custom.Select(kv => $"{kv.Key}={kv.Value}"))}");
                    session.Dispose();
                    _session = null;
                    _contract = null;
                    return;
                }

                _session = session;
                _contract = contract;
                Log.Line($"[VocalSeparator] loaded {Path.GetFileName(modelPath)} ep={ep} stems={contract.StemCount} chunk={contract.ChunkSize} inDims=[{JoinDims(contract.InDims)}] outDims=[{JoinDims(contract.OutDims)}]");
            }
            catch (Exception ex)
            {
                Log.Line($"[VocalSeparator] load failed: {ex.GetType().Name}: {ex.Message}");
                _session = null;
                _contract = null;
            }
        }
    }

    private static bool EnsureCpuSession()
    {
        lock (_gate)
        {
            if (_contract is { Ep: "cpu" }) return _session is not null;
            try
            {
                var modelPath = Tools.DemucsModelPath();
                if (modelPath is null) return false;
                _session?.Dispose();
                BootNative();
                var session = new InferenceSession(modelPath, new SessionOptions());
                _session = session;
                if (_contract is not null) _contract.Ep = "cpu";
                Log.Line("[VocalSeparator] DML inference failed — switched to CPU session (slower)");
                return true;
            }
            catch (Exception ex)
            {
                Log.Line($"[VocalSeparator] CPU session failed: {ex.GetType().Name}: {ex.Message}");
                _session = null;
                _contract = null;
                return false;
            }
        }
    }

    private static (InferenceSession Session, string Ep) CreateSession(string modelPath)
    {
        try
        {
            var dml = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC };
            dml.AppendExecutionProvider_DML(0);
            return (new InferenceSession(modelPath, dml), "dml");
        }
        catch (Exception ex)
        {
            Log.Line($"[VocalSeparator] DML unavailable ({ex.GetType().Name}), falling back to CPU");
            return (new InferenceSession(modelPath, new SessionOptions()), "cpu");
        }
    }

    private static void BootNative()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ORT_DYLIB_PATH"))) return;
        var root = Tools.Root;
        var candidates = new[] { root, Path.Combine(root, "libs"), Path.Combine(root, "runtimes", "win-x64", "native") };
        foreach (var c in candidates)
        {
            var p = Path.Combine(c, "onnxruntime.dll");
            if (File.Exists(p))
            {
                Environment.SetEnvironmentVariable("ORT_DYLIB_PATH", p);
                return;
            }
        }
    }

    private static Contract ProbeContract(InferenceSession session)
    {
        var inMeta = session.InputMetadata.First();
        var outMeta = session.OutputMetadata.First();
        var inDims = inMeta.Value.Dimensions.Select(d => (int) d).ToArray();
        var outDims = outMeta.Value.Dimensions.Select(d => (int) d).ToArray();

        var chunkSize = ChunkDefault;
        if (inDims.Length > 0 && inDims[^1] > 0) chunkSize = inDims[^1];
        if (session.ModelMetadata?.CustomMetadataMap.TryGetValue("chunk_size", out var cs) == true
            && int.TryParse(cs, out var parsed) && parsed > 0)
        {
            chunkSize = parsed;
        }

        var inputChannels = inDims.Length >= 2 && inDims[^2] == 2 ? 2 : 1;

        var multiStem = outDims.Length > inDims.Length;
        var stemCount = 1;
        if (multiStem)
        {
            var best = 1;
            for (var i = 0; i < outDims.Length - 2; i++) best = Math.Max(best, outDims[i]);
            stemCount = Math.Max(2, best);
        }

        return new Contract
        {
            InputName = inMeta.Key,
            OutputName = outMeta.Key,
            InDims = inDims,
            OutDims = outDims,
            ChunkSize = chunkSize,
            InputChannels = inputChannels,
            StemCount = stemCount,
            VocalIndex = stemCount - 1,
            SingleStem = !multiStem,
        };
    }

    private static float[]? SeparateModel(InferenceSession session, Contract c, float[] src, CancellationToken ct)
    {
        var chunk = c.ChunkSize;
        var hop = Math.Max(1, (int) Math.Round(chunk * (1.0 - Overlap)));
        var window = Hann(chunk);
        var outBuf = new float[src.Length];
        var norm = new float[src.Length];
        var inShape = ResolveShape(c.InDims, chunk, null);
        var totalIn = Product(inShape);

        for (var start = 0; start < src.Length; start += hop)
        {
            if (ct.IsCancellationRequested) return null;
            var inputBuf = new float[totalIn];
            var take = Math.Min(chunk, src.Length - start);
            for (var n = 0; n < take; n++)
            {
                var v = src[start + n] * window[n];
                if (c.InputChannels == 2)
                {
                    inputBuf[n] = v;
                    inputBuf[chunk + n] = v;
                }
                else
                {
                    inputBuf[n] = v;
                }
            }

            var tensor = new DenseTensor<float>(inputBuf, inShape);
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(c.InputName, tensor) };
            using var results = session.Run(inputs);
            var outTensor = results.FirstOrDefault()?.AsTensor<float>();
            if (outTensor is null) return null;
            var outArr = outTensor.ToArray();

            var outShape = ResolveShape(c.OutDims, chunk, outArr.Length);
            var sampleSize = outShape[^1];
            var channelSize = outShape.Length >= 2 && outShape[^2] == c.InputChannels ? c.InputChannels : 1;
            var offset = c.SingleStem ? 0 : c.VocalIndex * channelSize * sampleSize;

            var endLimit = Math.Min(sampleSize, src.Length - start);
            for (var n = 0; n < endLimit; n++)
            {
                double sum = 0.0;
                for (var ch = 0; ch < channelSize; ch++) sum += outArr[offset + ch * sampleSize + n];
                sum /= channelSize;
                outBuf[start + n] += (float) (sum * window[n]);
                norm[start + n] += window[n] * window[n];
            }

            if (start + chunk >= src.Length) break;
        }

        for (var i = 0; i < outBuf.Length; i++)
        {
            outBuf[i] = norm[i] > 1e-8f ? outBuf[i] / norm[i] : 0f;
        }
        return outBuf;
    }

    private static int[] ResolveShape(int[] dims, int chunk, long? totalLen)
    {
        var shape = (int[]) dims.Clone();
        long known = 1;
        var negCount = 0;
        var negIdx = -1;
        for (var i = 0; i < shape.Length; i++)
        {
            if (shape[i] > 0) known *= shape[i];
            else { negCount++; negIdx = i; }
        }

        if (negCount == 0) return shape;
        if (negCount == 1)
        {
            shape[negIdx] = totalLen.HasValue ? (int) (totalLen.Value / Math.Max(1, known)) : (negIdx == shape.Length - 1 ? chunk : 1);
            return shape;
        }

        for (var i = 0; i < shape.Length; i++)
        {
            if (shape[i] <= 0) shape[i] = i == shape.Length - 1 ? chunk : 1;
        }
        return shape;
    }

    private static int Product(int[] a)
    {
        var p = 1;
        foreach (var v in a) p *= v;
        return p;
    }

    private static float[] Hann(int n)
    {
        var w = new float[n];
        for (var i = 0; i < n; i++) w[i] = (float) (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
        return w;
    }

    private static float[] ResampleLinear(float[] src, int srcRate, int dstRate)
    {
        if (srcRate == dstRate || src.Length == 0) return src;
        var dstLen = (int) Math.Round((double) src.Length * dstRate / srcRate);
        if (dstLen <= 0) return Array.Empty<float>();
        var dst = new float[dstLen];
        var ratio = (double) srcRate / dstRate;
        for (var i = 0; i < dstLen; i++)
        {
            var srcPos = i * ratio;
            var i0 = (int) srcPos;
            var frac = srcPos - i0;
            var a = i0 >= 0 && i0 < src.Length ? src[i0] : 0f;
            var b = i0 + 1 >= 0 && i0 + 1 < src.Length ? src[i0 + 1] : a;
            dst[i] = (float) (a + (b - a) * frac);
        }
        return dst;
    }

    private static float[] MatchLength(float[] src, int len)
    {
        if (src.Length == len) return src;
        var r = new float[len];
        Array.Copy(src, r, Math.Min(src.Length, len));
        return r;
    }

    private static string JoinDims(int[]? dims) => dims is null ? "" : string.Join(",", dims);

    private static (double Rms, double Peak) LevelStats(float[] x)
    {
        double sum = 0, peak = 0;
        for (var i = 0; i < x.Length; i++)
        {
            var v = x[i];
            sum += v * v;
            var a = Math.Abs(v);
            if (a > peak) peak = a;
        }
        return (Math.Sqrt(sum / Math.Max(1, x.Length)), peak);
    }

    private sealed class Contract
    {
        public string InputName = "";
        public string OutputName = "";
        public int[] InDims = Array.Empty<int>();
        public int[] OutDims = Array.Empty<int>();
        public int ChunkSize = ChunkDefault;
        public int InputChannels = 1;
        public int StemCount = 1;
        public int VocalIndex = 0;
        public bool SingleStem = true;
        public string Ep = "cpu";
    }
}
#endif
