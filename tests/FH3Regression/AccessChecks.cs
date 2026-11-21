using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FH6RB.Assets;
using FH6RB.Core;
using FH6RB.Services;
using FH6RB.ViewModels;
using static TestSupport;

internal static class AccessChecks
{
    public static void Run(string root)
    {
        CheckGameFileTransaction(root);
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
            var german = Path.Combine(audio, Physical("RadioInfo_DE.xml"));
            var bank = Path.Combine(banks, Physical("R1_Tracks.bank"));
            var dlc = Path.Combine(banks, Physical("R1_Tracks_PDLC1.bank"));
            File.WriteAllText(english, SyntheticChecks.Xml());
            File.WriteAllText(french, SyntheticChecks.Xml());
            File.WriteAllText(german, SyntheticChecks.Xml());
            UwpBuildChecks.WriteTemplate(bank);
            UwpBuildChecks.WriteTemplate(dlc);
            File.WriteAllBytes(Path.Combine(banks, Physical("R1_LFE.bank")), []);
            var archive = Directory.CreateDirectory(Path.Combine(game, "ZZ-archive")).FullName;
            var archivedBank = Path.Combine(archive, "R1_Tracks.assets.bank");
            var archivedXml = Path.Combine(archive, "RadioInfo_EN.xml");
            File.WriteAllBytes(archivedBank, []);
            File.WriteAllBytes(archivedXml, []);
            var resources = new[] { english, french, german, bank, dlc };
            var contents = resources.ToDictionary(p => p, File.ReadAllBytes);
            using (var lockedArchive = new FileStream(archivedBank, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                CheckReadable(GameScanner.Scan(game));
                Check(GameScanner.BankPath(game, "R1_Tracks") == bank
                    && GameScanner.RadioInfoPathByFile(game, "RadioInfo_EN.xml") == english,
                    "Public path resolution chose an archived bank or XML.");
            }
            File.Delete(bank);
            Check(GameScanner.BankPath(game, "R1_Tracks") is null,
                "An archived copy replaced a missing bank in the active audio tree.");
            File.WriteAllBytes(bank, contents[bank]);
            CheckReadable(GameScanner.Scan(game));
            CheckReadable(ScanResources(game));

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var all = ScanResources(game, path =>
            {
                visited.Add(Path.GetFullPath(path));
                ReadOne(path);
            });
            CheckReadable(all);
            Check(visited.SetEquals(resources.Select(Path.GetFullPath)), "The scanner did not probe exactly the selected resources.");

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
                    CheckFailure(GameScanner.Scan(game), GameScanner.GameScanIssue.TransientLock, target);
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
            File.WriteAllBytes(bank, contents[bank][..25]);
            CheckFailure(GameScanner.Scan(game), GameScanner.GameScanIssue.InvalidRadioResource, bank);
            Check(BankReader.ReadTracks(bank, RadioInfo.Load(english), 1).Error is not null,
                "A truncated bank was loaded as an empty station.");
            File.WriteAllBytes(bank, contents[bank]);
            File.WriteAllBytes(german, []);
            CheckFailure(GameScanner.Scan(game), GameScanner.GameScanIssue.InvalidRadioResource, german);
            File.WriteAllText(german, "<Radio><RadioStation");
            CheckFailure(GameScanner.Scan(game), GameScanner.GameScanIssue.InvalidRadioResource, german);
            File.WriteAllBytes(german, contents[german]);
            CheckMainWindowResourceError(game);
            CheckSettingsScanDetails(game);
        }
        CheckSettingsPersistence(root);
        Console.WriteLine("Verified active resource selection, read diagnostics, bank and XML validation, shared reads and lock recovery.");
    }

    private static void CheckSettingsPersistence(string root)
    {
        var path = Path.Combine(root, "settings-fixture.json");
        var load = typeof(SettingsService).GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Static,
            null, [typeof(string)], null)!;
        AppSettings Read() => (AppSettings)load.Invoke(null, [path])!;

