using OpticalBackup.Core;
using OpticalBackup.Core.Backup;
using OpticalBackup.Core.Catalog;

// Command-line front end. The GUI is the main way to use the program; this exists for
// scripting and for testing the engine on any platform.
return Cli.Run(args);

static class Cli
{
    const string Usage = """
        Optical Backup Assistant (command line)

          obackup backup  --job NAME --source DIR --out DIR --catalog FILE
                          [--disc cd650|cd700|dvd5|dvd9|bd25|bd50|bd100|bd128|custom:MB]
                          [--recovery PERCENT] [--folder] [--rehash] [--plan-only]
          obackup verify  DISC_ROOT [--catalog FILE]
          obackup restore --job NAME --disc DISC_ROOT --dest DIR [--catalog FILE]
          obackup status  --job NAME --dest DIR --catalog FILE
          obackup list    --catalog FILE
          obackup import  DISC_ROOT --catalog FILE
        """;

    public static int Run(string[] args)
    {
        if (args.Length == 0) { Console.WriteLine(Usage); return 2; }
        try
        {
            var a = new Args(args.Skip(1).ToArray());
            switch (args[0])
            {
                case "backup": return Backup(a);
                case "verify": return Verify(a);
                case "restore": return Restore(a);
                case "status": return Status(a);
                case "list": return List(a);
                case "import": return Import(a);
                default: Console.WriteLine(Usage); return 2;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException
                                       or InvalidOperationException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }

    static DiscType ParseDisc(string id)
    {
        if (id.StartsWith("custom:", StringComparison.OrdinalIgnoreCase)) return DiscTypes.Custom(long.Parse(id[7..]));
        return DiscTypes.ById(id) ?? throw new ArgumentException($"Unknown disc type '{id}'.");
    }

    static IProgress<ProgressInfo> ConsoleProgress()
    {
        string? lastStage = null;
        return new SyncProgress(p =>
        {
            if (p.Stage != lastStage) { Console.WriteLine(p.Stage + "..."); lastStage = p.Stage; }
        });
    }

    static int Backup(Args a)
    {
        string catalogPath = a.Required("catalog");
        var catalog = CatalogStore.Load(catalogPath);
        var options = new BackupOptions
        {
            JobName = a.Required("job"),
            SourcePath = a.Required("source"),
            DiscType = ParseDisc(a.Get("disc") ?? "bd25"),
            RecoveryPercent = double.Parse(a.Get("recovery") ?? "5", System.Globalization.CultureInfo.InvariantCulture),
            RehashAll = a.Flag("rehash"),
        };
        var plan = BackupPlanner.CreatePlan(catalog, options, ConsoleProgress());
        Console.WriteLine($"Scanned {plan.ScannedFiles:N0} files ({BackupPlanner.FormatSize(plan.ScannedBytes)}).");
        Console.WriteLine($"To back up: {plan.NewFiles:N0} files ({BackupPlanner.FormatSize(plan.NewBytes)}) on {plan.Discs.Count} disc(s) of {options.DiscType.Name}.");
        Console.WriteLine($"Already backed up: {plan.UnchangedPaths:N0} unchanged, {plan.RelocatedPaths:N0} moved/renamed/duplicate. Deleted since last run: {plan.DeletedPaths:N0}.");
        foreach (var s in plan.Skipped) Console.WriteLine($"  SKIPPED {s.Path}: {s.Reason}");
        if (a.Flag("plan-only")) return 0;

        var results = BackupExecutor.Execute(catalog, plan, new ExecuteOptions
        {
            OutputDirectory = a.Required("out"),
            CatalogPath = catalogPath,
            Mode = a.Flag("folder") ? OutputMode.Folder : OutputMode.IsoImage,
        }, ConsoleProgress());
        foreach (var r in results)
            Console.WriteLine($"  {r.Label}: {r.OutputPath} ({BackupPlanner.FormatSize(r.ImageBytes)})" + (r.CatalogIncluded ? "" : " [no catalog copy]"));
        Console.WriteLine("Done. Catalog saved to " + catalogPath);
        return 0;
    }

    static int Verify(Args a)
    {
        string root = a.Positional(0);
        var result = DiscVerifier.Verify(root, ConsoleProgress());
        foreach (var p in result.Problems) Console.WriteLine($"  PROBLEM {p.Path}: {p.Reason}");
        Console.WriteLine($"{result.Manifest.Label}: {result.OkFiles:N0} files OK, {result.Problems.Count:N0} problems.");
        if (result.Success && a.Get("catalog") is { } catalogPath)
        {
            var catalog = CatalogStore.Load(catalogPath);
            if (CatalogUpdater.MarkVerified(catalog, result.Manifest, DateTime.UtcNow))
            {
                CatalogStore.Save(catalog, catalogPath);
                Console.WriteLine("Marked as verified in the catalog.");
            }
        }
        return result.Success ? 0 : 1;
    }

    static Job LoadJob(Args a, string? discRoot)
    {
        var catalog = a.Get("catalog") is { } path ? CatalogStore.Load(path)
            : CatalogStore.LoadFromDisc(discRoot ?? throw new ArgumentException("--catalog is required."));
        string name = a.Required("job");
        return catalog.FindJob(name) ?? throw new ArgumentException($"The catalog has no job named '{name}'.");
    }

    static void PrintStatus(Job job, string dest)
    {
        foreach (var s in RestoreEngine.GetStatus(job, dest))
            Console.WriteLine($"  {s.Label}: {s.FilesRestored:N0} of {s.FilesNeeded:N0} files restored" + (s.Complete ? "" : "  <-- needed"));
    }

    static int Restore(Args a)
    {
        string root = a.Required("disc"), dest = a.Required("dest");
        var job = LoadJob(a, root);
        var result = RestoreEngine.RestoreFromDisc(job, root, dest, ConsoleProgress());
        foreach (var p in result.Problems) Console.WriteLine($"  PROBLEM {p.Path}: {p.Reason}");
        Console.WriteLine($"{result.Label}: restored {result.Restored:N0}, already present {result.AlreadyPresent:N0}, problems {result.Problems.Count:N0}.");
        PrintStatus(job, dest);
        return result.Problems.Count == 0 ? 0 : 1;
    }

    static int Status(Args a)
    {
        PrintStatus(LoadJob(a, null), a.Required("dest"));
        return 0;
    }

    static int List(Args a)
    {
        var catalog = CatalogStore.Load(a.Required("catalog"));
        foreach (var job in catalog.Jobs)
        {
            Console.WriteLine($"{job.Name}: {job.Files.Count(f => f.RemovedRun is null):N0} current files, {job.Contents.Count:N0} stored contents, source {job.LastSourcePath}");
            foreach (var run in job.Runs)
            {
                Console.WriteLine($"  Run {run.Number} ({run.DateUtc:yyyy-MM-dd}), {run.DiscType}, {run.Discs.Count} disc(s)");
                foreach (var d in run.Discs)
                    Console.WriteLine($"    {d.Label}: {d.FileCount:N0} files, {BackupPlanner.FormatSize(d.DataBytes)}" +
                                      (d.VerifiedUtc is { } v ? $", verified {v:yyyy-MM-dd}" : ", not verified"));
            }
        }
        return 0;
    }

    static int Import(Args a)
    {
        string catalogPath = a.Required("catalog");
        var catalog = CatalogStore.Load(catalogPath);
        var updated = CatalogStore.Merge(catalog, CatalogStore.LoadFromDisc(a.Positional(0)));
        CatalogStore.Save(catalog, catalogPath);
        Console.WriteLine(updated.Count == 0 ? "Catalog already up to date." : "Updated jobs: " + string.Join(", ", updated));
        return 0;
    }
}

sealed class SyncProgress(Action<ProgressInfo> handler) : IProgress<ProgressInfo>
{
    public void Report(ProgressInfo value) => handler(value);
}

sealed class Args
{
    private readonly Dictionary<string, string?> _named = new();
    private readonly List<string> _positional = new();
    private static readonly HashSet<string> Flags = new() { "folder", "rehash", "plan-only" };

    public Args(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) { _positional.Add(args[i]); continue; }
            string key = args[i][2..];
            if (Flags.Contains(key)) _named[key] = null;
            else if (i + 1 < args.Length) _named[key] = args[++i];
            else throw new ArgumentException($"--{key} needs a value.");
        }
    }

    public string? Get(string key) => _named.GetValueOrDefault(key);
    public bool Flag(string key) => _named.ContainsKey(key);
    public string Required(string key) => Get(key) ?? throw new ArgumentException($"--{key} is required.");
    public string Positional(int i) => i < _positional.Count ? _positional[i] : throw new ArgumentException("Missing argument.");
}
