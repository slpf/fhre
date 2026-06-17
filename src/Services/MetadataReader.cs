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
            var psi = new ProcessStartInfo(exe,
                $"-v error -select_streams a:0 " +
                $"-show_entries format=duration:format_tags=title,artist:stream_tags=title,artist " +
                $"-of default=noprint_wrappers=1 \"{path}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };

            using var p = Process.Start(psi);
            
            if (p is null)
            {
                return (null, null, 0);
            }

            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            
            p.WaitForExit();
            
            var output = so.GetAwaiter().GetResult();
            _ = se.GetAwaiter().GetResult();

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
