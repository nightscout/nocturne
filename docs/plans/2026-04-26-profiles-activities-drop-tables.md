# Profiles & Activities Table Drop — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Drop the legacy `profiles` and `activities` PostgreSQL tables, replacing profile reads with focused stateless resolvers backed by V4 schedule repos, and maintaining V1/V3 API compatibility via projection.

**Architecture:** Activities are already decomposed (StateSpan + HeartRate + StepCount) — the table is dead storage, just delete. Profiles are decomposed into 7 focused resolvers (BasalRate, Sensitivity, CarbRatio, TargetRange, TherapySettings, ActiveProfile, TempBasal) that query V4 schedule repos directly. V1/V3 endpoints reconstruct the monolithic Profile shape via ProfileProjectionService for backward compatibility.

**Tech Stack:** C# / .NET 10, EF Core, xUnit, Moq, FluentAssertions

**Worktree:** `c:/Users/rhysg/Documents/Github/nocturne/.worktrees/drop-entries-table` (branch: `feature/v1-deprecation`)

**Design doc:** `docs/plans/2026-04-26-profiles-activities-drop-tables-design.md`

---

## Phase 1: Activities Table Drop

Activities are already decomposed — `ActivityService` merges StateSpans + HeartRate + StepCount. The `activities` table and `IActivityRepository` are legacy dead code.

### Task 1: Migrate CountController and DDataService off IActivityRepository

**Files:**
- Modify: `src/API/Nocturne.API/Controllers/V1/CountController.cs` (lines 28, 47, 216, 314)
- Modify: `src/API/Nocturne.API/Services/Legacy/DDataService.cs` (lines 24, 46, 76)

**Step 1: Migrate CountController**

Replace `IActivityRepository` with `IActivityService` in CountController. The activity count endpoints (`CountActivity` at line 216, `CountGeneric` "activity" case at line 314) call `_activityRepository.CountActivitiesAsync(find, ct)`.

`IActivityService` doesn't currently expose a count method. Add `CountActivitiesAsync(string? find, CancellationToken ct)` to `IActivityService` interface and implement in `ActivityService` by delegating to `IStateSpanService` count with `StateSpanCategory.Profile` filter (activities are StateSpans).

Check the existing `ActivityService` to see if it already has a count method or if one needs adding. The count should include StateSpans + HeartRate + StepCount to match what the legacy repo counted.

Remove `IActivityRepository` field and constructor parameter from CountController.

**Step 2: Migrate DDataService**

`DDataService` uses `IActivityRepository` at line 76 in `LoadActivityAsync`. Replace with `IActivityService.GetActivitiesAsync()`. Remove `IActivityRepository` field and constructor parameter.

**Step 3: Build and test**

```bash
dotnet build src/API/Nocturne.API/Nocturne.API.csproj 2>&1 | tail -5
dotnet test --filter "FullyQualifiedName~CountController" --no-restore 2>&1 | tail -5
dotnet test --filter "FullyQualifiedName~DDataService" --no-restore 2>&1 | tail -5
```

**Step 4: Commit**

```bash
git commit -m "refactor(activities): migrate CountController and DDataService off IActivityRepository"
```

---

### Task 2: Delete activity dead code

**Files to DELETE:**
- `src/Infrastructure/Nocturne.Infrastructure.Data/Entities/ActivityEntity.cs`
- `src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/ActivityRepository.cs`
- `src/Infrastructure/Nocturne.Infrastructure.Data/Mappers/ActivityMapper.cs`
- `src/Core/Nocturne.Core.Contracts/Repositories/IActivityRepository.cs`
- Related test files for ActivityRepository

**Files to MODIFY:**
- `src/Infrastructure/Nocturne.Infrastructure.Data/NocturneDbContext.cs` — remove `DbSet<ActivityEntity> Activities` (line 77), remove modelBuilder config for ActivityEntity (lines 700-720), remove global query filter (line 1839), remove property configs (search for ActivityEntity)
- `src/Infrastructure/Nocturne.Infrastructure.Data/Extensions/ServiceCollectionExtensions.cs` — remove `IActivityRepository` DI registration (line 258)
- Fix all compilation errors from deleted references

**Step 1: Delete files**

Delete the 4 files listed above plus any ActivityRepository test files.

**Step 2: Clean up NocturneDbContext**

Remove DbSet, modelBuilder config, query filter, property configs for ActivityEntity. Search for "Activity" in the file to catch all references.

**Step 3: Remove DI registration**

Remove `services.AddScoped<IActivityRepository, ActivityRepository>();` from ServiceCollectionExtensions.cs.

**Step 4: Build and fix**

```bash
dotnet build 2>&1 | tail -20
```

Fix any remaining compilation errors. There may be references in test files that need updating.

**Step 5: Run tests**

```bash
dotnet test --filter "Category!=Integration&Category!=Performance" 2>&1 | tail -10
```

**Step 6: Commit**

```bash
git commit -m "refactor: delete IActivityRepository, ActivityRepository, ActivityEntity, ActivityMapper"
```

---

### Task 3: DROP TABLE activities migration

**Step 1: Build without NSwag**

```bash
dotnet build -p:GenerateNSwagClient=false
```

**Step 2: Scaffold migration**

```bash
dotnet ef migrations add DropActivitiesTable \
    -p src/Infrastructure/Nocturne.Infrastructure.Data \
    -s src/API/Nocturne.API \
    --no-build
```

**Step 3: Verify migration**

Read the generated migration file. Confirm it drops the `activities` table. If EF includes unrelated changes, manually trim the migration to only contain the table drop.

**Step 4: Commit**

```bash
git commit -m "feat(db): add DropActivitiesTable migration"
```

---

## Phase 2: Profile Resolver Infrastructure

### Task 4: Create ScheduleResolution static utility

The core time-slot resolution algorithm, extracted from ProfileService.GetValueFromContainer (lines 755-800). Used by all schedule resolvers.

**Files:**
- Create: `src/API/Nocturne.API/Services/Profiles/Resolvers/ScheduleResolution.cs`
- Create: `tests/Unit/Nocturne.API.Tests/Services/Profiles/Resolvers/ScheduleResolutionTests.cs`

**Step 1: Write failing tests**

