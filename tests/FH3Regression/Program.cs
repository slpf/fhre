var scratch = Path.Combine(AppContext.BaseDirectory, "scratch", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
var previousTemp = Environment.GetEnvironmentVariable("TEMP");
var previousTmp = Environment.GetEnvironmentVariable("TMP");
Environment.SetEnvironmentVariable("TEMP", scratch);
Environment.SetEnvironmentVariable("TMP", scratch);
try
{
    await SyntheticChecks.RunAsync(scratch);
    var installation = Array.IndexOf(args, "--installation");
    if (installation >= 0)
    {
        if (installation + 1 >= args.Length) throw new ArgumentException("--installation requires a source directory.");
        await InstallationChecks.RunAsync(Path.GetFullPath(args[installation + 1]), scratch);
    }
    Console.WriteLine("FH3 regression checks passed.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Console.Error.WriteLine("Fixtures: " + scratch);
    return 1;
}
finally
{
    Environment.SetEnvironmentVariable("TEMP", previousTemp);
    Environment.SetEnvironmentVariable("TMP", previousTmp);
    if (!args.Contains("--keep", StringComparer.Ordinal)) TestSupport.DeleteDirectory(scratch, Path.Combine(AppContext.BaseDirectory, "scratch"));
}
