using System.Diagnostics;

namespace FH6RB.Services;

public static class MetadataReader
{
    public static (string? Title, string? Artist, double Duration) Read(string path)
    {
        var exe = Tools.FfprobePath;
        
        if (!File.Exists(exe))
        {
            return (null, null, 0);
        }

        try
        {
            var args =
                $"-v error -select_streams a:0 " +
                $"-show_entries format=duration:format_tags=title,artist:stream_tags=title,artist " +
                $"-of default=noprint_wrappers=1 \"{path}\"";

            var (output, _, _) = Proc.Run(exe, args, timeoutMs: 120_000,
                stdoutEncoding: System.Text.Encoding.UTF8, stderrEncoding: System.Text.Encoding.UTF8);

            string? title = null;
            string? artist = null;
            double duration = 0;

            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                var eq = line.IndexOf('=');
                
                if (eq <= 0)
                {
                    continue;
                }

                var key = line[..eq].Trim().ToLowerInvariant();
                var val = line[(eq + 1)..].Trim();
                
                if (val.Length == 0)
                {
                    continue;
                }

                if (title is null && key.EndsWith("title"))
                {
                    title = val;
                }
                else if (artist is null && key.EndsWith("artist"))
                {
                    artist = val;
                }
                else if (key == "duration" && double.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                {
                    duration = d;
                }
            }

            return (title, artist, duration);
        }
        catch
        {
            return (null, null, 0);
        }
    }
}
