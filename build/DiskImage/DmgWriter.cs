using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace AutoKey.Build.DiskImage;

/// <summary>
/// Wraps a raw disk image (such as one from <see cref="IsoImageWriter"/>) in Apple's UDIF format, zlib-compressed
/// ("UDZO"): the usual .dmg. The layout follows libdmg's whole-disk images: one block table with 256 KiB chunks,
/// CRC32 checksums, an XML property list and the 512-byte "koly" trailer.
/// </summary>
public static class DmgWriter
{
    private const int SectorSize = 512;
    private const int SectorsPerChunk = 0x200;
    private const uint DecompressBufferRequested = 0x208;
    private const uint EntireDevice = 0xFFFFFFFE;
    private const uint ChecksumCrc32 = 2;
    private const uint ChunkRaw = 0x00000001;
    private const uint ChunkZlib = 0x80000005;
    private const uint ChunkTerminator = 0xFFFFFFFF;

    public static void Write(string rawImagePath, string dmgPath)
    {
        using FileStream input = File.OpenRead(rawImagePath);
        if (input.Length % SectorSize != 0)
        {
            throw new InvalidOperationException("The raw image must be a whole number of 512-byte sectors.");
        }

        ulong sectorCount = (ulong)input.Length / SectorSize;
        using FileStream output = File.Create(dmgPath);

        var chunks = new List<Chunk>();
        uint imageCrc = 0;    // checksum of the uncompressed image (block table)
        uint dataForkCrc = 0; // checksum of what's stored (data fork)
        var buffer = new byte[SectorsPerChunk * SectorSize];
        ulong sector = 0;
        while (sector < sectorCount)
        {
            ulong count = Math.Min(SectorsPerChunk, sectorCount - sector);
            int length = (int)count * SectorSize;
            input.ReadExactly(buffer, 0, length);
            imageCrc = Crc32.Append(imageCrc, buffer.AsSpan(0, length));

            byte[] compressed = Compress(buffer.AsSpan(0, length));
            ulong offset = (ulong)output.Position;
            if (compressed.Length >= length)
            {
                output.Write(buffer, 0, length);
                dataForkCrc = Crc32.Append(dataForkCrc, buffer.AsSpan(0, length));
                chunks.Add(new Chunk(ChunkRaw, sector, count, offset, (ulong)length));
            }
            else
            {
                output.Write(compressed);
                dataForkCrc = Crc32.Append(dataForkCrc, compressed);
                chunks.Add(new Chunk(ChunkZlib, sector, count, offset, (ulong)compressed.Length));
            }

            sector += count;
        }

        ulong dataForkLength = (ulong)output.Position;
        chunks.Add(new Chunk(ChunkTerminator, sector, 0, dataForkLength, 0));

        byte[] blockTable = BlockTable(sectorCount, imageCrc, chunks);
        byte[] plist = PropertyList(blockTable);
        output.Write(plist);

        // The master checksum covers the checksums of all block tables (here just one), big-endian.
        var blockChecksums = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(blockChecksums, imageCrc);
        uint masterCrc = Crc32.Append(0, blockChecksums);

        output.Write(Trailer(dataForkLength, dataForkCrc, xmlOffset: dataForkLength, (ulong)plist.Length, masterCrc, sectorCount));
    }

