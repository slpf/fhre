using System.Diagnostics;
using System.IO.Enumeration;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using FH6RB.Core;

namespace FH6RB.Services;

public static partial class GameScanner
{
    public enum GameScanIssue
    {
        None,
        EmptyPath,
        DirectoryMissing,
        ExeMissing,
        LanguageFilesMissing,
        AccessDenied,
        TransientLock,
        PartialAccess,
        OtherError,
        ResourceReadDenied,
        EncryptedResources,
        ResourceReadError,
        RadioBanksMissing,
        InvalidRadioResource,
    }

    public sealed record GameScanResult(
        bool IsValid,
        GameScanIssue Issue,
        string? Detail,
        string? ExePath,
        int LanguageFileCount,
        int BankCount)
    {
        public string GameName { get; init; } = "";
        internal int Version { get; init; }
        internal IReadOnlyList<string> RadioBanks { get; init; } = [];
        internal IReadOnlyList<string> RadioLanguages { get; init; } = [];
        internal RadioResources? Resources { get; init; }
    }

    private sealed class ScanStats
    {
        public int AccessDeniedDirs;
        public int TransientLockDirs;
        public int OtherErrorDirs;
        public readonly List<string> Samples = new();
    }

    internal sealed class RadioResources
    {
        private readonly Dictionary<string, string> _paths;

        public RadioResources(string gamePath, IEnumerable<string> paths)
        {
            var hasAudioTree = Directory.Exists(Path.Combine(gamePath, AudioRoot))
                || Directory.Exists(Path.Combine(gamePath, EncodedAudioRoot));
            _paths = paths
                .Select(path => (Path: path, Logical: LogicalFileName(path), Relative: Path.GetRelativePath(gamePath, path)))
                .Where(file => IsRadioResource(file.Logical))
                .Select(file => (file.Path, file.Logical, file.Relative, Rank: ResourceDirectoryRank(file.Relative)))
                .Where(file => !hasAudioTree || file.Rank < 2)
                .OrderBy(file => file.Rank)
                .ThenByDescending(file => file.Logical.EndsWith(".assets.bank", StringComparison.OrdinalIgnoreCase))
                .ThenBy(file => !IsRadioResource(Path.GetFileName(file.Path)))
                .ThenBy(file => file.Relative.Count(c => c == Path.DirectorySeparatorChar))
                .ThenBy(file => file.Relative, StringComparer.OrdinalIgnoreCase)
                .GroupBy(file => BankNameFromFile(file.Logical) ?? file.Logical, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Path, StringComparer.OrdinalIgnoreCase);
            Languages = _paths.Keys.Where(IsLanguageFile).Order(StringComparer.Ordinal).ToArray();
            Banks = _paths.Keys.Where(name => !IsLanguageFile(name)).Order(StringComparer.Ordinal).ToArray();
        }

        public string? LanguagePath(string name) => IsLanguageFile(name) ? _paths.GetValueOrDefault(name) : null;

        public string? BankPath(string name) => _paths.GetValueOrDefault(name);

        public IReadOnlyList<string> Languages { get; }

        public IReadOnlyList<string> Banks { get; }

        public IEnumerable<string> SelectedPaths => _paths.Values.Order(StringComparer.Ordinal);
    }

