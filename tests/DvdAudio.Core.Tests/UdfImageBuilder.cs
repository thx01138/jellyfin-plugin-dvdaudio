using System.Buffers.Binary;
using System.Text;

namespace DvdAudio.Core.Tests;

internal static class UdfImageBuilder
{
    public static byte[] Build(string discPath, byte[] data)
    {
        var parts = discPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            throw new InvalidOperationException("The test builder expects a single directory and file.");
        }

        const int partitionStart = 100;
        var dataSectors = Math.Max(1, (data.Length + 2047) / 2048);
        var sectorCount = Math.Max(257, partitionStart + 7 + dataSectors);
        var image = new byte[sectorCount * 2048];

        WriteTag(Sector(image, 256), 2, 256);
        WriteU32(Sector(image, 256), 16, 3 * 2048);
        WriteU32(Sector(image, 256), 20, 32);

        WriteTag(Sector(image, 32), 1, 32);
        WriteTag(Sector(image, 33), 5, 33);
        WriteU32(Sector(image, 33), 188, partitionStart);
        WriteU32(Sector(image, 33), 192, 1000);

        var logicalVolume = Sector(image, 34);
        WriteTag(logicalVolume, 6, 34);
        WriteU32(logicalVolume, 212, 2048);
        WriteU32(logicalVolume, 248, 2048);
        WriteU32(logicalVolume, 252, 0);
        WriteU32(logicalVolume, 264, 6);
        WriteU32(logicalVolume, 268, 1);
        logicalVolume[440] = 1;
        logicalVolume[441] = 6;
        logicalVolume[442] = 1;

        var fileIdentLength = FidLength(parts[1]);
        var directoryIdentLength = FidLength(parts[0]);
        WriteFileEntry(Sector(image, partitionStart + 1), (uint)(partitionStart + 1), fileType: 4, directoryIdentLength, adLocation: 2, (uint)directoryIdentLength);
        WriteFileEntry(Sector(image, partitionStart + 3), (uint)(partitionStart + 3), fileType: 4, fileIdentLength, adLocation: 4, (uint)fileIdentLength);
        WriteFileEntry(Sector(image, partitionStart + 5), (uint)(partitionStart + 5), fileType: 5, data.Length, adLocation: 6, (uint)data.Length);

        WriteFid(Sector(image, partitionStart + 2), parts[0], icbLogical: 3, directory: true);
        WriteFid(Sector(image, partitionStart + 4), parts[1], icbLogical: 5, directory: false);
        data.CopyTo(image, (partitionStart + 6) * 2048);

        var fileSet = Sector(image, partitionStart);
        WriteTag(fileSet, 256, (uint)partitionStart);
        WriteU32(fileSet, 400, 2048);
        WriteU32(fileSet, 404, 1);
        return image;
    }

    private static Span<byte> Sector(byte[] image, int sector) => image.AsSpan(sector * 2048, 2048);

    private static void WriteFileEntry(Span<byte> sector, uint tagLocation, byte fileType, long informationLength, uint adLocation, uint adLength)
    {
        WriteTag(sector, 261, tagLocation);
        sector[27] = fileType;
        WriteU64(sector, 56, (ulong)informationLength);
        WriteU32(sector, 172, 8);
        WriteU32(sector, 176, adLength);
        WriteU32(sector, 180, adLocation);
    }

    private static int WriteFid(Span<byte> sector, string name, uint icbLogical, bool directory)
    {
        var identifier = new byte[name.Length + 1];
        identifier[0] = 8;
        Encoding.ASCII.GetBytes(name).CopyTo(identifier, 1);
        WriteTag(sector, 257, 0);
        sector[18] = directory ? (byte)0x02 : (byte)0;
        sector[19] = (byte)identifier.Length;
        WriteU32(sector, 20, 2048);
        WriteU32(sector, 24, icbLogical);
        identifier.CopyTo(sector[38..]);
        return (38 + identifier.Length + 3) & ~3;
    }

    private static int FidLength(string name) => (38 + name.Length + 1 + 3) & ~3;

    private static void WriteTag(Span<byte> sector, ushort id, uint location)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(sector, id);
        sector[2] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(sector[12..], location);
    }

    private static void WriteU32(Span<byte> data, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data[offset..], value);
    }

    private static void WriteU64(Span<byte> data, int offset, ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(data[offset..], value);
    }
}
