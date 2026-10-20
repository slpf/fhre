using System.Text.RegularExpressions;
using FH6RB.Services;

var root = Path.Combine(AppContext.BaseDirectory, "scratch", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    TestFirstSession(root);
    TestRetention(root);
    TestConcurrentMessages(root);
    TestRotationFailure(root);
    TestInitializationRecovery(root);
    Console.WriteLine("Log regression checks passed.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}
finally
{
    var scratch = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "scratch")) + Path.DirectorySeparatorChar;
    var target = Path.GetFullPath(root);
    if (!target.StartsWith(scratch, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("The test directory lies outside its scratch root.");
    Directory.Delete(target, recursive: true);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static string TestPath(string root, string name)
{
    return Path.Combine(root, name, "fhre.log");
}

static void TestFirstSession(string root)
{
    var path = TestPath(root, "first");
    var writer = new SessionLog(path);
    writer.Line("first message");
    writer.Line("second message\r\nthird message\rfourth message\n");
    var lines = File.ReadAllLines(path);
    Check(lines.Length == 5 && lines[0].StartsWith("FHRE log - ", StringComparison.Ordinal), "A fresh session has an invalid header or message count.");
    Check(lines[0].Contains("version=", StringComparison.Ordinal) && lines[0].Contains("runtime=", StringComparison.Ordinal) && lines[0].Contains("pid=", StringComparison.Ordinal), "Session diagnostics were omitted from the header.");
    Check(lines.Skip(1).All(l => Regex.IsMatch(l, @"^\[\d{2}:\d{2}:\d{2}\.\d{3}\] ")), "A multiline message has an untimestamped line.");
    Check(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.log").Length == 1, "A single session rotated its own log.");
    Check(lines.Skip(1).Select(l => l[15..]).SequenceEqual(new[] { "first message", "second message", "third message", "fourth message" }), "Multiline message content or order changed.");
}

static void TestRetention(string root)
{
    var path = TestPath(root, "retention");
    for (var session = 1; session <= 9; session++)
    {
        var writer = new SessionLog(path);
        writer.Line($"session={session:00}; first");
        writer.Line($"session={session:00}; second");
        var files = Directory.GetFiles(Path.GetDirectoryName(path)!, "*.log");
        Check(files.Length == Math.Min(session, 5), "The number of retained logs differs from the five-log limit.");
        for (var history = 0; history < Math.Min(session, 5); history++)
        {
            var file = history == 0 ? path : Path.Combine(Path.GetDirectoryName(path)!, $"fhre.{history}.log");
            var expected = session - history;
            var lines = File.ReadAllLines(file);
            Check(lines.Count(l => l.StartsWith("FHRE log - ", StringComparison.Ordinal)) == 1, "A normal archive contains multiple session headers.");
            Check(lines.Length == 3 && lines[1].EndsWith($"session={expected:00}; first", StringComparison.Ordinal) && lines[2].EndsWith($"session={expected:00}; second", StringComparison.Ordinal), "Archive ordering or message preservation is incorrect.");
        }
    }
    Check(!File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "fhre.5.log")), "An archive beyond the retention limit was created.");
}

static void TestConcurrentMessages(string root)
{
    var path = TestPath(root, "concurrent");
    var writer = new SessionLog(path);
    Parallel.For(0, 128, i => writer.Line($"entry={i:000}; first\nentry={i:000}; second"));
    var lines = File.ReadAllLines(path);
    Check(lines.Length == 257 && lines.Count(l => l.StartsWith("FHRE log - ", StringComparison.Ordinal)) == 1, "Concurrent logging duplicated the session header or lost messages.");
    var entries = new HashSet<int>();
    for (var offset = 1; offset < lines.Length; offset += 2)
    {
        var first = Regex.Match(lines[offset], @"^(\[\d{2}:\d{2}:\d{2}\.\d{3}\]) entry=(\d{3}); first$");
        Check(first.Success, "A concurrent message has an invalid first line.");
        var entry = int.Parse(first.Groups[2].Value);
        Check(entries.Add(entry), "A concurrent message was duplicated.");
        Check(lines[offset + 1] == $"{first.Groups[1].Value} entry={entry:000}; second", "Concurrent multiline messages were interleaved.");
    }
    Check(entries.SetEquals(Enumerable.Range(0, 128)), "A concurrent message was omitted.");
}

static void TestRotationFailure(string root)
{
    var path = TestPath(root, "failure");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, "existing session\nexisting payload\n");
    for (var index = 1; index <= 4; index++)
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, $"fhre.{index}.log"), $"archive={index}\n");
    var oldest = Path.Combine(Path.GetDirectoryName(path)!, "fhre.4.log");
    using (var held = new FileStream(oldest, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        var writer = new SessionLog(path);
        writer.Line("message after failed rotation");
        writer.Line("second message after failed rotation");
        var text = File.ReadAllText(path);
        Check(text.StartsWith("existing session\nexisting payload\n", StringComparison.Ordinal), "Failed rotation truncated the current log.");
        Check(Regex.Matches(text, "FHRE log - ").Count == 1, "Failed rotation retried session initialization for every message.");
        Check(text.Contains("WARN log.rotation.failed", StringComparison.Ordinal), "Rotation failure was not recorded in the preserved log.");
        Check(text.Contains("message after failed rotation", StringComparison.Ordinal) && text.Contains("second message after failed rotation", StringComparison.Ordinal), "Rotation failure prevented subsequent messages from being appended.");
    }
    Check(File.ReadAllText(oldest) == "archive=4\n", "Rotation failure overwrote the locked oldest archive.");
    Check(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.log").Length == 5, "Rotation failure created an additional archive.");
}

static void TestInitializationRecovery(string root)
{
    var blocked = Path.Combine(root, "blocked");
    File.WriteAllText(blocked, "temporary obstacle");
    var path = Path.Combine(blocked, "fhre.log");
    var writer = new SessionLog(path);
    writer.Line("unavailable destination");
    File.Delete(blocked);
    writer.Line("recovered destination");
    var text = File.ReadAllText(path);
    Check(text.StartsWith("FHRE log - ", StringComparison.Ordinal) && text.Contains("recovered destination", StringComparison.Ordinal), "Logging did not recover when the destination became writable.");
}
