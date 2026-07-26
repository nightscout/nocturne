using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;

namespace Nocturne.Infrastructure.Data.Security;

/// <summary>
/// Data Protection wrapping for the TOTP shared secret column. TOTP secrets are a permanent
/// second factor: a plaintext column exposes them to any database read or restored backup, so
/// the column is stored as a Data Protection payload and decrypted on materialization by an EF
/// value converter.
/// </summary>
/// <remarks>
/// The purpose string is distinct from the TOTP setup challenge (<c>Nocturne.Totp.Setup</c>), the
/// passkey challenge, and the OIDC state, so a payload from one cannot be replayed as another.
/// Data Protection keys are persisted to the database (<see cref="NocturneDbContext"/> implements
/// <c>IDataProtectionKeyContext</c>), so stored secrets survive restarts and are readable by every
/// instance.
/// </remarks>
public static class TotpSecretProtection
{
    /// <summary>Data Protection purpose for the persisted TOTP secret column.</summary>
    public const string Purpose = "Nocturne.Totp.SecretKey";

    /// <summary>
    /// Process-lifetime provider used when no application <see cref="IDataProtectionProvider"/> is
    /// reachable — design-time model builds and tests that construct a context from a bare
    /// <c>DbContextOptionsBuilder</c>. Payloads written under it are unreadable by a later process,
    /// which is the fail-closed outcome; the alternative, writing plaintext, is not.
    /// A single static instance keeps every such context in one process mutually readable.
    /// </summary>
    private static readonly IDataProtectionProvider EphemeralFallback = new EphemeralDataProtectionProvider();

    /// <summary>
    /// Creates the protector for the TOTP secret column from the application's service provider,
    /// falling back to the process-lifetime provider when one is not registered.
    /// Callers that write the column outside EF must use this method so they agree with the model.
    /// </summary>
    public static IDataProtector CreateProtector(IServiceProvider? applicationServices) =>
        (applicationServices?.GetService<IDataProtectionProvider>() ?? EphemeralFallback)
            .CreateProtector(Purpose);

    /// <summary>
    /// EF value converter that protects on write and unprotects on read. A plaintext value in the
    /// column throws on materialization rather than being read as a secret.
    /// </summary>
    public static ValueConverter<byte[], byte[]> CreateConverter(IDataProtector protector) =>
        new ValueConverter<byte[], byte[]>(
            plaintext => protector.Protect(plaintext),
            payload => protector.Unprotect(payload));

    /// <summary>
    /// True when <paramref name="value"/> starts with the Data Protection payload magic header
    /// (<c>0x09F0C9F0</c>, big-endian), i.e. the column already holds a protected payload.
    /// </summary>
    /// <remarks>
    /// Exists so the one-pass encryption of pre-existing rows can tell an unencrypted secret from an
    /// already-encrypted one without attempting decryption: a failed decryption is ambiguous between
    /// "plaintext" and "protected under a key this instance has lost", and re-protecting the latter
    /// would corrupt it. A random plaintext secret matches the header with probability 2^-32; such a
    /// row is left alone and its next read fails, so the credential is re-enrolled rather than
    /// silently trusted.
    /// </remarks>
    public static bool IsProtectedPayload(byte[] value) =>
        value.Length >= 4 && value[0] == 0x09 && value[1] == 0xF0 && value[2] == 0xC9 && value[3] == 0xF0;
}
