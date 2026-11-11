using System.Text;

namespace OpticalBackup.Core.Par2;

/// <summary>A file protected by a PAR2 recovery set.</summary>
/// <param name="Name">Path relative to the folder holding the .par2 file, using '/' separators.</param>
/// <param name="Size">File length in bytes (must be greater than zero).</param>
/// <param name="Open">Opens the file content for reading.</param>
/// <param name="ExpectedSha256">Optional lower-case hex SHA-256 the content must match.</param>
public sealed record Par2SourceFile(string Name, long Size, Func<Stream>? Open = null, string? ExpectedSha256 = null);

/// <summary>The fully-determined shape of one PAR2 recovery set (one .par2 file).</summary>
public sealed class Par2Set
{
    public const string Creator = "Optical Backup Assistant";

    public required string FileName { get; init; }
    public required IReadOnlyList<Par2SourceFile> Files { get; init; }
    public required int BlockSize { get; init; }
    public required int RecoveryBlocks { get; init; }

    public long SourceBytes => Files.Sum(f => f.Size);
    public int SliceCount => Files.Sum(f => SlicesOf(f.Size, BlockSize));

    public static int SlicesOf(long size, int blockSize) => (int)((size + blockSize - 1) / blockSize);
    internal static int Pad4(int n) => (n + 3) & ~3;

    /// <summary>Size of the critical packets (main, file descriptions, checksums, creator).</summary>
    internal long CriticalPacketBytes
    {
        get
        {
            long total = 64 + 12 + 16L * Files.Count;            // main
            foreach (var f in Files)
            {
                total += 64 + 56 + Pad4(Encoding.UTF8.GetByteCount(f.Name));       // file description
                total += 64 + 16 + 20L * SlicesOf(f.Size, BlockSize);              // slice checksums
            }
            total += 64 + Pad4(Encoding.ASCII.GetByteCount(Creator));              // creator
            return total;
        }
    }

    /// <summary>Exact size in bytes of the .par2 file this set produces.</summary>
    public long OutputSize => 2 * CriticalPacketBytes + (long)RecoveryBlocks * (68L + BlockSize);
}

public static class Par2Planner
{
    /// <summary>Target number of input slices per set. Encoding cost grows with slices x redundancy.</summary>
    public const int TargetSlices = 2000;
    public const int MaxFilesPerSet = 1000;
    public const int MinBlockSize = 1024;

    /// <summary>
    /// Groups files (kept in the given order) into recovery sets and chooses block size and
    /// recovery block count for each, so that each set carries roughly
    /// <paramref name="redundancyPercent"/> percent of its size in recovery data.
    /// </summary>
    public static List<Par2Set> Plan(IEnumerable<Par2SourceFile> files, double redundancyPercent, long setTargetBytes)
    {
        var sets = new List<Par2Set>();
        if (redundancyPercent <= 0) return sets;

        var current = new List<Par2SourceFile>();
        long currentBytes = 0;
        void Flush()
        {
            if (current.Count == 0) return;
            sets.Add(BuildSet($"recovery-{sets.Count + 1:D3}.par2", current, redundancyPercent));
            current = new List<Par2SourceFile>();
            currentBytes = 0;
        }

        foreach (var f in files)
        {
            if (f.Size <= 0) continue; // empty files carry no data to protect
            if (current.Count > 0 && (current.Count >= MaxFilesPerSet || currentBytes + f.Size > setTargetBytes))
                Flush();
            current.Add(f);
            currentBytes += f.Size;
        }
        Flush();
        return sets;
    }

    internal static Par2Set BuildSet(string fileName, List<Par2SourceFile> files, double redundancyPercent)
    {
        long total = files.Sum(f => f.Size);
        long max = files.Max(f => f.Size);

        // Smallest block size (multiple of 4) that keeps the slice count within the target.
        long lo = MinBlockSize / 4, hi = Math.Max(lo, (max + 3) / 4);
        while (lo < hi)
        {
            long mid = (lo + hi) / 2;
            if (CountSlices(files, mid * 4) <= TargetSlices) hi = mid; else lo = mid + 1;
        }
        long blockSize = lo * 4;
        if (blockSize > int.MaxValue - 1024)
            throw new InvalidOperationException("A file is too large for the recovery data block size.");

        long slices = CountSlices(files, blockSize);
        long recovery = (long)Math.Round(total * redundancyPercent / 100.0 / blockSize);
        recovery = Math.Clamp(recovery, 1, Math.Min(slices, 32768));

        return new Par2Set
        {
            FileName = fileName,
            Files = files,
            BlockSize = (int)blockSize,
            RecoveryBlocks = (int)recovery,
        };
    }

    private static long CountSlices(List<Par2SourceFile> files, long blockSize)
    {
        long n = 0;
        foreach (var f in files) n += (f.Size + blockSize - 1) / blockSize;
        return n;
    }
}
