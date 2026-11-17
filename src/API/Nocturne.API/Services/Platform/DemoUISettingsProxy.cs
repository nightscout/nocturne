using System.Text.Json;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.API.Services.Platform;

/// <summary>
/// The demo data service's ui-settings, read once per TTL and shared by every request.
/// </summary>
/// <remarks>
/// The demo service is one per deployment and its ui-settings route takes no tenant, so the answer
/// is instance-level and is held here rather than in <c>ITenantCache</c>. A demo tenant persists no
/// settings writes, so the answer changes only when the demo service does.
/// <para>
/// A failed read is remembered as well, for <see cref="FailureTtl"/>: without it a hung demo service
/// would cost every settings load the full <see cref="ProxyTimeout"/> before its fixtures were served.
/// Concurrent loads share the read in flight, and none of them cancels it for the others.
/// </para>
/// </remarks>
public sealed class DemoUISettingsProxy
{
    /// <summary>
    /// Deadline for the demo service's ui-settings read. The demo service is co-deployed and answers
    /// this route from memory, so anything slower than a few seconds is a hang, and the demo tenant
    /// has fixtures to fall back on. The unnamed <see cref="IHttpClientFactory"/> client carries no
    /// resilience handler, so without a deadline a hung demo service would hold the read open for
    /// <see cref="HttpClient"/>'s 100-second default.
    /// </summary>
    internal static readonly TimeSpan ProxyTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long a successful read is served. Long enough that a burst of visitors costs one call,
    /// short enough that a redeployed demo service shows within a minute.
    /// </summary>
    internal static readonly TimeSpan SuccessTtl = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long a failed read is remembered, so a demo service that is down costs one
    /// <see cref="ProxyTimeout"/> per window and one that recovers is used again soon.
    /// </summary>
    internal static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDemoModeService _demoMode;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<DemoUISettingsProxy> _logger;
    private readonly Lock _gate = new();
    private Task<string?>? _read;
    private DateTimeOffset _expiresAt;

    public DemoUISettingsProxy(
        IDemoModeService demoMode,
        IHttpClientFactory httpClientFactory,
        TimeProvider time,
        ILogger<DemoUISettingsProxy> logger)
    {
        _demoMode = demoMode;
        _httpClientFactory = httpClientFactory;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// A copy of the demo service's settings, or null when there is no demo service or its last
    /// read failed. The caller owns the copy and may change it.
    /// </summary>
    public async Task<UISettingsConfiguration?> GetAsync(CancellationToken ct)
    {
        if (!_demoMode.IsConfigured)
            return null;

        Task<string?> read;
        lock (_gate)
        {
            if (_read is null || (_read.IsCompleted && _time.GetUtcNow() >= _expiresAt))
                _read = ReadAsync();
            read = _read;
        }

        var body = await read.WaitAsync(ct);
        return body is null ? null : JsonSerializer.Deserialize<UISettingsConfiguration>(body, JsonOptions);
    }

    private async Task<string?> ReadAsync()
    {
        string? body = null;
        try
        {
            using var deadline = new CancellationTokenSource(ProxyTimeout, _time);
            using var response = await _httpClientFactory.CreateClient().GetAsync(
                $"{_demoMode.ServiceUrl?.TrimEnd('/')}/ui-settings",
                deadline.Token);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(deadline.Token);
            if (JsonSerializer.Deserialize<UISettingsConfiguration>(content, JsonOptions) is not null)
                body = content;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch UI settings from demo service, falling back to defaults");
        }

        lock (_gate)
            _expiresAt = _time.GetUtcNow() + (body is null ? FailureTtl : SuccessTtl);

        return body;
    }
}
