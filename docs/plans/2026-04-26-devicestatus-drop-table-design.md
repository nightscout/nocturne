# DeviceStatus Table Drop — Design Document

## Goal

Remove the legacy `device_statuses` table. V1/V2/V3 devicestatus endpoints read and write exclusively via V4 tables (`aps_snapshots`, `pump_snapshots`, `uploader_snapshots`, `state_spans`) plus a new `device_status_extras` diagnostic table. The `DeviceStatus` domain model is retained as a V1/V3 response DTO.

## Context

DeviceStatus is the last legacy Nightscout collection with a decomposition story. Entries, treatments, activities, and profiles have already been dropped in this branch. The `DeviceStatusDecomposer` (453 lines) already handles the write-path decomposition of DeviceStatus into V4 snapshots.

## Uploader Analysis

All three major APS uploaders (Trio, AAPS, Loop) were analyzed from source. Their critical data — APS algorithm state, pump hardware, phone/uploader status, overrides — is already captured by the existing decomposer. The gap is small peripheral data.

| Sub-object | Trio | AAPS | Loop | V4 Target |
|---|---|---|---|---|
| `openaps` (iob, suggested, enacted, predBGs) | yes | yes | -- | `ApsSnapshot` |
| `loop` (iob, cob, predicted, enacted) | -- | -- | yes | `ApsSnapshot` |
| `pump` (battery, reservoir, status, clock) | yes | yes | yes | `PumpSnapshot` |
| `uploader` (battery, isCharging) | yes | yes | yes | `UploaderSnapshot` |
| `override` (name, duration, multiplier) | -- | -- | yes | `StateSpan` |
| `configuration` (AAPS plugin settings) | -- | every 12th | -- | extras |
| `radioAdapter` / `rileylinks` | -- | -- | yes | extras |
| `xdripjs` (xDrip+ voltage/state) | xDrip+ | -- | -- | extras |
| `cgm` / `meter` / `insulinPen` | -- | -- | -- | dead code |

Upload cadence is ~5 minutes for all three (one per loop cycle). Trio uses V1, AAPS uses V3 with incremental sync, Loop uses V1.

### Trio-Specific Fields

- `openaps.recommendedBolus`, `openaps.suggested.TDD/ISF/CR` — Trio-specific algorithm outputs preserved in `SuggestedJson`/`EnactedJson` blobs
- `openaps.version` — used for Trio vs vanilla OpenAPS detection (see AidVersion below)
- `pump.bolusIncrement` — stored in `PumpSnapshot.AdditionalProperties`
- `uploader.isCharging` — already captured by decomposer
- No `mills` or `date` set by Trio; server assigns `created_at`

### AAPS-Specific Fields

- `date` field (Unix ms) as primary timestamp instead of `mills`
- `pump.extended` — driver-dependent key-value pairs (see PumpSnapshot decision)
- `configuration` — plugin settings sent every 12th upload (routed to extras)
- `app` always `"AAPS"`, `device` uses `"openaps://"` prefix
- V3 incremental sync via `/history/{lastModified}`

### Loop-Specific Fields

- Uses `loop` namespace, not `openaps`
- `loop.automaticDoseRecommendation`, `loop.currentCorrectionRange`, `loop.forecastError`, `loop.failureReason` — Loop-specific (see LoopJson below)
- `loop.rileylinks[]` — hardware status array (routed to extras)
- `override` as top-level object (already handled by decomposer → StateSpan)
- `pump.reservoir_display_override`, `pump.reservoir_level_override`, `pump.secondsFromGMT`, `pump.extended` — stored in `PumpSnapshot.AdditionalProperties`

## Design Decisions

| Decision | Resolution | Rationale |
|---|---|---|
| Sub-objects with no V4 model | `device_status_extras` table | Alpha — capture for diagnostics, droppable later |
| Extras content | Uncaptured sub-objects only | Decomposer strips openaps/loop/pump/uploader/override; remainder stored as JSONB |
| IOB/COB service reads | Migrate to V4 repos | ApsSnapshot has all needed fields as first-class columns |
| Pump-reported IOB (no APS) | Add `Iob`/`BolusIob` columns to PumpSnapshot | Semantically pump data, not APS data |
| Loop round-trip fidelity | Add `LoopJson` column to ApsSnapshot | Parallels SuggestedJson/EnactedJson for OpenAPS; stores full loop object |
| OpenAps.Version preservation | Add `AidVersion` column to ApsSnapshot | Used for Trio detection; avoids lossy round-trip |
| pump.extended (AAPS) | `PumpSnapshot.AdditionalProperties` | Pump data stays with pump; clean projection |
| V3 nested field filtering | Map known paths to V4 columns | Deep JSON queries are a theoretical concern; no real client uses them |
| SignalR broadcasting | Decompose first, project back, broadcast projected | Single source of truth — SignalR reflects exactly what was stored |
| AAPS incremental sync | Query ApsSnapshot.ModifiedAt with orphan fallback | ApsSnapshot is the anchor; AAPS always sends openaps data |
| V1/V3 projection | DeviceStatusProjectionService | Same pattern as entries/treatments; reassemble from V4 by CorrelationId |
| Non-APS uploads (xDrip+) | Orphan fallback via PumpSnapshot/UploaderSnapshot | Query all three tables, union by CorrelationId |
| Single delete | DeleteByLegacyIdAsync across all V4 tables | Existing pattern from dual-write cleanup |
| Bulk delete | Translate find query to V4 filters, cascade by CorrelationId | Time-range based in practice |
| DataFetchStage | Remove devicestatus fetch | IOB/COB services query V4 directly; AidAlgorithm detection moves inline |
| CGM transmitter battery in BatteryService | Drop the code path | No real uploader populates cgm sub-object |
| PumpAlertService | Direct PumpSnapshot repo query | Single most recent record, no batch iteration |