Test cases:
- Single entry at midnight → returns that value for any time
- Multiple entries (00:00=1.0, 06:00=0.8, 22:00=1.2) → returns correct value at 03:00, 12:00, 23:00
- Time exactly on boundary → returns that boundary's value
- Empty entries → returns null (caller provides default)
- TargetRange variant → returns (Low, High) tuple

```csharp
namespace Nocturne.API.Tests.Services.Profiles.Resolvers;

public class ScheduleResolutionTests
{
    [Fact]
    public void FindValueAtTime_SingleEntry_ReturnsValue()
    {
        var entries = new List<ScheduleEntry>
        {
            new() { Time = "00:00", Value = 1.0, TimeAsSeconds = 0 }
        };

        var result = ScheduleResolution.FindValueAtTime(entries, 43200); // noon
        result.Should().Be(1.0);
    }

    [Fact]
    public void FindValueAtTime_MultipleEntries_ReturnsCorrectSlot()
    {
        var entries = new List<ScheduleEntry>
        {
            new() { Time = "00:00", Value = 1.0, TimeAsSeconds = 0 },
            new() { Time = "06:00", Value = 0.8, TimeAsSeconds = 21600 },
            new() { Time = "22:00", Value = 1.2, TimeAsSeconds = 79200 },
        };

        ScheduleResolution.FindValueAtTime(entries, 10800).Should().Be(1.0);  // 03:00
        ScheduleResolution.FindValueAtTime(entries, 43200).Should().Be(0.8);  // 12:00
        ScheduleResolution.FindValueAtTime(entries, 82800).Should().Be(1.2);  // 23:00
    }

    [Fact]
    public void FindValueAtTime_ExactBoundary_ReturnsBoundaryValue()
    {
        var entries = new List<ScheduleEntry>
        {
            new() { Time = "00:00", Value = 1.0, TimeAsSeconds = 0 },
            new() { Time = "06:00", Value = 0.8, TimeAsSeconds = 21600 },
        };

        ScheduleResolution.FindValueAtTime(entries, 21600).Should().Be(0.8);
    }

    [Fact]
    public void FindValueAtTime_EmptyEntries_ReturnsNull()
    {
        var result = ScheduleResolution.FindValueAtTime(new List<ScheduleEntry>(), 43200);
        result.Should().BeNull();
    }

    [Fact]
    public void FindRangeAtTime_ReturnsLowHighPair()
    {
        var entries = new List<TargetRangeEntry>
        {
            new() { Time = "00:00", Low = 70, High = 180, TimeAsSeconds = 0 },
            new() { Time = "08:00", Low = 80, High = 150, TimeAsSeconds = 28800 },
        };

        var (low, high) = ScheduleResolution.FindRangeAtTime(entries, 43200); // noon
        low.Should().Be(80);
        high.Should().Be(150);
    }
}
```

**Step 2: Run tests to verify they fail**

```bash
dotnet test --filter "FullyQualifiedName~ScheduleResolutionTests" --no-restore 2>&1 | tail -5
```

**Step 3: Implement ScheduleResolution**

```csharp
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Profiles.Resolvers;

/// <summary>
/// Shared time-of-day schedule resolution algorithm.
/// Finds the most recent schedule entry at-or-before a given time of day.
/// </summary>
internal static class ScheduleResolution
{
    /// <summary>
    /// Returns the value from the most recent schedule entry at-or-before
    /// <paramref name="secondsFromMidnight"/>, or null if entries is empty.
    /// Entries must have TimeAsSeconds populated.
    /// </summary>
    public static double? FindValueAtTime(List<ScheduleEntry> entries, int secondsFromMidnight)
    {
        if (entries.Count == 0)
            return null;

        var sorted = entries.OrderBy(e => e.TimeAsSeconds ?? 0).ToList();
        var value = sorted[0].Value;

        foreach (var entry in sorted)
        {
            if (secondsFromMidnight >= (entry.TimeAsSeconds ?? 0))
                value = entry.Value;
            else
                break;
        }

        return value;
    }

    /// <summary>
    /// Returns the (Low, High) target range from the most recent entry
    /// at-or-before <paramref name="secondsFromMidnight"/>.
    /// </summary>
    public static (double Low, double High)? FindRangeAtTime(
        List<TargetRangeEntry> entries,
        int secondsFromMidnight)
    {
        if (entries.Count == 0)
            return null;

        var sorted = entries.OrderBy(e => e.TimeAsSeconds ?? 0).ToList();
        var low = sorted[0].Low;
        var high = sorted[0].High;

        foreach (var entry in sorted)
        {
            if (secondsFromMidnight >= (entry.TimeAsSeconds ?? 0))
            {
                low = entry.Low;
                high = entry.High;
            }
            else
            {
                break;
            }
        }

        return (low, high);
    }
}
```

**Step 4: Run tests to verify they pass**

```bash
dotnet test --filter "FullyQualifiedName~ScheduleResolutionTests" --no-restore 2>&1 | tail -5
```

**Step 5: Commit**

```bash
git commit -m "feat(profiles): add ScheduleResolution static utility for time-of-day lookups"
```

---

### Task 5: Add GetActiveAtAsync to V4 schedule repositories

Add a `GetActiveAtAsync(string profileName, DateTime timestamp)` method to each V4 schedule repository interface and implementation. Returns the most recent record where `ProfileName == profileName && Timestamp <= timestamp`.

**Files:**
- Modify: `src/Core/Nocturne.Core.Contracts/V4/Repositories/IBasalScheduleRepository.cs`
- Modify: `src/Core/Nocturne.Core.Contracts/V4/Repositories/ICarbRatioScheduleRepository.cs`
- Modify: `src/Core/Nocturne.Core.Contracts/V4/Repositories/ISensitivityScheduleRepository.cs`
- Modify: `src/Core/Nocturne.Core.Contracts/V4/Repositories/ITargetRangeScheduleRepository.cs`
- Modify: `src/Core/Nocturne.Core.Contracts/V4/Repositories/ITherapySettingsRepository.cs`
- Modify: Corresponding repository implementations in `src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/V4/`

**Step 1: Add method to all 5 interfaces**

Add to each interface:

```csharp
/// <summary>
/// Returns the most recent schedule record for the given profile name
/// that was active at-or-before the specified timestamp.
/// </summary>
Task<T?> GetActiveAtAsync(string profileName, DateTime timestamp, CancellationToken ct = default);
```

