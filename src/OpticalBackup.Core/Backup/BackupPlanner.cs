using System.Security.Cryptography;
using System.Text;
using OpticalBackup.Core.Catalog;
using OpticalBackup.Core.Udf;

namespace OpticalBackup.Core.Backup;

public sealed class BackupOptions
{
    public required string JobName { get; init; }
    public required string SourcePath { get; init; }
    public required DiscType DiscType { get; init; }
    /// <summary>Recovery (PAR2) data as a percentage of the data on each disc; 0 disables it.</summary>
    public double RecoveryPercent { get; init; } = 5;
    /// <summary>Re-read every file instead of trusting unchanged size and modified time.</summary>
    public bool RehashAll { get; init; }
}

public sealed class PlannedDisc
{
    public required int Number { get; init; }
    public required List<PlannedFile> Files { get; init; }
    /// <summary>Upper bound of the image size measured while planning.</summary>
    public long PlannedImageBytes { get; set; }
    public long DataBytes => Files.Sum(f => f.Source.Size);
}

/// <summary>A scanned file whose content is (or will be) held on a disc.</summary>
public sealed record CoveredFile(SourceFile Source, string Sha256);

/// <summary>Everything a backup run is going to do, computed before any disc is written.</summary>
public sealed class BackupPlan
{
    public required BackupOptions Options { get; init; }
    public required int RunNumber { get; init; }
    public required DateTime RunDateUtc { get; init; }
    public List<PlannedDisc> Discs { get; } = new();
    public List<SkippedItem> Skipped { get; } = new();
    /// <summary>Scanned files whose content is on an earlier disc or on one of this run's discs.</summary>
    public List<CoveredFile> Covered { get; } = new();
    /// <summary>Every path seen in the source, including skipped ones (so they are not treated as deleted).</summary>
    public HashSet<string> SeenPaths { get; } = new(StringComparer.Ordinal);
    public List<string> UnreadableFolders { get; } = new();

    public int ScannedFiles { get; set; }
    public long ScannedBytes { get; set; }
    public int NewFiles => Discs.Sum(d => d.Files.Count);
    public long NewBytes => Discs.Sum(d => d.DataBytes);
    /// <summary>Paths whose content is already on a disc but which are new to the catalog (moved, renamed or copied).</summary>
    public int RelocatedPaths { get; set; }
    public int UnchangedPaths { get; set; }
    public int DeletedPaths { get; set; }
    public int TooLargeFiles { get; set; }

    public DiscContext Context(PlannedDisc disc) => new(Options.JobName, RunNumber, RunDateUtc, disc.Number,
        Discs.Count, Options.RecoveryPercent, BackupPlanner.UsableCapacity(Options.DiscType));
}

public static class BackupPlanner
{
    /// <summary>Space left unused at the end of every disc, as a margin for drive and media variation.</summary>
    public const long SafetyMarginBytes = 2L * 1024 * 1024;

    public static long UsableCapacity(DiscType type) => type.CapacityBytes - SafetyMarginBytes;

    public static string ValidateJobName(string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("Give the backup job a name.");
        if (name.Length > 40) throw new ArgumentException("The job name can be at most 40 characters.");
        return name;
    }

