<script lang="ts">
  import { onMount, onDestroy } from "svelte";
  import { getActiveAlerts } from "$api/generated/alerts.generated.remote";
  import { acknowledge } from "$api/generated/alerts.generated.remote";
  import type { ActiveExcursionResponse } from "$api-clients";
  import { Button } from "$lib/components/ui/button";
  import { AlertTriangle, X, Check } from "lucide-svelte";
  import { formatTimeSince } from "./alertTime";

  let alerts = $state<ActiveExcursionResponse[]>([]);
  let dismissedIds = $state<Set<string>>(new Set());
  let acknowledging = $state(false);
  let pollInterval: ReturnType<typeof setInterval> | null = null;

  const visibleAlerts = $derived(
    alerts.filter((a) => !dismissedIds.has(a.id ?? ""))
  );

  function getConditionLabel(conditionType: string | undefined): string {
    switch (conditionType) {
      case "threshold_low":
        return "Low Glucose";
      case "threshold_high":
        return "High Glucose";
      case "rate_of_change":
        return "Rapid Change";
      case "signal_loss":
        return "Signal Lost";
      case "composite":
        return "Composite Alert";
      default:
        return conditionType ?? "Alert";
    }
  }

  async function fetchAlerts() {
    try {
      const result = await getActiveAlerts();
      if (Array.isArray(result)) {
        alerts = result;
      }
    } catch {
      // Silently fail on polling errors
    }
  }

  async function handleAcknowledge() {
    acknowledging = true;
    try {
      await acknowledge({ acknowledgedBy: "web_user" });
      await fetchAlerts();
    } catch {
      // Error handling via remote function
    } finally {
      acknowledging = false;
    }
  }

  function handleDismiss(id: string) {
    dismissedIds = new Set([...dismissedIds, id]);
  }

  onMount(async () => {
    await fetchAlerts();
    pollInterval = setInterval(fetchAlerts, 30000);
  });

  onDestroy(() => {
    if (pollInterval) {
      clearInterval(pollInterval);
    }
  });
</script>

{#if visibleAlerts.length > 0}
  <div class="border-b border-destructive/20 bg-destructive/5">
    {#each visibleAlerts as alert (alert.id)}
      <div
        class="container mx-auto flex items-center gap-3 px-4 py-2 max-w-7xl"
      >
        <AlertTriangle class="h-4 w-4 shrink-0 text-destructive" />
        <div class="flex-1 min-w-0">
          <span class="text-sm font-medium text-destructive">
            {alert.ruleName ?? "Alert"}
          </span>
          <span class="text-sm text-muted-foreground mx-2">
            {getConditionLabel(alert.conditionType)}
          </span>
          <span class="text-xs text-muted-foreground">
            {formatTimeSince(alert.startedAt)}
          </span>
        </div>
        <div class="flex items-center gap-2 shrink-0">
          {#if !alert.acknowledgedAt}
            <Button
              variant="outline"
              size="sm"
              class="h-7 text-xs"
              onclick={handleAcknowledge}
              disabled={acknowledging}
            >
              <Check class="h-3 w-3 mr-1" />
              Acknowledge
            </Button>
          {/if}
          <Button
            variant="ghost"
            size="sm"
            class="h-7 w-7 p-0"
            onclick={() => handleDismiss(alert.id ?? "")}
          >
            <X class="h-3 w-3" />
          </Button>
        </div>
      </div>
    {/each}
  </div>
{/if}
