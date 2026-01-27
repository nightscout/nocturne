# Injectable Medications System Design

## Overview

A comprehensive system for tracking insulin and other injectable medications, making MDI (Multiple Daily Injections) users first-class citizens while supporting pump users with concentrated insulins (U200) and users of GLP-1 agonists.

## Goals

- Support multiple insulin types with distinct activity profiles (DIA, onset, peak)
- Track insulin concentrations (U100, U200, U300) as metadata
- Include GLP-1 agonists and other injectable therapies
- Integrate with existing Tracker/Treatment system
- Maintain backward compatibility with legacy Nightscout data
- Design for future pen/vial inventory tracking

## Non-Goals

- Full inventory management UI (designed for, not built)
- Scheduled injection reminders
- Quick-action buttons for common doses
- Oral medication tracking
- Automatic migration of historical treatments

---

## Domain Models

### InjectableMedication

User-owned catalog of injectable medications, pre-populated on account setup.

```csharp
public class InjectableMedication
{
    public Guid Id { get; set; }
    public string Name { get; set; }                    // "My Humalog", "Tresiba"
    public InjectableCategory Category { get; set; }
    public int Concentration { get; set; } = 100;       // U100, U200, U300
    public UnitType UnitType { get; set; }              // Units (insulin) or Milligrams (GLP-1)

    // Activity profile (rapid/short-acting only)
    public double? Dia { get; set; }                    // Duration of insulin action (hours)
    public double? Onset { get; set; }                  // Minutes until action begins
    public double? Peak { get; set; }                   // Minutes until peak action

    // Long-acting duration
    public double? Duration { get; set; }               // Hours of action (24, 42, etc.)

    public double? DefaultDose { get; set; }            // Optional quick-entry default
    public int SortOrder { get; set; }                  // User's preferred display order
    public bool IsArchived { get; set; }                // Soft delete
}
```

### InjectableCategory

```csharp
public enum InjectableCategory
{
    RapidActing,      // Humalog, Novolog/Novorapid, Fiasp, Apidra
    UltraRapid,       // Lyumjev, Afrezza
    ShortActing,      // Regular/R
    Intermediate,     // NPH
    LongActing,       // Lantus, Levemir, Basaglar
    UltraLong,        // Tresiba, Toujeo
    GLP1Daily,        // Victoza
    GLP1Weekly,       // Ozempic, Mounjaro, Trulicity
    Other             // Future-proofing
}
```

### UnitType

```csharp
public enum UnitType
{
    Units,            // Insulin
    Milligrams        // GLP-1 agonists
}
```

### InjectableDose

Record of an administered injection, linked to Treatment for timeline integration.

```csharp
public class InjectableDose
{
    public Guid Id { get; set; }
    public Guid InjectableMedicationId { get; set; }
    public double Units { get; set; }                   // Amount (units or mg based on medication)
    public long Timestamp { get; set; }                 // Mills (Unix milliseconds)

    // Optional fields
    public InjectionSite? InjectionSite { get; set; }
    public Guid? PenVialId { get; set; }                // Link to inventory
    public string? LotNumber { get; set; }
    public string? Notes { get; set; }
    public string? EnteredBy { get; set; }              // "user", "caregiver", "imported"
    public string? Source { get; set; }                 // Origin system if imported
    public string? OriginalId { get; set; }             // Migration compatibility
}
```

### InjectionSite

```csharp
public enum InjectionSite
{
    Abdomen,
    AbdomenLeft,
    AbdomenRight,
    ThighLeft,
    ThighRight,
    ArmLeft,
    ArmRight,
    Buttock,
    Other
}
```

### PenVial (Future Inventory Tracking)

```csharp
public class PenVial
{
    public Guid Id { get; set; }
    public Guid InjectableMedicationId { get; set; }
    public long? OpenedAt { get; set; }                 // Mills
    public long? ExpiresAt { get; set; }                // Mills (typically opened + 28 days)
    public double? InitialUnits { get; set; }           // 300u pen, 1000u vial, etc.
    public double? RemainingUnits { get; set; }         // Decremented on dose logging
    public string? LotNumber { get; set; }
    public PenVialStatus Status { get; set; }
    public string? Notes { get; set; }
    public bool IsArchived { get; set; }
}

public enum PenVialStatus
{
    Unopened,
    Active,
    Empty,
    Expired
}
```

---

## Database Entities

Following existing conventions: snake_case columns, UUID v7 primary keys, sys_created_at/sys_updated_at timestamps.

### InjectableMedicationEntity

```csharp
public class InjectableMedicationEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public string Category { get; set; }
    public int Concentration { get; set; }
    public string UnitType { get; set; }
    public double? Dia { get; set; }
    public double? Onset { get; set; }
    public double? Peak { get; set; }
    public double? Duration { get; set; }
    public double? DefaultDose { get; set; }
    public int SortOrder { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset SysCreatedAt { get; set; }
    public DateTimeOffset SysUpdatedAt { get; set; }

    // Navigation
    public ICollection<InjectableDoseEntity> Doses { get; set; }
    public ICollection<PenVialEntity> PenVials { get; set; }
}
```

