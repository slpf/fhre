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
        if (Cc(_src, 0) != "RIFF" || Cc(_src, 8) != "FEV ")
        {
            throw new InvalidDataException("not a RIFF/FEV bank");
        }
        
        var pos = 12;
        
        while (pos + 8 <= _src.Length)
        {
            var id = Cc(_src, pos);
            var size = (int) U32(_src, pos + 4);
            var pay = pos + 8;
            
            switch (id)
            {
                case "FMT ": _fmtOff = pos; _fmtPay = pay; _fmtSize = size; break;
                case "LIST": _listOff = pos; _listPay = pay; _listSize = size; break;
                case "SND ": _sndOff = pos; _sndPay = pay; _sndSize = size; break;
            }
            
            if (id == "SND ") break;
            
            pos = pay + size;
        }
        
        var p = _listPay + 4;
        var end = _listPay + _listSize;
        
        while (p + 8 <= end)
        {
            var id = Cc(_src, p);
            var size = (int) U32(_src, p + 4);
            var pay = p + 8;
            
            switch (id)
            {
                case "STBL":
                    _stblOff = p; 
                    _stblPay = pay; 
                    _stblSize = size;
                    break;
                case "SNDH":
                    _sndhPay = pay;
                    break;
            }

            p = pay + size;
            if ((p & 1) != 0) p++;
        }

        _fsbOff  = (int) U32(_src, _sndhPay + 4);
        _fsbSize = (int) U32(_src, _sndhPay + 8);
    }

    private byte[] FmtChunk() => _src[_fmtOff..(_fmtPay + _fmtSize)];
    private byte[] ListPayloadBeforeStbl() => _src[_listPay.._stblOff];

    private byte[] ListPayloadAfterStbl()
    {
        var e = _stblPay + _stblSize;
        if ((e & 1) != 0) e++;
        return _src[e..(_listPay + _listSize)];
    }
    
    // public byte[] SourceBank => _src;
    // public int Fsb5Offset => _fsbOff;
    // public int Fsb5Size => _fsbSize;
    public int Fsb5Mode => _fsbOff > 0 && _fsbOff + 0x1C <= _src.Length ? (int) U32(_src, _fsbOff + 0x18) : 0;
    public byte[] ExtractFsb5() => _src[_fsbOff..(_fsbOff + _fsbSize)];
    
    public static HashSet<ulong> ReadStblIdsFromFile(string path)
    {
        var ids = new HashSet<ulong>();

        try
        {
            using var fs = File.OpenRead(path);
            
            if (fs.Length < 12)
            {
                return ids;
            }

            var head = new byte[12];
            
            if (fs.Read(head, 0, 12) < 12 || Cc(head, 0) != "RIFF" || Cc(head, 8) != "FEV ")
            {
                return ids;
            }

            var hdr = new byte[8];
            
            while (fs.Position + 8 <= fs.Length)
            {
                if (fs.Read(hdr, 0, 8) < 8)
                {
                    break;
                }

                var id = Cc(hdr, 0);
                var size = (int) U32(hdr, 4);

                if (id == "LIST")
                {
                    var list = new byte[size];
                    
                    if (fs.Read(list, 0, size) == size)
                    {
                        ParseStblIds(list, ids);
                    }

                    return ids;
                }

                if (id == "SND ")
                {
                    break;
                }

                fs.Seek(size, SeekOrigin.Current);
            }
        }
        catch
        {
            // ignored
        }

        return ids;
    }
    
    private static void ParseStblIds(byte[] list, HashSet<ulong> ids)
    {
        var p = 4;
        
        while (p + 8 <= list.Length)
        {
            var id = Cc(list, p);
            var size = (int) U32(list, p + 4);
            var pay = p + 8;

            if (id == "STBL")
            {
                if (size < 10 || pay + 10 > list.Length) return;
                
                int count = U16(list, pay + 8);
                var idp = pay + 10;
                
                for (var i = 0; i < count && idp + 8 <= list.Length; i++, idp += 8)
                {
                    ids.Add(BinaryPrimitives.ReadUInt64LittleEndian(list.AsSpan(idp)));
                }

                return;
            }

            p = pay + size;
            
            if ((p & 1) != 0)
            {
                p++;
            }
        }
    }
    
    public sealed record BankTrack(ulong Id, int Index, long Frames, int SampleRate);
    
    public static List<BankTrack> ReadTrackInfoFromFile(string path)
    {
        var result = new List<BankTrack>();
        using var fs = File.OpenRead(path);
        
        if (fs.Length < 12)
        {
            return result;
        }

        var head = new byte[12];
        
        if (fs.Read(head, 0, 12) < 12 || Cc(head, 0) != "RIFF" || Cc(head, 8) != "FEV ")
        {
            return result;
        }

        byte[]? list = null;
        var hdr = new byte[8];
        
        while (fs.Position + 8 <= fs.Length)
        {
            if (fs.Read(hdr, 0, 8) < 8)
            {
                break;
            }

            var id = Cc(hdr, 0);
            var size = (int) U32(hdr, 4);

            if (id == "LIST")
            {
                list = new byte[size];
                if (fs.Read(list, 0, size) != size)
                {
                    return result;
                }

                break;
            }

            if (id == "SND ")
            {
                break;
            }

            fs.Seek(size, SeekOrigin.Current);
        }

        if (list is null)
        {
            return result;
        }

        var stbl = ParseStblEntries(list);
        var (fsbOff, _) = ParseSndh(list);
        
        (long Frames, int Rate)[] samp = [];
        
        if (fsbOff > 0 && fsbOff + 60 <= fs.Length)
        {
            fs.Seek(fsbOff, SeekOrigin.Begin);
            
            var fh = new byte[60];
            
            if (fs.Read(fh, 0, 60) == 60 && Cc(fh, 0) == "FSB5")
            {
                var shSize = (int) U32(fh, 12);
                var nameSize = (int) U32(fh, 16);
                var need = 60L + shSize + nameSize;
                
                if (need > 60 && fsbOff + need <= fs.Length && need < 64 * 1024 * 1024)
                {
                    var buf = new byte[need];
                    
                    fs.Seek(fsbOff, SeekOrigin.Begin);
                    
                    if (fs.Read(buf, 0, (int) need) == need)
                    {
                        samp = Fsb5.ReadSampleInfo(buf);
                    }
                }
            }
        }

        foreach (var (sid, idx) in stbl)
        {
            var has = idx >= 0 && idx < samp.Length;
            
            result.Add(new BankTrack(sid, idx, has ? samp[idx].Frames : 0, has ? samp[idx].Rate : 0));
        }

        return result;
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

            if (id == "SNDH" && pay + 12 <= list.Length)
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

    public byte[] FillEmpty(byte[] stblPayload, byte[] newFsb5)
    {
        var proj = new MemoryStream();
        
        proj.Write(_src, _listPay, 4);

        var p = _listPay + 4;
        var end = _listPay + _listSize;

        while (p + 8 <= end)
        {
            var id = Cc(_src, p);
            var size = (int) U32(_src, p + 4);
            var pay = p + 8;

            switch (id)
            {
                case "STBL":
                    WriteChunk(proj, "STBL", stblPayload);
                    break;
                case "SNDH":
                {
                    var sndh = new byte[12];
                    BinaryPrimitives.WriteUInt16LittleEndian(sndh.AsSpan(0), 3);
                    BinaryPrimitives.WriteUInt16LittleEndian(sndh.AsSpan(2), 8);
                    BinaryPrimitives.WriteUInt32LittleEndian(sndh.AsSpan(8), (uint) newFsb5.Length);
                    WriteChunk(proj, "SNDH", sndh);
                    break;
                }
                default:
                {
                    var chunkLen = pay + size - p;
                    proj.Write(_src, p, chunkLen);
                    if ((chunkLen & 1) != 0) proj.WriteByte(0);
                    break;
                }
            }

            p = pay + size;
            
            if ((p & 1) != 0)
            {
                p++;
            }
        }

        var projBytes = proj.ToArray();
        var sndPayloadStart = 12 + FmtChunk().Length + 8 + projBytes.Length + 8;
        var sndPrefix = AlignPrefix(sndPayloadStart);
        var sndPayload = new byte[sndPrefix + newFsb5.Length];
        
        Buffer.BlockCopy(newFsb5, 0, sndPayload, sndPrefix, newFsb5.Length);

        var body = new MemoryStream();
        var bw = new BinaryWriter(body);
        
        bw.Write(FmtChunk());
        bw.Write(System.Text.Encoding.ASCII.GetBytes("LIST"));
        bw.Write((uint) projBytes.Length);
        bw.Write(projBytes);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("SND "));
        bw.Write((uint) sndPayload.Length);
        bw.Write(sndPayload);

        var outMs = new MemoryStream();
        var ow = new BinaryWriter(outMs);
        
        ow.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        ow.Write((uint) (body.Length + 4));
        ow.Write(System.Text.Encoding.ASCII.GetBytes("FEV "));
        ow.Write(body.ToArray());

        var outBytes = outMs.ToArray();
        var fixedBank = new FevBank(outBytes);
        var fsbAbs = fixedBank._sndPay + sndPrefix;
        
        BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(fixedBank._sndhPay + 4), (uint) fsbAbs);
        BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(fixedBank._sndhPay + 8), (uint) newFsb5.Length);

        return outBytes;
    }

    private static void WriteChunk(Stream s, string id, byte[] payload)
    {
        s.Write(System.Text.Encoding.ASCII.GetBytes(id));
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(len, (uint) payload.Length);
        s.Write(len);
        s.Write(payload);
        
        if ((payload.Length & 1) != 0)
        {
            s.WriteByte(0);
        }
    }

    private static int AlignPrefix(int sndPayloadStart) => (0x20 - sndPayloadStart % 0x20) % 0x20;

    public byte[] WithStblAndFsb(byte[] stblPayload, byte[] newFsb5)
    {
        var stblPad = stblPayload.Length & 1;
        var stblChunkLen = 8 + stblPayload.Length + stblPad;
        var before = ListPayloadBeforeStbl();
        var after = ListPayloadAfterStbl();
        var listLen = before.Length + stblChunkLen + after.Length;
        var fmt = FmtChunk();
        var sndPayloadStart = 12 + fmt.Length + 8 + listLen + 8;
        var prefix = AlignPrefix(sndPayloadStart);
        var sndPayloadLen = prefix + newFsb5.Length;
        var bodyLen = fmt.Length + 8 + listLen + 8 + sndPayloadLen;
        var outBytes = new byte[12 + bodyLen];
        var pos = 0;
        
        void Tag(string s) { foreach (var ch in s) outBytes[pos++] = (byte) ch; }
        void U32w(long v) { BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(pos), (uint) v); pos += 4; }
        void Bytes(byte[] src) { src.CopyTo(outBytes.AsSpan(pos)); pos += src.Length; }

        Tag("RIFF"); 
        U32w(bodyLen + 4); 
        Tag("FEV ");
        Bytes(fmt);
        Tag("LIST"); 
        U32w(listLen);
        Bytes(before);
        Tag("STBL"); 
        U32w(stblPayload.Length); 
        Bytes(stblPayload); pos += stblPad;
        Bytes(after);
        Tag("SND "); 
        U32w(sndPayloadLen);
        
        pos += prefix;
        newFsb5.CopyTo(outBytes.AsSpan(pos));
        pos += newFsb5.Length;

        var fixedBank = new FevBank(outBytes);
        var newFsbOff = fixedBank._sndPay + prefix;

        BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(fixedBank._sndhPay + 4), (uint) newFsbOff);
        BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(fixedBank._sndhPay + 8), (uint) newFsb5.Length);

        return outBytes;
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
            
            if (id == "FMT ")
            {
                var pay = ReadExact(fs, size);
                fmtChunk = new byte[8 + size];
                Array.Copy(hdr, 0, fmtChunk, 0, 8);
                Array.Copy(pay, 0, fmtChunk, 8, size);
            }
            else if (id == "LIST") { listPay = ReadExact(fs, size); }
            else if (id == "SND ") { sawSnd = true; break; }
            else { fs.Seek(size, SeekOrigin.Current); }
        }

        if (!sawSnd || listPay is null || fmtChunk is null)
        {
            return new BankSkeleton(fmtChunk ?? [], listPay ?? [], 0, 0, [], [], 0, 0, Empty: true);
        }

        var (stblRelOff, stblSize) = FindChunkInList(listPay, "STBL");
        var (sndhRelOff, _) = FindChunkInList(listPay, "SNDH");
        
        if (stblRelOff < 0 || sndhRelOff < 0)
        {
            return new BankSkeleton(fmtChunk, listPay, 0, 0, [], [], 0, 0, Empty: true);
        }

        long fsbOff = BinaryPrimitives.ReadUInt32LittleEndian(listPay.AsSpan(sndhRelOff + 8 + 4));
        var stbl = ParseStblPayload(listPay, stblRelOff + 8);

        fs.Seek(fsbOff, SeekOrigin.Begin);
        
        var h60 = ReadExact(fs, Fsb5HeaderSize);
        var shs = (int) BinaryPrimitives.ReadUInt32LittleEndian(h60.AsSpan(12));
        var ns = (int) BinaryPrimitives.ReadUInt32LittleEndian(h60.AsSpan(16));
        var mode = (int) BinaryPrimitives.ReadUInt32LittleEndian(h60.AsSpan(0x18));

        fs.Seek(fsbOff, SeekOrigin.Begin);
        
        var region = ReadExact(fs, Fsb5HeaderSize + shs);
        var dataStartAbs = fsbOff + Fsb5HeaderSize + shs + ns;

        return new BankSkeleton(fmtChunk, listPay, stblRelOff, stblSize, stbl, region, dataStartAbs, mode, Empty: false);
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
        IReadOnlyList<byte[]> sampleHeaders, IReadOnlyList<DataRef> sampleData)
    {
        if (sampleHeaders.Count != sampleData.Count)
        {
            throw new InvalidOperationException("headers/data count mismatch");
        }

        var headersLen = sampleHeaders.Aggregate(0L, (current, h) => current + h.Length);

        var offs = new long[sampleData.Count];
        long dataLen = 0;
        
        for (var i = 0; i < sampleData.Count; i++)
        {
            offs[i] = dataLen;
            var l = sampleData[i].Length;
            dataLen += l + (0x20 - (l % 0x20)) % 0x20;
        }

        var fsbSize = Fsb5HeaderSize + headersLen + dataLen;
        var h60 = (byte[]) fsb5Header60.Clone();
        
        BinaryPrimitives.WriteUInt32LittleEndian(h60.AsSpan(8),  (uint) sampleHeaders.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(h60.AsSpan(12), (uint) headersLen);
        BinaryPrimitives.WriteUInt32LittleEndian(h60.AsSpan(16), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(h60.AsSpan(20), (uint) dataLen);

        var before = srcListPayload[0..stblRelOff];
        var e = stblRelOff + 8 + stblOldSize;
        
        if ((e & 1) != 0) e++;
        
        var after = srcListPayload[e..];
        var stblPad = newStblPayload.Length & 1;

        using var lm = new MemoryStream();
        lm.Write(before, 0, before.Length);
        WriteTag(lm, "STBL"); 
        WriteU32(lm, (uint) newStblPayload.Length);
        lm.Write(newStblPayload, 0, newStblPayload.Length);
        if (stblPad != 0) lm.WriteByte(0);
        lm.Write(after, 0, after.Length);
        
        var newList = lm.ToArray();
        var listLen = newList.Length;

        var sndStart = 12 + fmtChunk.Length + 8 + listLen + 8;
        var prefix = (0x20 - (sndStart % 0x20)) % 0x20;
        var fsbAbs = (long) sndStart + prefix;
        var (sndhRel, _) = FindChunkInList(newList, "SNDH");
        
        BinaryPrimitives.WriteUInt32LittleEndian(newList.AsSpan(sndhRel + 8 + 4), (uint) fsbAbs);
        BinaryPrimitives.WriteUInt32LittleEndian(newList.AsSpan(sndhRel + 8 + 8), (uint) fsbSize);

        var sndPayloadLen = prefix + fsbSize;
        var bodyLen = fmtChunk.Length + 8 + listLen + 8 + sndPayloadLen;
        
        var totalSize = 12 + bodyLen;
        
        if (totalSize > int.MaxValue)
        {
            throw new BankTooLargeException(
                $"Resulting bank is {totalSize / 1073741824.0:0.00} GB, over the 2 GB limit the game can load. " +
                "Use fewer tracks or place them in a different bank.");
        }

        using var f = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        WriteTag(f, "RIFF"); 
        WriteU32(f, (uint)(bodyLen + 4)); 
        WriteTag(f, "FEV ");
        f.Write(fmtChunk, 0, fmtChunk.Length);
        WriteTag(f, "LIST"); 
        WriteU32(f, (uint) listLen); 
        f.Write(newList, 0, newList.Length);
        WriteTag(f, "SND "); 
        WriteU32(f, (uint) sndPayloadLen); 
        WriteZeros(f, prefix);
        f.Write(h60, 0, h60.Length);
        
        for (var i = 0; i < sampleHeaders.Count; i++)
        {
            var rb = Fsb5.RebaseHeader(sampleHeaders[i], offs[i]);
            f.Write(rb, 0, rb.Length);
        }

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
                CopyExact(src, f, dr.Length, buf);
                WriteZeros(f, (0x20 - (dr.Length % 0x20)) % 0x20);
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
    }
    
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

    private static void CopyExact(Stream src, Stream dst, long length, byte[] buf)
    {
        while (length > 0)
        {
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
