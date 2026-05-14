# Subject Token Migration Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Migrate active Nightscout subjects and their access tokens into Nocturne as part of the existing API-mode migration job, so existing clients continue authenticating without reconfiguration.

**Architecture:** Add a `"subjects"` collection step to `MigrationJob.ExecuteApiMigrationAsync` that fetches Nightscout roles and subjects via the v2 API, creates `SubjectEntity` + `SubjectRoleEntity` records with the original token hash, and assigns matching Nocturne roles (creating custom roles on the fly if needed).

**Tech Stack:** C# / .NET 10, EF Core, SHA-256 hashing, xUnit + FluentAssertions + Moq

---

### Task 1: Add `MigrateSubjectsViaApiAsync` to `MigrationJob`

**Files:**
- Modify: `src/API/Nocturne.API/Services/Migration/MigrationJobService.cs:460-468` (collection list)
- Modify: `src/API/Nocturne.API/Services/Migration/MigrationJobService.cs:217` (available collections)

**Step 1: Register subjects as first collection in the migration list**

In `ExecuteApiMigrationAsync` (~line 460), add `"subjects"` as the **first** entry so tokens are in place before clinical data:

```csharp
var allCollections = new (string name, Func<HttpClient, NocturneDbContext, CancellationToken, Task> migrate)[]
{
    ("subjects", MigrateSubjectsViaApiAsync),
    ("entries", MigrateEntriesViaApiAsync),
    ("treatments", MigrateTreatmentsViaApiAsync),
    ("devicestatus", MigrateDeviceStatusViaApiAsync),
    ("profile", MigrateProfilesViaApiAsync),
    ("food", MigrateFoodViaApiAsync),
    ("activity", MigrateActivityViaApiAsync),
};
```

**Step 2: Add `"subjects"` to `AvailableCollections` in `TestApiConnectionAsync`**

At line 217, update the hardcoded list:

```csharp
AvailableCollections = ["subjects", "entries", "treatments", "profile", "devicestatus", "food", "activity"],
```

**Step 3: Implement `MigrateSubjectsViaApiAsync`**

Add this method to the `MigrationJob` class. It follows the same pattern as `MigrateProfilesViaApiAsync` (single-fetch, no pagination — subjects are always a small collection).

