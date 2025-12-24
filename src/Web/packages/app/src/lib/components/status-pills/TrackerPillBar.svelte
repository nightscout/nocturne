<script lang="ts">
  import TrackerPill from "./TrackerPill.svelte";
  import type { TrackerInstanceDto, TrackerDefinitionDto } from "$lib/api";
  import { DashboardVisibility, NotificationUrgency } from "$lib/api";
  import { cn } from "$lib/utils";

  interface TrackerPillBarProps {
    /** Active tracker instances */
    instances: TrackerInstanceDto[];
    /** Tracker definitions for metadata */
    definitions: TrackerDefinitionDto[];
    /** Additional CSS classes */
    class?: string;
    /** Callback when complete button is clicked */
    onComplete?: (instanceId: string, instanceName: string) => void;
  }

  let {
    instances = [],
    definitions = [],
    class: className,
    onComplete,
  }: TrackerPillBarProps = $props();

  // Get definition for an instance
  function getDefinition(
    instance: TrackerInstanceDto
  ): TrackerDefinitionDto | undefined {
    return definitions.find((d) => d.id === instance.definitionId);
  }

  // Map urgency enum to numeric level for comparison
  function urgencyToLevel(urgency: NotificationUrgency | undefined): number {
    switch (urgency) {
      case NotificationUrgency.Info:
        return 0;
      case NotificationUrgency.Warn:
        return 1;
      case NotificationUrgency.Hazard:
        return 2;
      case NotificationUrgency.Urgent:
        return 3;
      default:
        return -1;
    }
  }

  // Map DashboardVisibility enum to numeric level
  function visibilityToLevel(
    visibility: DashboardVisibility | undefined
  ): number {
    switch (visibility) {
      case DashboardVisibility.Off:
        return Infinity; // Never show
      case DashboardVisibility.Always:
        return -1; // Always show
      case DashboardVisibility.Info:
        return 0;
      case DashboardVisibility.Warn:
        return 1;
      case DashboardVisibility.Hazard:
        return 2;
      case DashboardVisibility.Urgent:
        return 3;
      default:
        return -1; // Default to always show
    }
  }

  // Calculate current alert level for an instance
  function getCurrentLevel(
    instance: TrackerInstanceDto,
    def?: TrackerDefinitionDto
  ): number {
    if (!instance.ageHours || !def?.notificationThresholds) return -1;

    const age = instance.ageHours;
    let maxLevel = -1;

    for (const threshold of def.notificationThresholds) {
      if (threshold.hours && age >= threshold.hours) {
        const level = urgencyToLevel(threshold.urgency);
        if (level > maxLevel) maxLevel = level;
      }
    }
    return maxLevel;
  }

  // Check if an instance should be visible based on its definition's visibility setting
  function isVisible(
    instance: TrackerInstanceDto,
    def?: TrackerDefinitionDto
  ): boolean {
    if (!def) return false;

    const visibilityThreshold = visibilityToLevel(def.dashboardVisibility);

    // Off = never show
    if (visibilityThreshold === Infinity) return false;

    // Always = always show
    if (visibilityThreshold === -1) return true;

    // Otherwise, show if current level >= visibility threshold
    const currentLevel = getCurrentLevel(instance, def);
    return currentLevel >= visibilityThreshold;
  }

  // Filter instances based on per-definition visibility
  const visibleInstances = $derived.by(() => {
    return instances.filter((instance) => {
      const def = getDefinition(instance);
      return isVisible(instance, def);
    });
  });

  const hasVisiblePills = $derived(visibleInstances.length > 0);
</script>

{#if hasVisiblePills}
  <div class={cn("flex flex-wrap items-center gap-2", className)}>
    {#each visibleInstances as instance (instance.id)}
      <TrackerPill
        {instance}
        definition={getDefinition(instance)}
        {onComplete}
      />
    {/each}
  </div>
{/if}
