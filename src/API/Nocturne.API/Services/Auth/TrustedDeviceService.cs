using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.API.Services.Auth;

/// <inheritdoc cref="ITrustedDeviceService"/>
/// <remarks>
/// The token is 32 random bytes and the store keeps only its SHA-256, which needs no salt or
/// stretching at that entropy. The lookup is by hash, so nothing is compared in variable time.
/// </remarks>
public class TrustedDeviceService : ITrustedDeviceService
{
    private const int TokenLengthBytes = 32;

    private readonly ITrustedDeviceRepository _devices;
    private readonly TimeProvider _clock;
    private readonly ILogger<TrustedDeviceService> _logger;

    public TrustedDeviceService(
        ITrustedDeviceRepository devices,
        IOptions<OidcOptions> options,
        TimeProvider clock,
        ILogger<TrustedDeviceService> logger)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger;
        Lifetime = options.Value.Session.TrustedDeviceLifetime;
    }

    /// <inheritdoc />
    public TimeSpan Lifetime { get; }

    private bool Enabled => Lifetime > TimeSpan.Zero;

    /// <inheritdoc />
    public async Task<TrustedDeviceGrant?> TrustAsync(
        Guid subjectId, string? replacedToken, string? userAgent, CancellationToken ct = default)
    {
        if (!Enabled)
            return null;

        if (!string.IsNullOrEmpty(replacedToken)
            && await _devices.FindByTokenHashAsync(HashToken(replacedToken), ct) is { } replaced
            && replaced.SubjectId == subjectId)
        {
            await _devices.RemoveAsync(replaced.Id, subjectId, ct);
        }

        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenLengthBytes));
        var now = _clock.GetUtcNow().UtcDateTime;
        var expiresAt = now.Add(Lifetime);

        var deviceId = await _devices.CreateAsync(
            new NewTrustedDevice(
                subjectId,
                HashToken(token),
                DeviceLabel.FromUserAgent(userAgent),
                now,
                expiresAt),
            ct);

        _logger.LogInformation(
            "Device {DeviceId} trusted for subject {SubjectId} until {ExpiresAt:o}",
            deviceId, subjectId, expiresAt);

        return new TrustedDeviceGrant(deviceId, token, expiresAt);
    }

    /// <inheritdoc />
    public async Task<bool> IsTrustedAsync(Guid subjectId, string? token, CancellationToken ct = default)
    {
        if (!Enabled || string.IsNullOrEmpty(token))
            return false;

        var device = await _devices.FindByTokenHashAsync(HashToken(token), ct);
        var now = _clock.GetUtcNow().UtcDateTime;
        if (device is null || device.SubjectId != subjectId || device.ExpiresAt <= now)
            return false;

        await _devices.MarkUsedAsync(device.Id, now, ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TrustedDeviceInfo>> ListAsync(
        Guid subjectId, string? currentToken, CancellationToken ct = default)
    {
        if (!Enabled)
            return [];

        var currentHash = string.IsNullOrEmpty(currentToken) ? null : HashToken(currentToken);
        var now = _clock.GetUtcNow().UtcDateTime;

        return (await _devices.ListAsync(subjectId, ct))
            .Where(d => d.ExpiresAt > now)
            .Select(d => new TrustedDeviceInfo(
                d.Id,
                d.DeviceDescription,
                d.CreatedAt,
                d.LastUsedAt,
                d.ExpiresAt,
                IsCurrent: d.TokenHash == currentHash))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<bool> RevokeAsync(Guid deviceId, Guid subjectId, CancellationToken ct = default)
    {
        var removed = await _devices.RemoveAsync(deviceId, subjectId, ct);
        if (removed)
        {
            _logger.LogInformation(
                "Trusted device {DeviceId} revoked for subject {SubjectId}", deviceId, subjectId);
        }

        return removed;
    }

    /// <inheritdoc />
    public async Task<int> RevokeAllAsync(Guid subjectId, CancellationToken ct = default)
    {
        var removed = await _devices.RemoveAllAsync(subjectId, keepTokenHash: null, ct);
        if (removed > 0)
        {
            _logger.LogInformation(
                "{Count} trusted devices revoked for subject {SubjectId}", removed, subjectId);
        }

        return removed;
    }

    /// <inheritdoc />
    public async Task<int> RevokeOthersAsync(Guid subjectId, string? currentToken, CancellationToken ct = default)
    {
        var keep = string.IsNullOrEmpty(currentToken) ? null : HashToken(currentToken);
        var removed = await _devices.RemoveAllAsync(subjectId, keep, ct);
        if (removed > 0)
        {
            _logger.LogInformation(
                "{Count} other trusted devices revoked for subject {SubjectId}", removed, subjectId);
        }

        return removed;
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
