# DeviceStatus Table Drop Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Remove the legacy `device_statuses` table by routing all V1/V3 devicestatus reads through V4 snapshot tables and promoting the decomposer to the primary write path.

**Architecture:** Extend the V4 snapshot models with missing columns (LoopJson, AidVersion, PumpSnapshot IOB). Create a `device_status_extras` table for uncaptured sub-objects. Build a `DeviceStatusProjectionService` that reassembles DeviceStatus from V4 snapshots by CorrelationId. Migrate IOB/COB/pump/battery services to query V4 repos directly. Remove legacy entity/repo/service/mapper and DROP TABLE.

**Tech Stack:** C# / .NET 10, EF Core, xUnit, FluentAssertions, Moq, SQLite in-memory for tests

**Design doc:** `docs/plans/2026-04-26-devicestatus-drop-table-design.md`

---

## Task 1: Add missing columns to V4 snapshot models

The ApsSnapshot needs `LoopJson` (full Loop status for round-trip fidelity) and `AidVersion` (algorithm version string for Trio detection). PumpSnapshot needs `Iob` and `BolusIob` for pump-reported IOB without APS.

**Files:**
- Modify: `src/Core/Nocturne.Core.Models/V4/ApsSnapshot.cs:129` (add properties after PredictedStartTimestamp)
- Modify: `src/Core/Nocturne.Core.Models/V4/PumpSnapshot.cs:105` (add properties after DeviceId)
- Modify: `src/Infrastructure/Nocturne.Infrastructure.Data/Entities/V4/ApsSnapshotEntity.cs:202` (add columns after AdditionalPropertiesJson)
- Modify: `src/Infrastructure/Nocturne.Infrastructure.Data/Entities/V4/PumpSnapshotEntity.cs:140` (add columns after AdditionalPropertiesJson)
- Modify: `src/Infrastructure/Nocturne.Infrastructure.Data/Mappers/V4/ApsSnapshotMapper.cs` (map new fields in all three methods)
- Modify: `src/Infrastructure/Nocturne.Infrastructure.Data/Mappers/V4/PumpSnapshotMapper.cs` (map new fields in all three methods)

**Step 1: Add properties to ApsSnapshot domain model**

In `ApsSnapshot.cs`, after `PredictedStartTimestamp` (line 129):

```csharp
/// <summary>Full serialized Loop status object for round-trip fidelity.</summary>
public string? LoopJson { get; set; }

/// <summary>Algorithm version string (e.g. Trio app version).</summary>
public string? AidVersion { get; set; }
```

**Step 2: Add properties to PumpSnapshot domain model**

In `PumpSnapshot.cs`, after `DeviceId` (line 105):

```csharp
/// <summary>Pump-reported total IOB (when no APS algorithm is running).</summary>
public double? Iob { get; set; }

/// <summary>Pump-reported bolus IOB.</summary>
public double? BolusIob { get; set; }
```

**Step 3: Add columns to ApsSnapshotEntity**

In `ApsSnapshotEntity.cs`, after `AdditionalPropertiesJson` (line 202):

```csharp
[Column("loop_json", TypeName = "jsonb")]
public string? LoopJson { get; set; }

[Column("aid_version")]
[MaxLength(64)]
public string? AidVersion { get; set; }
```

**Step 4: Add columns to PumpSnapshotEntity**

In `PumpSnapshotEntity.cs`, after `AdditionalPropertiesJson` (line 140):

```csharp
[Column("iob")]
public double? Iob { get; set; }

[Column("bolus_iob")]
public double? BolusIob { get; set; }
```

**Step 5: Update ApsSnapshotMapper**

In `ApsSnapshotMapper.cs`, add to all three methods:
- `ToEntity`: `LoopJson = model.LoopJson`, `AidVersion = model.AidVersion`
- `ToDomainModel`: `LoopJson = entity.LoopJson`, `AidVersion = entity.AidVersion`
- `UpdateEntity`: `entity.LoopJson = model.LoopJson`, `entity.AidVersion = model.AidVersion`

**Step 6: Update PumpSnapshotMapper**

In `PumpSnapshotMapper.cs`, add to all three methods:
- `ToEntity`: `Iob = model.Iob`, `BolusIob = model.BolusIob`
- `ToDomainModel`: `Iob = entity.Iob`, `BolusIob = entity.BolusIob`
- `UpdateEntity`: `entity.Iob = model.Iob`, `entity.BolusIob = model.BolusIob`

**Step 7: Generate EF migration**

```bash
dotnet build -p:GenerateNSwagClient=false
dotnet ef migrations add AddSnapshotColumns -p src/Infrastructure/Nocturne.Infrastructure.Data -s src/API/Nocturne.API --no-build
```

Review the migration — it should add `loop_json`, `aid_version` to `aps_snapshots` and `iob`, `bolus_iob` to `pump_snapshots`. No other table changes.

**Step 8: Build and run tests**

```bash
dotnet build
dotnet test --filter "Category!=Integration&Category!=Performance" -v n
```

**Step 9: Commit**

```bash
git add -f src/Core/Nocturne.Core.Models/V4/ApsSnapshot.cs src/Core/Nocturne.Core.Models/V4/PumpSnapshot.cs src/Infrastructure/Nocturne.Infrastructure.Data/Entities/V4/ApsSnapshotEntity.cs src/Infrastructure/Nocturne.Infrastructure.Data/Entities/V4/PumpSnapshotEntity.cs src/Infrastructure/Nocturne.Infrastructure.Data/Mappers/V4/ApsSnapshotMapper.cs src/Infrastructure/Nocturne.Infrastructure.Data/Mappers/V4/PumpSnapshotMapper.cs src/Infrastructure/Nocturne.Infrastructure.Data/Migrations/
git commit -m "feat(db): add LoopJson, AidVersion to ApsSnapshot and Iob columns to PumpSnapshot"
```

