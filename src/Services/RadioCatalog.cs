using System.Text.RegularExpressions;
using FH6RB.Core;

namespace FH6RB.Services;

public sealed record StationInfo(int Number, string Name, IReadOnlyList<string> Variants, IReadOnlyDictionary<string, string> VariantBanks)
{
    public string Display => $"R{Number} · {Name}";
    
    public string Prefix => $"R{Number}_Tracks_";
    
    public string BankName(string variant) => VariantBanks.TryGetValue(variant, out var b) ? b : $"R{Number}_Tracks_{variant}";
}

public static partial class RadioCatalog
{
    public static List<StationInfo> Build(string gamePath, RadioInfo? radio) =>
        Build(GameScanner.RadioBankNames(gamePath), radio);

    internal static List<StationInfo> Build(IReadOnlyList<string> banks, RadioInfo? radio)
    {
        var groups = new Dictionary<int, Dictionary<string, string>>();

        foreach (var bank in banks)
        {
            if (!TryParseBank(bank, out var number, out var label)) continue;

            if (!groups.TryGetValue(number, out var map))
            {
                map = [];
                groups[number] = map;
            }

            map[label] = bank;
        }
        
        var names = new Dictionary<int, string>();
        
        if (radio is not null)
        {
            foreach (var st in radio.Document.Descendants("RadioStation"))
            {
                if (int.TryParse((string?) st.Attribute("Number"), out var n))
                {
                    names.TryAdd(n, (string?)st.Attribute("Name") ?? $"Station {n}");
                }
            }
        }

        var stations = groups
            .Where(g => radio is null || names.ContainsKey(g.Key))
            .OrderBy(g => g.Key)
            .Select(g => new StationInfo(g.Key, names.GetValueOrDefault(g.Key, $"Station {g.Key}"), g.Value.Keys.OrderBy(v => v).ToList(), g.Value))
            .Where(st => !string.Equals(st.Name, StreamerModeName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var summary = $"RadioCatalog: {stations.Count} stations";
        Log.Line(Log.IsVerbose
            ? string.Join(Environment.NewLine, stations.Select(st =>
                $"  #{st.Number} '{st.Name}' variants=[{string.Join(", ", st.Variants)}]").Prepend(summary))
            : summary);
        
        return stations;
    }

    private const string StreamerModeName = "Streamer Mode";

    internal static bool TryParseBank(string bank, out int number, out string label)
    {
        var match = TrackBankRegex().Match(bank);
        if (!match.Success) match = Horizon2Regex().Match(bank);
        label = match.Groups[2].Success ? match.Groups[2].Value : "Tracks";
        return int.TryParse(match.Groups[1].Value, out number) && number > 0;
    }

    [GeneratedRegex(@"^R(\d+)_Tracks(?:_(.+))?$")]
    private static partial Regex TrackBankRegex();

    [GeneratedRegex(@"^HZ2_MX_Radio_(\d+)_Tracks$", RegexOptions.IgnoreCase)]
    private static partial Regex Horizon2Regex();
}
