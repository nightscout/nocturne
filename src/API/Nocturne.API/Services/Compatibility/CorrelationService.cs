using Microsoft.Extensions.Options;
using Nocturne.API.Configuration;

namespace Nocturne.API.Services.Compatibility;

/// <summary>
/// Generates and propagates correlation IDs for requests mirrored to the upstream Nightscout
/// instance by the compatibility proxy.
/// </summary>
/// <seealso cref="CorrelationService"/>
public interface ICorrelationService
{
    /// <summary>
    /// Generates a unique correlation ID for the current mirror request.
    /// Returns an empty string when correlation tracking is disabled in configuration.
    /// </summary>
    string GenerateCorrelationId();

    /// <summary>Returns the correlation ID stored in the current async-flow context, or <see langword="null"/> if none has been set.</summary>
    string? GetCurrentCorrelationId();

    /// <summary>Stores a correlation ID in the current async-flow context so downstream calls (e.g. Nightscout forwarding) can read it without passing it explicitly.</summary>
    void SetCorrelationId(string correlationId);
}

/// <summary>
/// <see cref="ICorrelationService"/> implementation that stores the active correlation ID in
/// an <see cref="System.Threading.AsyncLocal{T}"/> so it flows naturally across <c>await</c>
/// continuations without explicit parameter threading.
/// </summary>
/// <remarks>
/// Generated IDs follow the format <c>INT-yyyyMMdd-HHmmss-{uuid7}</c>.
/// When <see cref="Configuration.CompatibilityProxyConfiguration.EnableCorrelationTracking"/> is
/// <see langword="false"/>, <see cref="GenerateCorrelationId"/> returns an empty string and no
/// ID is stored or forwarded.
/// </remarks>
/// <seealso cref="ICorrelationService"/>
public class CorrelationService : ICorrelationService
{
    private static readonly AsyncLocal<string?> _correlationId = new();
    private readonly IOptions<CompatibilityProxyConfiguration> _configuration;
    private readonly TimeProvider _time;
    private readonly ILogger<CorrelationService> _logger;

    /// <summary>
    /// Initializes a new instance of the CorrelationService class
    /// </summary>
    /// <param name="configuration">Compatibility proxy configuration settings</param>
    /// <param name="timeProvider">Clock the generated IDs are stamped from</param>
    /// <param name="logger">Logger instance for this service</param>
    public CorrelationService(
        IOptions<CompatibilityProxyConfiguration> configuration,
        TimeProvider timeProvider,
        ILogger<CorrelationService> logger
    )
    {
        _configuration = configuration;
        _time = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public string GenerateCorrelationId()
    {
        if (!_configuration.Value.EnableCorrelationTracking)
        {
            return string.Empty;
        }

        var correlationId = $"INT-{_time.GetUtcNow():yyyyMMdd-HHmmss}-{Guid.CreateVersion7():N}";

        _logger.LogDebug("Generated correlation ID: {CorrelationId}", correlationId);

        return correlationId;
    }

    /// <inheritdoc />
    public string? GetCurrentCorrelationId()
    {
        return _correlationId.Value;
    }

    /// <inheritdoc />
    public void SetCorrelationId(string correlationId)
    {
        _correlationId.Value = correlationId;
        _logger.LogDebug("Set correlation ID in context: {CorrelationId}", correlationId);
    }
}
