using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using FH6RB.Core;
using FH6RB.Services;
using static TestSupport;

internal static class UwpBuildChecks
{
    public static async Task RunAsync(string root)
    {
        var input = Path.Combine(root, "uwp-input.wav");
        WriteWave(input);
        var nativeWav = Path.Combine(root, "uwp-native.wav");
        await Loudnorm.RenderAsync(input, nativeWav, new AppSettings { LoudnessNormalize = false });
        var fsb = Path.Combine(root, "uwp-native.fsb");
        var cache = Path.Combine(root, "uwp-fsbank-cache");
        Directory.CreateDirectory(cache);
        var encode = Proc.Run(Tools.FsbankclPath, $"-format fadpcm -o \"{fsb}\" \"{nativeWav}\" -cache_dir \"{cache}\"");
        Check(encode.ExitCode == 0 && File.Exists(fsb), "Could not generate a native FADPCM fixture: " + encode.StdErr + encode.StdOut);
        var native = Fsb5.Parse(File.ReadAllBytes(fsb));
        var template = Path.Combine(root, "uwp-empty.bank");
        WriteTemplate(template);
        var bankData = new FevBank(File.ReadAllBytes(template)).FillEmpty(
            FevBank.BuildStbl([(Lookup.SoundNameToId("S&--"), 0), (Lookup.SoundNameToId("S2"), 1)]),
            native.Build([native.Samples[0], native.Samples[0]]));
        foreach (var obfuscated in new[] { false, true })
        {
            var game = Path.Combine(root, obfuscated ? "uwp-build-obfuscated" : "uwp-build-normal");
            var audio = Path.Combine(game, obfuscated ? "gq6$l" : "media", obfuscated ? "l=6$h" : "audio");
            var banks = Path.Combine(audio, obfuscated ? "vgh6hu=~" : "fmodopus", obfuscated ? "]j(" : "x64");
            Directory.CreateDirectory(banks);
            File.WriteAllBytes(Path.Combine(game, "forza_x64_release_final.exe"), []);
            File.WriteAllText(Path.Combine(game, "AppxManifest.xml"), SyntheticChecks.Manifest);
            string Physical(string name) => obfuscated ? Fh3FileNameCodec.Encode(name.ToLowerInvariant()) : name;
            var xml = Path.Combine(audio, Physical("RadioInfo_EN.xml"));
            var bank = Path.Combine(banks, Physical("R1_Tracks.bank"));
            File.WriteAllText(xml, SyntheticChecks.Xml());
            File.WriteAllBytes(bank, bankData);
            var radio = RadioInfo.Load(xml);
            foreach (var sample in radio.Document.Descendants("Sample").Where(s => (string?) s.Attribute("SoundName") is "S&--" or "S2"))
            {
                sample.SetAttributeValue("SampleLength", native.Samples[0].Frames);
                sample.SetAttributeValue("End", native.Samples[0].Frames - 1);
                sample.SetAttributeValue("TrackBreakDown", native.Samples[0].Frames * 3 / 4);
                sample.SetAttributeValue("DJStart", native.Samples[0].Frames - 1);
                sample.SetAttributeValue("StingerStart", native.Samples[0].Frames - 2);
            }
            radio.Save(xml);
            Check(GameScanner.Scan(game).IsValid && GameScanner.DetectVersion(game) == 3
                && GameScanner.BankPath(game, "R1_Tracks") == bank, "The public UWP scanner lost a native physical bank path.");
            var read = BankReader.ReadTracks(bank, radio, 1);
            Check(read.Error is null && read.Tracks.Count == 2, "Could not read native UWP bank metadata.");
            var settings = new AppSettings { GamePath = game, LoudnessNormalize = true, TargetLufs = -23, VorbisQuality = 95 };
            var before = BankState(bank);
            var items = read.Tracks.Select(Item).ToList();
            items[0] = items[0] with { SourcePath = input, IsReplacement = true, GainDb = -3, Markers = null };
            var custom = Naming.MakeSoundName(303);
            items.Add(new BuildItem(custom, true, input, "UWP & custom", "Artist", -3, false));
            var output = Path.Combine(root, $"uwp-{obfuscated}.output.bank");
            var log = new ConcurrentQueue<string>();
            var added = await BankBuildService.BuildToFileAsync(bank, output, items, settings, log.Enqueue);
            var after = BankState(output);
            Check(FevBank.ReadSkeleton(output).Mode == 16 && after.Count == 3
                && after[Lookup.SoundNameToId("S2")] == before[Lookup.SoundNameToId("S2")],
                "UWP encoding changed the source codec or an unrelated compressed track.");
            var expected = AudioDecoder.DecodeAdded(input, settings, gainDb: -3);
            var prepared = log.Single(l => l.Contains("[track=0 ", StringComparison.Ordinal) && l.Contains("wav.prepared ", StringComparison.Ordinal));
            using (var json = JsonDocument.Parse(prepared[(prepared.IndexOf("wav.prepared ", StringComparison.Ordinal) + "wav.prepared ".Length)..]))
                Check(json.RootElement.GetProperty("PcmSha256").GetString() == AudioBuildValidation.Inspect(expected).PcmSha256,
                    "UWP preview and encoder input use different processing.");
            var paddedFrames = (AudioBuildValidation.Inspect(expected).Frames + 255) / 256 * 256;
            Check(after[Lookup.SoundNameToId("S&--")].Frames == paddedFrames
                && AudioBuildValidation.Inspect(AudioDecoder.DecodeBank(output, 0)).Frames == paddedFrames,
                "UWP FADPCM encoding or preview changed block padding.");
            var station = RadioCatalog.Build(game, radio).Single();
            Apply(radio, station, "R1_Tracks", added, items);
            radio.Save(xml);
            var saved = RadioInfo.Load(xml).StationByNumber(1)!.ReadTracks().Single(t => t.SoundName == custom);
            Check(!saved.Enabled && saved.UsesTrackBreakDown && !saved.UsesMilliseconds
                && saved.Markers!.ContainsKey("TrackBreakDown") && !saved.Markers.ContainsKey("PostDrop"),
                "UWP custom XML lost its native marker schema or inactive state.");
            File.Move(output, bank, overwrite: true);
            var repeat = Path.Combine(root, $"uwp-{obfuscated}.repeat.bank");
            var reloaded = BankReader.ReadTracks(bank, RadioInfo.Load(xml), 1);
            await BankBuildService.BuildToFileAsync(bank, repeat, reloaded.Tracks.Select(Item).ToArray(), settings);
            var repeated = BankState(repeat);
            Check(after.Count == repeated.Count && after.All(p => repeated.TryGetValue(p.Key, out var value) && value == p.Value),
                "A repeated UWP build changed existing encoded audio.");
        }
        Console.WriteLine("Verified UWP FADPCM replacement/addition, preview parity, native markers and padding, retained bytes and repeat builds with normal and obfuscated paths.");
    }

    internal static void WriteTemplate(string path)
    {
        using var list = new MemoryStream();
        list.Write("PROJ"u8);
        Chunk(list, "BNKI", []);
        Chunk(list, "STBL", []);
        Chunk(list, "SNDH", []);
        using var body = new MemoryStream();
        Chunk(body, "FMT ", [0x50, 0, 0, 0]);
        Chunk(body, "LIST", list.ToArray());
        using var output = File.Create(path);
        output.Write("RIFF"u8);
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write(checked((uint) body.Length + 4));
        output.Write("FEV "u8);
        body.Position = 0;
        body.CopyTo(output);
    }

    private static void Chunk(Stream output, string tag, byte[] payload)
    {
        output.Write(Encoding.ASCII.GetBytes(tag));
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write((uint) payload.Length);
        output.Write(payload);
        if ((payload.Length & 1) != 0) output.WriteByte(0);
    }
}
