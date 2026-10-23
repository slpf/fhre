using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FH6RB.Assets;
using FH6RB.Services;
using FH6RB.ViewModels;
using static TestSupport;

internal static class AccessChecks
{
    public static void Run(string root)
    {
        foreach (var obfuscated in new[] { false, true })
        {
            var game = Path.Combine(root, obfuscated ? "access-obfuscated" : "access-normal");
            Directory.CreateDirectory(game);
            File.WriteAllBytes(Path.Combine(game, "forza_x64_release_final.exe"), []);
            File.WriteAllText(Path.Combine(game, "AppxManifest.xml"), SyntheticChecks.Manifest);
            var audio = Path.Combine(game, obfuscated ? "gq6$l" : "media", obfuscated ? "l=6$h" : "audio");
            var banks = Path.Combine(audio, obfuscated ? "vgh6hu=~" : "fmodopus", obfuscated ? "]j(" : "x64");
            Directory.CreateDirectory(banks);
            string Physical(string name) => obfuscated ? Fh3FileNameCodec.Encode(name) : name;
            var english = Path.Combine(audio, Physical("RadioInfo_EN.xml"));
            var french = Path.Combine(audio, Physical("RadioInfo_FR.xml"));
            var emptyXml = Path.Combine(audio, Physical("RadioInfo_DE.xml"));
            var bank = Path.Combine(banks, Physical("R1_Tracks.bank"));
            var dlc = Path.Combine(banks, Physical("R1_Tracks_PDLC1.bank"));
            File.WriteAllText(english, SyntheticChecks.Xml());
            File.WriteAllText(french, SyntheticChecks.Xml());
            File.WriteAllBytes(emptyXml, []);
            File.WriteAllBytes(bank, []);
            File.WriteAllBytes(dlc, []);
            File.WriteAllBytes(Path.Combine(banks, Physical("R1_LFE.bank")), []);
            var resources = new[] { english, french, emptyXml, bank, dlc };
            var contents = resources.ToDictionary(p => p, File.ReadAllBytes);
            CheckHorizon3Disabled(game);
            CheckReadable(ScanResources(game));

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var all = ScanResources(game, path =>
            {
                visited.Add(Path.GetFullPath(path));
                ReadOne(path);
            });
            CheckReadable(all);
            Check(visited.SetEquals(resources.Select(Path.GetFullPath)), "The scanner did not probe every recognized physical XML/bank or probed unrelated resources.");

            foreach (var target in new[] { french, dlc })
            {
                var denied = ScanResources(game, path =>
                {
                    if (SamePath(path, target)) throw new UnauthorizedAccessException("Synthetic resource read denial.");
                    ReadOne(path);
                });
                CheckFailure(denied, GameScanner.GameScanIssue.ResourceReadDenied, target);

                var readError = ScanResources(game, path =>
                {
                    if (SamePath(path, target)) throw new IOException("Synthetic CRC read failure.", unchecked((int) 0x80070017));
                    ReadOne(path);
                });
                CheckFailure(readError, GameScanner.GameScanIssue.ResourceReadError, target);

                foreach (var hresult in new[] { unchecked((int) 0x80070020), unchecked((int) 0x80070021) })
                {
                    var sharing = ScanResources(game, path =>
                    {
                        if (SamePath(path, target)) throw new IOException("Synthetic sharing violation.", hresult);
                        ReadOne(path);
                    });
                    CheckFailure(sharing, GameScanner.GameScanIssue.TransientLock, target);
                }
            }

            foreach (var target in new[] { english, bank })
            {
                using (var exclusive = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Check(Directory.GetFiles(Path.GetDirectoryName(target)!).Contains(target), "A file sharing lock unexpectedly prevented directory enumeration.");
                    CheckHorizon3Disabled(game);
                    CheckFailure(ScanResources(game), GameScanner.GameScanIssue.TransientLock, target);
                }
                CheckReadable(ScanResources(game));
            }

            using (var xmlReader = new FileStream(english, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var bankReader = new FileStream(bank, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                ReadOne(english);
                ReadOne(bank);
                CheckReadable(ScanResources(game));
            }
            Check(resources.All(p => File.ReadAllBytes(p).AsSpan().SequenceEqual(contents[p])), "A resource-read capability scan changed fixture content.");
            CheckMainWindowResourceError(game);
            CheckSettingsScanDetails(game);
        }
        Console.WriteLine("Verified resource-read denial, distinct sharing/I/O errors, complete physical probes, zero-byte resources, shared reads and lock recovery without ACL changes.");
    }

    private static void CheckMainWindowResourceError(string game)
    {
        var vm = CreateVm(game, "RadioInfo_EN.xml");
        try
        {
            vm.PlaylistLow = false;
            var setError = Method("SetResourceError");
            var changes = new HashSet<string?>();
            vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
            const string error = "Blocked fixture radio resource.";
            setError.Invoke(vm, [error]);
            Check(vm.HasResourceError && !vm.CanBuild && !vm.CanAddTracks && vm.BuildTip == error && vm.AddTracksTip == error,
                "A resource error did not disable main-window actions and replace their tooltips.");
            Check(new[] { nameof(vm.CanBuild), nameof(vm.CanAddTracks), nameof(vm.BuildTip), nameof(vm.AddTracksTip) }.All(changes.Contains),
                "Resource-error changes did not notify every affected main-window action and tooltip.");
            var settings = vm.Settings;
            Set(vm, "<Settings>k__BackingField", null!);
            try
            {
                vm.BuildAsync().GetAwaiter().GetResult();
                Check(vm.Status == error && !vm.IsBuilding, "A direct build did not stop at the resource error before reading settings or files.");
            }
            finally { Set(vm, "<Settings>k__BackingField", settings); }
            changes.Clear();
            setError.Invoke(vm, [null]);
            Check(!vm.HasResourceError && vm.CanBuild == Tools.CanBuild && vm.CanAddTracks == Tools.CanAddTracks,
                "Clearing a resource error did not restore tool-based main-window availability.");
            Check(vm.BuildTip != error && vm.AddTracksTip != error
                && new[] { nameof(vm.CanBuild), nameof(vm.CanAddTracks), nameof(vm.BuildTip), nameof(vm.AddTracksTip) }.All(changes.Contains),
                "Clearing a resource error left stale tooltips or missing notifications.");
        }
        finally { ReleaseVm(vm); }
    }

    private static void CheckSettingsScanDetails(string game)
    {
        var vm = (SettingsWindowViewModel) RuntimeHelpers.GetUninitializedObject(typeof(SettingsWindowViewModel));
        vm.ExeLine = "";
        vm.LangLine = "";
        vm.BankLine = "";
        vm.Error = "";
        var apply = typeof(SettingsWindowViewModel).GetMethod("ApplyGameScan", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException(nameof(SettingsWindowViewModel), "ApplyGameScan");
        var exe = Path.Combine(game, "forza_x64_release_final.exe");
        var changed = new HashSet<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        foreach (var (issue, format) in new[]
        {
            (GameScanner.GameScanIssue.ResourceReadDenied, Str.ErrScanResourceReadDeniedFmt),
            (GameScanner.GameScanIssue.EncryptedResources, Str.ErrScanEncryptedResourcesFmt),
            (GameScanner.GameScanIssue.ResourceReadError, Str.ErrScanResourceReadErrorFmt),
        })
        {
            const string detail = "required-radio-file";
            apply.Invoke(vm, [new GameScanner.GameScanResult(false, issue, detail, exe, 3, 2)]);
            Check(!vm.IsValid && vm.ShowError && vm.HasScanDetails && vm.Error == string.Format(format, detail),
                "Settings scan presentation lost a specific resource-read failure.");
            Check(vm.ExeLine == string.Format(Str.FoundFmt, Path.GetFileName(exe))
                && vm.LangLine == string.Format(Str.LocalizationsFmt, 3)
                && vm.BankLine == string.Format(Str.RadioBanksFmt, 2),
                "Invalid settings scan discarded discovered executable or language/bank counts.");
        }
        Check(changed.Contains(nameof(vm.HasScanDetails)), "Settings scan details did not notify the UI.");
        apply.Invoke(vm, [new GameScanner.GameScanResult(false, GameScanner.GameScanIssue.Horizon3Disabled, null, exe, 0, 0)]);
        Check(!vm.IsValid && vm.ShowError && vm.HasScanDetails && vm.Error == Str.ErrScanHorizon3Disabled
            && vm.ExeLine == string.Format(Str.FoundFmt, Path.GetFileName(exe))
            && vm.LangLine.Length == 0 && vm.BankLine.Length == 0,
            "Disabled FH3 selection lost executable details or displayed unprobed resource counts.");
        apply.Invoke(vm, [new GameScanner.GameScanResult(true, GameScanner.GameScanIssue.None, null, exe, 3, 2)]);
        Check(vm.IsValid && !vm.ShowError && vm.Error.Length == 0 && vm.HasScanDetails,
            "A successful rescan retained the previous resource error or hid discovered details.");
    }

    public static void CheckBlockedInstallation(string game)
    {
        CheckHorizon3Disabled(game);
        var result = ScanResources(game);
        Check(!result.IsValid && result.Issue == GameScanner.GameScanIssue.EncryptedResources,
            "The blocked package was not classified as unreadable encrypted resources: " + JsonSerializer.Serialize(result));
        Check(result.ExePath is not null && result.LanguageFileCount > 0 && result.BankCount > 0,
            "Blocked-package diagnostics lost executable identity or enumerated file counts.");
        Console.WriteLine("Blocked FH3 installation read-only check passed: public selection disabled; internal resource scan: " + JsonSerializer.Serialize(result));
    }

    private static void ReadOne(string path)
    {
        using var input = File.OpenRead(path);
        input.ReadByte();
    }

    private static bool SamePath(string first, string second) => Path.GetFullPath(first).Equals(Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

    private static void CheckReadable(GameScanner.GameScanResult result)
    {
        Check(result.IsValid && result.Issue == GameScanner.GameScanIssue.None, "Readable resources failed capability scanning: " + JsonSerializer.Serialize(result));
        CheckCounts(result);
    }

    private static void CheckFailure(GameScanner.GameScanResult result, GameScanner.GameScanIssue expected, string target)
    {
        Check(!result.IsValid && result.Issue == expected, "Resource access was misclassified: " + JsonSerializer.Serialize(result));
        CheckCounts(result);
        Check(result.Detail?.Contains(Path.GetFileName(target), StringComparison.OrdinalIgnoreCase) == true,
            "Resource failure diagnostics omitted the inaccessible physical filename.");
    }

    private static void CheckCounts(GameScanner.GameScanResult result) => Check(result.ExePath is not null
        && result.LanguageFileCount == 3 && result.BankCount == 2, "Resource access diagnostics lost discovered identity or counts.");
}
