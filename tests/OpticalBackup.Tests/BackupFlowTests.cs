using OpticalBackup.Core;
using OpticalBackup.Core.Backup;
using OpticalBackup.Core.Catalog;
using Xunit;

namespace OpticalBackup.Tests;

public class BackupFlowTests : IDisposable
{
    private readonly string _dir = TestUtil.NewTempDir();
    private string Source => Path.Combine(_dir, "source");
    private string Output => Path.Combine(_dir, "out");
    private string CatalogPath => Path.Combine(_dir, "catalog", "catalog.json");

    public void Dispose() => Directory.Delete(_dir, true);

    private BackupOptions Options(long discMb = 10, double recovery = 5, bool rehash = false) => new()
    {
        JobName = "Test Job",
        SourcePath = Source,
        DiscType = DiscTypes.Custom(discMb),
        RecoveryPercent = recovery,
        RehashAll = rehash,
    };

    private (BackupPlan Plan, List<DiscResult> Results) Backup(BackupCatalog catalog, BackupOptions? options = null)
    {
        var plan = BackupPlanner.CreatePlan(catalog, options ?? Options());
        var results = BackupExecutor.Execute(catalog, plan, new ExecuteOptions
        {
            OutputDirectory = Output,
            CatalogPath = CatalogPath,
            Mode = OutputMode.Folder,
        });
        return (plan, results);
    }

    private void SeedSource()
    {
        int seed = 1;
        for (int i = 0; i < 30; i++) TestUtil.WriteFile(Source, $"photos/2019/img{i:D2}.jpg", TestUtil.RandomBytes(seed++, 400_000 + i * 1000));
        for (int i = 0; i < 50; i++) TestUtil.WriteFile(Source, $"docs/letter{i}.txt", TestUtil.RandomBytes(seed++, 100 + i * 37));
        TestUtil.WriteFile(Source, "docs/empty.txt", Array.Empty<byte>());
        TestUtil.WriteFile(Source, "docs/copy of img00.jpg", File.ReadAllBytes(Path.Combine(Source, "photos", "2019", "img00.jpg")));
        TestUtil.WriteFile(Source, "Thumbs.db", new byte[10]);
    }