    public static BackupPlan CreatePlan(BackupCatalog catalog, BackupOptions options,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        string jobName = ValidateJobName(options.JobName);
        if (options.RecoveryPercent is < 0 or > 50) throw new ArgumentException("Recovery data must be between 0 and 50 percent.");
        long capacity = UsableCapacity(options.DiscType);
        if (capacity < 8L * 1024 * 1024) throw new ArgumentException("Disc size is too small.");

        var job = catalog.FindJob(jobName) ?? new Job { Name = jobName };
        var plan = new BackupPlan
        {
            Options = options,
            RunNumber = job.NextRunNumber,
            RunDateUtc = DateTime.UtcNow,
        };
        var meter = new ProgressMeter(progress);

        // 1. Scan.
        meter.Start("Scanning source folder", 0);
        var scan = SourceScanner.Scan(options.SourcePath, n => meter.Item($"{n:N0} files found"), ct);
        plan.Skipped.AddRange(scan.Skipped);
        plan.UnreadableFolders.AddRange(scan.UnreadableFolders);
        plan.ScannedFiles = scan.Files.Count;
        plan.ScannedBytes = scan.Files.Sum(f => f.Size);
        foreach (var f in scan.Files) plan.SeenPaths.Add(f.RelativePath);
        foreach (var s in scan.Skipped) plan.SeenPaths.Add(s.Path);

        // 2. Hash (re-using catalog hashes for files whose size and modified time are unchanged).
        var current = new Dictionary<string, FileRecord>(StringComparer.Ordinal);
        foreach (var r in job.Files)
            if (r.RemovedRun is null) current[r.Path] = r;

        var hashes = new string?[scan.Files.Count];
        long toHash = 0;
        for (int i = 0; i < scan.Files.Count; i++)
        {
            var f = scan.Files[i];
            if (!options.RehashAll && current.TryGetValue(f.RelativePath, out var rec)
                && rec.Size == f.Size && rec.ModifiedUtc == f.ModifiedUtc)
                hashes[i] = rec.Sha256;
            else toHash += f.Size;
        }
        meter.Start("Reading and checksumming files", toHash);
        var buffer = new byte[1 << 20];
        for (int i = 0; i < scan.Files.Count; i++)
        {
            if (hashes[i] != null) continue;
            ct.ThrowIfCancellationRequested();
            var f = scan.Files[i];
            meter.Item(f.RelativePath);
            try
            {
                hashes[i] = HashFile(f, buffer, meter, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                plan.Skipped.Add(new SkippedItem(f.RelativePath, "File could not be read: " + ex.Message));
            }
        }
        meter.Finish();

        // 3. Work out which contents are new.
        var fresh = new Dictionary<string, PlannedFile>(StringComparer.Ordinal);
        var pending = new List<PlannedFile>();
        var coveredByNew = new List<(CoveredFile File, PlannedFile Owner)>();
        for (int i = 0; i < scan.Files.Count; i++)
        {
            string? hash = hashes[i];
            if (hash is null) continue;
            var f = scan.Files[i];
            var covered = new CoveredFile(f, hash);
            if (job.Contents.ContainsKey(hash))
            {
                plan.Covered.Add(covered);
                if (current.TryGetValue(f.RelativePath, out var rec) && rec.Sha256 == hash) plan.UnchangedPaths++;
                else plan.RelocatedPaths++;
            }
            else if (fresh.TryGetValue(hash, out var owner))
            {
                owner.Duplicates.Add(f);
                coveredByNew.Add((covered, owner));
            }
            else
            {
                var pf = new PlannedFile { Source = f, Sha256 = hash };
                fresh.Add(hash, pf);
                pending.Add(pf);
                coveredByNew.Add((covered, pf));
            }
        }
        plan.DeletedPaths = current.Keys.Count(p => !plan.SeenPaths.Contains(p) && !IsUnderUnreadable(plan, p));

        // 4. Split the new files across discs.
        meter.Start("Planning discs", pending.Count);
        long catalogReserve = EstimateCatalogBytes(catalog, scan.Files.Count - plan.UnchangedPaths, pending,
            scan.Files, hashes, current);
        var tooLarge = new HashSet<PlannedFile>();
        Pack(plan, pending, capacity, catalogReserve, tooLarge, meter, ct);
        foreach (var pf in tooLarge)
        {
            plan.TooLargeFiles++;
            plan.Skipped.Add(new SkippedItem(pf.Source.RelativePath,
                $"File ({FormatSize(pf.Source.Size)}) is larger than one disc"));
        }
        foreach (var (file, owner) in coveredByNew)
            if (!tooLarge.Contains(owner)) plan.Covered.Add(file);
        meter.Finish();
        return plan;
    }

    internal static bool IsUnderUnreadable(BackupPlan plan, string path)
    {
        foreach (var prefix in plan.UnreadableFolders)
            if (path.StartsWith(prefix, StringComparison.Ordinal)) return true;
        return false;
    }

    private static string HashFile(SourceFile f, byte[] buffer, ProgressMeter meter, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var fs = DiscComposer.OpenSource(f.FullPath);
        long total = 0;
        int n;
        while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.AppendData(buffer, 0, n);
            total += n;
            meter.Add(n);
        }
        if (total != f.Size) meter.Add(f.Size - total); // keep the progress total honest if the file changed size
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Generous estimate of the gzip-compressed catalog once this run is recorded.</summary>
    private static long EstimateCatalogBytes(BackupCatalog catalog, int changedPaths, List<PlannedFile> pending,
        List<SourceFile> files, string?[] hashes, Dictionary<string, FileRecord> current)
    {
        long estimate = CatalogStore.ToGzip(catalog).Length + 8192;
        foreach (var pf in pending) estimate += 80 + Encoding.UTF8.GetByteCount(pf.Source.RelativePath);
        for (int i = 0; i < files.Count; i++)
        {
            if (hashes[i] is null) continue;
            if (current.TryGetValue(files[i].RelativePath, out var rec) && rec.Sha256 == hashes[i]) continue;
            estimate += 90 + Encoding.UTF8.GetByteCount(files[i].RelativePath);
        }
        return estimate;
    }

    /// <summary>Rough cost of adding a file to a disc; the exact figure is checked afterwards.</summary>
    private static long EstimateCost(PlannedFile f, double recoveryPercent)
    {
        long size = f.Source.Size;
        int pathBytes = Encoding.UTF8.GetByteCount(f.Source.RelativePath);
        long cost = (size + 2047) / 2048 * 2048 + 2048;           // data blocks + file entry
        cost += 6L * pathBytes + 700;                              // directory entry, manifest, checksums, PAR2 packets
        foreach (var other in f.Duplicates) cost += Encoding.UTF8.GetByteCount(other.RelativePath) + 16;
        if (recoveryPercent > 0) cost += (long)(size * (recoveryPercent / 100.0 + 0.002)) + 64;
        return cost;
    }

    private static void Pack(BackupPlan plan, List<PlannedFile> pending, long capacity, long catalogReserve,
        HashSet<PlannedFile> tooLarge, ProgressMeter meter, CancellationToken ct)
    {
        var options = plan.Options;
        double pct = options.RecoveryPercent;
        long fixedCost = (257 + 257 + 16) * 2048L + catalogReserve + 32 * 1024;
        var costs = pending.Select(f => EstimateCost(f, pct)).ToArray();
        var placed = new bool[pending.Count];
        int first = 0;
        const int Lookahead = 5000;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            while (first < pending.Count && placed[first]) first++;
            if (first >= pending.Count) break;

            int number = plan.Discs.Count + 1;
            var ctx = new DiscContext(options.JobName, plan.RunNumber, plan.RunDateUtc, number,
                DiscComposer.PlanningDiscCount, pct, capacity);

            // Fill in path order; once a file does not fit, look ahead for smaller ones that do.
            var chosen = new List<int>();
            var folders = new HashSet<string>(StringComparer.Ordinal);
            long used = fixedCost;
            int misses = 0;
            for (int i = first; i < pending.Count && misses < Lookahead; i++)
            {
                if (placed[i]) continue;
                long cost = costs[i] + NewFolderCost(folders, pending[i].Source.RelativePath, commit: false);
                if (used + cost <= capacity)
                {
                    NewFolderCost(folders, pending[i].Source.RelativePath, commit: true);
                    used += cost;
                    chosen.Add(i);
                }
                else
                {
                    misses++;
                    if (capacity - used < 64 * 1024) break;
                }
            }
            if (chosen.Count == 0) chosen.Add(first); // nothing fits by estimate: let the exact check decide

            // Exact check; back off from the end until the image really fits.
            var files = chosen.Select(i => pending[i]).ToList();
            long size;
            while ((size = DiscComposer.Measure(ctx, files, catalogReserve)) > capacity)
            {
                if (files.Count == 1) break;
                long over = size - capacity, removed = 0;
                while (files.Count > 1 && removed < over)
                {
                    removed += costs[chosen[^1]];
                    chosen.RemoveAt(chosen.Count - 1);
                    files.RemoveAt(files.Count - 1);
                }
            }
            if (size > capacity)
            {
                // A single file that cannot fit on an otherwise empty disc.
                placed[chosen[0]] = true;
                tooLarge.Add(files[0]);
                meter.Add(1);
                continue;
            }

            foreach (int i in chosen) placed[i] = true;
            plan.Discs.Add(new PlannedDisc { Number = number, Files = files, PlannedImageBytes = size });
            meter.Add(files.Count);
        }
    }

    private static long NewFolderCost(HashSet<string> folders, string relativePath, bool commit)
    {
        long cost = 0;
        int slash = -1;
        while ((slash = relativePath.IndexOf('/', slash + 1)) >= 0)
        {
            string folder = relativePath[..slash];
            if (commit ? folders.Add(folder) : !folders.Contains(folder))
                cost += 2 * 2048 + 2L * folder.Length + 48;
        }
        return cost;
    }

    public static string FormatSize(long bytes)
    {
        string[] units = { "bytes", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes:N0} bytes" : $"{v:0.##} {units[u]}";
    }
}