        Check(Read().SettingsVersion == SettingsService.CurrentSettingsVersion && !File.Exists(path),
            "Loading a missing profile created a settings file.");
        var json = JsonSerializer.Serialize(new AppSettings { GamePath = root, SettingsVersion = SettingsService.CurrentSettingsVersion });
        File.WriteAllText(path, json);
        AppSettings blocked;
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Delete))
        {
            blocked = Read();
            Check(File.Exists(path) && !File.Exists(path + ".corrupt"),
                "An unreadable profile was moved as corrupt.");
        }
        var save = typeof(SettingsService).GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Static,
            null, [typeof(AppSettings), typeof(string)], null)!;
        try
        {
            save.Invoke(null, [blocked, path]);
            throw new InvalidOperationException("A fallback profile accepted writes after a read failure.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is IOException) { }
        Check(File.ReadAllText(path) == json, "A temporary share violation changed settings on disk.");

        File.WriteAllText(path, """{"SettingsVersion":5,"VorbisQuality":70}""");
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var migrated = Read();
            Check(migrated.SettingsVersion == SettingsService.CurrentSettingsVersion && migrated.VorbisQuality == 90,
                "A blocked migration write prevented loading settings.");
        }

        File.WriteAllText(path, """{"SettingsVersion":7,"WindowSizes":null,"GamePath":null}""");
        var nullable = Read();
        Check(nullable.WindowSizes is { Count: 0 } && nullable.GamePath == "",
            "Null values in settings were not normalized.");

        File.WriteAllText(path, "{");
        Read();
        Check(File.ReadAllText(path + ".corrupt") == "{" && !File.Exists(path),
            "Invalid JSON was not preserved before resetting settings.");
        Check(MarkerDefaults.Resolve("NaN", 48000, 48000) == -1
            && MarkerDefaults.Resolve("1e999", 48000, 48000) == -1,
            "Non-finite marker defaults were interpreted as positions.");
    }

    private static void CheckGameFileTransaction(string root)
    {
        var folder = Path.Combine(root, "game-transaction");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "ForzaHorizon6.exe"), []);
        var xml = Path.Combine(folder, "RadioInfo_EN.xml");
        File.WriteAllText(xml, SyntheticChecks.Xml());
        var noBanks = GameScanner.Scan(folder);
        Check(!noBanks.IsValid && noBanks.Issue == GameScanner.GameScanIssue.RadioBanksMissing,
            "A game without station banks was accepted.");

        var bank = Path.Combine(folder, "R1_Tracks.assets.bank");
        File.WriteAllText(bank, "live bank");
        UwpBuildChecks.WriteTemplate(bank + ".bak");
        File.Copy(xml, xml + ".bak");
        File.WriteAllText(xml, "<Radio><RadioStation");
        var station = new StationInfo(1, "Test", ["Tracks"], new Dictionary<string, string> { ["Tracks"] = "R1_Tracks" });
        try
        {
            BackupService.RestoreStation(folder, station);
            throw new InvalidOperationException("A malformed live XML was accepted.");
        }
        catch (IOException)
        {
            Check(File.ReadAllText(bank) == "live bank" && File.ReadAllText(xml) == "<Radio><RadioStation",
                "Failed station restore changed the live bank or XML.");
        }

        var type = typeof(BackupService).Assembly.GetType("FH6RB.Services.GameFileTransaction", throwOnError: true)!;
        using (var transaction = (IDisposable)Activator.CreateInstance(type, folder)!)
        {
            var stage = type.GetMethod("Stage")!;
            var stagedBank = (string)stage.Invoke(transaction, [bank])!;
            File.WriteAllText(stagedBank, "new bank");
            stage.Invoke(transaction, [xml]);
            try
            {
                type.GetMethod("Commit")!.Invoke(transaction, null);
                throw new InvalidOperationException("The transaction committed a missing staged XML.");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is IOException)
            {
                Check(File.ReadAllText(bank) == "live bank" && File.ReadAllText(xml) == "<Radio><RadioStation",
                    "Commit failure did not restore the previous game files.");
            }
        }
        Check(!Directory.EnumerateFiles(folder).Any(path => path.EndsWith(".stage") || path.EndsWith(".rollback")
            || path.EndsWith(".fhre-lock") || path.EndsWith(".fhre-write-lock")), "The failed transaction left staging files behind.");

        CheckInterruptedTransaction(type, folder, bank, xml);
    }

    private static void CheckInterruptedTransaction(Type type, string folder, string bank, string xml)
    {
        var pending = Path.Combine(folder, ".fhre-transaction.json");
        var backup = Path.Combine(folder, "RadioInfo_FR.xml.bak");
        var originalXml = File.ReadAllText(xml);
        var changes = new[]
        {
            new { Target = backup, Existed = false },
            new { Target = bank, Existed = true },
            new { Target = xml, Existed = true },
        };
        var recover = type.GetMethod("RecoverPending")!;

        var id = Guid.NewGuid();
        var stagedXml = $"{xml}.{id:N}.stage";
        var rollback = $"{bank}.{id:N}.rollback";
        File.WriteAllText(backup, "new backup");
        File.WriteAllText(rollback, "live bank");
        File.WriteAllText(bank, "partially committed bank");
        File.WriteAllText(stagedXml, "staged XML");
        File.WriteAllText(pending, JsonSerializer.Serialize(new { Id = id, Settled = false, Changes = changes }));
        recover.Invoke(null, [folder]);

        Check(File.ReadAllText(bank) == "live bank" && File.ReadAllText(xml) == originalXml && !File.Exists(backup),
            "Recovery did not roll back the interrupted bank and its newly created backup.");
        Check(!File.Exists(pending) && !File.Exists(stagedXml) && !File.Exists(rollback),
            "Recovery left temporary files from the interrupted transaction.");

        File.WriteAllText(rollback, "live bank");
        File.WriteAllText(bank, "committed bank");
        File.WriteAllText(xml, "committed XML");
        File.WriteAllText(backup, "committed backup");
        File.WriteAllText(pending, JsonSerializer.Serialize(new { Id = id, Settled = true, Changes = changes }));
        recover.Invoke(null, [folder]);

        Check(File.ReadAllText(bank) == "committed bank" && File.ReadAllText(xml) == "committed XML" && File.Exists(backup),
            "Recovery rolled back a completed transaction.");
        Check(!File.Exists(pending) && !File.Exists(rollback), "Recovery left temporary files from the completed transaction.");

        File.WriteAllText(stagedXml, "prepared XML");
        File.WriteAllText($"{bank}.{id:N}.stage.tmp", "incomplete bank");
        File.WriteAllText(pending, JsonSerializer.Serialize(new { Id = id, Settled = false, Changes = changes, Preparing = true }));
        recover.Invoke(null, [folder]);

        Check(File.ReadAllText(bank) == "committed bank" && File.ReadAllText(xml) == "committed XML" && File.Exists(backup),
            "Preparation recovery changed live game files.");
        Check(!File.Exists(stagedXml) && !File.Exists($"{bank}.{id:N}.stage.tmp") && !File.Exists(pending),
            "Recovery did not remove the interrupted preparation files.");
    }

    private static void CheckMainWindowResourceError(string game)
    {
        var vm = CreateVm(game, "RadioInfo_EN.xml");
        try
        {
            var station = new StationInfo(1, "Test", ["Tracks"], new Dictionary<string, string> { ["Tracks"] = "R1_Tracks" });
            Set(vm, "_selectedStation", station);
            Set(vm, "_selectedVariant", "Tracks");
            Set(vm, "_loadedSelection", (vm.SelectedLanguage!, station.Number, "Tracks"));
            Set(vm, "_loadedBankPath", "R1_Tracks.bank");
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
        vm.GameLine = "";
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
        apply.Invoke(vm, [new GameScanner.GameScanResult(true, GameScanner.GameScanIssue.PartialAccess, "blocked", exe, 3, 2)]);
        Check(vm.IsValid && !vm.ShowError && vm.ShowWarning && vm.Error.Contains("blocked"),
            "Settings hid a partial access warning.");
        var currentSettings = new AppSettings { GamePath = game };
        typeof(SettingsWindowViewModel).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, currentSettings);
        typeof(SettingsWindowViewModel).GetField("_gamePath", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, Path.Combine(game, "another-game"));
        var commit = typeof(SettingsWindowViewModel).GetMethod("TryCommitAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var prompted = false;
        Func<Task<bool>> decline = () =>
        {
            prompted = true;
            return Task.FromResult(false);
        };
        var committed = (Task<bool>) commit.Invoke(vm, [decline])!;
        Check(!committed.GetAwaiter().GetResult() && prompted && !vm.Saved && currentSettings.GamePath == game,
            "Canceling the game path change committed the settings.");
        currentSettings.LoopMinSeconds = double.NaN;
        var approved = (Task<bool>)commit.Invoke(vm, [(Func<Task<bool>>)(() => Task.FromResult(true))])!;
        try
        {
            approved.GetAwaiter().GetResult();
            throw new InvalidOperationException("A non-serializable profile was saved.");
        }
        catch (ArgumentException) { }
        Check(!vm.Saved && currentSettings.GamePath == game && currentSettings.TargetLufs == -23,
            "A failed settings write changed the live profile.");
        apply.Invoke(vm, [new GameScanner.GameScanResult(true, GameScanner.GameScanIssue.None, null, exe, 3, 2)]);
        Check(vm.IsValid && !vm.ShowError && vm.Error.Length == 0 && vm.HasScanDetails,
            "A successful rescan retained the previous resource error or hid discovered details.");
    }

    public static void CheckBlockedInstallation(string game)
    {
        Check(GameScanner.DetectVersion(game) == 3, "The blocked package lost its UWP identity.");
        var result = GameScanner.Scan(game);
        Check(!result.IsValid && result.Issue == GameScanner.GameScanIssue.EncryptedResources,
            "The blocked package was not classified as unreadable encrypted resources: " + JsonSerializer.Serialize(result));
        Check(result.ExePath is not null && result.LanguageFileCount > 0 && result.BankCount > 0,
            "Blocked-package diagnostics lost executable identity or enumerated file counts.");
        Console.WriteLine("Blocked FH3 installation read-only check passed: " + JsonSerializer.Serialize(result));
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
