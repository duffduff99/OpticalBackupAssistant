using System.Text;
using System.Text.Json;
using OpticalBackup.Core.Catalog;
using OpticalBackup.Core.Par2;
using OpticalBackup.Core.Udf;

namespace OpticalBackup.Core.Backup;

/// <summary>A file chosen to be written to a disc in this run.</summary>
public sealed class PlannedFile
{
    public required SourceFile Source { get; init; }
    public required string Sha256 { get; init; }
    /// <summary>Other source files with identical content (stored once, listed in the manifest).</summary>
    public List<SourceFile> Duplicates { get; } = new();
    public string DiscPath => DiscComposer.FilesPrefix + Source.RelativePath;
}

/// <summary>Identifies one disc of one run.</summary>
public sealed record DiscContext(string JobName, int Run, DateTime RunDateUtc, int DiscNumber, int DiscCount,
    double RecoveryPercent, long CapacityBytes);

/// <summary>Decides exactly what goes on a disc: the user's files plus manifest, checksums, recovery data and README.</summary>
public static class DiscComposer
{
    public const string FilesPrefix = "Files/";
    public const string ReadmePath = "README.txt";
    public const string SumsPath = "SHA256SUMS.txt";

    /// <summary>Placeholder disc count used while planning, so real metadata is never larger than measured.</summary>
    internal const int PlanningDiscCount = 9999;

