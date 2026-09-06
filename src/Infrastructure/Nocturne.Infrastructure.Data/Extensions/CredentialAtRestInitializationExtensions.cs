using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Nocturne.Infrastructure.Data.Security;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// One-pass conversions that bring pre-existing credential columns onto their at-rest storage
/// format: TOTP secrets to Data Protection payloads, tenant share tokens to SHA-256 digests. Both
/// run at startup, before the server accepts requests, because both need application code (a
/// protector, a token generator) that a SQL migration cannot call. Both use raw ADO to read and
/// write the stored representation directly, bypassing the EF value converter that would reject the
/// old format. Both are idempotent, discriminating on the stored format rather than on a marker.
/// </summary>
/// <remarks>
/// Neither <c>totp_credentials</c> nor <c>tenants</c> is tenant-scoped, so neither pass sets
/// <c>app.current_tenant_id</c>. A pass over a tenant-scoped table without that GUC would silently
/// affect zero rows, so <c>CredentialAtRestPassTests</c> asserts both tables stay outside row level
/// security.
/// </remarks>
public static class CredentialAtRestInitializationExtensions
{
    /// <summary>
    /// Encrypts every <c>totp_credentials.secret_key</c> that still holds a bare secret, so no
    /// plaintext TOTP seed survives in the table or in a backup taken from it. Rows that already
    /// hold a Data Protection payload are left untouched.
    /// </summary>
    /// <param name="dataSource">Data source for the runtime application role.</param>
    /// <param name="protector">
    /// Protector for the TOTP secret column. Must come from
    /// <see cref="TotpSecretProtection.CreateProtector"/> so it matches the one the EF model uses;
    /// a mismatch would write payloads the application cannot read.
    /// </param>
    /// <param name="logger">Logger for progress. Never receives a secret.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of rows encrypted by this pass.</returns>
    public static async Task<int> ProtectTotpSecretsAsync(
        NpgsqlDataSource dataSource,
        IDataProtector protector,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // Buffered rather than streamed: the rewrite runs on this same connection, which cannot
        // issue commands while a reader is open.
        var plaintextRows = new List<(Guid Id, byte[] Secret)>();
        var alreadyProtected = 0;

        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT id, secret_key FROM totp_credentials";
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                var stored = (byte[])reader.GetValue(1);

                if (TotpSecretProtection.IsProtectedPayload(stored))
                {
                    alreadyProtected++;
                    continue;
                }

                plaintextRows.Add((id, stored));
            }
        }

        if (plaintextRows.Count == 0)
        {
            logger.LogDebug(
                "TOTP secrets already encrypted at rest ({Count} credential(s) checked).",
                alreadyProtected);
            return 0;
        }

        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            foreach (var (id, secret) in plaintextRows)
            {
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE totp_credentials SET secret_key = @payload WHERE id = @id";
                update.Parameters.AddWithValue("@payload", protector.Protect(secret));
                update.Parameters.AddWithValue("@id", id);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation(
            "Encrypted {Encrypted} TOTP secret(s) at rest; {Skipped} were already encrypted.",
            plaintextRows.Count, alreadyProtected);

        return plaintextRows.Count;
    }

    /// <summary>
    /// Replaces every plaintext <c>tenants.share_token</c> with the digest of a freshly generated
    /// token. The stored plaintext was readable in the table and in every backup taken from it, so
    /// it is not merely hashed in place — it is retired. The new token's plaintext is discarded, so
    /// each affected tenant's previous share link stops resolving and the owner has to generate a
    /// new one; callers are expected to notify the owners returned here.
    /// </summary>
    /// <param name="dataSource">Data source for the runtime application role.</param>
    /// <param name="generateToken">Mints a new share token. Called until the digest is unique.</param>
    /// <param name="logger">Logger for progress. Never receives a token.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The ids of the tenants whose share token was rotated by this pass.</returns>
    public static async Task<IReadOnlyList<Guid>> RotatePlaintextShareTokensAsync(
        NpgsqlDataSource dataSource,
        Func<string> generateToken,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // A digest is always CredentialHash.HexLength characters; a generated token is shorter, so
        // length alone separates the not-yet-converted rows and makes the pass idempotent.
        var stale = new List<Guid>();
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = """
                SELECT id FROM tenants
                WHERE share_token IS NOT NULL AND length(share_token) <> @hexLength
                """;
            read.Parameters.AddWithValue("@hexLength", CredentialHash.HexLength);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                stale.Add(reader.GetGuid(0));
            }
        }

        if (stale.Count == 0)
        {
            logger.LogDebug("No plaintext tenant share tokens to rotate.");
            return [];
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT share_token FROM tenants WHERE share_token IS NOT NULL";
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                used.Add(reader.GetString(0));
            }
        }

        var rotated = new List<Guid>(stale.Count);
        var now = DateTime.UtcNow;

        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            foreach (var tenantId in stale)
            {
                string digest;
                do
                {
                    digest = CredentialHash.ShareToken(generateToken());
                }
                while (!used.Add(digest));

                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE tenants
                    SET share_token = @digest, share_token_set_at = @setAt
                    WHERE id = @id
                    """;
                update.Parameters.AddWithValue("@digest", digest);
                update.Parameters.AddWithValue("@setAt", now);
                update.Parameters.AddWithValue("@id", tenantId);
                await update.ExecuteNonQueryAsync(cancellationToken);
                rotated.Add(tenantId);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation(
            "Rotated {Count} plaintext tenant share token(s); the previous links no longer resolve.",
            rotated.Count);

        return rotated;
    }
}
