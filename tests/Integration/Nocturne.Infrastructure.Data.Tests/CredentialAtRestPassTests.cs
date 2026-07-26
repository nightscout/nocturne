using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Security;
using Nocturne.Infrastructure.Data.Tests.Rls;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// Verifies the one-pass conversions of the credential columns against a real PostgreSQL schema:
/// that they actually write rows, that they are idempotent, and that neither table they touch is
/// under row level security — so no <c>app.current_tenant_id</c> is required and the passes cannot
/// silently affect zero rows.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class CredentialAtRestPassTests
{
    private readonly RlsCompletenessFixture _fx;

    public CredentialAtRestPassTests(RlsCompletenessFixture fx)
    {
        _fx = fx;
    }

    /// <summary>
    /// Both passes read and write without setting the tenant GUC. That is only correct while these
    /// tables are outside row level security; were either brought under a policy, the passes would
    /// match zero rows and silently do nothing, so pin the assumption here.
    /// </summary>
    [Theory]
    [InlineData("totp_credentials")]
    [InlineData("tenants")]
    public async Task Table_touched_by_a_credential_pass_is_not_row_level_secured(string table)
    {
        await using var conn = await _fx.OpenMigratorConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT relrowsecurity FROM pg_class WHERE relname = @table";
        cmd.Parameters.AddWithValue("@table", table);

        var rowSecurity = await cmd.ExecuteScalarAsync();

        rowSecurity.Should().Be(false,
            $"the credential passes query {table} without setting app.current_tenant_id");
    }

    [Fact]
    public async Task ProtectTotpSecretsAsync_encrypts_a_plaintext_secret_in_place()
    {
        await using var dataSource = NpgsqlDataSource.Create(_fx.AppConnectionString);
        var protector = TotpSecretProtection.CreateProtector(null);

        var plaintext = NewSecret();
        var credentialId = await SeedPlaintextTotpCredentialAsync(dataSource, plaintext);

        var encrypted = await CredentialAtRestInitializationExtensions.ProtectTotpSecretsAsync(
            dataSource, protector, NullLogger.Instance);

        encrypted.Should().BeGreaterThanOrEqualTo(1, "the seeded plaintext row must be rewritten");

        var stored = await ReadSecretAsync(dataSource, credentialId);
        stored.Should().NotEqual(plaintext, "the column must no longer hold the seed");
        TotpSecretProtection.IsProtectedPayload(stored).Should().BeTrue();
        protector.Unprotect(stored).Should().Equal(plaintext,
            "verification reads the secret back through the same protector");

        // Second run: the row is already protected, so it is skipped rather than double-encrypted.
        var second = await CredentialAtRestInitializationExtensions.ProtectTotpSecretsAsync(
            dataSource, protector, NullLogger.Instance);
        second.Should().Be(0);
        (await ReadSecretAsync(dataSource, credentialId)).Should().Equal(stored);
    }

    [Fact]
    public async Task ProtectTotpSecretsAsync_leaves_a_secret_readable_through_the_EF_converter()
    {
        await using var dataSource = NpgsqlDataSource.Create(_fx.AppConnectionString);
        var protector = TotpSecretProtection.CreateProtector(null);

        var plaintext = NewSecret();
        var credentialId = await SeedPlaintextTotpCredentialAsync(dataSource, plaintext);

        await CredentialAtRestInitializationExtensions.ProtectTotpSecretsAsync(
            dataSource, protector, NullLogger.Instance);

        // The model's converter resolves the same process-lifetime fallback protector the pass used,
        // so a converted row still verifies — this is the "TOTP still works after encryption" check.
        await using var db = CreateContext(dataSource);
        var credential = await db.TotpCredentials.AsNoTracking()
            .FirstAsync(c => c.Id == credentialId);

        credential.SecretKey.Should().Equal(plaintext);
    }

    [Fact]
    public async Task The_EF_write_path_never_stores_a_TOTP_secret_as_plaintext()
    {
        await using var dataSource = NpgsqlDataSource.Create(_fx.AppConnectionString);
        var plaintext = NewSecret();

        var credentialId = Guid.CreateVersion7();
        await using (var db = CreateContext(dataSource))
        {
            db.Subjects.Add(NewSubject(out var subjectId));
            db.TotpCredentials.Add(new TotpCredentialEntity
            {
                Id = credentialId,
                SubjectId = subjectId,
                SecretKey = plaintext,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var stored = await ReadSecretAsync(dataSource, credentialId);
        stored.Should().NotEqual(plaintext);
        TotpSecretProtection.IsProtectedPayload(stored).Should().BeTrue();
    }

    [Fact]
    public async Task RotatePlaintextShareTokensAsync_retires_a_plaintext_token()
    {
        await using var dataSource = NpgsqlDataSource.Create(_fx.AppConnectionString);

        const string oldToken = "k7m2q9x4r3wt";
        var tenantId = await SeedTenantAsync(dataSource, shareToken: oldToken);

        // A distinct token per call: the pass loops until the digest is unused, so a constant
        // generator would spin if the sweep ever found more than one stale row.
        var minted = new List<string>();
        var rotated = await CredentialAtRestInitializationExtensions.RotatePlaintextShareTokensAsync(
            dataSource,
            () =>
            {
                var token = $"mint{minted.Count:00000000}";
                minted.Add(token);
                return token;
            },
            NullLogger.Instance);

        rotated.Should().Contain(tenantId, "the seeded plaintext row must be rewritten");

        var stored = await ReadShareTokenAsync(dataSource, tenantId);
        stored.Should().NotBeNull();
        stored!.Should().HaveLength(CredentialHash.HexLength).And.MatchRegex("^[0-9a-f]+$");
        stored.Should().NotBe(oldToken);
        stored.Should().NotBe(CredentialHash.ShareToken(oldToken),
            "the token is retired, not merely hashed — its plaintext was already exposed");
        minted.Select(CredentialHash.ShareToken).Should().Contain(stored,
            "the stored digest must be of a freshly minted token");

        // Second run: the digest is already in the target format, so nothing is rotated again.
        var second = await CredentialAtRestInitializationExtensions.RotatePlaintextShareTokensAsync(
            dataSource, () => "0000000000aa", NullLogger.Instance);
        second.Should().BeEmpty();
        (await ReadShareTokenAsync(dataSource, tenantId)).Should().Be(stored);
    }

    [Fact]
    public async Task RotatePlaintextShareTokensAsync_leaves_a_tenant_without_a_link_alone()
    {
        await using var dataSource = NpgsqlDataSource.Create(_fx.AppConnectionString);
        var tenantId = await SeedTenantAsync(dataSource, shareToken: null);

        var rotated = await CredentialAtRestInitializationExtensions.RotatePlaintextShareTokensAsync(
            dataSource, () => $"idle{Guid.NewGuid():N}"[..12], NullLogger.Instance);

        rotated.Should().NotContain(tenantId);
        (await ReadShareTokenAsync(dataSource, tenantId)).Should().BeNull();
    }

    private static NocturneDbContext CreateContext(NpgsqlDataSource dataSource) =>
        new(new DbContextOptionsBuilder<NocturneDbContext>().UseNpgsql(dataSource).Options);

    private static byte[] NewSecret()
    {
        var secret = new byte[20];
        Random.Shared.NextBytes(secret);
        // Keep the seed from accidentally matching the Data Protection payload header.
        secret[0] = 0x00;
        return secret;
    }

    private static SubjectEntity NewSubject(out Guid subjectId)
    {
        subjectId = Guid.CreateVersion7();
        return new SubjectEntity { Id = subjectId, Name = $"credential-pass-{subjectId:N}" };
    }

    /// <summary>
    /// Seeds a credential whose column holds the bare secret, exactly as a pre-fix release wrote it.
    /// The row is created through EF (so schema defaults apply) and the column then overwritten with
    /// raw SQL, because the EF write path would protect it and leave nothing to convert.
    /// </summary>
    private static async Task<Guid> SeedPlaintextTotpCredentialAsync(
        NpgsqlDataSource dataSource, byte[] plaintext)
    {
        var credentialId = Guid.CreateVersion7();

        await using (var db = CreateContext(dataSource))
        {
            db.Subjects.Add(NewSubject(out var subjectId));
            db.TotpCredentials.Add(new TotpCredentialEntity
            {
                Id = credentialId,
                SubjectId = subjectId,
                SecretKey = [0x01],
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE totp_credentials SET secret_key = @secret WHERE id = @id";
        cmd.Parameters.AddWithValue("@secret", plaintext);
        cmd.Parameters.AddWithValue("@id", credentialId);
        (await cmd.ExecuteNonQueryAsync()).Should().Be(1);

        return credentialId;
    }

    private static async Task<byte[]> ReadSecretAsync(NpgsqlDataSource dataSource, Guid credentialId)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT secret_key FROM totp_credentials WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", credentialId);
        return (byte[])(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<Guid> SeedTenantAsync(NpgsqlDataSource dataSource, string? shareToken)
    {
        var tenantId = Guid.CreateVersion7();
        var slug = $"t{tenantId:N}"[..20];

        await using var db = CreateContext(dataSource);
        db.Tenants.Add(new TenantEntity
        {
            Id = tenantId,
            Slug = slug,
            DisplayName = slug,
            // ShareToken carries no value converter, so this lands in the column verbatim — the
            // pre-fix representation the rotation pass has to find.
            ShareToken = shareToken,
            ShareTokenSetAt = shareToken is null ? null : DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        return tenantId;
    }

    private static async Task<string?> ReadShareTokenAsync(NpgsqlDataSource dataSource, Guid tenantId)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT share_token FROM tenants WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", tenantId);
        var value = await cmd.ExecuteScalarAsync();
        return value is DBNull or null ? null : (string)value;
    }
}
