using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using Nocturne.API.Configuration;
using Nocturne.API.Models.Compatibility;
using Nocturne.API.Services.Compatibility;

namespace Nocturne.API.Middleware;

/// <summary>
/// Middleware that intercepts GET requests on v1/v2/v3 API paths, lets Nocturne respond
/// normally, then fires a background comparison against the upstream Nightscout instance.
/// No latency is added to the client response.
/// </summary>
/// <remarks>
/// <para>
/// Runs after all authentication and authorization middleware, immediately before endpoint
/// execution. Controlled by <see cref="CompatibilityProxyConfiguration.Enabled"/>.
/// </para>
/// <para>
/// Uses <see cref="IRequestCloningService"/> to snapshot the request before the pipeline
/// consumes it, <see cref="IRequestForwardingService"/> to send the clone to Nightscout,
/// <see cref="IResponseComparisonService"/> to diff the responses, and
/// <see cref="IDiscrepancyPersistenceService"/> to store any discrepancies.
/// </para>
/// </remarks>
/// <seealso cref="CompatibilityProxyConfiguration"/>
/// <seealso cref="IRequestCloningService"/>
/// <seealso cref="IResponseComparisonService"/>
public class CompatibilityProxyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<CompatibilityProxyMiddleware> _logger;

    /// <summary>
    /// Maximum response size (in bytes) that will be captured for background comparison.
    /// Responses larger than this are returned to the client but skipped for comparison.
    /// </summary>
    private const int MaxResponseSizeBytes = 10 * 1024 * 1024; // 10 MB

    /// <summary>
    /// Creates a new instance of <see cref="CompatibilityProxyMiddleware"/>.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="logger">Logger for proxy and comparison diagnostics.</param>
    public CompatibilityProxyMiddleware(
        RequestDelegate next,
        ILogger<CompatibilityProxyMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Processes an HTTP request through the compatibility proxy pipeline.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>A task that completes when the middleware has finished processing.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        // Only intercept GET requests on v1/v2/v3 API paths
        if (!ShouldIntercept(context.Request))
        {
            await _next(context);
            return;
        }

        // Check if the proxy is enabled
        var configuration = context.RequestServices.GetRequiredService<IOptions<CompatibilityProxyConfiguration>>();
        if (!configuration.Value.Enabled)
        {
            await _next(context);
            return;
        }

        // Clone the request BEFORE calling next (body may be consumed)
        var cloningService = context.RequestServices.GetRequiredService<IRequestCloningService>();
        var clonedRequest = await cloningService.CloneRequestAsync(context.Request);

        // Capture the path before the background task (HttpContext will be disposed)
        // Sanitize to prevent log forging (strip newlines from user-controlled value)
        var path = context.Request.Path.ToString().Replace("\r", "").Replace("\n", "");

        // Swap response body with a memory stream to capture Nocturne's response
        var originalBody = context.Response.Body;
        using var memoryStream = new MemoryStream();
        context.Response.Body = memoryStream;

        var stopwatch = Stopwatch.StartNew();
        await _next(context);
        stopwatch.Stop();

        // Capture response details before restoring the original stream
        var responseBytes = memoryStream.ToArray();
        var statusCode = context.Response.StatusCode;
        var contentType = context.Response.ContentType;

        // Restore original stream and flush captured bytes to client
        context.Response.Body = originalBody;
        if (responseBytes.Length > 0)
        {
            await originalBody.WriteAsync(responseBytes);
        }

        // Skip comparison for oversized responses
        if (responseBytes.Length > MaxResponseSizeBytes)
        {
            _logger.LogDebug(
                "Skipping compatibility comparison for {Path}: response size {Size} exceeds {Max} byte limit",
                path, responseBytes.Length, MaxResponseSizeBytes);
            return;
        }

        // Resolve the scope factory BEFORE the background task starts.
        // Do NOT capture HttpContext in the closure — it is disposed after the request completes.
        var scopeFactory = context.RequestServices.GetRequiredService<IServiceScopeFactory>();
        var nocturneResponseTimeMs = stopwatch.ElapsedMilliseconds;

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var forwardingService = scope.ServiceProvider.GetRequiredService<IRequestForwardingService>();
                var comparisonService = scope.ServiceProvider.GetRequiredService<IResponseComparisonService>();
                var persistenceService = scope.ServiceProvider.GetRequiredService<IDiscrepancyPersistenceService>();

                // Forward to Nightscout
                var nightscoutResponse = await forwardingService.ForwardToNightscoutAsync(
                    clonedRequest, CancellationToken.None);

                // Build Nocturne's response model
                var nocturneResponse = new TargetResponse
                {
                    Target = "Nocturne",
                    StatusCode = statusCode,
                    Body = responseBytes,
                    ContentType = contentType,
                    IsSuccess = statusCode >= 200 && statusCode < 300,
                    ResponseTimeMs = nocturneResponseTimeMs,
                };

                // Compare
                var traceId = Guid.NewGuid().ToString();
                var comparisonResult = await comparisonService.CompareResponsesAsync(
                    nightscoutResponse, nocturneResponse, traceId, path);

                // Build the proxy response wrapper for persistence
                var proxyResponse = new CompatibilityProxyResponse
                {
                    NightscoutResponse = nightscoutResponse,
                    NocturneResponse = nocturneResponse,
                    TraceId = traceId,
                    ComparisonResult = comparisonResult,
                    SelectionReason = "Nocturne is the primary responder",
                    SelectedResponse = nocturneResponse,
                };

                // Persist
                await persistenceService.StoreAnalysisAsync(
                    comparisonResult, proxyResponse, "GET", path, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background compatibility comparison failed for {Path}", path);
            }
        });
    }

    private static bool ShouldIntercept(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method))
            return false;

        var path = request.Path.Value;
        if (string.IsNullOrEmpty(path))
            return false;

        return path.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/v2/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/v3/", StringComparison.OrdinalIgnoreCase);
    }
}
