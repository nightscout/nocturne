using System.Buffers.Binary;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Controllers.V4.Account;
using Nocturne.API.Extensions;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Storage;
using Nocturne.Core.Models.Authorization;
using SkiaSharp;

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
        var result = await UploadAsync(Encode(600, 400, SKEncodedImageFormat.Png), "image/png");

        result.Result.Should().BeOfType<OkObjectResult>();
        using var stored = SKCodec.Create(new MemoryStream(_saved!));
        stored.EncodedFormat.Should().Be(SKEncodedImageFormat.Webp);
        stored.Info.Size.Should().Be(new SKSizeI(256, 256));
    }

    [Fact]
    public async Task AWebp_IsAccepted()
    {
        var result = await UploadAsync(Encode(300, 300, SKEncodedImageFormat.Webp), "image/webp");

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task ABmpDeclaredAsPng_IsRefused()
    {
        var bmp = OnePixelBmp();
        AssertSkiaDecodes(bmp, SKEncodedImageFormat.Bmp);

        var result = await UploadAsync(bmp, "image/png");

        AssertRefused(result);
    }

    [Fact]
    public async Task AGifDeclaredAsPng_IsRefused()
    {
        AssertSkiaDecodes(OnePixelGif, SKEncodedImageFormat.Gif);

        var result = await UploadAsync(OnePixelGif, "image/png");

        AssertRefused(result);
    }

    [Fact]
    public async Task APngWhoseHeaderClaimsMoreThanThePixelBudget_IsRefused()
    {
        var result = await UploadAsync(WithPngDimensions(Encode(1, 1, SKEncodedImageFormat.Png), 8000, 6000), "image/png");

        AssertRefused(result);
        _store.Verify(
            s => s.SaveAsync(It.IsAny<Guid>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task APngCutShort_IsRefused()
    {
        var png = Encode(600, 400, SKEncodedImageFormat.Png);

        var result = await UploadAsync(png[..(png.Length / 2)], "image/png");

        AssertRefused(result);
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
        var jpeg = WithExif(Encode(300, 300, SKEncodedImageFormat.Jpeg), orientation: 1);
        jpeg.AsSpan().IndexOf(CameraMake).Should().BePositive();

        var result = await UploadAsync(jpeg, "image/jpeg");

        result.Result.Should().BeOfType<OkObjectResult>();
        _saved.AsSpan().IndexOf(CameraMake).Should().Be(-1);
        _saved.AsSpan().IndexOf("EXIF"u8).Should().Be(-1);
    }

    /// <summary>
    /// The source is twice as wide as it is high, and white outside its centre square. That square
    /// is red in its top left quarter, green in its top right one and blue below. Each case names
    /// the quarters of the avatar the red and the green land in once the orientation is applied.
    /// </summary>
    [Theory]
    [InlineData(1, "TL", "TR")]
    [InlineData(2, "TR", "TL")]
    [InlineData(3, "BR", "BL")]
    [InlineData(4, "BL", "BR")]
    [InlineData(5, "TL", "BL")]
    [InlineData(6, "TR", "BR")]
    [InlineData(7, "BR", "TR")]
    [InlineData(8, "BL", "TL")]
    public async Task TheSourcesExifOrientationIsApplied(int orientation, string red, string green)
    {
        using var source = new SKBitmap(400, 200);
        using (var canvas = new SKCanvas(source))
        using (var paint = new SKPaint())
        {
            canvas.Clear(SKColors.White);
            paint.Color = SKColors.Blue;
            canvas.DrawRect(100, 0, 200, 200, paint);
            paint.Color = SKColors.Red;
            canvas.DrawRect(100, 0, 100, 100, paint);
            paint.Color = new SKColor(0, 255, 0);
            canvas.DrawRect(200, 0, 100, 100, paint);
        }
        using var encoded = source.Encode(SKEncodedImageFormat.Jpeg, 100);

        var result = await UploadAsync(WithExif(encoded.ToArray(), orientation), "image/jpeg");

        result.Result.Should().BeOfType<OkObjectResult>();
        using var stored = SKBitmap.Decode(_saved!);
        var quarters = new[] { "TL", "TR", "BL", "BR" }.ToDictionary(
            quarter => quarter,
            quarter => Dominant(stored.GetPixel(quarter[1] == 'L' ? 64 : 192, quarter[0] == 'T' ? 64 : 192)));
        quarters.Where(q => q.Value == "red").Select(q => q.Key).Should().Equal(red);
        quarters.Where(q => q.Value == "green").Select(q => q.Key).Should().Equal(green);
        quarters.Values.Count(colour => colour == "blue").Should().Be(2);
    }

    private static string Dominant(SKColor color) =>
        color is { Red: > 180, Green: < 80, Blue: < 80 } ? "red"
        : color is { Green: > 180, Red: < 80, Blue: < 80 } ? "green"
        : color is { Blue: > 180, Red: < 80, Green: < 80 } ? "blue"
        : "mixed";

    /// <summary>
    /// Guards the fixture of a refusal test: Skia itself decodes it, so the refusal is the controller's.
    /// </summary>
    private static void AssertSkiaDecodes(byte[] bytes, SKEncodedImageFormat format)
    {
        using var codec = SKCodec.Create(new MemoryStream(bytes));
        codec.Should().NotBeNull();
        codec.EncodedFormat.Should().Be(format);
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

    private static byte[] Encode(int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var data = bitmap.Encode(format, 90);
        return data.ToArray();
    }

    private static ReadOnlySpan<byte> CameraMake => "Acme Camera"u8;

    private static readonly byte[] OnePixelGif =
    [
        0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, 0xFF, 0xFF, 0xFF,
        0x00, 0x00, 0x00, 0x21, 0xF9, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00, 0x2C, 0x00, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x01, 0x00, 0x00, 0x02, 0x02, 0x44, 0x01, 0x00, 0x3B,
    ];

    private static byte[] OnePixelBmp()
    {
        const int fileHeader = 14, infoHeader = 40, pixels = 4;
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write("BM"u8);
        writer.Write(fileHeader + infoHeader + pixels);
        writer.Write(0);
        writer.Write(fileHeader + infoHeader);
        writer.Write(infoHeader);
        writer.Write(1); // width
        writer.Write(1); // height
        writer.Write((short)1); // planes
        writer.Write((short)24); // bits per pixel
        writer.Write(0); // uncompressed
        writer.Write(pixels);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);
        writer.Write([0x00, 0x00, 0xFF, 0x00]);
        writer.Flush();
        return output.ToArray();
    }

    /// <summary>
    /// Inserts an EXIF segment holding a camera make and <paramref name="orientation"/> after the
    /// JFIF segment of an encoded JPEG.
    /// </summary>
    private static byte[] WithExif(byte[] jpeg, int orientation)
    {
        const int entries = 2, entrySize = 12, ifd = 8, makeData = ifd + 2 + entries * entrySize + 4;
        const ushort makeTag = 0x010F, orientationTag = 0x0112, ascii = 2, unsignedShort = 3;

        var tiff = new byte[makeData + CameraMake.Length + 1];
        tiff[0] = tiff[1] = (byte)'M';
        tiff[3] = 42;
        BinaryPrimitives.WriteInt32BigEndian(tiff.AsSpan(4), ifd);
        BinaryPrimitives.WriteInt16BigEndian(tiff.AsSpan(ifd), entries);

        var make = tiff.AsSpan(ifd + 2, entrySize);
        BinaryPrimitives.WriteUInt16BigEndian(make, makeTag);
        BinaryPrimitives.WriteUInt16BigEndian(make[2..], ascii);
        BinaryPrimitives.WriteInt32BigEndian(make[4..], CameraMake.Length + 1);
        BinaryPrimitives.WriteInt32BigEndian(make[8..], makeData);
        CameraMake.CopyTo(tiff.AsSpan(makeData));

        var oriented = tiff.AsSpan(ifd + 2 + entrySize, entrySize);
        BinaryPrimitives.WriteUInt16BigEndian(oriented, orientationTag);
        BinaryPrimitives.WriteUInt16BigEndian(oriented[2..], unsignedShort);
        BinaryPrimitives.WriteInt32BigEndian(oriented[4..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(oriented[8..], (ushort)orientation);

        ReadOnlySpan<byte> exifHeader = [(byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0];
        var segment = new byte[4 + exifHeader.Length + tiff.Length];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(segment.Length - 2));
        exifHeader.CopyTo(segment.AsSpan(4));
        tiff.CopyTo(segment.AsSpan(4 + exifHeader.Length));

        var at = 2;
        if (jpeg[2] == 0xFF && jpeg[3] == 0xE0)
            at += 2 + BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(4));

        return [.. jpeg[..at], .. segment, .. jpeg[at..]];
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
