using OpticalBackup.Core.Udf;

namespace OpticalBackup.Core.Backup;

/// <summary>A file found in the source folder.</summary>
public sealed record SourceFile(string RelativePath, string FullPath, long Size, DateTime ModifiedUtc);

/// <summary>Something that was left out of the backup, and why.</summary>
public sealed record SkippedItem(string Path, string Reason);

public sealed class ScanResult
{
    public List<SourceFile> Files { get; } = new();
    public List<SkippedItem> Skipped { get; } = new();
    /// <summary>Relative paths (ending in '/') of folders that could not be listed.</summary>
    public List<string> UnreadableFolders { get; } = new();
}

public static class SourceScanner
{
    /// <summary>System clutter that is never backed up.</summary>
    public static readonly IReadOnlySet<string> ExcludedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Thumbs.db", "ehthumbs.db", "desktop.ini", ".DS_Store" };

    public static readonly IReadOnlySet<string> ExcludedFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "$RECYCLE.BIN", "System Volume Information", "@eaDir", "#recycle", ".Trashes", ".Spotlight-V100", ".fseventsd" };

    public static ScanResult Scan(string root, Action<int>? onFileCount = null, CancellationToken ct = default)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Source folder not found: {root}");
        var result = new ScanResult();
        var pending = new Stack<(DirectoryInfo Dir, string Rel)>();
        pending.Push((new DirectoryInfo(root), ""));

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, rel) = pending.Pop();
            List<FileSystemInfo> children;
            try
            {
                children = dir.EnumerateFileSystemInfos().ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (rel.Length == 0) throw;
                result.Skipped.Add(new SkippedItem(rel, "Folder could not be read: " + ex.Message));
                result.UnreadableFolders.Add(rel);
                continue;
            }

            foreach (var child in children.OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                string childRel = rel + child.Name;
                bool isDir = (child.Attributes & FileAttributes.Directory) != 0;
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0 && isDir)
                {
                    result.Skipped.Add(new SkippedItem(childRel + "/", "Link or junction to another folder (not followed)"));
                    continue;
                }
                if (isDir)
                {
                    if (ExcludedFolderNames.Contains(child.Name)) continue;
                    if (!UdfImageBuilder.IsValidName(child.Name))
                    {
                        result.Skipped.Add(new SkippedItem(childRel + "/", "Folder name is too long to store on a disc"));
                        result.UnreadableFolders.Add(childRel + "/");
                        continue;
                    }
                    pending.Push(((DirectoryInfo)child, childRel + "/"));
                }
                else
                {
                    if (ExcludedFileNames.Contains(child.Name)) continue;
                    if (!UdfImageBuilder.IsValidName(child.Name))
                    {
                        result.Skipped.Add(new SkippedItem(childRel, "File name is too long to store on a disc"));
                        continue;
                    }
                    var fi = (FileInfo)child;
                    try
                    {
                        result.Files.Add(new SourceFile(childRel, fi.FullName, fi.Length, fi.LastWriteTimeUtc));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        result.Skipped.Add(new SkippedItem(childRel, "File could not be read: " + ex.Message));
                    }
                    if (result.Files.Count % 500 == 0) onFileCount?.Invoke(result.Files.Count);
                }
            }
        }
        onFileCount?.Invoke(result.Files.Count);
        result.Files.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return result;
    }
}
