---
name: nocturne-remote-functions
description: Use when adding API endpoints, creating frontend data fetching, writing +page.server.ts, or connecting Svelte components to backend data in the Nocturne project. Triggers on remote functions, server load, apiClient, query/command/form patterns.
---

# Nocturne Remote Function Toolchain

## Overview

Nocturne uses a three-stage code generation pipeline that turns C# controller attributes into type-safe SvelteKit remote functions. **The generated remote functions are the primary way the frontend fetches data and executes mutations.** Never use raw `fetch`, hand-roll API calls, or write `+page.server.ts` load functions when a remote function can do the job.

## The Pipeline

```
C# Controller + [RemoteQuery/Command/Form] attribute
        |
        v
  1. NSwag generates OpenAPI spec + TypeScript client
  2. Zod generator creates validation schemas
  3. openapi-remote-codegen generates .generated.remote.ts files
        |
        v
  src/Web/packages/app/src/lib/api/generated/{tag}.generated.remote.ts
```

All three stages run automatically on `dotnet build` (or Aspire startup using `aspire start`) via MSBuild targets in `Nocturne.API.csproj`. Manual: `pnpm run generate-api` from the Web root.

## Step 1: Add the Attribute (Backend)

Decorate your controller action with one of three attributes from `OpenApi.Remote.Attributes`:

| Attribute         | Use for                                         | Generated wrapper |
| ----------------- | ----------------------------------------------- | ----------------- |
| `[RemoteQuery]`   | GET / read-only                                 | `query(...)`      |
| `[RemoteCommand]` | POST/PUT/DELETE mutations                       | `command(...)`    |
| `[RemoteForm]`    | Form submissions needing server-side validation | `form(...)`       |

### Cache Invalidation

Commands and forms accept an `Invalidates` array naming query methods whose caches should be refreshed after the mutation succeeds:

```csharp
[HttpPut("{connectorName}")]
[RemoteCommand(Invalidates = ["GetConfiguration", "GetAllConnectorStatus"])]
public async Task<ActionResult<ConnectorConfigurationResponse>> SaveConfiguration(
    string connectorName, [FromBody] JsonDocument configuration, CancellationToken ct)
```

This generates a `command(...)` that calls `.refresh()` on the named queries after success:

```typescript
// AUTO-GENERATED
export const saveConfiguration = command(
  schema,
  async ({ connectorName, request }) => {
    const result = await apiClient.configuration.saveConfiguration(
      connectorName,
      request,
    );
    await Promise.all([
      getConfiguration(connectorName).refresh(),
      getAllConnectorStatus(undefined).refresh(),
    ]);
    return result;
  },
);
```

## Step 2: Rebuild to Generate

```bash
dotnet build                              # Full pipeline
dotnet build -t:GenerateClient ...        # NSwag only
pnpm run generate-api                     # All three stages from Web root
pnpm run generate-remote-functions        # Stage 3 only (if openapi.json is current)
```

Output lands in `src/Web/packages/app/src/lib/api/generated/`. **Never edit generated files.**

## Step 3: Use the Remote Function (Frontend)

### Importing

```typescript
// Generated functions — preferred for simple CRUD
import {
  getActiveAlerts,
  acknowledge,
} from "#lib/api/generated/alerts.generated.remote.js";

// Hand-maintained wrappers — for orchestration or transformation
import { getChartData } from "#lib/api/chart-data.remote.js";
```

### Query (read data)

```svelte
<script lang="ts">
  import { getAlertHistory } from "#lib/api/generated/alerts.generated.remote.js";

  // Reactive: re-runs when params change
  const alerts = $derived(getAlertHistory({ page: 1, pageSize: 20 }));
</script>

{#await alerts}
  <p>Loading...</p>
{:then data}
  <!-- render data -->
{:catch err}
  <p>Error: {err.message}</p>
{/await}
```

### Command (mutate data)

```svelte
<script lang="ts">
  import { acknowledge } from "#lib/api/generated/alerts.generated.remote.js";

  async function handleAcknowledge(id: string) {
    await acknowledge({ alertInstanceIds: [id] });
    // Cache is auto-invalidated per the Invalidates array
  }
</script>
```

### Accessing `.current` and `.refresh()`

Generated query functions return objects with `.current` (the cached/promised value) and `.refresh()` (force re-fetch):

```typescript
// Read cached value
const members = await getMembers(undefined).current;

// Force refresh after a related mutation
await getMembers(undefined).refresh();
```

## When to Write a Hand-Maintained Remote File

Generated remote functions cover simple 1:1 endpoint-to-function mappings. Write a hand-maintained `*.remote.ts` file **only** when you need:

