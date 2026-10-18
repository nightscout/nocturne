# Nocturne Caching Infrastructure

An in-process cache over `IMemoryCache`, used for the hot read paths (current and recent entries,
recent treatments) and for upload processing status. There is no distributed cache implementation:
`MemoryCacheService` is the only `ICacheService`, so cache state is per API instance.

## Setup

```csharp
builder.Services.AddNocturneMemoryCache();
```

Registers `ICacheService` (singleton) and `IProcessingStatusService` (singleton). The key prefix
(`nocturne`) and the 300-second default expiration are set in that call, not bound from
configuration.

## Keys

Tenant data read under row level security is cached through the API's `ITenantCache`, which
prefixes every key with `{tenantId}:{partition}:{demo|live}:` and clears a key family with the
pattern `{tenantId}:*:*:{family}:*`, so one invalidation reaches every reader partition. Other
callers assemble their own keys against `ICacheService`: `status:system:{tenantId}`,
`oauth:revoked:{jti}` among them.

`MemoryCacheService.RemoveByPatternAsync` prepends the `nocturne:` prefix itself before anchoring
the regex, so a caller's pattern must be written without that prefix, and must match the rest of
the key end to end. The only wildcards are `*` and `?`.

## TTLs

The keys below take their TTL from a `CacheConstants` member, passed explicitly by the
caller. Keys assembled outside this project set their own: `StatusService` hardcodes 2 minutes for
`status:system:*`, and the statistics, OAuth-revocation and rotation-successor keys pass a TTL
computed at the call site.

| Key                | TTL       | Constant                                     |
|--------------------|-----------|----------------------------------------------|
| Current entries    | 1 minute  | `Defaults.CurrentEntryExpirationSeconds`     |
| Recent entries     | 2 minutes | `Defaults.RecentEntriesExpirationSeconds`    |
| Recent treatments  | 5 minutes | `Defaults.RecentTreatmentsExpirationSeconds` |
| Processing status  | 1 hour    | `DefaultTtl.ProcessingStatus`                |

`MemoryProcessingStatusService` sweeps expired status entries on the
`CleanupIntervals.StatusCleanup` interval.

## Testing

```bash
dotnet test --filter "Category=Cache"
```

`tests/Unit/Nocturne.Infrastructure.Cache.Tests` covers hit rate over a seeded access pattern and
concurrent read-modify-write. Retrieval latency is deliberately not asserted: a wall-clock budget
on a shared runner measures the runner.