### InjectableDoseEntity

```csharp
public class InjectableDoseEntity
{
    public Guid Id { get; set; }
    public Guid InjectableMedicationId { get; set; }
    public double Units { get; set; }
    public long Timestamp { get; set; }
    public string? InjectionSite { get; set; }
    public Guid? PenVialId { get; set; }
    public string? LotNumber { get; set; }
    public string? Notes { get; set; }
    public string? EnteredBy { get; set; }
    public string? Source { get; set; }
    public string? OriginalId { get; set; }
    public DateTimeOffset SysCreatedAt { get; set; }
    public DateTimeOffset SysUpdatedAt { get; set; }

    // Navigation
    public InjectableMedicationEntity InjectableMedication { get; set; }
    public PenVialEntity? PenVial { get; set; }
}
```

### PenVialEntity

```csharp
public class PenVialEntity
{
    public Guid Id { get; set; }
    public Guid InjectableMedicationId { get; set; }
    public long? OpenedAt { get; set; }
    public long? ExpiresAt { get; set; }
    public double? InitialUnits { get; set; }
    public double? RemainingUnits { get; set; }
    public string? LotNumber { get; set; }
    public string Status { get; set; }
    public string? Notes { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset SysCreatedAt { get; set; }
    public DateTimeOffset SysUpdatedAt { get; set; }

    // Navigation
    public InjectableMedicationEntity InjectableMedication { get; set; }
    public ICollection<InjectableDoseEntity> Doses { get; set; }
}
```

### TreatmentEntity Update

Add foreign key to link treatments to injectable doses:

```csharp
// Add to existing TreatmentEntity
public Guid? InjectableDoseId { get; set; }
public InjectableDoseEntity? InjectableDose { get; set; }
```

---

## Pre-populated Catalog

Seed data copied to user's catalog on account creation.

### Rapid-Acting

| Name | Concentration | DIA | Onset | Peak |
|------|---------------|-----|-------|------|
| Humalog (Lispro) | U100 | 4.0h | 15min | 75min |
| Humalog U200 | U200 | 4.0h | 15min | 75min |
| Novolog (Aspart) | U100 | 4.0h | 15min | 75min |
| Novorapid (Aspart) | U100 | 4.0h | 15min | 75min |
| Apidra (Glulisine) | U100 | 4.0h | 15min | 75min |
| Fiasp | U100 | 3.5h | 5min | 60min |
| Lyumjev | U100 | 3.5h | 5min | 60min |

### Short-Acting

| Name | Concentration | DIA | Onset | Peak |
|------|---------------|-----|-------|------|
| Regular (R) | U100 | 6.0h | 30min | 150min |

### Intermediate

| Name | Concentration | DIA | Onset | Peak |
|------|---------------|-----|-------|------|
| NPH | U100 | 14.0h | 90min | 480min |

### Long-Acting

| Name | Concentration | Duration |
|------|---------------|----------|
| Lantus (Glargine) | U100 | 24h |
| Toujeo (Glargine) | U300 | 24h |
| Basaglar (Glargine) | U100 | 24h |
| Levemir (Detemir) | U100 | 20h |
| Tresiba (Degludec) | U100 | 42h |
| Tresiba U200 | U200 | 42h |

### GLP-1 Agonists

| Name | Unit Type | Category |
|------|-----------|----------|
| Ozempic (Semaglutide) | mg | GLP1Weekly |
| Mounjaro (Tirzepatide) | mg | GLP1Weekly |
| Trulicity (Dulaglutide) | mg | GLP1Weekly |
| Victoza (Liraglutide) | mg | GLP1Daily |

---

## IOB Calculation Changes

### Current Behavior

- Single DIA from profile (default 3.0h)
- All insulin treatments use same activity curve

### New Behavior

- Doses with `InjectableDoseId` use their medication's activity profile
- Only `RapidActing`, `UltraRapid`, and `ShortActing` categories contribute to IOB
- `LongActing`, `UltraLong`, `Intermediate`, and GLP-1 categories excluded from IOB
- Legacy treatments (no `InjectableDoseId`) fall back to profile DIA

### IOB Calculation Logic

```csharp
public async Task<IobResult> CalculateIob(long time)
{
    var doses = await GetRecentDoses(time);
    double totalIob = 0;

    foreach (var dose in doses)
    {
        if (dose.InjectableDoseId != null)
        {
            var medication = await GetMedication(dose.InjectableMedicationId);

            // Skip non-bolus insulins
            if (!IsRapidOrShortActing(medication.Category))
                continue;

            // Use medication-specific DIA
            var dia = medication.Dia ?? DEFAULT_DIA;
            totalIob += CalculateDoseIob(dose, time, dia, medication.Peak);
        }
        else
        {
            // Legacy treatment - use profile DIA
            var dia = await _profileService.GetDIA(time);
            totalIob += CalculateDoseIob(dose, time, dia, DEFAULT_PEAK);
        }
    }

    return new IobResult { Iob = totalIob };
}
```

