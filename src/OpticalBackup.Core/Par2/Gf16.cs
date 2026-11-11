using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace OpticalBackup.Core.Par2;

/// <summary>
/// Arithmetic in GF(2^16) with the PAR2 generator polynomial 0x1100B, plus the
/// "multiply a buffer by a constant and XOR it into another buffer" kernel that
/// Reed-Solomon encoding spends all of its time in.
/// </summary>
internal static class Gf16
{
    private const int Polynomial = 0x1100B;
    public const int Order = 65535;

    private static readonly ushort[] LogTable = new ushort[65536];
    private static readonly ushort[] ExpTable = new ushort[65536];

    static Gf16()
    {
        int b = 1;
        for (int l = 0; l < Order; l++)
        {
            LogTable[b] = (ushort)l;
            ExpTable[l] = (ushort)b;
            b <<= 1;
            if ((b & 0x10000) != 0) b ^= Polynomial;
        }
        ExpTable[Order] = ExpTable[0];
    }

    public static ushort Exp(int log) => ExpTable[log % Order];

    public static ushort Mul(ushort a, ushort b)
    {
        if (a == 0 || b == 0) return 0;
        return ExpTable[(LogTable[a] + LogTable[b]) % Order];
    }

    /// <summary>
    /// The log (base 2) of the constant PAR2 assigns to each input slice: the n-th
    /// integer that is coprime to 65535, i.e. not divisible by 3, 5, 17 or 257.
    /// </summary>
    public static int[] SliceBaseLogs(int count)
    {
        var result = new int[count];
        int n = 0;
        for (int i = 0; i < count; i++)
        {
            do { n++; } while (n % 3 == 0 || n % 5 == 0 || n % 17 == 0 || n % 257 == 0);
            if (n >= Order) throw new InvalidOperationException("Too many input slices for PAR2.");
            result[i] = n;
        }
        return result;
    }

    /// <summary>Coefficient of an input slice in the recovery slice with the given exponent.</summary>
    public static ushort Coefficient(int sliceBaseLog, int exponent)
        => ExpTable[(int)((long)sliceBaseLog * exponent % Order)];

    /// <summary>dst ^= src * c, treating both buffers as little-endian 16-bit words.</summary>
    public static void MulAdd(Span<byte> dst, ReadOnlySpan<byte> src, ushort c)
    {
        if (dst.Length != src.Length || (src.Length & 1) != 0)
            throw new ArgumentException("Buffers must have the same even length.");
        if (c == 0) return;

        // Products of c with each nibble position of a 16-bit word.
        Span<ushort> nib = stackalloc ushort[64];
        for (int k = 0; k < 4; k++)
            for (int n = 0; n < 16; n++)
                nib[k * 16 + n] = Mul(c, (ushort)(n << (4 * k)));

        int done = 0;
        if (Avx2.IsSupported && src.Length >= 32) done = MulAddAvx2(dst, src, nib);
        else if (Ssse3.IsSupported && src.Length >= 16) done = MulAddSsse3(dst, src, nib);
        if (done < src.Length) MulAddScalar(dst[done..], src[done..], nib);
    }

    internal static void MulAddScalar(Span<byte> dst, ReadOnlySpan<byte> src, ReadOnlySpan<ushort> nib)
    {
        Span<ushort> lo = stackalloc ushort[256];
        Span<ushort> hi = stackalloc ushort[256];
        for (int b = 0; b < 256; b++)
        {
            lo[b] = (ushort)(nib[b & 15] ^ nib[16 + (b >> 4)]);
            hi[b] = (ushort)(nib[32 + (b & 15)] ^ nib[48 + (b >> 4)]);
        }
        for (int i = 0; i + 1 < src.Length; i += 2)
        {
            ushort r = (ushort)(lo[src[i]] ^ hi[src[i + 1]]);
            dst[i] ^= (byte)r;
            dst[i + 1] ^= (byte)(r >> 8);
        }
    }

    private static void SplitTables(ReadOnlySpan<ushort> nib, Span<byte> tables)
    {
        // tables: 8 x 16 bytes. [k*2] = low bytes of nibble table k, [k*2+1] = high bytes.
        for (int k = 0; k < 4; k++)
            for (int n = 0; n < 16; n++)
            {
                ushort v = nib[k * 16 + n];
                tables[(k * 2) * 16 + n] = (byte)v;
                tables[(k * 2 + 1) * 16 + n] = (byte)(v >> 8);
            }
    }