## Schema Changes

### New Table: `device_status_extras`

| Column | Type | Notes |
|---|---|---|
| `id` | uuid (PK) | UUID v7 |
| `tenant_id` | uuid (FK) | RLS |
| `correlation_id` | uuid | Links to correlated V4 snapshot group |
| `timestamp` | timestamptz | From source devicestatus |
| `extras` | jsonb | Uncaptured sub-objects |
| `created_at` | timestamptz | |
| `modified_at` | timestamptz | |

Index on `(tenant_id, correlation_id)`. RLS policy matching other tenant-scoped tables.

### ApsSnapshot Additions

| Column | Type | Notes |
|---|---|---|
| `loop_json` | text (nullable) | Full serialized Loop status object for round-trip fidelity |
| `aid_version` | text (nullable) | Algorithm version string (Trio app version, AAPS version, etc.) |

### PumpSnapshot Additions

| Column | Type | Notes |
|---|---|---|
| `iob` | decimal (nullable) | Pump-reported total IOB |
| `bolus_iob` | decimal (nullable) | Pump-reported bolus IOB |

## Write Path (After)

```
V1/V3 POST DeviceStatus
  -> Validate
  -> DeviceStatusDecomposer (promoted to primary write path):
      -> ApsSnapshot (from openaps or loop, with LoopJson/AidVersion)
      -> PumpSnapshot (from pump, with Iob/BolusIob, extended in AdditionalProperties)
      -> UploaderSnapshot (from uploader)
      -> StateSpan (from override)
      -> DeviceStatusExtras (remainder JSONB)
  -> DeviceStatusProjectionService.ProjectFromCorrelationId()
  -> WriteSideEffectsService (cache invalidation + SignalR with projected DeviceStatus)
```

The legacy `DeviceStatusRepository.CreateDeviceStatusAsync()` call is removed. SignalR broadcasts the projected DeviceStatus (reassembled from V4) so realtime clients see exactly what was stored.

## Read Path (After)

```
V1/V3 GET /devicestatus
  -> DeviceStatusProjectionService:
      1. Query ApsSnapshot as primary anchor (pagination, filtering, time range)
      2. Pick up orphan PumpSnapshot/UploaderSnapshot with no correlated ApsSnapshot
      3. Batch-load by CorrelationId: PumpSnapshot, UploaderSnapshot, StateSpan(Override), extras
      4. Reassemble DeviceStatus shape:
         - SuggestedJson/EnactedJson -> OpenAps.Suggested/Enacted (or LoopJson -> Loop)
         - AidAlgorithm discriminates OpenAPS vs Loop namespace
         - AidVersion -> OpenAps.Version
         - Structured APS fields -> IOB/COB sub-objects
         - PumpSnapshot -> Pump (including AdditionalProperties -> Extended)
         - UploaderSnapshot -> Uploader
         - StateSpan(Override) -> Override
         - Extras JSONB -> splat remaining keys
  -> Return DeviceStatus[] (V1) or V3CollectionResponse<DeviceStatus> (V3)
```

### AAPS Incremental Sync

`GET /api/v3/devicestatus/history/{lastModified}` queries `ApsSnapshot.ModifiedAt >= lastModified` with orphan fallback on PumpSnapshot/UploaderSnapshot. Projects to DeviceStatus shape via the same projection service.

### GetByIdAsync

- Parse ID: if valid UUID, query V4 tables by PK
- Otherwise, query by `LegacyId` across ApsSnapshot, PumpSnapshot, UploaderSnapshot
- Load correlated records by CorrelationId, project to DeviceStatus

## IOB/COB Service Migration

### IobService

**Current:** Receives `List<DeviceStatus>`, calls `LastIobDeviceStatus()` which iterates and checks `Loop.Iob` > `OpenAps.Iob` > `Pump.Iob`.

