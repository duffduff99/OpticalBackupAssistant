namespace OpticalBackup.Core;

/// <summary>A source file was modified, truncated or replaced between hashing and writing.</summary>
public sealed class SourceFileChangedException : IOException
{
    public string FilePath { get; }
    public SourceFileChangedException(string filePath, string detail)
        : base($"'{filePath}' changed while the backup was running ({detail}). Run the backup again.")
    {
        FilePath = filePath;
    }
}
