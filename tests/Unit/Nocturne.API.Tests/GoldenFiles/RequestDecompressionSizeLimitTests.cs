using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Nocturne.API.Tests.GoldenFiles.Infrastructure;

namespace Nocturne.API.Tests.GoldenFiles;

public class RequestDecompressionSizeLimitTests : GoldenFileTestBase
{
    private const string Boundary = "sound-boundary";
    private const int CustomSoundLimitBytes = 512_000;

    public RequestDecompressionSizeLimitTests(GoldenFileWebAppFactory factory) : base(factory) { }

    [Fact]
    public async Task GzipBodyThatInflatesPastTheEndpointLimit_IsRefusedAtTheEndpointLimit()
    {
        var head = Encoding.ASCII.GetBytes(
            $"--{Boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"a.mp3\"\r\n"
            + "Content-Type: audio/mpeg\r\n\r\n");
        var tail = Encoding.ASCII.GetBytes($"\r\n--{Boundary}--\r\n");
        using var packed = new MemoryStream();
        await using (var gzip = new GZipStream(packed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            await gzip.WriteAsync(head);
            await gzip.WriteAsync(new byte[CustomSoundLimitBytes * 4]);
            await gzip.WriteAsync(tail);
        }
        packed.Length.Should().BeLessThan(CustomSoundLimitBytes);

        var content = new ByteArrayContent(packed.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data")
        {
            Parameters = { new NameValueHeaderValue("boundary", Boundary) },
        };
        content.Headers.ContentEncoding.Add("gzip");

        var response = await Client.PostAsync("/api/v4/alert-sounds", content);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain($"larger than the request body size limit {CustomSoundLimitBytes}");
    }
}
