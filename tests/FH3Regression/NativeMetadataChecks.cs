using System.Xml.Linq;
using FH6RB.Core;
using FH6RB.Services;
using FH6RB.ViewModels;
using static TestSupport;

internal static class NativeMetadataChecks
{
    public static void Run(string game, IReadOnlyList<string> banks, IReadOnlyList<string> languages, string root)
    {
        var named = 0;
        foreach (var language in languages)
        {
            var radio = RadioInfo.Load(GameScanner.RadioInfoPathByFile(game, language)!);
            var samples = radio.Document.Descendants("SampleList").Where(e => (string?) e.Attribute("Type") == "Track")
                .Elements("Sample").ToArray();
            Check(samples.Length == 153 && samples.All(s => (int?) s.Attribute("SampleRate") == 48000
                && s.Attribute("TrackBreakDown") is not null && s.Attribute("PostDrop") is null
                && (long?) s.Attribute("DJStart") - (long?) s.Attribute("StingerStart") == 1000),
                language + " has an unexpected native marker schema or StingerStart lead.");
            var records = 0;
            var orphans = 0;
            foreach (var bankName in banks)
            {
                var station = radio.StationForBank(bankName)!;
                var stationNode = radio.Document.Descendants("RadioStation").Single(s => s.Descendants("Bank")
                    .Any(b => (string?) b.Attribute("Name") == bankName));
                var read = BankReader.ReadTracks(GameScanner.BankPath(game, bankName)!, radio, (int) stationNode.Attribute("Number")!);
                Check(read.Error is null, bankName + ": " + read.Error);
                foreach (var track in read.Tracks)
                {
                    records++;
                    var row = new TrackItemViewModel(track);
                    if (row.IsBankOnly) { orphans++; continue; }
                    Check(track.UsesTrackBreakDown && !track.UsesMilliseconds && track.SampleRate == 48000,
                        "FH3 native sample positions were interpreted as milliseconds.");
                    Check(new EditWindowViewModel(row).ValidateMarkers().Count == 0,
                        "The marker editor rejected native metadata for " + track.SoundName);
                    var meta = station.GetSampleMeta(track.SoundName);
                    Check(meta is not null && meta.Value.DisplayName == track.DisplayName && meta.Value.Artist == track.Artist,
                        "Bank metadata overlay changed a title or artist.");
                    named++;
                }
            }
            Check(records == 156 && orphans == 3, "Native bank/XML identities changed for " + language);
            var copy = Path.Combine(root, "native-" + language);
            radio.Save(copy);
            Check(XNode.DeepEquals(radio.Document.Root, RadioInfo.Load(copy).Document.Root),
                "A native XML save changed track metadata or playlists in " + language);
        }
        Console.WriteLine($"Verified {named} native named track records across {languages.Count} languages, all 10 banks, sample units, TrackBreakDown/StingerStart, editor validation and lossless XML roundtrips.");
    }
}
