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
    public static List<StationInfo> Build(string gamePath, RadioInfo? radio)
    {
        var groups = new Dictionary<int, Dictionary<string, string>>();

        foreach (var bank in GameScanner.RadioBankNames(gamePath))
        {
            int number;
            string label;

            var m = VariantRegex().Match(bank);
            
            if (m.Success)
            {
                number = int.Parse(m.Groups[1].Value);
                label = m.Groups[2].Value;
            }
            else
            {
                var m4 = PlainRegex().Match(bank);
                
                if (!m4.Success)
                {
                    continue;
                }

                number = int.Parse(m4.Groups[1].Value);
                label = "Tracks";
            }

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
                if ((int?) st.Attribute("Number") is { } n)
                {
                    names[n] = (string?)st.Attribute("Name") ?? $"Station {n}";
                }
            }
        }

        var stations = groups
            .OrderBy(g => g.Key)
            .Select(g => new StationInfo(g.Key, names.GetValueOrDefault(g.Key, $"Station {g.Key}"), g.Value.Keys.OrderBy(v => v).ToList(), g.Value))
            .ToList();

        Log.Line($"RadioCatalog: {stations.Count} stations");
        
        foreach (var st in stations)
        {
            Log.Line($"  #{st.Number} '{st.Name}' variants=[{string.Join(", ", st.Variants)}]");
        }
        
        return stations;
    }

    [GeneratedRegex(@"^R(\d+)_Tracks_(.+)$")]
    private static partial Regex VariantRegex();

    [GeneratedRegex(@"^R(\d+)_Tracks$")]
    private static partial Regex PlainRegex();
}
