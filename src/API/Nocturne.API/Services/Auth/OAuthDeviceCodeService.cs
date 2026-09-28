using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Manages OAuth Device Authorization Grant (RFC 8628) device codes, including creation,
/// user-code lookup, and approval/denial flows.
/// </summary>
/// <seealso cref="IOAuthDeviceCodeService"/>
/// <seealso cref="IOAuthGrantService"/>
public class OAuthDeviceCodeService : IOAuthDeviceCodeService
{
    /// <summary>
    /// Reduced alphabet for user codes: no vowels (avoid forming words),
    /// no ambiguous characters (0/O/I/L/1).
    /// </summary>
    private const string UserCodeAlphabet = "BCDFGHJKMNPQRSTVWXYZ23456789";

    private const int UserCodeLength = 8;
    private const int MaxUserCodeRetries = 5;
    private const int DeviceCodeExpirationMinutes = 30;
    private const int DefaultPollingInterval = 5;

    private readonly NocturneDbContext _db;
    private readonly IJwtService _jwtService;
    private readonly IOAuthClientService _clientService;
    private readonly IOAuthGrantService _grantService;
    private readonly ILogger<OAuthDeviceCodeService> _logger;

    /// <summary>
    /// Initialises a new <see cref="OAuthDeviceCodeService"/>.
    /// </summary>
    /// <param name="db">Database context for reading and writing device code records.</param>
    /// <param name="jwtService">Used to generate and hash the opaque device code value.</param>
    /// <param name="clientService">Used to look up registered OAuth clients by ID.</param>
    /// <param name="grantService">Used to create or update grants when a device code is approved.</param>
    /// <param name="logger">Logger instance.</param>
    public OAuthDeviceCodeService(
        NocturneDbContext db,
        IJwtService jwtService,
        IOAuthClientService clientService,
        IOAuthGrantService grantService,
        ILogger<OAuthDeviceCodeService> logger
    )
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
        CancellationToken ct = default
    )
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

            var exists = await _db.OAuthDeviceCodes.AnyAsync(d => d.UserCode == normalized, ct);

            if (!exists)
                break;

            if (attempt == MaxUserCodeRetries - 1)
            {
                _logger.LogError(
                    "Failed to generate a unique user code after {MaxRetries} attempts for client {ClientId}",
                    MaxUserCodeRetries,
                    clientId
                );
                throw new InvalidOperationException(
                    $"Failed to generate a unique user code after {MaxUserCodeRetries} attempts"
                );
            }

            _logger.LogWarning(
                "User code collision on attempt {Attempt} for client {ClientId}, retrying",
                attempt + 1,
                clientId
            );
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
            clientId,
            userCode
        );

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
        CancellationToken ct = default
    )
    {
        var normalized = NormalizeUserCode(userCode);

        _logger.LogInformation(
            "Looking up device code: input={Input}, normalized={Normalized}",
            userCode,
            normalized
        );

        var entity = await _db.OAuthDeviceCodes.FirstOrDefaultAsync(
            d => d.UserCode == normalized,
            ct
        );

        if (entity == null)
        {
            _logger.LogWarning("Device code not found for user code {UserCode}", normalized);
            return null;
        }

        _logger.LogInformation(
            "Found device code: id={Id}, expired={Expired}, approved={Approved}, denied={Denied}",
            entity.Id,
            entity.IsExpired,
            entity.IsApproved,
            entity.IsDenied
        );

        // Look up client display name
        var client = await _clientService.GetClientAsync(entity.ClientId, ct);

        return new DeviceCodeInfo
        {
            Id = entity.Id,
            UserCode = entity.UserCode,
            ClientId = entity.ClientId,
            ClientDisplayName = client?.DisplayName,
            IsKnownClient = client?.IsKnown ?? false,
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
        bool limitTo24Hours = false,
        CancellationToken ct = default
    )
    {
        var normalized = NormalizeUserCode(userCode);

        var entity = await _db.OAuthDeviceCodes.FirstOrDefaultAsync(
            d => d.UserCode == normalized,
            ct
        );

        if (entity == null)
        {
            _logger.LogWarning(
                "Attempted to approve non-existent device code {UserCode}",
                normalized
            );
            return false;
        }

        if (entity.IsExpired)
        {
            _logger.LogWarning("Attempted to approve expired device code {UserCode}", normalized);
            return false;
        }

        if (entity.IsApproved)
        {
            _logger.LogWarning(
                "Attempted to approve already-approved device code {UserCode}",
                normalized
            );
            return false;
        }

        if (entity.IsDenied)
        {
            _logger.LogWarning(
                "Attempted to approve already-denied device code {UserCode}",
                normalized
            );
            return false;
        }

        // Find the client entity so we can create a grant against the internal ID
        var client = await _clientService.GetClientAsync(entity.ClientId, ct);
        if (client == null)
        {
            _logger.LogWarning(
                "Cannot approve device code {UserCode}: client {ClientId} no longer registered",
                normalized, entity.ClientId);
            return false;
        }

        // Create a grant linking the user to the client with the requested scopes
        var grant = await _grantService.CreateOrUpdateGrantAsync(
            client.Id,
            subjectId,
            entity.Scopes,
            limitTo24Hours: limitTo24Hours,
            ct: ct
        );

        entity.ApprovedAt = DateTime.UtcNow;
        entity.GrantId = grant.Id;
        entity.SubjectId = subjectId;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Device code {UserCode} approved by subject {SubjectId} for client {ClientId}, created grant {GrantId} of type {GrantType}",
            normalized,
            subjectId,
            entity.ClientId,
            grant.Id,
            grant.GrantType
        );

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DenyDeviceCodeAsync(string userCode, CancellationToken ct = default)
    {
        var normalized = NormalizeUserCode(userCode);

        var entity = await _db.OAuthDeviceCodes.FirstOrDefaultAsync(
            d => d.UserCode == normalized,
            ct
        );

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
            _logger.LogWarning(
                "Attempted to deny already-approved device code {UserCode}",
                normalized
            );
            return false;
        }

        if (entity.IsDenied)
        {
            _logger.LogWarning(
                "Attempted to deny already-denied device code {UserCode}",
                normalized
            );
            return false;
        }

        entity.DeniedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Device code {UserCode} denied for client {ClientId}",
            normalized,
            entity.ClientId
        );

        return true;
    }

    /// <summary>
    /// Normalises a user code by stripping hyphens and spaces and converting to uppercase.
    /// Ensures that <c>abcd-1234</c>, <c>ABCD 1234</c>, and <c>abcd1234</c> all match
    /// the stored database format.
    /// </summary>
    /// <param name="userCode">The raw user-entered or generated code.</param>
    /// <returns>The normalised code suitable for database lookup.</returns>
    private static string NormalizeUserCode(string userCode)
    {
        return userCode
            .Replace("-", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal)
            .ToUpperInvariant();
    }

    /// <summary>
    /// Generates a cryptographically random user code using the reduced <see cref="UserCodeAlphabet"/>.
    /// </summary>
    /// <remarks>Format is <c>XXXX-YYYY</c> — eight characters from the reduced alphabet separated by a hyphen.</remarks>
    /// <returns>A formatted user code string.</returns>
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
