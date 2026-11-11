using OpticalBackup.Core.Catalog;

namespace OpticalBackup.Core.Backup;

/// <summary>Records the results of a backup run in the catalog.</summary>
public static class CatalogUpdater
{
    private static Run EnsureRun(Job job, BackupPlan plan)
    {
        var run = job.Runs.FirstOrDefault(r => r.Number == plan.RunNumber);
        if (run is null)
        {
            run = new Run
            {
                Number = plan.RunNumber,
                DateUtc = plan.RunDateUtc,
                DiscType = plan.Options.DiscType.Name,
                DiscCapacityBytes = plan.Options.DiscType.CapacityBytes,
                RecoveryPercent = plan.Options.RecoveryPercent,
            };
            job.Runs.Add(run);
        }
        job.LastSourcePath = plan.Options.SourcePath;
        return run;
    }

    /// <summary>Records one finished disc: its contents become "backed up".</summary>
    public static void ApplyDisc(BackupCatalog catalog, BackupPlan plan, PlannedDisc disc, long imageBytes)
    {
        var job = catalog.GetOrAddJob(plan.Options.JobName.Trim());
        var run = EnsureRun(job, plan);
        run.Discs.RemoveAll(d => d.Number == disc.Number);
        run.Discs.Add(new DiscRecord
        {
            Number = disc.Number,
            Label = DiscComposer.Label(job.Name, plan.RunNumber, disc.Number),
            ImageBytes = imageBytes,
            FileCount = disc.Files.Count,
            DataBytes = disc.DataBytes,
        });

        var current = CurrentByPath(job);
        foreach (var f in disc.Files)
        {
            job.Contents[f.Sha256] = new StoredContent
            {
                Size = f.Source.Size,
                Run = plan.RunNumber,
                Disc = disc.Number,
                DiscPath = f.DiscPath,
            };
            Upsert(job, current, f.Source, f.Sha256, plan.RunNumber);
            foreach (var duplicate in f.Duplicates)
                Upsert(job, current, duplicate, f.Sha256, plan.RunNumber);
        }
    }

    /// <summary>
    /// Records everything that needs no disc: paths whose content is already stored (moves, renames,
    /// copies), and paths that have disappeared from the source.
    /// </summary>
    public static void ApplyFinal(BackupCatalog catalog, BackupPlan plan)
    {
        var job = catalog.GetOrAddJob(plan.Options.JobName.Trim());
        EnsureRun(job, plan);
        var current = CurrentByPath(job);
        foreach (var c in plan.Covered)
            if (job.Contents.ContainsKey(c.Sha256))
                Upsert(job, current, c.Source, c.Sha256, plan.RunNumber);

        foreach (var (path, record) in current)
            if (!plan.SeenPaths.Contains(path) && !BackupPlanner.IsUnderUnreadable(plan, path))
                record.RemovedRun = plan.RunNumber;
    }

    private static Dictionary<string, FileRecord> CurrentByPath(Job job)
    {
        var current = new Dictionary<string, FileRecord>(StringComparer.Ordinal);
        foreach (var r in job.Files)
            if (r.RemovedRun is null) current[r.Path] = r;
        return current;
    }

    private static void Upsert(Job job, Dictionary<string, FileRecord> current, SourceFile file, string sha256, int run)
    {
        if (current.TryGetValue(file.RelativePath, out var existing))
        {
            if (existing.Sha256 == sha256)
            {
                existing.Size = file.Size;
                existing.ModifiedUtc = file.ModifiedUtc; // touched but unchanged
                return;
            }
            existing.RemovedRun = run;
        }
        var record = new FileRecord
        {
            Path = file.RelativePath,
            Size = file.Size,
            ModifiedUtc = file.ModifiedUtc,
            Sha256 = sha256,
            AddedRun = run,
        };
        job.Files.Add(record);
        current[file.RelativePath] = record;
    }

    /// <summary>Marks a disc as verified. Returns false when the catalog does not know the disc.</summary>
    public static bool MarkVerified(BackupCatalog catalog, DiscManifest manifest, DateTime whenUtc)
    {
        var disc = catalog.FindJob(manifest.Job)?.Runs.FirstOrDefault(r => r.Number == manifest.Run)
            ?.Discs.FirstOrDefault(d => d.Number == manifest.Disc);
        if (disc is null) return false;
        disc.VerifiedUtc = whenUtc;
        return true;
    }

    /// <summary>
    /// Forgets the most recent run of a job, e.g. because its images were never burned.
    /// Files first backed up in that run will be backed up again next time.
    /// </summary>
    public static bool RemoveLastRun(Job job)
    {
        if (job.Runs.Count == 0) return false;
        int last = job.Runs.Max(r => r.Number);
        job.Runs.RemoveAll(r => r.Number == last);
        foreach (var key in job.Contents.Where(kv => kv.Value.Run == last).Select(kv => kv.Key).ToList())
            job.Contents.Remove(key);
        job.Files.RemoveAll(f => f.AddedRun == last);
        foreach (var f in job.Files)
            if (f.RemovedRun == last) f.RemovedRun = null;
        return true;
    }
}
