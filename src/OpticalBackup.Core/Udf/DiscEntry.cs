namespace OpticalBackup.Core.Udf;

/// <summary>A file to be placed on a disc.</summary>
/// <param name="DiscPath">Path on the disc using '/' separators, e.g. "Files/2019/img.jpg".</param>
/// <param name="Size">Exact length in bytes.</param>
/// <param name="ModifiedUtc">Modification time recorded on the disc.</param>
/// <param name="Open">Opens the content; may be null when only the layout is being measured.</param>
/// <param name="ExpectedSha256">Optional lower-case hex SHA-256 the content must match while it is written.</param>
public sealed record DiscEntry(string DiscPath, long Size, DateTime ModifiedUtc,
    Func<Stream>? Open = null, string? ExpectedSha256 = null);
