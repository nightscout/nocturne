using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Storage;
using SkiaSharp;

namespace Nocturne.API.Controllers.V4.Account;

/// <summary>
/// Profile pictures of accounts. A signed-in caller can upload and delete its own avatar. Anyone
/// can read the avatar of an account by its id, with no sign-in.
/// </summary>
[ApiController]
[Tags("Identity")]
[Authorize]
[Route("api/v4/me/avatar")]
public class AvatarController(IAvatarStore avatarStore, ILogger<AvatarController> logger) : ControllerBase
{
    private const int MaxUploadBytes = 5 * 1024 * 1024; // 5 MB
    private const int OutputSize = 256;
    private const long MaxSourcePixels = 40_000_000;

    /// <summary>
    /// Upload or replace the current subject's avatar. Image is resized to 256x256 WebP.
    /// </summary>
    [HttpPost]
    [DenyDemoSubject]
    [RemoteCommand]
    [RequestSizeLimit(MaxUploadBytes)]
    [ProducesResponseType(typeof(AvatarUploadResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AvatarUploadResponse>> Upload([FromForm] IFormFile file, CancellationToken ct)
    {
        var subjectId = HttpContext.GetSubjectId();
        if (subjectId is null) return Unauthorized();

        if (file.Length > MaxUploadBytes)
            return Problem(detail: "File exceeds 5 MB limit", statusCode: 400, title: "Bad Request");

        using var input = new MemoryStream();
        await file.CopyToAsync(input, ct);

        using var avatar = AvatarImage.Encode(input.ToArray(), OutputSize, MaxSourcePixels);
        if (avatar is null)
            return Problem(detail: "File must be a PNG, JPEG, or WebP image of at most 40 megapixels", statusCode: 400, title: "Bad Request");

        using var output = avatar.AsStream();
        var avatarUrl = await avatarStore.SaveAsync(subjectId.Value, output, "image/webp", ct);

        logger.LogInformation("Avatar uploaded for subject {SubjectId}", subjectId.Value);

        return Ok(new AvatarUploadResponse { AvatarUrl = avatarUrl });
    }

    /// <summary>
    /// Serve the current subject's avatar image.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Get([FromQuery] Guid? id, CancellationToken ct)
    {
        // If id is provided, serve that subject's avatar (public). Otherwise, serve the caller's.
        var subjectId = id ?? HttpContext.GetSubjectId();
        if (subjectId is null) return NotFound();

        var avatar = await avatarStore.GetAsync(subjectId.Value, ct);
        if (avatar is null) return NotFound();

        Response.Headers.CacheControl = "public, max-age=86400";
        return File(avatar.Data, avatar.ContentType);
    }

    /// <summary>
    /// Delete the current subject's avatar.
    /// </summary>
    [HttpDelete]
    [DenyDemoSubject]
    [RemoteCommand]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> Delete(CancellationToken ct)
    {
        var subjectId = HttpContext.GetSubjectId();
        if (subjectId is null) return Unauthorized();

        await avatarStore.DeleteAsync(subjectId.Value, ct);

        logger.LogInformation("Avatar deleted for subject {SubjectId}", subjectId.Value);

        return NoContent();
    }
}

public class AvatarUploadResponse
{
    public string AvatarUrl { get; set; } = string.Empty;
}

/// <summary>
/// Turns an uploaded picture into the stored avatar: a square WebP cut from the centre of the source.
/// </summary>
internal static class AvatarImage
{
    private const int WebpQuality = 80;

    /// <summary>
    /// Returns the encoded avatar, or null when <paramref name="upload"/> is not a whole PNG, JPEG or
    /// WebP image of at most <paramref name="maxSourcePixels"/> pixels. Only the pixels are redrawn,
    /// so none of the source's metadata (EXIF, XMP, IPTC) reaches the avatar, which is served
    /// anonymously.
    /// </summary>
    public static SKData? Encode(byte[] upload, int size, long maxSourcePixels)
    {
        // Skia also reads GIF, BMP, ICO and others; none of those decoders parses an upload,
        // whatever content type the client claimed.
        if (!IsPngJpegOrWebp(upload))
            return null;

        using var data = SKData.CreateCopy(upload);
        using var codec = SKCodec.Create(data);
        if (codec is null)
            return null;

        // The header alone gives the dimensions, so an oversized image is refused before its pixels are allocated.
        var source = codec.Info;
        if (source.Width <= 0 || source.Height <= 0 || (long)source.Width * source.Height > maxSourcePixels)
            return null;

        var decoded = new SKImageInfo(
            source.Width,
            source.Height,
            SKImageInfo.PlatformColorType,
            source.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul,
            source.ColorSpace);
        using var bitmap = new SKBitmap();
        if (!bitmap.TryAllocPixels(decoded) || codec.GetPixels(decoded, bitmap.GetPixels()) != SKCodecResult.Success)
            return null;

        using var surface = SKSurface.Create(
            new SKImageInfo(size, size, SKImageInfo.PlatformColorType, SKAlphaType.Premul, SKColorSpace.CreateSrgb()));
        if (surface is null)
            return null;

        // The centre square of the stored pixels is also the centre square of the picture as its
        // EXIF orientation displays it, so the crop is taken first and the orientation applied to
        // the square it is drawn into.
        var side = Math.Min(source.Width, source.Height);
        var left = (source.Width - side) / 2;
        var top = (source.Height - side) / 2;
        var crop = new SKRect(left, top, left + side, top + side);

        // Mipmaps average the pixels a large reduction skips; cubic keeps an enlargement smooth.
        var sampling = side > size
            ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
            : new SKSamplingOptions(SKCubicResampler.Mitchell);

        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(OrientationMatrix(codec.EncodedOrigin, size));
        using var image = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(image, crop, new SKRect(0, 0, size, size), sampling);

        using var result = surface.Snapshot();
        return result.Encode(SKEncodedImageFormat.Webp, WebpQuality);
    }

    private static bool IsPngJpegOrWebp(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])
        || bytes.StartsWith<byte>([0xFF, 0xD8, 0xFF])
        || (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8));

    /// <summary>
    /// Maps a square of <paramref name="size"/> holding the stored pixels onto the same square as
    /// the EXIF orientation displays it.
    /// </summary>
    private static SKMatrix OrientationMatrix(SKEncodedOrigin origin, float size) => origin switch
    {
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, size, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, size, 0, -1, size, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, size, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, size, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, size, -1, 0, size, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, size, 0, 0, 1),
        _ => SKMatrix.Identity,
    };
}
