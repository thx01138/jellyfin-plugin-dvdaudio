namespace DvdAudio.Core;

public readonly record struct FileExtent(long Sector, long Length);

public sealed class ImageFile
{
    public ImageFile(string fullPath, long length, IReadOnlyList<FileExtent> extents, string? localPath = null, byte[]? inlineData = null)
    {
        FullPath = fullPath;
        Length = length;
        Extents = extents;
        LocalPath = localPath;
        InlineData = inlineData;
    }

    /// <summary>Absolute disc path such as <c>/AUDIO_TS/ATS_01_0.IFO</c>.</summary>
    public string FullPath { get; }

    public long Length { get; }

    public IReadOnlyList<FileExtent> Extents { get; }

    /// <summary>Set when the disc is a folder rip rather than an image.</summary>
    public string? LocalPath { get; }

    public byte[]? InlineData { get; }
}