    public static string SafeJobName(string jobName)
    {
        var sb = new StringBuilder();
        foreach (char ch in jobName)
            sb.Append(ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' ? ch : '_');
        string s = sb.ToString().Trim('_');
        return s.Length == 0 ? "BACKUP" : s;
    }

    /// <summary>Volume label, e.g. PHOTOS_R003_D01.</summary>
    public static string Label(string jobName, int run, int disc)
    {
        string job = SafeJobName(jobName).ToUpperInvariant();
        if (job.Length > 14) job = job[..14];
        return $"{job}_R{run:D3}_D{disc:D2}";
    }

    public static string OutputName(string jobName, int run, int disc, int discCount)
        => $"{SafeJobName(jobName)}_run{run:D3}_disc{disc:D2}of{discCount:D2}";

    /// <summary>Recovery sets are kept large enough to absorb a sizeable scratch, small enough to encode in memory.</summary>
    public static long Par2SetTargetBytes(long capacityBytes)
        => capacityBytes < 30L * 1000 * 1000 * 1000 ? 2L << 30 : 4L << 30;

    public static List<Par2Set> PlanRecovery(DiscContext ctx, IEnumerable<PlannedFile> files, bool withContent)
        => Par2Planner.Plan(
            files.Select(f => new Par2SourceFile(f.DiscPath, f.Source.Size,
                withContent ? () => OpenSource(f.Source.FullPath) : null, f.Sha256)),
            ctx.RecoveryPercent, Par2SetTargetBytes(ctx.CapacityBytes));

    internal static FileStream OpenSource(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);

    public static DiscManifest BuildManifest(DiscContext ctx, IEnumerable<PlannedFile> files) => new()
    {
        Job = ctx.JobName,
        Run = ctx.Run,
        RunDateUtc = ctx.RunDateUtc,
        Disc = ctx.DiscNumber,
        DiscCount = ctx.DiscCount,
        Label = Label(ctx.JobName, ctx.Run, ctx.DiscNumber),
        RecoveryPercent = ctx.RecoveryPercent,
        Files = files.Select(f => new ManifestFile
        {
            Path = f.DiscPath,
            Size = f.Source.Size,
            Sha256 = f.Sha256,
            ModifiedUtc = f.Source.ModifiedUtc,
            AlsoAt = f.Duplicates.Count > 0 ? f.Duplicates.Select(d => d.RelativePath).ToList() : null,
        }).ToList(),
    };

    public static byte[] ManifestBytes(DiscManifest manifest)
        => JsonSerializer.SerializeToUtf8Bytes(manifest, CatalogStore.Indented);

    /// <summary>Checksum list in the format understood by "sha256sum -c".</summary>
    public static byte[] SumsBytes(IEnumerable<PlannedFile> files)
    {
        var sb = new StringBuilder();
        foreach (var f in files) sb.Append(f.Sha256).Append(" *").Append(f.DiscPath).Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static byte[] ReadmeBytes(DiscContext ctx, bool hasRecoveryData)
    {
        string label = Label(ctx.JobName, ctx.Run, ctx.DiscNumber);
        var sb = new StringBuilder();
        void L(string s = "") => sb.Append(s).Append("\r\n");
        L("OPTICAL BACKUP DISC");
        L("===================");
        L();
        L($"Backup job : {ctx.JobName}");
        L($"Backup run : {ctx.Run} ({ctx.RunDateUtc:yyyy-MM-dd})");
        L($"This disc  : {ctx.DiscNumber} of {ctx.DiscCount}   (label {label})");
        L();
        L("HOW TO GET THE FILES BACK");
        L("-------------------------");
        L("No special software is needed. The backed-up files are ordinary files in the");
        L("\"Files\" folder of this disc, in their original folder structure. Copy that");
        L("folder to a hard drive.");
        L();
        L("A backup usually spans several discs, and later runs add more discs holding");
        L("only what was new or changed. To rebuild everything, copy the \"Files\" folder");
        L("of every disc into the same destination folder, oldest run first, letting");
        L("newer files replace older ones.");
        L();
        L("Things a plain copy cannot know:");
        L(" - Identical files are stored only once. If the same file existed in several");
        L("   places, the other locations are listed as \"alsoAt\" in BackupInfo\\disc.json.");
        L(" - Files that were later renamed, moved or deleted in the original folder");
        L("   still appear under their old names.");
        L("The program that made this disc (Optical Backup Assistant) has a Restore");
        L("function that handles both: it recreates the folder exactly as it was at the");
        L("last backup and tells you which discs it needs.");
        L();
        L("WHAT ELSE IS ON THIS DISC");
        L("-------------------------");
        L("SHA256SUMS.txt             Checksums of every file on this disc. Check with");
        L("                           \"sha256sum -c SHA256SUMS.txt\" (Linux/macOS) or");
        L("                           compare using \"certutil -hashfile <file> SHA256\".");
        L("BackupInfo\\disc.json       List of the files on this disc (plain text).");
        L("BackupInfo\\catalog.json.gz Copy of the full backup catalog at the time this");
        L("                           disc was made: every file, and which disc holds it.");
        if (hasRecoveryData)
        {
            L("recovery-NNN.par2          Recovery data (standard PAR2 format).");
            L();
            L("IF THE DISC IS DAMAGED");
            L("----------------------");
            L($"This disc carries about {ctx.RecoveryPercent:0.#}% recovery data. If some files cannot be read");
            L("or fail their checksum:");
            L(" 1. Copy everything that can be read from this disc to a folder on a hard");
            L("    drive, keeping the folder structure (the .par2 files next to \"Files\").");
            L(" 2. Open each recovery-NNN.par2 with a PAR2 program and choose Repair:");
            L("    MultiPar (Windows, free), or \"par2 repair recovery-001.par2\" with");
            L("    par2cmdline (Windows/macOS/Linux, free).");
        }
        L();
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Builds the complete list of disc entries. Content openers are optional so the same code
    /// measures a planned disc and assembles a real one.
    /// </summary>
    internal static List<DiscEntry> Entries(DiscContext ctx, IReadOnlyList<PlannedFile> files,
        IReadOnlyList<(string Name, long Size, Func<Stream>? Open)> extras, bool withContent)
    {
        var entries = new List<DiscEntry>(files.Count + extras.Count);
        foreach (var (name, size, open) in extras)
            entries.Add(new DiscEntry(name, size, ctx.RunDateUtc, open));
        foreach (var f in files)
            entries.Add(new DiscEntry(f.DiscPath, f.Source.Size, f.Source.ModifiedUtc,
                withContent ? () => OpenSource(f.Source.FullPath) : null, f.Sha256));
        return entries;
    }

    internal static (string, long, Func<Stream>?) Extra(string name, byte[] content)
        => (name, content.Length, () => new MemoryStream(content, writable: false));

    public static UdfImageBuilder ImageBuilder(DiscContext ctx, IEnumerable<DiscEntry> entries)
    {
        var builder = new UdfImageBuilder(Label(ctx.JobName, ctx.Run, ctx.DiscNumber), ctx.RunDateUtc);
        foreach (var e in entries) builder.AddFile(e);
        return builder;
    }

    /// <summary>
    /// Exact upper bound of the image size for a planned disc, given the space set aside for the
    /// catalog copy. The real image is never larger.
    /// </summary>
    public static long Measure(DiscContext ctx, IReadOnlyList<PlannedFile> files, long catalogReserveBytes)
    {
        var planning = ctx with { DiscCount = PlanningDiscCount };
        var sets = PlanRecovery(planning, files, withContent: false);
        var extras = new List<(string, long, Func<Stream>?)>
        {
            (ReadmePath, ReadmeBytes(planning, sets.Count > 0).Length, null),
            (SumsPath, SumsBytes(files).Length, null),
            (CatalogStore.DiscManifestPath, ManifestBytes(BuildManifest(planning, files)).Length, null),
            (CatalogStore.DiscCatalogPath, catalogReserveBytes, null),
        };
        foreach (var s in sets) extras.Add((s.FileName, s.OutputSize, null));
        return ImageBuilder(planning, Entries(planning, files, extras, withContent: false)).ComputeImageSize();
    }
}
