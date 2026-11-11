using OpticalBackup.Core.Par2;
using Xunit;

namespace OpticalBackup.Tests;

public class Par2Tests
{
    [Fact]
    public void MulAdd_SimdMatchesScalarAndFieldMultiply()
    {
        var rnd = new Random(1);
        foreach (int len in new[] { 2, 14, 16, 30, 32, 34, 62, 64, 1000, 4098 })
        {
            var src = new byte[len];
            rnd.NextBytes(src);
            foreach (ushort c in new ushort[] { 1, 2, 0x1234, 0xFFFF, 0x8000 })
            {
                var dst = new byte[len];
                rnd.NextBytes(dst);
                var expected = (byte[])dst.Clone();
                for (int i = 0; i < len; i += 2)
                {
                    ushort w = (ushort)(src[i] | (src[i + 1] << 8));
                    ushort p = Gf16.Mul(w, c);
                    expected[i] ^= (byte)p;
                    expected[i + 1] ^= (byte)(p >> 8);
                }
                Gf16.MulAdd(dst, src, c);
                Assert.Equal(expected, dst);
            }
        }
    }

    [Fact]
    public void SliceBases_FollowThePar2Sequence()
    {
        // n = 1, 2, 4, 7, 8, 11, 13, 14, 16, ... (not divisible by 3, 5, 17, 257)
        Assert.Equal(new[] { 1, 2, 4, 7, 8, 11, 13, 14, 16, 19 }, Gf16.SliceBaseLogs(10));
        Assert.Equal((ushort)2, Gf16.Exp(1));
    }

    [Fact]
    public void Planner_RespectsLimitsAndPredictsExactSize()
    {
        var files = new List<Par2SourceFile>();
        var rnd = new Random(5);
        for (int i = 0; i < 2500; i++) files.Add(new Par2SourceFile($"Files/dir{i % 7}/file{i}.bin", rnd.Next(1, 3_000_000)));
        files.Add(new Par2SourceFile("Files/empty.bin", 0));
        var sets = Par2Planner.Plan(files, 5, 1L << 30);
        Assert.True(sets.Count >= 3);
        Assert.Equal(2500, sets.Sum(s => s.Files.Count));
        foreach (var s in sets)
        {
            Assert.True(s.Files.Count <= Par2Planner.MaxFilesPerSet);
            Assert.True(s.SliceCount <= Par2Planner.TargetSlices);
            Assert.Equal(0, s.BlockSize % 4);
            long recoveryBytes = (long)s.RecoveryBlocks * s.BlockSize;
            Assert.True(recoveryBytes <= s.SourceBytes * 0.05 + s.BlockSize);
        }
        Assert.Empty(Par2Planner.Plan(files, 0, 1L << 30));
    }

    [Theory]
    [InlineData(Par2Writer.DefaultMemoryLimit)]
    [InlineData(40_000L)] // forces several encoding passes
    public void WrittenSet_VerifiesAndRepairsWithPar2cmdline(long memoryLimit)
    {
        string dir = TestUtil.NewTempDir();
        try
        {
            var specs = new (string Name, int Size)[]
            {
                ("Files/a.bin", 300_000), ("Files/sub dir/b.bin", 1), ("Files/sub dir/c.jpg", 16384),
                ("Files/d.bin", 123_457), ("Files/ünïcode/é.txt", 5000), ("Files/e.bin", 16385),
            };
            var sources = new List<Par2SourceFile>();
            int seed = 10;
            foreach (var (name, size) in specs)
            {
                string full = TestUtil.WriteFile(dir, name, TestUtil.RandomBytes(seed++, size));
                string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(full))).ToLowerInvariant();
                sources.Add(new Par2SourceFile(name, size, () => File.OpenRead(full), sha));
            }
            var set = Par2Planner.BuildSet("recovery-001.par2", sources, 10);
            string par2Path = Path.Combine(dir, set.FileName);
            long progress = 0;
            using (var fs = File.Create(par2Path))
                Par2Writer.Write(set, fs, d => progress += d, default, memoryLimit);

            Assert.Equal(set.OutputSize, new FileInfo(par2Path).Length);
            Assert.Equal(set.SourceBytes, progress);
            if (memoryLimit < 100_000) Assert.True(Par2Writer.PassCount(set, memoryLimit) > 1);

            if (!TestUtil.ToolExists("par2")) return; // external cross-check only where par2cmdline is installed

            var verify = TestUtil.Run("par2", dir, "verify", set.FileName);
            Assert.True(verify.ExitCode == 0, verify.Output);

            // Damage one file and delete another, then let par2cmdline repair them.
            string a = Path.Combine(dir, "Files", "a.bin");
            byte[] original = File.ReadAllBytes(a);
            byte[] damaged = (byte[])original.Clone();
            for (int i = 100_000; i < 110_000; i++) damaged[i] ^= 0x5A;
            File.WriteAllBytes(a, damaged);
            string c = Path.Combine(dir, "Files", "sub dir", "c.jpg");
            byte[] originalC = File.ReadAllBytes(c);
            File.Delete(c);

            Assert.NotEqual(0, TestUtil.Run("par2", dir, "verify", set.FileName).ExitCode);
            var repair = TestUtil.Run("par2", dir, "repair", set.FileName);
            Assert.True(repair.ExitCode == 0, repair.Output);
            Assert.Equal(original, File.ReadAllBytes(a));
            Assert.Equal(originalC, File.ReadAllBytes(c));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Write_DetectsChangedSource()
    {
        string dir = TestUtil.NewTempDir();
        try
        {
            string full = TestUtil.WriteFile(dir, "x.bin", TestUtil.RandomBytes(3, 50_000));
            var src = new Par2SourceFile("x.bin", 50_000, () => File.OpenRead(full), new string('0', 64));
            var set = Par2Planner.BuildSet("r.par2", new List<Par2SourceFile> { src }, 5);
            Assert.Throws<OpticalBackup.Core.SourceFileChangedException>(() => Par2Writer.Write(set, new MemoryStream()));
        }
        finally { Directory.Delete(dir, true); }
    }
}
