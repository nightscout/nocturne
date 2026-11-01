using System.Text;
using System.Text.Json;
using Nocturne.API.Models.DevOnly;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Services.DevOnly;

/// <summary>
/// Loads the committed dev identity fixture (docs/seed/dev-identities.json) and
/// upserts its subjects and passkey credentials. Development-only: invoked from
/// startup (so a DB wipe doesn't cost the developer their login) and from
/// seed-tenant (so fixture subjects become owners of every seeded tenant).
/// </summary>
public static class DevIdentityFixtureSeeder
{
    public const string PathConfigKey = "DevFixture:IdentitiesPath";
    public const string DefaultRelativePath = "docs/seed/dev-identities.json";

    /// <summary>
    /// Prefix of synthetic credential ids created by dev seeding (seed-tenant
    /// owners, recovery keepers). These satisfy the setup-gate credential check
    /// but can never complete a WebAuthn assertion, and are excluded from
    /// fixture export.
    /// </summary>
    public const string SyntheticCredentialPrefix = "dev-seed-";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static bool IsSyntheticCredentialId(byte[] credentialId)
    {
        var prefix = Encoding.UTF8.GetBytes(SyntheticCredentialPrefix);
        return credentialId.Length >= prefix.Length
            && credentialId.AsSpan(0, prefix.Length).SequenceEqual(prefix);
    }

    /// <summary>
    /// Resolves the fixture path: explicit DevFixture:IdentitiesPath config wins,
    /// otherwise walk up from the runtime base directory to the repository root
    /// (identified by its docs/seed directory) — the API runs from its project
    /// output directory in local development.
    /// </summary>
    public static string ResolveFixturePath(IConfiguration configuration)
    {
        var configured = configuration[PathConfigKey];
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var seedDir = Path.Combine(dir.FullName, "docs", "seed");
            if (Directory.Exists(seedDir))
                return Path.Combine(seedDir, "dev-identities.json");
            dir = dir.Parent;
        }

        return Path.GetFullPath(DefaultRelativePath);
    }

    /// <summary>
    /// Loads and parses the fixture, or returns null when the file doesn't exist
    /// or contains no identities.
    /// </summary>
    public static DevIdentityFixtureFile? Load(IConfiguration configuration, ILogger logger)
    {
        var path = ResolveFixturePath(configuration);
        if (!File.Exists(path))
            return null;

        try
        {
            var fixture = JsonSerializer.Deserialize<DevIdentityFixtureFile>(
                File.ReadAllText(path), JsonOptions);
            if (fixture is null || fixture.Identities.Count == 0)
                return null;
            return fixture;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Dev identity fixture at {Path} is malformed — ignoring", path);
            return null;
        }
    }

    /// <summary>
    /// Upserts the fixture's subjects (by id) and credentials (by credential id)
    /// and returns the subject ids present in the fixture. Credentials are
    /// inserted with sign_count = 0; existing rows are left untouched.
    /// </summary>
    public static async Task<List<Guid>> SeedAsync(
        ISubjectRepository subjects,
        IPasskeyCredentialRepository passkeys,
        IConfiguration configuration,
        TimeProvider time,
        ILogger logger,
        CancellationToken ct = default)
    {
        var fixture = Load(configuration, logger);
        if (fixture is null)
            return [];

        var subjectIds = fixture.Identities.Select(i => i.SubjectId).ToList();
        var existingSubjects = (await subjects.GetManyAsync(subjectIds, ct)).ToDictionary(s => s.Id);

        var seeded = 0;
        foreach (var identity in fixture.Identities)
        {
            if (!existingSubjects.TryGetValue(identity.SubjectId, out var existing))
            {
                await subjects.CreateAsync(
                    new NewSubject(
                        identity.Name,
                        Username: identity.Username,
                        Email: identity.Email,
                        Id: identity.SubjectId),
                    ct);
            }
            else if (!existing.IsActive)
            {
                // A deactivated fixture subject would be silently excluded from
                // login candidate filters — the fixture is an assertion that
                // this identity is usable.
                await subjects.UpdateAsync(identity.SubjectId, new SubjectEdit(IsActive: true), ct);
            }

            var existingCredentialIds = await passkeys.GetCredentialIdsAsync(identity.SubjectId, ct);

            foreach (var credential in identity.Credentials)
            {
                byte[] credentialId;
                byte[] publicKey;
                try
                {
                    credentialId = Convert.FromBase64String(credential.CredentialId);
                    publicKey = Convert.FromBase64String(credential.PublicKey);
                }
                catch (FormatException ex)
                {
                    // A hand-edited fixture must not crash-loop every
                    // teammate's Development startup.
                    logger.LogWarning(ex,
                        "Dev identity fixture: credential '{Label}' for subject {SubjectId} has invalid base64 — skipping",
                        credential.Label, identity.SubjectId);
                    continue;
                }

                if (existingCredentialIds.Any(existing => existing.SequenceEqual(credentialId)))
                    continue;

                await passkeys.CreateAsync(
                    new NewPasskeyCredential(
                        identity.SubjectId,
                        credentialId,
                        publicKey,
                        SignCount: 0,
                        credential.Transports,
                        credential.Label ?? "dev fixture",
                        credential.AaGuid,
                        time.GetUtcNow().UtcDateTime),
                    ct);
                seeded++;
            }
        }

        if (seeded > 0)
            logger.LogInformation(
                "Dev identity fixture: seeded {Count} credential(s) for {Subjects} subject(s)",
                seeded, fixture.Identities.Count);

        return subjectIds;
    }
}