Where `T` is the respective model type (BasalSchedule, CarbRatioSchedule, etc.).

**Step 2: Implement in all 5 repositories**

Each implementation follows the same pattern. Example for BasalScheduleRepository:

```csharp
public async Task<BasalSchedule?> GetActiveAtAsync(
    string profileName, DateTime timestamp, CancellationToken ct = default)
{
    var entity = await _context.BasalSchedules
        .Where(e => e.ProfileName == profileName && e.Timestamp <= timestamp)
        .OrderByDescending(e => e.Timestamp)
        .FirstOrDefaultAsync(ct);

    return entity is null ? null : BasalScheduleMapper.ToDomainModel(entity);
}
```

Repeat for all 5 repositories with appropriate entity types.

**Step 3: Build**

```bash
dotnet build 2>&1 | tail -5
```

**Step 4: Commit**

```bash
git commit -m "feat(profiles): add GetActiveAtAsync to V4 schedule repositories"
```

---

### Task 6: Create IActiveProfileResolver

Resolves which named profile is active at time T by querying Profile StateSpan records.

**Files:**
- Create: `src/Core/Nocturne.Core.Contracts/Profiles/Resolvers/IActiveProfileResolver.cs`
- Create: `src/API/Nocturne.API/Services/Profiles/Resolvers/ActiveProfileResolver.cs`
- Create: `tests/Unit/Nocturne.API.Tests/Services/Profiles/Resolvers/ActiveProfileResolverTests.cs`

**Step 1: Define the interface**

```csharp
using Nocturne.API.Services.Profiles.Resolvers;

namespace Nocturne.Core.Contracts.Profiles.Resolvers;

/// <summary>
/// Resolves which named profile is active at a given time
/// by querying Profile StateSpan records (profile switch events).
/// </summary>
public interface IActiveProfileResolver
{
    /// <summary>
    /// Returns the name of the active profile at the given Unix millisecond timestamp,
    /// or null if no profile switch is active (meaning "Default" should be used).
    /// </summary>
    Task<string?> GetActiveProfileNameAsync(long timeMills, CancellationToken ct = default);

    /// <summary>
    /// Returns CircadianPercentageProfile adjustments if active at the given time, or null.
    /// </summary>
    Task<CircadianAdjustment?> GetCircadianAdjustmentAsync(long timeMills, CancellationToken ct = default);
}

/// <summary>
/// CircadianPercentageProfile modifier parameters.
/// </summary>
public record CircadianAdjustment(double Percentage, long TimeshiftMs);
```

**Step 2: Write failing tests**

Test cases:
- No profile switches → returns null (default profile)
- Active profile switch → returns profile name from StateSpan metadata
- Profile switch with duration expired → returns null
- CircadianPercentageProfile → returns adjustment with percentage and timeshift
- No CCP → returns null adjustment

Use Moq to mock `IStateSpanService`.

**Step 3: Implement ActiveProfileResolver**

```csharp
using Microsoft.Extensions.Caching.Memory;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Security;

namespace Nocturne.API.Services.Profiles.Resolvers;

internal sealed class ActiveProfileResolver(
    IStateSpanService stateSpanService,
    ITenantAccessor tenantAccessor,
    IMemoryCache cache
) : IActiveProfileResolver
{
    private const int CacheTtlMs = 5000;

    public async Task<string?> GetActiveProfileNameAsync(long timeMills, CancellationToken ct = default)
    {
        var stateSpan = await GetActiveProfileStateSpanAsync(timeMills, ct);
        if (stateSpan?.Metadata == null) return null;

        return stateSpan.Metadata.TryGetValue("profileName", out var name) ? name?.ToString() : null;
    }

    public async Task<CircadianAdjustment?> GetCircadianAdjustmentAsync(long timeMills, CancellationToken ct = default)
    {
        var stateSpan = await GetActiveProfileStateSpanAsync(timeMills, ct);
        if (stateSpan?.Metadata == null) return null;

        if (!stateSpan.Metadata.TryGetValue("percentage", out var pctObj) || pctObj is not double percentage)
            return null;

        var timeshiftHours = stateSpan.Metadata.TryGetValue("timeshift", out var tsObj) && tsObj is double ts ? ts : 0.0;
        var timeshiftMs = (long)((timeshiftHours % 24) * 3600000);

        return new CircadianAdjustment(percentage, timeshiftMs);
    }

    private async Task<StateSpan?> GetActiveProfileStateSpanAsync(long timeMills, CancellationToken ct)
    {
        var minuteTime = (long)(Math.Round(timeMills / 60000.0) * 60000);
        var cacheKey = $"activeProfile:{tenantAccessor.TenantId}:{minuteTime}";

        if (cache.TryGetValue(cacheKey, out StateSpan? cached))
            return cached;

        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timeMills).UtcDateTime;

        // Query profile StateSpans active at this time
        var spans = await stateSpanService.GetStateSpansAsync(
            category: StateSpanCategory.Profile,
            from: null,
            to: timestamp,
            active: null,
            count: 10,
            skip: 0,
            cancellationToken: ct);

        // Find the most recent span that covers the requested time
        var activeSpan = spans
            .Where(s => s.StartTimestamp <= timestamp)
            .Where(s => s.EndTimestamp == null || s.EndTimestamp > timestamp)
            .OrderByDescending(s => s.StartTimestamp)
            .FirstOrDefault();

        cache.Set(cacheKey, activeSpan, TimeSpan.FromMilliseconds(CacheTtlMs));
        return activeSpan;
    }
}
```

Note: The exact StateSpan metadata keys ("profileName", "percentage", "timeshift") must match what `TreatmentDecomposer.BuildProfileMetadata()` writes. Read lines 812-834 of TreatmentDecomposer.cs to verify the exact key names.

**Step 4: Run tests**

```bash
dotnet test --filter "FullyQualifiedName~ActiveProfileResolverTests" --no-restore 2>&1 | tail -5
```

**Step 5: Commit**

```bash
git commit -m "feat(profiles): add IActiveProfileResolver backed by Profile StateSpans"
```

---

### Task 7: Create IBasalRateResolver

Template for all schedule resolvers. Sensitivity, CarbRatio follow the same pattern — only the repo, model type, and CCP adjustment formula differ.

