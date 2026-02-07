using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Service for managing OAuth Device Authorization Grant (RFC 8628) device codes.
/// Handles device code creation, user code lookup, and approval/denial flows.
/// </summary>
public class OAuthDeviceCodeService : IOAuthDeviceCodeService
{
    /// <summary>
    /// Reduced alphabet for user codes: no vowels (avoid forming words),
    /// no ambiguous characters (0/O/I/L/1).
    /// </summary>
    private const string UserCodeAlphabet = "BCDFGHJKMNPQRSTVWXYZ23456789";

    private const int UserCodeLength = 8;
    private const int MaxUserCodeRetries = 5;
    private const int DeviceCodeExpirationMinutes = 15;
    private const int DefaultPollingInterval = 5;

    private readonly NocturneDbContext _db;
    private readonly IJwtService _jwtService;
    private readonly IOAuthClientService _clientService;
    private readonly IOAuthGrantService _grantService;
    private readonly ILogger<OAuthDeviceCodeService> _logger;

    /// <summary>
    /// Creates a new instance of OAuthDeviceCodeService
    /// </summary>
    public OAuthDeviceCodeService(
        NocturneDbContext db,
        IJwtService jwtService,
        IOAuthClientService clientService,
        IOAuthGrantService grantService,
        ILogger<OAuthDeviceCodeService> logger)
    {
        _db = db;
        _jwtService = jwtService;
        _clientService = clientService;
        _grantService = grantService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DeviceCodeResult> CreateDeviceCodeAsync(
        string clientId,
        IEnumerable<string> scopes,
        CancellationToken ct = default)
    {
        // Generate a crypto-random device code and hash it for storage
        var deviceCode = _jwtService.GenerateRefreshToken();
        var deviceCodeHash = _jwtService.HashRefreshToken(deviceCode);

        // Generate a unique user code with collision retry
        string userCode = null!;
        for (var attempt = 0; attempt < MaxUserCodeRetries; attempt++)
        {
            userCode = GenerateUserCode();
            var normalized = NormalizeUserCode(userCode);

            var exists = await _db.OAuthDeviceCodes
                .AnyAsync(d => d.UserCode == normalized, ct);

            if (!exists)
                break;

            if (attempt == MaxUserCodeRetries - 1)
            {
                _logger.LogError(
                    "Failed to generate a unique user code after {MaxRetries} attempts for client {ClientId}",
                    MaxUserCodeRetries, clientId);
                throw new InvalidOperationException(
                    $"Failed to generate a unique user code after {MaxUserCodeRetries} attempts");
            }

            _logger.LogWarning(
                "User code collision on attempt {Attempt} for client {ClientId}, retrying",
                attempt + 1, clientId);
        }

        var scopesList = scopes.ToList();
        var normalizedUserCode = NormalizeUserCode(userCode);

        var entity = new OAuthDeviceCodeEntity
        {
            ClientId = clientId,
            DeviceCodeHash = deviceCodeHash,
            UserCode = normalizedUserCode,
            Scopes = scopesList,
            ExpiresAt = DateTime.UtcNow.AddMinutes(DeviceCodeExpirationMinutes),
            Interval = DefaultPollingInterval,
        };

        _db.OAuthDeviceCodes.Add(entity);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Created device code for client {ClientId} with user code {UserCode}",
            clientId, userCode);

        return new DeviceCodeResult
        {
            DeviceCode = deviceCode,
            UserCode = userCode,
            ExpiresIn = DeviceCodeExpirationMinutes * 60,
            Interval = DefaultPollingInterval,
        };
    }

