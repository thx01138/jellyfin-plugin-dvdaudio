using System.Text;

namespace DvdAudio.Core.Tests;

internal static class IsoImageBuilder
{
    private sealed class Node
    {
        public string Name { get; init; } = string.Empty;

        public byte[]? Data { get; init; }

        public List<Node> Children { get; } = [];

        public int Sector { get; set; }
    }

    public static byte[] Build(params (string Path, byte[] Data)[] files)
    {
        var root = new Node();
        foreach (var (path, data) in files)
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            for (var i = 0; i < parts.Length; i++)
            {
                var existing = current.Children.FirstOrDefault(child => child.Name.Equals(parts[i], StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    existing = new Node
                    {
                        Name = parts[i],
                        Data = i == parts.Length - 1 ? data : null
                    };
                    current.Children.Add(existing);
                }

                current = existing;
            }
        }

        var next = 17;
        Assign(root, ref next);
        var image = new byte[next * 2048];
        WriteNode(image, root, parent: null);
        WritePrimaryVolume(image, root, next);
        return image;
    }

    private static void Assign(Node node, ref int next)
    {
        if (node.Data is not null)
        {
            node.Sector = next;
            next += Math.Max(1, (node.Data.Length + 2047) / 2048);
            return;
        }

        node.Sector = next++;
        foreach (var child in node.Children)
        {
            Assign(child, ref next);
        }
    }

    private static void WriteNode(byte[] image, Node node, Node? parent)
    {
        if (node.Data is not null)
        {
            node.Data.CopyTo(image, node.Sector * 2048);
            return;
        }

        using var directory = new MemoryStream();
        WriteRecord(directory, node.Sector, DirectoryLength(node), directoryRecord: true, [0]);
        WriteRecord(directory, parent?.Sector ?? node.Sector, DirectoryLength(parent ?? node), directoryRecord: true, [1]);
        foreach (var child in node.Children)
        {
            var length = child.Data?.Length ?? DirectoryLength(child);
            WriteRecord(directory, child.Sector, length, child.Data is null, Encoding.ASCII.GetBytes(child.Name));
        }

        directory.ToArray().CopyTo(image, node.Sector * 2048);
        foreach (var child in node.Children)
        {
            WriteNode(image, child, node);
        }
    }

    private static int DirectoryLength(Node node)
    {
        var size = RecordSize(1) + RecordSize(1);
        foreach (var child in node.Children)
        {
            size += RecordSize(child.Name.Length);
        }

        return size;
    }

    private static int RecordSize(int nameLength)
    {
        var size = 33 + nameLength;
        return (size & 1) == 0 ? size : size + 1;
    }

    private static void WriteRecord(Stream output, long sector, long length, bool directoryRecord, byte[] name)
    {
        var record = new byte[RecordSize(name.Length)];
        record[0] = (byte)record.Length;
        WriteBoth32(record, 2, (uint)sector);
        WriteBoth32(record, 10, (uint)length);
        record[25] = directoryRecord ? (byte)0x02 : (byte)0;
        record[28] = 1;
        record[31] = 1;
        record[32] = (byte)name.Length;
        name.CopyTo(record, 33);
        output.Write(record);
    }

    private static void WritePrimaryVolume(byte[] image, Node root, int sectorCount)
    {
        var volume = image.AsSpan(16 * 2048, 2048);
        volume[0] = 1;
        "CD001"u8.CopyTo(volume[1..]);
        volume[6] = 1;
        WriteBoth32(volume, 80, (uint)sectorCount);
        volume[128] = 0x00;
        volume[129] = 0x08;
        volume[130] = 0x08;
        volume[131] = 0x00;
        using var rootRecord = new MemoryStream();
        WriteRecord(rootRecord, root.Sector, DirectoryLength(root), directoryRecord: true, [0]);
        rootRecord.ToArray().CopyTo(volume[156..]);
    }

    private static void WriteBoth32(Span<byte> data, int offset, uint value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
        data[offset + 3] = (byte)(value >> 24);
        data[offset + 4] = (byte)(value >> 24);
        data[offset + 5] = (byte)(value >> 16);
        data[offset + 6] = (byte)(value >> 8);
        data[offset + 7] = (byte)value;
    }
}
