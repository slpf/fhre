#if DEBUG
namespace FH6RB.Services;

public static class LufsCheck
{
    public static async Task RunAsync(string bankPath, IReadOnlyList<(string Name, int Sub)> tracks)
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, "lufs.log");
        var header = $"# LUFS check {DateTime.Now:yyyy-MM-dd HH:mm:ss} | {Path.GetFileName(bankPath)} | {tracks.Count} tracks";

        try
        {
            await File.WriteAllTextAsync(logPath, header + Environment.NewLine).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Line("LUFS log create failed: " + ex.Message);
            return;
        }

        Log.Line($"LUFS check: start -> {logPath}");

        var done = 0;
        foreach (var (name, sub) in tracks)
        {
            if (sub < 0)
            {
                continue;
            }

            string line;
            try
            {
                var wav = await Task.Run(() => AudioDecoder.DecodeBank(bankPath, sub)).ConfigureAwait(false);
                var lufs = await Loudnorm.MeasureIntegratedAsync(wav).ConfigureAwait(false);
                line = $"{lufs ?? "n/a",10}  {name}";
            }
            catch (Exception ex)
            {
                line = $"{"ERR",10}  {name}  ({ex.Message})";
            }

            try
            {
                await File.AppendAllTextAsync(logPath, line + Environment.NewLine).ConfigureAwait(false);
            }
            catch
            {
            }

            Log.Line($"LUFS {++done}: {line.Trim()}");
        }

        Log.Line($"LUFS check done: {logPath} ({done} measured)");
    }
}
#endif