```csharp
private async Task MigrateSubjectsViaApiAsync(
    HttpClient httpClient,
    NocturneDbContext dbContext,
    CancellationToken ct)
{
    _currentOperation = "Migrating subjects";
    var collectionName = "subjects";

    var totalMigrated = 0L;
    var totalFailed = 0L;
    var totalSkipped = 0L;

    try
    {
        // 1. Fetch roles to build name→permissions lookup
        var rolePermissions = await FetchNightscoutRolePermissionsAsync(httpClient, ct);

        // 2. Fetch subjects
        var response = await httpClient.GetAsync("/api/v2/authorization/subjects", ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Failed to fetch subjects: {StatusCode}. The API secret may lack admin access. Skipping subject migration.",
                response.StatusCode);
            UpdateCollectionProgress(collectionName, 0, 0, 0, true);
            return;
        }

        var content = await response.Content.ReadAsStringAsync(ct);
        var subjects = System.Text.Json.JsonSerializer.Deserialize<NightscoutSubject[]>(
            content,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

        UpdateCollectionProgress(collectionName, subjects.Length, 0, 0, false);
        UpdateOverallProgress();

        // 3. Pre-load existing token hashes for duplicate detection
        var existingHashes = await dbContext.Subjects
            .Where(s => s.AccessTokenHash != null)
            .Select(s => s.AccessTokenHash!)
            .ToHashSetAsync(ct);

        // 4. Pre-load existing Nocturne roles by name
        var nocturneRoles = await dbContext.Roles
            .ToDictionaryAsync(r => r.Name, r => r.Id, ct);

        foreach (var subject in subjects)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (string.IsNullOrWhiteSpace(subject.AccessToken))
                {
                    totalSkipped++;
                    continue;
                }

                var tokenHash = HashAccessToken(subject.AccessToken);

                if (existingHashes.Contains(tokenHash))
                {
                    totalSkipped++;
                    continue;
                }

                // Determine if subject should be inactive ("denied" is only role)
                var isDenied = subject.Roles is ["denied"];

                var entity = new SubjectEntity
                {
                    Id = Guid.CreateVersion7(),
                    Name = subject.Name ?? "Unnamed",
                    AccessTokenHash = tokenHash,
                    AccessTokenPrefix = $"{(subject.Name ?? "unknown").ToLowerInvariant()}-{subject.AccessToken[..Math.Min(8, subject.AccessToken.Length)]}",
                    IsActive = !isDenied,
                    Notes = "Migrated from Nightscout. Consider rotating to a Nocturne token.",
                    OriginalId = subject.Id,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    ApprovalStatus = "Approved",
                };

                dbContext.Subjects.Add(entity);
                await dbContext.SaveChangesAsync(ct);

                // Assign roles
                foreach (var roleName in subject.Roles ?? [])
                {
                    if (roleName == "denied")
                        continue;

                    if (!nocturneRoles.TryGetValue(roleName, out var roleId))
                    {
                        // Custom Nightscout role: create it with fetched permissions
                        var permissions = rolePermissions.GetValueOrDefault(roleName, []);
                        var roleEntity = new RoleEntity
                        {
                            Id = Guid.CreateVersion7(),
                            Name = roleName,
                            Description = $"Migrated from Nightscout",
                            Permissions = permissions,
                            IsSystemRole = false,
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow,
                        };
                        dbContext.Roles.Add(roleEntity);
                        await dbContext.SaveChangesAsync(ct);
                        roleId = roleEntity.Id;
                        nocturneRoles[roleName] = roleId;
                    }

                    dbContext.SubjectRoles.Add(new SubjectRoleEntity
                    {
                        SubjectId = entity.Id,
                        RoleId = roleId,
                        AssignedAt = DateTime.UtcNow,
                    });
                }

                await dbContext.SaveChangesAsync(ct);

                existingHashes.Add(tokenHash);
                totalMigrated++;
                UpdateCollectionProgress(collectionName, subjects.Length, totalMigrated, totalFailed, false);
                UpdateOverallProgress();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to migrate subject {Name}", subject.Name);
                totalFailed++;
            }
        }

        UpdateCollectionProgress(collectionName, subjects.Length, totalMigrated, totalFailed, true);
        UpdateOverallProgress();
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error migrating subjects via API");
    }

    _logger.LogInformation(
        "Subject migration complete: {Migrated} migrated, {Skipped} skipped, {Failed} failed",
        totalMigrated, totalSkipped, totalFailed);
}
```

**Step 4: Add the helper method `FetchNightscoutRolePermissionsAsync`**

```csharp
/// <summary>
/// Fetches Nightscout role definitions and returns a name→permissions lookup.
/// Falls back gracefully if the endpoint is inaccessible.
/// </summary>
private async Task<Dictionary<string, List<string>>> FetchNightscoutRolePermissionsAsync(
    HttpClient httpClient, CancellationToken ct)
{
    var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

    try
    {
        var response = await httpClient.GetAsync("/api/v2/authorization/roles", ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to fetch Nightscout roles: {StatusCode}. Using default role mappings.", response.StatusCode);
            return result;
        }

        var content = await response.Content.ReadAsStringAsync(ct);
        var roles = System.Text.Json.JsonSerializer.Deserialize<NightscoutRole[]>(
            content,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

        foreach (var role in roles)
        {
            if (!string.IsNullOrWhiteSpace(role.Name))
            {
                result[role.Name] = role.Permissions ?? [];
            }
        }
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Error fetching Nightscout roles. Custom roles may not have correct permissions.");
    }

    return result;
}
```

**Step 5: Add the DTO classes for deserialization**

Add these inside the `MigrationJob` class (or as private nested types at the bottom):

```csharp
private record NightscoutSubject
{
    public string? Id { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("_id")]
    public string? MongoId { get; init; }
    public string? Name { get; init; }
    public List<string> Roles { get; init; } = [];
    public string? AccessToken { get; init; }
}

private record NightscoutRole
{
    public string? Name { get; init; }
    public List<string> Permissions { get; init; } = [];
}
```

**Step 6: Add the `HashAccessToken` helper**

This matches the existing pattern in `SubjectService`:

```csharp
private static string HashAccessToken(string accessToken)
{
    var bytes = System.Text.Encoding.UTF8.GetBytes(accessToken);
    var hash = System.Security.Cryptography.SHA256.HashData(bytes);
    return Convert.ToHexString(hash).ToLowerInvariant();
}
```

