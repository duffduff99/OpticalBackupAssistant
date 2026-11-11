using System.Diagnostics;

namespace OpticalBackup.Tests;

internal static class TestUtil
{
    public static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "oba-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static byte[] RandomBytes(int seed, int length)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    public static string WriteFile(string root, string relative, byte[] data, DateTime? mtimeUtc = null)
    {
        string full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, data);
        File.SetLastWriteTimeUtc(full, mtimeUtc ?? new DateTime(2024, 5, 6, 7, 8, 10, DateTimeKind.Utc));
        return full;
    }

    public static bool ToolExists(string name)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is null) return false;
        return path.Split(Path.PathSeparator).Any(p => File.Exists(Path.Combine(p, name)));
    }

    public static (int ExitCode, string Output) Run(string file, string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        string stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout + stderr.Result);
    }
}
