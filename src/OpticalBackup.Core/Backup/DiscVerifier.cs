using System.Security.Cryptography;
using OpticalBackup.Core.Catalog;

namespace OpticalBackup.Core.Backup;

public sealed class VerifyResult
{
    public required DiscManifest Manifest { get; init; }
    public int OkFiles { get; set; }
    public List<SkippedItem> Problems { get; } = new();
    public bool Success => Problems.Count == 0;
}

/// <summary>Checks a burned disc (or a mounted image, or a folder copy) against its own manifest.</summary>
public static class DiscVerifier
{
    public static VerifyResult Verify(string discRoot, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var manifest = CatalogStore.LoadManifest(discRoot);
        var result = new VerifyResult { Manifest = manifest };
        var meter = new ProgressMeter(progress);
        meter.Start($"Verifying {manifest.Label}", manifest.Files.Sum(f => f.Size));
        var buffer = new byte[1 << 20];
        foreach (var f in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            meter.Item(f.Path);
            string path = Path.Combine(discRoot, f.Path.Replace('/', Path.DirectorySeparatorChar));
            long read = 0;
            try
            {
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                int n;
                while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    sha.AppendData(buffer, 0, n);
                    read += n;
                    meter.Add(n);
                }
                string actual = Convert.ToHexString(sha.GetHashAndReset());
                if (read != f.Size) result.Problems.Add(new SkippedItem(f.Path, $"Wrong size ({read:N0} bytes, expected {f.Size:N0})"));
                else if (!string.Equals(actual, f.Sha256, StringComparison.OrdinalIgnoreCase)) result.Problems.Add(new SkippedItem(f.Path, "Content is damaged (checksum mismatch)"));
                else result.OkFiles++;
            }
            catch (FileNotFoundException) { result.Problems.Add(new SkippedItem(f.Path, "File is missing")); }
            catch (DirectoryNotFoundException) { result.Problems.Add(new SkippedItem(f.Path, "File is missing")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Problems.Add(new SkippedItem(f.Path, "Read error: " + ex.Message));
            }
            if (read < f.Size) meter.Add(f.Size - read);
        }
        meter.Finish();
        return result;
    }
}
