using System.Security.Cryptography;
using OpticalBackup.Core.Catalog;
using OpticalBackup.Core.Par2;
using OpticalBackup.Core.Udf;

namespace OpticalBackup.Core.Backup;

public enum OutputMode
{
    /// <summary>One .iso image per disc, ready for any burning program.</summary>
    IsoImage,
    /// <summary>One folder per disc, holding exactly what should be burned.</summary>
    Folder,
}

public sealed class ExecuteOptions
{
    public required string OutputDirectory { get; init; }
    public required string CatalogPath { get; init; }
    public OutputMode Mode { get; init; } = OutputMode.IsoImage;
}

public sealed record DiscResult(int Number, string Label, string OutputPath, long ImageBytes, bool CatalogIncluded);

/// <summary>Carries out a <see cref="BackupPlan"/>: writes each disc and records it in the catalog.</summary>
public static class BackupExecutor
{
    /// <summary>
    /// Writes every planned disc. The catalog is saved after each disc, so if the run stops part
    /// way through, completed discs stay recorded and the next run picks up the rest.
    /// </summary>
    public static List<DiscResult> Execute(BackupCatalog catalog, BackupPlan plan, ExecuteOptions options,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var results = new List<DiscResult>();
        int expectedRun = catalog.FindJob(plan.Options.JobName.Trim())?.NextRunNumber ?? 1;
        if (plan.RunNumber != expectedRun)
            throw new InvalidOperationException("The catalog has changed since this plan was made. Scan again.");
        Directory.CreateDirectory(options.OutputDirectory);
        var meter = new ProgressMeter(progress);

        if (plan.Discs.Count == 0)
        {
            CatalogUpdater.ApplyFinal(catalog, plan);
            CatalogStore.Save(catalog, options.CatalogPath);
            return results;
        }

        foreach (var disc in plan.Discs)
        {
            ct.ThrowIfCancellationRequested();
            bool last = disc.Number == plan.Discs.Count;
            var result = WriteDisc(catalog, plan, disc, last, options, meter, ct);
            CatalogUpdater.ApplyDisc(catalog, plan, disc, result.ImageBytes);
            if (last) CatalogUpdater.ApplyFinal(catalog, plan);
            CatalogStore.Save(catalog, options.CatalogPath);
            results.Add(result);
        }
        return results;
    }

