<script lang="ts">
  import * as Card from "#lib/components/ui/card/index.js";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import {
    AlertRuleSeverity,
    type TenantOverviewItem,
  } from "#lib/api/generated/nocturne-api-client.js";
  import { getGlucoseStatusClass } from "#lib/utils/glucose-status.js";
  import { bg, bgDelta, relativeAge, toDate } from "#lib/utils/formatting.js";
  import { getDirectionInfo } from "#lib/utils/index.js";
  import { tenantUrl } from "#lib/utils/tenant-host.js";
  import Bell from "@lucide/svelte/icons/bell";

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

  const alertBadgeVariant = $derived.by(() => {
    switch (tenant.highestActiveSeverity) {
      case AlertRuleSeverity.Critical:
        return "destructive" as const;
      case AlertRuleSeverity.Warning:
        return "warning" as const;
      default:
        return "secondary" as const;
    }
  });
</script>

{#snippet tileContent()}
  <Card.Root interactive class="h-full">
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
            variant={alertBadgeVariant}
            class="shrink-0"
            data-testid="alert-badge"
          >
            <Bell class="h-3 w-3" aria-hidden="true" />
            {tenant.activeAlertCount}
          </Badge>
        {/if}
      </div>
    </Card.Header>
    <Card.Content>
      <div class="flex flex-wrap items-baseline gap-x-2">
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
          Last reading {relativeAge(lastReadingAt)}
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
