using System.IO.Compression;
using Microsoft.AspNetCore.Mvc;
using Nocturne.Portal.API.Models;
using Nocturne.Portal.API.Services;

namespace Nocturne.Portal.API.Controllers;

/// <summary>
/// Generates docker-compose files for Nocturne deployment
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class GenerateController : ControllerBase
{
    private readonly DockerComposeGenerator _generator;
    private readonly ILogger<GenerateController> _logger;

    public GenerateController(DockerComposeGenerator generator, ILogger<GenerateController> logger)
    {
        _generator = generator;
        _logger = logger;
    }

    /// <summary>
    /// Generates docker-compose.yml and .env files based on user configuration
    /// </summary>
    /// <param name="request">Generation configuration</param>
    /// <returns>ZIP file containing docker-compose.yml and .env</returns>
    [HttpPost]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Generate([FromBody] GenerateRequest request)
    {
        try
        {
            // Validate request
            if (request.SetupType == "migrate" && request.Migration == null)
            {
                return BadRequest("Migration configuration required for 'migrate' setup type");
            }

            if (request.SetupType == "compatibility-proxy" && request.CompatibilityProxy == null)
            {
                return BadRequest("Compatibility proxy configuration required for 'compatibility-proxy' setup type");
            }

            if (!request.Postgres.UseContainer && string.IsNullOrEmpty(request.Postgres.ConnectionString))
            {
                return BadRequest("Connection string required when not using PostgreSQL container");
            }

            // Generate the files
            var (dockerCompose, envFile) = await _generator.GenerateAsync(request);

            // Create ZIP archive
            using var memoryStream = new MemoryStream();
            using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
            {
                var dockerComposeEntry = archive.CreateEntry("docker-compose.yml");
                using (var writer = new StreamWriter(dockerComposeEntry.Open()))
                {
                    await writer.WriteAsync(dockerCompose);
                }

                var envEntry = archive.CreateEntry(".env");
                using (var writer = new StreamWriter(envEntry.Open()))
                {
                    await writer.WriteAsync(envFile);
                }
            }

            memoryStream.Position = 0;
            return File(memoryStream.ToArray(), "application/zip", "nocturne-config.zip");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate configuration files");
            return StatusCode(500, "Failed to generate configuration files");
        }
    }
}
