using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;

namespace OpticalBackup.Core.Par2;

/// <summary>
/// Creates standard PAR2 (Parity Volume Set 2.0) files that par2cmdline, MultiPar and
/// QuickPar-compatible tools can use to verify and repair the protected files.
/// </summary>
public static class Par2Writer
{
    private static readonly byte[] Magic = "PAR2\0PKT"u8.ToArray();
    private static readonly byte[] TypeMain = "PAR 2.0\0Main\0\0\0\0"u8.ToArray();
    private static readonly byte[] TypeFileDesc = "PAR 2.0\0FileDesc"u8.ToArray();
    private static readonly byte[] TypeIfsc = "PAR 2.0\0IFSC\0\0\0\0"u8.ToArray();
    private static readonly byte[] TypeRecovery = "PAR 2.0\0RecvSlic"u8.ToArray();
    private static readonly byte[] TypeCreator = "PAR 2.0\0Creator\0"u8.ToArray();

    public const long DefaultMemoryLimit = 384L * 1024 * 1024;

    private sealed class FileState
    {
        public required Par2SourceFile File;
        public byte[] NameBytes = Array.Empty<byte>();
        public byte[] Id = Array.Empty<byte>();
        public byte[] Md5First16K = Array.Empty<byte>();
        public byte[] Md5Full = Array.Empty<byte>();
        public int FirstSlice;
        public int Slices;
        public byte[] SliceChecksums = Array.Empty<byte>(); // 20 bytes per slice
    }

    /// <summary>Number of times the source files are read for a set under a memory limit.</summary>
    public static int PassCount(Par2Set set, long memoryLimit = DefaultMemoryLimit)
    {
        int batch = BatchSize(set, memoryLimit);
        return (set.RecoveryBlocks + batch - 1) / batch;
    }

    private static int BatchSize(Par2Set set, long memoryLimit)
        => (int)Math.Clamp(memoryLimit / set.BlockSize, 1, set.RecoveryBlocks);