**Files:**
- Create: `src/Core/Nocturne.Core.Contracts/Profiles/Resolvers/IBasalRateResolver.cs`
- Create: `src/API/Nocturne.API/Services/Profiles/Resolvers/BasalRateResolver.cs`
- Create: `tests/Unit/Nocturne.API.Tests/Services/Profiles/Resolvers/BasalRateResolverTests.cs`

**Step 1: Define interface**

```csharp
namespace Nocturne.Core.Contracts.Profiles.Resolvers;

public interface IBasalRateResolver
{
    Task<double> GetBasalRateAsync(long timeMills, string? specProfile = null, CancellationToken ct = default);
}
```

**Step 2: Write failing tests**

Test cases:
- Returns correct basal rate for time of day from V4 schedule
- Uses active profile name from IActiveProfileResolver when specProfile is null
- Uses specProfile directly when provided
- Applies CircadianPercentageProfile: `value * percentage / 100`
- Returns default (1.0) when no schedule exists
- Caches schedule per tenant+profileName

**Step 3: Implement BasalRateResolver**

```csharp
using Microsoft.Extensions.Caching.Memory;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Security;

namespace Nocturne.API.Services.Profiles.Resolvers;

internal sealed class BasalRateResolver(
    IBasalScheduleRepository basalScheduleRepo,
    IActiveProfileResolver activeProfileResolver,
    ITenantAccessor tenantAccessor,
    IMemoryCache cache
) : IBasalRateResolver
{
    private const double DefaultBasalRate = 1.0;
    private const int CacheTtlMs = 5000;

    public async Task<double> GetBasalRateAsync(
        long timeMills, string? specProfile = null, CancellationToken ct = default)
    {
        // Resolve profile name
        var profileName = specProfile
            ?? await activeProfileResolver.GetActiveProfileNameAsync(timeMills, ct)
            ?? "Default";

        // Check for CircadianPercentageProfile adjustment
        var adjustment = await activeProfileResolver.GetCircadianAdjustmentAsync(timeMills, ct);
        var adjustedTime = timeMills + (adjustment?.TimeshiftMs ?? 0);

        // Load schedule (cached)
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(adjustedTime).UtcDateTime;
        var schedule = await GetCachedScheduleAsync(profileName, timestamp, ct);

        if (schedule?.Entries is null || schedule.Entries.Count == 0)
            return DefaultBasalRate;

        // Resolve time of day
        var timezone = schedule.Entries.Count > 0 ? "UTC" : "UTC"; // timezone comes from TherapySettings
        var secondsFromMidnight = GetSecondsFromMidnight(adjustedTime);

        var value = ScheduleResolution.FindValueAtTime(schedule.Entries, secondsFromMidnight)
            ?? DefaultBasalRate;

        // Apply CCP: basal uses direct formula
        if (adjustment != null)
            value = value * adjustment.Percentage / 100;

        return value;
    }

    private async Task<BasalSchedule?> GetCachedScheduleAsync(
        string profileName, DateTime timestamp, CancellationToken ct)
    {
        var cacheKey = $"basalSchedule:{tenantAccessor.TenantId}:{profileName}";

        if (cache.TryGetValue(cacheKey, out BasalSchedule? cached))
            return cached;

        var schedule = await basalScheduleRepo.GetActiveAtAsync(profileName, timestamp, ct);
        cache.Set(cacheKey, schedule, TimeSpan.FromMilliseconds(CacheTtlMs));
        return schedule;
    }

    private static int GetSecondsFromMidnight(long timeMills)
    {
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(timeMills);
        return (int)dt.TimeOfDay.TotalSeconds;
    }
}
```

**Important note on timezone:** The `GetSecondsFromMidnight` calculation needs the profile's timezone to convert UTC to local time. The current ProfileService gets timezone from the profile itself. BasalRateResolver will need `ITherapySettingsResolver` for timezone. This creates a circular dependency concern — resolve by having a shared timezone lookup method or by passing timezone as a parameter. Check how this is best handled during implementation.

**Step 4: Run tests**

```bash
dotnet test --filter "FullyQualifiedName~BasalRateResolverTests" --no-restore 2>&1 | tail -5
```

**Step 5: Commit**

```bash
git commit -m "feat(profiles): add IBasalRateResolver backed by V4 BasalScheduleRepository"
```

---

### Task 8: Create ISensitivityResolver

Same pattern as BasalRateResolver. CCP formula is **inverse**: `value * 100 / percentage`.

**Files:**
- Create: `src/Core/Nocturne.Core.Contracts/Profiles/Resolvers/ISensitivityResolver.cs`
- Create: `src/API/Nocturne.API/Services/Profiles/Resolvers/SensitivityResolver.cs`
- Create: `tests/Unit/Nocturne.API.Tests/Services/Profiles/Resolvers/SensitivityResolverTests.cs`

**Step 1:** Define interface — `GetSensitivityAsync(long timeMills, string? specProfile = null, CancellationToken ct = default)` returning `Task<double>`.

**Step 2:** Write failing tests — same test cases as BasalRateResolver but with inverse CCP formula and default value of 50.0.

**Step 3:** Implement — clone BasalRateResolver pattern, swap repo to `ISensitivityScheduleRepository`, CCP formula to `value * 100 / percentage`, default to 50.0.

**Step 4:** Run tests, commit.

```bash
git commit -m "feat(profiles): add ISensitivityResolver backed by V4 SensitivityScheduleRepository"
```

---

### Task 9: Create ICarbRatioResolver

Same pattern. CCP formula is **inverse**: `value * 100 / percentage`.

**Files:**
- Create: `src/Core/Nocturne.Core.Contracts/Profiles/Resolvers/ICarbRatioResolver.cs`
- Create: `src/API/Nocturne.API/Services/Profiles/Resolvers/CarbRatioResolver.cs`
- Create: `tests/Unit/Nocturne.API.Tests/Services/Profiles/Resolvers/CarbRatioResolverTests.cs`

**Step 1:** Interface — `GetCarbRatioAsync(long timeMills, string? specProfile, CancellationToken ct)` → `Task<double>`.

**Step 2:** Tests — inverse CCP, default 12.0.

**Step 3:** Implement — swap repo to `ICarbRatioScheduleRepository`.

**Step 4:** Run tests, commit.

