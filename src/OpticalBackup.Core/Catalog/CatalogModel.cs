namespace OpticalBackup.Core.Catalog;

/// <summary>The backup "memory": every job, every disc written, and where each file's content lives.</summary>
public sealed class BackupCatalog
{
    public int FormatVersion { get; set; } = 1;
    public List<Job> Jobs { get; set; } = new();

    public Job? FindJob(string name)
        => Jobs.FirstOrDefault(j => string.Equals(j.Name, name, StringComparison.OrdinalIgnoreCase));

    public Job GetOrAddJob(string name)
    {
        var job = FindJob(name);
        if (job is null)
        {
            job = new Job { Name = name };
            Jobs.Add(job);
        }
        return job;
    }
}

/// <summary>A named backup of one source folder, built up over many runs.</summary>
public sealed class Job
{
    public string Name { get; set; } = "";
    public string? LastSourcePath { get; set; }
    public List<Run> Runs { get; set; } = new();

    /// <summary>Every path ever backed up. Entries with no RemovedRun describe the source as of the last run.</summary>
    public List<FileRecord> Files { get; set; } = new();

    /// <summary>Where each distinct file content (keyed by SHA-256) is stored.</summary>
    public Dictionary<string, StoredContent> Contents { get; set; } = new();

    public int NextRunNumber => Runs.Count == 0 ? 1 : Runs.Max(r => r.Number) + 1;
}

public sealed class Run
{
    public int Number { get; set; }
    public DateTime DateUtc { get; set; }
    public string DiscType { get; set; } = "";
    public long DiscCapacityBytes { get; set; }
    public double RecoveryPercent { get; set; }
    public List<DiscRecord> Discs { get; set; } = new();
}

public sealed class DiscRecord
{
    public int Number { get; set; }
    public string Label { get; set; } = "";
    public long ImageBytes { get; set; }
    public int FileCount { get; set; }
    public long DataBytes { get; set; }
    /// <summary>When a burned copy of this disc last passed verification.</summary>
    public DateTime? VerifiedUtc { get; set; }
}

public sealed class FileRecord
{
    /// <summary>Path relative to the job's source folder, using '/' separators.</summary>
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public string Sha256 { get; set; } = "";
    public int AddedRun { get; set; }
    /// <summary>Run in which this path was found deleted or changed; null while current.</summary>
    public int? RemovedRun { get; set; }
}

public sealed class StoredContent
{
    public long Size { get; set; }
    public int Run { get; set; }
    public int Disc { get; set; }
    /// <summary>Path on that disc, e.g. "Files/2019/img.jpg".</summary>
    public string DiscPath { get; set; } = "";
}

/// <summary>The BackupInfo/disc.json file on every disc.</summary>
public sealed class DiscManifest
{
    public int FormatVersion { get; set; } = 1;
    public string CreatedBy { get; set; } = "Optical Backup Assistant";
    public string Job { get; set; } = "";
    public int Run { get; set; }
    public DateTime RunDateUtc { get; set; }
    public int Disc { get; set; }
    /// <summary>Number of discs planned for this run.</summary>
    public int DiscCount { get; set; }
    public string Label { get; set; } = "";
    public double RecoveryPercent { get; set; }
    public List<ManifestFile> Files { get; set; } = new();
}

public sealed class ManifestFile
{
    /// <summary>Path on the disc, e.g. "Files/2019/img.jpg".</summary>
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public DateTime ModifiedUtc { get; set; }
    /// <summary>Other source paths that held identical content when the disc was made.</summary>
    public List<string>? AlsoAt { get; set; }
}
