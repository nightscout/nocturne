<script lang="ts">
  import * as Popover from "$lib/components/ui/popover";
  import { Button } from "$lib/components/ui/button";
  import type { TrackerInstanceDto, TrackerDefinitionDto } from "$lib/api";
  import { NotificationUrgency } from "$lib/api";
  import { cn } from "$lib/utils";
  import { Check, Clock, Timer } from "lucide-svelte";

  type AlertLevel = "none" | "info" | "warn" | "hazard" | "urgent";

  interface TrackerPillProps {
    /** The tracker instance to display */
    instance: TrackerInstanceDto;
    /** The tracker definition for metadata */
    definition?: TrackerDefinitionDto;
    /** Additional CSS classes */
    class?: string;
    /** Callback when complete button is clicked */
    onComplete?: (instanceId: string, instanceName: string) => void;
  }

  let {
    instance,
    definition,
    class: className,
    onComplete,
  }: TrackerPillProps = $props();

  let popoverOpen = $state(false);

  // Format age display
  function formatAge(hours: number | undefined): string {
    if (hours === undefined || hours === null) return "n/a";
    if (hours < 1) return `${Math.floor(hours * 60)}m`;
    if (hours < 24) return `${Math.floor(hours)}h`;
    const days = Math.floor(hours / 24);
    const h = Math.floor(hours % 24);
    return h > 0 ? `${days}d ${h}h` : `${days}d`;
  }

  // Format lifespan display
  function formatLifespan(hours: number | undefined): string {
    if (hours === undefined || hours === null) return "Not set";
    if (hours < 24) return `${hours} hours`;
    const days = Math.floor(hours / 24);
    return days === 1 ? "1 day" : `${days} days`;
  }

  // Calculate time remaining
  const timeRemaining = $derived.by(() => {
    if (!instance.ageHours || !definition?.lifespanHours) return undefined;
    return definition.lifespanHours - instance.ageHours;
  });

  // Format time remaining
  function formatTimeRemaining(hours: number | undefined): string {
    if (hours === undefined) return "Unknown";
    if (hours <= 0) return "Overdue";
    return formatAge(hours);
  }

  // Determine alert level based on thresholds
  const level = $derived.by((): AlertLevel => {
    if (!instance.ageHours || !definition?.notificationThresholds)
      return "none";

    const age = instance.ageHours;
    const thresholds = definition.notificationThresholds.sort(
      (a, b) => (b.hours ?? 0) - (a.hours ?? 0)
    );

    for (const threshold of thresholds) {
      if (threshold.hours && age >= threshold.hours) {
        const urgency = threshold.urgency;
        if (urgency === NotificationUrgency.Urgent) return "urgent";
        if (urgency === NotificationUrgency.Hazard) return "hazard";
        if (urgency === NotificationUrgency.Warn) return "warn";
        if (urgency === NotificationUrgency.Info) return "info";
      }
    }
    return "none";
  });

  const pillClasses = $derived.by(() => {
    const baseClasses =
      "inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md text-sm font-medium transition-colors cursor-pointer select-none";

    const levelClasses: Record<AlertLevel, string> = {
      none: "bg-secondary text-secondary-foreground hover:bg-secondary/80",
      info: "bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-300 hover:bg-blue-200 dark:hover:bg-blue-900/50",
      warn: "bg-yellow-100 text-yellow-800 dark:bg-yellow-900/30 dark:text-yellow-300 hover:bg-yellow-200 dark:hover:bg-yellow-900/50",
      hazard:
        "bg-orange-100 text-orange-800 dark:bg-orange-900/30 dark:text-orange-300 hover:bg-orange-200 dark:hover:bg-orange-900/50",
      urgent:
        "bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-300 hover:bg-red-200 dark:hover:bg-red-900/50",
    };

    return cn(baseClasses, levelClasses[level], className);
  });

  const label = $derived(
    instance.definitionName ?? definition?.name ?? "Tracker"
  );
  const ageDisplay = $derived(formatAge(instance.ageHours));

  function handleComplete() {
    popoverOpen = false;
    onComplete?.(instance.id!, label);
  }
</script>

<Popover.Root bind:open={popoverOpen}>
  <Popover.Trigger class={pillClasses}>
    <Timer class="h-3 w-3 opacity-75" />
    <span class="text-xs font-normal opacity-75">{label}</span>
    <span>{ageDisplay}</span>
  </Popover.Trigger>
  <Popover.Content class="w-72 p-0" align="center" side="bottom">
    <div class="px-4 py-3 border-b border-border">
      <h4 class="font-semibold text-sm flex items-center gap-2">
        <Timer class="h-4 w-4" />
        {label}
      </h4>
      <p class="text-xs text-muted-foreground">Active tracker</p>
    </div>
    <div class="px-4 py-3 space-y-2">
      <div class="flex justify-between items-center text-sm">
        <span class="text-muted-foreground flex items-center gap-1.5">
          <Clock class="h-3.5 w-3.5" />
          Running Time
        </span>
        <span class="font-medium">{ageDisplay}</span>
      </div>
      {#if definition?.lifespanHours}
        <div class="flex justify-between items-center text-sm">
          <span class="text-muted-foreground">Expected Lifespan</span>
          <span class="font-medium">
            {formatLifespan(definition.lifespanHours)}
          </span>
        </div>
        <div class="flex justify-between items-center text-sm">
          <span class="text-muted-foreground">Time Remaining</span>
          <span
            class={cn(
              "font-medium",
              timeRemaining !== undefined && timeRemaining <= 0
                ? "text-destructive"
                : timeRemaining !== undefined && timeRemaining < 6
                  ? "text-yellow-600 dark:text-yellow-400"
                  : ""
            )}
          >
            {formatTimeRemaining(timeRemaining)}
          </span>
        </div>
      {/if}
      {#if instance.startNotes}
        <hr class="border-border my-2" />
        <div class="text-sm">
          <span class="text-muted-foreground">Notes:</span>
          <span>{instance.startNotes}</span>
        </div>
      {/if}
      {#if instance.startedAt}
        <div class="text-xs text-muted-foreground mt-2">
          Started {new Date(instance.startedAt).toLocaleString([], {
            month: "short",
            day: "numeric",
            hour: "2-digit",
            minute: "2-digit",
          })}
        </div>
      {/if}
    </div>
    <div class="p-2 border-t border-border">
      <Button
        variant="outline"
        size="sm"
        class="w-full"
        onclick={handleComplete}
      >
        <Check class="h-4 w-4 mr-2" />
        Complete Tracker
      </Button>
    </div>
  </Popover.Content>
</Popover.Root>