```bash
git commit -m "feat(profiles): add ICarbRatioResolver backed by V4 CarbRatioScheduleRepository"
```

---

### Task 10: Create ITargetRangeResolver

Different entry shape — `TargetRangeEntry` has `Low`/`High` instead of `Value`. Uses `ScheduleResolution.FindRangeAtTime`.

**Files:**
- Create: `src/Core/Nocturne.Core.Contracts/Profiles/Resolvers/ITargetRangeResolver.cs`
- Create: `src/API/Nocturne.API/Services/Profiles/Resolvers/TargetRangeResolver.cs`
- Create: `tests/Unit/Nocturne.API.Tests/Services/Profiles/Resolvers/TargetRangeResolverTests.cs`

**Step 1: Define interface**

```csharp
namespace Nocturne.Core.Contracts.Profiles.Resolvers;

public interface ITargetRangeResolver
{
    Task<double> GetLowBGTargetAsync(long timeMills, string? specProfile = null, CancellationToken ct = default);
    Task<double> GetHighBGTargetAsync(long timeMills, string? specProfile = null, CancellationToken ct = default);
}
```

**Step 2:** Tests — default Low=70.0, High=180.0. CCP does NOT adjust targets.

**Step 3:** Implement — uses `ITargetRangeScheduleRepository`, `FindRangeAtTime`.

**Step 4:** Run tests, commit.

```bash
git commit -m "feat(profiles): add ITargetRangeResolver backed by V4 TargetRangeScheduleRepository"
```

---

### Task 11: Create ITherapySettingsResolver

Handles scalar values: DIA, CarbAbsorptionRate, Timezone, Units. DIA has the priority chain with PatientInsulin.

**Files:**
- Create: `src/Core/Nocturne.Core.Contracts/Profiles/Resolvers/ITherapySettingsResolver.cs`
- Create: `src/API/Nocturne.API/Services/Profiles/Resolvers/TherapySettingsResolver.cs`
- Create: `tests/Unit/Nocturne.API.Tests/Services/Profiles/Resolvers/TherapySettingsResolverTests.cs`

**Step 1: Define interface**

```csharp
namespace Nocturne.Core.Contracts.Profiles.Resolvers;

public interface ITherapySettingsResolver
{
    Task<double> GetDIAAsync(long timeMills, string? specProfile = null, CancellationToken ct = default);
    Task<double> GetCarbAbsorptionRateAsync(long timeMills, string? specProfile = null, CancellationToken ct = default);
    Task<string?> GetTimezoneAsync(string? specProfile = null, CancellationToken ct = default);
    Task<string?> GetUnitsAsync(string? specProfile = null, CancellationToken ct = default);
    Task<bool> HasDataAsync(CancellationToken ct = default);
}
```

**Step 2: Write failing tests**

Test the DIA priority chain:
- ExternallyManaged=true → returns TherapySettings.Dia regardless of PatientInsulin
- ExternallyManaged=false, PatientInsulin exists → returns PatientInsulin.Dia
- ExternallyManaged=false, no PatientInsulin → returns TherapySettings.Dia
- No TherapySettings → returns default 3.0
- CarbAbsorptionRate → returns TherapySettings.CarbsHr, default 20.0
- Timezone/Units → returns TherapySettings values

**Step 3: Implement TherapySettingsResolver**

Dependencies: `ITherapySettingsRepository`, `IPatientInsulinRepository`, `IActiveProfileResolver`, `ITenantAccessor`, `IMemoryCache`.

DIA resolution:
```csharp
public async Task<double> GetDIAAsync(long timeMills, string? specProfile = null, CancellationToken ct = default)
{
    var settings = await GetCachedSettingsAsync(timeMills, specProfile, ct);

    if (settings?.IsExternallyManaged == true)
        return settings.Dia > 0 ? settings.Dia : DefaultDia;

    var insulin = await GetCachedPrimaryInsulinAsync(ct);
    if (insulin?.Dia is > 0)
        return insulin.Dia.Value;

    return settings?.Dia is > 0 ? settings.Dia : DefaultDia;
}
```

**Step 4:** Run tests, commit.

```bash
git commit -m "feat(profiles): add ITherapySettingsResolver with DIA priority chain"
```

---

### Task 12: Create ITempBasalResolver

Composite resolver — combines `IBasalRateResolver` scheduled rate with active TempBasal V4 records.

**Files:**
- Create: `src/Core/Nocturne.Core.Contracts/Profiles/Resolvers/ITempBasalResolver.cs`
- Create: `src/API/Nocturne.API/Services/Profiles/Resolvers/TempBasalResolver.cs`
- Create: `tests/Unit/Nocturne.API.Tests/Services/Profiles/Resolvers/TempBasalResolverTests.cs`

**Step 1: Define interface**

```csharp
namespace Nocturne.Core.Contracts.Profiles.Resolvers;

public interface ITempBasalResolver
{
    Task<TempBasalResult> GetTempBasalAsync(long timeMills, string? specProfile = null, CancellationToken ct = default);
}
```

Reuse the existing `TempBasalResult` model if it exists in Core.Models, or define it here.

**Step 2: Write failing tests**

Test cases:
- No active temp basal → returns scheduled basal only
- Active temp basal with Absolute rate → returns that rate
- Active temp basal with Percent → basal * (100 + percent) / 100
- Combo bolus active → includes combo portion in result

**Step 3: Implement**

Dependencies: `IBasalRateResolver`, `ITempBasalRepository`.

Query `ITempBasalRepository` for the active TempBasal at the given time. Apply the same priority chain from ProfileService.GetTempBasal (Absolute → Rate → Percent → Amount → Calculated).

The binary search optimization from the old ProfileService is not needed here since we're querying the V4 repo directly (which does the time-range query in SQL).

**Step 4:** Run tests, commit.

```bash
git commit -m "feat(profiles): add ITempBasalResolver composing BasalRate + V4 TempBasal"
```

---

## Phase 3: V1/V3 API Compatibility

### Task 13: Create ProfileProjectionService

Reconstructs the monolithic Profile JSON from V4 schedule records for V1/V3 GET endpoints.

