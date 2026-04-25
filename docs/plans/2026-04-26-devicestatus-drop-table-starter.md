# DeviceStatus Table Drop — Context Starter

## What This Is

Context document for a new Claude Code session to brainstorm and plan the `devicestatus` table drop, continuing the V1 deprecation work on branch `feature/v1-deprecation` in worktree `c:/Users/rhysg/Documents/Github/nocturne/.worktrees/drop-entries-table`.

**Already dropped in this branch:** entries, treatments, activities, profiles.
**This is the last legacy Nightscout collection with a decomposition story.**

## Prior Art

Read these for the established pattern:
- `docs/plans/2026-04-26-profiles-activities-drop-tables-design.md` — most recent design doc
- `docs/plans/2026-04-26-profiles-activities-drop-tables.md` — most recent implementation plan
- `docs/plans/2026-04-25-treatments-drop-table.md` — the first table drop, established the pattern

The pattern: build a V4-only read service that projects V4 records back to the legacy shape, migrate all consumers off the legacy repository, delete the entity/repo/mapper, DROP TABLE.

## DeviceStatus Architecture

### Legacy Model

`DeviceStatus` (Core.Models) is a 1126-line polymorphic grab bag. One document can contain any combination of:

| Nested Object | Purpose | V4 Equivalent |
|---|---|---|
| `OpenApsStatus` | APS algorithm state — IOB, COB, suggested/enacted, predictions | `ApsSnapshot` |
| `LoopStatus` | Loop algorithm state — IOB, COB, predicted, recommended bolus | `ApsSnapshot` |
| `PumpStatus` | Pump hardware — battery, reservoir, status, clock | `PumpSnapshot` |
| `UploaderStatus` | Phone/bridge — battery, voltage, temperature | `UploaderSnapshot` |
| `OverrideStatus` | Active overrides (Trio/Loop) | `StateSpan` (Category=Override) |
| `XDripJsStatus` | xDrip state/voltage | No V4 equivalent |
| `RadioAdapterStatus` | Signal strength | No V4 equivalent |
| `CgmStatus` | CGM monitoring | No V4 equivalent |
| `MeterStatus` | Meter monitoring | No V4 equivalent |
| `InsulinPenStatus` | Connected pen | No V4 equivalent |

### V4 Decomposition (Already Exists)

`DeviceStatusDecomposer` (453 lines) already handles the write path:
- One DeviceStatus → up to 3 V4 records (ApsSnapshot + PumpSnapshot + UploaderSnapshot) linked by `CorrelationId`
- Override status → StateSpan (Category=Override)
- Detects APS variant: AAPS (uploader contains "AndroidAPS"), Trio (openaps.version present), vanilla OpenAPS
- Resolves DeviceId via IDeviceService for pump and uploader snapshots
- Idempotent via LegacyId matching

### Consumers

**IOB/COB priority (critical path):**
- `IobService.LastIobDeviceStatus()` — prefers device-reported IOB over calculated. Reads `OpenAps.Iob` or `Loop.Iob` from DeviceStatus.
- `CobService.LastCOBDeviceStatus()` — prefers device-reported COB. Reads `OpenAps.Cob` or `Loop.Cob`.
- Both fall back to treatment-based calculation if device status is stale (>30min).

**Pump monitoring:**
- `PumpAlertService` — reads pump battery, reservoir, clock drift from DeviceStatus.Pump
- `BatteryService` — reads uploader/pump battery levels

**Chart data pipeline:**
- `DataFetchStage` — fetches recent 100 device statuses
- `IobCobComputeStage` — passes device statuses to IOB/COB services

**API surface:**
- V1 DeviceStatusController — full CRUD at `/api/v1/devicestatus`
- V3 DeviceStatusController — with pagination, filtering, AAPS incremental sync (`/history/{lastModified}`)
- No V4 controller — V4 clients use ApsSnapshot/PumpSnapshot/UploaderSnapshot repos directly

**Other:**
- `WidgetSummaryService`, `DevicePublisher`, `DataHub` (SignalR), `DDataService`

### What Makes This Different From Entries/Treatments

1. **No clean 1:1 mapping.** Entries map cleanly to SensorGlucose. Treatments decompose into typed V4 records. DeviceStatus is a bag where some sub-objects have V4 equivalents and others (xDrip, radio adapter, CGM, meter, insulin pen) don't.

2. **IOB/COB priority is the critical path.** IobService and CobService use device status as their *primary* data source, falling back to treatment-based calculation only when device status is stale. Getting this wrong means incorrect IOB/COB on dashboards.

3. **AAPS incremental sync.** The V3 controller has a `/history/{lastModified}` endpoint specifically for AAPS's modified-since polling pattern. This needs to survive.

