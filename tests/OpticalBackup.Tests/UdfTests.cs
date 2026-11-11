using OpticalBackup.Core;
using OpticalBackup.Core.Udf;
using Xunit;

namespace OpticalBackup.Tests;

public class UdfTests
{
    internal static List<(string Path, byte[] Data)> SampleFiles()
    {
        var files = new List<(string, byte[])>
        {
            ("README.txt", "hello disc\r\n"u8.ToArray()),
            ("Files/empty.dat", Array.Empty<byte>()),
            ("Files/one-byte.dat", new byte[] { 42 }),
            ("Files/exactly-one-block.dat", TestUtil.RandomBytes(1, 2048)),
            ("Files/photos/2019/IMG_0001.jpg", TestUtil.RandomBytes(2, 300_001)),
            ("Files/photos/2019/IMG_0002.jpg", TestUtil.RandomBytes(3, 4096)),
            ("Files/photos/Été à la plage/naïve café.txt", TestUtil.RandomBytes(4, 777)),
            ("Files/photos/日本語のフォルダ/写真 01.jpg", TestUtil.RandomBytes(5, 9000)),
            ("Files/" + new string('x', 200) + ".txt", TestUtil.RandomBytes(6, 10)),
            ("Files/" + new string('長', 80) + ".txt", TestUtil.RandomBytes(7, 10)),
        };
        // Enough entries to make a directory span several blocks.
        for (int i = 0; i < 300; i++)
            files.Add(($"Files/many/file-with-a-moderately-long-name-{i:D4}.bin", TestUtil.RandomBytes(100 + i, i * 13)));
        return files;
    }

    internal static UdfImageBuilder Build(List<(string Path, byte[] Data)> files, bool withContent = true)
    {
        var b = new UdfImageBuilder("TEST_R001_D01", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        foreach (var (path, data) in files)
            b.AddFile(new DiscEntry(path, data.Length, new DateTime(2020, 2, 3, 4, 5, 6, DateTimeKind.Utc),
                withContent ? () => new MemoryStream(data) : null));
        return b;
    }

    [Fact]
    public void ImageSizeIsPredictedExactly()
    {
        var files = SampleFiles();
        long predicted = Build(files, withContent: false).ComputeImageSize();
        var ms = new MemoryStream();
        Build(files).Write(ms);
        Assert.Equal(predicted, ms.Length);
        Assert.Equal(0, ms.Length % 2048);
    }

    [Fact]
    public void ImageExtractsCorrectlyWith7Zip()
    {
        if (!TestUtil.ToolExists("7z")) return; // external cross-check only where 7-Zip is installed
        string dir = TestUtil.NewTempDir();
        try
        {
            var files = SampleFiles();
            string iso = Path.Combine(dir, "test.iso");
            long progress = 0;
            using (var fs = File.Create(iso)) Build(files).Write(fs, d => progress += d);
            Assert.Equal(files.Sum(f => (long)f.Data.Length), progress);

            string outDir = Path.Combine(dir, "out");
            var r = TestUtil.Run("7z", dir, "x", "-tudf", "-y", "-o" + outDir, iso);
            Assert.True(r.ExitCode == 0, r.Output);
            foreach (var (path, data) in files)
            {
                string full = Path.Combine(outDir, path.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(full), "missing " + path);
                Assert.Equal(data, File.ReadAllBytes(full));
            }
            int extracted = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories).Count();
            Assert.Equal(files.Count, extracted);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void RejectsBadNamesAndChangedContent()
    {
        Assert.False(UdfImageBuilder.IsValidName(new string('a', 255)));
        Assert.True(UdfImageBuilder.IsValidName(new string('a', 254)));
        Assert.False(UdfImageBuilder.IsValidName(new string('長', 128)));
        Assert.True(UdfImageBuilder.IsValidName(new string('長', 127)));
        Assert.False(UdfImageBuilder.IsValidName(""));

        var b = new UdfImageBuilder("X", DateTime.UtcNow);
        b.AddFile(new DiscEntry("a/b", 1, DateTime.UtcNow));
        Assert.Throws<ArgumentException>(() => b.AddFile(new DiscEntry("a/b", 1, DateTime.UtcNow)));
        Assert.Throws<ArgumentException>(() => b.AddFile(new DiscEntry("a/b/c", 1, DateTime.UtcNow)));

        var data = new byte[5000];
        var grown = new UdfImageBuilder("X", DateTime.UtcNow);
        grown.AddFile(new DiscEntry("f", 4000, DateTime.UtcNow, () => new MemoryStream(data)));
        Assert.Throws<SourceFileChangedException>(() => grown.Write(new MemoryStream()));

        var shrunk = new UdfImageBuilder("X", DateTime.UtcNow);
        shrunk.AddFile(new DiscEntry("f", 6000, DateTime.UtcNow, () => new MemoryStream(data)));
        Assert.Throws<SourceFileChangedException>(() => shrunk.Write(new MemoryStream()));

        var wrongHash = new UdfImageBuilder("X", DateTime.UtcNow);
        wrongHash.AddFile(new DiscEntry("f", 5000, DateTime.UtcNow, () => new MemoryStream(data), new string('0', 64)));
        Assert.Throws<SourceFileChangedException>(() => wrongHash.Write(new MemoryStream()));
    }
}

public class UdfSampleImage
{
    /// <summary>Writes a sample image to the path in OBA_SAMPLE_ISO, for inspection with external tools.</summary>
    [Fact]
    public void WriteSampleImageWhenRequested()
    {
        string? path = Environment.GetEnvironmentVariable("OBA_SAMPLE_ISO");
        if (string.IsNullOrEmpty(path)) return;
        using var fs = File.Create(path);
        UdfTests.Build(UdfTests.SampleFiles()).Write(fs);
    }
}
