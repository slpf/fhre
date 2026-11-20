namespace FH6RB.Core;

public sealed class BankEditor
{
    private readonly FevBank _bank;
    private readonly Fsb5 _fsb;
    private readonly Dictionary<int, ulong> _indexToHash;

    public BankEditor(byte[] sourceAssetsBank)
    {
        _bank = new FevBank(sourceAssetsBank);
        _fsb = Fsb5.Parse(_bank.ExtractFsb5());
        var tracks = _bank.ReadStbl();
        FevBank.ValidateTrackIndices(tracks, _fsb.Samples.Count);
        _indexToHash = tracks.ToDictionary(track => track.Index, track => track.Id);

        if (_indexToHash.Count != _fsb.Samples.Count)
        {
            throw new InvalidDataException($"STBL entries ({_indexToHash.Count}) != FSB5 samples ({_fsb.Samples.Count})");
        }
    }

    public int SourceSampleCount => _fsb.Samples.Count;
    public IReadOnlyList<Fsb5Sample> SourceSamples => _fsb.Samples;
    public ulong HashForIndex(int sourceIndex) => _indexToHash[sourceIndex];
    
    public byte[] Build(IReadOnlyList<PlanItem> plan)
    {
        if (plan.Count == 0) throw new ArgumentException("plan is empty");

        var outSamples = new List<Fsb5Sample>(plan.Count);
        var stbl = new List<(ulong Id, int Index)>(plan.Count);
        var seen = new HashSet<ulong>();
        var originalNames = _indexToHash.ToDictionary(entry => entry.Value, entry => _fsb.Samples[entry.Key].Name);
        var names = new byte[]?[plan.Count];

        for (var newIndex = 0; newIndex < plan.Count; newIndex++)
        {
            var it = plan[newIndex];
            Fsb5Sample sample;
            ulong hash;

            if (it.IsNew)
            {
                sample = it.NewSample ?? throw new InvalidOperationException("new item missing sample");
                hash = it.Hash;
            }
            else
            {
                sample = _fsb.Samples[it.SourceIndex];
                hash = _indexToHash[it.SourceIndex];
            }

            if (!seen.Add(hash))
            {
                throw new InvalidOperationException($"duplicate STBL id 0x{hash:x16} in plan (index {newIndex})");
            }

            names[newIndex] = originalNames.GetValueOrDefault(hash) ?? sample.Name;
            outSamples.Add(sample);
            stbl.Add((hash, newIndex));
        }

        var newFsb5 = _fsb.Build(outSamples, Fsb5.BuildNameTable(names));
        var stblPayload = FevBank.BuildStbl(stbl);
        
        return _bank.WithStblAndFsb(stblPayload, newFsb5);
    }
}

public sealed class PlanItem
{
    public bool IsNew { get; private init; }
    public int SourceIndex { get; private init; }
    public ulong Hash { get; private init; }
    public Fsb5Sample? NewSample { get; private init; }
    
    public static PlanItem Keep(int sourceIndex) => new() { IsNew = false, SourceIndex = sourceIndex };
    public static PlanItem Add(ulong hash, Fsb5Sample sample) => new() { IsNew = true, Hash = hash, NewSample = sample };
}
