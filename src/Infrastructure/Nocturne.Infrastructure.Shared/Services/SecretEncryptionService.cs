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
/// Uses an installation-specific salt combined with a static component for added security.
/// </summary>
public class SecretEncryptionService : ISecretEncryptionService
{
    private const int NonceSize = 12; // AES-GCM standard nonce size
    private const int TagSize = 16;   // AES-GCM standard tag size
    private const int KeySize = 32;   // 256 bits for AES-256
    private const int SaltSize = 16;  // 128-bit installation salt
    private const int Iterations = 100_000;
    private const string SaltConfigKey = "Nocturne:EncryptionSalt";
    private static readonly byte[] StaticSaltComponent = Encoding.UTF8.GetBytes("nocturne-secrets-v2");

    private readonly byte[]? _encryptionKey;
    private readonly ILogger<SecretEncryptionService> _logger;

    public SecretEncryptionService(IConfiguration configuration, ILogger<SecretEncryptionService> logger)
    {
        _logger = logger;

        // Get api-secret from configuration (same as used for authentication)
        var apiSecret = configuration["Parameters:api-secret"]
                     ?? configuration["API_SECRET"]
                     ?? string.Empty;

        if (string.IsNullOrEmpty(apiSecret))
        {
            _logger.LogWarning("api-secret not configured - secret encryption will not be available");
            _encryptionKey = null;
            return;
        }

        // Get or generate installation-specific salt
        var salt = GetOrCreateInstallationSalt(configuration);

        // Derive encryption key from api-secret using PBKDF2
        _encryptionKey = KeyDerivation.Pbkdf2(
            password: apiSecret,
            salt: salt,
            prf: KeyDerivationPrf.HMACSHA256,
            iterationCount: Iterations,
            numBytesRequested: KeySize);

        _logger.LogDebug("Secret encryption service initialized with installation-specific salt");
    }

    /// <summary>
    /// Gets the installation-specific salt from configuration, or generates a new one.
    /// The salt is a combination of a static component and an installation-specific component.
    /// </summary>
    private byte[] GetOrCreateInstallationSalt(IConfiguration configuration)
    {
        // Try to get existing installation salt from configuration
        var installationSaltBase64 = configuration[SaltConfigKey];

        byte[] installationSalt;
        if (!string.IsNullOrEmpty(installationSaltBase64))
        {
            try
            {
                installationSalt = Convert.FromBase64String(installationSaltBase64);
                if (installationSalt.Length >= SaltSize)
                {
                    _logger.LogDebug("Using existing installation-specific encryption salt");
                }
                else
                {
                    _logger.LogWarning("Installation salt too short, generating new one");
                    installationSalt = GenerateNewSalt();
                }
            }
            catch (FormatException)
            {
                _logger.LogWarning("Invalid installation salt format, generating new one");
                installationSalt = GenerateNewSalt();
            }
        }
        else
        {
            // Generate a new installation-specific salt
            // Note: In production, this should be persisted to appsettings.json or a secrets manager
            installationSalt = GenerateNewSalt();
            _logger.LogInformation(
                "Generated new installation-specific encryption salt. " +
                "To persist across restarts, add to configuration: {Key}={Value}",
                SaltConfigKey,
                Convert.ToBase64String(installationSalt));
        }

        // Combine static and installation-specific components
        var combinedSalt = new byte[StaticSaltComponent.Length + installationSalt.Length];
        Buffer.BlockCopy(StaticSaltComponent, 0, combinedSalt, 0, StaticSaltComponent.Length);
        Buffer.BlockCopy(installationSalt, 0, combinedSalt, StaticSaltComponent.Length, installationSalt.Length);

        return combinedSalt;
    }

    private static byte[] GenerateNewSalt()
    {
        var salt = new byte[SaltSize];
        RandomNumberGenerator.Fill(salt);
        return salt;
    }

    /// <inheritdoc />
    public bool IsConfigured => _encryptionKey != null;

    /// <inheritdoc />
    public string Encrypt(string plaintext)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Secret encryption is not configured. Ensure api-secret is set.");
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
            throw new InvalidOperationException("Secret encryption is not configured. Ensure api-secret is set.");
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
