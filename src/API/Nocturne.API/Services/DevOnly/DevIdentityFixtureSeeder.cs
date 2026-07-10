using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Models.DevOnly;
using Nocturne.Infrastructure.Data;

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
        NocturneDbContext db,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken ct = default)
    {
        var fixture = Load(configuration, logger);
        if (fixture is null)
            return [];

        var subjectIds = fixture.Identities.Select(i => i.SubjectId).ToList();
        var existingSubjects = await db.Subjects
            .Where(s => subjectIds.Contains(s.Id))
            .Select(s => s.Id)
            .ToListAsync(ct);

        var seeded = 0;
        foreach (var identity in fixture.Identities)
        {
            if (!existingSubjects.Contains(identity.SubjectId))
            {
                db.Subjects.Add(new()
                {
                    Id = identity.SubjectId,
                    Name = identity.Name,
                    Username = identity.Username,
                    Email = identity.Email,
                    IsActive = true,
                    IsSystemSubject = false,
                    ApprovalStatus = "Approved",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
            }
            else
            {
                // A deactivated fixture subject would be silently excluded from
                // login candidate filters — the fixture is an assertion that
                // this identity is usable.
                await db.Subjects
                    .Where(s => s.Id == identity.SubjectId && !s.IsActive)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, true), ct);
            }

            var existingCredentialIds = await db.PasskeyCredentials
                .Where(c => c.SubjectId == identity.SubjectId)
                .Select(c => c.CredentialId)
                .ToListAsync(ct);

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

                db.PasskeyCredentials.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    SubjectId = identity.SubjectId,
                    CredentialId = credentialId,
                    PublicKey = publicKey,
                    SignCount = 0,
                    Transports = credential.Transports,
                    Label = credential.Label ?? "dev fixture",
                    AaGuid = credential.AaGuid,
                    CreatedAt = DateTime.UtcNow,
                });
                seeded++;
            }
        }

        await db.SaveChangesAsync(ct);

        if (seeded > 0)
            logger.LogInformation(
                "Dev identity fixture: seeded {Count} credential(s) for {Subjects} subject(s)",
                seeded, fixture.Identities.Count);

        return subjectIds;
    }
}