---

## Task 2: Create DeviceStatusExtras model, entity, and repository

New diagnostic table for uncaptured sub-objects (configuration, radioAdapter, rileylinks, xdripjs, etc.).

**Files:**
- Create: `src/Core/Nocturne.Core.Models/V4/DeviceStatusExtras.cs`
- Create: `src/Infrastructure/Nocturne.Infrastructure.Data/Entities/V4/DeviceStatusExtrasEntity.cs`
- Create: `src/Infrastructure/Nocturne.Infrastructure.Data/Mappers/V4/DeviceStatusExtrasMapper.cs`
- Create: `src/Core/Nocturne.Core.Contracts/V4/Repositories/IDeviceStatusExtrasRepository.cs`
- Create: `src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/V4/DeviceStatusExtrasRepository.cs`
- Modify: `src/Infrastructure/Nocturne.Infrastructure.Data/NocturneDbContext.cs:42` (add DbSet)
- Modify: `src/Infrastructure/Nocturne.Infrastructure.Data/Extensions/ServiceCollectionExtensions.cs` (add DI registration)

**Step 1: Create DeviceStatusExtras domain model**

Follow the pattern from `ApsSnapshot.cs`. The model needs: `Id`, `TenantId`, `CorrelationId`, `Timestamp`, `Extras` (Dictionary<string, object?>), `CreatedAt`, `ModifiedAt`.

**Step 2: Create DeviceStatusExtrasEntity**

Follow the pattern from `ApsSnapshotEntity.cs`. Table name: `device_status_extras`. Columns: `id`, `tenant_id`, `correlation_id`, `timestamp`, `extras` (jsonb), `sys_created_at`, `sys_updated_at`. The entity implements `ITenantScoped` and `IAuditable`.

**Step 3: Create DeviceStatusExtrasMapper**

Follow the pattern from `ApsSnapshotMapper.cs`. Three static methods: `ToEntity`, `ToDomainModel`, `UpdateEntity`. Serialize/deserialize `Extras` dictionary as JSON.

**Step 4: Create IDeviceStatusExtrasRepository**

Minimal interface:
- `Task<DeviceStatusExtras> CreateAsync(DeviceStatusExtras model, CancellationToken ct)`
- `Task<IEnumerable<DeviceStatusExtras>> GetByCorrelationIdsAsync(IEnumerable<Guid> correlationIds, CancellationToken ct)`
- `Task<int> DeleteByCorrelationIdAsync(Guid correlationId, CancellationToken ct)`
- `Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct)` — extras don't have LegacyId directly, but can be deleted by CorrelationId lookup

**Step 5: Create DeviceStatusExtrasRepository**

Follow the pattern from `ApsSnapshotRepository.cs`. Implement the interface methods using EF Core.

**Step 6: Add DbSet to NocturneDbContext**

In `NocturneDbContext.cs` near line 42:

```csharp
public DbSet<DeviceStatusExtrasEntity> DeviceStatusExtras { get; set; }
```

**Step 7: Add DI registration**

In `ServiceCollectionExtensions.cs` (Infrastructure.Data):

```csharp
services.AddScoped<IDeviceStatusExtrasRepository, DeviceStatusExtrasRepository>();
```

**Step 8: Generate EF migration**

```bash
dotnet build -p:GenerateNSwagClient=false
dotnet ef migrations add AddDeviceStatusExtrasTable -p src/Infrastructure/Nocturne.Infrastructure.Data -s src/API/Nocturne.API --no-build
```

Review: should create `device_status_extras` table with RLS policy, index on `(tenant_id, correlation_id)`.

**Step 9: Build and run tests**

```bash
dotnet build
dotnet test --filter "Category!=Integration&Category!=Performance" -v n
```

**Step 10: Commit**

```bash
git add -f src/Core/Nocturne.Core.Models/V4/DeviceStatusExtras.cs src/Core/Nocturne.Core.Contracts/V4/Repositories/IDeviceStatusExtrasRepository.cs src/Infrastructure/Nocturne.Infrastructure.Data/Entities/V4/DeviceStatusExtrasEntity.cs src/Infrastructure/Nocturne.Infrastructure.Data/Mappers/V4/DeviceStatusExtrasMapper.cs src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/V4/DeviceStatusExtrasRepository.cs src/Infrastructure/Nocturne.Infrastructure.Data/NocturneDbContext.cs src/Infrastructure/Nocturne.Infrastructure.Data/Extensions/ServiceCollectionExtensions.cs src/Infrastructure/Nocturne.Infrastructure.Data/Migrations/
git commit -m "feat(db): add device_status_extras table for uncaptured sub-objects"
```

---

## Task 3: Add CorrelationId queries to V4 snapshot repositories

The projection service needs to batch-load correlated snapshots by CorrelationId. All three snapshot repos need this method.

**Files:**
- Modify: `src/Core/Nocturne.Core.Contracts/V4/Repositories/IApsSnapshotRepository.cs`
- Modify: `src/Core/Nocturne.Core.Contracts/V4/Repositories/IPumpSnapshotRepository.cs`
- Modify: `src/Core/Nocturne.Core.Contracts/V4/Repositories/IUploaderSnapshotRepository.cs`
- Modify: `src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/V4/ApsSnapshotRepository.cs`
- Modify: `src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/V4/PumpSnapshotRepository.cs`
- Modify: `src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/V4/UploaderSnapshotRepository.cs`
- Test: `tests/Unit/Nocturne.Infrastructure.Data.Tests/` (if repo tests exist for these)

**Step 1: Add method to each interface**

Add to all three snapshot repository interfaces:

