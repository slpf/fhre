using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FH6RB.Core;

namespace FH6RB.Services;

internal sealed class GameFileTransaction : IDisposable
{
    private sealed record Change(string Target, bool Existed);
    private sealed record Journal(Guid Id, bool Settled, Change[] Changes, bool Preparing = false);

    private const string JournalName = ".fhre-transaction.json";
    private static readonly JsonSerializerOptions JournalOptions = new() { RespectRequiredConstructorParameters = true };
    private readonly Guid _id = Guid.NewGuid();
    private readonly string _root;
    private readonly string _journalPath;
    private readonly List<Change> _changes = [];
    private readonly Dictionary<string, byte[]> _radioHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileStream> _locks = [];
    private bool _journalOwnsStaging;

    public GameFileTransaction(string gamePath)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath));
        _journalPath = Path.Combine(_root, JournalName);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_root.ToUpperInvariant())));
        var locks = Path.Combine(AppPaths.TempBase, "game-locks");
        Directory.CreateDirectory(locks);
        _locks.Add(OpenLock(Path.Combine(locks, digest + ".lock")));
        try
        {
            Recover();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public static bool RecoverPending(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || !File.Exists(Path.Combine(gamePath, JournalName))) return false;
        using var transaction = new GameFileTransaction(gamePath);
        return true;
    }

    private static FileStream OpenLock(string path) => new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
        FileShare.None, 1, FileOptions.DeleteOnClose);

    public string Stage(string path)
    {
        var target = TargetPath(path);
        if (_changes.Any(change => string.Equals(change.Target, target, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"File already staged: {target}");

        _locks.Add(OpenLock(target + ".fhre-lock"));
        _changes.Add(new Change(target, File.Exists(target)));
        WriteJournal(new Journal(_id, false, _changes.ToArray(), Preparing: true));
        return StagePath(target, _id);
    }

    public void StageOriginal(string path, bool refresh)
    {
        var backup = path + ".bak";
        if (!File.Exists(backup) || refresh) StageCopy(path, backup);
    }

    public void StageCopy(string source, string target) => Atomic.Copy(source, Stage(target));

    public void EnsureGameClosed()
    {
        if (GameScanner.IsGameProcessRunning(_root))
            throw new IOException("Close the game before writing its radio files.");
    }

    public void Commit()
    {
        if (_changes.Count == 0) return;
        var changes = _changes.OrderByDescending(change => change.Target.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)).ToArray();
        FileGuard.EnsureWritable(changes.Select(change => change.Target));
        foreach (var change in changes)
        {
            var staged = StagePath(change.Target, _id);
            if (!File.Exists(staged)) throw new FileNotFoundException("Staged game file is missing.", staged);
        }

        foreach (var (path, hash) in _radioHashes) VerifyRadio(path, path, hash);
        var journal = new Journal(_id, false, changes);
        WriteJournal(journal);
        EnsureGameClosed();
        _journalOwnsStaging = true;
        try
        {
            foreach (var change in changes)
            {
                var staged = StagePath(change.Target, _id);
                if (change.Existed)
                {
                    var rollback = RollbackPath(change.Target, _id);
                    File.Replace(staged, change.Target, rollback);
                    if (_radioHashes.TryGetValue(change.Target, out var hash)) VerifyRadio(rollback, change.Target, hash);
                }
                else File.Move(staged, change.Target);
            }
            WriteJournal(journal with { Settled = true });
        }
        catch (Exception error)
        {
            try { Recover(journal); }
            catch (Exception recoveryError)
            {
                throw new IOException($"Could not roll back the game files. Recovery journal: {_journalPath}",
                    new AggregateException(error, recoveryError));
            }
            throw;
        }
        Cleanup(journal);
    }

    private void Recover()
    {
        if (!File.Exists(_journalPath)) return;
        Journal? journal;
        using (var stream = File.OpenRead(_journalPath)) journal = JsonSerializer.Deserialize<Journal>(stream, JournalOptions);
        if (journal is null || journal.Id == Guid.Empty || journal.Changes is not { Length: > 0 })
            throw new InvalidDataException($"Invalid game file recovery journal: {_journalPath}");

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in journal.Changes)
        {
            if (change is null || string.IsNullOrEmpty(change.Target)
                || !string.Equals(change.Target, TargetPath(change.Target), StringComparison.OrdinalIgnoreCase)
                || !targets.Add(change.Target))
                throw new InvalidDataException($"Invalid target in game file recovery journal: {_journalPath}");
        }
        if (!journal.Preparing && !journal.Settled && GameScanner.IsGameProcessRunning(_root))
            throw new IOException("Close the game before recovering its radio files.");

        Recover(journal);
        if (File.Exists(_journalPath)) throw new IOException($"Could not remove the recovery journal: {_journalPath}");
    }

    private void Recover(Journal journal)
    {
        if (!journal.Preparing && !journal.Settled)
        {
            foreach (var change in journal.Changes.Reverse())
            {
                var rollback = RollbackPath(change.Target, journal.Id);
                if (File.Exists(rollback)) File.Move(rollback, change.Target, overwrite: true);
                else if (!change.Existed && !File.Exists(StagePath(change.Target, journal.Id))) File.Delete(change.Target);
                else if (change.Existed && !File.Exists(change.Target))
                    throw new IOException($"Original file is missing: {change.Target}");
            }
            WriteJournal(journal with { Settled = true });
            Log.Line($"Recovered interrupted game file transaction: {_root}");
        }
        Cleanup(journal);
    }

    private void WriteJournal(Journal journal) =>
        Atomic.Write(_journalPath, stream => JsonSerializer.Serialize(stream, journal, JournalOptions));

    private void Cleanup(Journal journal)
    {
        var clean = true;
        foreach (var change in journal.Changes)
        {
            var staged = StagePath(change.Target, journal.Id);
            clean &= DeleteTemporary(staged);
            clean &= DeleteTemporary(staged + ".tmp");
            clean &= DeleteTemporary(RollbackPath(change.Target, journal.Id));
        }
        if (clean) DeleteTemporary(_journalPath);
    }

    private string TargetPath(string path)
    {
        var target = Path.GetFullPath(path);
        var prefix = Path.EndsInDirectorySeparator(_root) ? _root : _root + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !GameScanner.IsRadioFile(target))
            throw new IOException($"Invalid game radio file target: {target}");
        return target;
    }

    private static string StagePath(string target, Guid id) => $"{target}.{id:N}.stage";

    private static string RollbackPath(string target, Guid id) => $"{target}.{id:N}.rollback";

    public void ExpectRadio(string path, RadioInfo radio) => _radioHashes.Add(TargetPath(path), radio.SourceHash);

    private static void VerifyRadio(string path, string target, byte[] expected)
    {
        using var stream = File.OpenRead(path);
        if (!SHA256.HashData(stream).AsSpan().SequenceEqual(expected))
            throw new IOException(string.Format(Assets.Str.StatusBuildRadioChangedFmt, Path.GetFileName(target)));
    }

    public void StageRadio(RadioInfo radio, string path)
    {
        _radioHashes.TryAdd(TargetPath(path), radio.SourceHash);
        var staged = Stage(path);
        StageOriginal(path, File.Exists(path + ".bak") && !File.ReadAllText(path).Contains(RadioInfo.XmlMarker + " edited"));
        radio.Save(staged);
    }

    public void Dispose()
    {
        if (!_journalOwnsStaging && _changes.Count > 0)
        {
            Cleanup(new Journal(_id, false, _changes.ToArray(), Preparing: true));
        }
        foreach (var handle in _locks) handle.Dispose();
    }

    private static bool DeleteTemporary(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Line($"game files: could not remove temporary file {path}: {ex}");
            return false;
        }
    }
}
