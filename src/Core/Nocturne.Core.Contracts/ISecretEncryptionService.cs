namespace Nocturne.Core.Contracts;

/// <summary>
/// Service for encrypting and decrypting secrets using AES-256-GCM.
/// The encryption key is derived from the api-secret using PBKDF2-SHA256.
/// </summary>
public interface ISecretEncryptionService
{
    /// <summary>
    /// Encrypts a plaintext secret value.
    /// </summary>
    /// <param name="plaintext">The secret value to encrypt</param>
    /// <returns>Base64-encoded ciphertext (nonce || ciphertext || tag)</returns>
    /// <exception cref="InvalidOperationException">Thrown when encryption is not configured (api-secret not set).</exception>
    string Encrypt(string plaintext);

    /// <summary>
    /// Decrypts an encrypted secret value.
    /// </summary>
    /// <param name="ciphertext">Base64-encoded ciphertext from Encrypt()</param>
    /// <returns>The original plaintext secret</returns>
    /// <exception cref="InvalidOperationException">Thrown when encryption is not configured (api-secret not set).</exception>
    /// <exception cref="ArgumentException">Thrown when the ciphertext has an invalid format.</exception>
    string Decrypt(string ciphertext);

    /// <summary>
    /// Encrypts a dictionary of secrets (property name → value).
    /// </summary>
    /// <param name="secrets">Dictionary of secret property names to plaintext values</param>
    /// <returns>Dictionary of property names to encrypted values</returns>
    Dictionary<string, string> EncryptSecrets(Dictionary<string, string> secrets);

    /// <summary>
    /// Decrypts a dictionary of secrets (property name → encrypted value).
    /// </summary>
    /// <param name="encryptedSecrets">Dictionary of property names to encrypted values</param>
    /// <returns>Dictionary of property names to plaintext values</returns>
    Dictionary<string, string> DecryptSecrets(Dictionary<string, string> encryptedSecrets);

    /// <summary>
    /// Whether the encryption service is properly configured (api-secret is available).
    /// </summary>
    bool IsConfigured { get; }
}
