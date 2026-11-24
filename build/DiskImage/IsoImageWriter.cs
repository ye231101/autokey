using System.Buffers.Binary;
using System.Text;

namespace AutoKey.Build.DiskImage;

/// <summary>
/// Writes an ISO 9660 file system with Rock Ridge extensions (RRIP 1.09): long, case-preserving names, POSIX
/// permissions and symbolic links. Wrapped in a .dmg by <see cref="DmgWriter"/>, macOS mounts it like any disk image.
/// This is the combination genisoimage/xorrisofs and libdmg produce when .dmg files are built without a Mac.
/// </summary>
public sealed class IsoImageWriter
{
    private const int SectorSize = 2048;
    private const int SystemAreaSectors = 16;
    private const byte DirectoryFlag = 0x02;

    // POSIX file types and permissions (octal 0040755, 0100755, 0100644 and 0120777).
    private const uint DirectoryMode = 0x41ED;
    private const uint ExecutableMode = 0x81ED;
    private const uint RegularMode = 0x81A4;
    private const uint SymbolicLinkMode = 0xA1FF;

    // Rock Ridge "RR" flags: which entries a record carries.
    private const byte HasPosixAttributes = 0x01;
    private const byte HasSymbolicLink = 0x04;
    private const byte HasName = 0x08;
    private const byte HasTimestamps = 0x80;

    private const string ExtensionId = "RRIP_1991A";
    private const string ExtensionDescription = "THE ROCK RIDGE INTERCHANGE PROTOCOL PROVIDES SUPPORT FOR POSIX FILE SYSTEM SEMANTICS";
    private const string ExtensionSource = "PLEASE CONTACT DISC PUBLISHER FOR SPECIFICATION SOURCE.  SEE PUBLISHER IDENTIFIER IN PRIMARY VOLUME DESCRIPTOR FOR CONTACT INFORMATION.";

    private readonly DirectoryNode _root = new(string.Empty);
    private readonly DateTime _time = DateTime.UtcNow.AddTicks(-(DateTime.UtcNow.Ticks % TimeSpan.TicksPerSecond));

    /// <summary>Adds the contents of a directory on disk under <paramref name="imagePath"/> (for example "AutoKey.app").</summary>
    public void AddDirectory(string sourceDirectory, string imagePath, Func<string, bool> isExecutable)
    {
        DirectoryNode target = GetOrCreateDirectory(imagePath);
        Fill(target, sourceDirectory, isExecutable);
    }

    /// <summary>Adds a symbolic link, for example "Applications" pointing to "/Applications".</summary>
    public void AddSymbolicLink(string imagePath, string target)
    {
        int slash = imagePath.LastIndexOf('/');
        DirectoryNode parent = slash < 0 ? _root : GetOrCreateDirectory(imagePath[..slash]);
        parent.Children.Add(new LinkNode(imagePath[(slash + 1)..], target));
    }

