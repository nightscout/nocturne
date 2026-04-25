# Profiles & Activities Table Drop — Design Document

## Scope

Drop the legacy `profiles` and `activities` PostgreSQL tables as part of V1 deprecation. Food table is not in scope — it is integral to the V4 meals system (CarbIntake -> TreatmentFood -> Food).

## Activities: Simple Deletion

Activities are already decomposed: regular activities route to StateSpans via `IStateSpanService`, sensor data (heart rate, step count) routes to dedicated tables via `IActivityDecomposer`. The `activities` table is dead storage.

### What Gets Deleted

- `ActivityEntity`, `ActivityRepository`, `ActivityMapper` (Infrastructure.Data)
- `IActivityRepository` (Core.Contracts)
- `DbSet<ActivityEntity>` from `NocturneDbContext`
- DI registration for `IActivityRepository`
- Profile-warming path from `CacheWarmingService` (remove `WarmUserProfileAsync` and `IProfileRepository` dependency; service itself stays for entries/treatments/system warming)

### What Stays

- `Activity` domain model (Core.Models) — serves as API DTO shape, assembled by `ActivityService` from V4 sources
- `ActivityService` — continues merging StateSpan + HeartRate + StepCount
- V1 `ActivityController` — already exclusively uses `IActivityService`, never `IActivityRepository`
- `ActivityDecomposer` — continues routing sensor data

### Consumer Migration

Two consumers reference `IActivityRepository` directly:
- **CountController** (`GET /api/v1/count/activity/where`) — migrate to `IActivityService`
- **DDataService** — migrate to `IActivityService`

### EF Migration

DROP TABLE `activities`.

---

## Profiles: Decomposition into Focused Resolvers

Replace monolithic `IProfileService` with focused, stateless resolvers backed by V4 schedule repositories. V1/V3 API compatibility maintained via projection.

### Architecture

#### Resolver Decomposition

| Resolver | V4 Repo | Methods | Consumers |
|---|---|---|---|
| `IBasalRateResolver` | `IBasalScheduleRepository` | `GetBasalRate(time, specProfile?)` | IobService, IobCobComputeStage, ProfileLoadStage |
| `ISensitivityResolver` | `ISensitivityScheduleRepository` | `GetSensitivity(time, specProfile?)` | IobService, CobService |
| `ICarbRatioResolver` | `ICarbRatioScheduleRepository` | `GetCarbRatio(time, specProfile?)` | CobService |
| `ITargetRangeResolver` | `ITargetRangeScheduleRepository` | `GetLowBGTarget(time, specProfile?)`, `GetHighBGTarget(time, specProfile?)` | ProfileLoadStage |
| `ITherapySettingsResolver` | `ITherapySettingsRepository` + `IPatientInsulinRepository` | `GetDIA(time, specProfile?)`, `GetCarbAbsorptionRate(time, specProfile?)`, `GetTimezone(specProfile?)`, `GetUnits(specProfile?)` | IobService, CobService, ProfileLoadStage, PumpAlertService |
| `IActiveProfileResolver` | `IStateSpanRepository` (Category=Profile) | `GetActiveProfileName(time)`, `GetCircadianAdjustment(time)` | All schedule resolvers |
| `ITempBasalResolver` | `ITempBasalRepository` + `IBasalRateResolver` | `GetTempBasal(time, specProfile?)` → `TempBasalResult` | IobCobComputeStage |

All resolvers are **stateless with short-TTL per-tenant IMemoryCache**. No `LoadData()` step — resolvers lazy-load from V4 repos on first access.

#### IActiveProfileResolver

Resolves the cross-cutting "which named profile is active at time T" concern.

- Queries `StateSpan` records with `Category = StateSpanCategory.Profile`
- StateSpan Metadata dictionary contains: profile name, `Percentage`, `Timeshift`, `ProfileJson`
- Returns `CircadianAdjustment` (Percentage + TimeshiftMs) when a CircadianPercentageProfile is active
- Consumers: every schedule resolver calls this when `specProfile` is null

CircadianPercentageProfile adjustment application happens in each schedule resolver:
- Sensitivity, CarbRatio: `value * 100 / percentage` (inverse)
- Basal: `value * percentage / 100` (direct)
- DIA, targets, scalars: not adjusted

#### Schedule Resolution Algorithm

Shared static utility `ScheduleResolution`:
- `FindValueAtTime(List<ScheduleEntry> entries, int secondsFromMidnight)` — returns most recent entry value where `TimeAsSeconds <= secondsFromMidnight`. Used by BasalRate, Sensitivity, CarbRatio resolvers.
- `FindRangeAtTime(List<TargetRangeEntry> entries, int secondsFromMidnight)` — same logic but returns Low/High pair. Used by TargetRange resolver.

TherapySettingsResolver does not use this — DIA, CarbsHr, Timezone, Units are scalars on TherapySettings. DIA takes a time parameter for profile-name resolution and future time-of-day scheduling, but currently returns a flat scalar.

#### Schedule Temporal Resolution

Each V4 schedule repo gets a `GetActiveAtAsync(string profileName, DateTime timestamp)` query that returns the single most recent record where `Timestamp <= timestamp`. Indexed query, avoids loading all historical schedules.

#### DIA Priority Chain

Owned by `ITherapySettingsResolver`:
1. Externally managed profile (`IsExternallyManaged`) → TherapySettings.Dia
2. PatientInsulin primary bolus exists → PatientInsulin.Dia
3. Fallback → TherapySettings.Dia

PatientInsulin queried via `IPatientInsulinRepository` (cached).

#### Default Fallback Values

