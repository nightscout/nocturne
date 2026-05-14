# Subject Token Migration Design

## Goal

Migrate active Nightscout subjects (and their access tokens) into Nocturne as
part of the existing API-mode migration job, so clients (AAPS, xDrip, etc.)
continue authenticating with their current tokens without reconfiguration.

## Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Source | Nightscout v2 API (`/api/v2/authorization/subjects`, `/roles`) | API-mode only; no MongoDB path needed |
| Subject type | All imported as `Device` | Token-based access is the Device pattern in Nocturne |
| Role handling | Map to permissions directly | Fetch Nightscout roles to resolve permissions; no Nocturne role entities assigned |
| Token handling | Preserve original token (SHA-256 hash stored) + flag for rotation | Seamless cutover; `Notes` field carries rotation nudge |
| Duplicate detection | Skip by `AccessTokenHash` | If a subject with the same hash exists, skip silently |
| Integration | New collection step in existing migration job | Tracked as `"subjects"` in `CollectionProgress` |

## Data Flow

```
Source Nightscout instance
  |
  +- GET /api/v2/authorization/roles    -> role name -> permissions[]
  +- GET /api/v2/authorization/subjects  -> name, accessToken, roles[]
  |
  v
MigrationJob ("subjects" collection step, runs first)
  |
  +- Build role->permissions lookup from fetched roles
  +- For each subject:
  |    +- SHA-256 hash the plaintext accessToken
  |    +- Check SubjectEntity by AccessTokenHash -> skip if exists
  |    +- Resolve permissions: subject.Roles -> lookup -> flatten + dedupe
  |    +- Insert SubjectEntity:
  |    |    Type = Device
  |    |    AccessTokenHash = sha256(accessToken)
  |    |    AccessTokenPrefix = "{name}-{accessToken[..8]}"
  |    |    IsActive = true (false if only role is "denied")
  |    |    Notes = "Migrated from Nightscout. Consider rotating to a Nocturne token."
  |    |    OriginalId = subject._id (if present)
  |    +- Store resolved permissions on the subject
  |
  v
SubjectEntity in DB, immediately usable via AccessTokenHandler
```

## Permission Resolution

Nightscout roles are fetched from `/api/v2/authorization/roles`. Each role has a
`permissions` array of Shiro-style strings. For each migrated subject:

1. Look up each of the subject's role names in the fetched roles
2. Collect all permissions from matched roles
3. Flatten and deduplicate
4. Store directly on the subject (no intermediate role entity)

If a role name is not found in the fetched roles, fall back to the
`DefaultRoles` mapping in `RoleService` (admin, readable, api, careportal,
denied, public). Unknown roles are logged and skipped.

## Ordering

Subjects are migrated **before** clinical data collections. This ensures auth
tokens are in place even if the migration is interrupted partway through data
import.

## Progress Tracking

The subjects collection does not have a count endpoint. `TotalDocuments` starts
at 0 (unknown) and `DocumentsMigrated` increments as subjects are inserted —
same pattern as profiles/food/activity today.

## Edge Cases

- **"denied" role only**: Subject created with `IsActive = false`
- **Empty accessToken**: Subject skipped (no token to preserve)
- **Duplicate token hash**: Subject skipped silently
- **Custom Nightscout roles**: Resolved via the fetched roles response; if a
  custom role has permissions defined, they're preserved exactly

## What We Don't Do

- No new API endpoints
- No new database columns or EF migrations
- No role entities created for migrated subjects
- No token rotation mechanism (separate future work)
- No MongoDB-mode subject migration