---

## API Endpoints

All Nocturne-specific endpoints under `/api/v4/`.

### Injectable Medications

```
GET    /api/v4/injectable-medications          # List user's catalog
GET    /api/v4/injectable-medications/{id}     # Get single medication
POST   /api/v4/injectable-medications          # Create custom medication
PUT    /api/v4/injectable-medications/{id}     # Update medication
DELETE /api/v4/injectable-medications/{id}     # Archive medication
```

### Injectable Doses

```
GET    /api/v4/injectable-doses                # List doses (with date filters)
GET    /api/v4/injectable-doses/{id}           # Get single dose
POST   /api/v4/injectable-doses                # Log new dose
PUT    /api/v4/injectable-doses/{id}           # Update dose
DELETE /api/v4/injectable-doses/{id}           # Delete dose
```

### Pen/Vials (Minimal Implementation)

```
GET    /api/v4/pen-vials                       # List active pens/vials
GET    /api/v4/pen-vials/{id}                  # Get single pen/vial
POST   /api/v4/pen-vials                       # Register new pen/vial
PUT    /api/v4/pen-vials/{id}                  # Update pen/vial
DELETE /api/v4/pen-vials/{id}                  # Archive pen/vial
```

---

## UI Integration

### Dose Entry (Tracker)

1. User selects "Log Injection"
2. Insulins displayed grouped by category:
   - **Rapid** (Humalog, Fiasp)
   - **Long-Acting** (Tresiba)
   - **GLP-1** (Ozempic)
3. User selects insulin → enters units
4. Optional fields expand: injection site, notes, pen/vial
5. Timestamp defaults to now, adjustable
6. Submit creates Treatment + InjectableDose records

### Main Dashboard

- **IOB display**: Rapid-acting IOB only (unchanged behavior)
- **Active Medications panel** (new):
  - Last long-acting: "Tresiba 20u - 14h ago"
  - Last GLP-1: "Ozempic 0.5mg - 3 days ago"
  - Visual indicator if dose overdue

### Timeline/History

- All doses in chronological treatment timeline
- Distinguished by category (color/icon)
- Expandable for details (site, notes, pen)

### Settings → My Insulins

- Manage personal catalog
- Add/edit/archive entries
- Customize DIA, onset, peak
- Set default doses

---

## Backend Architecture

### Domain Models (`Core.Models`)

- `InjectableMedication.cs`
- `InjectableDose.cs`
- `PenVial.cs`
- `InjectableCategory.cs`
- `InjectionSite.cs`
- `UnitType.cs`
- `PenVialStatus.cs`

### Database Entities (`Infrastructure.Data/Entities`)

- `InjectableMedicationEntity.cs`
- `InjectableDoseEntity.cs`
- `PenVialEntity.cs`
- Update `TreatmentEntity.cs` with FK

### Mappers (`Infrastructure.Data/Mappers`)

- `InjectableMedicationMapper.cs`
- `InjectableDoseMapper.cs`
- `PenVialMapper.cs`

### Service Interfaces (`Core.Contracts`)

- `IInjectableMedicationService.cs`
- `IInjectableDoseService.cs`
- `IPenVialService.cs`

### Service Implementations (`API/Services`)

- `InjectableMedicationService.cs`
- `InjectableDoseService.cs`
- `PenVialService.cs`
- Update `IobService.cs`

### Controllers (`API/Controllers`)

- `InjectableMedicationsController.cs`
- `InjectableDosesController.cs`
- `PenVialsController.cs`

---

## Migration & Backward Compatibility

### Existing Data

- Legacy `Treatment` records unchanged
- IOB falls back to profile DIA when `InjectableDoseId` is null
- No automatic migration of historical data

### New User Setup

- EF migration seeds `injectable_medications` with pre-populated catalog
- Existing users get catalog added on migration

### Future Enhancement

- Optional tool to link historical treatments to catalog entries
- User-driven, not automatic

---

## Implementation Order

1. **Domain models and enums** - Core.Models
2. **Database entities and EF configuration** - Infrastructure.Data
3. **Mappers** - Infrastructure.Data/Mappers
4. **Service interfaces** - Core.Contracts
5. **Service implementations** - API/Services
6. **Controllers** - API/Controllers
7. **IOB service updates** - Modify to use per-medication DIA
8. **Seed data migration** - Pre-populate catalog
9. **Frontend: Settings → My Insulins** - Catalog management
10. **Frontend: Dose entry** - Tracker integration
11. **Frontend: Active Medications panel** - Dashboard display
12. **Frontend: Timeline updates** - Category-aware display
