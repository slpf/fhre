using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FH6RB.Core;
using FH6RB.Services;
using FH6RB.ViewModels;

internal static class RadioBuildChecks
{
    public static async Task RunAsync(string temp, string originalAudio, string replacementAudio, Action<string, int> writeEmptyBank)
    {
        const string bankName = "R1_Tracks";
        const string secondBank = "R1_Tracks_CU1";
        const string stock = "STOCK_TRACK";
        const string english = "RadioInfo_EN.xml";
        const string german = "RadioInfo_DE.xml";
        var game = Directory.CreateDirectory(Path.Combine(temp, "radio-edits")).FullName;
        var bank = Path.Combine(game, bankName + ".assets.bank");
        var sibling = Path.Combine(game, secondBank + ".assets.bank");
        var settings = new AppSettings { GamePath = game, LoudnessNormalize = false };
        writeEmptyBank(sibling, 12);
        await BankBuildService.BuildToFileAsync(sibling, bank,
            [new BuildItem(stock, true, originalAudio, null, null, null, true)], settings);

        foreach (var file in new[] { english, german })
        {
            File.WriteAllText(Path.Combine(game, file), $"""
                <Radio><RadioStation Number="1" Name="Test"><Banks><Bank Name="{bankName}"/></Banks>
                  <SampleList Type="Track"><Sample SoundName="{stock}" DisplayName="Stock" Artist="{file}"
                    SampleLength="144000" SampleRate="48000" End="143999"><BPM Start="0" Value="128"/></Sample></SampleList>
                  <PlayList Type="FreeRoam"><Entry Name="{stock}"/></PlayList>
                </RadioStation></Radio>
                """);
        }

        var vm = (MainWindowViewModel) RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
        const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(MainWindowViewModel).GetField("<Settings>k__BackingField", fields)!.SetValue(vm, settings);
        var loadedRadio = typeof(MainWindowViewModel).GetField("_loadedRadio", fields)!;
        var build = typeof(MainWindowViewModel).GetMethod("BuildGameFilesAsync", fields)!;
        var resources = typeof(GameScanner).GetMethod("ReadRadioResources", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [game]);
        var station = RadioCatalog.Build(game, Load(english)).Single();
        RadioInfo Load(string file) => RadioInfo.Load(Path.Combine(game, file));
        List<BuildItem> Read(string file) => BankReader.ReadTracks(bank, Load(file), 1).Tracks
            .Select(track => new BuildItem(track.SoundName, false, null, track.DisplayName, track.Artist, null, track.Enabled,
                track.Markers, MarkerSampleRate: track.SampleRate, BankId: track.BankId)).ToList();
        Task<(IReadOnlyList<AddedSample> Added, long Written, int SavedXml, int Dead)> Build(string file, List<BuildItem> items,
            string[]? other = null, string name = bankName, Action<string>? progress = null) =>
            (Task<(IReadOnlyList<AddedSample>, long, int, int)>) build.Invoke(vm,
                [game, station, name, Path.Combine(game, name + ".assets.bank"), Path.Combine(game, file), other ?? [],
                    resources, items, settings, progress ?? (_ => { })])!;

        var stale = Read(english);
        loadedRadio.SetValue(vm, Load(english));
        var external = Load(english);
        external.Document.Root!.SetAttributeValue("BeforeBuild", "preserved");
        external.Save(Path.Combine(game, english));
        await RejectAsync(() => Build(english, [stale[0] with { Enabled = false }]), "changed on disk");
        loadedRadio.SetValue(vm, null);
        Check(Load(english).StationByNumber(1)!.ReadTracks().Single().Enabled, "A stale edit changed the playlist.");

        var replacement = Read(english)[0] with { IsReplacement = true, SourcePath = replacementAudio };
        var originalBank = File.ReadAllBytes(bank);
        await RejectAsync(() => Build(english, [replacement], progress: _ =>
        {
            var current = Load(english);
            current.Document.Root!.SetAttributeValue("DuringBuild", "preserved");
            current.Save(Path.Combine(game, english));
        }), "changed on disk");
        Check((string?) Load(english).Document.Root!.Attribute("DuringBuild") == "preserved"
            && File.ReadAllBytes(bank).SequenceEqual(originalBank), "A conflicting XML write changed the bank or lost the external edit.");

        var custom = new BuildItem(Naming.MakeSoundName(0), true, replacementAudio, "German title", "Local artist", null, true);
        await Build(german, [..Read(german), custom]);
        await Build(english, Read(english), [german]);
        Check(Load(german).StationByNumber(1)!.GetSampleMeta(custom.SoundName)?.DisplayName == custom.DisplayName,
            "A reference locale without a sample deleted the other locale's metadata.");

        var enabled = Read(english).Select(item => item.SoundName == custom.SoundName ? item with { Enabled = true } : item).ToList();
        await Build(english, enabled);
        Check(Read(english).Single(item => item.SoundName == custom.SoundName) is { Enabled: true, Artist: "Local artist" },
            "Enabling audio from another language failed to create its local XML entry.");
        var unused = vm.CreateCustomStub(Path.Combine(temp, "absent.wav")).SoundName;
        Check(unused != custom.SoundName, "The allocator reused an occupied ID.");
        await RejectAsync(() => Build(english, [custom], name: secondBank), "already in use");

        var rename = Read(english).Select(item => item.SoundName == stock ? item with { DisplayName = "New title" } : item).ToList();
        await Build(english, rename, [german]);
        Check(Load(german).StationByNumber(1)!.GetSampleMeta(stock) == ("New title", german),
            "A title-only edit overwrote another language's artist.");

        await Build(english, Read(english).Select(item => item.SoundName == stock ? replacement : item).ToList());
        await Build(english, Read(english).Select(item => item.SoundName == stock ? stale[0] with { RestoreFromBak = true } : item).ToList());
        foreach (var file in new[] { english, german })
        {
            var row = Load(file).Document.Descendants("Sample").Single(sample => (string?) sample.Attribute("SoundName") == stock);
            Check((string?) row.Element("BPM")?.Attribute("Value") == "128" && row.Attribute("Replaced") is null,
                "Restoring a track lost its original BPM or kept its replacement flag.");
        }

        var radio = Load(english);
        var note = $"Migration note containing Name=\"{custom.SoundName}\"";
        radio.Document.Descendants("PlayList").Single().Add(new XComment(note));
        radio.Save(Path.Combine(game, english));
        await Build(english, Read(english).Where(item => item.SoundName != custom.SoundName).ToList());
        Check(Load(english).Document.DescendantNodes().OfType<XComment>().Any(comment => comment.Value == note),
            "Deleting a track removed an unrelated playlist comment.");
        Console.WriteLine("Verified RadioInfo conflicts, cross-language custom tracks, ID reservations, localized metadata and BPM restoration.");
    }

    private static async Task RejectAsync(Func<Task> action, string message)
    {
        try
        {
            await action();
        }
        catch (IOException ex) when (ex.Message.Contains(message, StringComparison.Ordinal))
        {
            return;
        }
        throw new InvalidOperationException($"Expected the build to be rejected with '{message}'.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
