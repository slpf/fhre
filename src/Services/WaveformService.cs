namespace FH6RB.Services;

public static class WaveformService
{
    public static (int SampleRate, long Frames) Probe(string wavPath)
    {
        using var fs = File.OpenRead(wavPath);
        using var br = new BinaryReader(fs);

        if (fs.Length < 44 || new string(br.ReadChars(4)) != "RIFF")
        {
            return (0, 0);
        }
        
        br.ReadInt32();
        
        if (new string(br.ReadChars(4)) != "WAVE")
        {
            return (0, 0);
        }

        int channels = 2, bits = 16, rate = 0;
        long dataLen = 0;

        while (fs.Position + 8 <= fs.Length)
        {
            var id = new string(br.ReadChars(4));
            var sz = br.ReadInt32();
            if (sz < 0) break;

            if (id == "fmt ")
            {
                br.ReadInt16();
                channels = br.ReadInt16();
                rate = br.ReadInt32();
                br.ReadInt32();
                br.ReadInt16();
                bits = br.ReadInt16();
                
                if (sz > 16)
                {
                    fs.Position += sz - 16;
                }
            }
            else if (id == "data")
            {
                dataLen = Math.Min(sz, fs.Length - fs.Position);
                fs.Position += dataLen;
            }
            else
            {
                fs.Position += sz;
            }

            if ((sz & 1) == 1 && fs.Position < fs.Length)
            {
                fs.Position += 1;
            }
        }

        if (rate <= 0 || bits != 16 || channels < 1)
        {
            return (0, 0);
        }
        
        return (rate, dataLen / (2 * channels));
    }

    public static float[] Peaks(string wavPath, int buckets)
    {
        if (buckets < 1)
        {
            buckets = 1;
        }

        using var fs = File.OpenRead(wavPath);
        using var br = new BinaryReader(fs);

        if (fs.Length < 44 || new string(br.ReadChars(4)) != "RIFF")
        {
            return [];
        }
        
        br.ReadInt32();
        
        if (new string(br.ReadChars(4)) != "WAVE")
        {
            return [];
        }

        int channels = 2, bits = 16;
        long dataPos = -1, dataLen = 0;

        while (fs.Position + 8 <= fs.Length)
        {
            var id = new string(br.ReadChars(4));
            var sz = br.ReadInt32();
            
            if (sz < 0)
            {
                break;
            }

            if (id == "fmt ")
            {
                br.ReadInt16();
                channels = br.ReadInt16();
                br.ReadInt32();
                br.ReadInt32();
                br.ReadInt16();
                bits = br.ReadInt16();
                
                if (sz > 16)
                {
                    fs.Position += sz - 16;
                }
            }
            else if (id == "data")
            {
                dataPos = fs.Position;
                dataLen = Math.Min(sz, fs.Length - fs.Position);
                fs.Position += dataLen;
            }
            else
            {
                fs.Position += sz;
            }

            if ((sz & 1) == 1 && fs.Position < fs.Length)
            {
                fs.Position += 1;
            }
        }

        if (dataPos < 0 || bits != 16 || channels < 1)
        {
            return [];
        }

        var frameBytes = 2 * channels;
        var frames = dataLen / frameBytes;
        
        if (frames <= 0)
        {
            return [];
        }

        if (buckets > frames)
        {
            buckets = (int)frames;
        }

        fs.Position = dataPos;
        
        var data = br.ReadBytes((int) Math.Min(dataLen, int.MaxValue));
        
        frames = data.Length / frameBytes;
        
        if (frames <= 0)
        {
            return [];
        }

        var peaks = new float[buckets];
        
        for (long f = 0; f < frames; f++)
        {
            var bucket = (int) (f * buckets / frames);
            
            if (bucket >= buckets)
            {
                bucket = buckets - 1;
            }

            var off = (int) (f * frameBytes);
            
            short max = 0;
            
            for (var c = 0; c < channels; c++)
            {
                var v = (short) (data[off + c * 2] | (data[off + c * 2 + 1] << 8));
                var a = v == short.MinValue ? short.MaxValue : (short) Math.Abs((int) v);
                
                if (a > max)
                {
                    max = a;
                }
            }

            var amp = max / 32768f;
            
            if (amp > peaks[bucket])
            {
                peaks[bucket] = amp;
            }
        }

        return peaks;
    }
}
