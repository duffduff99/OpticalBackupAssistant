using System.Security.Cryptography;
using OpticalBackup.Core.Catalog;

namespace OpticalBackup.Core.Backup;

/// <summary>How much of a job one disc contributes to a restore, and how much of that is already done.</summary>
public sealed record RestoreDiscStatus(int Run, int Disc, string Label, int FilesNeeded, int FilesRestored)
{
    public bool Complete => FilesRestored >= FilesNeeded;
}

public sealed class RestoreResult
{
    public required string Label { get; init; }
    public int Restored { get; set; }
    public int AlreadyPresent { get; set; }
    public List<SkippedItem> Problems { get; } = new();
}

/// <summary>Rebuilds a job's source folder, as it was at the last backup, from its discs.</summary>
public static class RestoreEngine
{
    private static IEnumerable<(FileRecord Record, StoredContent Content)> CurrentFiles(Job job)
    {
        foreach (var r in job.Files)
            if (r.RemovedRun is null && job.Contents.TryGetValue(r.Sha256, out var c))
                yield return (r, c);
    }

    private static string DestinationPath(string destination, string relativePath)
    {
        var parts = relativePath.Split('/');
        if (parts.Any(p => p.Length == 0 || p == "." || p == ".." || p.Contains(':') || p.Contains('\\')))
            throw new InvalidDataException($"Catalog contains an unsafe path: {relativePath}");
        return Path.Combine(destination, Path.Combine(parts));
    }

    private static bool IsRestored(string path, FileRecord r)
    {
        var fi = new FileInfo(path);
        return fi.Exists && fi.Length == r.Size && Math.Abs((fi.LastWriteTimeUtc - r.ModifiedUtc).TotalSeconds) <= 2;
    }

    /// <summary>Lists the discs needed to restore the job and how far the destination has got.</summary>
    public static List<RestoreDiscStatus> GetStatus(Job job, string destination)
    {
        var groups = new SortedDictionary<(int Run, int Disc), (int Needed, int Done)>();
        foreach (var (record, content) in CurrentFiles(job))
        {
            var key = (content.Run, content.Disc);
            groups.TryGetValue(key, out var counts);
            bool done = IsRestored(DestinationPath(destination, record.Path), record);
            groups[key] = (counts.Needed + 1, counts.Done + (done ? 1 : 0));
        }
        return groups.Select(g => new RestoreDiscStatus(g.Key.Run, g.Key.Disc,
            DiscComposer.Label(job.Name, g.Key.Run, g.Key.Disc), g.Value.Needed, g.Value.Done)).ToList();
    }

    /// <summary>Copies everything the job needs from the given disc into the destination folder.</summary>
    public static RestoreResult RestoreFromDisc(Job job, string discRoot, string destination,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var manifest = CatalogStore.LoadManifest(discRoot);
        if (!string.Equals(manifest.Job, job.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"This disc belongs to backup job '{manifest.Job}', not '{job.Name}'.");

        var result = new RestoreResult { Label = manifest.Label };
        var wanted = CurrentFiles(job).Where(x => x.Content.Run == manifest.Run && x.Content.Disc == manifest.Disc).ToList();
        var meter = new ProgressMeter(progress);
        meter.Start($"Restoring from {manifest.Label}", wanted.Sum(x => x.Record.Size));
        var buffer = new byte[1 << 20];
        foreach (var (record, content) in wanted)
        {
            ct.ThrowIfCancellationRequested();
            meter.Item(record.Path);
            string target = DestinationPath(destination, record.Path);
            if (IsRestored(target, record))
            {
                result.AlreadyPresent++;
                meter.Add(record.Size);
                continue;
            }
            string source = Path.Combine(discRoot, content.DiscPath.Replace('/', Path.DirectorySeparatorChar));
            string temp = target + ".restoring";
            long copied = 0;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string actual;
                using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
                using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    int n;
                    while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        sha.AppendData(buffer, 0, n);
                        dst.Write(buffer, 0, n);
                        copied += n;
                        meter.Add(n);
                    }
                    actual = Convert.ToHexString(sha.GetHashAndReset());
                }
                if (!string.Equals(actual, record.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(temp);
                    result.Problems.Add(new SkippedItem(record.Path, "Content on the disc is damaged (checksum mismatch); not restored"));
                }
                else
                {
                    File.SetLastWriteTimeUtc(temp, record.ModifiedUtc);
                    File.Move(temp, target, overwrite: true);
                    result.Restored++;
                }
            }
            catch (OperationCanceledException)
            {
                TryDelete(temp);
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(temp);
                result.Problems.Add(new SkippedItem(record.Path, "Could not be restored: " + ex.Message));
            }
            if (copied < record.Size) meter.Add(record.Size - copied);
        }
        meter.Finish();
        return result;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
