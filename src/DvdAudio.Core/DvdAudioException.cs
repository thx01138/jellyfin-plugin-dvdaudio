namespace DvdAudio.Core;

public sealed class DvdAudioException : Exception
{
    public DvdAudioException(string message)
        : base(message)
    {
    }

    public DvdAudioException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
