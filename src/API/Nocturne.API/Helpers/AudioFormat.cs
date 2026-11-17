namespace Nocturne.API.Helpers;

/// <summary>
/// Names the audio container a byte payload holds by its leading bytes, never by the type a client
/// declared: stored audio is served back from the API origin, so the content type it carries has to
/// describe what the bytes actually are.
/// </summary>
internal static class AudioFormat
{
    /// <summary>
    /// The MIME type for <paramref name="data"/>, or <c>null</c> when it is not MP3, WAV, Ogg, FLAC,
    /// WebM, MP4/M4A or ADTS AAC.
    /// </summary>
    public static string? Detect(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            return null;

        if (data.StartsWith("ID3"u8))
            return "audio/mpeg";
        if (data.Length >= 12 && data.StartsWith("RIFF"u8) && data[8..12].SequenceEqual("WAVE"u8))
            return "audio/wav";
        if (data.StartsWith("OggS"u8))
            return "audio/ogg";
        if (data.StartsWith("fLaC"u8))
            return "audio/flac";
        if (data.StartsWith((ReadOnlySpan<byte>)[0x1A, 0x45, 0xDF, 0xA3]))
            return "audio/webm";
        if (data.Length >= 8 && data[4..8].SequenceEqual("ftyp"u8))
            return "audio/mp4";

        // An MPEG audio frame sync: eleven set bits, then the version and layer. Layer 00 is ADTS AAC
        // (whose version bits name MPEG-4 or MPEG-2); layers 01-11 are MPEG-1/2 audio.
        if (data[0] == 0xFF && (data[1] & 0xE0) == 0xE0)
        {
            var layer = (data[1] >> 1) & 0b11;
            if (layer == 0)
                return (data[1] & 0b1_0000) != 0 ? "audio/aac" : null;
            return (data[1] & 0b1_1000) != 0b0_1000 ? "audio/mpeg" : null;
        }

        return null;
    }
}
