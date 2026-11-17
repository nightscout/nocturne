using System.Buffers.Binary;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Controllers.V4.Account;
using Nocturne.API.Extensions;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Storage;
using Nocturne.Core.Models.Authorization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

namespace Nocturne.API.Tests.Controllers.V4.Account;

public class AvatarControllerUploadTests
{
    private readonly Mock<IAvatarStore> _store = new();
    private byte[]? _saved;

    public AvatarControllerUploadTests()
    {
        _store
            .Setup(s => s.SaveAsync(It.IsAny<Guid>(), It.IsAny<Stream>(), "image/webp", It.IsAny<CancellationToken>()))
            .Callback<Guid, Stream, string, CancellationToken>((_, stream, _, _) =>
            {
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                _saved = copy.ToArray();
            })
            .ReturnsAsync("/api/v4/me/avatar");
    }

    [Fact]
    public async Task APng_IsStoredAs256SquareWebp()
    {
        var result = await UploadAsync(Encode(600, 400, new PngEncoder()), "image/png");

        result.Result.Should().BeOfType<OkObjectResult>();
        using var stored = Image.Load(_saved!);
        stored.Metadata.DecodedImageFormat!.Name.Should().Be("Webp");
        stored.Size.Should().Be(new Size(256, 256));
    }

    [Fact]
    public async Task ATiffDeclaredAsPng_IsRefused()
    {
        var result = await UploadAsync(Encode(64, 64, new TiffEncoder()), "image/png");

        AssertRefused(result);
    }

    [Fact]
    public async Task AGifDeclaredAsPng_IsRefused()
    {
        var result = await UploadAsync(Encode(64, 64, new GifEncoder()), "image/png");

        AssertRefused(result);
    }

    [Fact]
    public async Task APngWhoseHeaderClaimsMoreThanThePixelBudget_IsRefused()
    {
        var result = await UploadAsync(WithPngDimensions(Encode(1, 1, new PngEncoder()), 8000, 6000), "image/png");

        AssertRefused(result);
        _store.Verify(
            s => s.SaveAsync(It.IsAny<Guid>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task BytesThatAreNoImage_AreRefused()
    {
        var result = await UploadAsync("<html></html>"u8.ToArray(), "image/png");

        AssertRefused(result);
    }

    [Fact]
    public async Task TheSourcesExifIsNotCarriedIntoTheStoredAvatar()
    {
        var jpeg = Encode(300, 300, new JpegEncoder(), image =>
        {
            image.Metadata.ExifProfile = new ExifProfile();
            image.Metadata.ExifProfile.SetValue(ExifTag.Make, "Acme Camera");
            image.Metadata.ExifProfile.SetValue(ExifTag.GPSLatitudeRef, "N");
        });

        var result = await UploadAsync(jpeg, "image/jpeg");

        result.Result.Should().BeOfType<OkObjectResult>();
        using var stored = Image.Load(_saved!);
        stored.Metadata.ExifProfile.Should().BeNull();
    }

    private static void AssertRefused(ActionResult<AvatarUploadResponse> result) =>
        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);

    private async Task<ActionResult<AvatarUploadResponse>> UploadAsync(byte[] bytes, string contentType)
    {
        var http = new DefaultHttpContext();
        http.Items[AuthContextKeys.AuthContext] = new AuthContext { IsAuthenticated = true, SubjectId = Guid.NewGuid() };
        var controller = new AvatarController(_store.Object, NullLogger<AvatarController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            ProblemDetailsFactory = new EchoingProblemDetailsFactory(),
        };

        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "avatar")
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };

        return await controller.Upload(file, CancellationToken.None);
    }

    private static byte[] Encode(int width, int height, IImageEncoder encoder, Action<Image>? configure = null)
    {
        using var image = new Image<Rgba32>(width, height);
        configure?.Invoke(image);
        using var output = new MemoryStream();
        image.Save(output, encoder);
        return output.ToArray();
    }

    /// <summary>
    /// Rewrites the IHDR dimensions (and its CRC) of an encoded PNG, so a header can claim a size
    /// the test never allocates.
    /// </summary>
    private static byte[] WithPngDimensions(byte[] png, int width, int height)
    {
        const int ihdrType = 12, ihdrData = 16, ihdrDataLength = 13;
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(ihdrData), width);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(ihdrData + 4), height);
        var crc = Crc32(png.AsSpan(ihdrType, 4 + ihdrDataLength));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(ihdrData + ihdrDataLength), crc);
        return png;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}