4. **No existing DualPath/ReadService.** Unlike entries/treatments which had explicit dual-path stores, devicestatus has a simple service→repository architecture.

## ⚠️ CRITICAL: External Uploader Analysis Required

**DeviceStatus is the least structured Nightscout collection.** The schema is whatever uploaders send. Before designing the migration, you MUST understand what real-world uploaders actually submit.

### Trio (formerly FreeAPS X)
- **Repo:** https://github.com/nightscout/Trio
- **Key files to check:** Search for "devicestatus" or "deviceStatus" in the Swift codebase
- Trio uses the `openaps` nested object with `suggested`, `enacted`, and `iob` fields
- Also sends `pump` status and `uploader` battery
- Sends `override` status for active overrides (temp targets, exercise mode)
- **Version field:** Sets `openaps.version` which is how Nocturne distinguishes Trio from vanilla OpenAPS

### AAPS (AndroidAPS)
- **Repo:** https://github.com/nightscout/AndroidAPS
- **Key files to check:** Search for "devicestatus" or "DeviceStatus" in the Kotlin codebase
- AAPS uses the `openaps` nested object but with AAPS-specific fields
- Sends `pump` status with manufacturer-specific fields
- Uses `date` field instead of `mills` for timestamps (handled by decomposer)
- Uses V3 API with incremental sync (`/history/{lastModified}`)
- **Uploader name:** Contains "AndroidAPS" which is how Nocturne detects it

### Loop
- **Repo:** https://github.com/LoopKit/Loop
- **Key files to check:** Search for "devicestatus" in the Swift codebase
- Loop uses the `loop` nested object (NOT `openaps`)
- Fields: `iob`, `cob`, `predicted`, `recommendedBolus`, `automaticDoseRecommendation`
- Also sends `pump`, `uploader`, and `override` status

### xDrip+
- **Repo:** https://github.com/NightscoutFoundation/xDrip
- Sends `xdripjs` status (voltage, state)
- Also sends heart rate and step count via activities, not devicestatus

### What to Look For

For each uploader, document:
1. **Exactly which fields** are populated in the devicestatus document
2. **Which fields are used for calculations** (IOB, COB, predictions) vs display-only
3. **Any fields that don't map to existing V4 snapshot models** — these are the gap
4. **Timestamp handling** — which field is the source of truth (mills, date, created_at)
5. **Frequency of uploads** — how often does each uploader send devicestatus

## Key Design Questions

1. **What do we do with sub-objects that have no V4 equivalent?** (xDrip, radio adapter, CGM, meter, insulin pen) — drop support? Add V4 models? Store as catch-all JSON?

2. **How do IOB/COB services read from V4?** Currently they receive `List<DeviceStatus>` and parse nested objects. After migration, they'd need to read from `IApsSnapshotRepository`. This changes the IOB/COB priority logic significantly.

3. **Can we project V4 snapshots back to DeviceStatus shape?** This is harder than entries/treatments because DeviceStatus sub-objects are deeply nested with vendor-specific fields. The raw JSON blobs (`SuggestedJson`, `EnactedJson`) in ApsSnapshot preserve the original data, but reassembling the full DeviceStatus from V4 snapshots may be lossy.

4. **AAPS incremental sync** — V3's `/history/{lastModified}` queries by `sysUpdatedAt`. How does this translate to V4 snapshot queries?

5. **Should the V1/V3 API reconstruct DeviceStatus from V4, or should we keep the table for reads?** (Unlike profiles where the monolithic shape was wrong, DeviceStatus might be a case where the legacy shape IS what uploaders and consumers expect.)

## Files to Read First

In the Nocturne codebase:
- `src/Core/Nocturne.Core.Models/DeviceStatus.cs` — full legacy model (1126 lines)
- `src/API/Nocturne.API/Services/V4/DeviceStatusDecomposer.cs` — decomposition logic (453 lines)
- `src/API/Nocturne.API/Services/Treatments/IobService.cs` — IOB priority from device status
- `src/API/Nocturne.API/Services/Treatments/CobService.cs` — COB priority from device status
- `src/Core/Nocturne.Core.Models/V4/ApsSnapshot.cs` — V4 APS model
- `src/Core/Nocturne.Core.Models/V4/PumpSnapshot.cs` — V4 pump model
- `src/Core/Nocturne.Core.Models/V4/UploaderSnapshot.cs` — V4 uploader model
- `src/API/Nocturne.API/Services/Monitoring/PumpAlertService.cs` — pump alerts
- `src/API/Nocturne.API/Controllers/V3/DeviceStatusController.cs` — AAPS sync endpoint
