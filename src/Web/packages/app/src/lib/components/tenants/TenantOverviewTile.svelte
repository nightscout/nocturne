<script lang="ts">
  import * as Card from "$lib/components/ui/card";
  import { Badge } from "$lib/components/ui/badge";
  import {
    AlertRuleSeverity,
    type TenantOverviewItem,
  } from "$lib/api/generated/nocturne-api-client";
  import { getGlucoseStatusClass } from "$lib/utils/glucose-status";
  import { bg, bgDelta, minutesAgo, toDate } from "$lib/utils/formatting";
  import { getDirectionInfo } from "$lib/utils";
  import { tenantUrl } from "$lib/utils/tenant-host";
  import { Bell } from "lucide-svelte";

  interface Props {
    tenant: TenantOverviewItem;
    baseDomain: string | null;
  }

  const { tenant, baseDomain }: Props = $props();

  const statusClass = $derived(getGlucoseStatusClass(tenant.status));
  const directionInfo = $derived(
    tenant.latest?.direction ? getDirectionInfo(tenant.latest.direction) : null
  );
  const lastReadingAt = $derived(toDate(tenant.lastReadingAt));
  const href = $derived(
    tenant.slug && baseDomain ? tenantUrl(tenant.slug, baseDomain) : null
  );

  const alertBadgeClass = $derived.by(() => {
    switch (tenant.highestActiveSeverity) {
      case AlertRuleSeverity.Critical:
        return "bg-destructive text-destructive-foreground";
      case AlertRuleSeverity.Warning:
        return "bg-amber-500/15 text-amber-700 dark:text-amber-400";
      default:
        return "bg-muted text-muted-foreground";
    }
  });
</script>

{#snippet tileContent()}
  <Card.Root class="h-full transition-colors hover:bg-accent/50">
    <Card.Header class="pb-2">
      <div class="flex items-start justify-between gap-2">
        <div class="min-w-0">
          <Card.Title class="truncate text-base">
            {tenant.displayName || tenant.slug}
          </Card.Title>
          <p class="truncate text-xs text-muted-foreground">{tenant.slug}</p>
        </div>
        {#if tenant.activeAlertCount != null && tenant.activeAlertCount > 0}
          <Badge
            class="shrink-0 gap-1 {alertBadgeClass}"
            data-testid="alert-badge"
          >
            <Bell class="h-3 w-3" aria-hidden="true" />
            {tenant.activeAlertCount}
          </Badge>
        {/if}
      </div>
    </Card.Header>
    <Card.Content>
      <div class="flex items-baseline gap-2">
        {#if tenant.latest?.mgdl != null}
          <span
            class="text-3xl font-bold tabular-nums {statusClass}"
            data-testid="bg-value"
          >
            {bg(tenant.latest.mgdl)}
          </span>
          {#if directionInfo}
            {@const Icon = directionInfo.icon}
            <Icon
              class="h-5 w-5 {directionInfo.css}"
              aria-label={directionInfo.label}
            />
          {/if}
          {#if tenant.latest.delta != null}
            <span
              class="text-sm text-muted-foreground tabular-nums"
              data-testid="bg-delta"
            >
              {bgDelta(tenant.latest.delta)}
            </span>
          {/if}
        {:else}
          <span
            class="text-3xl font-bold text-muted-foreground"
            data-testid="bg-value"
          >
            —
          </span>
        {/if}
      </div>
      <p class="mt-1 text-xs text-muted-foreground" data-testid="freshness">
        {#if lastReadingAt}
          Last reading {minutesAgo(lastReadingAt.getTime())}
        {:else}
          No recent data
        {/if}
      </p>
    </Card.Content>
  </Card.Root>
{/snippet}

{#if href}
  <!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- href is an absolute cross-subdomain URL, not an app route -->
  <a {href} class="block h-full" data-testid="tenant-tile-link">
    {@render tileContent()}
  </a>
{:else}
  {@render tileContent()}
{/if}