```csharp
Task<IEnumerable<T>> GetByCorrelationIdsAsync(IEnumerable<Guid> correlationIds, CancellationToken ct = default);
```

Also add a `GetModifiedSinceAsync` method to `IApsSnapshotRepository` for AAPS incremental sync:

```csharp
Task<IEnumerable<ApsSnapshot>> GetModifiedSinceAsync(long lastModifiedMills, int limit = 1000, CancellationToken ct = default);
```

**Step 2: Implement in each repository**

Use `_context.{Snapshots}.Where(e => correlationIds.Contains(e.CorrelationId)).AsNoTracking()` pattern. Map via the existing mapper.

For `GetModifiedSinceAsync`: query by `SysUpdatedAt >= DateTimeOffset.FromUnixTimeMilliseconds(lastModifiedMills).UtcDateTime`, order by `SysUpdatedAt`, take `limit`.

**Step 3: Build and run tests**

```bash
dotnet build
dotnet test --filter "Category!=Integration&Category!=Performance" -v n
```

**Step 4: Commit**

```bash
git add -f src/Core/Nocturne.Core.Contracts/V4/Repositories/ src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/V4/
git commit -m "feat: add CorrelationId batch queries and ModifiedSince to snapshot repos"
```

---

## Task 4: Extend DeviceStatusDecomposer with new fields and extras

Update the decomposer to populate the new columns (LoopJson, AidVersion, PumpSnapshot.Iob) and store uncaptured sub-objects in the extras table.

**Files:**
- Modify: `src/API/Nocturne.API/Services/V4/DeviceStatusDecomposer.cs`
- Modify: `tests/Unit/Nocturne.API.Tests/Services/V4/DeviceStatusDecomposerTests.cs`

**Step 1: Write failing tests**

Add tests to `DeviceStatusDecomposerTests.cs`:

- `DecomposeAsync_WithLoopData_StoresLoopJson` — verify `ApsSnapshot.LoopJson` is populated with the serialized Loop object when Loop data is present
- `DecomposeAsync_WithTrioData_StoresAidVersion` — verify `ApsSnapshot.AidVersion` contains the `OpenAps.Version` string
- `DecomposeAsync_WithAapsData_StoresAidVersion` — verify version detection for AAPS
- `DecomposeAsync_WithPumpIob_StoresPumpIob` — verify `PumpSnapshot.Iob` and `PumpSnapshot.BolusIob` are populated from `DeviceStatus.Pump.Iob`
- `DecomposeAsync_WithConfiguration_StoresExtras` — verify AAPS `configuration` ends up in extras
- `DecomposeAsync_WithRadioAdapter_StoresExtras` — verify Loop `radioAdapter` ends up in extras
- `DecomposeAsync_WithXDripJs_StoresExtras` — verify xDrip+ `xdripjs` ends up in extras
- `DecomposeAsync_WithNoExtras_SkipsExtrasCreation` — verify no extras record created when all sub-objects are handled
- `DecomposeAsync_WithUnknownTopLevelKeys_CapturesInExtras` — verify unknown JSON keys are captured

**Step 2: Run tests, verify they fail**

```bash
dotnet test tests/Unit/Nocturne.API.Tests --filter "FullyQualifiedName~DeviceStatusDecomposerTests" -v n
```

**Step 3: Update DecomposeApsFromLoopAsync (line 166)**