    /// <inheritdoc />
    public async Task<DeviceCodeInfo?> GetByUserCodeAsync(
        string userCode,
        CancellationToken ct = default)
    {
        var normalized = NormalizeUserCode(userCode);

        var entity = await _db.OAuthDeviceCodes
            .FirstOrDefaultAsync(d => d.UserCode == normalized, ct);

        if (entity == null)
        {
            _logger.LogDebug("Device code not found for user code {UserCode}", normalized);
            return null;
        }

        // Look up client display name
        var client = await _clientService.FindOrCreateClientAsync(entity.ClientId, ct);

        return new DeviceCodeInfo
        {
            Id = entity.Id,
            UserCode = entity.UserCode,
            ClientId = entity.ClientId,
            ClientDisplayName = client.DisplayName,
            IsKnownClient = client.IsKnown,
            Scopes = entity.Scopes,
            IsExpired = entity.IsExpired,
            IsApproved = entity.IsApproved,
            IsDenied = entity.IsDenied,
        };
    }

    /// <inheritdoc />
    public async Task<bool> ApproveDeviceCodeAsync(
        string userCode,
        Guid subjectId,
        CancellationToken ct = default)
    {
        var normalized = NormalizeUserCode(userCode);

        var entity = await _db.OAuthDeviceCodes
            .FirstOrDefaultAsync(d => d.UserCode == normalized, ct);

        if (entity == null)
        {
            _logger.LogWarning("Attempted to approve non-existent device code {UserCode}", normalized);
            return false;
        }

        if (entity.IsExpired)
        {
            _logger.LogWarning("Attempted to approve expired device code {UserCode}", normalized);
            return false;
        }

        if (entity.IsApproved)
        {
            _logger.LogWarning("Attempted to approve already-approved device code {UserCode}", normalized);
            return false;
        }

        if (entity.IsDenied)
        {
            _logger.LogWarning("Attempted to approve already-denied device code {UserCode}", normalized);
            return false;
        }

        // Find the client entity so we can create a grant against the internal ID
        var client = await _clientService.FindOrCreateClientAsync(entity.ClientId, ct);

        // Create a grant linking the user to the client with the requested scopes
        var grant = await _grantService.CreateOrUpdateGrantAsync(
            client.Id, subjectId, entity.Scopes, ct: ct);

        entity.ApprovedAt = DateTime.UtcNow;
        entity.GrantId = grant.Id;
        entity.SubjectId = subjectId;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Device code {UserCode} approved by subject {SubjectId} for client {ClientId}",
            normalized, subjectId, entity.ClientId);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DenyDeviceCodeAsync(
        string userCode,
        CancellationToken ct = default)
    {
        var normalized = NormalizeUserCode(userCode);

        var entity = await _db.OAuthDeviceCodes
            .FirstOrDefaultAsync(d => d.UserCode == normalized, ct);

        if (entity == null)
        {
            _logger.LogWarning("Attempted to deny non-existent device code {UserCode}", normalized);
            return false;
        }

        if (entity.IsExpired)
        {
            _logger.LogWarning("Attempted to deny expired device code {UserCode}", normalized);
            return false;
        }

        if (entity.IsApproved)
        {
            _logger.LogWarning("Attempted to deny already-approved device code {UserCode}", normalized);
            return false;
        }

        if (entity.IsDenied)
        {
            _logger.LogWarning("Attempted to deny already-denied device code {UserCode}", normalized);
            return false;
        }

        entity.DeniedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Device code {UserCode} denied for client {ClientId}",
            normalized, entity.ClientId);

        return true;
    }

    /// <summary>
    /// Normalize a user code by stripping hyphens, spaces, and converting to uppercase.
    /// This ensures "abcd-1234", "ABCD 1234", "abcd1234" all match the stored format.
    /// </summary>
    private static string NormalizeUserCode(string userCode)
    {
        return userCode
            .Replace("-", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal)
            .ToUpperInvariant();
    }

    /// <summary>
    /// Generate a cryptographically random user code using the reduced alphabet.
    /// Format: XXXX-YYYY (8 characters from the reduced alphabet with a hyphen after the first 4).
    /// </summary>
    private static string GenerateUserCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(UserCodeLength);
        var chars = new char[UserCodeLength];

        for (var i = 0; i < UserCodeLength; i++)
        {
            chars[i] = UserCodeAlphabet[bytes[i] % UserCodeAlphabet.Length];
        }

        // Format as XXXX-YYYY
        return $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}";
    }
}
