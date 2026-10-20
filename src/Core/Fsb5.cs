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

    private static readonly int[] FreqTable = [4000, 8000, 11000, 11025, 16000, 22050, 24000, 32000, 44100, 48000, 96000];
    private static readonly int[] ChannelTable = [1, 2, 6, 8];

    private byte[] Header60 { get; }
    public List<Fsb5Sample> Samples { get; }

    private Fsb5(byte[] header60, List<Fsb5Sample> samples) { Header60 = header60; Samples = samples; }

    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    private static ulong U64(byte[] b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o));
    
    public static (long Frames, int SampleRate)[] ReadSampleInfo(byte[] b)
    {
        try { return ReadLayout(b).Samples.Select(s => (s.Frames, s.SampleRate)).ToArray(); }
        catch (InvalidDataException) { return []; }
    }

    public static Fsb5 Parse(byte[] b)
    {
        var (header60, layout) = ReadLayout(b);
        var dataStart = HeaderSize + (long) U32(b, 12) + U32(b, 16);
        if (dataStart + U32(b, 20) > b.Length)
        {
            throw new InvalidDataException("FSB5 data region is truncated");
        }
        var samples = new List<Fsb5Sample>(layout.Count);
        foreach (var sample in layout)
        {
            var start = checked((int) (dataStart + sample.DataOff));
            var end = checked((int) (dataStart + sample.DataOff + sample.DataLen));
            samples.Add(new Fsb5Sample
            {
                Header = sample.Header, Data = b[start..end], Frames = sample.Frames,
                SampleRate = sample.SampleRate, Channels = sample.Channels,
            });
        }
        
        return new Fsb5(header60, samples);
    }

    private static void RebaseOffsetInto(byte[] header, long byteOffset, Span<byte> dest)
    {
        if (byteOffset < 0 || byteOffset % 0x20 != 0 || byteOffset / 0x20 > 0x07FFFFFF)
        {
            throw new ArgumentException("data offset must fit the FSB5 field and be 0x20-aligned");
        }
        if (header.Length < 8) throw new InvalidDataException("FSB5 sample header is truncated");
        
        header.CopyTo(dest);
        
        var raw = BinaryPrimitives.ReadUInt64LittleEndian(dest);
        raw = (raw & ~OffMask) | ((((ulong)byteOffset / 0x20) & 0x07FFFFFF) << 7);
        BinaryPrimitives.WriteUInt64LittleEndian(dest, raw);
    }

    public sealed record SampleLayout(byte[] Header, long DataOff, long DataLen, long Frames, int SampleRate, int Channels = 0);

    public static (byte[] Header60, List<SampleLayout> Samples) ReadLayout(byte[] b)
    {
        if (b.Length < HeaderSize || b[0] != (byte)'F' || b[1] != (byte)'S' || b[2] != (byte)'B' || b[3] != (byte)'5')
        {
            throw new InvalidDataException("not an FSB5 block");
        }

        if (U32(b, 4) != 1) throw new InvalidDataException($"unsupported FSB5 version {U32(b, 4)}");
        var sampleHeaderSize = U32(b, 12);
        var count = U32(b, 8);
        long dataSize = U32(b, 20);
        if (sampleHeaderSize > b.Length - HeaderSize || count > sampleHeaderSize / 8)
        {
            throw new InvalidDataException($"FSB5 sample header region is invalid (count={count}, bytes={sampleHeaderSize})");
        }
        var numSamples = checked((int) count);
        var headerEnd = HeaderSize + checked((int) sampleHeaderSize);

        var header60 = b[0..HeaderSize];
        var hdrOff = new int[numSamples];
        var hdrLen = new int[numSamples];
        var dataOff = new long[numSamples];
        var frames = new long[numSamples];
        var rates = new int[numSamples];
        var channels = new int[numSamples];

        var pos = HeaderSize;
        
        for (var i = 0; i < numSamples; i++)
        {
            if (pos > headerEnd - 8)
            {
                throw new InvalidDataException("FSB5 layout: header region truncated");
            }
            
            var raw = U64(b, pos);
            
            var freqId = (int)((raw >> 1) & 0xF);
            if (freqId >= FreqTable.Length) throw new InvalidDataException($"FSB5 sample {i} has invalid frequency id {freqId}");
            rates[i] = FreqTable[freqId];
            channels[i] = ChannelTable[(int)((raw >> 5) & 0x3)];
            dataOff[i] = (long)((raw >> 7) & 0x07FFFFFF) * 0x20;
            frames[i]  = (long)((raw >> 34) & 0x3FFFFFFF);

            var p = pos + 8;
            
            if ((raw & 1) != 0)
            {
                uint chunk;
                do
                {
                    if (p > headerEnd - 4) throw new InvalidDataException($"FSB5 sample {i} metadata header is truncated");
                    chunk = U32(b, p);
                    var size = (int)((chunk >> 1) & 0xFFFFFF);
                    var type = chunk >> 25;
                    p += 4;
                    if (size > headerEnd - p) throw new InvalidDataException($"FSB5 sample {i} metadata payload is truncated");
                    switch (type)
                    {
                        case 1:
                            if (size < 1 || b[p] == 0) throw new InvalidDataException($"FSB5 sample {i} has invalid channel metadata");
                            channels[i] = b[p];
                            break;
                        case 2:
                            if (size < 4 || U32(b, p) == 0 || U32(b, p) > int.MaxValue)
                                throw new InvalidDataException($"FSB5 sample {i} has invalid frequency metadata");
                            rates[i] = (int) U32(b, p);
                            break;
                        case 14:
                            if (size < 4 || U32(b, p) == 0 || U32(b, p) > int.MaxValue / channels[i])
                                throw new InvalidDataException($"FSB5 sample {i} has invalid layer metadata");
                            channels[i] *= (int) U32(b, p);
                            break;
                    }
                    p += size;
                } while ((chunk & 1) != 0);
            }

            hdrOff[i] = pos;
            hdrLen[i] = p - pos;
            pos = p;
        }

        var samples = new List<SampleLayout>(numSamples);
        
        for (var i = 0; i < numSamples; i++)
        {
            var end = (i + 1 < numSamples) ? dataOff[i + 1] : dataSize;
            if (dataOff[i] > dataSize || end < dataOff[i] || end > dataSize)
                throw new InvalidDataException($"FSB5 sample {i} data offsets are invalid");
            samples.Add(new SampleLayout(b[hdrOff[i]..(hdrOff[i] + hdrLen[i])], dataOff[i], end - dataOff[i],
                frames[i], rates[i], channels[i]));
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

    public static byte[] BuildSingleSample(byte[] header60, Fsb5Sample sample)
    {
        var headersLen = sample.Header.Length;
        var pad = (0x20 - (sample.Data.Length % 0x20)) % 0x20;
        var dataLen = sample.Data.Length + pad;
        var total = checked((int)(HeaderSize + headersLen + dataLen));

        var outBuf = new byte[total];
        header60.AsSpan(0, HeaderSize).CopyTo(outBuf);
        BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(8), 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(12), (uint) headersLen);
        BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(16), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(20), (uint) dataLen);

        var hPos = HeaderSize;
        var dPos = HeaderSize + headersLen;
        RebaseOffsetInto(sample.Header, 0, outBuf.AsSpan(hPos, sample.Header.Length));
        sample.Data.CopyTo(outBuf.AsSpan(dPos));

        return outBuf;
    }
}
