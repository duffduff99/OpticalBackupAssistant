using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpticalBackup.Core.Catalog;

/// <summary>Loads, saves and merges catalog files.</summary>
public static class CatalogStore
{
    public const string DiscCatalogPath = "BackupInfo/catalog.json.gz";
    public const string DiscManifestPath = "BackupInfo/disc.json";

    internal static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static readonly JsonSerializerOptions Indented = new(Compact) { WriteIndented = true };

    /// <summary>Loads a catalog; a missing file yields an empty catalog.</summary>
    public static BackupCatalog Load(string path)
    {
        if (!File.Exists(path)) return new BackupCatalog();
        using var fs = File.OpenRead(path);
        return Read(fs);
    }

    private static BackupCatalog Read(Stream s)
    {
        var catalog = JsonSerializer.Deserialize<BackupCatalog>(s, Compact)
            ?? throw new InvalidDataException("Catalog file is empty.");
        if (catalog.FormatVersion > 1)
            throw new InvalidDataException("This catalog was written by a newer version of Optical Backup Assistant.");
        return catalog;
    }

    /// <summary>Saves atomically, keeping the previous version as "name.bak".</summary>
    public static void Save(BackupCatalog catalog, string path)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temp = full + ".tmp";
        using (var fs = File.Create(temp))
        {
            JsonSerializer.Serialize(fs, catalog, Compact);
            fs.Flush(true);
        }
        if (File.Exists(full)) File.Replace(temp, full, full + ".bak");
        else File.Move(temp, full);
    }

    public static byte[] ToGzip(BackupCatalog catalog)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            JsonSerializer.Serialize(gz, catalog, Compact);
        return ms.ToArray();
    }

    public static BackupCatalog Clone(BackupCatalog catalog)
        => JsonSerializer.Deserialize<BackupCatalog>(JsonSerializer.SerializeToUtf8Bytes(catalog, Compact), Compact)!;

    /// <summary>Reads the catalog copy stored on a disc (or a folder holding a disc's contents).</summary>
    public static BackupCatalog LoadFromDisc(string discRoot)
    {
        string path = Path.Combine(discRoot, DiscCatalogPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            throw new FileNotFoundException("This disc has no catalog copy (BackupInfo/catalog.json.gz).", path);
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        return Read(gz);
    }

    public static DiscManifest LoadManifest(string discRoot)
    {
        string path = Path.Combine(discRoot, DiscManifestPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            throw new FileNotFoundException("This is not an Optical Backup Assistant disc (BackupInfo/disc.json is missing).", path);
        using var fs = File.OpenRead(path);
        return JsonSerializer.Deserialize<DiscManifest>(fs, Compact) ?? throw new InvalidDataException("disc.json is empty.");
    }

    /// <summary>
    /// Merges jobs from another catalog (typically a disc's copy). A job is taken from the other
    /// catalog when it is missing here or has seen more runs there. Returns the names of jobs updated.
    /// </summary>
    public static List<string> Merge(BackupCatalog target, BackupCatalog other)
    {
        var updated = new List<string>();
        foreach (var job in other.Jobs)
        {
            var existing = target.FindJob(job.Name);
            int otherLast = job.Runs.Count == 0 ? 0 : job.Runs.Max(r => r.Number);
            int ourLast = existing is null || existing.Runs.Count == 0 ? 0 : existing.Runs.Max(r => r.Number);
            if (existing is null)
            {
                target.Jobs.Add(job);
                updated.Add(job.Name);
            }
            else if (otherLast > ourLast || (otherLast == ourLast && DiscCount(job) > DiscCount(existing)))
            {
                target.Jobs[target.Jobs.IndexOf(existing)] = job;
                updated.Add(job.Name);
            }
        }
        return updated;
    }

    private static int DiscCount(Job j) => j.Runs.Sum(r => r.Discs.Count);
}