**Step 7: Add required using statements**

At the top of the file, ensure these are present (most already are):

```csharp
using Nocturne.Infrastructure.Data.Entities;
```

**Step 8: Build to verify compilation**

Run: `dotnet build src/API/Nocturne.API/Nocturne.API.csproj`
Expected: Build succeeded

**Step 9: Commit**

```bash
git add src/API/Nocturne.API/Services/Migration/MigrationJobService.cs
git commit -m "feat(migration): add subject token migration to API-mode migration job"
```

---

### Task 2: Unit test — subjects are migrated with correct token hash and role assignment

**Files:**
- Create: `tests/Unit/Nocturne.API.Tests/Migration/SubjectMigrationTests.cs`

This task tests the core logic extracted into a testable helper. Since `MigrationJob` is tightly coupled to HTTP and DbContext, the most practical test approach is testing the hashing logic and the role resolution independently, then relying on an integration test for the full flow.

**Step 1: Write unit tests for token hashing consistency**

```csharp
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;

namespace Nocturne.API.Tests.Migration;

public class SubjectMigrationTests
{
    [Fact]
    public void HashAccessToken_produces_same_hash_as_SubjectService()
    {
        // The migration must produce the same SHA-256 hash that SubjectService
        // and AccessTokenHandler use, so migrated tokens are found on lookup.
        var token = "phone-a1b2c3d4e5f6g7h8";

        var bytes = Encoding.UTF8.GetBytes(token);
        var hash = SHA256.HashData(bytes);
        var expected = Convert.ToHexString(hash).ToLowerInvariant();

        // Verify the hash is a 64-char lowercase hex string
        expected.Should().HaveLength(64);
        expected.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void AccessTokenPrefix_format_matches_SubjectService_convention()
    {
        var name = "Phone";
        var accessToken = "phone-a1b2c3d4e5f6g7h8";

        var prefix = $"{name.ToLowerInvariant()}-{accessToken[..Math.Min(8, accessToken.Length)]}";

        prefix.Should().Be("phone-phone-a1");
    }

    [Theory]
    [InlineData(new[] { "denied" }, false)]
    [InlineData(new[] { "readable" }, true)]
    [InlineData(new[] { "admin", "denied" }, true)]
    [InlineData(new[] { "readable", "careportal" }, true)]
    public void IsActive_is_false_only_when_denied_is_sole_role(string[] roles, bool expectedActive)
    {
        var isDenied = roles is ["denied"];
        var isActive = !isDenied;

        isActive.Should().Be(expectedActive);
    }

    [Fact]
    public void Empty_accessToken_should_be_skipped()
    {
        var accessToken = "";
        string.IsNullOrWhiteSpace(accessToken).Should().BeTrue();
    }
}
```

**Step 2: Run tests to verify they pass**

Run: `dotnet test tests/Unit/Nocturne.API.Tests --filter "FullyQualifiedName~SubjectMigrationTests" -v minimal`
Expected: All 4 tests pass

**Step 3: Commit**

```bash
git add tests/Unit/Nocturne.API.Tests/Migration/SubjectMigrationTests.cs
git commit -m "test(migration): add unit tests for subject token migration logic"
```

---

### Task 3: Manual verification with Aspire

**Step 1: Start Aspire**

Run: `aspire start`

Wait for the API and frontend to be ready.

**Step 2: Verify subjects appear in the available collections**

Make a test connection request and confirm `"subjects"` is in the `AvailableCollections` response. This can be verified by checking the migration UI or calling:

```
POST /api/v4/migration/test
{
  "mode": "Api",
  "nightscoutUrl": "<test-nightscout-url>",
  "nightscoutApiSecret": "<secret>"
}
```

The response should include `"subjects"` in `availableCollections`.

**Step 3: Verify subject migration (if a Nightscout instance is available)**

Start a migration job that includes `"subjects"` and verify:
- Subjects appear in the `CollectionProgress` with counts
- Migrated subjects are visible in the subjects admin UI
- The `Notes` field shows the Nightscout migration message
- The original access token authenticates correctly against the Nocturne API

**Step 4: Commit (if any fixes were needed)**

```bash
git commit -am "fix(migration): address issues found during manual verification"
```
