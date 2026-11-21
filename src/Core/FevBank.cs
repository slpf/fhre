using System.Buffers.Binary;

namespace FH6RB.Core;

public sealed class FevBank
{
    private readonly byte[] _src;
    
    private int _fmtOff, _fmtPay, _fmtSize;
    private int _listOff, _listPay, _listSize;
    private int _sndOff,  _sndPay,  _sndSize;
    private int _stblOff, _stblPay, _stblSize;
    private int _sndhPay;
    private int _fsbOff, _fsbSize;

    public FevBank(byte[] src)
    {
        _src = src;
        Parse();
    }

    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    private static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));
    private static string Cc(byte[] b, int o) => System.Text.Encoding.ASCII.GetString(b, o, 4);

    private void Parse()
    {
        if (_src.Length < 12 || Cc(_src, 0) != "RIFF" || Cc(_src, 8) != "FEV ")
        {
            throw new InvalidDataException("not a RIFF/FEV bank");
        }
        
        var pos = 12;
        
        while (pos + 8 <= _src.Length)
        {
            var id = Cc(_src, pos);
            var size = (int) U32(_src, pos + 4);
            var pay = pos + 8;
            if (size < 0 || size > _src.Length - pay)
                throw new InvalidDataException($"RIFF chunk {id} has an invalid size {size}");
            
            switch (id)
            {
                case "FMT ": _fmtOff = pos; _fmtPay = pay; _fmtSize = size; break;
                case "LIST": _listOff = pos; _listPay = pay; _listSize = size; break;
                case "SND ": _sndOff = pos; _sndPay = pay; _sndSize = size; break;
            }
            
            if (id == "SND ") break;
            
            pos = pay + size;
            if ((pos & 1) != 0) pos++;
        }
        
        var p = _listPay + 4;
        var end = _listPay + _listSize;
        var sndhSize = 0;
        
        while (p + 8 <= end)
        {
            var id = Cc(_src, p);
            var size = (int) U32(_src, p + 4);
            var pay = p + 8;
            if (size < 0 || size > end - pay)
                throw new InvalidDataException($"LIST chunk {id} has an invalid size {size}");
            
            switch (id)
            {
                case "STBL":
                    _stblOff = p; 
                    _stblPay = pay; 
                    _stblSize = size;
                    break;
                case "SNDH":
                    if (size < 12 && size is not 0 and not 4) throw new InvalidDataException("SNDH chunk is truncated");
                    _sndhPay = pay;
                    sndhSize = size;
                    break;
            }

            p = pay + size;
            if ((p & 1) != 0) p++;
        }

        if (_sndhPay == 0) throw new InvalidDataException("FEV bank has no SNDH chunk");
        if (sndhSize >= 12)
        {
            _fsbOff = (int) U32(_src, _sndhPay + 4);
            _fsbSize = (int) U32(_src, _sndhPay + 8);
        }
    }

    private byte[] FmtChunk() => _src[_fmtOff..(_fmtPay + _fmtSize + (_fmtSize & 1))];
    public int Fsb5Mode => _fsbOff > 0 && _fsbOff + 0x1C <= _src.Length ? (int) U32(_src, _fsbOff + 0x18) : 0;
    public byte[] ExtractFsb5()
    {
        if (_fsbOff < 0 || _fsbSize <= 0 || (long) _fsbOff + _fsbSize > _src.Length)
        {
            throw new InvalidDataException($"FSB5 region out of bank bounds (off={_fsbOff}, size={_fsbSize}, bank={_src.Length})");
        }

        return _src[_fsbOff..(_fsbOff + _fsbSize)];
    }
    
    public static HashSet<ulong> ReadStblIdsFromFile(string path) => ReadStblInfoFromFile(path).Ids;

    public static int ReadStblCountFromFile(string path) => ReadStblInfoFromFile(path).Declared;

    internal static HashSet<ulong>? ReadCompleteStblIdsFromFile(string path)
    {
        var (ids, declared) = ReadStblInfoFromFile(path, requireComplete: true);
        return declared >= 0 && ids.Count == declared ? ids : null;
    }

    internal static HashSet<ulong> ReadCompleteStblIds(Stream stream)
    {
        var (ids, declared) = ReadStblInfo(stream, requireComplete: true, out _);
        if (declared < 0 || ids.Count != declared)
            throw new InvalidDataException("Invalid or incomplete RIFF/FEV track bank.");
        return ids;
    }

    internal static (HashSet<ulong> Ids, int Declared) ReadStblInfoFromFile(string path, bool requireComplete = false)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return ReadStblInfo(stream, requireComplete, out _);
        }
        catch
        {
            return ([], -1);
        }
    }

    internal static int ValidateTrackBank(string path)
    {
        using var stream = File.OpenRead(path);
        return ReadTrackProject(stream).Tracks.Count;
    }

    private static (byte[] List, List<(ulong Id, int Index)> Tracks) ReadTrackProject(Stream stream)
    {
        var (ids, declared) = ReadStblInfo(stream, requireComplete: true, out var list);
        if (list is null || declared < 0 || ids.Count != declared)
            throw new InvalidDataException("Invalid or incomplete RIFF/FEV track bank.");
        var tracks = ParseStblEntries(list);
        if (tracks.Count != declared)
            throw new InvalidDataException("RIFF/FEV track bank has an invalid track table.");
        return (list, tracks);
    }

    private static (HashSet<ulong> Ids, int Declared) ReadStblInfo(Stream fs, bool requireComplete, out byte[]? project)
    {
        project = null;
        var ids = new HashSet<ulong>();

        if (fs.Length < 12)
        {
            return (ids, -1);
        }

        var head = new byte[12];

        if (fs.Read(head, 0, 12) < 12 || Cc(head, 0) != "RIFF" || Cc(head, 8) != "FEV ")
        {
            return (ids, -1);
        }

        var riffEnd = fs.Length;
        if (requireComplete)
        {
            riffEnd = U32(head, 4) + 8L;
            if (riffEnd < 12 || riffEnd > fs.Length) return (ids, -1);
            if (riffEnd != fs.Length)
            {
                if (fs.Length - riffEnd != ModMarker.Length) return (ids, -1);
                fs.Position = riffEnd;
                var marker = new byte[ModMarker.Length];
                fs.ReadExactly(marker);
                if (!marker.AsSpan().SequenceEqual(ModMarker)) return (ids, -1);
                fs.Position = head.Length;
            }
        }
        var hasFormat = false;
        var hasSound = false;
        var declared = -1;
        var emptyTemplate = false;
        var hdr = new byte[8];

        while (fs.Position + 8 <= riffEnd)
        {
            if (fs.Read(hdr, 0, 8) < 8)
            {
                break;
            }

            var id = Cc(hdr, 0);
            var size = (int) U32(hdr, 4);
            if (size < 0 || size > riffEnd - fs.Position) return (ids, -1);

            if (id == "LIST")
            {
                var list = new byte[size];

                fs.ReadExactly(list);
                if (!requireComplete) return (ids, ParseStblIds(list, ids));
                if (list.Length >= 4 && Cc(list, 0) == "PROJ")
                {
                    if (declared >= 0) return (ids, -1);
                    declared = ParseStblIds(list, ids, requireComplete: true);
                    if (declared < 0) return (ids, -1);
                    project = list;
                    emptyTemplate = FindChunkInList(list, "STBL").PayloadSize == 0;
                    if (emptyTemplate && FindChunkInList(list, "SNDH").TagOff < 0) return (ids, -1);
                }
            }
            else
            {
                if (id == "FMT ") hasFormat |= size > 0;
                if (id == "SND ")
                {
                    if (!requireComplete) break;
                    hasSound = true;
                }
                fs.Seek(size, SeekOrigin.Current);
            }
            if ((size & 1) != 0 && (!requireComplete || id != "SND " || fs.Position != riffEnd))
                fs.Seek(1, SeekOrigin.Current);
        }
        return requireComplete && hasFormat && hasSound != emptyTemplate && fs.Position == riffEnd
            ? (ids, declared)
            : (ids, -1);
    }

    private static int ParseStblIds(byte[] list, HashSet<ulong> ids, bool requireComplete = false)
    {
        var p = 4;
        var declared = -1;
        
        while (p + 8 <= list.Length)
        {
            var id = Cc(list, p);
            var size = (int) U32(list, p + 4);
            var pay = p + 8;

            if (size < 0 || size > list.Length - pay) return -1;

            if (id == "STBL")
            {
                if (declared >= 0) return -1;
                if (requireComplete && size == 0)
                {
                    declared = 0;
                }
                else
                {
                    if (size < 10) return -1;
                    declared = U16(list, pay + 8);
                    var idp = pay + 10;
                    for (var i = 0; i < declared && idp + 8 <= pay + size; i++, idp += 8)
                        ids.Add(BinaryPrimitives.ReadUInt64LittleEndian(list.AsSpan(idp)));
                }
                if (!requireComplete) return declared;
            }

            p = pay + size;
            
            if ((p & 1) != 0)
            {
                p++;
            }
        }

        return p == list.Length ? declared : -1;
    }
    
    public sealed record BankTrack(ulong Id, int Index, long Frames, int SampleRate)
    {
        internal long Bytes { get; init; }
    }

    public static List<BankTrack> ReadTrackInfoFromFile(string path)
    {
        using var stream = File.OpenRead(path);
        return ReadTrackInfo(stream, out _, out _);
    }

    internal static List<BankTrack> ReadTrackInfo(Stream stream, out int mode, out long audioBytes)
    {
        var (list, stbl) = ReadTrackProject(stream);
        var (fsbOffset, fsbSize) = ParseSndh(list);
        mode = 0;
        audioBytes = 0;
        if (fsbOffset == 0 && fsbSize == 0)
            return stbl.Select(track => new BankTrack(track.Id, track.Index, 0, 0)).ToList();

        var (region, _) = ReadFsbRegion(stream, fsbOffset, fsbSize);
        var samples = Fsb5.ReadLayout(region).Samples;
        ValidateTrackIndices(stbl, samples.Count);
        mode = (int) U32(region, 24);
        audioBytes = samples.Sum(sample => sample.Header.Length + AlignDataLength(sample.DataLen));
        return stbl.Select(track =>
        {
            var sample = samples[track.Index];
            return new BankTrack(track.Id, track.Index, sample.Frames, sample.SampleRate)
            {
                Bytes = sample.Header.Length + AlignDataLength(sample.DataLen)
            };
        }).ToList();
    }

    internal static void ValidateTrackIndices(List<(ulong Id, int Index)> tracks, int sampleCount)
    {
        var indices = new HashSet<int>();
        foreach (var (_, index) in tracks)
        {
            if (index < 0 || index >= sampleCount)
                throw new InvalidDataException($"STBL sample index {index} is outside the FSB5 sample table.");
            if (!indices.Add(index))
                throw new InvalidDataException($"Duplicate STBL sample index {index}.");
        }
    }

    private static List<(ulong Id, int Index)> ParseStblEntries(byte[] list)
    {
        var res = new List<(ulong, int)>();
        var p = 4;
        
        while (p + 8 <= list.Length)
        {
            var id = Cc(list, p);
            var size = (int) U32(list, p + 4);
            var pay = p + 8;

            if (id == "STBL")
            {
                if (size < 10 || pay + 10 > list.Length) return res;
                
                int count = U16(list, pay + 8);
                var ids = new ulong[count];
                var idp = pay + 10;
                var ok = true;
                
                for (var i = 0; i < count; i++, idp += 8)
                {
                    if (idp + 8 > list.Length) { ok = false; break; }
                    ids[i] = BinaryPrimitives.ReadUInt64LittleEndian(list.AsSpan(idp));
                }

                var q = pay + 10 + count * 8;
                
                if (ok && q + 2 <= list.Length)
                {
                    int count2 = U16(list, q); q += 2;
                    for (var i = 0; i < count2 && i < count && q + 3 <= list.Length; i++)
                    {
                        var idx = list[q] | list[q + 1] << 8 | list[q + 2] << 16; q += 3;
                        res.Add((ids[i], idx));
                    }
                }

                return res;
            }

            p = pay + size;
            
            if ((p & 1) != 0)
            {
                p++;
            }
        }

        return res;
    }

    private static (int Off, int Size) ParseSndh(byte[] list)
    {
        var p = 4;
        
        while (p + 8 <= list.Length)
        {
            var id = Cc(list, p);
            var size = (int) U32(list, p + 4);
            var pay = p + 8;

            if (id == "SNDH" && size >= 12 && pay + 12 <= list.Length)
            {
                return ((int) U32(list, pay + 4), (int) U32(list, pay + 8));
            }

            p = pay + size;
            
            if ((p & 1) != 0)
            {
                p++;
            }
        }

        return (0, 0);
    }

    public static byte[] BuildStbl(IEnumerable<(ulong Id, int Index)> entries, uint version = 1, uint unk = 0, uint tail = 4)
    {
        var list = entries.OrderBy(e => e.Id).ToList();
        if (list.Count > ushort.MaxValue)
            throw new InvalidDataException($"STBL supports at most {ushort.MaxValue} entries");
        if (list.Any(e => e.Index < 0 || e.Index > 0xFFFFFF))
            throw new InvalidDataException("STBL sample indices must fit in 24 bits");
        
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        
        w.Write(version);
        w.Write(unk);
        w.Write((ushort) list.Count);
        
        foreach (var e in list)
        {
            w.Write(e.Id);
        }
        
        w.Write((ushort) list.Count);
        
        foreach (var e in list)
        {
            w.Write((byte)(e.Index & 0xff));
            w.Write((byte)((e.Index >> 8) & 0xff));
            w.Write((byte)((e.Index >> 16) & 0xff));
        }
        
        w.Write(tail);
        
        return ms.ToArray();
    }

    public bool IsEmptySkeleton => _sndOff == 0 || _stblSize == 0;

    public byte[] FillEmpty(byte[] stblPayload, byte[] newFsb5) =>
        BuildBank(stblPayload, newFsb5, emptyTemplate: true);

    public byte[] WithStblAndFsb(byte[] stblPayload, byte[] newFsb5) =>
        BuildBank(stblPayload, newFsb5, emptyTemplate: false);

    private byte[] BuildBank(byte[] stblPayload, byte[] fsb, bool emptyTemplate)
    {
        var header = BuildBankHeader(FmtChunk(), _src[_listPay..(_listPay + _listSize)], stblPayload, fsb.Length,
            emptyTemplate, markerLength: 0);
        var bank = new byte[header.Length + fsb.Length];
        header.CopyTo(bank, 0);
        fsb.CopyTo(bank, header.Length);
        return bank;
    }

    private static void WriteChunk(Stream stream, string id, byte[] payload)
    {
        WriteTag(stream, id);
        WriteU32(stream, (uint) payload.Length);
        stream.Write(payload);
        if ((payload.Length & 1) != 0) stream.WriteByte(0);
    }

    private static byte[] BuildBankHeader(byte[] fmtChunk, byte[] list, byte[] stbl, long fsbSize,
        bool emptyTemplate, int markerLength, (int Offset, int Size)? stblChunk = null)
    {
        var (stblOffset, stblSize) = stblChunk ?? FindChunkInList(list, "STBL");
        var (sndhOffset, sndhSize) = FindChunkInList(list, "SNDH");
        if (stblOffset < 0 || sndhOffset < 0 || !emptyTemplate && sndhSize < 12 || fmtChunk.Length < 8)
            throw new InvalidDataException("Bank template is missing a valid FMT, STBL or SNDH chunk.");

        var sndhEnd = sndhOffset + 8 + sndhSize + (sndhSize & 1);
        if (emptyTemplate)
        {
            using var normalized = new MemoryStream();
            normalized.Write(list.AsSpan(0, sndhOffset));
            WriteChunk(normalized, "SNDH", [3, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            normalized.Write(list.AsSpan(sndhEnd));
            list = normalized.ToArray();
            (stblOffset, stblSize) = FindChunkInList(list, "STBL");
        }

        using var project = new MemoryStream();
        project.Write(list.AsSpan(0, stblOffset));
        WriteChunk(project, "STBL", stbl);
        project.Write(list.AsSpan(stblOffset + 8 + stblSize + (stblSize & 1)));
        var projectBytes = project.ToArray();
        var soundStart = 12L + fmtChunk.Length + 8 + projectBytes.Length + 8;
        var prefix = AlignDataLength(soundStart) - soundStart;
        var fsbStart = soundStart + prefix;
        var riffSize = checked(fsbStart + fsbSize);
        var fileSize = checked(riffSize + markerLength);
        if (fileSize > int.MaxValue)
        {
            throw new BankTooLargeException(
                $"Resulting bank is {fileSize / 1073741824.0:0.00} GB, over the 2 GB limit the game can load. " +
                "Use fewer tracks or place them in a different bank.");
        }

        (sndhOffset, _) = FindChunkInList(projectBytes, "SNDH");
        BinaryPrimitives.WriteUInt32LittleEndian(projectBytes.AsSpan(sndhOffset + 12), (uint) fsbStart);
        BinaryPrimitives.WriteUInt32LittleEndian(projectBytes.AsSpan(sndhOffset + 16), (uint) fsbSize);

        using var header = new MemoryStream();
        WriteTag(header, "RIFF");
        WriteU32(header, (uint) (riffSize - 8));
        WriteTag(header, "FEV ");
        header.Write(fmtChunk);
        WriteChunk(header, "LIST", projectBytes);
        WriteTag(header, "SND ");
        WriteU32(header, (uint) (prefix + fsbSize));
        WriteZeros(header, prefix);
        return header.ToArray();
    }

    public List<(ulong Id, int Index)> ReadStbl()
    {
        if (_stblOff == 0 || _stblPay <= 0 || _stblPay + 10 > _src.Length)
        {
            return [];
        }

        var sp = _stblPay;
        var count = U16(_src, sp + 8);

        if (sp + 10 + count * 8 > _src.Length)
        {
            return [];
        }

        var ids = new ulong[count];
        
        for (var i = 0; i < count; i++)
        {
            ids[i] = BinaryPrimitives.ReadUInt64LittleEndian(_src.AsSpan(sp + 10 + i * 8));
        }

        var q = sp + 10 + count * 8;
        
        if (q + 2 > _src.Length)
        {
            return [];
        }

        var count2 = U16(_src, q); q += 2;
        
        if (q + count2 * 3 > _src.Length)
        {
            return [];
        }

        var result = new List<(ulong, int)>(count2);
        
        for (var i = 0; i < count2 && i < count; i++)
        {
            var idx = _src[q] | _src[q + 1] << 8 | _src[q + 2] << 16; q += 3;
            result.Add((ids[i], idx));
        }

        return result;
    }
    
    private const int Fsb5HeaderSize = 60;

    public readonly record struct DataRef(string Path, long Offset, long Length);

    public sealed record BankSkeleton(byte[] FmtChunk, byte[] ListPayload, int StblRelOff, int StblOldSize,
        List<(ulong Id, int Index)> Stbl, byte[] Fsb5HeaderRegion, long DataStartAbs, int Mode, bool Empty);

    private static string Tag4(byte[] b, int o) => $"{(char)b[o]}{(char)b[o + 1]}{(char)b[o + 2]}{(char)b[o + 3]}";

    public static BankSkeleton ReadSkeleton(string path)
    {
        using var fs = File.OpenRead(path);
        return ReadSkeleton(fs);
    }

    internal static BankSkeleton ReadSkeleton(Stream fs)
    {
        var head = ReadExact(fs, 12);
        
        if (Tag4(head, 0) != "RIFF" || Tag4(head, 8) != "FEV ")
        {
            throw new InvalidDataException("not a RIFF/FEV bank");
        }

        byte[]? fmtChunk = null;
        byte[]? listPay = null;
        
        var sawSnd = false;
        var hdr = new byte[8];

        while (fs.Position + 8 <= fs.Length)
        {
            if (fs.Read(hdr, 0, 8) < 8) break;
            
            var id = Tag4(hdr, 0);
            var size = (int) BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(4));
            if (size < 0 || size > fs.Length - fs.Position)
            {
                throw new InvalidDataException($"RIFF chunk {id} has an invalid size {size}");
            }
            
            if (id == "FMT ")
            {
                fmtChunk = new byte[8 + size + (size & 1)];
                hdr.CopyTo(fmtChunk, 0);
                fs.ReadExactly(fmtChunk.AsSpan(8));
            }
            else if (id == "LIST") { listPay = ReadExact(fs, size); }
            else if (id == "SND ") { sawSnd = true; break; }
            else { fs.Seek(size, SeekOrigin.Current); }
            if (id != "FMT " && (size & 1) != 0) fs.Seek(1, SeekOrigin.Current);
        }

        if (listPay is null || fmtChunk is null)
        {
            return new BankSkeleton(fmtChunk ?? [], listPay ?? [], 0, 0, [], [], 0, 0, Empty: true);
        }

        var (stblRelOff, stblSize) = FindChunkInList(listPay, "STBL");
        var (sndhRelOff, _) = FindChunkInList(listPay, "SNDH");
        
        if (!sawSnd || stblRelOff < 0 || sndhRelOff < 0)
        {
            return new BankSkeleton(fmtChunk, listPay, stblRelOff, stblSize, [], [], 0, 0, Empty: true);
        }

        var (fsbOffset, fsbSize) = ParseSndh(listPay);
        var stbl = ParseStblPayload(listPay, stblRelOff + 8);
        var (region, dataStart) = ReadFsbRegion(fs, fsbOffset, fsbSize);
        var mode = (int) U32(region, 24);
        return new BankSkeleton(fmtChunk, listPay, stblRelOff, stblSize, stbl, region, dataStart, mode, Empty: false);
    }

    private static (byte[] HeaderRegion, long DataStart) ReadFsbRegion(Stream stream, long offset, long size)
    {
        if (offset <= 0 || size < Fsb5HeaderSize || offset + size > stream.Length)
            throw new InvalidDataException("SNDH points outside the bank.");

        stream.Position = offset;
        var header = ReadExact(stream, Fsb5HeaderSize);
        if (!header.AsSpan(0, 4).SequenceEqual("FSB5"u8))
            throw new InvalidDataException("SNDH does not point to an FSB5 block.");

        var headerSize = Fsb5HeaderSize + (long) U32(header, 12);
        var dataOffset = headerSize + U32(header, 16);
        if (dataOffset > 64 * 1024 * 1024 || dataOffset + U32(header, 20) > size)
            throw new InvalidDataException("FSB5 header or data region lies outside the bank.");

        var region = new byte[checked((int) headerSize)];
        header.CopyTo(region, 0);
        stream.ReadExactly(region.AsSpan(Fsb5HeaderSize));
        return (region, offset + dataOffset);
    }

    private static List<(ulong Id, int Index)> ParseStblPayload(byte[] list, int sp)
    {
        var result = new List<(ulong, int)>();
        if (sp + 10 > list.Length) return result;
        
        int count = BinaryPrimitives.ReadUInt16LittleEndian(list.AsSpan(sp + 8));
        if (sp + 10 + count * 8 > list.Length) return result;

        var ids = new ulong[count];
        for (var i = 0; i < count; i++) ids[i] = BinaryPrimitives.ReadUInt64LittleEndian(list.AsSpan(sp + 10 + i * 8));

        var q = sp + 10 + count * 8;
        if (q + 2 > list.Length) return result;
        
        int count2 = BinaryPrimitives.ReadUInt16LittleEndian(list.AsSpan(q)); q += 2;
        if (q + count2 * 3 > list.Length) return result;

        for (var i = 0; i < count2 && i < count; i++)
        {
            var idx = list[q] | list[q + 1] << 8 | list[q + 2] << 16; q += 3;
            result.Add((ids[i], idx));
        }

        return result;
    }

    public static void AssembleToFile(string outPath, byte[] fmtChunk, byte[] srcListPayload,
        int stblRelOff, int stblOldSize, byte[] newStblPayload, byte[] fsb5Header60,
        IReadOnlyList<byte[]> sampleHeaders, IReadOnlyList<DataRef> sampleData, byte[]? nameTable = null,
        bool emptyTemplate = false, CancellationToken ct = default)
    {
        if (sampleHeaders.Count != sampleData.Count)
        {
            throw new InvalidOperationException("headers/data count mismatch");
        }

        var headersLen = sampleHeaders.Aggregate(0L, (current, h) => current + h.Length);

        var offsets = new long[sampleData.Count];
        long dataLen = 0;
        for (var i = 0; i < sampleData.Count; i++)
        {
            offsets[i] = dataLen;
            dataLen = checked(dataLen + AlignDataLength(sampleData[i].Length));
        }

        var names = nameTable ?? [];
        var fsbSize = checked(Fsb5HeaderSize + headersLen + names.Length + dataLen);
        var bankHeader = BuildBankHeader(fmtChunk, srcListPayload, newStblPayload, fsbSize, emptyTemplate,
            ModMarker.Length, (stblRelOff, stblOldSize));
        var fsbHeader = (byte[]) fsb5Header60.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(fsbHeader.AsSpan(8), (uint) sampleHeaders.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(fsbHeader.AsSpan(12), (uint) headersLen);
        BinaryPrimitives.WriteUInt32LittleEndian(fsbHeader.AsSpan(16), (uint) names.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(fsbHeader.AsSpan(20), (uint) dataLen);

        using var f = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        f.Write(bankHeader);
        f.Write(fsbHeader);

        for (var i = 0; i < sampleHeaders.Count; i++)
        {
            var rb = Fsb5.RebaseHeader(sampleHeaders[i], offsets[i]);
            f.Write(rb, 0, rb.Length);
        }

        f.Write(names);
        var cache = new Dictionary<string, FileStream>();

        try
        {
            var buf = new byte[1 << 20];
            foreach (var dr in sampleData)
            {
                if (!cache.TryGetValue(dr.Path, out var src))
                {
                    src = File.OpenRead(dr.Path);
                    cache[dr.Path] = src;
                }

                src.Seek(dr.Offset, SeekOrigin.Begin);
                CopyExact(src, f, dr.Length, buf, ct);
                WriteZeros(f, AlignDataLength(dr.Length) - dr.Length);
            }
        }
        finally
        {
            foreach (var s in cache.Values)
            {
                s.Dispose();
            }
        }
        
        f.Write(ModMarker, 0, ModMarker.Length);
        f.Flush(flushToDisk: true);
    }
    
    private static long AlignDataLength(long length) => checked(length + (0x20 - length % 0x20) % 0x20);

    public static readonly byte[] ModMarker = "FH6RBANK"u8.ToArray();
    
    public static bool HasModMarker(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);

            if (fs.Length < ModMarker.Length) return false;
            fs.Seek(-ModMarker.Length, SeekOrigin.End);

            var buf = new byte[ModMarker.Length];
            var read = 0;

            while (read < buf.Length)
            {
                var k = fs.Read(buf, read, buf.Length - read);
                if (k <= 0) return false;
                read += k;
            }

            return buf.AsSpan().SequenceEqual(ModMarker);
        }
        catch
        {
            return false;
        }
    }

    public static bool LooksModified(string path)
    {
        if (HasModMarker(path))
        {
            return true;
        }

        try
        {
            var ids = ReadStblIdsFromFile(path);
            return Naming.ScanCustomTracks(ids).Count > 0;
        }
        catch
        {
            return true;
        }
    }

    private static (int TagOff, int PayloadSize) FindChunkInList(byte[] list, string id)
    {
        var p = 4;
        
        while (p + 8 <= list.Length)
        {
            var cid = Tag4(list, p);
            var size = (int) BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(p + 4));
            
            if (cid == id) return (p, size);
            p = p + 8 + size;
            
            if ((p & 1) != 0)
            {
                p++;
            }
        }
        
        return (-1, 0);
    }

    private static byte[] ReadExact(Stream s, int count)
    {
        if (count < 0)
        {
            throw new InvalidDataException($"invalid chunk size {count}");
        }

        if (s.CanSeek)
        {
            var remaining = s.Length - s.Position;
            if (count > remaining)
            {
                throw new InvalidDataException($"chunk size {count} exceeds remaining {remaining} bytes");
            }
        }

        var buf = new byte[count];
        var read = 0;
        
        while (read < count)
        {
            var n = s.Read(buf, read, count - read); 
            
            if (n <= 0)
            {
                throw new EndOfStreamException();
            }
            
            read += n;
        }
        
        return buf;
    }

    private static void CopyExact(Stream src, Stream dst, long length, byte[] buf, CancellationToken ct)
    {
        while (length > 0)
        {
            ct.ThrowIfCancellationRequested();
            var n = src.Read(buf, 0, (int) Math.Min(length, buf.Length));
            
            if (n <= 0)
            {
                throw new EndOfStreamException("source data truncated");
            }
            
            dst.Write(buf, 0, n);
            length -= n;
        }
    }

    private static void WriteTag(Stream s, string tag)
    {
        var b = new byte[4];
        
        for (var i = 0; i < 4; i++)
        {
            b[i] = (byte)tag[i];
        }
        
        s.Write(b, 0, 4);
    }

    private static void WriteU32(Stream s, uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        s.Write(b, 0, 4);
    }

    private static void WriteZeros(Stream s, long count)
    {
        if (count <= 0) return;
        var z = new byte[64];
        while (count > 0)
        {
            var n = (int) Math.Min(count, z.Length); 
            s.Write(z, 0, n); 
            count -= n;
        }
    }
}

public sealed class BankTooLargeException : Exception
{
    public BankTooLargeException(string message) : base(message) { }
}