    private static int ResourceDirectoryRank(string relativePath)
    {
        if (relativePath.StartsWith(AudioRoot, StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith(EncodedAudioRoot, StringComparison.OrdinalIgnoreCase))
            return 0;
        return Path.GetDirectoryName(relativePath) is "" ? 1 : 2;
    }

    private static readonly string AudioRoot = Path.Combine("media", "audio") + Path.DirectorySeparatorChar;
    private static readonly string EncodedAudioRoot = Path.Combine(Fh3FileNameCodec.Encode("media"),
        Fh3FileNameCodec.Encode("audio")) + Path.DirectorySeparatorChar;

    internal static RadioResources ReadRadioResources(string gamePath) =>
        CollectResources(gamePath, [.. LanguagePatterns, .. BankPatterns]);

    private static RadioResources CollectResources(string gamePath, IReadOnlyList<string> patterns,
        ScanStats? stats = null, CancellationToken token = default) =>
        new(gamePath, SafeEnumerate(gamePath, patterns, stats: stats, token: token));

    private const int MaxScanDetailSamples = 3;

    public static bool IsValid(string gamePath) => Scan(gamePath).IsValid;

    public static GameScanResult Scan(string gamePath) => Scan(gamePath, CancellationToken.None);

    internal static GameScanResult Scan(string gamePath, CancellationToken token) =>
        Scan(gamePath, ValidateResource, token);

    internal static GameScanResult Scan(string gamePath, Action<string> readResource)
    {
        ArgumentNullException.ThrowIfNull(readResource);
        return Scan(gamePath, (path, _) => readResource(path), CancellationToken.None);
    }

    private static GameScanResult Scan(string gamePath, Action<string, IReadOnlySet<int>> validateResource,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            return new GameScanResult(false, GameScanIssue.EmptyPath, null, null, 0, 0);
        }

        if (!Directory.Exists(gamePath))
        {
            return new GameScanResult(false, GameScanIssue.DirectoryMissing, gamePath, null, 0, 0);
        }

        var (exe, version) = FindGame(gamePath, token);
        if (exe is null)
        {
            return new GameScanResult(false, GameScanIssue.ExeMissing, gamePath, null, 0, 0);
        }

        var stats = new ScanStats();
        var resources = CollectResources(gamePath, [.. LanguagePatterns, .. BankPatterns], stats, token);
        var langs = resources.Languages;
        var banks = resources.Banks;
        var bankStations = new HashSet<int>();
        foreach (var bank in banks)
            if (RadioCatalog.TryParseBank(bank, out var number, out _)) bankStations.Add(number);
        var gameName = version switch
        {
            2 => "Forza Horizon 2 (Azimuth)",
            3 => IsDurangoExecutable(exe) ? "Forza Horizon 3 (Azimuth)" : "Forza Horizon 3 (UWP)",
            _ => $"Forza Horizon {version}"
        };

        GameScanResult Result(bool valid, GameScanIssue issue, string? detail) =>
            new(valid, issue, detail, exe, langs.Count, banks.Count) { GameName = gameName, Version = version };

        if (langs.Count == 0 || banks.Count == 0)
        {
            var issue = stats.OtherErrorDirs > 0 ? GameScanIssue.OtherError
                      : stats.AccessDeniedDirs > 0 ? GameScanIssue.AccessDenied
                      : stats.TransientLockDirs > 0 ? GameScanIssue.TransientLock
                      : langs.Count == 0 ? GameScanIssue.LanguageFilesMissing : GameScanIssue.RadioBanksMissing;
            return Result(false, issue, BuildDetail(stats));
        }

        foreach (var path in resources.SelectedPaths)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                validateResource(path, bankStations);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or XmlException or InvalidDataException)
            {
                var attributes = ResourceAttributes(path);
                var issue = ex is XmlException or InvalidDataException or EndOfStreamException
                    ? GameScanIssue.InvalidRadioResource : ex is UnauthorizedAccessException
                    ? attributes?.HasFlag(FileAttributes.Encrypted) == true
                        ? GameScanIssue.EncryptedResources : GameScanIssue.ResourceReadDenied
                    : (ex.HResult & 0xffff) is 32 or 33
                        ? GameScanIssue.TransientLock : GameScanIssue.ResourceReadError;
                var logical = LogicalFileName(path);
                Log.Line($"GameScanner: resource.read.failed logical={JsonSerializer.Serialize(logical)} " +
                    $"path={JsonSerializer.Serialize(path)} attributes={attributes} exception={ex.GetType().Name} " +
                    $"hresult=0x{ex.HResult:X8} message={JsonSerializer.Serialize(ex.Message)}");
                var detail = issue == GameScanIssue.InvalidRadioResource ? $"{path}: {ex.Message}" : $"{logical}: {path}";
                return Result(false, issue, detail);
            }
        }

        var partial = stats.AccessDeniedDirs > 0 || stats.TransientLockDirs > 0 || stats.OtherErrorDirs > 0;
        token.ThrowIfCancellationRequested();
        return Result(true, partial ? GameScanIssue.PartialAccess : GameScanIssue.None, BuildDetail(stats)) with
        {
            RadioBanks = banks,
            RadioLanguages = langs,
            Resources = resources
        };
    }

    private static void ValidateResource(string path, IReadOnlySet<int> bankStations)
    {
        if (!IsLanguageFile(LogicalFileName(path)))
        {
            FevBank.ValidateTrackBank(path);
            return;
        }

        var radio = RadioInfo.Load(path);
        if (!radio.Document.Descendants("RadioStation").Any(st =>
                int.TryParse((string?) st.Attribute("Number"), out var number) && bankStations.Contains(number)))
            throw new InvalidDataException("RadioInfo has no station with an installed track bank.");
    }

    private static FileAttributes? ResourceAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? BuildDetail(ScanStats stats)
    {
        if (stats.AccessDeniedDirs == 0 && stats.TransientLockDirs == 0 && stats.OtherErrorDirs == 0)
        {
            return null;
        }

        var parts = new List<string>(4);
        if (stats.AccessDeniedDirs > 0) parts.Add($"{stats.AccessDeniedDirs} access-denied");
        if (stats.TransientLockDirs > 0) parts.Add($"{stats.TransientLockDirs} transient");
        if (stats.OtherErrorDirs > 0) parts.Add($"{stats.OtherErrorDirs} other");
        var summary = string.Join(", ", parts);
        var samples = stats.Samples.Count > 0 ? " [" + string.Join("; ", stats.Samples) + "]" : "";
        return $"{summary}{samples}";
    }

    public static string? FindExe(string gamePath) => FindGame(gamePath).Path;

    public static int DetectVersion(string gamePath) => FindGame(gamePath).Version;

    private static (string? Path, int Version) FindGame(string gamePath, CancellationToken token = default)
    {
        if (!Directory.Exists(gamePath))
        {
            return default;
        }

        try
        {
            var desktop = Directory.GetFiles(gamePath, "ForzaHorizon?.exe", FileEnumeration);
            for (var version = 6; version >= 4; version--)
            {
                var name = $"ForzaHorizon{version}.exe";
                var path = desktop.FirstOrDefault(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase));
                if (path is not null) return (path, version);
            }

            foreach (var path in Directory.EnumerateFiles(gamePath, "forza_durango_*.exe", FileEnumeration))
            {
                token.ThrowIfCancellationRequested();
                var version = DetectDurangoVersion(path, token);
                if (version != 0) return (path, version);
            }

            var uwp = FindHorizon3Executable(gamePath);
            return uwp is null ? default : (uwp, 3);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    private static bool IsDurangoExecutable(string exe) => Path.GetFileNameWithoutExtension(exe)
        .StartsWith("forza_durango_", StringComparison.OrdinalIgnoreCase);

    private static int DetectDurangoVersion(string exe, CancellationToken token)
    {
        var root = Path.GetDirectoryName(exe)!;
        var manifest = Path.Combine(root, "AppxManifest.xml");
        if (!File.Exists(manifest))
        {
            return SafeEnumerate(root, "HZ2_MX_Radio_*_Tracks.bank", token: token)
                .Any(path => BankNameFromFile(Path.GetFileName(path)) is not null) ? 2 : 0;
        }
        try
        {
            var document = ReadManifest(manifest);
            if (!DeclaresExecutable(document, Path.GetFileName(exe))) return 0;
            return PackageIdentity(document) switch
            {
                "Anthem" => 2,
                "Opus" => 3,
                _ => 0
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            Log.Line($"GameScanner: could not identify Xbox package {JsonSerializer.Serialize(manifest)}: {ex.Message}");
            return 0;
        }
    }

    private static XDocument ReadManifest(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });
        return XDocument.Load(reader);
    }

    private static string? PackageIdentity(XDocument document) => (string?) document.Root?.Elements()
        .FirstOrDefault(e => e.Name.LocalName == "Identity")?.Attribute("Name");

    private static bool DeclaresExecutable(XDocument document, string name) => document.Descendants()
        .Any(e => e.Name.LocalName == "Application" && string.Equals((string?) e.Attribute("Executable"), name,
            StringComparison.OrdinalIgnoreCase));

    private static string? FindHorizon3Executable(string gamePath)
    {
        try
        {
            var manifest = Directory.EnumerateFiles(gamePath, "AppxManifest.xml", FileEnumeration).FirstOrDefault();
            if (manifest is null) return null;
            var document = ReadManifest(manifest);
            if (PackageIdentity(document) != "Microsoft.OpusPG")
            {
                return null;
            }
            return !DeclaresExecutable(document, "forza_x64_release_final.exe") ? null
                : Directory.EnumerateFiles(gamePath, "forza_x64_release_final.exe", FileEnumeration).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }
    }

    public static bool IsGameProcessRunning(string gamePath)
    {
        var exe = FindExe(gamePath);
        if (exe is null)
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(exe);
        Process[] procs;
        try
        {
            procs = Process.GetProcessesByName(name);
        }
        catch
        {
            return false;
        }

        try
        {
            return procs.Length > 0;
        }
        finally
        {
            foreach (var p in procs)
            {
                p.Dispose();
            }
        }
    }

    public static List<string> LanguageFiles(string gamePath) => CollectResources(gamePath, LanguagePatterns).Languages.ToList();

    private static bool IsLanguageFile(string name)
    {
        if (!name.StartsWith("RadioInfo_", StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var code = name[10..^4];
        return code.Length is > 0 and <= 8 && code.All(c => char.IsAsciiLetter(c) || c == '-');
    }

    public static string? RadioInfoPathByFile(string gamePath, string fileName)
    {
        var logical = LogicalFileName(fileName);
        if (!IsLanguageFile(logical)) return null;
        return CollectResources(gamePath, [logical, Fh3FileNameCodec.Encode(logical)]).LanguagePath(logical);
    }

    public static List<string> RadioBankNames(string gamePath) => CollectResources(gamePath, BankPatterns).Banks.ToList();

    public static string? BankPath(string gamePath, string bankName)
    {
        var canonical = BankNameFromFile(bankName + ".bank");
        if (!string.Equals(canonical, bankName, StringComparison.OrdinalIgnoreCase)) canonical = null;
        string? path = null;
        if (canonical is not null)
        {
            var assets = canonical + ".assets.bank";
            var bank = canonical + ".bank";
            path = CollectResources(gamePath, [assets, bank, Fh3FileNameCodec.Encode(assets), Fh3FileNameCodec.Encode(bank)])
                .BankPath(canonical);
        }
        if (path is null || Log.IsVerbose)
        {
            Log.Line($"GameScanner: BankPath({bankName}) -> {path ?? "NOT FOUND"}");
        }
        return path;
    }

    public static List<string> BackupFiles(string gamePath) => SafeEnumerate(gamePath, "*.bak")
        .Where(IsRadioFile)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    internal static bool IsRadioFile(string path) => IsRadioResource(LogicalFileName(
        path.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path));

    public static string LogicalFileName(string pathOrName)
    {
        ArgumentNullException.ThrowIfNull(pathOrName);
        var name = Path.GetFileName(pathOrName);
        var logical = name;
        if (name.EndsWith(".]gb", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".`l8s", StringComparison.OrdinalIgnoreCase))
        {
            var decoded = Fh3FileNameCodec.Decode(name.ToLowerInvariant());
            if (IsRadioResource(decoded)) logical = decoded;
        }
        if (IsLanguageFile(logical)) return $"RadioInfo_{logical[10..^4].ToUpperInvariant()}.xml";
        var bank = BankNameFromFile(logical);
        if (bank is null) return name;
        var suffix = logical.EndsWith(".assets.bank", StringComparison.OrdinalIgnoreCase) ? ".assets.bank" : ".bank";
        return bank + suffix;
    }

    private static bool IsRadioResource(string name) => IsLanguageFile(name) || BankNameFromFile(name) is not null;

    private static string? BankNameFromFile(string fileName)
    {
        var suffixLength = fileName.EndsWith(".assets.bank", StringComparison.OrdinalIgnoreCase) ? 12
            : fileName.EndsWith(".bank", StringComparison.OrdinalIgnoreCase) ? 5 : 0;
        if (suffixLength == 0) return null;
        var bank = fileName[..^suffixLength];
        var match = TrackBankRegex().Match(bank);
        if (match.Success)
        {
            if (!int.TryParse(match.Groups[1].Value, out var number) || number <= 0) return null;
            var suffix = match.Groups[2].Value;
            if (suffix.Length == 0) return $"R{match.Groups[1].Value}_Tracks";
            if (suffix.Equals("Disk", StringComparison.OrdinalIgnoreCase)) suffix = "Disk";
            else if (Regex.IsMatch(suffix, @"^(?:PDLC|CU)\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                suffix = suffix.ToUpperInvariant();
            return $"R{match.Groups[1].Value}_Tracks_{suffix}";
        }
        match = Horizon2BankRegex().Match(bank);
        return match.Success && int.TryParse(match.Groups[1].Value, out var station) && station > 0
            ? $"HZ2_MX_Radio_{match.Groups[1].Value}_Tracks" : null;
    }

    private static readonly EnumerationOptions FileEnumeration = new()
    {
        MatchCasing = MatchCasing.CaseInsensitive,
        IgnoreInaccessible = false,
        AttributesToSkip = 0
    };

    private static readonly string[] LanguagePatterns =
    [
        "RadioInfo_*.xml",
        Fh3FileNameCodec.Encode("RadioInfo_*.xml")
    ];

    private static readonly string[] BankPatterns =
    [
        "R*_Tracks*.bank",
        Fh3FileNameCodec.Encode("R*_Tracks*.bank"),
        "HZ2_MX_Radio_*_Tracks.bank"
    ];

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "System Volume Information", "$RECYCLE.BIN", "$SysReset", "$WinREAgent",
        "$Windows.~BT", "$Windows.~WS", "Config.Msi", "Recovery", "WinSxS",
    };

    private static IEnumerable<string> SafeEnumerate(string root, string pattern,
        int maxDepth = 8, ScanStats? stats = null, CancellationToken token = default) =>
        SafeEnumerate(root, new[] { pattern }, maxDepth, stats, token);

    private static IEnumerable<string> SafeEnumerate(string root, IReadOnlyList<string> patterns,
        int maxDepth = 8, ScanStats? stats = null, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            yield break;
        }

        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));

        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();

            string[] files;
            try
            {
                files = Directory.GetFiles(dir, patterns.Count == 1 ? patterns[0] : "*", FileEnumeration);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RecordIssue(stats, dir, ex);
                continue;
            }

            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                if (patterns.Count == 1 || patterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true)))
                    yield return file;
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            string[] subdirs;
            try
            {
                subdirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RecordIssue(stats, dir, ex);
                subdirs = [];
            }

            foreach (var sub in subdirs)
            {
                if (!SkipDirs.Contains(Path.GetFileName(sub)))
                {
                    stack.Push((sub, depth + 1));
                }
            }
        }
    }

    private static void RecordIssue(ScanStats? stats, string dir, Exception ex)
    {
        if (stats is null) return;
        switch (ex)
        {
            case UnauthorizedAccessException:
                stats.AccessDeniedDirs++;
                break;
            case IOException when (ex.HResult & 0xffff) is 32 or 33:
                stats.TransientLockDirs++;
                break;
            default:
                stats.OtherErrorDirs++;
                break;
        }
        if (stats.Samples.Count < MaxScanDetailSamples)
        {
            stats.Samples.Add($"{ex.GetType().Name}@{dir}");
        }
    }

    [GeneratedRegex(@"^R(\d+)_Tracks(?:_([A-Za-z0-9_-]+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrackBankRegex();

    [GeneratedRegex(@"^HZ2_MX_Radio_(\d+)_Tracks$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Horizon2BankRegex();
}