**Files:**
- Create: `src/API/Nocturne.API/Services/Profiles/ProfileProjectionService.cs`
- Create: `src/Core/Nocturne.Core.Contracts/Profiles/IProfileProjectionService.cs`
- Create: `tests/Unit/Nocturne.API.Tests/Services/Profiles/ProfileProjectionServiceTests.cs`

**Step 1: Define interface**

```csharp
namespace Nocturne.Core.Contracts.Profiles;

public interface IProfileProjectionService
{
    Task<Profile?> GetCurrentProfileAsync(CancellationToken ct = default);
    Task<Profile?> GetProfileByIdAsync(string id, CancellationToken ct = default);
    Task<IEnumerable<Profile>> GetProfilesAsync(int count = 10, int skip = 0, CancellationToken ct = default);
    Task<long> CountProfilesAsync(string? find = null, CancellationToken ct = default);
}
```

**Step 2: Write failing tests**

Test that projection correctly assembles:
- TherapySettings scalars → Profile.Store["Default"].Dia, CarbsHr, Timezone, Units
- BasalSchedule entries → Profile.Store["Default"].Basal (List<TimeValue>)
- CarbRatioSchedule entries → Profile.Store["Default"].CarbRatio
- SensitivitySchedule entries → Profile.Store["Default"].Sens
- TargetRangeSchedule entries → Profile.Store["Default"].TargetLow and TargetHigh
- ScheduleEntry → TimeValue mapping (Time, Value, TimeAsSeconds preserved)
- Multiple named profiles → multiple Store keys

**Step 3: Implement**

Query all 5 V4 repos, group by ProfileName from CorrelationId, assemble Profile domain model. Use TherapySettings.Timestamp as the Profile's Mills/StartDate. Map V4 ScheduleEntry back to legacy TimeValue.

For pagination/listing: query TherapySettings (profile record proxy) with pagination, then project each into a full Profile.

**Step 4:** Run tests, commit.

```bash
git commit -m "feat(profiles): add ProfileProjectionService for V1/V3 API compatibility"
```

---

### Task 14: Rename ProfileDataService → ProfileWriteService

Strip read methods. Keep write orchestration (create, update, delete with side effects).

**Files:**
- Modify: `src/API/Nocturne.API/Services/Profiles/ProfileDataService.cs` → rename to `ProfileWriteService.cs`
- Modify: `src/Core/Nocturne.Core.Contracts/Profiles/IProfileDataService.cs` → rename to `IProfileWriteService.cs`
- Modify: DI registration in `ServiceRegistrationExtensions.cs`
- Update all references

**Step 1: Rename interface**

Create `IProfileWriteService` with only write methods:
```csharp
public interface IProfileWriteService
{
    Task<IEnumerable<Profile>> CreateProfilesAsync(IEnumerable<Profile> profiles, CancellationToken ct = default);
    Task<Profile?> UpdateProfileAsync(string id, Profile profile, CancellationToken ct = default);
    Task<bool> DeleteProfileAsync(string id, CancellationToken ct = default);
}
```

**Step 2: Rename implementation**

Rename class and file. Remove `GetProfilesAsync`, `GetProfileByIdAsync`, `GetCurrentProfileAsync`, `GetProfileAtTimestampAsync`, `DeleteProfilesAsync`. Keep create/update/delete with side effects.

The write methods still need `IProfileRepository` temporarily for the underlying storage — the decomposer creates V4 records but the legacy write path may still write to the profiles table during the transition. This dependency is removed when we delete ProfileRepository in Phase 5.

Actually — since `ProfileEffectDescriptor.DecomposeToV4` will be flipped to true, writes go through the decomposer. But the write service also needs to store the V4 records. Check whether `ProfileDecomposer` handles the full write (upsert into V4 repos) or just creates the domain models. If the decomposer handles persistence, ProfileWriteService can drop the IProfileRepository dependency immediately.

**Step 3: Update DI and references**

Replace `IProfileDataService` with `IProfileWriteService` in controllers and DI.

Replace `IProfileDataService` read call sites (ProfileLoadStage, CacheWarmingService) with `IProfileProjectionService`.

**Step 4: Build, test, commit**

```bash
git commit -m "refactor(profiles): rename ProfileDataService to ProfileWriteService, strip read methods"
```

---

### Task 15: Flip ProfileEffectDescriptor.DecomposeToV4 to true

**Files:**
- Modify: `src/API/Nocturne.API/Services/Effects/ProfileEffectDescriptor.cs` (line 15)

**Step 1: Change the flag**

```csharp
// Before:
public bool DecomposeToV4 => false;

// After:
public bool DecomposeToV4 => true;
```

**Step 2: Verify ProfileDecomposer is wired in DI**

Grep for `IProfileDecomposer` registration. If it's not registered, add it.

**Step 3: Build, test, commit**

```bash
git commit -m "feat(profiles): enable V4 decomposition on profile writes"
```

---

### Task 16: Extend TreatmentDecomposer for inline profile JSON

When a profile switch treatment has `ProfileJson`, decompose the embedded schedule data into V4 schedule records.

**Files:**
- Modify: `src/API/Nocturne.API/Services/V4/TreatmentDecomposer.cs` (DecomposeProfileSwitchAsync, lines 509-527)
- Create: `tests/Unit/Nocturne.API.Tests/Services/V4/TreatmentDecomposerInlineProfileTests.cs`

**Step 1: Write failing test**

Test that a profile switch treatment with `ProfileJson` containing basal rates, carb ratios, etc. produces:
- A StateSpan (existing behavior)
- V4 schedule records with synthetic profile name `"{name}@@@@@{mills}"`

**Step 2: Extend DecomposeProfileSwitchAsync**

After creating the StateSpan, add:
```csharp
if (!string.IsNullOrEmpty(treatment.ProfileJson))
{
    var profileData = JsonSerializer.Deserialize<ProfileData>(treatment.ProfileJson);
    if (profileData != null)
    {
        var profileName = $"{treatment.Profile ?? "Default"}@@@@@{treatment.Mills}";
        // Build a temporary Profile object with just this one store entry
        var tempProfile = new Profile
        {
            Id = treatment.Id ?? Guid.NewGuid().ToString(),
            Mills = treatment.Mills,
            Store = new Dictionary<string, ProfileData> { [profileName] = profileData }
        };

        await _profileDecomposer.DecomposeAsync(tempProfile, ct);
    }
}
```

