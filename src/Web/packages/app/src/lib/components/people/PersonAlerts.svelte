<script lang="ts">
  import {
    NotificationUrgency,
    TenantOverviewAttentionKind,
    type TenantOverviewItem,
  } from "#lib/api/generated/nocturne-api-client.js";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import Bell from "@lucide/svelte/icons/bell";
  import Lock from "@lucide/svelte/icons/lock";

  interface Props {
    person: TenantOverviewItem;
  }

  const { person }: Props = $props();

  const count = $derived(person.activeAlertCount);
  const level = $derived(
    person.attention?.find(
      (reason) => reason.kind === TenantOverviewAttentionKind.ActiveAlert
    )?.level
  );
  const variant = $derived.by(() => {
    switch (level) {
      case NotificationUrgency.Urgent:
        return "severity-urgent" as const;
      case NotificationUrgency.Hazard:
        return "severity-hazard" as const;
      case NotificationUrgency.Warn:
        return "severity-warn" as const;
      case NotificationUrgency.Info:
        return "severity-info" as const;
      default:
        return "secondary" as const;
    }
  });
</script>

{#if count == null}
  <span
    role="img"
    title="You do not have access to this person's alerts"
    aria-label="You do not have access to this person's alerts"
    class="inline-flex text-muted-foreground"
  >
    <Lock class="size-4" aria-hidden="true" />
  </span>
{:else if count > 0}
  <Badge {variant} class="shrink-0" data-testid="alert-badge">
    <Bell aria-hidden="true" />
    <span class="sr-only">Active alerts:</span>
    {count}
  </Badge>
{/if}