1. **Orchestration** — combining multiple API calls into one function (e.g., `getChartData` calls `apiClient.chartData.getDashboardChartData` then `transformChartData`)
2. **Server-side transformation** — reshaping API responses before sending to client
3. **Conditional logic** — trying multiple endpoints, fallback chains
4. **Cross-entity composition** — e.g., `getPublicAccessConfig` reads members then extracts the Public subject

Hand-maintained files live in two places:

- `src/Web/packages/app/src/lib/api/*.remote.ts` — shared across routes
- `src/Web/packages/app/src/routes/{route}/*.remote.ts` — co-located with the route that uses them

Hand-maintained files use the same `query`/`command`/`form` wrappers from `$app/server`:

```typescript
import { getRequestEvent, query } from "$app/server";
import { z } from "zod";

export const getMyCompositeData = query(
  z.object({ from: z.number(), to: z.number() }),
  async (params) => {
    const { apiClient } = getRequestEvent().locals;
    const [a, b] = await Promise.all([
      apiClient.foo.getA(params.from, params.to),
      apiClient.bar.getB(params.from, params.to),
    ]);
    return { a, b };
  },
);
```

Three rules SvelteKit 3 enforces at runtime:

- A `query` cannot read `event.url`, `event.params` or `event.route` (it throws). Take the value
  as an argument, or read the host from `request` headers (`getOriginalHost` in
  `#lib/server/request-host`).
- Every control in a remote `form` takes its name from the form's fields:
  `<input {...form.fields.notes.as("hidden", notes)} />`, or `name={form.fields.notes.as("text").name}`
  on a component that binds its own value. A hand-written `name="notes"` rejects the submission.
  An update form nests the record under `request` (`form.fields.request.notes`).
- Awaiting a query answers with the value a live subscriber already holds. To fetch again (a
  poll, a read after a write), use `fresh(query(args))` from `#lib/api/fresh-query`.

## What NOT to Do

### Never write +page.server.ts for data fetching

`+page.server.ts` with `load` functions is the **SvelteKit default** but is **not the Nocturne pattern**. Remote functions replace server load functions for nearly all cases.

**Only two legitimate uses for +page.server.ts:**

1. **Auth redirects** — checking `locals.isAuthenticated` and redirecting (the dashboard `+page.server.ts`)
2. **Streaming initial data** — the dashboard pre-fetches chart data to avoid a loading flash on first paint

If you're writing a `+page.server.ts` that just calls `apiClient` and returns data, **stop** — use a remote function instead.

```typescript
// BAD: +page.server.ts that should be a remote function
export const load: PageServerLoad = async ({ locals }) => {
  const data = await locals.apiClient.someEndpoint.getData();
  return { data };
};

// GOOD: Use the generated remote function directly in the component
import { getData } from "#lib/api/generated/someendpoint.generated.remote.js";
```

### Never use raw fetch

```typescript
// BAD
const res = await fetch("/api/v4/alerts/active");
const data = await res.json();

// GOOD
import { getActiveAlerts } from "#lib/api/generated/alerts.generated.remote.js";
const data = await getActiveAlerts();
```

### Never hand-write what the codegen produces

If you need a simple query or command for an endpoint, **add the attribute to the controller and regenerate**. Don't write the remote function by hand.

### Never duplicate generated functions in hand-maintained files

If a generated function exists, import it — don't rewrite it. Hand-maintained files should compose generated functions, not replace them.

## Quick Decision Flowchart

```
Need data from the API?
  |
  +--> Does a generated remote function exist?
  |      YES --> Import and use it directly
  |      NO  --> Does the endpoint have [RemoteQuery/Command/Form]?
  |               YES --> Rebuild to generate it
  |               NO  --> Add the attribute, rebuild
  |
  +--> Need to combine multiple calls or transform data?
         YES --> Write a hand-maintained *.remote.ts
         NO  --> Use the generated function as-is
```

## Common Mistakes

| Mistake                                                       | Fix                                            |
| ------------------------------------------------------------- | ---------------------------------------------- |
| Writing `+page.server.ts` to load data                        | Use remote functions in the component          |
| Using `fetch()` or `apiClient` directly in components         | Import from `#lib/api/generated/` or `#lib/api/` |
| Hand-writing a remote function that the codegen could produce | Add attribute to controller, regenerate        |
| Editing files in `api/generated/`                             | These are overwritten on every build           |
| Forgetting `Invalidates` on a mutation                        | Stale cache — add query names to the attribute |
| Creating a `+page.server.ts` just for an auth guard           | Use the layout-level auth check instead        |
