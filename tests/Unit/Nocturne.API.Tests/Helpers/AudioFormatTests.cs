using System.Text;
using Nocturne.API.Helpers;

namespace Nocturne.API.Tests.Helpers;

public class AudioFormatTests
{
    public static TheoryData<byte[], string> Recognised => new()
    {
        { [.. "ID3"u8, 0x04, 0x00, 0x00], "audio/mpeg" },
        { [0xFF, 0xFB, 0x90, 0x64], "audio/mpeg" },
        { [0xFF, 0xF3, 0x48, 0xC4], "audio/mpeg" },
        { [.. "RIFF"u8, 0x24, 0x08, 0x00, 0x00, .. "WAVE"u8, .. "fmt "u8], "audio/wav" },
        { [.. "OggS"u8, 0x00, 0x02], "audio/ogg" },
        { [.. "fLaC"u8, 0x00, 0x00, 0x00, 0x22], "audio/flac" },
        { [0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42], "audio/webm" },
        { [0x00, 0x00, 0x00, 0x20, .. "ftypM4A "u8], "audio/mp4" },
        { [0xFF, 0xF1, 0x50, 0x80], "audio/aac" },
        { [0xFF, 0xF9, 0x50, 0x80], "audio/aac" },
    };

    [Theory]
    [MemberData(nameof(Recognised))]
    public void NamesTheContainerByItsLeadingBytes(byte[] data, string expected)
    {
        AudioFormat.Detect(data).Should().Be(expected);
    }

    public static TheoryData<byte[]> Refused => new(
        Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>"),
        Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"),
        Encoding.Latin1.GetBytes("RIFF\x24\x08\0\0AVI "),
        Encoding.Latin1.GetBytes("\x89PNG\r\n\x1A\n"),
        Encoding.Latin1.GetBytes("\xFF\xE9\0\0"),
        Encoding.Latin1.GetBytes("\xFF\xE1\0\0"),
        Encoding.Latin1.GetBytes("\xFF\xFB"),
        []);

    [Theory]
    [MemberData(nameof(Refused))]
    public void RefusesAnythingElse(byte[] data)
    {
        AudioFormat.Detect(data).Should().BeNull();
    }
}
