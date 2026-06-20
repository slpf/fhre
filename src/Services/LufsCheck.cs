#if DEBUG
namespace FH6RB.Services;

public static class LufsCheck
{
    public static async Task RunAsync(string bankPath, IReadOnlyList<(string Name, int Sub)> tracks)
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, "lufs.log");
        var lines = new List<string>
        {
            $"# LUFS check {DateTime.Now:yyyy-MM-dd HH:mm:ss} | {Path.GetFileName(bankPath)} | {tracks.Count} tracks",
        };

        foreach (var (name, sub) in tracks)
        {
            if (sub < 0)
            {
                continue;
            }

            try
            {
                var wav = await Task.Run(() => AudioDecoder.DecodeBank(bankPath, sub)).ConfigureAwait(false);
                var lufs = await Loudnorm.MeasureIntegratedAsync(wav).ConfigureAwait(false);
                lines.Add($"{lufs ?? "n/a",10}  {name}");
            }
            catch (Exception ex)
            {
                lines.Add($"{"ERR",10}  {name}  ({ex.Message})");
            }
        }

        try
        {
            await File.WriteAllLinesAsync(logPath, lines).ConfigureAwait(false);
            Log.Line($"LUFS check written: {logPath}");
        }
        catch (Exception ex)
        {
            Log.Line("LUFS check write failed: " + ex.Message);
        }
    }
}
#endif