When no schedule data exists (preserved from current ProfileService):
- DIA: 3.0 hours
- Sensitivity: 50.0 mg/dL per U
- CarbRatio: 12.0 g/U
- CarbsHr: 20.0 g/hr
- TargetLow: 70.0 mg/dL
- TargetHigh: 180.0 mg/dL
- BasalRate: 1.0 U/hr

### V1/V3 API Compatibility

#### ProfileProjectionService (reads)

Reconstructs monolithic Profile JSON from V4 schedules for V1/V3 GET endpoints:
1. Load TherapySettings, BasalSchedule, CarbRatioSchedule, SensitivitySchedule, TargetRangeSchedule for the requested profile name(s)
2. Assemble into Profile domain model (Store dict with ProfileData arrays)
3. Map ScheduleEntry → TimeValue, TargetRangeEntry → TimeValue pairs
4. Populate scalars from TherapySettings

V3 advanced filtering (find, sort, pagination) operates on TherapySettings as the "profile record" proxy — it has one row per profile write with timestamp, profile name, and correlation ID.

#### ProfileWriteService (writes)

Renamed from `ProfileDataService`, stripped of read methods. Orchestrates:
1. Receive monolithic Profile from V1/V3 controller
2. Call `ProfileDecomposer` to fan out into V4 schedule records
3. Trigger side effects (cache invalidation, event broadcast)

#### Profile Decomposition on Write

`ProfileEffectDescriptor.DecomposeToV4` flipped from `false` to `true` so that V1/V3 profile writes automatically decompose via the standard write-side-effects pipeline.

### Inline Profile JSON

Profile switch treatments can carry embedded `ProfileJson` — a full profile definition inlined in the treatment.

**Write-time decomposition:** `TreatmentDecomposer.DecomposeProfileSwitchAsync()` extended:
1. Creates StateSpan (as today)
2. If `ProfileJson` is non-empty, calls `ProfileDecomposer` to decompose the embedded profile data into V4 schedule records tagged with synthetic profile name `"{name}@@@@@{mills}"`

At read time, resolvers query by profile name as normal — no special handling needed.

No backfill of existing inline profile data.

### Consumer Migration

| Consumer | Current Dependency | New Dependencies |
|---|---|---|
| **ProfileLoadStage** | IProfileService (LoadData, HasData, GetTimezone, GetLowBGTarget, GetHighBGTarget, GetBasalRate) | ITherapySettingsResolver, ITargetRangeResolver, IBasalRateResolver |
| **IobCobComputeStage** | IProfileService (HasData, GetDIA, GetBasalRate) | ITherapySettingsResolver, IBasalRateResolver |
| **IobService** | IProfileService (GetDIA, GetSensitivity, GetBasalRate) | ITherapySettingsResolver, ISensitivityResolver, IBasalRateResolver |
| **CobService** | IProfileService (GetSensitivity, GetCarbRatio, GetCarbAbsorptionRate) | ISensitivityResolver, ICarbRatioResolver, ITherapySettingsResolver |
| **PumpAlertService** | IProfileService (GetTimezone) | ITherapySettingsResolver |
| **PredictionService** | IProfileRepository (direct DB query) | IBasalRateResolver, ISensitivityResolver, ICarbRatioResolver, ITargetRangeResolver, ITherapySettingsResolver |

### What Gets Deleted

- `IProfileService`, `ProfileService` (Core.Contracts + API)
- `IProfileRepository`, `ProfileRepository` (Core.Contracts + Infrastructure.Data)
- `ProfileEntity` (Infrastructure.Data)
- `ProfileMapper` (Infrastructure.Data)
- `DbSet<ProfileEntity>` from `NocturneDbContext`
- Read methods from `ProfileDataService` (remainder renamed to `ProfileWriteService`)
- `WarmUserProfileAsync` from `CacheWarmingService`
- DI registrations for deleted types

### What Stays

- `Profile`, `ProfileData`, `TimeValue` domain models — serve as V1/V3 API DTO shape via ProfileProjectionService
- `ProfileDecomposer` — now the primary write path
- V4 schedule models, entities, repos, mappers (BasalSchedule, CarbRatioSchedule, SensitivitySchedule, TargetRangeSchedule, TherapySettings)
- V1/V3 ProfileControllers — backed by ProfileProjectionService + ProfileWriteService
- V4 ProfileController — backed by V4 schedule repos directly

### EF Migration

DROP TABLE `profiles`.

---

## Grill Resolutions

1. **Profile switches are StateSpans** (Category=Profile), not DeviceEvents. IActiveProfileResolver queries IStateSpanRepository.
2. **ProfileDecomposer exists but was disabled** (`DecomposeToV4 = false`). Flip to true.
3. **DIA is a scalar today** but physiologically time-scheduled. Resolver interface takes time parameter for future-proofing; internally treats scalar as single-entry schedule.
4. **Schedule temporal resolution** via `GetActiveAtAsync(profileName, timestamp)` — indexed DB query returns most recent schedule at-or-before timestamp.
5. **`specProfile` parameter** kept on all resolvers for testing convenience.
6. **CacheWarmingService** — remove profile-warming path only, service stays.
7. **FindTimeSlot** — shared static `ScheduleResolution` utility. Separate method for TargetRange (Low/High shape).
8. **Inline profile JSON** — decomposed at write time by TreatmentDecomposer calling ProfileDecomposer. No backfill.
9. **V3 filtering** — TherapySettings as profile record proxy for pagination/sorting/filtering.
10. **ProfileDataService** — stripped to write-only, renamed `ProfileWriteService`.
11. **DIA priority chain** — owned by ITherapySettingsResolver, depends on IPatientInsulinRepository.
12. **Activity domain model** — kept as API DTO; ActivityEntity/Repository/Mapper deleted.
