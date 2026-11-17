using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Storage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace Nocturne.API.Controllers.V4.Account;

/// <summary>
/// Upload, serve, and delete the authenticated subject's avatar image.
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
    /// Decodes PNG, JPEG and WebP only, recognised by their bytes, so no other ImageSharp decoder ever
    /// parses an upload whatever content type the client claimed.
    /// </summary>
    private static readonly DecoderOptions Decoding = new()
    {
        Configuration = new SixLabors.ImageSharp.Configuration(
            new PngConfigurationModule(),
            new JpegConfigurationModule(),
            new WebpConfigurationModule()),
        MaxFrames = 1,
    };

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

        using var image = await DecodeAsync(file, ct);
        if (image is null)
            return Problem(detail: "File must be a PNG, JPEG, or WebP image of at most 40 megapixels", statusCode: 400, title: "Bad Request");

        image.Mutate(x => x
            .AutoOrient()
            .Resize(new ResizeOptions
            {
                Size = new Size(OutputSize, OutputSize),
                Mode = ResizeMode.Crop,
                Position = AnchorPositionMode.Center,
            }));

        // The avatar is served anonymously; the source's camera and location metadata must not travel with it.
        image.Metadata.ExifProfile = null;
        image.Metadata.XmpProfile = null;
        image.Metadata.IptcProfile = null;

        using var output = new MemoryStream();
        await image.SaveAsWebpAsync(output, new WebpEncoder { Quality = 80 }, ct);
        output.Position = 0;

        var avatarUrl = await avatarStore.SaveAsync(subjectId.Value, output, "image/webp", ct);

        logger.LogInformation("Avatar uploaded for subject {SubjectId}", subjectId.Value);

        return Ok(new AvatarUploadResponse { AvatarUrl = avatarUrl });
    }

    private static async Task<Image?> DecodeAsync(IFormFile file, CancellationToken ct)
    {
        using var input = new MemoryStream();
        await file.CopyToAsync(input, ct);
        input.Position = 0;

        try
        {
            // The header alone gives the dimensions, so an oversized image is refused before its pixels are allocated.
            var info = await Image.IdentifyAsync(Decoding, input, ct);
            if ((long)info.Width * info.Height > MaxSourcePixels)
                return null;

            input.Position = 0;
            return await Image.LoadAsync(Decoding, input, ct);
        }
        catch (ImageFormatException)
        {
            return null;
        }
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
