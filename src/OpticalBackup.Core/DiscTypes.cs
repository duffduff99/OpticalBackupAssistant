namespace OpticalBackup.Core;

/// <summary>A kind of recordable disc and how many bytes of image it can hold.</summary>
public sealed record DiscType(string Id, string Name, long CapacityBytes)
{
    public override string ToString() => Name;
}

public static class DiscTypes
{
    private const long Sector = 2048;

    // Capacities are the smallest of the common variants of each format (e.g. DVD+R vs DVD-R).
    // Blu-ray figures assume the drive formats the disc with default spare areas (defect
    // management), which is what most burning software does unless told otherwise.
    public static readonly IReadOnlyList<DiscType> All = new[]
    {
        new DiscType("cd650", "CD-R 650 MB (74 min)", 333_000 * Sector),
        new DiscType("cd700", "CD-R 700 MB (80 min)", 359_844 * Sector),
        new DiscType("dvd5", "DVD±R 4.7 GB", 2_295_104 * Sector),
        new DiscType("dvd9", "DVD±R DL 8.5 GB", 4_171_712 * Sector),
        new DiscType("bd25", "Blu-ray BD-R 25 GB", 11_826_176 * Sector),
        new DiscType("bd50", "Blu-ray BD-R DL 50 GB", 23_652_352 * Sector),
        new DiscType("bd100", "Blu-ray BD-R XL 100 GB", 47_698_944 * Sector),
        new DiscType("bd128", "Blu-ray BD-R XL 128 GB", 60_928_000 * Sector),
    };

    public static DiscType Custom(long megabytes)
        => new("custom", $"Custom {megabytes:N0} MB", megabytes * 1024 * 1024 / Sector * Sector);

    public static DiscType? ById(string id) => All.FirstOrDefault(d => d.Id == id);
}