Inject `IProfileDecomposer` into TreatmentDecomposer constructor.

**Step 3: Run tests, commit**

```bash
git commit -m "feat(profiles): decompose inline profile JSON in profile switch treatments"
```

---

## Phase 4: Consumer Migration

### Task 17: Migrate ProfileLoadStage

**Files:**
- Modify: `src/API/Nocturne.API/Services/ChartData/Stages/ProfileLoadStage.cs`
- Modify: `tests/Unit/Nocturne.API.Tests/Services/ChartData/Stages/ProfileLoadStageTests.cs`

**Step 1: Replace dependencies**

Replace `IProfileDataService` + `IProfileService` with `ITherapySettingsResolver`, `ITargetRangeResolver`, `IBasalRateResolver`.

**Step 2: Rewrite ExecuteAsync**

```csharp
internal sealed class ProfileLoadStage(
    ITherapySettingsResolver therapySettings,
    ITargetRangeResolver targetRange,
    IBasalRateResolver basalRate,
    ILogger<ProfileLoadStage> logger
) : IChartDataStage
{
    private const double DefaultVeryLow = 54;
    private const double DefaultVeryHigh = 250;

    public async Task<ChartDataContext> ExecuteAsync(ChartDataContext context, CancellationToken ct)
    {
        var hasData = await therapySettings.HasDataAsync(ct);
        var timezone = hasData ? await therapySettings.GetTimezoneAsync(ct: ct) : null;

        ChartThresholdsDto thresholds;
        double defaultBasalRate;

        if (hasData)
        {
            thresholds = new ChartThresholdsDto
            {
                VeryLow = DefaultVeryLow,
                Low = await targetRange.GetLowBGTargetAsync(context.EndTime, ct: ct),
                High = await targetRange.GetHighBGTargetAsync(context.EndTime, ct: ct),
                VeryHigh = DefaultVeryHigh,
            };
            defaultBasalRate = await basalRate.GetBasalRateAsync(context.EndTime, ct: ct);
        }
        else
        {
            thresholds = new ChartThresholdsDto
            {
                VeryLow = DefaultVeryLow, Low = 70, High = 180, VeryHigh = DefaultVeryHigh,
            };
            defaultBasalRate = 1.0;
        }

        return context with { Timezone = timezone, Thresholds = thresholds, DefaultBasalRate = defaultBasalRate };
    }
}
```

**Step 3: Update tests, run, commit**

```bash
git commit -m "refactor(profiles): migrate ProfileLoadStage to V4 resolvers"
```

---

### Task 18: Migrate IobCobComputeStage

**Files:**
- Modify: `src/API/Nocturne.API/Services/ChartData/Stages/IobCobComputeStage.cs`
- Modify: corresponding test file

Replace `IProfileService` with `ITherapySettingsResolver` (for GetDIA) and `IBasalRateResolver` (for GetBasalRate at 5-min intervals). The `HasData()` guard becomes `therapySettings.HasDataAsync()`.

```bash
git commit -m "refactor(profiles): migrate IobCobComputeStage to V4 resolvers"
```

---

### Task 19: Migrate IobService

**Files:**
- Modify: `src/API/Nocturne.API/Services/Treatments/IobService.cs`
- Modify: corresponding test file

Replace `IProfileService` with `ITherapySettingsResolver` (GetDIA), `ISensitivityResolver` (GetSensitivity), `IBasalRateResolver` (GetBasalRate).

Note: IobService currently receives `IProfileService profile` as a method parameter in `CalculateTotal` and `FromTreatments`. The resolver dependencies should be injected via constructor instead. Update the method signatures to remove the profile parameter — callers no longer need to pass it.

```bash
git commit -m "refactor(profiles): migrate IobService to V4 resolvers"
```

---

### Task 20: Migrate CobService

**Files:**
- Modify: `src/API/Nocturne.API/Services/Treatments/CobService.cs`
- Modify: corresponding test file

Replace `IProfileService` with `ISensitivityResolver`, `ICarbRatioResolver`, `ITherapySettingsResolver` (CarbAbsorptionRate).

Same pattern as IobService — inject resolvers via constructor, remove profile parameter from method signatures.

```bash
git commit -m "refactor(profiles): migrate CobService to V4 resolvers"
```

---

### Task 21: Migrate PumpAlertService

**Files:**
- Modify: `src/API/Nocturne.API/Services/Monitoring/PumpAlertService.cs`

Only uses `GetTimezone()`. Replace `IProfileService` with `ITherapySettingsResolver.GetTimezoneAsync()`.

```bash
git commit -m "refactor(profiles): migrate PumpAlertService to ITherapySettingsResolver"
```

---

### Task 22: Migrate PredictionService

**Files:**
- Modify: `src/API/Nocturne.API/Services/Glucose/PredictionService.cs`
- Modify: corresponding test file

Currently uses `IProfileRepository` directly (line 185) to load profiles and manually build oref profile model. Replace with individual resolvers: `IBasalRateResolver`, `ISensitivityResolver`, `ICarbRatioResolver`, `ITargetRangeResolver`, `ITherapySettingsResolver`.

The oref profile model construction needs refactoring — instead of extracting arrays from ProfileData.Store, query each resolver for the time range needed. This may require adding a "get full schedule" method or iterating through the 24-hour day at appropriate resolution.

Remove `IProfileRepository` dependency.

```bash
git commit -m "refactor(profiles): migrate PredictionService from IProfileRepository to V4 resolvers"
```

---

### Task 23: Migrate V1 ProfileController

**Files:**
- Modify: `src/API/Nocturne.API/Controllers/V1/ProfileController.cs`

Replace `IProfileDataService` read calls with `IProfileProjectionService`. Replace write calls with `IProfileWriteService`.

```bash
git commit -m "refactor(profiles): migrate V1 ProfileController to ProjectionService + WriteService"
```

---

### Task 24: Migrate V3 ProfileController

**Files:**
- Modify: `src/API/Nocturne.API/Controllers/V3/ProfileController.cs`

Same pattern as V1. Advanced filtering queries `IProfileProjectionService` which uses TherapySettings as the profile record proxy.

```bash
git commit -m "refactor(profiles): migrate V3 ProfileController to ProjectionService + WriteService"
```

---

### Task 25: Remove profile warming from CacheWarmingService

