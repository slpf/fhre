using System.Security.Cryptography;
using System.Text;
using FH6RB.Core;

namespace FH6RB.Services;

internal sealed class GameFileTransaction : IDisposable
{
    private sealed record Change(string Target, string Staged, string Rollback, bool Existed);

    private readonly string _id = Guid.NewGuid().ToString("N");
    private readonly List<Change> _changes = [];
    private readonly List<FileStream> _locks = [];

    public GameFileTransaction(string gamePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())));
        var locks = Path.Combine(AppPaths.TempBase, "game-locks");
        Directory.CreateDirectory(locks);
        _locks.Add(OpenLock(Path.Combine(locks, digest + ".lock")));
    }

    private static FileStream OpenLock(string path) => new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
        FileShare.None, 1, FileOptions.DeleteOnClose);

    public string Stage(string path)
    {
        var target = Path.GetFullPath(path);
        if (_changes.Any(change => string.Equals(change.Target, target, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"File already staged: {target}");

        _locks.Add(OpenLock(target + ".fhre-lock"));
        var change = new Change(target, $"{target}.{_id}.stage", $"{target}.{_id}.rollback", File.Exists(target));
        _changes.Add(change);
        return change.Staged;
    }

    public void StageOriginal(string path, bool refresh)
    {
        var backup = path + ".bak";
        if (!File.Exists(backup) || refresh) StageCopy(path, backup);
    }

    public void StageCopy(string source, string target) => Atomic.Copy(source, Stage(target));

    public void Commit()
    {
        FileGuard.EnsureWritable(_changes.Select(change => change.Target));
        var applied = new List<Change>();
        try
        {
            foreach (var change in _changes.OrderByDescending(change => change.Target.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)))
            {
                applied.Add(change);
                if (change.Existed) File.Replace(change.Staged, change.Target, change.Rollback);
                else File.Move(change.Staged, change.Target);
            }
        }
        catch (Exception error)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var change in Enumerable.Reverse(applied))
            {
                try
                {
                    if (File.Exists(change.Rollback)) File.Move(change.Rollback, change.Target, overwrite: true);
                    else if (!change.Existed && !File.Exists(change.Staged)) File.Delete(change.Target);
                    else if (change.Existed && !File.Exists(change.Target))
                        throw new IOException($"Original file is missing: {change.Target}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    rollbackErrors.Add(new IOException($"Could not restore {change.Target}. Recovery file: {change.Rollback}", ex));
                }
            }

            if (rollbackErrors.Count > 0)
            {
                rollbackErrors.Insert(0, error);
                throw new IOException("Could not roll back all game files. See the log for recovery paths.",
                    new AggregateException(rollbackErrors));
            }
            throw;
        }

        foreach (var change in applied) DeleteTemporary(change.Rollback);
    }

    public void StageRadio(RadioInfo radio, string path)
    {
        var staged = Stage(path);
        StageOriginal(path, File.Exists(path + ".bak") && !File.ReadAllText(path).Contains(RadioInfo.XmlMarker + " edited"));
        radio.Save(staged);
    }

    public void Dispose()
    {
        foreach (var change in _changes) DeleteTemporary(change.Staged);
        foreach (var handle in _locks) handle.Dispose();
    }

    private static void DeleteTemporary(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Line($"game files: could not remove temporary file {path}: {ex}");
        }
    }
}
