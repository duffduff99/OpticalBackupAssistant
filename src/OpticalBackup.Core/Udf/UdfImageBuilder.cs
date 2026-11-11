using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace OpticalBackup.Core.Udf;

/// <summary>
/// Writes a read-only UDF 1.02 file system image (.iso) containing plain files and folders.
/// UDF 1.02 is readable by Windows (98 and later), macOS and Linux without extra software,
/// supports long Unicode names and files larger than 4 GB.
/// </summary>
public sealed class UdfImageBuilder
{
    public const int BlockSize = 2048;
    private const int PartitionStart = 257;         // first sector after the anchor at 256
    private const int TrailingSectors = 257;        // anchor at N-257, padding, anchor at N-1
    private const uint MaxExtentBytes = 0x3FFFF800; // largest block-aligned extent a short_ad can describe
    private const int MaxExtentsPerFile = (BlockSize - 176) / 8;
    private const string ImplementationId = "*OpticalBackupAssistant";

    private sealed class DirNode
    {
        public string Name = "";
        public DirNode? Parent;
        public readonly SortedDictionary<string, DirNode> Dirs = new(StringComparer.Ordinal);
        public readonly SortedDictionary<string, FileNode> Files = new(StringComparer.Ordinal);
        public uint FeLbn, DataLbn;
        public int DataLength;
        public ulong UniqueId;
    }

    private sealed class FileNode
    {
        public required string Name;
        public required DiscEntry Entry;
        public uint FeLbn, DataLbn;
        public ulong UniqueId;
    }

    private readonly DirNode _root = new();
    private readonly List<DirNode> _dirs = new();   // breadth-first, root first
    private readonly List<FileNode> _files = new(); // disc path order
    private readonly string _volumeLabel;
    private readonly DateTime _timestampUtc;
    private bool _laidOut;
    private uint _partitionBlocks;

    public UdfImageBuilder(string volumeLabel, DateTime timestampUtc)
    {
        if (string.IsNullOrEmpty(volumeLabel) || volumeLabel.Length > 30 || volumeLabel.Any(ch => ch > 0xFF))
            throw new ArgumentException("Volume label must be 1-30 Latin-1 characters.", nameof(volumeLabel));
        _volumeLabel = volumeLabel;
        _timestampUtc = timestampUtc.ToUniversalTime();
    }

    /// <summary>True when a single file or folder name can be stored in a UDF directory.</summary>
    public static bool IsValidName(string name)
        => name.Length > 0 && name != "." && name != ".." && name.IndexOfAny(new[] { '/', '\\', '\0' }) < 0
           && EncodedNameLength(name) <= 255;

    /// <summary>Largest file size a single file entry can describe in this writer.</summary>
    public static long MaxFileSize => (long)MaxExtentsPerFile * MaxExtentBytes;

    private static int EncodedNameLength(string name)
        => 1 + (name.Any(ch => ch > 0xFF) ? name.Length * 2 : name.Length);

