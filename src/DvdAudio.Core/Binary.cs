using System.Buffers.Binary;

namespace DvdAudio.Core;

internal static class Binary
{
    public static ushort Be16(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
    }

    public static uint Be32(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
    }

    public static ushort Le16(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
    }

    public static uint Le32(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
    }

    public static ulong Le64(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
    }

    /// <summary>
    /// ISO 9660 stores 32-bit values as little-endian followed by big-endian.
    /// </summary>
    public static uint Both32(ReadOnlySpan<byte> data, int offset)
    {
        return Le32(data, offset);
    }

    public static bool Fits(ReadOnlySpan<byte> data, int offset, int size)
    {
        return offset >= 0 && size >= 0 && (uint)offset + (uint)size <= (uint)data.Length;
    }
}