**Files:**
- Modify: `src/Infrastructure/Nocturne.Infrastructure.Cache/Services/CacheWarmingService.cs`

Delete `WarmUserProfileAsync` method (lines 286-317). Remove `IProfileRepository` field and constructor parameter. Remove the `WarmUserProfileAsync` call from `WarmUserCacheAsync` (line 92).

```bash
git commit -m "refactor(profiles): remove profile warming from CacheWarmingService"
```

---

## Phase 5: Cleanup and DROP TABLE

### Task 26: Delete profile dead code

**Files to DELETE:**
- `src/Core/Nocturne.Core.Contracts/Profiles/IProfileService.cs`
- `src/API/Nocturne.API/Services/Profiles/ProfileService.cs`
- `src/Core/Nocturne.Core.Contracts/Repositories/IProfileRepository.cs`
- `src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/ProfileRepository.cs`
- `src/Infrastructure/Nocturne.Infrastructure.Data/Entities/ProfileEntity.cs`
- `src/Infrastructure/Nocturne.Infrastructure.Data/Mappers/ProfileMapper.cs`
- Old `IProfileDataService.cs` if not already deleted
- Related test files: `ProfileServiceTests.cs`, `ProfileDataServiceTests.cs`

**Files to MODIFY:**
- `NocturneDbContext.cs` — remove `DbSet<ProfileEntity> Profiles` (line 72), modelBuilder config (lines 679-697), query filter (line 1835), property configs
- `ServiceCollectionExtensions.cs` — remove `IProfileRepository` DI registration (line 255)
- `ServiceRegistrationExtensions.cs` — remove `IProfileService` registration (line 123), add new resolver DI registrations
- Fix all compilation errors

**Step 1: Add resolver DI registrations**

In ServiceRegistrationExtensions.cs:
```csharp
services.AddScoped<IActiveProfileResolver, ActiveProfileResolver>();
services.AddScoped<IBasalRateResolver, BasalRateResolver>();
services.AddScoped<ISensitivityResolver, SensitivityResolver>();
services.AddScoped<ICarbRatioResolver, CarbRatioResolver>();
services.AddScoped<ITargetRangeResolver, TargetRangeResolver>();
services.AddScoped<ITherapySettingsResolver, TherapySettingsResolver>();
services.AddScoped<ITempBasalResolver, TempBasalResolver>();
services.AddScoped<IProfileProjectionService, ProfileProjectionService>();
services.AddScoped<IProfileWriteService, ProfileWriteService>();
```

**Step 2: Delete files, clean up DbContext, fix compilation**

**Step 3: Run full test suite**

```bash
dotnet test --filter "Category!=Integration&Category!=Performance" 2>&1 | tail -10
```

**Step 4: Commit**

```bash
git commit -m "refactor: delete IProfileService, ProfileService, IProfileRepository, ProfileRepository, ProfileEntity, ProfileMapper"
```

---

### Task 27: DROP TABLE profiles migration

```bash
dotnet build -p:GenerateNSwagClient=false
dotnet ef migrations add DropProfilesTable \
    -p src/Infrastructure/Nocturne.Infrastructure.Data \
    -s src/API/Nocturne.API \
    --no-build
```

Verify the migration drops the `profiles` table and nothing else unexpected.

```bash
git commit -m "feat(db): add DropProfilesTable migration"
```

---

### Task 28: Final verification

```bash
# No remaining references to deleted types
grep -rn "ProfileEntity\|IProfileRepository\|ProfileRepository\|IProfileService\b\|ProfileService\b\|_context\.Profiles\|ActivityEntity\|IActivityRepository\|ActivityRepository\|_context\.Activities" src/ --include="*.cs" | grep -v "Migrations/" | grep -v "//" | grep -v "Resolver" | grep -v "ProjectionService" | grep -v "WriteService"

# Build
dotnet build 2>&1 | tail -5

# Tests
dotnet test --filter "Category!=Integration&Category!=Performance" 2>&1 | tail -10
```

---

## Decision Log (for subagent reference)

Decisions made during design that need to be respected:

1. **Resolvers are stateless** — no LoadData() pattern. Use IMemoryCache with 5s TTL.
2. **DIA is scalar today** — GetDIA takes time param for profile-name resolution and future-proofing, but returns scalar from TherapySettings.
3. **CCP formulas** — Sensitivity/CarbRatio: `value * 100 / percentage` (inverse). Basal: `value * percentage / 100` (direct). Targets/DIA: not adjusted.
4. **Profile switches are StateSpans** — Category=Profile, metadata has profileName/percentage/timeshift.
5. **Inline profile JSON** — decomposed at write time by TreatmentDecomposer calling ProfileDecomposer. Synthetic profile name: `"{name}@@@@@{mills}"`.
6. **V3 filtering** — TherapySettings is the profile record proxy for pagination/sorting.
7. **Activity domain model stays** — it's the API DTO shape. Only entity/repo/mapper deleted.
8. **No backfill** — fresh V4 data going forward.
9. **specProfile parameter** — kept on all resolvers for testing convenience.
10. **Timezone for time-of-day resolution** — resolvers need the profile's timezone to convert UTC timestamps to local seconds-from-midnight. TherapySettingsResolver provides this. Other resolvers may need to depend on it or receive timezone as a parameter. Watch for circular dependency and resolve pragmatically.

## Open Questions (write findings to `docs/plans/2026-04-26-implementation-notes.md`)

These may need investigation during implementation:

1. **UpdateTreatments has no production callers** — verify this is truly dead. If so, the treatment overlay logic in the old ProfileService is test-only and doesn't need replication.
2. **ProfileDecomposer persistence** — does it handle upserts into V4 repos directly, or does it just create domain models? This affects whether ProfileWriteService needs IProfileRepository during the transition.
3. **Timezone circular dependency** — BasalRateResolver needs timezone for seconds-from-midnight calculation. Timezone comes from TherapySettingsResolver. Both depend on IActiveProfileResolver. Verify no circular DI issues.
4. **GetComboBolusTreatment** — the old ProfileService resolved combo boluses from injected treatments. If UpdateTreatments is dead, this may be dead too. Verify before implementing in TempBasalResolver.
5. **V4 ProfileController** — currently queries V4 repos directly for the /summary endpoint. Verify it doesn't need changes.