After creating the ApsSnapshot from Loop data:
- Serialize the full `ds.Loop` object to JSON → `LoopJson`
- Set `AidVersion = null` (Loop doesn't use this field)

**Step 4: Update DecomposeApsFromOpenApsAsync (line 118)**

After creating the ApsSnapshot from OpenAPS data:
- Set `AidVersion = ds.OpenAps?.Version`

**Step 5: Update DecomposePumpAsync (line 222)**

After creating the PumpSnapshot:
- Set `Iob = ds.Pump?.Iob?.Iob`
- Set `BolusIob = ds.Pump?.Iob?.BolusIob`
- Store `ds.Pump?.Extended` in `AdditionalProperties` if present

**Step 6: Add DecomposeExtrasAsync method**

New private method called at the end of `DecomposeAsync` (line 66). Logic:
1. Build a dictionary of uncaptured sub-objects from the DeviceStatus
2. Check each: `XDripJs`, `RadioAdapter`, `Connect`, `Cgm`, `Meter`, `InsulinPen`, `MmTune`, and any `configuration` or `rileylinks` fields
3. If the dictionary is non-empty, create a `DeviceStatusExtras` record with the shared `CorrelationId`
4. If empty, skip (no extras record)

The decomposer needs `IDeviceStatusExtrasRepository` injected.

**Step 7: Update DeleteByLegacyIdAsync (line 440)**

Add deletion of extras records when a legacy devicestatus is deleted. Delete by CorrelationId (look up from any V4 snapshot with matching LegacyId).

**Step 8: Run tests, verify they pass**

```bash
dotnet test tests/Unit/Nocturne.API.Tests --filter "FullyQualifiedName~DeviceStatusDecomposerTests" -v n
```

**Step 9: Commit**

```bash
git add -f src/API/Nocturne.API/Services/V4/DeviceStatusDecomposer.cs tests/Unit/Nocturne.API.Tests/Services/V4/DeviceStatusDecomposerTests.cs
git commit -m "feat: extend decomposer with LoopJson, AidVersion, PumpIob, and extras"
```

---

## Task 5: Create DeviceStatusProjectionService

Build the read service that reassembles `DeviceStatus` from V4 snapshots, following the entries/treatments pattern.

**Files:**
- Create: `src/API/Nocturne.API/Services/Devices/DeviceStatusProjectionService.cs`
- Create: `tests/Unit/Nocturne.API.Tests/Services/Devices/DeviceStatusProjectionServiceTests.cs`
- Reference: `src/Core/Nocturne.Core.Models/DeviceStatus.cs` (target shape)
- Reference: `src/API/Nocturne.API/Services/V4/DeviceStatusDecomposer.cs` (reverse mapping reference)

**Step 1: Write failing tests**

Create `DeviceStatusProjectionServiceTests.cs`. Mock all V4 repos. Key test cases:

- `ProjectAsync_WithOpenApsSnapshot_ReassemblesOpenApsDeviceStatus` — verify SuggestedJson → OpenAps.Suggested, EnactedJson → OpenAps.Enacted, IOB/COB fields, AidVersion → OpenAps.Version
- `ProjectAsync_WithLoopSnapshot_ReassemblesLoopDeviceStatus` — verify LoopJson → Loop object, IOB/COB from ApsSnapshot
- `ProjectAsync_WithPumpSnapshot_ReassemblesPumpObject` — verify all pump fields including AdditionalProperties → Extended
- `ProjectAsync_WithUploaderSnapshot_ReassemblesUploaderObject` — verify uploader fields
- `ProjectAsync_WithOverrideStateSpan_ReassemblesOverrideObject`
- `ProjectAsync_WithExtras_SplatsOntoDocument` — verify extras JSONB keys appear on result
- `ProjectAsync_CorrelatesByCorrelationId` — verify APS + Pump + Uploader joined correctly
- `ProjectAsync_OrphanPumpSnapshot_ReturnsDeviceStatusWithPumpOnly` — xDrip+ case
- `GetAsync_WithPagination_ReturnsPagedResults`
- `GetAsync_WithTimeRange_FiltersCorrectly`
- `GetModifiedSinceAsync_ReturnsModifiedRecords` — AAPS incremental sync
- `GetByIdAsync_WithUuid_QueriesByPrimaryKey`
- `GetByIdAsync_WithLegacyId_QueriesByLegacyId`

**Step 2: Run tests, verify they fail**

```bash
dotnet test tests/Unit/Nocturne.API.Tests --filter "FullyQualifiedName~DeviceStatusProjectionServiceTests" -v n
```

**Step 3: Implement DeviceStatusProjectionService**

Dependencies: `IApsSnapshotRepository`, `IPumpSnapshotRepository`, `IUploaderSnapshotRepository`, `IStateSpanRepository`, `IDeviceStatusExtrasRepository`.

Core methods:

```csharp
public async Task<IEnumerable<DeviceStatus>> GetAsync(
    int count, int skip, string? find, CancellationToken ct)
{
    // 1. Query ApsSnapshot as primary anchor (with pagination)
    // 2. Collect CorrelationIds from results
    // 3. Query orphan PumpSnapshot/UploaderSnapshot (no APS correlation)
    // 4. Batch-load correlated: PumpSnapshot, UploaderSnapshot, StateSpan(Override), extras
    // 5. Project each group → DeviceStatus
}

public async Task<DeviceStatus?> GetByIdAsync(string id, CancellationToken ct)
{
    // Try UUID parse → PK lookup across all three repos
    // Fallback → LegacyId lookup
    // Load correlated records, project
}

public async Task<IEnumerable<DeviceStatus>> GetModifiedSinceAsync(
    long lastModified, int limit, CancellationToken ct)
{
    // Query ApsSnapshot by ModifiedAt
    // Load correlated records, project
}
```

Projection logic (private method `ProjectFromSnapshots`):
- If `AidAlgorithm` is Loop → deserialize `LoopJson` → `DeviceStatus.Loop`
- If OpenAPS/AAPS/Trio → deserialize `SuggestedJson` → `DeviceStatus.OpenAps.Suggested`, `EnactedJson` → `DeviceStatus.OpenAps.Enacted`
- Map `AidVersion` → `DeviceStatus.OpenAps.Version`
- Map IOB/COB fields → nested IOB/COB sub-objects
- Map PumpSnapshot → `DeviceStatus.Pump` (including `AdditionalProperties` → `Pump.Extended`)
- Map PumpSnapshot.Iob → `DeviceStatus.Pump.Iob`
- Map UploaderSnapshot → `DeviceStatus.Uploader`
- Map StateSpan(Override) → `DeviceStatus.Override`
- Splat extras JSONB onto the document
- Set `Id` = `LegacyId ?? ApsSnapshot.Id.ToString()`
- Set `Mills` = ApsSnapshot.Mills (computed from Timestamp)

**Step 4: Run tests, verify they pass**

```bash
dotnet test tests/Unit/Nocturne.API.Tests --filter "FullyQualifiedName~DeviceStatusProjectionServiceTests" -v n
```

**Step 5: Commit**

```bash
git add -f src/API/Nocturne.API/Services/Devices/DeviceStatusProjectionService.cs tests/Unit/Nocturne.API.Tests/Services/Devices/DeviceStatusProjectionServiceTests.cs
git commit -m "feat: add DeviceStatusProjectionService for V4-to-legacy projection"
```

---

## Task 6: Migrate IobService to V4 repos

Remove `List<DeviceStatus>` parameter, query ApsSnapshot and PumpSnapshot directly.

**Files:**
- Modify: `src/API/Nocturne.API/Services/Treatments/IobService.cs`
- Modify: `tests/Unit/Nocturne.API.Tests/Services/Treatments/IobServiceTests.cs`

**Step 1: Update IobService tests**

Update `CalculateTotal` tests:
- Remove `List<DeviceStatus>` parameter from all test calls
- Mock `IApsSnapshotRepository.GetAsync()` to return test ApsSnapshot data
- Mock `IPumpSnapshotRepository.GetAsync()` for pump-IOB-only tests
- Test priority: ApsSnapshot IOB > PumpSnapshot.Iob > treatment calculation
- Test staleness: ApsSnapshot older than 30 minutes → falls through to treatments
- Test AidAlgorithm discrimination: Loop vs OpenAPS vs AAPS

**Step 2: Run tests, verify they fail**

```bash
dotnet test tests/Unit/Nocturne.API.Tests --filter "FullyQualifiedName~IobServiceTests" -v n
```

**Step 3: Update IobService implementation**

Replace constructor dependency on `List<DeviceStatus>` parameter in `CalculateTotal` with injected `IApsSnapshotRepository` and `IPumpSnapshotRepository`.

Rewrite `LastIobDeviceStatus` → `GetLatestDeviceIobAsync`:
1. Query `IApsSnapshotRepository.GetAsync(from: recentTime, to: futureTime, limit: 1)`
2. If found, extract IOB from ApsSnapshot columns directly (no nested object parsing)
3. If not found, query `IPumpSnapshotRepository.GetAsync(from: recentTime, to: futureTime, limit: 1)`
4. If found, use `PumpSnapshot.Iob` / `PumpSnapshot.BolusIob`
5. If neither found, return null (falls through to treatment calculation)

Remove `FromDeviceStatus`, `HasLoopIob`, `HasOpenApsIob`, `HasPumpIob` helper methods (lines 150-556).

**Step 4: Run tests, verify they pass**

```bash
dotnet test tests/Unit/Nocturne.API.Tests --filter "FullyQualifiedName~IobServiceTests" -v n
```

**Step 5: Commit**

```bash
git add -f src/API/Nocturne.API/Services/Treatments/IobService.cs tests/Unit/Nocturne.API.Tests/Services/Treatments/IobServiceTests.cs
git commit -m "refactor: migrate IobService from DeviceStatus to V4 ApsSnapshot/PumpSnapshot"
```

---

## Task 7: Migrate CobService to V4 repos

Remove `List<DeviceStatus>` parameter, query ApsSnapshot directly.

**Files:**
- Modify: `src/API/Nocturne.API/Services/Treatments/CobService.cs`
- Modify: `tests/Unit/Nocturne.API.Tests/Services/Treatments/CobServiceTests.cs`

**Step 1: Update CobService tests**

Same pattern as IobService:
- Remove `List<DeviceStatus>` parameter
- Mock `IApsSnapshotRepository.GetAsync()` to return test ApsSnapshot with `Cob` value
- Test staleness threshold (10 minutes for COB vs 30 for IOB — check existing code)
- Test fallback to treatment-based calculation when no ApsSnapshot

**Step 2: Run tests, verify they fail**

```bash
dotnet test tests/Unit/Nocturne.API.Tests --filter "FullyQualifiedName~CobServiceTests" -v n
```

**Step 3: Update CobService implementation**

Rewrite `LastCOBDeviceStatus` → `GetLatestDeviceCobAsync`:
1. Query `IApsSnapshotRepository.GetAsync(from: recentTime, to: futureTime, limit: 1)`
2. If found and `Cob > 0`, return `Cob` value directly
3. If not found, return null (falls through to treatment calculation)

Remove `FromDeviceStatus` method (lines 399-454).

**Step 4: Run tests, verify they pass**

```bash
dotnet test tests/Unit/Nocturne.API.Tests --filter "FullyQualifiedName~CobServiceTests" -v n
```

**Step 5: Commit**

```bash
git add -f src/API/Nocturne.API/Services/Treatments/CobService.cs tests/Unit/Nocturne.API.Tests/Services/Treatments/CobServiceTests.cs
git commit -m "refactor: migrate CobService from DeviceStatus to V4 ApsSnapshot"
```

---

## Task 8: Migrate PumpAlertService to PumpSnapshot

Replace `DeviceStatus.Pump` reads with direct `IPumpSnapshotRepository` queries.

**Files:**
- Modify: `src/API/Nocturne.API/Services/Monitoring/PumpAlertService.cs`
- Modify: `tests/Unit/Nocturne.API.Tests/Services/Monitoring/PumpAlertServiceTests.cs`

**Step 1: Update tests**

Replace `DeviceStatus` test fixtures with `PumpSnapshot` fixtures. The alert logic (thresholds, severity levels) stays the same — only the data source changes.

**Step 2: Run tests, verify they fail**

```bash
dotnet test tests/Unit/Nocturne.API.Tests --filter "FullyQualifiedName~PumpAlertServiceTests" -v n
```

**Step 3: Update PumpAlertService**

Replace constructor dependency: remove `IDeviceStatusService`, add `IPumpSnapshotRepository`.

In `BuildPumpStatus` (line 91):
- Replace `IEnumerable<DeviceStatus>` parameter with query to `IPumpSnapshotRepository.GetAsync(limit: 1)`
- Map PumpSnapshot fields directly to the alert data structure
- PumpSnapshot already has: `Reservoir`, `BatteryPercent`, `BatteryVoltage`, `Clock`, `Bolusing`, `Suspended`, `PumpStatus`

In `PrepareData` (line 234):
- Replace `DeviceStatus?.Pump` navigation with `PumpSnapshot` fields directly

**Step 4: Run tests, verify they pass**

```bash
dotnet test tests/Unit/Nocturne.API.Tests --filter "FullyQualifiedName~PumpAlertServiceTests" -v n
```

**Step 5: Commit**

```bash
git add -f src/API/Nocturne.API/Services/Monitoring/PumpAlertService.cs tests/Unit/Nocturne.API.Tests/Services/Monitoring/PumpAlertServiceTests.cs
git commit -m "refactor: migrate PumpAlertService from DeviceStatus to PumpSnapshot"
```

---

## Task 9: Migrate BatteryService to V4 repos

Replace `IDeviceStatusRepository` with `IUploaderSnapshotRepository` + `IPumpSnapshotRepository`. Drop the CGM battery path.

**Files:**
- Modify: `src/API/Nocturne.API/Services/Devices/BatteryService.cs`
- Modify: `tests/Unit/Nocturne.API.Tests/Services/Devices/BatteryServiceTests.cs` (if exists)

**Step 1: Update BatteryService**

Replace constructor dependency: remove `IDeviceStatusRepository`, add `IUploaderSnapshotRepository` and `IPumpSnapshotRepository`.

In `ConvertToBatteryReadings` (line 306):
- Replace `DeviceStatus` parameter with separate `UploaderSnapshot` and `PumpSnapshot` parameters
- Remove CGM transmitter battery extraction (lines 350-369) — no real uploader populates this
- Map uploader battery from `UploaderSnapshot.Battery`, `UploaderSnapshot.BatteryVoltage`, `UploaderSnapshot.IsCharging`, `UploaderSnapshot.Temperature`
- Map pump battery from `PumpSnapshot.BatteryPercent`, `PumpSnapshot.BatteryVoltage`

**Step 2: Build and run tests**

```bash
dotnet build
dotnet test --filter "Category!=Integration&Category!=Performance" -v n
```

**Step 3: Commit**

```bash
git add -f src/API/Nocturne.API/Services/Devices/BatteryService.cs
git commit -m "refactor: migrate BatteryService from DeviceStatus to V4 snapshot repos"
```

---

## Task 10: Simplify DataFetchStage and IobCobComputeStage

Remove devicestatus fetch from the chart data pipeline.

**Files:**
- Modify: `src/API/Nocturne.API/Services/ChartData/Stages/DataFetchStage.cs`
- Modify: `src/API/Nocturne.API/Services/ChartData/Stages/IobCobComputeStage.cs`
- Modify: `src/API/Nocturne.API/Services/ChartData/ChartDataContext.cs:66` (remove DeviceStatusList)

**Step 1: Remove DeviceStatusList from ChartDataContext**

In `ChartDataContext.cs`, remove line 66:

```csharp
// DELETE: public IReadOnlyList<DeviceStatus> DeviceStatusList { get; init; } = [];
```

**Step 2: Remove devicestatus fetch from DataFetchStage**

In `DataFetchStage.cs`:
- Remove `IDeviceStatusService` dependency from constructor
- Remove the line that fetches 100 device statuses
- Remove the assignment to `DeviceStatusList`

**Step 3: Remove DeviceStatus parameter from IobCobComputeStage**

In `IobCobComputeStage.cs`:
- Remove `context.DeviceStatusList.ToList()` (line 63)
- Remove `List<DeviceStatus> deviceStatuses` parameter from `BuildIobCobSeriesAsync`
- Update calls to `IobService.CalculateTotal` and `CobService.CobTotal` to not pass devicestatuses (they now query V4 repos internally)

**Step 4: Build and run tests**

```bash
dotnet build
dotnet test --filter "Category!=Integration&Category!=Performance" -v n
```

Fix any compilation errors from removed `DeviceStatusList` references.

**Step 5: Commit**

```bash
git add -f src/API/Nocturne.API/Services/ChartData/Stages/DataFetchStage.cs src/API/Nocturne.API/Services/ChartData/Stages/IobCobComputeStage.cs src/API/Nocturne.API/Services/ChartData/ChartDataContext.cs
git commit -m "refactor: remove DeviceStatus from chart data pipeline"
```

---

## Task 11: Migrate DeviceStatusPredictionService to V4

Replace direct DeviceStatus reads with ApsSnapshot queries.

**Files:**
- Modify: `src/API/Nocturne.API/Services/Devices/DeviceStatusPredictionService.cs`

**Step 1: Update implementation**

Replace `IDeviceStatusService` dependency with `IApsSnapshotRepository`.

In `GetPredictionsAsync` (line 28):
- Query `IApsSnapshotRepository.GetAsync(limit: 1)` for the most recent snapshot
- If `AidAlgorithm` is Loop → deserialize `LoopJson` to get prediction values
- If OpenAPS/AAPS/Trio → deserialize prediction JSON columns (`PredictedDefaultJson`, `PredictedIobJson`, etc.)
- Map to `GlucosePredictionResponse` using ApsSnapshot columns: `CurrentBg`, `Iob`, `Cob`, `SensitivityRatio`

**Step 2: Build and run tests**

```bash
dotnet build
dotnet test --filter "Category!=Integration&Category!=Performance" -v n
```

**Step 3: Commit**

```bash
git add -f src/API/Nocturne.API/Services/Devices/DeviceStatusPredictionService.cs
git commit -m "refactor: migrate DeviceStatusPredictionService to ApsSnapshot"
```

---

## Task 12: Migrate WidgetSummaryService to V4

Replace DeviceStatus reads for IOB/COB/predictions with V4 repos.

**Files:**
- Modify: `src/API/Nocturne.API/Services/Analytics/WidgetSummaryService.cs`

**Step 1: Update implementation**

Replace `IDeviceStatusService` dependency with `IApsSnapshotRepository`.

In `CalculateIobCob` (line 83):
- IOB/COB services now query V4 internally — just call them without `deviceStatusList` parameter

In `ProcessPredictions` (line 384):
- Query `IApsSnapshotRepository.GetAsync(limit: 1)` for most recent snapshot
- Same prediction extraction as DeviceStatusPredictionService (Task 11)
- Reuse the same logic or extract a shared `PredictionProjection` helper

**Step 2: Build and run tests**

```bash
dotnet build
dotnet test --filter "Category!=Integration&Category!=Performance" -v n
```

**Step 3: Commit**

```bash
git add -f src/API/Nocturne.API/Services/Analytics/WidgetSummaryService.cs
git commit -m "refactor: migrate WidgetSummaryService from DeviceStatus to V4 repos"
```

---

## Task 13: Migrate DDataService to projection service

Replace direct DeviceStatus repository reads with the projection service.

**Files:**
- Modify: `src/API/Nocturne.API/Services/Legacy/DDataService.cs`

**Step 1: Update implementation**

Replace `IDeviceStatusRepository` dependency with `DeviceStatusProjectionService`.

In `LoadDeviceStatusAsync` (line 714):
- Replace `_deviceStatuses.GetDeviceStatusAsync(count: 1000)` with `_projectionService.GetAsync(count: 1000, skip: 0, find: null, ct)`

**Step 2: Build and run tests**

```bash
dotnet build
dotnet test --filter "Category!=Integration&Category!=Performance" -v n
```

**Step 3: Commit**

```bash
git add -f src/API/Nocturne.API/Services/Legacy/DDataService.cs
git commit -m "refactor: migrate DDataService to DeviceStatusProjectionService"
```

---

## Task 14: Migrate OpenApsService from DeviceStatus

`IOpenApsService.AnalyzeData` takes `IEnumerable<DeviceStatus>`. Migrate to V4.

**Files:**
- Modify: `src/Core/Nocturne.Core.Contracts/Devices/IOpenApsService.cs`
- Modify: implementation file for OpenApsService (find via grep)
- Modify: callers of `AnalyzeData`

**Step 1: Find and audit OpenApsService**

```bash
# Find the implementation
grep -r "class.*: IOpenApsService" src/
# Find all callers of AnalyzeData
grep -rn "AnalyzeData" src/
```

**Step 2: Update interface and implementation**

Replace `IEnumerable<DeviceStatus>` parameter with `IEnumerable<ApsSnapshot>`. Update the method body to read from ApsSnapshot fields instead of parsing DeviceStatus nested objects.

**Step 3: Update callers**

Each caller that passed `List<DeviceStatus>` now passes the ApsSnapshot list from the V4 repo.

**Step 4: Build and run tests**

```bash
dotnet build
dotnet test --filter "Category!=Integration&Category!=Performance" -v n
```

**Step 5: Commit**

```bash
git add -f src/Core/Nocturne.Core.Contracts/Devices/IOpenApsService.cs src/API/
git commit -m "refactor: migrate OpenApsService from DeviceStatus to ApsSnapshot"
```

---

## Task 15: Update write path — promote decomposer to primary

Route V1/V3 controller writes through the decomposer directly, with post-write projection for SignalR.

**Files:**
- Modify: `src/API/Nocturne.API/Controllers/V1/DeviceStatusController.cs`
- Modify: `src/API/Nocturne.API/Controllers/V3/DeviceStatusController.cs`
- Modify: `src/API/Nocturne.API/Services/Effects/DeviceStatusEffectDescriptor.cs`

**Step 1: Update V1 DeviceStatusController**

In `CreateDeviceStatus` (line 151):
- Replace `_deviceStatusService.CreateDeviceStatusAsync()` with direct decomposer call
- After decomposition, project the V4 snapshots back to DeviceStatus shape via `DeviceStatusProjectionService`
- Broadcast the projected DeviceStatus via `WriteSideEffectsService` (single source of truth)

In `DeleteDeviceStatus` (line 239):
- Replace `_deviceStatusService.DeleteDeviceStatusAsync()` with V4 repo deletes via `DeleteByLegacyIdAsync` across all snapshot repos + extras

In `BulkDeleteDeviceStatus` (line 289):
- Translate the find query to V4 filters, collect CorrelationIds, cascade delete

**Step 2: Update V3 DeviceStatusController**

Same changes as V1, plus:

In `UpdateDeviceStatus` (line 284):
- Delete old V4 records by LegacyId, decompose updated DeviceStatus, project back

In `GetDeviceStatusHistory` (line 427):
- Replace `_repository.GetDeviceStatusModifiedSinceAsync()` with `IApsSnapshotRepository.GetModifiedSinceAsync()` + projection

In `GetDeviceStatus` (line 45):
- Replace `_repository.GetDeviceStatusWithAdvancedFilterAsync()` with projection service

In `GetDeviceStatusById` (line 128):
- Replace `_repository.GetDeviceStatusByIdAsync()` with projection service

**Step 3: Update DeviceStatusEffectDescriptor**

Set `DecomposeToV4 = false` since decomposition now happens in the controller/write path directly, not as a side effect.

**Step 4: Build and run tests**

```bash
dotnet build
dotnet test --filter "Category!=Integration&Category!=Performance" -v n
```

**Step 5: Commit**

```bash
git add -f src/API/Nocturne.API/Controllers/V1/DeviceStatusController.cs src/API/Nocturne.API/Controllers/V3/DeviceStatusController.cs src/API/Nocturne.API/Services/Effects/DeviceStatusEffectDescriptor.cs
git commit -m "feat: promote decomposer to primary devicestatus write path"
```

---

## Task 16: Wire up DI registration

Swap legacy registrations for new services.

**Files:**
- Modify: `src/API/Nocturne.API/Extensions/ServiceRegistrationExtensions.cs`
- Modify: `src/Infrastructure/Nocturne.Infrastructure.Data/Extensions/ServiceCollectionExtensions.cs`

**Step 1: Update registrations**

In `ServiceRegistrationExtensions.cs`:
- Remove: `services.AddScoped<IDeviceStatusService, DeviceStatusService>()` (line 393)
- Add: `services.AddScoped<DeviceStatusProjectionService>()`
- Keep: `services.AddScoped<IDeviceStatusDecomposer, DeviceStatusDecomposer>()` (line 523)

In Infrastructure `ServiceCollectionExtensions.cs`:
- Remove: `services.AddScoped<IDeviceStatusRepository, DeviceStatusRepository>()` (line 119, line 254)

**Step 2: Build and run full test suite**

```bash
dotnet build
dotnet test --filter "Category!=Integration&Category!=Performance" -v n
```

Fix any remaining references to `IDeviceStatusService` or `IDeviceStatusRepository`.

**Step 3: Commit**

```bash
git add -f src/API/Nocturne.API/Extensions/ServiceRegistrationExtensions.cs src/Infrastructure/Nocturne.Infrastructure.Data/Extensions/ServiceCollectionExtensions.cs
git commit -m "refactor: wire DeviceStatusProjectionService, remove legacy DI registrations"
```

---

## Task 17: Remove legacy devicestatus infrastructure

Delete the legacy entity, repository, service, and mapper now that nothing references them.

**Files:**
- Delete: `src/Infrastructure/Nocturne.Infrastructure.Data/Entities/DeviceStatusEntity.cs`
- Delete: `src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/DeviceStatusRepository.cs`
- Delete: `src/Infrastructure/Nocturne.Infrastructure.Data/Mappers/DeviceStatusMapper.cs`
- Delete: `src/API/Nocturne.API/Services/Devices/DeviceStatusService.cs`
- Delete: `src/Core/Nocturne.Core.Contracts/Devices/IDeviceStatusService.cs`
- Delete: `src/Core/Nocturne.Core.Contracts/Repositories/IDeviceStatusRepository.cs`
- Modify: `src/Infrastructure/Nocturne.Infrastructure.Data/NocturneDbContext.cs` — remove `DbSet<DeviceStatusEntity>`
- Delete: `tests/Unit/Nocturne.API.Tests/Services/Devices/DeviceStatusServiceTests.cs`
- Delete: related golden file test infrastructure that seeds DeviceStatusEntity directly

**Step 1: Delete files**

Remove all files listed above.

**Step 2: Remove DbSet from NocturneDbContext**

In `NocturneDbContext.cs`, remove line 42:

```csharp
// DELETE: public DbSet<DeviceStatusEntity> DeviceStatuses { get; set; }
```

Also remove any model configuration for `DeviceStatusEntity` in `OnModelCreating`.

**Step 3: Build**

```bash
dotnet build
```

Fix any remaining references. Grep for `DeviceStatusEntity`, `IDeviceStatusRepository`, `IDeviceStatusService`, `DeviceStatusMapper` and clean up.

**Step 4: Run full test suite**

```bash
dotnet test --filter "Category!=Integration&Category!=Performance" -v n
```

**Step 5: Commit**

```bash
git add -A
git commit -m "refactor: remove legacy devicestatus infrastructure"
```

---

## Task 18: Update golden file tests

Golden file tests seed `DeviceStatusEntity` directly. After the entity is deleted, they need to seed V4 snapshot entities instead.

**Files:**
- Modify: `tests/Unit/Nocturne.API.Tests/GoldenFiles/V1/DeviceStatusGoldenTests.cs`
- Modify: `tests/Unit/Nocturne.API.Tests/GoldenFiles/Infrastructure/GoldenFileTestBase.cs` (if it has DeviceStatus seed helpers)

**Step 1: Update seed helpers**

Replace `SeedDeviceStatus(DeviceStatusEntity)` with seeding of V4 entities:
- `ApsSnapshotEntity` with `SuggestedJson`/`EnactedJson` for OpenAPS data
- `PumpSnapshotEntity` with pump hardware fields
- `UploaderSnapshotEntity` with battery data
- Link them via `CorrelationId`

**Step 2: Run golden file tests and update snapshots**

```bash
dotnet test tests/Unit/Nocturne.API.Tests --filter "FullyQualifiedName~DeviceStatusGoldenTests" -v n
```

Review diffs carefully. Accept cosmetic changes (field ordering), investigate semantic changes.

**Step 3: Commit**

```bash
git add -f tests/
git commit -m "test: update golden file tests to seed V4 snapshots instead of DeviceStatusEntity"
```

---

## Task 19: Drop device_statuses table

Generate the EF migration to drop the legacy table.

**Files:**
- Create: new EF migration

**Step 1: Build without NSwag**

```bash
dotnet build -p:GenerateNSwagClient=false
```

**Step 2: Generate migration**

```bash
dotnet ef migrations add DropDeviceStatusTable -p src/Infrastructure/Nocturne.Infrastructure.Data -s src/API/Nocturne.API --no-build
```

**Step 3: Review the migration**

Verify it contains:
- `migrationBuilder.DropTable(name: "devicestatus")` (and indexes, RLS policies)
- No unintended changes to other tables

**Step 4: Build and run tests**

```bash
dotnet build
dotnet test --filter "Category!=Integration&Category!=Performance" -v n
```

**Step 5: Commit**

```bash
git add -f src/Infrastructure/Nocturne.Infrastructure.Data/Migrations/
git commit -m "feat(db): drop device_statuses table (migrated to V4 snapshots)"
```

---

## Task 20: Final verification

**Step 1: Full build**

```bash
dotnet build
```

**Step 2: Full unit test suite**

```bash
dotnet test --filter "Category!=Integration&Category!=Performance" -v n
```

**Step 3: Frontend type check**

```bash
cd src/Web/packages/app && pnpm run check
```

**Step 4: Verify no remaining references**

Search for: `DeviceStatusEntity`, `DeviceStatusRepository`, `IDeviceStatusRepository`, `DeviceStatusService`, `IDeviceStatusService`, `DeviceStatusMapper`, `DeviceStatusList`.

**Step 5: Commit any final fixes**

```bash
git add -A
git commit -m "chore: final cleanup after device_statuses table removal"
```
