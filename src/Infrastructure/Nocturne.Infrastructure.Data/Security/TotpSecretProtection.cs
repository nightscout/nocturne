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
    /// Process-lifetime provider for contexts built without an application service provider —
    /// design-time model builds and tests using a bare <c>DbContextOptionsBuilder</c>. One static
    /// instance, so every such context in a process stays mutually readable.
    /// </summary>
    private static readonly IDataProtectionProvider EphemeralFallback = new EphemeralDataProtectionProvider();

    /// <summary>
    /// Creates the protector the EF model uses. Lenient: the model has to build in design-time and
    /// test harnesses that never register Data Protection, so a provider without it falls back.
    /// Code that writes the column outside EF should prefer <see cref="RequireProtector"/>.
    /// </summary>
    public static IDataProtector CreateProtector(IServiceProvider? applicationServices) =>
        (applicationServices?.GetService<IDataProtectionProvider>() ?? EphemeralFallback)
            .CreateProtector(Purpose);

    /// <summary>
    /// Creates the protector for a running application, which must have Data Protection registered.
    /// Throws rather than falling back, because a pass that wrote the column under the
    /// process-lifetime provider would leave payloads no later process can read.
    /// </summary>
    public static IDataProtector RequireProtector(IServiceProvider applicationServices) =>
        applicationServices.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);

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
    /// Lets the encryption pass tell an unencrypted secret from an already-encrypted one without
    /// attempting decryption, which would be ambiguous between "plaintext" and "protected under a
    /// lost key" — and re-protecting the latter would corrupt it. A random plaintext secret matches
    /// the header with probability 2^-32; such a row is skipped and its next read then fails, so the
    /// credential is re-enrolled rather than silently trusted.
    /// </remarks>
    public static bool IsProtectedPayload(byte[] value) =>
        value.Length >= 4 && value[0] == 0x09 && value[1] == 0xF0 && value[2] == 0xC9 && value[3] == 0xF0;
}