    private static byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return compressed.ToArray();
    }

    /// <summary>The "mish" block table: which compressed chunk holds which sectors.</summary>
    private static byte[] BlockTable(ulong sectorCount, uint imageCrc, List<Chunk> chunks)
    {
        var table = new byte[204 + (chunks.Count * 40)];
        Span<byte> s = table;
        "mish"u8.CopyTo(s);
        BinaryPrimitives.WriteUInt32BigEndian(s[4..], 1);                         // version
        BinaryPrimitives.WriteUInt64BigEndian(s[8..], 0);                         // first sector
        BinaryPrimitives.WriteUInt64BigEndian(s[16..], sectorCount);
        BinaryPrimitives.WriteUInt64BigEndian(s[24..], 0);                        // data offset
        BinaryPrimitives.WriteUInt32BigEndian(s[32..], DecompressBufferRequested);
        BinaryPrimitives.WriteUInt32BigEndian(s[36..], EntireDevice);             // blocks descriptor
        BinaryPrimitives.WriteUInt32BigEndian(s[64..], ChecksumCrc32);
        BinaryPrimitives.WriteUInt32BigEndian(s[68..], 32);                       // checksum size in bits
        BinaryPrimitives.WriteUInt32BigEndian(s[72..], imageCrc);
        BinaryPrimitives.WriteUInt32BigEndian(s[200..], (uint)chunks.Count);
        for (int i = 0; i < chunks.Count; i++)
        {
            Span<byte> entry = s[(204 + (i * 40))..];
            BinaryPrimitives.WriteUInt32BigEndian(entry, chunks[i].Type);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], 0);                 // comment
            BinaryPrimitives.WriteUInt64BigEndian(entry[8..], chunks[i].Sector);
            BinaryPrimitives.WriteUInt64BigEndian(entry[16..], chunks[i].SectorCount);
            BinaryPrimitives.WriteUInt64BigEndian(entry[24..], chunks[i].Offset);
            BinaryPrimitives.WriteUInt64BigEndian(entry[32..], chunks[i].Length);
        }

        return table;
    }

    private static byte[] PropertyList(byte[] blockTable)
    {
        string data = Convert.ToBase64String(blockTable, Base64FormattingOptions.InsertLineBreaks).Replace("\r\n", "\n");
        const string name = "whole disk (unknown partition : 0)";
        string plist =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
            "<plist version=\"1.0\">\n" +
            "<dict>\n" +
            "\t<key>resource-fork</key>\n" +
            "\t<dict>\n" +
            "\t\t<key>blkx</key>\n" +
            "\t\t<array>\n" +
            "\t\t\t<dict>\n" +
            "\t\t\t\t<key>Attributes</key>\n" +
            "\t\t\t\t<string>0x0050</string>\n" +
            "\t\t\t\t<key>CFName</key>\n" +
            $"\t\t\t\t<string>{name}</string>\n" +
            "\t\t\t\t<key>Data</key>\n" +
            $"\t\t\t\t<data>\n{data}\n\t\t\t\t</data>\n" +
            "\t\t\t\t<key>ID</key>\n" +
            "\t\t\t\t<string>0</string>\n" +
            "\t\t\t\t<key>Name</key>\n" +
            $"\t\t\t\t<string>{name}</string>\n" +
            "\t\t\t</dict>\n" +
            "\t\t</array>\n" +
            "\t</dict>\n" +
            "</dict>\n" +
            "</plist>\n";
        return Encoding.UTF8.GetBytes(plist);
    }

    /// <summary>The 512-byte "koly" trailer at the very end of every .dmg.</summary>
    private static byte[] Trailer(ulong dataForkLength, uint dataForkCrc, ulong xmlOffset, ulong xmlLength, uint masterCrc, ulong sectorCount)
    {
        var trailer = new byte[512];
        Span<byte> s = trailer;
        "koly"u8.CopyTo(s);
        BinaryPrimitives.WriteUInt32BigEndian(s[4..], 4);               // version
        BinaryPrimitives.WriteUInt32BigEndian(s[8..], 512);             // header size
        BinaryPrimitives.WriteUInt32BigEndian(s[12..], 1);              // flags: flattened
        BinaryPrimitives.WriteUInt64BigEndian(s[16..], 0);              // running data fork offset
        BinaryPrimitives.WriteUInt64BigEndian(s[24..], 0);              // data fork offset
        BinaryPrimitives.WriteUInt64BigEndian(s[32..], dataForkLength);
        BinaryPrimitives.WriteUInt64BigEndian(s[40..], 0);              // resource fork offset
        BinaryPrimitives.WriteUInt64BigEndian(s[48..], 0);              // resource fork length
        BinaryPrimitives.WriteUInt32BigEndian(s[56..], 1);              // segment number
        BinaryPrimitives.WriteUInt32BigEndian(s[60..], 1);              // segment count
        RandomNumberGenerator.Fill(s.Slice(64, 16));                    // segment id
        BinaryPrimitives.WriteUInt32BigEndian(s[80..], ChecksumCrc32);  // data fork checksum
        BinaryPrimitives.WriteUInt32BigEndian(s[84..], 32);
        BinaryPrimitives.WriteUInt32BigEndian(s[88..], dataForkCrc);
        BinaryPrimitives.WriteUInt64BigEndian(s[216..], xmlOffset);
        BinaryPrimitives.WriteUInt64BigEndian(s[224..], xmlLength);
        BinaryPrimitives.WriteUInt32BigEndian(s[352..], ChecksumCrc32); // master checksum
        BinaryPrimitives.WriteUInt32BigEndian(s[356..], 32);
        BinaryPrimitives.WriteUInt32BigEndian(s[360..], masterCrc);
        BinaryPrimitives.WriteUInt32BigEndian(s[488..], 1);             // image variant: device image
        BinaryPrimitives.WriteUInt64BigEndian(s[492..], sectorCount);
        return trailer;
    }

    private readonly record struct Chunk(uint Type, ulong Sector, ulong SectorCount, ulong Offset, ulong Length);
}

/// <summary>The standard CRC-32 (as in zlib), which UDIF uses for its checksums.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = CreateTable();

    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        crc = ~crc;
        foreach (byte b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }
}
