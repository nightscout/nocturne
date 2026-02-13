using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Cryptography.KeyDerivation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts;

namespace Nocturne.Infrastructure.Shared.Services;

/// <summary>
/// Encrypts and decrypts secrets using AES-256-GCM with a key derived from api-secret.
/// The key is derived using PBKDF2-SHA256 with 100,000 iterations.
/// The salt is derived deterministically from the api-secret via HMAC-SHA256,
/// ensuring stability across restarts without requiring external persistence.
/// An explicit salt can still be provided via configuration as an override.
/// </summary>
public class SecretEncryptionService : ISecretEncryptionService
{
    private const int NonceSize = 12; // AES-GCM standard nonce size
    private const int TagSize = 16; // AES-GCM standard tag size
    private const int KeySize = 32; // 256 bits for AES-256
    private const int SaltSize = 16; // 128-bit installation salt
    private const int Iterations = 100_000;
    private const string SaltConfigKey = "Nocturne:EncryptionSalt";
    private static readonly byte[] SaltDerivationKey = Encoding.UTF8.GetBytes(
        "nocturne-encryption-salt-derivation"
    );

    private readonly byte[]? _encryptionKey;
    private readonly ILogger<SecretEncryptionService> _logger;

    public SecretEncryptionService(
        IConfiguration configuration,
        ILogger<SecretEncryptionService> logger
    )
    {
        _logger = logger;

        // Get api-secret from configuration (same as used for authentication)
        var apiSecret =
            configuration["Parameters:api-secret"] ?? configuration["API_SECRET"] ?? string.Empty;

        if (string.IsNullOrEmpty(apiSecret))
        {
            _logger.LogWarning(
                "api-secret not configured - secret encryption will not be available"
            );
            _encryptionKey = null;
            return;
        }

        // Get or derive deterministic salt
        var salt = GetSalt(configuration, apiSecret);

        // Derive encryption key from api-secret using PBKDF2
        _encryptionKey = KeyDerivation.Pbkdf2(
            password: apiSecret,
            salt: salt,
            prf: KeyDerivationPrf.HMACSHA256,
            iterationCount: Iterations,
            numBytesRequested: KeySize
        );

        _logger.LogDebug("Secret encryption service initialized");
    }

    /// <summary>
    /// Gets the encryption salt. Uses an explicit configuration value if provided,
    /// otherwise derives a deterministic salt from the api-secret via HMAC-SHA256.
    /// This ensures the salt is stable across restarts without requiring external persistence.
    /// </summary>
    private byte[] GetSalt(IConfiguration configuration, string apiSecret)
    {
        // Allow explicit override via configuration
        var installationSaltBase64 = configuration[SaltConfigKey];
        if (!string.IsNullOrEmpty(installationSaltBase64))
        {
            try
            {
                var explicitSalt = Convert.FromBase64String(installationSaltBase64);
                if (explicitSalt.Length >= SaltSize)
                {
                    _logger.LogDebug("Using explicit encryption salt from configuration");
                    return explicitSalt;
                }

                _logger.LogWarning(
                    "Configured encryption salt too short, falling back to derived salt"
                );
            }
            catch (FormatException)
            {
                _logger.LogWarning(
                    "Invalid encryption salt format in configuration, falling back to derived salt"
                );
            }
        }

        // Derive a deterministic salt from the api-secret using HMAC-SHA256.
        // This follows the HKDF pattern: use HMAC with a fixed purpose key to derive
        // a stable, installation-unique salt without requiring external persistence.
        var derived = HMACSHA256.HashData(SaltDerivationKey, Encoding.UTF8.GetBytes(apiSecret));

        _logger.LogDebug("Using deterministic encryption salt derived from api-secret");
        return derived;
    }

    /// <inheritdoc />
    public bool IsConfigured => _encryptionKey != null;

    /// <inheritdoc />
    public string Encrypt(string plaintext)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Secret encryption is not configured. Ensure api-secret is set."
            );
        }

        if (string.IsNullOrEmpty(plaintext))
        {
            return string.Empty;
        }

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_encryptionKey!, TagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Combine: nonce || ciphertext || tag
        var result = new byte[NonceSize + ciphertext.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, result, NonceSize, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, NonceSize + ciphertext.Length, TagSize);

        return Convert.ToBase64String(result);
    }

    /// <inheritdoc />
    public string Decrypt(string ciphertext)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Secret encryption is not configured. Ensure api-secret is set."
            );
        }

        if (string.IsNullOrEmpty(ciphertext))
        {
            return string.Empty;
        }

        var combined = Convert.FromBase64String(ciphertext);

        if (combined.Length < NonceSize + TagSize)
        {
            throw new ArgumentException("Invalid ciphertext format");
        }

        var nonce = new byte[NonceSize];
        var ciphertextLength = combined.Length - NonceSize - TagSize;
        var encryptedData = new byte[ciphertextLength];
        var tag = new byte[TagSize];

        Buffer.BlockCopy(combined, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(combined, NonceSize, encryptedData, 0, ciphertextLength);
        Buffer.BlockCopy(combined, NonceSize + ciphertextLength, tag, 0, TagSize);

        var plaintext = new byte[ciphertextLength];

        using var aes = new AesGcm(_encryptionKey!, TagSize);
        aes.Decrypt(nonce, encryptedData, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }

    /// <inheritdoc />
    public Dictionary<string, string> EncryptSecrets(Dictionary<string, string> secrets)
    {
        if (secrets == null || secrets.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        var encrypted = new Dictionary<string, string>(secrets.Count);
        foreach (var (key, value) in secrets)
        {
            encrypted[key] = string.IsNullOrEmpty(value) ? string.Empty : Encrypt(value);
        }
        return encrypted;
    }

    /// <inheritdoc />
    public Dictionary<string, string> DecryptSecrets(Dictionary<string, string> encryptedSecrets)
    {
        if (encryptedSecrets == null || encryptedSecrets.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        var decrypted = new Dictionary<string, string>(encryptedSecrets.Count);
        foreach (var (key, value) in encryptedSecrets)
        {
            try
            {
                decrypted[key] = string.IsNullOrEmpty(value) ? string.Empty : Decrypt(value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to decrypt secret for key {Key}", key);
                decrypted[key] = string.Empty;
            }
        }
        return decrypted;
    }
}
