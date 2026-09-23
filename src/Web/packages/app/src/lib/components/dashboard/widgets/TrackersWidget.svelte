<script lang="ts">
  import WidgetCard from "./WidgetCard.svelte";
  import { Badge } from "$lib/components/ui/badge";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { ListChecks } from "lucide-svelte";
  import { DashboardVisibility, NotificationUrgency } from "$lib/api";

  const realtimeStore = getRealtimeStore();

  // Map urgency to color classes
  function getUrgencyColor(urgency: NotificationUrgency | undefined): string {
    switch (urgency) {
      case NotificationUrgency.Urgent:
        return "bg-severity-urgent/20 text-severity-urgent border-severity-urgent/30";
      case NotificationUrgency.Hazard:
        return "bg-severity-hazard/20 text-severity-hazard border-severity-hazard/30";
      case NotificationUrgency.Warn:
        return "bg-severity-warn/20 text-severity-warn border-severity-warn/30";
      case NotificationUrgency.Info:
      default:
        return "bg-severity-info/20 text-severity-info border-severity-info/30";
    }
  }

  // Calculate current urgency level based on age and thresholds
  function getCurrentUrgency(
    ageHours: number | undefined,
    thresholds:
      | Array<{ hours?: number; urgency?: NotificationUrgency }>
      | undefined
  ): NotificationUrgency {
    if (!ageHours || !thresholds?.length) return NotificationUrgency.Info;

    let currentUrgency = NotificationUrgency.Info;
    for (const threshold of thresholds) {
      if (
        threshold.hours !== undefined &&
        threshold.urgency !== undefined &&
        ageHours >= threshold.hours
      ) {
        currentUrgency = threshold.urgency;
      }
    }
    return currentUrgency;
  }

  // Processed tracker data with definitions
  const trackerData = $derived.by(() => {
    return realtimeStore.trackerInstances
      .map((instance) => {
        const def = realtimeStore.trackerDefinitions.find(
          (d) => d.id === instance.definitionId
        );
        if (!def) return null;

        // Check dashboard visibility
        if (def.dashboardVisibility === DashboardVisibility.Off) return null;

        // Compute age dynamically
        const age = instance.startedAt
          ? (realtimeStore.now - new Date(instance.startedAt).getTime()) /
            (1000 * 60 * 60)
          : (instance.ageHours ?? 0);

        const urgency = getCurrentUrgency(age, def.notificationThresholds);

        return {
          instance,
          definition: def,
          ageHours: age,
          urgency,
          displayAge:
            age < 24 ? `${Math.floor(age)}h` : `${Math.floor(age / 24)}d`,
        };
      })
      .filter(Boolean)
      .slice(0, 4);
  });

  const hasTrackers = $derived(trackerData.length > 0);
</script>

<WidgetCard title="Trackers">
  {#if hasTrackers}
    <div class="space-y-1.5">
      {#each trackerData as tracker (tracker?.instance.id)}
        {#if tracker}
          <div
            class="flex items-center justify-between px-2 py-1 rounded border {getUrgencyColor(
              tracker.urgency
            )}"
          >
            <span class="text-xs font-medium truncate max-w-[60%]">
              {tracker.definition.name}
            </span>
            <Badge variant="outline" class="tabular-nums">
              {tracker.displayAge}
            </Badge>
          </div>
        {/if}
      {/each}
    </div>
  {:else}
    <div
      class="flex flex-col items-center justify-center text-muted-foreground py-2"
    >
      <ListChecks class="h-6 w-6 mb-1 opacity-50" />
      <p class="text-xs">No active trackers</p>
    </div>
  {/if}
</WidgetCard>
