namespace FH6RB.Core;

public static class Lookup
{
    private static uint Rot(uint x, int k) => (x << k) | (x >> (32 - k));

    private static void HashLittle2(ReadOnlySpan<byte> data, ref uint pc, ref uint pb)
    {
        var length = (uint)data.Length;
        uint a, b, c;
        a = b = c = 0xdeadbeef + length + pc;
        c += pb;

        var pos = 0;
        var rem = length;
        
        while (rem > 12)
        {
            a += U32(data, pos);
            b += U32(data, pos + 4);
            c += U32(data, pos + 8);
            a -= c; 
            a ^= Rot(c, 4);  
            c += b;
            b -= a; 
            b ^= Rot(a, 6);  
            a += c;
            c -= b; 
            c ^= Rot(b, 8);  
            b += a;
            a -= c; 
            a ^= Rot(c, 16); 
            c += b;
            b -= a; 
            b ^= Rot(a, 19); 
            a += c;
            c -= b; 
            c ^= Rot(b, 4);  
            b += a;
            pos += 12; 
            rem -= 12;
        }
        
        switch (rem)
        {
            case 12: 
                c += (uint) data[pos + 8] | (uint) data[pos + 9] << 8 | (uint) data[pos + 10] << 16 | (uint) data[pos + 11] << 24;
                b += U32(data, pos + 4); 
                a += U32(data, pos); 
                break;
            case 11: 
                c += (uint) data[pos + 8] | (uint) data[pos + 9] << 8 | (uint) data[pos + 10] << 16;
                b += U32(data, pos + 4); 
                a += U32(data, pos); 
                break;
            case 10: 
                c += (uint) data[pos + 8] | (uint) data[pos + 9] << 8;
                b += U32(data, pos + 4); 
                a += U32(data, pos); 
                break;
            case 9:  
                c += data[pos + 8]; 
                b += U32(data, pos + 4); 
                a += U32(data, pos); 
                break;
            case 8:  
                b += U32(data, pos + 4); 
                a += U32(data, pos); 
                break;
            case 7:  
                b += (uint) data[pos + 4] | (uint) data[pos + 5] << 8 | (uint) data[pos + 6] << 16; 
                a += U32(data, pos); 
                break;
            case 6:  
                b += (uint) data[pos + 4] | (uint) data[pos + 5] << 8; 
                a += U32(data, pos); 
                break;
            case 5:  
                b += data[pos + 4]; 
                a += U32(data, pos); 
                break;
            case 4:  
                a += U32(data, pos); 
                break;
            case 3:  
                a += (uint) data[pos] | (uint) data[pos + 1] << 8 | (uint) data[pos + 2] << 16; 
                break;
            case 2:  
                a += (uint) data[pos] | (uint) data[pos + 1] << 8; 
                break;
            case 1:  
                a += data[pos]; 
                break;
            case 0:  
                pc = c; pb = b; 
                return;
        }
        
        c ^= b; 
        c -= Rot(b, 14);
        a ^= c; 
        a -= Rot(c, 11);
        b ^= a; 
        b -= Rot(a, 25);
        c ^= b; 
        c -= Rot(b, 16);
        a ^= c; 
        a -= Rot(c, 4);
        b ^= a; 
        b -= Rot(a, 14);
        c ^= b; 
        c -= Rot(b, 24);

        pc = c; 
        pb = b;
    }
    
    public static ulong SoundNameToId(string soundName)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(soundName);
        uint pc = 0, pb = 0;
        HashLittle2(bytes, ref pc, ref pb);
        return ((ulong) pb << 32) | pc;
    }

    private static uint U32(ReadOnlySpan<byte> d, int o) => (uint) d[o] | (uint) d[o + 1] << 8 | (uint) d[o + 2] << 16 | (uint) d[o + 3] << 24;
}