    public void AddFile(DiscEntry entry)
    {
        if (_laidOut) throw new InvalidOperationException("Layout already computed.");
        if (entry.Size < 0 || entry.Size > MaxFileSize) throw new ArgumentException($"Unsupported file size: {entry.DiscPath}");
        var parts = entry.DiscPath.Split('/');
        var dir = _root;
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (!IsValidName(part)) throw new ArgumentException($"Name cannot be stored on disc: '{part}' in '{entry.DiscPath}'");
            if (i == parts.Length - 1)
            {
                if (dir.Dirs.ContainsKey(part) || !dir.Files.TryAdd(part, new FileNode { Name = part, Entry = entry }))
                    throw new ArgumentException($"Duplicate path on disc: {entry.DiscPath}");
            }
            else
            {
                if (dir.Files.ContainsKey(part)) throw new ArgumentException($"Path is both a file and a folder: {entry.DiscPath}");
                if (!dir.Dirs.TryGetValue(part, out var child))
                {
                    child = new DirNode { Name = part, Parent = dir };
                    dir.Dirs.Add(part, child);
                }
                dir = child;
            }
        }
    }

    /// <summary>Total image size in bytes for the files added so far.</summary>
    public long ComputeImageSize()
    {
        Layout();
        return ((long)PartitionStart + _partitionBlocks + TrailingSectors) * BlockSize;
    }

    private static int FidLength(string name) => (38 + EncodedNameLength(name) + 3) & ~3;

    private void Layout()
    {
        if (_laidOut) return;
        _laidOut = true;

        var queue = new Queue<DirNode>();
        queue.Enqueue(_root);
        while (queue.Count > 0)
        {
            var d = queue.Dequeue();
            _dirs.Add(d);
            foreach (var c in d.Dirs.Values) queue.Enqueue(c);
        }
        void Collect(DirNode d)
        {
            // Files in each folder first, then sub-folders: keeps a folder's files adjacent on disc.
            foreach (var f in d.Files.Values) _files.Add(f);
            foreach (var c in d.Dirs.Values) Collect(c);
        }
        Collect(_root);

        long next = 2; // 0 = file set descriptor, 1 = terminator
        ulong uniqueId = 16;
        foreach (var d in _dirs)
        {
            d.UniqueId = d == _root ? 0 : uniqueId++;
            d.FeLbn = (uint)next++;
            int len = 40; // parent entry
            foreach (var c in d.Dirs.Values) len += FidLength(c.Name);
            foreach (var f in d.Files.Values) len += FidLength(f.Name);
            d.DataLength = len;
            d.DataLbn = (uint)next;
            next += (len + BlockSize - 1) / BlockSize;
        }
        foreach (var f in _files)
        {
            f.UniqueId = uniqueId++;
            f.FeLbn = (uint)next++;
        }
        foreach (var f in _files)
        {
            if (f.Entry.Size == 0) continue;
            f.DataLbn = (uint)next;
            next += (f.Entry.Size + BlockSize - 1) / BlockSize;
            if (next > 0x7FFFFFF0) throw new InvalidOperationException("Image is too large.");
        }
        _partitionBlocks = (uint)next;
    }

    /// <summary>
    /// Writes the image sequentially. <paramref name="onProgress"/> receives byte deltas of file
    /// content written. Content is checked against each entry's expected SHA-256, if present.
    /// </summary>
    public void Write(Stream output, Action<long>? onProgress = null, CancellationToken ct = default)
    {
        Layout();
        if (_files.Any(f => f.Entry.Open is null)) throw new InvalidOperationException("All entries need content to write an image.");
        var w = new SectorWriter(output);
        uint totalSectors = PartitionStart + _partitionBlocks + TrailingSectors;
        uint partitionEnd = PartitionStart + _partitionBlocks;

        // Minimal ISO 9660 "bridge" so tools that only know ISO 9660 still recognise the image.
        // It lists a single stub file; all real content is in the UDF file system.
        w.SeekSector(16);
        w.WriteSector(IsoPrimaryVolumeDescriptor(totalSectors));
        var terminator = new byte[BlockSize];
        terminator[0] = 255;
        Encoding.ASCII.GetBytes("CD001", terminator.AsSpan(1));
        terminator[6] = 1;
        w.WriteSector(terminator);

        // UDF volume recognition sequence.
        foreach (var id in new[] { "BEA01", "NSR02", "TEA01" })
        {
            var b = new byte[BlockSize];
            Encoding.ASCII.GetBytes(id, b.AsSpan(1));
            b[6] = 1;
            w.WriteSector(b);
        }
        w.WriteSector(IsoPathTable(littleEndian: true));
        w.WriteSector(IsoPathTable(littleEndian: false));
        w.WriteSector(IsoRootDirectory());
        var stub = new byte[BlockSize];
        IsoStubText.CopyTo(stub, 0);
        w.WriteSector(stub);

        // Main and reserve volume descriptor sequences (16 sectors each).
        foreach (uint start in new uint[] { 32, 48 })
        {
            w.SeekSector(start);
            w.WriteSector(PrimaryVolumeDescriptor(start));
            w.WriteSector(ImplementationUseVolumeDescriptor(start + 1));
            w.WriteSector(PartitionDescriptor(start + 2));
            w.WriteSector(LogicalVolumeDescriptor(start + 3));
            w.WriteSector(UnallocatedSpaceDescriptor(start + 4));
            w.WriteSector(TerminatingDescriptor(start + 5));
        }

        w.SeekSector(64);
        w.WriteSector(LogicalVolumeIntegrityDescriptor(64));
        w.WriteSector(TerminatingDescriptor(65));

        w.SeekSector(256);
        w.WriteSector(AnchorDescriptor(256));

        // Partition: file set, directories, file entries, then file data.
        w.WriteSector(FileSetDescriptor());
        w.WriteSector(TerminatingDescriptor(1));
        foreach (var d in _dirs)
        {
            w.SeekSector(PartitionStart + d.FeLbn);
            w.WriteSector(DirectoryFileEntry(d));
            w.Write(DirectoryData(d));
        }
        foreach (var f in _files)
        {
            w.SeekSector(PartitionStart + f.FeLbn);
            w.WriteSector(FileFileEntry(f));
        }

        var buffer = new byte[1 << 20];
        foreach (var f in _files)
        {
            if (f.Entry.Size == 0)
            {
                VerifyEmpty(f.Entry);
                continue;
            }
            w.SeekSector(PartitionStart + f.DataLbn);
            using var src = f.Entry.Open!();
            using var sha = f.Entry.ExpectedSha256 != null ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
            long remaining = f.Entry.Size;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buffer.Length, remaining);
                int got = src.Read(buffer, 0, want);
                if (got <= 0) throw new SourceFileChangedException(f.Entry.DiscPath, "file is shorter than expected");
                sha?.AppendData(buffer, 0, got);
                w.Write(buffer.AsSpan(0, got));
                remaining -= got;
                onProgress?.Invoke(got);
            }
            if (src.ReadByte() >= 0) throw new SourceFileChangedException(f.Entry.DiscPath, "file is longer than expected");
            if (sha != null)
            {
                string actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                if (!string.Equals(actual, f.Entry.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new SourceFileChangedException(f.Entry.DiscPath, "content no longer matches its checksum");
            }
        }

        w.SeekSector(partitionEnd);
        w.WriteSector(AnchorDescriptor(partitionEnd));
        w.SeekSector(totalSectors - 1);
        w.WriteSector(AnchorDescriptor(totalSectors - 1));
        output.Flush();
    }

    private static void VerifyEmpty(DiscEntry e)
    {
        using var s = e.Open!();
        if (s.ReadByte() >= 0) throw new SourceFileChangedException(e.DiscPath, "file is longer than expected");
    }

    // ---------------------------------------------------------------- volume descriptors

    private byte[] PrimaryVolumeDescriptor(uint location)
    {
        var b = new byte[BlockSize];
        U32(b, 16, 0);                                   // volume descriptor sequence number
        U32(b, 20, 0);                                   // primary volume descriptor number
        DString(b.AsSpan(24, 32), _volumeLabel);
        U16(b, 56, 1); U16(b, 58, 1);                    // volume sequence number, maximum
        U16(b, 60, 2); U16(b, 62, 3);                    // interchange level, maximum
        U32(b, 64, 1); U32(b, 68, 1);                    // character set list, maximum
        string setId = ((ulong)_timestampUtc.Ticks).ToString("X16") + _volumeLabel;
        DString(b.AsSpan(72, 128), setId);
        Charspec(b.AsSpan(200, 64));
        Charspec(b.AsSpan(264, 64));
        EntityId(b.AsSpan(344, 32), ImplementationId, ReadOnlySpan<byte>.Empty); // application identifier
        Timestamp(b.AsSpan(376, 12), _timestampUtc);
        ImplementationEntity(b.AsSpan(388, 32));
        Tag(b, 1, location, 512);
        return b;
    }

    private byte[] ImplementationUseVolumeDescriptor(uint location)
    {
        var b = new byte[BlockSize];
        U32(b, 16, 1);
        UdfEntity(b.AsSpan(20, 32), "*UDF LV Info");
        Charspec(b.AsSpan(52, 64));
        DString(b.AsSpan(116, 128), _volumeLabel);
        // LVInfo1-3 (3 x 36 bytes at 244) left empty.
        ImplementationEntity(b.AsSpan(352, 32));
        Tag(b, 4, location, 512);
        return b;
    }

    private byte[] PartitionDescriptor(uint location)
    {
        var b = new byte[BlockSize];
        U32(b, 16, 2);
        U16(b, 20, 1);                                   // flags: space allocated
        U16(b, 22, 0);                                   // partition number
        EntityId(b.AsSpan(24, 32), "+NSR02", ReadOnlySpan<byte>.Empty);
        // Partition header descriptor (128 bytes at 56): no space tables on a read-only partition.
        U32(b, 184, 1);                                  // access type: read only
        U32(b, 188, PartitionStart);
        U32(b, 192, _partitionBlocks);
        ImplementationEntity(b.AsSpan(196, 32));
        Tag(b, 5, location, 512);
        return b;
    }

    private byte[] LogicalVolumeDescriptor(uint location)
    {
        var b = new byte[BlockSize];
        U32(b, 16, 3);
        Charspec(b.AsSpan(20, 64));
        DString(b.AsSpan(84, 128), _volumeLabel);
        U32(b, 212, BlockSize);
        DomainEntity(b.AsSpan(216, 32));
        LongAd(b.AsSpan(248, 16), 2 * BlockSize, 0);     // file set descriptor sequence
        U32(b, 264, 6);                                  // map table length
        U32(b, 268, 1);                                  // number of partition maps
        ImplementationEntity(b.AsSpan(272, 32));
        U32(b, 432, 2 * BlockSize); U32(b, 436, 64);     // integrity sequence extent
        b[440] = 1; b[441] = 6;                          // type 1 partition map
        U16(b, 442, 1);                                  // volume sequence number
        U16(b, 444, 0);                                  // partition number
        Tag(b, 6, location, 446);
        return b;
    }

    private static byte[] UnallocatedSpaceDescriptor(uint location)
    {
        var b = new byte[BlockSize];
        U32(b, 16, 4);
        U32(b, 20, 0);
        Tag(b, 7, location, 24);
        return b;
    }

    private static byte[] TerminatingDescriptor(uint location)
    {
        var b = new byte[BlockSize];
        Tag(b, 8, location, 512);
        return b;
    }

    private byte[] LogicalVolumeIntegrityDescriptor(uint location)
    {
        var b = new byte[BlockSize];
        Timestamp(b.AsSpan(16, 12), _timestampUtc);
        U32(b, 28, 1);                                   // integrity type: closed
        U64(b, 40, 16UL + (ulong)_dirs.Count + (ulong)_files.Count); // next unique ID
        U32(b, 72, 1);                                   // number of partitions
        U32(b, 76, 46);                                  // length of implementation use
        U32(b, 80, 0);                                   // free space
        U32(b, 84, _partitionBlocks);                    // partition size
        ImplementationEntity(b.AsSpan(88, 32));
        U32(b, 120, (uint)_files.Count);
        U32(b, 124, (uint)_dirs.Count);
        U16(b, 128, 0x0102); U16(b, 130, 0x0102); U16(b, 132, 0x0102); // min read, min write, max write
        Tag(b, 9, location, 134);
        return b;
    }

    private static byte[] AnchorDescriptor(uint location)
    {
        var b = new byte[BlockSize];
        U32(b, 16, 16 * BlockSize); U32(b, 20, 32);
        U32(b, 24, 16 * BlockSize); U32(b, 28, 48);
        Tag(b, 2, location, 512);
        return b;
    }

    // ---------------------------------------------------------------- ISO 9660 bridge

    private const uint IsoPathTableL = 21, IsoPathTableM = 22, IsoRootSector = 23, IsoStubSector = 24;
    private static readonly byte[] IsoStubText = Encoding.ASCII.GetBytes(
        "This disc uses the UDF file system. Windows, macOS and Linux read it natively.\r\n" +
        "If you only see this file, open the disc on a newer computer.\r\n");

    private static void Both32(Span<byte> b, int o, uint v)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(b[o..], v);
        BinaryPrimitives.WriteUInt32BigEndian(b[(o + 4)..], v);
    }

    private static void Both16(Span<byte> b, int o, ushort v)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(b[o..], v);
        BinaryPrimitives.WriteUInt16BigEndian(b[(o + 2)..], v);
    }

    private static void IsoText(Span<byte> field, string s)
    {
        field.Fill((byte)' ');
        Encoding.ASCII.GetBytes(s.Length > field.Length ? s[..field.Length] : s, field);
    }

    private int IsoDirectoryRecord(Span<byte> b, ReadOnlySpan<byte> name, uint sector, uint length, bool directory)
    {
        int len = (33 + name.Length + 1) & ~1;
        b[0] = (byte)len;
        Both32(b, 2, sector);
        Both32(b, 10, length);
        b[18] = (byte)(_timestampUtc.Year - 1900); b[19] = (byte)_timestampUtc.Month; b[20] = (byte)_timestampUtc.Day;
        b[21] = (byte)_timestampUtc.Hour; b[22] = (byte)_timestampUtc.Minute; b[23] = (byte)_timestampUtc.Second;
        b[25] = (byte)(directory ? 2 : 0);
        Both16(b, 28, 1);
        b[32] = (byte)name.Length;
        name.CopyTo(b[33..]);
        return len;
    }

    private byte[] IsoPrimaryVolumeDescriptor(uint totalSectors)
    {
        var b = new byte[BlockSize];
        b[0] = 1;
        Encoding.ASCII.GetBytes("CD001", b.AsSpan(1));
        b[6] = 1;
        IsoText(b.AsSpan(8, 32), "");
        string isoLabel = new string(_volumeLabel.ToUpperInvariant()
            .Select(ch => ch is >= 'A' and <= 'Z' or >= '0' and <= '9' ? ch : '_').ToArray());
        IsoText(b.AsSpan(40, 32), isoLabel);
        Both32(b, 80, totalSectors);
        Both16(b, 120, 1);
        Both16(b, 124, 1);
        Both16(b, 128, BlockSize);
        Both32(b, 132, 10);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(140), IsoPathTableL);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(148), IsoPathTableM);
        IsoDirectoryRecord(b.AsSpan(156, 34), stackalloc byte[] { 0 }, IsoRootSector, BlockSize, true);
        IsoText(b.AsSpan(190, 128), "");
        IsoText(b.AsSpan(318, 128), "");
        IsoText(b.AsSpan(446, 128), "");
        IsoText(b.AsSpan(574, 128), "OPTICAL BACKUP ASSISTANT");
        IsoText(b.AsSpan(702, 37), "");
        IsoText(b.AsSpan(739, 37), "");
        IsoText(b.AsSpan(776, 37), "");
        string stamp = _timestampUtc.ToString("yyyyMMddHHmmss") + "00";
        Encoding.ASCII.GetBytes(stamp, b.AsSpan(813, 16));
        Encoding.ASCII.GetBytes(stamp, b.AsSpan(830, 16));
        Encoding.ASCII.GetBytes("0000000000000000", b.AsSpan(847, 16));
        Encoding.ASCII.GetBytes("0000000000000000", b.AsSpan(864, 16));
        b[881] = 1;
        return b;
    }

    private static byte[] IsoPathTable(bool littleEndian)
    {
        var b = new byte[BlockSize];
        b[0] = 1;
        if (littleEndian)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(2), IsoRootSector);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), 1);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(2), IsoRootSector);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6), 1);
        }
        return b;
    }

    private byte[] IsoRootDirectory()
    {
        var b = new byte[BlockSize];
        int pos = IsoDirectoryRecord(b, stackalloc byte[] { 0 }, IsoRootSector, BlockSize, true);
        pos += IsoDirectoryRecord(b.AsSpan(pos), stackalloc byte[] { 1 }, IsoRootSector, BlockSize, true);
        IsoDirectoryRecord(b.AsSpan(pos), "README.TXT;1"u8, IsoStubSector, (uint)IsoStubText.Length, false);
        return b;
    }

    // ---------------------------------------------------------------- file set and files

    private byte[] FileSetDescriptor()
    {
        var b = new byte[BlockSize];
        Timestamp(b.AsSpan(16, 12), _timestampUtc);
        U16(b, 28, 3); U16(b, 30, 3);
        U32(b, 32, 1); U32(b, 36, 1);
        Charspec(b.AsSpan(48, 64));
        DString(b.AsSpan(112, 128), _volumeLabel);
        Charspec(b.AsSpan(240, 64));
        DString(b.AsSpan(304, 32), _volumeLabel);
        LongAd(b.AsSpan(400, 16), BlockSize, _root.FeLbn);
        DomainEntity(b.AsSpan(416, 32));
        Tag(b, 256, 0, 512);
        return b;
    }

    private byte[] FileEntryHeader(byte fileType, uint permissions, ushort linkCount, long length, DateTime time, ulong uniqueId)
    {
        var b = new byte[BlockSize];
        // ICB tag
        U16(b, 20, 4);                                   // strategy type
        U16(b, 24, 1);                                   // maximum number of entries
        b[27] = fileType;
        U16(b, 34, 0);                                   // flags: short allocation descriptors
        U32(b, 36, 0xFFFFFFFF);                          // uid: unspecified
        U32(b, 40, 0xFFFFFFFF);                          // gid: unspecified
        U32(b, 44, permissions);
        U16(b, 48, linkCount);
        U64(b, 56, (ulong)length);
        U64(b, 64, (ulong)((length + BlockSize - 1) / BlockSize));
        Timestamp(b.AsSpan(72, 12), time);
        Timestamp(b.AsSpan(84, 12), time);
        Timestamp(b.AsSpan(96, 12), time);
        U32(b, 108, 1);                                  // checkpoint
        ImplementationEntity(b.AsSpan(128, 32));
        U64(b, 160, uniqueId);
        return b;
    }

    private byte[] DirectoryFileEntry(DirNode d)
    {
        var b = FileEntryHeader(4, 0x14A5, (ushort)Math.Min(1 + d.Dirs.Count, ushort.MaxValue), d.DataLength, _timestampUtc, d.UniqueId);
        U32(b, 172, 8);
        U32(b, 176, (uint)d.DataLength);
        U32(b, 180, d.DataLbn);
        Tag(b, 261, d.FeLbn, 176 + 8);
        return b;
    }

    private byte[] FileFileEntry(FileNode f)
    {
        var b = FileEntryHeader(5, 0x1084, 1, f.Entry.Size, f.Entry.ModifiedUtc.ToUniversalTime(), f.UniqueId);
        int ads = 0;
        long remaining = f.Entry.Size;
        uint lbn = f.DataLbn;
        while (remaining > 0)
        {
            uint len = (uint)Math.Min(remaining, MaxExtentBytes);
            U32(b, 176 + ads * 8, len);
            U32(b, 180 + ads * 8, lbn);
            lbn += len / BlockSize;
            remaining -= len;
            ads++;
        }
        U32(b, 172, (uint)(ads * 8));
        Tag(b, 261, f.FeLbn, 176 + ads * 8);
        return b;
    }

    private byte[] DirectoryData(DirNode d)
    {
        var data = new byte[d.DataLength];
        int pos = 0;
        void Fid(string name, byte characteristics, uint icbLbn)
        {
            int nameLen = name.Length == 0 ? 0 : EncodedNameLength(name);
            int len = (38 + nameLen + 3) & ~3;
            var b = data.AsSpan(pos, len);
            U16(b, 16, 1);                               // file version number
            b[18] = characteristics;
            b[19] = (byte)nameLen;
            LongAd(b.Slice(20, 16), BlockSize, icbLbn);
            if (nameLen > 0) DChars(b.Slice(38, nameLen), name);
            Tag(b, 257, d.DataLbn + (uint)(pos / BlockSize), len);
            pos += len;
        }
        Fid("", 0x0A, (d.Parent ?? d).FeLbn);            // parent directory
        // One merged, ordinal-sorted listing of sub-folders and files.
        foreach (var name in d.Dirs.Keys.Concat(d.Files.Keys).OrderBy(n => n, StringComparer.Ordinal))
        {
            if (d.Dirs.TryGetValue(name, out var sub)) Fid(name, 0x02, sub.FeLbn);
            else Fid(name, 0x00, d.Files[name].FeLbn);
        }
        return data;
    }

    // ---------------------------------------------------------------- primitives

    private static void U16(Span<byte> b, int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b[o..], v);
    private static void U32(Span<byte> b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b[o..], v);
    private static void U64(Span<byte> b, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b[o..], v);

    /// <summary>Fills in the 16-byte descriptor tag at the start of <paramref name="b"/>.</summary>
    private static void Tag(Span<byte> b, ushort id, uint location, int descriptorLength)
    {
        U16(b, 0, id);
        U16(b, 2, 2);                                    // descriptor version (NSR02)
        U16(b, 6, 1);                                    // tag serial number
        int crcLength = descriptorLength - 16;
        U16(b, 8, Crc16(b.Slice(16, crcLength)));
        U16(b, 10, (ushort)crcLength);
        U32(b, 12, location);
        byte sum = 0;
        for (int i = 0; i < 16; i++) if (i != 4) sum += b[i];
        b[4] = sum;
    }

    private static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (byte x in data)
        {
            crc ^= (ushort)(x << 8);
            for (int i = 0; i < 8; i++)
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return crc;
    }

    private static void Charspec(Span<byte> b)
    {
        b[0] = 0; // CS0
        Encoding.ASCII.GetBytes("OSTA Compressed Unicode", b[1..]);
    }

    /// <summary>OSTA compressed Unicode characters: compression ID followed by 8- or 16-bit characters.</summary>
    private static void DChars(Span<byte> b, string s)
    {
        if (s.Any(ch => ch > 0xFF))
        {
            b[0] = 16;
            for (int i = 0; i < s.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(b[(1 + i * 2)..], s[i]);
        }
        else
        {
            b[0] = 8;
            for (int i = 0; i < s.Length; i++) b[1 + i] = (byte)s[i];
        }
    }

    /// <summary>Fixed-length dstring: d-characters, zero padding, length in the final byte.</summary>
    private static void DString(Span<byte> field, string s)
    {
        int max = field.Length - 2;
        if (s.Length > max) s = s[..max];
        if (s.Length == 0) return;
        if (s.Any(ch => ch > 0xFF)) throw new ArgumentException("Volume identifiers must be Latin-1.");
        DChars(field, s);
        field[^1] = (byte)(1 + s.Length);
    }

    private static void Timestamp(Span<byte> b, DateTime utc)
    {
        U16(b, 0, 0x1000); // type 1 (local time), offset 0 minutes from UTC
        BinaryPrimitives.WriteInt16LittleEndian(b[2..], (short)utc.Year);
        b[4] = (byte)utc.Month; b[5] = (byte)utc.Day;
        b[6] = (byte)utc.Hour; b[7] = (byte)utc.Minute; b[8] = (byte)utc.Second;
        b[9] = (byte)(utc.Millisecond / 10);
    }

    private static void EntityId(Span<byte> b, string id, ReadOnlySpan<byte> suffix)
    {
        b[0] = 0;
        Encoding.ASCII.GetBytes(id, b.Slice(1, 23));
        suffix.CopyTo(b[24..]);
    }

    private static void DomainEntity(Span<byte> b)
        => EntityId(b, "*OSTA UDF Compliant", stackalloc byte[] { 0x02, 0x01, 0x03 }); // UDF 1.02, hard+soft write protect

    private static void UdfEntity(Span<byte> b, string id)
        => EntityId(b, id, stackalloc byte[] { 0x02, 0x01, 6, 0 });   // UDF 1.02, OS class Windows NT

    private static void ImplementationEntity(Span<byte> b)
        => EntityId(b, ImplementationId, stackalloc byte[] { 6, 0 }); // OS class Windows NT

    private static void LongAd(Span<byte> b, uint length, uint lbn)
    {
        U32(b, 0, length);
        U32(b, 4, lbn);
        U16(b, 8, 0); // partition reference number
    }

    private sealed class SectorWriter
    {
        private readonly Stream _s;
        private long _pos;
        private static readonly byte[] Zeros = new byte[1 << 16];
        public SectorWriter(Stream s) { _s = s; }

        public void SeekSector(uint sector)
        {
            long target = (long)sector * BlockSize;
            if (target < _pos) throw new InvalidOperationException($"UDF layout error: sector {sector} already written.");
            while (_pos < target)
            {
                int n = (int)Math.Min(Zeros.Length, target - _pos);
                _s.Write(Zeros, 0, n);
                _pos += n;
            }
        }

        public void WriteSector(byte[] sector)
        {
            if (sector.Length != BlockSize || _pos % BlockSize != 0) throw new InvalidOperationException("UDF layout error: misaligned sector.");
            Write(sector);
        }

        public void Write(ReadOnlySpan<byte> data)
        {
            _s.Write(data);
            _pos += data.Length;
        }
    }
}