    /// <summary>
    /// Reads every file in the set and writes the .par2 file to <paramref name="output"/>.
    /// <paramref name="onProgress"/> receives byte deltas that add up to the set's SourceBytes.
    /// </summary>
    public static void Write(Par2Set set, Stream output, Action<long>? onProgress = null,
        CancellationToken ct = default, long memoryLimit = DefaultMemoryLimit)
    {
        if (set.Files.Count == 0) throw new ArgumentException("Recovery set has no files.");
        if (set.Files.Any(f => f.Size <= 0 || f.Open is null))
            throw new ArgumentException("Recovery set files must be non-empty and readable.");
        int blockSize = set.BlockSize;

        // --- Pass over the first 16 KiB of each file to derive the PAR2 file IDs. ---
        var states = new List<FileState>(set.Files.Count);
        var head = new byte[16384];
        foreach (var f in set.Files)
        {
            ct.ThrowIfCancellationRequested();
            using var s = f.Open!();
            int n = ReadFully(s, head);
            if (n < Math.Min(f.Size, head.Length))
                throw new SourceFileChangedException(f.Name, "file is shorter than expected");
            var st = new FileState { File = f, NameBytes = Encoding.UTF8.GetBytes(f.Name) };
            st.Md5First16K = MD5.HashData(head.AsSpan(0, n));
            var idInput = new byte[24 + st.NameBytes.Length];
            st.Md5First16K.CopyTo(idInput, 0);
            BinaryPrimitives.WriteUInt64LittleEndian(idInput.AsSpan(16), (ulong)f.Size);
            st.NameBytes.CopyTo(idInput, 24);
            st.Id = MD5.HashData(idInput);
            st.Slices = Par2Set.SlicesOf(f.Size, blockSize);
            st.SliceChecksums = new byte[20 * st.Slices];
            states.Add(st);
        }

        // PAR2 orders files by ID, compared as 128-bit little-endian integers.
        states.Sort((a, b) => CompareIds(a.Id, b.Id));
        for (int i = 1; i < states.Count; i++)
            if (CompareIds(states[i - 1].Id, states[i].Id) == 0)
                throw new InvalidOperationException("Two files in a recovery set have identical PAR2 IDs.");
        int totalSlices = 0;
        foreach (var st in states) { st.FirstSlice = totalSlices; totalSlices += st.Slices; }
        if (totalSlices > 32768) throw new InvalidOperationException("Too many slices in recovery set.");
        int[] baseLogs = Gf16.SliceBaseLogs(totalSlices);

        // --- Main packet and set ID. ---
        var mainBody = new byte[12 + 16 * states.Count];
        BinaryPrimitives.WriteUInt64LittleEndian(mainBody, (ulong)blockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(mainBody.AsSpan(8), (uint)states.Count);
        for (int i = 0; i < states.Count; i++) states[i].Id.CopyTo(mainBody, 12 + 16 * i);
        byte[] setId = MD5.HashData(mainBody);

        // --- Reed-Solomon encoding, in as many passes as the memory limit requires. ---
        int batchSize = BatchSize(set, memoryLimit);
        int passes = (set.RecoveryBlocks + batchSize - 1) / batchSize;
        var recovery = new byte[batchSize][];
        for (int i = 0; i < batchSize; i++) recovery[i] = new byte[blockSize];
        var slice = new byte[blockSize];
        byte[]? critical = null;

        for (int pass = 0; pass < passes; pass++)
        {
            int r0 = pass * batchSize;
            int count = Math.Min(batchSize, set.RecoveryBlocks - r0);
            for (int i = 0; i < count; i++) Array.Clear(recovery[i]);
            bool hashing = pass == 0;

            foreach (var st in states)
            {
                using var s = st.File.Open!();
                using var md5Full = hashing ? IncrementalHash.CreateHash(HashAlgorithmName.MD5) : null;
                using var sha = hashing && st.File.ExpectedSha256 != null
                    ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
                long remaining = st.File.Size;
                for (int si = 0; si < st.Slices; si++)
                {
                    ct.ThrowIfCancellationRequested();
                    int want = (int)Math.Min(blockSize, remaining);
                    int got = ReadFully(s, slice.AsSpan(0, want));
                    if (got != want) throw new SourceFileChangedException(st.File.Name, "file is shorter than expected");
                    if (want < blockSize) Array.Clear(slice, want, blockSize - want);
                    remaining -= want;

                    int sliceIndex = st.FirstSlice + si;
                    int baseLog = baseLogs[sliceIndex];
                    void Encode() => Parallel.For(0, count, r =>
                        Gf16.MulAdd(recovery[r], slice, Gf16.Coefficient(baseLog, r0 + r)));

                    if (hashing)
                    {
                        int checksumOffset = si * 20;
                        Parallel.Invoke(
                            () => MD5.HashData(slice, st.SliceChecksums.AsSpan(checksumOffset, 16)),
                            () => BinaryPrimitives.WriteUInt32LittleEndian(
                                st.SliceChecksums.AsSpan(checksumOffset + 16), Crc32.HashToUInt32(slice)),
                            () => md5Full!.AppendData(slice, 0, want),
                            () => sha?.AppendData(slice, 0, want),
                            Encode);
                    }
                    else Encode();

                    onProgress?.Invoke(ProgressShare(want, pass, passes));
                }
                if (s.ReadByte() >= 0) throw new SourceFileChangedException(st.File.Name, "file is longer than expected");
                if (hashing)
                {
                    st.Md5Full = md5Full!.GetHashAndReset();
                    if (sha != null)
                    {
                        string actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                        if (!string.Equals(actual, st.File.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                            throw new SourceFileChangedException(st.File.Name, "content no longer matches its checksum");
                    }
                }
            }

            if (hashing)
            {
                critical = BuildCriticalPackets(states, mainBody, setId);
                output.Write(critical);
            }
            for (int i = 0; i < count; i++)
                WriteRecoveryPacket(output, setId, (uint)(r0 + i), recovery[i]);
        }
        output.Write(critical!);
    }

    // Split progress evenly across passes without losing bytes to rounding.
    private static long ProgressShare(long bytes, int pass, int passes)
        => passes == 1 ? bytes : (pass == passes - 1 ? bytes - bytes / passes * (passes - 1) : bytes / passes);

    private static byte[] BuildCriticalPackets(List<FileState> states, byte[] mainBody, byte[] setId)
    {
        using var ms = new MemoryStream();
        WritePacket(ms, setId, TypeMain, mainBody);
        foreach (var st in states)
        {
            var body = new byte[56 + Par2Set.Pad4(st.NameBytes.Length)];
            st.Id.CopyTo(body, 0);
            st.Md5Full.CopyTo(body, 16);
            st.Md5First16K.CopyTo(body, 32);
            BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(48), (ulong)st.File.Size);
            st.NameBytes.CopyTo(body, 56);
            WritePacket(ms, setId, TypeFileDesc, body);
        }
        foreach (var st in states)
        {
            var body = new byte[16 + st.SliceChecksums.Length];
            st.Id.CopyTo(body, 0);
            st.SliceChecksums.CopyTo(body, 16);
            WritePacket(ms, setId, TypeIfsc, body);
        }
        var creator = new byte[Par2Set.Pad4(Encoding.ASCII.GetByteCount(Par2Set.Creator))];
        Encoding.ASCII.GetBytes(Par2Set.Creator, creator);
        WritePacket(ms, setId, TypeCreator, creator);
        return ms.ToArray();
    }

    private static void WritePacket(Stream output, byte[] setId, byte[] type, ReadOnlySpan<byte> body)
    {
        Span<byte> header = stackalloc byte[64];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], (ulong)(64 + body.Length));
        setId.CopyTo(header[32..]);
        type.CopyTo(header[48..]);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(header[32..]);
        md5.AppendData(body);
        md5.GetHashAndReset(header[16..32]);
        output.Write(header);
        output.Write(body);
    }

    private static void WriteRecoveryPacket(Stream output, byte[] setId, uint exponent, byte[] data)
    {
        Span<byte> header = stackalloc byte[68];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], (ulong)(68 + data.Length));
        setId.CopyTo(header[32..]);
        TypeRecovery.CopyTo(header[48..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[64..], exponent);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(header[32..]);
        md5.AppendData(data);
        md5.GetHashAndReset(header[16..32]);
        output.Write(header);
        output.Write(data);
    }

    private static int CompareIds(byte[] a, byte[] b)
    {
        for (int i = 15; i >= 0; i--)
        {
            int c = a[i].CompareTo(b[i]);
            if (c != 0) return c;
        }
        return 0;
    }

    internal static int ReadFully(Stream s, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = s.Read(buffer[total..]);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
