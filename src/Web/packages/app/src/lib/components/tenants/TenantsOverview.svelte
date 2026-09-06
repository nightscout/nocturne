<script lang="ts">
  import { page } from "$app/state";
  import { getOverview } from "$lib/api/generated/myTenants.generated.remote";
  import TenantOverviewTile from "$lib/components/tenants/TenantOverviewTile.svelte";
  import { Button } from "$lib/components/ui/button";
  import { sortTenantsByUrgency } from "$lib/utils/glucose-status";
  import { pollWhileVisible } from "$lib/utils/poll-while-visible.svelte";

  /** How often to re-read the cross-tenant overview while the tab is visible. */
  const POLL_MS = 60_000;

  const overviewQuery = getOverview();
  const data = $derived(overviewQuery.current);
  const loadError = $derived(overviewQuery.error);

  const baseDomain = $derived(page.data.baseDomain ?? null);

  // Detached callback: use .refresh(), not a bare await (no reactive context here).
  pollWhileVisible(() => overviewQuery.refresh(), POLL_MS);
</script>

<svelte:head>
  <title>Tenants overview - Nocturne</title>
</svelte:head>

<div class="container mx-auto space-y-6 p-4 md:p-6">
  <div>
    <h1 class="text-2xl font-bold">Tenants overview</h1>
    <p class="text-sm text-muted-foreground">
      Latest glucose across the tenants you have access to. Refreshes every
      minute.
    </p>
  </div>

  {#if loadError}
    <div class="space-y-2">
      <p class="text-sm text-muted-foreground">
        Failed to load the tenants overview.
      </p>
      <Button
        variant="outline"
        size="sm"
        onclick={() => overviewQuery.refresh()}
      >
        Retry
      </Button>
    </div>
  {:else if overviewQuery.loading && !data}
    <p class="text-sm text-muted-foreground">Loading…</p>
  {:else if data}
    {@const tenants = sortTenantsByUrgency(data.tenants ?? [])}
    {#if tenants.length === 0}
      <p class="text-sm text-muted-foreground">
        No tenants with glucose access.
      </p>
    {:else}
      <div class="grid grid-cols-2 gap-4 lg:grid-cols-3 xl:grid-cols-4">
        {#each tenants as tenant (tenant.tenantId)}
          <TenantOverviewTile {tenant} {baseDomain} />
        {/each}
      </div>
    {/if}
  {/if}
</div>