    private static int MulAddAvx2(Span<byte> dst, ReadOnlySpan<byte> src, ReadOnlySpan<ushort> nib)
    {
        Span<byte> t = stackalloc byte[128];
        SplitTables(nib, t);
        var tl = new Vector256<byte>[4];
        var th = new Vector256<byte>[4];
        for (int k = 0; k < 4; k++)
        {
            var l = Vector128.Create<byte>(t.Slice(k * 32, 16));
            var h = Vector128.Create<byte>(t.Slice(k * 32 + 16, 16));
            tl[k] = Vector256.Create(l, l);
            th[k] = Vector256.Create(h, h);
        }
        var mask = Vector256.Create((ushort)0x000F);
        int n256 = src.Length & ~31;
        ref byte s0 = ref MemoryMarshal.GetReference(src);
        ref byte d0 = ref MemoryMarshal.GetReference(dst);
        for (int i = 0; i < n256; i += 32)
        {
            var v = Vector256.LoadUnsafe(ref s0, (nuint)i).AsUInt16();
            // Each index vector holds one nibble in the low byte of every word and 0 in the
            // high byte; table entry 0 is always 0, so the high-byte lookups vanish.
            var n0 = Avx2.And(v, mask).AsByte();
            var n1 = Avx2.And(Avx2.ShiftRightLogical(v, 4), mask).AsByte();
            var n2 = Avx2.And(Avx2.ShiftRightLogical(v, 8), mask).AsByte();
            var n3 = Avx2.ShiftRightLogical(v, 12).AsByte();
            var lo = Avx2.Xor(Avx2.Xor(Avx2.Shuffle(tl[0], n0), Avx2.Shuffle(tl[1], n1)),
                              Avx2.Xor(Avx2.Shuffle(tl[2], n2), Avx2.Shuffle(tl[3], n3)));
            var hi = Avx2.Xor(Avx2.Xor(Avx2.Shuffle(th[0], n0), Avx2.Shuffle(th[1], n1)),
                              Avx2.Xor(Avx2.Shuffle(th[2], n2), Avx2.Shuffle(th[3], n3)));
            var product = Avx2.Xor(lo.AsUInt16(), Avx2.ShiftLeftLogical(hi.AsUInt16(), 8)).AsByte();
            var d = Vector256.LoadUnsafe(ref d0, (nuint)i);
            Avx2.Xor(d, product).StoreUnsafe(ref d0, (nuint)i);
        }
        return n256;
    }

    private static int MulAddSsse3(Span<byte> dst, ReadOnlySpan<byte> src, ReadOnlySpan<ushort> nib)
    {
        Span<byte> t = stackalloc byte[128];
        SplitTables(nib, t);
        var tl = new Vector128<byte>[4];
        var th = new Vector128<byte>[4];
        for (int k = 0; k < 4; k++)
        {
            tl[k] = Vector128.Create<byte>(t.Slice(k * 32, 16));
            th[k] = Vector128.Create<byte>(t.Slice(k * 32 + 16, 16));
        }
        var mask = Vector128.Create((ushort)0x000F);
        int n128 = src.Length & ~15;
        ref byte s0 = ref MemoryMarshal.GetReference(src);
        ref byte d0 = ref MemoryMarshal.GetReference(dst);
        for (int i = 0; i < n128; i += 16)
        {
            var v = Vector128.LoadUnsafe(ref s0, (nuint)i).AsUInt16();
            var n0 = Sse2.And(v, mask).AsByte();
            var n1 = Sse2.And(Sse2.ShiftRightLogical(v, 4), mask).AsByte();
            var n2 = Sse2.And(Sse2.ShiftRightLogical(v, 8), mask).AsByte();
            var n3 = Sse2.ShiftRightLogical(v, 12).AsByte();
            var lo = Sse2.Xor(Sse2.Xor(Ssse3.Shuffle(tl[0], n0), Ssse3.Shuffle(tl[1], n1)),
                              Sse2.Xor(Ssse3.Shuffle(tl[2], n2), Ssse3.Shuffle(tl[3], n3)));
            var hi = Sse2.Xor(Sse2.Xor(Ssse3.Shuffle(th[0], n0), Ssse3.Shuffle(th[1], n1)),
                              Sse2.Xor(Ssse3.Shuffle(th[2], n2), Ssse3.Shuffle(th[3], n3)));
            var product = Sse2.Xor(lo.AsUInt16(), Sse2.ShiftLeftLogical(hi.AsUInt16(), 8)).AsByte();
            var d = Vector128.LoadUnsafe(ref d0, (nuint)i);
            Sse2.Xor(d, product).StoreUnsafe(ref d0, (nuint)i);
        }
        return n128;
    }
}
