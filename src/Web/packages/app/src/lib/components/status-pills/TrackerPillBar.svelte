<script lang="ts">
  import TrackerPill from "./TrackerPill.svelte";
  import type { TrackerInstanceDto, TrackerDefinitionDto } from "$lib/api";
  import { TrackerCategory } from "$lib/api";
  import { cn } from "$lib/utils";
  import { trackerDefinition, visibleTrackerInstances } from "./tracker-pill-visibility";

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

  const visibleInstances = $derived(visibleTrackerInstances(instances, definitions, now));

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
        definition={trackerDefinition(instance, definitions)}
        {now}
        {onComplete}
      />
    {/each}
  </div>
{/if}
