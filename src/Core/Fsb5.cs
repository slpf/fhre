using System.Buffers.Binary;

namespace FH6RB.Core;

public sealed class Fsb5Sample
{
    public required byte[] Header;
    public required byte[] Data;
    public long Frames;
    public int  SampleRate;
    public int  Channels;
    public TimeSpan Duration => SampleRate > 0 ? TimeSpan.FromSeconds((double)Frames / SampleRate) : TimeSpan.Zero;
}

public sealed class Fsb5
{
    private const int HeaderSize = 60;
    private const ulong OffMask = 0x07FFFFFFUL << 7;

    private static readonly int[] FreqTable = [0, 8000, 11000, 11025, 16000, 22050, 24000, 32000, 44100, 48000, 96000];

    private byte[] Header60 { get; }
    public List<Fsb5Sample> Samples { get; }

    private Fsb5(byte[] header60, List<Fsb5Sample> samples) { Header60 = header60; Samples = samples; }

    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    private static ulong U64(byte[] b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o));
    
    public static (long Frames, int SampleRate)[] ReadSampleInfo(byte[] b)
    {
        if (b.Length < HeaderSize || b[0] != (byte)'F' || b[1] != (byte)'S' || b[2] != (byte)'B' || b[3] != (byte)'5')
        {
            return [];
        }

        var numSamples = (int) U32(b, 8);
        var sampleHeaderSize = (int) U32(b, 12);
        var nameStart = HeaderSize + sampleHeaderSize;

        var res = new (long, int)[numSamples];
        var pos = HeaderSize;

        for (var i = 0; i < numSamples; i++)
        {
            if (pos + 8 > b.Length)
            {
                break;
            }

            var raw = U64(b, pos);
            var freqId = (int)((raw >> 1) & 0xF);
            var frames = (long)((raw >> 34) & 0x3FFFFFFF);

            var p = pos + 8;
            if ((raw & 1) != 0)
            {
                while (p + 4 <= b.Length)
                {
                    var ch = U32(b, p);
                    var size = (int)((ch >> 1) & 0xFFFFFF);
                    p += 4 + size;
                    if ((ch & 1) == 0) break;
                }
            }

            res[i] = (frames, freqId < FreqTable.Length ? FreqTable[freqId] : 0);
            pos = p;
            if (pos > nameStart) break;
        }

        return res;
    }

    public static Fsb5 Parse(byte[] b)
    {
        if (b.Length < HeaderSize || b[0] != (byte)'F' || b[1] != (byte)'S' || b[2] != (byte)'B' || b[3] != (byte)'5')
        {
            throw new InvalidDataException("not an FSB5 block");
        }

        var numSamples = (int) U32(b, 8);
        var sampleHeaderSize = (int) U32(b, 12);
        var nameSize = (int) U32(b, 16);
        long dataSize = U32(b, 20);

        var header60 = b[0..HeaderSize];
        var shStart = HeaderSize;
        var nameStart = HeaderSize + sampleHeaderSize;
        var dataStart = nameStart + nameSize;

        var hdrOff = new int[numSamples];
        var hdrLen = new int[numSamples];
        var dataOff = new long[numSamples];
        var frames = new long[numSamples];
        var freqId = new int[numSamples];
        var chans = new int[numSamples];

        var pos = shStart;
        
        for (int i = 0; i < numSamples; i++)
        {
            var raw = U64(b, pos);
            freqId[i] = (int)((raw >> 1) & 0xF);
            chans[i] = (int)((raw >> 5) & 0x3) + 1;
            dataOff[i] = (long)((raw >> 7) & 0x07FFFFFF) * 0x20;
            frames[i] = (long)((raw >> 34) & 0x3FFFFFFF);

            var p = pos + 8;
            
            if ((raw & 1) != 0)
            {
                while (p + 4 <= b.Length)
                {
                    var ch = U32(b, p);
                    var size = (int)((ch >> 1) & 0xFFFFFF);
                    p += 4 + size;
                    if ((ch & 1) == 0) break;
                }
            }
            
            hdrOff[i] = pos; hdrLen[i] = p - pos;
            pos = p;
        }

        if (pos > nameStart)
        {
            throw new InvalidDataException($"FSB5 header walk overran at 0x{pos:x}, expected <= 0x{nameStart:x}");
        }
        
        var samples = new List<Fsb5Sample>(numSamples);
        
        for (var i = 0; i < numSamples; i++)
        {
            var end = (i + 1 < numSamples) ? dataOff[i + 1] : dataSize;
            
            samples.Add(new Fsb5Sample
            {
                Header = b[hdrOff[i]..(hdrOff[i] + hdrLen[i])],
                Data = b[(int)(dataStart + dataOff[i])..(int)(dataStart + end)],
                Frames = frames[i],
                SampleRate = freqId[i] < FreqTable.Length ? FreqTable[freqId[i]] : 0,
                Channels = chans[i],
            });
        }
        
        return new Fsb5(header60, samples);
    }

    private static void RebaseOffsetInto(byte[] header, long byteOffset, Span<byte> dest)
    {
        if (byteOffset % 0x20 != 0)
        {
            throw new ArgumentException("data offset must be 0x20-aligned");
        }
        
        header.CopyTo(dest);
        
        var raw = BinaryPrimitives.ReadUInt64LittleEndian(dest);
        raw = (raw & ~OffMask) | ((((ulong)byteOffset / 0x20) & 0x07FFFFFF) << 7);
        BinaryPrimitives.WriteUInt64LittleEndian(dest, raw);
    }

    public sealed record SampleLayout(byte[] Header, long DataOff, long DataLen, long Frames, int SampleRate);

    public static (byte[] Header60, List<SampleLayout> Samples) ReadLayout(byte[] b)
    {
        if (b.Length < HeaderSize || b[0] != (byte)'F' || b[1] != (byte)'S' || b[2] != (byte)'B' || b[3] != (byte)'5')
        {
            throw new InvalidDataException("not an FSB5 block");
        }

        var numSamples = (int) U32(b, 8);
        var sampleHeaderSize = (int) U32(b, 12);
        long dataSize = U32(b, 20);

        var header60 = b[0..HeaderSize];
        var hdrOff = new int[numSamples];
        var hdrLen = new int[numSamples];
        var dataOff = new long[numSamples];
        var frames = new long[numSamples];
        var freqId = new int[numSamples];

        var pos = HeaderSize;
        
        for (var i = 0; i < numSamples; i++)
        {
            if (pos + 8 > b.Length)
            {
                throw new InvalidDataException("FSB5 layout: header region truncated");
            }
            
            var raw = U64(b, pos);
            
            freqId[i]  = (int)((raw >> 1) & 0xF);
            dataOff[i] = (long)((raw >> 7) & 0x07FFFFFF) * 0x20;
            frames[i]  = (long)((raw >> 34) & 0x3FFFFFFF);

            var p = pos + 8;
            
            if ((raw & 1) != 0)
            {
                while (p + 4 <= b.Length)
                {
                    var ch = U32(b, p);
                    var size = (int)((ch >> 1) & 0xFFFFFF);
                    p += 4 + size;
                    if ((ch & 1) == 0) break;
                }
            }

            hdrOff[i] = pos;
            hdrLen[i] = p - pos;
            pos = p;
        }

        var samples = new List<SampleLayout>(numSamples);
        
        for (var i = 0; i < numSamples; i++)
        {
            var end = (i + 1 < numSamples) ? dataOff[i + 1] : dataSize;
            samples.Add(new SampleLayout(b[hdrOff[i]..(hdrOff[i] + hdrLen[i])], dataOff[i], end - dataOff[i],
                frames[i], freqId[i] < FreqTable.Length ? FreqTable[freqId[i]] : 0));
        }

        return (header60, samples);
    }

    public static byte[] RebaseHeader(byte[] header, long byteOffset)
    {
        var h = new byte[header.Length];
        RebaseOffsetInto(header, byteOffset, h);
        return h;
    }

    public byte[] Build(IReadOnlyList<Fsb5Sample> samples)
    {
        long headersLen = 0;
        long dataLen = 0;
        
        foreach (var s in samples)
        {
            headersLen += s.Header.Length;
            var pad = (0x20 - (s.Data.Length % 0x20)) % 0x20;
            dataLen += s.Data.Length + pad;
        }

        var total = checked((int)(HeaderSize + headersLen + dataLen));
        var outBuf = new byte[total];
        
        Header60.AsSpan(0, HeaderSize).CopyTo(outBuf);
        BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(8),  (uint)samples.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(12), (uint)headersLen);
        BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(16), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(20), (uint)dataLen);

        var hPos = HeaderSize;
        var dPos = HeaderSize + (int)headersLen;
        long off = 0;

        foreach (var s in samples)
        {
            RebaseOffsetInto(s.Header, off, outBuf.AsSpan(hPos, s.Header.Length));
            hPos += s.Header.Length;
            s.Data.CopyTo(outBuf.AsSpan(dPos));
            
            var pad = (0x20 - (s.Data.Length % 0x20)) % 0x20;
            dPos += s.Data.Length + pad;
            off += s.Data.Length + pad;
        }

        return outBuf;
    }
}