    /// <summary>Writes the image. The volume identifier is limited to A–Z, 0–9 and _ (at most 32 characters).</summary>
    public void Write(Stream output, string volumeId)
    {
        if (volumeId.Length > 32 || volumeId.Any(c => !(c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')))
        {
            throw new ArgumentException("Use at most 32 characters A-Z, 0-9 and _.", nameof(volumeId));
        }

        // Directories in path-table order: breadth first, children sorted by ISO name.
        var directories = new List<DirectoryNode>();
        var queue = new Queue<DirectoryNode>([_root]);
        while (queue.Count > 0)
        {
            DirectoryNode directory = queue.Dequeue();
            AssignIsoNames(directory);
            directory.Number = directories.Count + 1;
            directories.Add(directory);
            foreach (DirectoryNode child in directory.Children.OfType<DirectoryNode>())
            {
                child.Parent = directory;
                queue.Enqueue(child);
            }
        }

        byte[] extensionReference = ExtensionReference();
        foreach (DirectoryNode directory in directories)
        {
            directory.Size = DirectoryExtent(directory, continuationSector: 0, extensionReference.Length).Length;
        }

        // Allocate sectors: descriptors, path tables, the continuation area, directories, then file data.
        byte[] littleEndianTable = PathTable(directories, bigEndian: false);
        int pathTableSectors = SectorsFor(littleEndianTable.Length);
        int sector = SystemAreaSectors + 2;
        int littleEndianTableSector = sector;
        sector += pathTableSectors;
        int bigEndianTableSector = sector;
        sector += pathTableSectors;
        int continuationSector = sector++;
        foreach (DirectoryNode directory in directories)
        {
            directory.Extent = sector;
            sector += directory.Size / SectorSize;
        }

        List<FileNode> files = directories.SelectMany(d => d.Children.OfType<FileNode>()).ToList();
        foreach (FileNode file in files)
        {
            file.Extent = file.Length == 0 ? 0 : sector;
            sector += SectorsFor(file.Length);
        }

        int totalSectors = sector;

        output.Write(new byte[SystemAreaSectors * SectorSize]);
        output.Write(PrimaryVolumeDescriptor(volumeId, totalSectors, littleEndianTable.Length, littleEndianTableSector, bigEndianTableSector));
        output.Write(Terminator());
        output.Write(PadToSectors(PathTable(directories, bigEndian: false)));
        output.Write(PadToSectors(PathTable(directories, bigEndian: true)));
        output.Write(PadToSectors(extensionReference));
        foreach (DirectoryNode directory in directories)
        {
            output.Write(DirectoryExtent(directory, continuationSector, extensionReference.Length));
        }

        foreach (FileNode file in files)
        {
            using (FileStream source = File.OpenRead(file.Source))
            {
                source.CopyTo(output);
            }

            int remainder = (int)(file.Length % SectorSize);
            if (remainder != 0)
            {
                output.Write(new byte[SectorSize - remainder]);
            }
        }
    }

    private static void Fill(DirectoryNode target, string source, Func<string, bool> isExecutable)
    {
        foreach (string file in Directory.GetFiles(source))
        {
            target.Children.Add(new FileNode(Path.GetFileName(file), file, new FileInfo(file).Length, isExecutable(file)));
        }

        foreach (string directory in Directory.GetDirectories(source))
        {
            var child = new DirectoryNode(Path.GetFileName(directory));
            target.Children.Add(child);
            Fill(child, directory, isExecutable);
        }
    }

    private DirectoryNode GetOrCreateDirectory(string imagePath)
    {
        DirectoryNode current = _root;
        foreach (string part in imagePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            DirectoryNode? next = current.Children.OfType<DirectoryNode>().FirstOrDefault(d => d.Name == part);
            if (next is null)
            {
                next = new DirectoryNode(part);
                current.Children.Add(next);
            }

            current = next;
        }

        return current;
    }

    /// <summary>ISO 9660 level 1 names are just placeholders here; readers show the Rock Ridge names.</summary>
    private static void AssignIsoNames(DirectoryNode directory)
    {
        int directoryNumber = 0;
        int fileNumber = 0;
        foreach (Node child in directory.Children.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            child.IsoName = child is DirectoryNode ? $"D{++directoryNumber:D7}" : $"F{++fileNumber:D7}.;1";
        }

        directory.Children.Sort((a, b) => string.CompareOrdinal(a.IsoName, b.IsoName));
    }

    private byte[] DirectoryExtent(DirectoryNode directory, int continuationSector, int continuationLength)
    {
        DirectoryNode parent = directory.Parent ?? directory;
        var records = new List<byte[]>
        {
            Record(directory.Extent, directory.Size, DirectoryFlag, [0x00],
                SystemUse(directory, name: null, isRoot: directory == _root, continuationSector, continuationLength)),
            Record(parent.Extent, parent.Size, DirectoryFlag, [0x01], SystemUse(parent, name: null)),
        };

        foreach (Node child in directory.Children)
        {
            byte[] identifier = Encoding.ASCII.GetBytes(child.IsoName);
            records.Add(child switch
            {
                DirectoryNode d => Record(d.Extent, d.Size, DirectoryFlag, identifier, SystemUse(d, d.Name)),
                FileNode f => Record(f.Extent, f.Length, 0, identifier, SystemUse(f, f.Name)),
                _ => Record(0, 0, 0, identifier, SystemUse(child, child.Name)),
            });
        }

        // Records never cross a sector boundary.
        var extent = new MemoryStream();
        foreach (byte[] record in records)
        {
            int used = (int)(extent.Length % SectorSize);
            if (used + record.Length > SectorSize)
            {
                extent.Write(new byte[SectorSize - used]);
            }

            extent.Write(record);
        }

        return PadToSectors(extent.ToArray());
    }

    private byte[] Record(int extent, long dataLength, byte flags, byte[] identifier, byte[] systemUse)
    {
        int padding = identifier.Length % 2 == 0 ? 1 : 0;
        int length = 33 + identifier.Length + padding + systemUse.Length;
        length += length % 2; // records have an even length
        if (length > 255)
        {
            throw new InvalidOperationException("A file name is too long for the disk image.");
        }

        var record = new byte[length];
        record[0] = (byte)length;
        BothEndian32(record.AsSpan(2), (uint)extent);
        BothEndian32(record.AsSpan(10), (uint)dataLength);
        RecordingDate(record.AsSpan(18));
        record[25] = flags;
        BothEndian16(record.AsSpan(28), 1); // volume sequence number
        record[32] = (byte)identifier.Length;
        identifier.CopyTo(record, 33);
        systemUse.CopyTo(record, 33 + identifier.Length + padding);
        return record;
    }

    /// <summary>The Rock Ridge entries of a directory record.</summary>
    private byte[] SystemUse(Node node, string? name, bool isRoot = false, int continuationSector = 0, int continuationLength = 0)
    {
        var entries = new List<byte>();
        if (isRoot)
        {
            entries.AddRange([(byte)'S', (byte)'P', 7, 1, 0xBE, 0xEF, 0]); // SUSP is in use
        }

        byte flags = HasPosixAttributes | HasTimestamps;
        if (name is not null)
        {
            flags |= HasName;
        }

        if (node is LinkNode)
        {
            flags |= HasSymbolicLink;
        }

        entries.AddRange([(byte)'R', (byte)'R', 5, 1, flags]);

        if (isRoot)
        {
            // The extension reference (ER) is too long for the record, so it lives in a continuation area.
            var continuation = new byte[28];
            "CE"u8.CopyTo(continuation);
            continuation[2] = 28;
            continuation[3] = 1;
            BothEndian32(continuation.AsSpan(4), (uint)continuationSector);
            BothEndian32(continuation.AsSpan(12), 0);
            BothEndian32(continuation.AsSpan(20), (uint)continuationLength);
            entries.AddRange(continuation);
        }

        var posix = new byte[36];
        "PX"u8.CopyTo(posix);
        posix[2] = 36;
        posix[3] = 1;
        BothEndian32(posix.AsSpan(4), node.Mode);
        BothEndian32(posix.AsSpan(12), node is DirectoryNode d ? (uint)(2 + d.Children.OfType<DirectoryNode>().Count()) : 1u);
        entries.AddRange(posix); // owner and group stay 0

        var timestamps = new byte[26];
        "TF"u8.CopyTo(timestamps);
        timestamps[2] = 26;
        timestamps[3] = 1;
        timestamps[4] = 0x0E; // modification, access and attribute-change times
        for (int i = 0; i < 3; i++)
        {
            RecordingDate(timestamps.AsSpan(5 + (i * 7)));
        }

        entries.AddRange(timestamps);

        if (name is not null)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(name);
            entries.AddRange([(byte)'N', (byte)'M', (byte)(5 + bytes.Length), 1, 0]);
            entries.AddRange(bytes);
        }

        if (node is LinkNode link)
        {
            var components = new List<byte>();
            foreach (string part in link.Target.Split('/'))
            {
                if (part.Length == 0)
                {
                    if (components.Count == 0)
                    {
                        components.AddRange([0x08, 0]); // leading "/": the root directory
                    }

                    continue;
                }

                byte[] bytes = Encoding.UTF8.GetBytes(part);
                components.AddRange([0, (byte)bytes.Length]);
                components.AddRange(bytes);
            }

            entries.AddRange([(byte)'S', (byte)'L', (byte)(5 + components.Count), 1, 0]);
            entries.AddRange(components);
        }

        return entries.ToArray();
    }

    private static byte[] ExtensionReference()
    {
        byte[] id = Encoding.ASCII.GetBytes(ExtensionId);
        byte[] description = Encoding.ASCII.GetBytes(ExtensionDescription);
        byte[] source = Encoding.ASCII.GetBytes(ExtensionSource);
        var entry = new List<byte> { (byte)'E', (byte)'R', (byte)(8 + id.Length + description.Length + source.Length), 1 };
        entry.AddRange([(byte)id.Length, (byte)description.Length, (byte)source.Length, 1]);
        entry.AddRange(id);
        entry.AddRange(description);
        entry.AddRange(source);
        return entry.ToArray();
    }

    private static byte[] PathTable(List<DirectoryNode> directories, bool bigEndian)
    {
        var table = new MemoryStream();
        foreach (DirectoryNode directory in directories)
        {
            byte[] identifier = directory.Parent is null ? [0x00] : Encoding.ASCII.GetBytes(directory.IsoName);
            var record = new byte[8 + identifier.Length + (identifier.Length % 2)];
            record[0] = (byte)identifier.Length;
            int parent = directory.Parent?.Number ?? 1;
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(2), (uint)directory.Extent);
                BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(6), (ushort)parent);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(2), (uint)directory.Extent);
                BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), (ushort)parent);
            }

            identifier.CopyTo(record, 8);
            table.Write(record);
        }

        return table.ToArray();
    }

    private byte[] PrimaryVolumeDescriptor(string volumeId, int totalSectors, int pathTableSize, int littleEndianTable, int bigEndianTable)
    {
        var descriptor = new byte[SectorSize];
        descriptor[0] = 1;
        "CD001"u8.CopyTo(descriptor.AsSpan(1));
        descriptor[6] = 1;
        Text(descriptor.AsSpan(8, 32), string.Empty);
        Text(descriptor.AsSpan(40, 32), volumeId);
        BothEndian32(descriptor.AsSpan(80), (uint)totalSectors);
        BothEndian16(descriptor.AsSpan(120), 1); // volume set size
        BothEndian16(descriptor.AsSpan(124), 1); // volume sequence number
        BothEndian16(descriptor.AsSpan(128), SectorSize);
        BothEndian32(descriptor.AsSpan(132), (uint)pathTableSize);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(140), (uint)littleEndianTable);
        BinaryPrimitives.WriteUInt32BigEndian(descriptor.AsSpan(148), (uint)bigEndianTable);
        Record(_root.Extent, _root.Size, DirectoryFlag, [0x00], []).CopyTo(descriptor, 156);
        Text(descriptor.AsSpan(190, 128), string.Empty); // volume set
        Text(descriptor.AsSpan(318, 128), string.Empty); // publisher
        Text(descriptor.AsSpan(446, 128), string.Empty); // data preparer
        Text(descriptor.AsSpan(574, 128), "AUTOKEY");    // application
        Text(descriptor.AsSpan(702, 37), string.Empty);  // copyright file
        Text(descriptor.AsSpan(739, 37), string.Empty);  // abstract file
        Text(descriptor.AsSpan(776, 37), string.Empty);  // bibliographic file
        VolumeDate(descriptor.AsSpan(813), _time);       // created
        VolumeDate(descriptor.AsSpan(830), _time);       // modified
        VolumeDate(descriptor.AsSpan(847), null);        // expires: never
        VolumeDate(descriptor.AsSpan(864), null);        // effective: now
        descriptor[881] = 1;                             // file structure version
        return descriptor;
    }

    private static byte[] Terminator()
    {
        var descriptor = new byte[SectorSize];
        descriptor[0] = 255;
        "CD001"u8.CopyTo(descriptor.AsSpan(1));
        descriptor[6] = 1;
        return descriptor;
    }

    private void RecordingDate(Span<byte> target)
    {
        target[0] = (byte)(_time.Year - 1900);
        target[1] = (byte)_time.Month;
        target[2] = (byte)_time.Day;
        target[3] = (byte)_time.Hour;
        target[4] = (byte)_time.Minute;
        target[5] = (byte)_time.Second;
        target[6] = 0; // UTC
    }

    private static void VolumeDate(Span<byte> target, DateTime? time)
    {
        Encoding.ASCII.GetBytes(time is DateTime t ? t.ToString("yyyyMMddHHmmss00") : "0000000000000000").CopyTo(target);
        target[16] = 0;
    }

    private static void Text(Span<byte> target, string text)
    {
        target.Fill((byte)' ');
        Encoding.ASCII.GetBytes(text).CopyTo(target);
    }

    private static void BothEndian16(Span<byte> target, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target, value);
        BinaryPrimitives.WriteUInt16BigEndian(target[2..], value);
    }

    private static void BothEndian32(Span<byte> target, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(target, value);
        BinaryPrimitives.WriteUInt32BigEndian(target[4..], value);
    }

    private static int SectorsFor(long bytes) => (int)((bytes + SectorSize - 1) / SectorSize);

    private static byte[] PadToSectors(byte[] data)
    {
        int padded = SectorsFor(data.Length) * SectorSize;
        if (padded == data.Length)
        {
            return data;
        }

        var result = new byte[padded];
        data.CopyTo(result, 0);
        return result;
    }

    private abstract class Node(string name)
    {
        public string Name { get; } = name;

        public string IsoName { get; set; } = string.Empty;

        public abstract uint Mode { get; }
    }

    private sealed class DirectoryNode(string name) : Node(name)
    {
        public List<Node> Children { get; } = [];

        public DirectoryNode? Parent { get; set; }

        public int Number { get; set; }

        public int Extent { get; set; }

        public int Size { get; set; }

        public override uint Mode => DirectoryMode;
    }

    private sealed class FileNode(string name, string source, long length, bool executable) : Node(name)
    {
        public string Source { get; } = source;

        public long Length { get; } = length;

        public int Extent { get; set; }

        public override uint Mode => executable ? ExecutableMode : RegularMode;
    }

    private sealed class LinkNode(string name, string target) : Node(name)
    {
        public string Target { get; } = target;

        public override uint Mode => SymbolicLinkMode;
    }
}