**After:**
1. Query `IApsSnapshotRepository` for most recent snapshot within 30-minute staleness window
2. `AidAlgorithm` enum replaces heuristic detection (checking `Loop != null` vs `OpenAps != null`)
3. Read `Iob`, `BasalIob`, `BolusIob` directly from ApsSnapshot columns
4. Fallback: query `IPumpSnapshotRepository` for `PumpSnapshot.Iob` / `PumpSnapshot.BolusIob` (pump-reported IOB without APS)
5. Final fallback: treatment-based calculation (unchanged)

Priority: ApsSnapshot > PumpSnapshot.Iob > treatment-based calculation.

### CobService

**Current:** Receives `List<DeviceStatus>`, reads `Loop.Cob` or `OpenAps.Cob`.

**After:** Query `IApsSnapshotRepository` for most recent snapshot. Read `Cob` directly. Fallback to treatment-based decay calculation (unchanged).

### DataFetchStage / IobCobComputeStage

- Remove devicestatus fetch from `DataFetchStage` entirely
- Remove `List<DeviceStatus>` parameter from `IobCobComputeStage`
- APS system detection (`AidAlgorithm`) surfaces from the IOB/COB service results

## Consumer Migration

| Consumer | Current Dependency | After |
|---|---|---|
| IobService | `List<DeviceStatus>` | `IApsSnapshotRepository` + `IPumpSnapshotRepository` |
| CobService | `List<DeviceStatus>` | `IApsSnapshotRepository` |
| PumpAlertService | `DeviceStatus.Pump` fields | `IPumpSnapshotRepository` (latest record) |
| BatteryService | uploader/pump/CGM batteries | `IUploaderSnapshotRepository` + `IPumpSnapshotRepository` |
| DataFetchStage | fetches 100 DeviceStatus | removed |
| IobCobComputeStage | `List<DeviceStatus>` parameter | simplified (no DeviceStatus parameter) |
| V1 DeviceStatusController | IDeviceStatusService | DeviceStatusProjectionService (reads) + decomposer (writes) |
| V3 DeviceStatusController | IDeviceStatusService | DeviceStatusProjectionService (reads) + decomposer (writes) |
| WidgetSummaryService | DeviceStatus | V4 repos or projection service |
| DevicePublisher | DeviceStatus | projection service |
| DataHub (SignalR) | DeviceStatus | projected DeviceStatus from write path |
| DDataService | DeviceStatus | projection service |

## Cleanup

### Delete

- `DeviceStatusEntity` and its EF configuration
- `DeviceStatusRepository` and `IDeviceStatusRepository`
- `DeviceStatusService` and `IDeviceStatusService`
- `DeviceStatusMapper`
- `DbSet<DeviceStatusEntity>` from `NocturneDbContext`
- `device_statuses` table via EF migration (DROP TABLE)
- CGM battery path from `BatteryService`

### Keep

- `DeviceStatus` domain model (V1/V3 response DTO)
- `DeviceStatusDecomposer` (promoted to primary write path, extended with extras + LoopJson + AidVersion + PumpIob)
- `WriteSideEffectsService` orchestration
- V1/V3 controller signatures and response shapes

## Grill Resolutions

1. **Uncaptured sub-objects** — stored in `device_status_extras` JSONB for alpha diagnostics, not full raw document. Droppable later.
2. **Loop round-trip** — `LoopJson` column on ApsSnapshot parallels SuggestedJson/EnactedJson. Loop is not priority (Trio is).
3. **OpenAps.Version** — stored as `AidVersion` on ApsSnapshot. Used for Trio detection, preserved for V1/V3 round-trip.
4. **pump.extended** — stored in `PumpSnapshot.AdditionalProperties` (pump data stays with pump).
5. **V3 nested field filtering** — theoretical concern, no real client uses deep nested filters. Map known paths to V4 columns.
6. **SignalR single source of truth** — decompose first, project back, broadcast projected version. No divergence between realtime and stored data.
7. **Pump-reported IOB** — new `Iob`/`BolusIob` columns on PumpSnapshot. IobService priority: ApsSnapshot > PumpSnapshot.Iob > treatments.
8. **DataFetchStage** — remove devicestatus fetch entirely. AidAlgorithm detection moves into IOB/COB services.
9. **CGM transmitter battery** — drop from BatteryService. No real uploader populates it.
10. **PumpAlertService** — direct latest PumpSnapshot query, no batch iteration.
11. **Delete semantics** — single delete via DeleteByLegacyIdAsync across all tables. Bulk delete translates find query to V4 filters, cascades by CorrelationId.
12. **AAPS incremental sync** — query ApsSnapshot.ModifiedAt with orphan PumpSnapshot/UploaderSnapshot fallback.