    private static DiscResult WriteDisc(BackupCatalog catalog, BackupPlan plan, PlannedDisc disc, bool last,
        ExecuteOptions options, ProgressMeter meter, CancellationToken ct)
    {
        var ctx = plan.Context(disc);
        string label = DiscComposer.Label(ctx.JobName, ctx.Run, ctx.DiscNumber);
        string name = DiscComposer.OutputName(ctx.JobName, ctx.Run, ctx.DiscNumber, ctx.DiscCount);
        string prefix = $"Disc {disc.Number} of {plan.Discs.Count}: ";
        string workDir = Path.Combine(options.OutputDirectory, $".oba-work-{Guid.NewGuid():N}");
        string partial = Path.Combine(options.OutputDirectory, name + (options.Mode == OutputMode.IsoImage ? ".iso.partial" : ".partial"));
        Directory.CreateDirectory(workDir);
        try
        {
            EnsureFreeSpace(options.OutputDirectory, disc.PlannedImageBytes * 11 / 10);

            // Recovery data first: this also re-reads every file and confirms it still matches its checksum.
            var sets = DiscComposer.PlanRecovery(ctx, disc.Files, withContent: true);
            var extras = new List<(string, long, Func<Stream>?)>();
            meter.Start(prefix + "computing recovery data", sets.Sum(s => s.SourceBytes));
            foreach (var set in sets)
            {
                meter.Item(set.FileName);
                string path = Path.Combine(workDir, set.FileName);
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                    Par2Writer.Write(set, fs, meter.Add, ct);
                extras.Add((set.FileName, new FileInfo(path).Length, () => File.OpenRead(path)));
            }
            meter.Finish();

            extras.Insert(0, DiscComposer.Extra(DiscComposer.ReadmePath, DiscComposer.ReadmeBytes(ctx, sets.Count > 0)));
            extras.Insert(1, DiscComposer.Extra(DiscComposer.SumsPath, DiscComposer.SumsBytes(disc.Files)));
            extras.Insert(2, DiscComposer.Extra(CatalogStore.DiscManifestPath,
                DiscComposer.ManifestBytes(DiscComposer.BuildManifest(ctx, disc.Files))));

            // The disc carries a copy of the catalog as it will be once this disc is recorded.
            var snapshot = CatalogStore.Clone(catalog);
            CatalogUpdater.ApplyDisc(snapshot, plan, disc, disc.PlannedImageBytes);
            if (last) CatalogUpdater.ApplyFinal(snapshot, plan);
            byte[] catalogBytes = CatalogStore.ToGzip(snapshot);

            var withCatalog = new List<(string, long, Func<Stream>?)>(extras);
            withCatalog.Insert(3, DiscComposer.Extra(CatalogStore.DiscCatalogPath, catalogBytes));
            var entries = DiscComposer.Entries(ctx, disc.Files, withCatalog, withContent: true);
            bool catalogIncluded = true;
            long imageBytes = DiscComposer.ImageBuilder(ctx, entries).ComputeImageSize();
            if (imageBytes > ctx.CapacityBytes)
            {
                // The catalog compressed worse than estimated; this disc goes without its copy.
                catalogIncluded = false;
                entries = DiscComposer.Entries(ctx, disc.Files, extras, withContent: true);
                imageBytes = DiscComposer.ImageBuilder(ctx, entries).ComputeImageSize();
                if (imageBytes > ctx.CapacityBytes)
                    throw new InvalidOperationException($"Disc {disc.Number} does not fit the disc size (internal planning error).");
            }

            long dataBytes = entries.Sum(e => e.Size);
            meter.Start(prefix + (options.Mode == OutputMode.IsoImage ? "writing disc image" : "copying files"), dataBytes);
            string output;
            if (options.Mode == OutputMode.IsoImage)
            {
                output = Path.Combine(options.OutputDirectory, name + ".iso");
                using (var fs = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    DiscComposer.ImageBuilder(ctx, entries).Write(fs, meter.Add, ct);
                    fs.Flush(true);
                }
                File.Move(partial, output, overwrite: true);
            }
            else
            {
                output = Path.Combine(options.OutputDirectory, name);
                if (Directory.Exists(partial)) Directory.Delete(partial, true);
                FolderWriter.Write(entries, partial, meter.Add, ct);
                if (Directory.Exists(output)) Directory.Delete(output, true);
                Directory.Move(partial, output);
            }
            meter.Finish();
            return new DiscResult(disc.Number, label, output, imageBytes, catalogIncluded);
        }
        finally
        {
            TryDelete(workDir);
            TryDelete(partial);
        }
    }

    private static void EnsureFreeSpace(string directory, long needed)
    {
        long free;
        try
        {
            free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return; // network shares and unusual volumes: let the write itself fail if space runs out
        }
        if (free < needed)
            throw new IOException($"Not enough free space in the output folder: {BackupPlanner.FormatSize(needed)} needed for " +
                                  $"the next disc, {BackupPlanner.FormatSize(free)} available.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Writes disc entries as an ordinary folder tree, checking content as it is copied.</summary>
public static class FolderWriter
{
    public static void Write(IEnumerable<DiscEntry> entries, string directory, Action<long>? onProgress = null,
        CancellationToken ct = default)
    {
        var buffer = new byte[1 << 20];
        foreach (var e in entries)
        {
            string path = Path.Combine(directory, e.DiscPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var src = e.Open!())
            using (var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var sha = e.ExpectedSha256 != null ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null)
            {
                long total = 0;
                int n;
                while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    total += n;
                    if (total > e.Size) throw new SourceFileChangedException(e.DiscPath, "file is longer than expected");
                    sha?.AppendData(buffer, 0, n);
                    dst.Write(buffer, 0, n);
                    onProgress?.Invoke(n);
                }
                if (total != e.Size) throw new SourceFileChangedException(e.DiscPath, "file is shorter than expected");
                if (sha != null && !string.Equals(Convert.ToHexString(sha.GetHashAndReset()), e.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new SourceFileChangedException(e.DiscPath, "content no longer matches its checksum");
            }
            File.SetLastWriteTimeUtc(path, e.ModifiedUtc);
        }
    }
}