    private static void AssertTreesEqual(string expected, string actual)
    {
        string[] Rel(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p)).Where(p => Path.GetFileName(p) != "Thumbs.db")
            .OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.Equal(Rel(expected), Rel(actual));
        foreach (var rel in Rel(expected))
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(expected, rel)), File.ReadAllBytes(Path.Combine(actual, rel)));
            Assert.Equal(File.GetLastWriteTimeUtc(Path.Combine(expected, rel)), File.GetLastWriteTimeUtc(Path.Combine(actual, rel)));
        }
    }

    private void RestoreAll(BackupCatalog catalog, string dest)
    {
        var job = catalog.FindJob("test job")!;
        foreach (var discDir in Directory.GetDirectories(Output))
        {
            var r = RestoreEngine.RestoreFromDisc(job, discDir, dest);
            Assert.Empty(r.Problems);
        }
        Assert.All(RestoreEngine.GetStatus(job, dest), s => Assert.True(s.Complete));
    }

    [Fact]
    public void FullThenIncrementalBackup_RestoresExactly()
    {
        SeedSource();
        var catalog = new BackupCatalog();
        var (plan, results) = Backup(catalog);

        Assert.True(results.Count >= 2, "expected the backup to span several discs");
        long capacity = BackupPlanner.UsableCapacity(Options().DiscType);
        Assert.All(results, r => Assert.True(r.ImageBytes <= capacity));
        Assert.All(results, r => Assert.True(r.CatalogIncluded));
        Assert.All(plan.Discs.Zip(results), p => Assert.True(p.Second.ImageBytes <= p.First.PlannedImageBytes));
        Assert.Equal(81, plan.NewFiles); // 30 photos + 50 letters + empty; the copy is stored once
        Assert.DoesNotContain(plan.Covered, c => c.Source.RelativePath == "Thumbs.db");

        // Every disc verifies and carries the standard pieces.
        foreach (var r in results)
        {
            Assert.True(File.Exists(Path.Combine(r.OutputPath, "README.txt")));
            Assert.True(File.Exists(Path.Combine(r.OutputPath, "SHA256SUMS.txt")));
            Assert.True(File.Exists(Path.Combine(r.OutputPath, "recovery-001.par2")));
            var v = DiscVerifier.Verify(r.OutputPath);
            Assert.True(v.Success);
            Assert.True(CatalogUpdater.MarkVerified(catalog, v.Manifest, DateTime.UtcNow));
        }

        // The catalog on disk round-trips, and a second run finds nothing to do.
        var reloaded = CatalogStore.Load(CatalogPath);
        var idle = BackupPlanner.CreatePlan(reloaded, Options());
        Assert.Empty(idle.Discs);
        Assert.Equal(82, idle.UnchangedPaths);

        string restore1 = Path.Combine(_dir, "restore1");
        RestoreAll(reloaded, restore1);
        AssertTreesEqual(Source, restore1);

        // Change the source: rename a folder, delete, modify, add, duplicate.
        Directory.Move(Path.Combine(Source, "photos", "2019"), Path.Combine(Source, "photos", "renamed"));
        File.Delete(Path.Combine(Source, "docs", "letter3.txt"));
        TestUtil.WriteFile(Source, "docs/letter4.txt", TestUtil.RandomBytes(999, 4321), DateTime.UtcNow.AddMinutes(-1));
        TestUtil.WriteFile(Source, "photos/new.jpg", TestUtil.RandomBytes(1000, 123_456));
        TestUtil.WriteFile(Source, "photos/new copy.jpg", TestUtil.RandomBytes(1000, 123_456));

        var (plan2, results2) = Backup(reloaded);
        Assert.Equal(2, plan2.RunNumber);
        Assert.Single(results2);
        Assert.Equal(2, plan2.NewFiles); // letter4 and one of the two new photos
        Assert.Equal(30, plan2.RelocatedPaths);

        string restore2 = Path.Combine(_dir, "restore2");
        var final = CatalogStore.Load(CatalogPath);
        RestoreAll(final, restore2);
        AssertTreesEqual(Source, restore2);

        // The newest disc's own catalog copy is enough to restore on a new computer.
        var fromDisc = CatalogStore.LoadFromDisc(results2[0].OutputPath);
        string restore3 = Path.Combine(_dir, "restore3");
        RestoreAll(fromDisc, restore3);
        AssertTreesEqual(Source, restore3);

        // History is kept: the deleted letter is still known and still on a disc.
        var job = final.FindJob("Test Job")!;
        var deleted = job.Files.Single(f => f.Path == "docs/letter3.txt");
        Assert.Equal(2, deleted.RemovedRun);
        Assert.True(job.Contents.ContainsKey(deleted.Sha256));
        Assert.Equal(2, job.Files.Count(f => f.Path == "docs/letter4.txt"));
    }

    [Fact]
    public void FileLargerThanDisc_IsReportedAndRetriedLater()
    {
        TestUtil.WriteFile(Source, "small.bin", TestUtil.RandomBytes(1, 1000));
        TestUtil.WriteFile(Source, "big.bin", TestUtil.RandomBytes(2, 9_000_000));
        var catalog = new BackupCatalog();
        var (plan, results) = Backup(catalog);
        Assert.Single(results);
        Assert.Equal(1, plan.TooLargeFiles);
        Assert.Contains(plan.Skipped, s => s.Path == "big.bin" && s.Reason.Contains("larger than one disc"));
        Assert.DoesNotContain(catalog.FindJob("Test Job")!.Files, f => f.Path == "big.bin");

        // With a bigger disc the file is picked up.
        var (plan2, _) = Backup(catalog, Options(discMb: 20));
        Assert.Equal(1, plan2.NewFiles);
        Assert.Equal(0, plan2.TooLargeFiles);
    }

    [Fact]
    public void UnchangedFilesAreNotReRead_UnlessAsked()
    {
        TestUtil.WriteFile(Source, "a.bin", TestUtil.RandomBytes(1, 5000));
        var catalog = new BackupCatalog();
        Backup(catalog);

        // Silent corruption: same size and time, different content.
        string path = Path.Combine(Source, "a.bin");
        var time = File.GetLastWriteTimeUtc(path);
        File.WriteAllBytes(path, TestUtil.RandomBytes(2, 5000));
        File.SetLastWriteTimeUtc(path, time);

        Assert.Empty(BackupPlanner.CreatePlan(catalog, Options()).Discs);
        Assert.Single(BackupPlanner.CreatePlan(catalog, Options(rehash: true)).Discs);
    }

    [Fact]
    public void SourceChangingMidRun_FailsThatDiscAndKeepsEarlierOnes()
    {
        for (int i = 0; i < 6; i++) TestUtil.WriteFile(Source, $"f{i}.bin", TestUtil.RandomBytes(i, 3_000_000));
        var catalog = new BackupCatalog();
        var plan = BackupPlanner.CreatePlan(catalog, Options());
        Assert.True(plan.Discs.Count >= 2);

        // Modify a file that belongs on the last disc after planning.
        var victim = plan.Discs[^1].Files[0].Source;
        File.WriteAllBytes(victim.FullPath, TestUtil.RandomBytes(77, 3_000_000));

        var exec = new ExecuteOptions { OutputDirectory = Output, CatalogPath = CatalogPath, Mode = OutputMode.IsoImage };
        Assert.Throws<SourceFileChangedException>(() => BackupExecutor.Execute(catalog, plan, exec));

        // Earlier discs are recorded and saved; no partial output is left behind.
        var saved = CatalogStore.Load(CatalogPath);
        Assert.Equal(plan.Discs.Count - 1, saved.FindJob("Test Job")!.Runs.Single().Discs.Count);
        Assert.Equal(plan.Discs.Count - 1, Directory.GetFiles(Output, "*.iso").Length);
        Assert.Empty(Directory.GetFiles(Output, "*.partial"));
        Assert.Empty(Directory.GetDirectories(Output));

        // The stale plan is refused; a fresh plan finishes the job as a new run.
        Assert.Throws<InvalidOperationException>(() => BackupExecutor.Execute(saved, plan, exec));
        var plan2 = BackupPlanner.CreatePlan(saved, Options());
        Assert.Equal(2, plan2.RunNumber);
        Assert.Equal(plan.Discs[^1].Files.Count, plan2.NewFiles);
    }

    [Fact]
    public void RemoveLastRun_RollsTheCatalogBack()
    {
        TestUtil.WriteFile(Source, "a.bin", TestUtil.RandomBytes(1, 5000));
        var catalog = new BackupCatalog();
        Backup(catalog);
        TestUtil.WriteFile(Source, "b.bin", TestUtil.RandomBytes(2, 5000));
        File.Delete(Path.Combine(Source, "a.bin"));
        Backup(catalog);

        var job = catalog.FindJob("Test Job")!;
        Assert.Equal(2, job.Runs.Count);
        Assert.True(CatalogUpdater.RemoveLastRun(job));
        Assert.Single(job.Runs);
        Assert.Single(job.Contents);
        var only = Assert.Single(job.Files);
        Assert.Equal("a.bin", only.Path);
        Assert.Null(only.RemovedRun);
    }

    [Fact]
    public void Merge_TakesTheNewerJob()
    {
        TestUtil.WriteFile(Source, "a.bin", TestUtil.RandomBytes(1, 5000));
        var catalog = new BackupCatalog();
        Backup(catalog);
        var old = CatalogStore.Clone(catalog);
        TestUtil.WriteFile(Source, "b.bin", TestUtil.RandomBytes(2, 5000));
        Backup(catalog);

        Assert.Empty(CatalogStore.Merge(CatalogStore.Clone(catalog), old));
        Assert.Equal(new[] { "Test Job" }, CatalogStore.Merge(old, catalog));
        Assert.Equal(2, old.FindJob("Test Job")!.Runs.Count);
    }

    [Fact]
    public void Verify_ReportsDamageAndMissingFiles()
    {
        SeedSource();
        var catalog = new BackupCatalog();
        var (_, results) = Backup(catalog, Options(discMb: 100));
        string disc = results.Single().OutputPath;
        string a = Path.Combine(disc, "Files", "docs", "letter10.txt");
        var bytes = File.ReadAllBytes(a);
        bytes[5] ^= 1;
        File.WriteAllBytes(a, bytes);
        File.Delete(Path.Combine(disc, "Files", "docs", "letter11.txt"));

        var v = DiscVerifier.Verify(disc);
        Assert.False(v.Success);
        Assert.Equal(2, v.Problems.Count);
        Assert.Contains(v.Problems, p => p.Path == "Files/docs/letter10.txt" && p.Reason.Contains("damaged"));
        Assert.Contains(v.Problems, p => p.Path == "Files/docs/letter11.txt" && p.Reason.Contains("missing"));
    }

    [Fact]
    public void DiscsFillClosely()
    {
        // 200 files of ~100 KB on 10 MB discs (8 MB usable): packing should waste little space.
        for (int i = 0; i < 200; i++) TestUtil.WriteFile(Source, $"d{i % 5}/f{i:D3}.bin", TestUtil.RandomBytes(i, 80_000 + (i * 7919) % 40_000));
        var plan = BackupPlanner.CreatePlan(new BackupCatalog(), Options());
        long capacity = BackupPlanner.UsableCapacity(Options().DiscType);
        foreach (var d in plan.Discs.Take(plan.Discs.Count - 1))
            Assert.True(d.PlannedImageBytes > capacity * 0.93, $"disc {d.Number} only {d.PlannedImageBytes * 100 / capacity}% full");
        Assert.Equal(200, plan.NewFiles);
    }
}
