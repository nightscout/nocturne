<script lang="ts">
  import TrackerPill from "./TrackerPill.svelte";
  import type { TrackerInstanceDto, TrackerDefinitionDto } from "$lib/api";
  import { DashboardVisibility, NotificationUrgency, TrackerCategory } from "$lib/api";
  import { cn } from "$lib/utils";
  import { reachedUrgency, urgencyRank } from "$lib/components/trackers/schedule";

  interface TrackerPillBarProps {
    /** Active tracker instances */
    instances: TrackerInstanceDto[];
    /** Tracker definitions for metadata */
    definitions: TrackerDefinitionDto[];
    /** Epoch milliseconds the reached level is judged against. */
    now: number;
    /** Additional CSS classes */
    class?: string;
    /** Callback when complete button is clicked */
    onComplete?: (
      instanceId: string,
      instanceName: string,
      category: TrackerCategory,
      definitionId: string,
      completionEventType?: string
    ) => void;
  }

  let {
    instances = [],
    definitions = [],
    now,
    class: className,
    onComplete,
  }: TrackerPillBarProps = $props();

  function getDefinition(
    instance: TrackerInstanceDto
  ): TrackerDefinitionDto | undefined {
    return definitions.find((d) => d.id === instance.definitionId);
  }

  /** The step a pill waits for before it appears; null for always, undefined for never. */
  const SHOWN_FROM: Record<DashboardVisibility, NotificationUrgency | null | undefined> = {
    [DashboardVisibility.Off]: undefined,
    [DashboardVisibility.Always]: null,
    [DashboardVisibility.Info]: NotificationUrgency.Info,
    [DashboardVisibility.Warn]: NotificationUrgency.Warn,
    [DashboardVisibility.Hazard]: NotificationUrgency.Hazard,
    [DashboardVisibility.Urgent]: NotificationUrgency.Urgent,
  };

  function isVisible(instance: TrackerInstanceDto, def?: TrackerDefinitionDto): boolean {
    if (!def) return false;
    const shownFrom = SHOWN_FROM[def.dashboardVisibility ?? DashboardVisibility.Always];
    if (shownFrom === undefined) return false;
    if (shownFrom === null) return true;
    return urgencyRank(reachedUrgency(instance, now)) >= urgencyRank(shownFrom);
  }

  const visibleInstances = $derived(
    instances.filter((instance) => isVisible(instance, getDefinition(instance)))
  );

  const hasVisiblePills = $derived(visibleInstances.length > 0);
</script>

{#if hasVisiblePills}
  <div
    class={cn("flex flex-wrap items-center gap-x-2 gap-y-1", className)}
    data-testid="tracker-pill-bar"
  >
    {#each visibleInstances as instance (instance.id)}
      <TrackerPill
        {instance}
        definition={getDefinition(instance)}
        {now}
        {onComplete}
      />
    {/each}
  </div>
{/if}
